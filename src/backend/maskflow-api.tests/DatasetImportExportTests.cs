using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SkiaSharp;

namespace MaskFlow.Api.Tests;

static class ImageFixture
{
    // Solid background with a coloured rectangle; `shift` moves it to produce a distinct image.
    public static byte[] Png(int width = 64, int height = 48, int shift = 0, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(200, 210, 220));
            canvas.DrawRect(SKRect.Create(8 + shift, 8, width / 3f, height / 2f), new SKPaint { Color = color ?? SKColors.DarkRed });
            canvas.DrawCircle(width * 0.75f - shift, height * 0.6f, height / 5f, new SKPaint { Color = SKColors.Navy });
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Distinct random blocks per seed, so different seeds never look alike to a perceptual hash.
    public static byte[] RandomPng(int seed, int width = 200, int height = 100)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            for (var x = 0; x < width; x += 25)
            {
                for (var y = 0; y < height; y += 25)
                {
                    canvas.DrawRect(SKRect.Create(x, y, 25, 25), new SKPaint { Color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)) });
                }
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static MemoryStream Zip(params (string Name, byte[] Bytes)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(bytes);
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static byte[] Text(string value) => Encoding.UTF8.GetBytes(value);
}

public class DatasetImportParserTests
{
    [Theory]
    [InlineData("names: [car, 'hard hat']")]
    [InlineData("names: {0: car, 1: hard hat}")]
    [InlineData("path: .\nnames:\n  0: car\n  1: hard hat\n")]
    [InlineData("names:\n  - car\n  - \"hard hat\"  # comment\nnc: 2")]
    public void ParseYamlNames_HandlesUltralyticsStyles(string yaml)
    {
        Assert.Equal(["car", "hard hat"], DatasetImportParser.ParseYamlNames(yaml));
    }

    [Fact]
    public void Yolo_ReadsBoxesPolygonsAndNames()
    {
        var result = DatasetImportParser.Parse(
        [
            ("ds/data.yaml", ImageFixture.Text("names: [car, person]")),
            ("ds/labels/train/IMG_01.txt", ImageFixture.Text("0 0.5 0.5 0.2 0.4\n1 0.1 0.1 0.3 0.1 0.3 0.3\nbad line\n")),
            ("ds/labels/val/img_02.txt", ImageFixture.Text(""))
        ], "auto");

        Assert.Equal("yolo", result.Format);
        Assert.Equal(["car", "person"], result.ClassNames);
        var first = result.Images["img_01"];
        Assert.Equal(2, first.Shapes.Count);
        Assert.Equal(("car", 0.2), (first.Shapes[0].Label, first.Shapes[0].Box.Width));
        Assert.Equal("person", first.Shapes[1].Label);
        Assert.Equal(6, first.Shapes[1].Segment!.Count);
        Assert.Equal(0.2, first.Shapes[1].Box.Cx, 6);
        Assert.Empty(result.Images["img_02"].Shapes);
        Assert.Single(result.Warnings, x => x.Contains("第 3 行"));
    }

    [Fact]
    public void Coco_NormalizesPixelsAndKeepsLargestPolygon()
    {
        var coco = """
        {
          "images": [{ "id": 7, "file_name": "a/Street.jpg", "width": 200, "height": 100 }],
          "categories": [{ "id": 3, "name": "person" }, { "id": 1, "name": "car" }],
          "annotations": [
            { "id": 1, "image_id": 7, "category_id": 1, "bbox": [20, 10, 40, 20],
              "segmentation": [[20, 10, 21, 10, 21, 11], [20, 10, 60, 10, 60, 30, 20, 30]] },
            { "id": 2, "image_id": 7, "category_id": 3, "bbox": [100, 50, 50, 50], "segmentation": { "counts": "rle" } }
          ]
        }
        """;

        var result = DatasetImportParser.Parse([("instances_train.json", ImageFixture.Text(coco))], null);

        Assert.Equal("coco", result.Format);
        Assert.Equal(["car", "person"], result.ClassNames);
        var shapes = result.Images["street"].Shapes;
        Assert.Equal(new YoloBox(0.2, 0.2, 0.2, 0.2), shapes[0].Box);
        Assert.Equal([0.1, 0.1, 0.3, 0.1, 0.3, 0.3, 0.1, 0.3], shapes[0].Segment);
        Assert.Null(shapes[1].Segment);
    }

