using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public sealed record ImportedShape(string Label, YoloBox Box, List<double>? Segment);
public sealed record ImportedImage(string Key, int Width, int Height, List<ImportedShape> Shapes);
public sealed record ImportParseResult(string Format, List<string> ClassNames, Dictionary<string, ImportedImage> Images, List<string> Warnings);

// Parses existing annotations (YOLO txt, COCO json, LabelMe json, Pascal VOC xml) into
// normalized shapes keyed by image file name without extension (lower-case).
public static partial class DatasetImportParser
{
    public static readonly string[] AnnotationExtensions = [".txt", ".json", ".xml", ".yaml", ".yml", ".names"];

    public static string ImageKey(string path)
    {
        var name = path.Replace('\\', '/').Split('/').Last();
        return Path.GetFileNameWithoutExtension(name).Trim().ToLowerInvariant();
    }

    public static ImportParseResult Parse(IReadOnlyList<(string Path, byte[] Bytes)> files, string? format)
    {
        var detected = string.IsNullOrWhiteSpace(format) || format == "auto" ? Detect(files) : format.Trim().ToLowerInvariant();
        return detected switch
        {
            "yolo" => ParseYolo(files),
            "coco" => ParseCoco(files),
            "labelme" => ParseLabelMe(files),
            "voc" => ParseVoc(files),
            _ => throw new BadHttpRequestException("无法识别标注格式：支持 YOLO（txt + data.yaml/classes.txt）、COCO（json）、LabelMe（json）、Pascal VOC（xml）。", 400)
        };
    }

