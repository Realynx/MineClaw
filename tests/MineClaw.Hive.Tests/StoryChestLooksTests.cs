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

    /// <summary>
    /// Live 11-10: "chest-looks 41x 1980 ms max 760 ms" a minute, each a whole read of the chest memory under the colony lock. A host
    /// that allows it (the live one) has the look times read in the background: a search uses the last read and, when it is 5 s old,
    /// has the next one read; a slow read holds nobody up. A look after the last read is found once the next read is in.
    /// </summary>
    [Fact]
    public void A_host_that_reads_off_the_lock_has_the_look_times_read_in_the_background()
    {
        var (c, h, t) = Rig(feed: true);
        h.LooksOffLock = true;
        h.Seen[A] = ([new Stack(5, 0, 12)], t.Now.AddSeconds(1));
        HaulSearch(c, h, t); // the first read: at once, as before
        Assert.Equal(1, h.LookReads);
        Assert.Equal(12, c.StorageAt(A)!.Contents.Single().Count);
        // A player's deposit into B, looked at by a body; then the chest memory turns slow.
        h.Seen[B] = ([new Stack(4, 0, 30)], t.Now);
        using var gate = new ManualResetEventSlim(false);
        h.LooksGate = gate;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        HaulSearch(c, h, t); // the last read is old: the next one starts in the background (it waits at the gate)
        HaulSearch(c, h, t); // one at a time: none more starts, and the searches use the last read
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"the searches waited {clock.Elapsed} for the chest memory");
        Assert.Empty(c.StorageAt(B)!.Contents); // not in the last read yet
        gate.Set();
        Assert.True(SpinWait.SpinUntil(() => h.LookReads == 2 && !Reading(c), TimeSpan.FromSeconds(10)), "the background read never ended");
        h.LooksGate = null;
        HaulSearch(c, h, t);
        Assert.Equal(30, c.StorageAt(B)!.Contents.Single().Count);
        Assert.True(SpinWait.SpinUntil(() => !Reading(c), TimeSpan.FromSeconds(10)));
        Assert.InRange(h.LookReads, 2, 3); // the first, the slow one, maybe one more begun by the last search
    }

    private static bool Reading(StoryColony c) =>
        (int)typeof(StoryColony).GetField("_looksReading", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(c)! != 0;

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