    [Fact]
    public void LabelMe_AndVoc_AreParsed()
    {
        var labelme = """
        { "imagePath": "..\\images\\cat.png", "imageWidth": 100, "imageHeight": 50,
          "shapes": [
            { "label": "cat", "shape_type": "rectangle", "points": [[10, 5], [30, 25]] },
            { "label": "tail", "shape_type": "polygon", "points": [[0, 0], [10, 0], [10, 10]] },
            { "label": "eye", "shape_type": "circle", "points": [[1, 1], [2, 2]] } ] }
        """;
        var lm = DatasetImportParser.Parse([("cat.json", ImageFixture.Text(labelme))], "auto");
        Assert.Equal("labelme", lm.Format);
        Assert.Equal(2, lm.Images["cat"].Shapes.Count);
        AssertBox(new YoloBox(0.2, 0.3, 0.2, 0.4), lm.Images["cat"].Shapes[0].Box);
        Assert.Single(lm.Warnings, x => x.Contains("circle"));

        var voc = """
        <annotation><filename>dog.jpg</filename><size><width>100</width><height>50</height><depth>3</depth></size>
          <object><name>dog</name><bndbox><xmin>11</xmin><ymin>6</ymin><xmax>30</xmax><ymax>25</ymax></bndbox></object>
        </annotation>
        """;
        var parsed = DatasetImportParser.Parse([("Annotations/dog.xml", ImageFixture.Text(voc))], "auto");
        Assert.Equal("voc", parsed.Format);
        AssertBox(new YoloBox(0.2, 0.3, 0.2, 0.4), parsed.Images["dog"].Shapes[0].Box);
    }

    static void AssertBox(YoloBox expected, YoloBox actual)
    {
        Assert.Equal(expected.Cx, actual.Cx, 9);
        Assert.Equal(expected.Cy, actual.Cy, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }

    [Fact]
    public void Voc_RejectsExternalEntities()
    {
        var xxe = """<?xml version="1.0"?><!DOCTYPE a [<!ENTITY x SYSTEM "file:///etc/passwd">]><annotation><filename>&x;</filename></annotation>""";
        var result = DatasetImportParser.Parse([("evil.xml", ImageFixture.Text(xxe))], "voc");
        Assert.Empty(result.Images);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void UnknownFormat_IsRejected()
    {
        Assert.Throws<Microsoft.AspNetCore.Http.BadHttpRequestException>(() => DatasetImportParser.Parse([("x.json", ImageFixture.Text("[1,2]"))], "auto"));
    }
}

public class NearDuplicateTests
{
    [Fact]
    public void DifferenceHash_MatchesResizedCopiesButNotOtherImages()
    {
        static ulong Hash(byte[] png)
        {
            using var image = ImageTools.DecodeOriented(png);
            return ImageTools.DifferenceHash(image);
        }

        var original = Hash(ImageFixture.Png(64, 48));
        var resized = Hash(ImageFixture.Png(128, 96));
        var different = Hash(ImageFixture.Png(64, 48, shift: 20, color: SKColors.Gold));

        Assert.True(System.Numerics.BitOperations.PopCount(original ^ resized) <= NearDuplicates.DefaultMaxDistance);
        Assert.True(System.Numerics.BitOperations.PopCount(original ^ different) > NearDuplicates.DefaultMaxDistance);
    }

    [Fact]
    public void Group_IsTransitiveAndIgnoresMissingHashes()
    {
        var groups = NearDuplicates.Group(
        [
            (1, "0000000000000000"),
            (2, "0000000000000007"), // 3 bits from #1
            (3, "000000000000003f"), // 3 bits from #2, 6 from #1
            (4, "ffffffffffffffff"),
            (5, ""),
            (6, null)
        ]);

        Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 1 }, groups);
    }

