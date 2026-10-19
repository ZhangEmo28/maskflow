namespace MaskFlow.Api.Tests;

public class AutoAnnotationTests
{
    static AnnotationItem Box(string id, double cx, double cy, double size, bool confirmed) =>
        new(id, 0, "car", new YoloBox(cx, cy, size, size), null, 0.9, confirmed);

    [Fact]
    public void Parse_UsesLargestPolygonAndFreshIds()
    {
        const string body = """
        {
          "width": 100, "height": 50,
          "masks": [{
            "id": "mask-0",
            "score": 0.83,
            "yoloBox": { "cx": 0.5, "cy": 0.5, "width": 0.4, "height": 0.4 },
            "yoloSegments": [
              [0.1, 0.1, 0.11, 0.1, 0.11, 0.11],
              [0.3, 0.3, 0.7, 0.3, 0.7, 0.7, 0.3, 0.7]
            ]
          }]
        }
        """;

        var request = AutoAnnotationParser.Parse(7, body, "Car", ["person", "car"]);

        var item = Assert.Single(request.Annotations);
        Assert.Equal(7, request.FileId);
        Assert.Equal(100, request.Width);
        Assert.Equal(8, item.Segment!.Count);
        Assert.Equal(0.3, item.Segment[0]);
        Assert.Equal(1, item.ClassId);
        Assert.Equal("Car", item.Label);
        Assert.Equal(0.83, item.Confidence, 3);
        Assert.StartsWith("ann_", item.Id);
    }

    [Fact]
    public void Parse_DropsUnknownDefaultLabel()
    {
        const string body = """{ "width": 10, "height": 10, "masks": [{ "yoloBox": { "cx": 0.5, "cy": 0.5, "width": 0.2, "height": 0.2 } }] }""";
        var item = Assert.Single(AutoAnnotationParser.Parse(1, body, "dog", ["cat"]).Annotations);
        Assert.Null(item.Label);
        Assert.Equal(-1, item.ClassId);
        Assert.Null(item.Segment);
    }

    [Fact]
    public void PickLargestSegment_IgnoresMalformedPolygons()
    {
        var picked = AutoAnnotationParser.PickLargestSegment([[0.1, 0.2, 0.3], [0, 0, 1, 0, 1, 1]]);
        Assert.Equal([0, 0, 1, 0, 1, 1], picked);
        Assert.Null(AutoAnnotationParser.PickLargestSegment([]));
    }

    [Fact]
    public void Iou_ComputesOverlap()
    {
        Assert.Equal(1, AnnotationMerge.Iou(new YoloBox(0.5, 0.5, 0.2, 0.2), new YoloBox(0.5, 0.5, 0.2, 0.2)), 6);
        Assert.Equal(0, AnnotationMerge.Iou(new YoloBox(0.1, 0.1, 0.1, 0.1), new YoloBox(0.9, 0.9, 0.1, 0.1)), 6);
        // Overlap 0.15 x 0.2 = 0.03, union 0.04 + 0.04 - 0.03 = 0.05.
        Assert.Equal(0.6, AnnotationMerge.Iou(new YoloBox(0.5, 0.5, 0.2, 0.2), new YoloBox(0.55, 0.5, 0.2, 0.2)), 6);
    }

    [Fact]
    public void KeepConfirmed_KeepsHumanWorkAndDropsDuplicates()
    {
        var existing = new[]
        {
            Box("confirmed", 0.2, 0.2, 0.2, confirmed: true),
            Box("unconfirmed", 0.8, 0.8, 0.2, confirmed: false)
        };
        var incoming = new[]
        {
            Box("dup-of-confirmed", 0.21, 0.2, 0.2, confirmed: false),
            Box("new", 0.5, 0.5, 0.1, confirmed: false)
        };

        var merged = AnnotationMerge.KeepConfirmed(existing, incoming);

        Assert.Equal(["confirmed", "new"], merged.Select(x => x.Id));
    }
}
