using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The storage record follows the bodies' looks into chests, and asks the chest memory only about chests looked into after the record
/// (live 11-10: every haul search read all 374 chests' memories every 5 s, 3,782 reads a minute under the colony lock).
/// </summary>
public sealed class StoryChestLooksTests
{
    private static readonly P3 A = new(2, 64, 2), B = new(6, 64, 2), C = new(10, 64, 2);

    private static (StoryColony C, FakeStoryHost H, StoryLiveShapeBenchmarkTests.SimClock T) Rig(bool feed)
    {
        var time = new StoryLiveShapeBenchmarkTests.SimClock();
        var (c, h, w) = StoryRig.Make(pawns: 1, time: time);
        foreach (var at in new[] { A, B, C }) w.Set(at.X, at.Y, at.Z, 54);
        Assert.Null(c.StorageOp("set", [A, B, C], null, null, null).Error);
        h.KnowsChests = true;
        h.FeedsLooks = feed;
        return (c, h, time);
    }

    /// <summary>A hauler's work search, a while after the last one (each chest is asked at most every 5 s).</summary>
    private static void HaulSearch(StoryColony c, FakeStoryHost h, StoryLiveShapeBenchmarkTests.SimClock t)
    {
        t.Now += TimeSpan.FromSeconds(6);
        c.Candidate(WorkTypes.Haul, c.Pawns[c.P()], h.Live(c.P()));
    }

    [Fact]
    public void Only_chests_looked_into_after_their_record_are_read_from_the_memory()
    {
        var (c, h, t) = Rig(feed: true);
        h.Seen[A] = ([new Stack(5, 0, 12)], t.Now.AddSeconds(1)); // a body opened A after its record was made
        h.SeenReads = 0;
        HaulSearch(c, h, t);
        Assert.Equal(1, h.SeenReads); // A alone: nobody looked into B or C
        Assert.Equal(12, c.StorageAt(A)!.Contents.Single().Count);
        HaulSearch(c, h, t);
        Assert.Equal(1, h.SeenReads); // nothing new since
        // A player's deposit into B shows once a body opens B.
        h.Seen[B] = ([new Stack(4, 0, 30)], t.Now);
        HaulSearch(c, h, t);
        Assert.Equal(2, h.SeenReads);
        Assert.Equal(30, c.StorageAt(B)!.Contents.Single().Count);
        // A look recorded at the chest beside C (the other half of a double chest) has C read.
        h.Seen[C.Add(1, 0, 0)] = ([new Stack(4, 0, 1)], t.Now);
        HaulSearch(c, h, t);
        Assert.Equal(3, h.SeenReads);
        Assert.True(h.LookReads <= 4, $"the memory's look times read {h.LookReads} times for 4 searches");
    }

    [Fact]
    public void A_host_that_cannot_tell_has_every_due_chest_read_as_before()
    {
        var (c, h, t) = Rig(feed: false);
        h.Seen[A] = ([new Stack(5, 0, 12)], t.Now.AddSeconds(1)); // a body opened A after its record was made
        h.SeenReads = 0;
        HaulSearch(c, h, t);
        HaulSearch(c, h, t);
        Assert.Equal(6, h.SeenReads);
        Assert.Equal(12, c.StorageAt(A)!.Contents.Single().Count);
    }
}