    [Fact]
    public void Splitter_KeepsGroupsTogether()
    {
        var samples = Enumerable.Range(1, 40)
            .Select(id => new DatasetSplitSample(id, new Dictionary<string, int> { ["a"] = 1 }))
            .ToList();
        var groupOf = new Dictionary<int, int> { [3] = 3, [17] = 3, [29] = 3, [8] = 8, [9] = 8 };

        for (var seed = 0; seed < 20; seed++)
        {
            var assigned = DatasetSplitter.Assign(samples, new SplitConfig(60, 20, 20), seed, groupOf);
            Assert.Equal(40, assigned.Count);
            Assert.Single(new[] { assigned[3], assigned[17], assigned[29] }.Distinct());
            Assert.Equal(assigned[8], assigned[9]);
        }
    }
}

public class DatasetImportStoreTests
{
    [Fact]
    public async Task ZipImport_AddsImagesLabelsAndAnnotations_ThenSkipsOrReplaces()
    {
        var (store, _) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("imp", null, "detection", null));
        await store.SaveProjectLabelsAsync(user.Id, project.Id, ["person"]);

        var zip = ImageFixture.Zip(
            ("ds/data.yaml", ImageFixture.Text("names: [car, person]")),
            ("ds/images/train/a.png", ImageFixture.Png()),
            ("ds/images/train/b.png", ImageFixture.Png(shift: 10)),
            ("ds/images/train/not-an-image.png", ImageFixture.Text("hello")),
            ("ds/labels/train/a.txt", ImageFixture.Text("0 0.5 0.5 0.2 0.2\n1 0.2 0.2 0.1 0.1")),
            ("ds/labels/train/ghost.txt", ImageFixture.Text("0 0.5 0.5 0.2 0.2")),
            ("__MACOSX/ds/._a.png", ImageFixture.Text("junk")));

        var summary = await store.ImportDatasetAsync(user, project.Id, zip, "ds.zip", new ImportOptions("auto"));

        Assert.Equal("yolo", summary.Format);
        Assert.Equal(2, summary.ImagesAdded);
        Assert.Equal(1, summary.ImagesAnnotated);
        Assert.Equal(2, summary.AnnotationsImported);
        Assert.Equal(["car"], summary.NewLabels);
        Assert.Equal(["ghost"], summary.UnmatchedAnnotations);
        Assert.Contains(summary.Warnings, x => x.Contains("not-an-image"));
        Assert.Equal(["person", "car"], store.State.ProjectLabels[project.Id]);
        var fileA = store.State.Files.Single(x => x.Name == "a.png");
        Assert.Matches("^[0-9a-f]{16}$", fileA.Phash!);
        var set = store.GetAnnotationSet(user.Id, fileA.Id)!;
        Assert.Equal([("car", 1), ("person", 0)], set.Annotations.Select(x => (x.Label!, x.ClassId)));
        Assert.All(set.Annotations, x => Assert.True(x.Confirmed));
        Assert.Equal(store.State.Files.Where(x => x.ProjectId == project.Id).Sum(x => x.Size), store.GetUser(user.Id)!.UsedBytes);

        // Annotation-only COCO import onto the existing image, first skipping then replacing.
        var coco = """
        { "images": [{ "id": 1, "file_name": "a.png", "width": 64, "height": 48 }],
          "categories": [{ "id": 1, "name": "truck" }],
          "annotations": [{ "id": 1, "image_id": 1, "category_id": 1, "bbox": [0, 0, 32, 24] }] }
        """;
        var skipped = await store.ImportDatasetAsync(user, project.Id, new MemoryStream(ImageFixture.Text(coco)), "instances.json", new ImportOptions("auto"));
        Assert.Equal(1, skipped.SkippedExisting);
        Assert.Equal(2, store.GetAnnotationSet(user.Id, fileA.Id)!.Annotations.Count);

        var replaced = await store.ImportDatasetAsync(user, project.Id, new MemoryStream(ImageFixture.Text(coco)), "instances.json", new ImportOptions("auto", Confirmed: false, OnExisting: "replace"));
        Assert.Equal(1, replaced.ImagesAnnotated);
        var truck = Assert.Single(store.GetAnnotationSet(user.Id, fileA.Id)!.Annotations);
        Assert.Equal(("truck", false), (truck.Label, truck.Confirmed));
        Assert.Equal(new YoloBox(0.25, 0.25, 0.5, 0.5), truck.Bbox);

