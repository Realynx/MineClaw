using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A mine build's search reads its queued cells once, not on every pick (live 11-10: a 47,000-cell strip mine cost 284k block reads a
/// pick), and still hands out exactly the cells that are open: one the colony's own dig opened at once, never one walled in since it
/// was read, and one another player opened within <see cref="StoryColony.MineRecheck"/>.
/// </summary>
public sealed class StoryMineCellsTests
{
    /// <summary>A shaft from the surface at 5,·,5 down to y 60, then a tunnel east along y 60 (<paramref name="tunnel"/> cells).</summary>
    private static (StoryColony C, FakeStoryHost H, FakeStoryWorld W, StoryLiveShapeBenchmarkTests.SimClock T) Mine(int tunnel, params (int X, int Y, int Z)[] more)
    {
        var time = new StoryLiveShapeBenchmarkTests.SimClock();
        var (c, h, w) = StoryRig.Make(pawns: 2, time: time);
        foreach (var p in c.Pawns.Values)
            foreach (var t in WorkTypes.All) p.Priorities[t] = t == WorkTypes.Mine ? 1 : 0; // a mine design's digs are mining work
        h.Lives[c.P(0)] = FakeStoryHost.Live(5, 64, 3);
        h.Lives[c.P(1)] = FakeStoryHost.Live(9, 64, 3);
        var cells = new JsonArray();
        for (int y = 63; y >= 60; y--) cells.Add(new JsonArray(5, y, 5));
        for (int x = 6; x < 6 + tunnel; x++) cells.Add(new JsonArray(x, 60, 5));
        foreach (var (x, y, z) in more) cells.Add(new JsonArray(x, y, z));
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "mine", ["blocks"] = cells }), null).Error);
        return (c, h, w, time);
    }

    private static string At(JsonObject j) => j["at"]!.GetValue<string>();

    /// <summary>The pawn finds no dig (it idles), and its idle leg ends, so its next pick searches again.</summary>
    private static void NoDig(StoryColony c, int i)
    {
        var j = c.Next(i);
        Assert.NotEqual("dig", j["kind"]?.GetValue<string>());
        c.Report(c.P(i), j["id"]!.GetValue<string>(), "done", null, null);
    }

    [Fact]
    public void A_queued_mine_is_read_once_and_a_cell_the_colony_digs_opens_the_next_at_once()
    {
        var (c, h, w, _) = Mine(tunnel: 1500);
        var first = c.Next(0);
        Assert.Equal("5,63,5", At(first));
        // The only open cell is out with pawn 0: pawn 1's mine search finds every other cell buried without reading them again
        // (before 11-10: 1,500 x 6 reads on every search).
        long before = w.BlockReads;
        Assert.Null(c.Candidate(WorkTypes.Mine, c.Pawns[c.P(1)], h.Live(c.P(1))));
        Assert.InRange(w.BlockReads - before, 0, 50);
        // Pawn 0 digs its cell: the one under it is open now, and the next search hands it out without waiting for a recheck.
        w.Set(5, 63, 5, 0);
        Assert.True(c.Report(c.P(0), first["id"]!.GetValue<string>(), "done", null, null));
        var next = c.Next(0);
        Assert.Equal("dig", next["kind"]!.GetValue<string>());
        Assert.Equal("5,62,5", At(next));
    }

    [Fact]
    public void A_cell_walled_in_since_it_was_read_is_not_handed_out()
    {
        // Two cells open to the sky: 5,63,5 (the shaft's top) and 9,63,5.
        var (c, _, w, _) = Mine(tunnel: 3, (9, 63, 5));
        Assert.Equal("5,63,5", At(c.Next(0)));
        // A block goes in over 9,63,5 (a pawn's pillar, another player): it is buried now, though it read as open a moment ago.
        w.Set(9, 64, 5, 1);
        Assert.NotEqual("9,63,5", c.Next(1)["at"]?.GetValue<string>());
    }

    [Fact]
    public void A_cell_another_player_opened_is_found_within_the_recheck()
    {
        var (c, _, w, time) = Mine(tunnel: 30);
        Assert.Equal("5,63,5", At(c.Next(0)));
        NoDig(c, 1);
        // Someone else breaks into the tunnel from a cave above 20,60,5: the colony never hears of it.
        w.Set(20, 61, 5, 0);
        time.Now += StoryColony.MineRecheck * 2;
        var j = c.Next(1);
        Assert.Equal("dig", j["kind"]!.GetValue<string>());
        Assert.Equal("20,60,5", At(j));
    }

    [Fact]
    public void A_mine_mark_dug_beside_the_tunnel_opens_it_at_once()
    {
        var (c, _, w, _) = Mine(tunnel: 30);
        // A pit from the surface down to 20,61,5, the block over the tunnel cell 20,60,5.
        w.Set(20, 63, 5, 0);
        w.Set(20, 62, 5, 0);
        Assert.Equal("5,63,5", At(c.Next(0)));
        NoDig(c, 1); // the tunnel's cells are read and parked as buried
        Assert.Null(c.Designate("add", "mine", null, [new P3(20, 61, 5)], null).Error);
        var mark = c.Next(1);
        Assert.Equal("20,61,5", At(mark));
        w.Set(20, 61, 5, 0);
        Assert.True(c.Report(c.P(1), mark["id"]!.GetValue<string>(), "done", null, null));
        Assert.Empty(c.Designations); // the mark's job was the dig
        // No recheck has come due: the colony's own dig of the mark woke the cell under it.
        var j = c.Next(1);
        Assert.Equal("dig", j["kind"]!.GetValue<string>());
        Assert.Equal("20,60,5", At(j));
    }
}