    public static string Detect(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        foreach (var (path, bytes) in files.Where(x => Ext(x.Path) == ".json"))
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("images", out _) && root.TryGetProperty("annotations", out _)) return "coco";
                if (root.TryGetProperty("shapes", out _)) return "labelme";
            }
            catch (JsonException)
            {
            }
        }

        if (files.Any(x => Ext(x.Path) == ".xml" && Encoding.UTF8.GetString(x.Bytes).Contains("<annotation", StringComparison.OrdinalIgnoreCase))) return "voc";
        if (files.Any(IsYoloLabelFile)) return "yolo";
        return "unknown";
    }

    // ---- YOLO ----

    static bool IsYoloLabelFile((string Path, byte[] Bytes) file)
    {
        var name = Path.GetFileName(file.Path).ToLowerInvariant();
        return Ext(file.Path) == ".txt" && name is not ("classes.txt" or "labels.txt" or "readme.txt")
            && !file.Path.Replace('\\', '/').Contains("ImageSets/", StringComparison.OrdinalIgnoreCase);
    }

    static ImportParseResult ParseYolo(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        var warnings = new List<string>();
        var names = ReadYoloNames(files);
        var images = new Dictionary<string, ImportedImage>();
        var maxClass = -1;
        foreach (var file in files.Where(IsYoloLabelFile))
        {
            var key = ImageKey(file.Path);
            if (images.ContainsKey(key))
            {
                warnings.Add($"重复的标注文件名，已忽略：{file.Path}");
                continue;
            }

            var shapes = new List<ImportedShape>();
            var lineNumber = 0;
            foreach (var line in Encoding.UTF8.GetString(file.Bytes).Split('\n'))
            {
                lineNumber++;
                var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;
                if (!int.TryParse(tokens[0], out var classIndex) || classIndex < 0
                    || !tokens.Skip(1).All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    || (tokens.Length != 5 && (tokens.Length < 7 || tokens.Length % 2 == 0)))
                {
                    warnings.Add($"{file.Path} 第 {lineNumber} 行格式不对，已跳过");
                    continue;
                }

                maxClass = Math.Max(maxClass, classIndex);
                var values = tokens.Skip(1).Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
                var label = classIndex < names.Count ? names[classIndex] : $"class_{classIndex}";
                shapes.Add(tokens.Length == 5
                    ? new ImportedShape(label, new YoloBox(values[0], values[1], values[2], values[3]), null)
                    : new ImportedShape(label, BoxFromPolygon(values), values));
            }

            images[key] = new ImportedImage(key, 0, 0, shapes);
        }

        if (names.Count == 0 && maxClass >= 0)
        {
            warnings.Add("没有找到 data.yaml / classes.txt，类别名按 class_0、class_1… 命名，导入后可在标签栏修改。");
        }

        var classNames = names.Count > 0 ? names.Take(Math.Max(names.Count, maxClass + 1)).ToList() : Enumerable.Range(0, maxClass + 1).Select(i => $"class_{i}").ToList();
        return new ImportParseResult("yolo", classNames, images, warnings);
    }

    static List<string> ReadYoloNames(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        foreach (var (_, bytes) in files.Where(x => Ext(x.Path) is ".yaml" or ".yml"))
        {
            var names = ParseYamlNames(Encoding.UTF8.GetString(bytes));
            if (names.Count > 0) return names;
        }

        var listFile = files.FirstOrDefault(x => Path.GetFileName(x.Path).Equals("classes.txt", StringComparison.OrdinalIgnoreCase) || Ext(x.Path) == ".names");
        return listFile.Bytes is null
            ? []
            : Encoding.UTF8.GetString(listFile.Bytes).Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }

    // Handles the shapes Ultralytics writes: `names: [a, b]`, `names: {0: a}` and block maps/lists.
    public static List<string> ParseYamlNames(string yaml)
    {
        var lines = yaml.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = NamesKey().Match(lines[i]);
            if (!match.Success) continue;
            var inline = match.Groups[1].Value.Trim();
            if (inline.StartsWith('['))
            {
                return SplitInline(inline.Trim('[', ']')).ToList();
            }

            if (inline.StartsWith('{'))
            {
                return SplitInline(inline.Trim('{', '}'))
                    .Select(x => x.Split(':', 2))
                    .Where(x => x.Length == 2 && int.TryParse(x[0].Trim(), out _))
                    .OrderBy(x => int.Parse(x[0].Trim()))
                    .Select(x => Unquote(x[1]))
                    .ToList();
            }

            var mapped = new SortedDictionary<int, string>();
            var listed = new List<string>();
            for (var j = i + 1; j < lines.Length && (lines[j].StartsWith(' ') || lines[j].StartsWith('\t') || lines[j].Trim().Length == 0); j++)
            {
                var item = lines[j].Trim();
                if (item.StartsWith("- ")) listed.Add(Unquote(item[2..]));
                else if (item.Split(':', 2) is [var index, var value] && int.TryParse(index.Trim(), out var number)) mapped[number] = Unquote(value);
            }

            return listed.Count > 0 ? listed : mapped.Values.ToList();
        }

        return [];
    }

    [GeneratedRegex(@"^names\s*:(.*)$")]
    private static partial Regex NamesKey();

    static IEnumerable<string> SplitInline(string value) =>
        value.Split(',').Select(Unquote).Where(x => x.Length > 0);

    static string Unquote(string value) => value.Split('#')[0].Trim().Trim('\'', '"').Trim();

    // ---- COCO ----

    static ImportParseResult ParseCoco(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        var warnings = new List<string>();
        var classNames = new List<string>();
        var images = new Dictionary<string, ImportedImage>();
        foreach (var (path, bytes) in files.Where(x => Ext(x.Path) == ".json"))
        {
            using var document = TryParseJson(bytes);
            var root = document?.RootElement;
            if (root is not { ValueKind: JsonValueKind.Object } || !root.Value.TryGetProperty("annotations", out var annotations)) continue;

            var categories = Items(root.Value, "categories").ToDictionary(x => x.GetProperty("id").GetInt32(), x => x.GetProperty("name").GetString() ?? "");
            foreach (var name in categories.OrderBy(x => x.Key).Select(x => x.Value))
            {
                if (!classNames.Contains(name, StringComparer.OrdinalIgnoreCase)) classNames.Add(name);
            }

            var imageInfo = new Dictionary<int, (string Key, int Width, int Height)>();
            foreach (var image in Items(root.Value, "images"))
            {
                var key = ImageKey(image.GetProperty("file_name").GetString() ?? "");
                var width = Int(image, "width");
                var height = Int(image, "height");
                if (width <= 0 || height <= 0)
                {
                    warnings.Add($"{path}：图片 {key} 缺少宽高，已跳过");
                    continue;
                }

                imageInfo[image.GetProperty("id").GetInt32()] = (key, width, height);
                images.TryAdd(key, new ImportedImage(key, width, height, []));
            }

            foreach (var annotation in annotations.EnumerateArray())
            {
                if (!imageInfo.TryGetValue(Int(annotation, "image_id"), out var info)) continue;
                if (!categories.TryGetValue(Int(annotation, "category_id"), out var label)) continue;
                List<double>? segment = null;
                if (annotation.TryGetProperty("segmentation", out var segmentation) && segmentation.ValueKind == JsonValueKind.Array)
                {
                    segment = AutoAnnotationParser.PickLargestSegment(segmentation.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.Array)
                        .Select(polygon => Normalize(polygon.EnumerateArray().Select(x => x.GetDouble()).ToList(), info.Width, info.Height)));
                }

                YoloBox box;
                if (annotation.TryGetProperty("bbox", out var bbox) && bbox.GetArrayLength() == 4)
                {
                    var values = bbox.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    box = new YoloBox((values[0] + values[2] / 2) / info.Width, (values[1] + values[3] / 2) / info.Height, values[2] / info.Width, values[3] / info.Height);
                }
                else if (segment is not null)
                {
                    box = BoxFromPolygon(segment);
                }
                else
                {
                    continue;
                }

                images[info.Key].Shapes.Add(new ImportedShape(label, box, segment));
            }
        }

        return new ImportParseResult("coco", classNames, images, warnings);
    }

    // ---- LabelMe ----

    static ImportParseResult ParseLabelMe(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        var warnings = new List<string>();
        var classNames = new List<string>();
        var images = new Dictionary<string, ImportedImage>();
        foreach (var (path, bytes) in files.Where(x => Ext(x.Path) == ".json"))
        {
            using var document = TryParseJson(bytes);
            var root = document?.RootElement;
            if (root is not { ValueKind: JsonValueKind.Object } || !root.Value.TryGetProperty("shapes", out var shapes)) continue;
            var imagePath = root.Value.TryGetProperty("imagePath", out var imagePathElement) ? imagePathElement.GetString() : null;
            var key = ImageKey(string.IsNullOrWhiteSpace(imagePath) ? path : imagePath);
            var width = Int(root.Value, "imageWidth");
            var height = Int(root.Value, "imageHeight");
            if (width <= 0 || height <= 0)
            {
                warnings.Add($"{path} 缺少 imageWidth/imageHeight，已跳过");
                continue;
            }

            var imported = new List<ImportedShape>();
            foreach (var shape in shapes.EnumerateArray())
            {
                var label = shape.TryGetProperty("label", out var labelElement) ? labelElement.GetString()?.Trim() : null;
                var type = shape.TryGetProperty("shape_type", out var typeElement) ? typeElement.GetString() : "polygon";
                var points = shape.TryGetProperty("points", out var pointsElement)
                    ? pointsElement.EnumerateArray().SelectMany(point => point.EnumerateArray().Take(2).Select(x => x.GetDouble())).ToList()
                    : [];
                if (string.IsNullOrEmpty(label)) continue;
                if (!classNames.Contains(label, StringComparer.OrdinalIgnoreCase)) classNames.Add(label);
                var normalized = Normalize(points, width, height);
                if (type == "rectangle" && normalized.Count == 4)
                {
                    imported.Add(new ImportedShape(label, BoxFromPolygon(normalized), null));
                }
                else if (type == "polygon" && normalized.Count >= 6)
                {
                    imported.Add(new ImportedShape(label, BoxFromPolygon(normalized), normalized));
                }
                else
                {
                    warnings.Add($"{path}：不支持的形状 {type}（{label}），已跳过");
                }
            }

            images[key] = new ImportedImage(key, width, height, imported);
        }

        return new ImportParseResult("labelme", classNames, images, warnings);
    }

    // ---- Pascal VOC ----

    static ImportParseResult ParseVoc(IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        var warnings = new List<string>();
        var classNames = new List<string>();
        var images = new Dictionary<string, ImportedImage>();
        foreach (var (path, bytes) in files.Where(x => Ext(x.Path) == ".xml"))
        {
            XElement root;
            try
            {
                // DtdProcessing is prohibited by default in XDocument.Load(Stream): no external entities.
                root = XDocument.Load(new MemoryStream(bytes)).Root!;
            }
            catch (System.Xml.XmlException)
            {
                warnings.Add($"{path} 不是有效的 XML，已跳过");
                continue;
            }

            if (root.Name.LocalName != "annotation") continue;
            var key = ImageKey(root.Element("filename")?.Value is { Length: > 0 } fileName ? fileName : path);
            var size = root.Element("size");
            var width = (int?)XmlInt(size?.Element("width")) ?? 0;
            var height = (int?)XmlInt(size?.Element("height")) ?? 0;
            if (width <= 0 || height <= 0)
            {
                warnings.Add($"{path} 缺少图片宽高，已跳过");
                continue;
            }

            var shapes = new List<ImportedShape>();
            foreach (var item in root.Elements("object"))
            {
                var label = item.Element("name")?.Value.Trim();
                var box = item.Element("bndbox");
                if (string.IsNullOrEmpty(label) || box is null) continue;
                if (!classNames.Contains(label, StringComparer.OrdinalIgnoreCase)) classNames.Add(label);
                var xmin = XmlDouble(box.Element("xmin")) - 1;
                var ymin = XmlDouble(box.Element("ymin")) - 1;
                var xmax = XmlDouble(box.Element("xmax"));
                var ymax = XmlDouble(box.Element("ymax"));
                shapes.Add(new ImportedShape(label, new YoloBox((xmin + xmax) / 2 / width, (ymin + ymax) / 2 / height, (xmax - xmin) / width, (ymax - ymin) / height), null));
            }

            images[key] = new ImportedImage(key, width, height, shapes);
        }

        return new ImportParseResult("voc", classNames, images, warnings);
    }

    // ---- helpers ----

    static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    static JsonDocument? TryParseJson(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)Math.Round(value.GetDouble()) : 0;

    static double? XmlInt(XElement? element) =>
        element is not null && double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Round(value) : null;

    static double XmlDouble(XElement? element) =>
        element is not null && double.TryParse(element.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    static List<double> Normalize(List<double> points, int width, int height) =>
        points.Select((value, index) => value / (index % 2 == 0 ? width : height)).ToList();

    static YoloBox BoxFromPolygon(IReadOnlyList<double> points)
    {
        var xs = points.Where((_, index) => index % 2 == 0).ToList();
        var ys = points.Where((_, index) => index % 2 == 1).ToList();
        double left = xs.Min(), right = xs.Max(), top = ys.Min(), bottom = ys.Max();
        return new YoloBox((left + right) / 2, (top + bottom) / 2, right - left, bottom - top);
    }
}