        foreach (var file in store.State.Files) File.Delete(file.Path);
    }

    [Fact]
    public async Task FailedImport_RemovesWrittenImages()
    {
        var (store, repository) = TestStore.Create();
        var user = await TestStore.RegisterAsync(store);
        var project = await store.CreateProjectAsync(user.Id, new ProjectCreate("imp", null, null, null));
        var zip = ImageFixture.Zip(("a.png", ImageFixture.Png()), ("a.txt", ImageFixture.Text("0 0.5 0.5 0.1 0.1")));

        repository.FailNextSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportDatasetAsync(user, project.Id, zip, "a.zip", new ImportOptions("yolo")));

        Assert.Empty(store.State.Files);
        Assert.False(Directory.Exists(Path.Combine(store.StorageRoot, user.Id.ToString(), "projects", project.Id))
            && Directory.EnumerateFiles(Path.Combine(store.StorageRoot, user.Id.ToString(), "projects", project.Id)).Any());
    }
}

public class DatasetExportFormatTests
{
    sealed class MemoryRepository : IMaskFlowRepository
    {
        public Task EnsureSchemaAsync() => Task.CompletedTask;
        public Task<MaskFlowLoadResult> LoadAsync() => Task.FromResult(new MaskFlowLoadResult(new(), false));
        public Task SaveChangesAsync(MaskFlowState? previous, MaskFlowState current) => Task.CompletedTask;
    }

