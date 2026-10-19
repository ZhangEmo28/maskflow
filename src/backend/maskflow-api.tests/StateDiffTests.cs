using System.Collections;

namespace MaskFlow.Api.Tests;

public class StateDiffTests
{
    static FileItem File(int id, string name = "a.jpg") =>
        new(id, 1, null, name, "/tmp/x", 1, "image", null, DateTimeOffset.UnixEpoch, "");

    [Fact]
    public void Compare_ReportsOnlyChangedRows()
    {
        var unchanged = File(1);
        var replaced = File(2);
        var removed = File(3);
        var previous = new List<FileItem> { unchanged, replaced, removed };
        var current = new List<FileItem> { unchanged, replaced with { Name = "b.jpg" }, File(4) };

        var changes = StateDiff.Compare(previous, current, x => x.Id);

        Assert.Equal([2, 4], changes.Upserts.Select(x => x.Id));
        Assert.Equal([3], changes.Deletes);
    }

    [Fact]
    public void Compare_DictionariesByReference()
    {
        var keep = new List<string> { "car" };
        var previous = new Dictionary<string, List<string>> { ["p1"] = keep, ["p2"] = ["a"], ["p3"] = ["x"] };
        var current = new Dictionary<string, List<string>> { ["p1"] = keep, ["p2"] = ["a"], ["p4"] = ["y"] };

        var changes = StateDiff.Compare(previous, current);

        Assert.Equal(["p2", "p4"], changes.Upserts.Select(x => x.Key));
        Assert.Equal(["p3"], changes.Deletes);
    }

    // Guards against adding a collection to MaskFlowState and forgetting to copy it in Snapshot:
    // a shared collection would let a writer mutate the published state readers are using.
    [Fact]
    public void Snapshot_CopiesEveryCollectionButSharesRows()
    {
        var state = new MaskFlowState();
        state.Files.Add(File(1));
        var snapshot = StateDiff.Snapshot(state);

        foreach (var property in typeof(MaskFlowState).GetProperties().Where(x => typeof(IEnumerable).IsAssignableFrom(x.PropertyType)))
        {
            Assert.False(ReferenceEquals(property.GetValue(state), property.GetValue(snapshot)), $"{property.Name} is shared");
        }

        Assert.Same(state.Files[0], snapshot.Files[0]);
    }
}