    static (MaskFlowStore Store, string Dir) CreateStore(bool duplicates = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), "maskflow-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new MaskFlowStore(new MemoryRepository());
        store.State.Projects.Add(new Project("p", 1, "p", "", "segmentation", new SplitConfig(70, 20, 10), 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        store.State.ProjectLabels["p"] = ["car", "person"];
        for (var id = 1; id <= 10; id++)
        {
            var path = Path.Combine(dir, $"{id}.png");
            var bytes = ImageFixture.RandomPng(duplicates && id == 2 ? 1 : id);
            File.WriteAllBytes(path, bytes);
            store.State.Files.Add(new FileItem(id, 1, "p", $"{id}.png", path, bytes.Length, "image", "image/png", DateTimeOffset.UtcNow, ""));
            store.State.AnnotationSets.Add(new AnnotationSet($"s{id}", 1, id, 200, 100,
            [
                new("car", 0, "car", new YoloBox(0.25, 0.5, 0.1, 0.2), [0.2, 0.4, 0.3, 0.4, 0.3, 0.6, 0.2, 0.6], 1, Confirmed: true),
                new("person", 1, "person", new YoloBox(0.75, 0.5, 0.1, 0.2), null, 0.6, Confirmed: false)
            ], "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        return (store, dir);
    }

    [Fact]
    public async Task Coco_WritesPixelBoxesPolygonsAndOneBasedCategories()
    {
        var (store, dir) = CreateStore();
        try
        {
            var export = await store.CreateDatasetExportAsync(1, new ExportRequest("p", null, new SplitConfig(70, 20, 10), "coco"));
            using var zip = ZipFile.OpenRead(export.Path!);
            var documents = DatasetSplitter.Names.Select(split =>
            {
                using var stream = zip.GetEntry($"annotations/instances_{split}.json")!.Open();
                return JsonDocument.Parse(stream);
            }).ToList();
            var images = documents.Sum(x => x.RootElement.GetProperty("images").GetArrayLength());
            var annotations = documents.SelectMany(x => x.RootElement.GetProperty("annotations").EnumerateArray()).ToList();
            var categories = documents[0].RootElement.GetProperty("categories").EnumerateArray().Select(x => (x.GetProperty("id").GetInt32(), x.GetProperty("name").GetString())).ToList();

            Assert.Equal(10, images);
            Assert.Equal([(1, "car"), (2, "person")], categories);
            var car = annotations.First(x => x.GetProperty("category_id").GetInt32() == 1);
            Assert.Equal([40, 40, 20, 20], car.GetProperty("bbox").EnumerateArray().Select(x => x.GetDouble()));
            Assert.Equal(8, car.GetProperty("segmentation")[0].GetArrayLength());
            Assert.Equal(400, car.GetProperty("area").GetDouble());
            var person = annotations.First(x => x.GetProperty("category_id").GetInt32() == 2);
            Assert.Equal(0, person.GetProperty("segmentation").GetArrayLength());
            Assert.Equal(10, zip.Entries.Count(x => x.FullName.StartsWith("images/")));
            File.Delete(export.Path!);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Voc_WritesOneBasedBoxesAndImageSets_ConfirmedOnly()
    {
        var (store, dir) = CreateStore();
        try
        {
            var export = await store.CreateDatasetExportAsync(1, new ExportRequest("p", null, new SplitConfig(70, 20, 10), "voc", ConfirmedOnly: true));
            using var zip = ZipFile.OpenRead(export.Path!);
            using var xml = zip.GetEntry("Annotations/1_1.xml")!.Open();
            var document = XDocument.Load(xml);
            var objects = document.Root!.Elements("object").ToList();
            var box = objects.Single().Element("bndbox")!;

            Assert.Equal("car", objects.Single().Element("name")!.Value);
            Assert.Equal(["41", "41", "60", "60"], new[] { "xmin", "ymin", "xmax", "ymax" }.Select(x => box.Element(x)!.Value));
            var listed = DatasetSplitter.Names.SelectMany(split =>
            {
                using var reader = new StreamReader(zip.GetEntry($"ImageSets/Main/{split}.txt")!.Open());
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }).ToList();
            Assert.Equal(10, listed.Count);
            Assert.NotNull(zip.GetEntry("JPEGImages/1_1.png"));
            File.Delete(export.Path!);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task NearDuplicates_LandInTheSameSplitAndAreReported()
    {
        var (store, dir) = CreateStore(duplicates: true);
        try
        {
            var request = new ExportRequest("p", null, new SplitConfig(50, 25, 25), "yolo-detect", Seed: 7);
            var export = await store.CreateDatasetExportAsync(1, request);
            using var zip = ZipFile.OpenRead(export.Path!);
            using var report = JsonDocument.Parse(zip.GetEntry("split-report.json")!.Open());
            var groups = report.RootElement.GetProperty("nearDuplicateGroups").EnumerateArray()
                .Select(group => group.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToList();
            var splits = report.RootElement.GetProperty("files").EnumerateArray()
                .ToDictionary(x => x.GetProperty("fileId").GetInt32(), x => x.GetProperty("split").GetString());

            Assert.Contains(groups, group => group.SequenceEqual([1, 2]));
            Assert.Equal(splits[1], splits[2]);
            Assert.All(store.State.Files, file => Assert.Matches("^[0-9a-f]{16}$", file.Phash!));

            var analysis = JsonSerializer.SerializeToElement(DatasetAnalysis.Analyze(DatasetExportPlan.Create(store.State, 1, request), request.Split!, request.Seed));
            Assert.Contains(analysis.GetProperty("warnings").EnumerateArray(), x => x.GetString()!.Contains("近重复"));
            File.Delete(export.Path!);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ExportedCoco_CanBeImportedBack()
    {
        var (store, dir) = CreateStore();
        try
        {
            var export = await store.CreateDatasetExportAsync(1, new ExportRequest("p", null, new SplitConfig(70, 20, 10), "coco"));
            var files = new List<(string, byte[])>();
            using (var zip = ZipFile.OpenRead(export.Path!))
            {
                foreach (var entry in zip.Entries.Where(x => x.FullName.StartsWith("annotations/")))
                {
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    files.Add((entry.FullName, buffer.ToArray()));
                }
            }

            var parsed = DatasetImportParser.Parse(files, "auto");
            var car = parsed.Images["1_1"].Shapes.Single(x => x.Label == "car");
            Assert.Equal(0.25, car.Box.Cx, 6);
            Assert.Equal(0.2, car.Box.Height, 6);
            Assert.Equal(8, car.Segment!.Count);
            File.Delete(export.Path!);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
