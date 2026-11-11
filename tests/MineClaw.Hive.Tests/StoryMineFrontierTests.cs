using MineClaw.Hive.Story;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-10, with a 47,000-cell strip mine queued: about four pawns a minute stood idle, each in turn, as "no reachable target: mine
/// …: buried (dug once a cell over or beside it is gone)". That mark was a single owner's mine mark on the surface by the chests, a
/// block under a dirt floor: nothing to do with the mine. The mine (a corridor and branches on four levels, reached by a one-wide,
/// three-high stair shaft from the surface) opens two or three cells at a time while its shaft is dug: its other cells are buried
/// until the ones beside them go. With 14 miners and 16 generalists on that face most of them had nothing to dig, and the idle audit
/// named the buried mark (the search's passed-over mark outranked its held work), while the mine's own note said "the level below is
/// still being built". Now the mine says its open cells are being dug by other pawns, and a buried mark no longer hides that. The
/// strip-mine colony (<see cref="StoryStripMineFixture"/>, all of it in view) with its quarry zone gone (live, the miners had no quarry
/// to fall back on).
/// </summary>
public sealed class StoryMineFrontierTests(ITestOutputHelper output)
{
    /// <summary>A buried mine mark by the chests, as live (a block under the floor, nothing open beside it).</summary>
    private static readonly P3 BuriedMark = new(1750, 40, 1100);

    private static StoryStripMineFixture.Rig Colony(bool openCorridor, P3 mark)
    {
        var r = StoryStripMineFixture.Make(viewChunks: 16);
        var c = r.C;
        foreach (var z in c.Zones.Where(z => z.Kind == "mine").ToList()) Assert.Null(c.ZoneOp("remove", null, null, z.Id).Error);
        Assert.Null(c.Designate("add", "mine", null, [mark], null).Error);
        if (openCorridor) // every other cell of the first mine's corridor has air over it: plenty of open cells to dig
            foreach (var g in Corridor(r).Where((_, k) => k % 2 == 0)) r.W.Set(g.At.X, g.At.Y + 1, g.At.Z, 0);
        // Pockets empty but for a pickaxe: no pawn is off delivering what it carries.
        foreach (var n in r.Names) r.H.Lives[n] = r.H.Lives[n] with { Inventory = [new Stack(274, 0, 1)], FreeSlots = 35 };
        return r;
    }

    private static List<GhostState> Corridor(StoryStripMineFixture.Rig r) =>
        r.C.Builds.First(b => b.Kind == "mine").Ghosts.Where(g => g.State == "todo" && g.At.Y == 17 && g.At.Z == StoryStripMineFixture.CorridorZ).ToList();

    /// <summary>One second of play: every pawn asks for its job, a third of them finish it (a dig really happens), then the tick.
    /// The miners standing idle are returned.</summary>
    private List<string> Second(StoryStripMineFixture.Rig r, int s)
    {
        var c = r.C;
        r.Clock.Now += TimeSpan.FromSeconds(1);
        var idle = new List<string>();
        for (int i = 0; i < r.Names.Count; i++)
        {
            var n = r.Names[i];
            if (c.Next(n) is not { } j) continue;
            var cur = c.CurrentOf(n);
            if (cur?.Source is "none" or "wander" && c.Pawns[n].Role == "miner") idle.Add(n);
            var id = j["id"]!.GetValue<string>();
            if ((s + i) % 3 != 0) { c.Report(n, id, "progress", null, 0.5); continue; }
            if (j["kind"]?.GetValue<string>() == "dig" && j["at"] is { } at)
            {
                var xyz = at.GetValue<string>().Split(',').Select(int.Parse).ToArray();
                r.W.Set(xyz[0], xyz[1], xyz[2], 0);
            }
            c.Report(n, id, "done", null, null);
        }
        c.Tick();
        return idle;
    }

    /// <param name="markOnTheMine">The buried mark on one of the mine's own buried cells instead of by the chests.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Miners_stay_busy_on_a_mine_with_open_cells_whatever_a_buried_mark(bool markOnTheMine)
    {
        // The corridor's second cell: no air over it (the first and third have), so buried, under the mark.
        var onTheMine = new P3(StoryStripMineFixture.MineCells(0, 0)[1].X, 17, StoryStripMineFixture.CorridorZ);
        var r = Colony(openCorridor: true, markOnTheMine ? onTheMine : BuriedMark);
        if (markOnTheMine) Assert.Equal(onTheMine, Corridor(r)[1].At);
        var miners = r.Names.Count(n => r.C.Pawns[n].Role == "miner");
        for (int s = 0; s < 15; s++)
        {
            var idle = Second(r, s);
            output.WriteLine($"{s}: {idle.Count} of {miners} miners idle{(idle.Count > 0 ? ": " + string.Join(", ", idle.Select(n => r.C.IdleReasonOf(n))) : "")}");
            if (s >= 2) Assert.Empty(idle); // the first second or two: picking their pickaxes up, as any pick
        }
    }

    [Fact]
    public void A_narrow_mine_face_says_its_cells_are_being_dug_not_an_unrelated_buried_mark()
    {
        var r = Colony(openCorridor: false, BuriedMark);
        var c = r.C;
        var reasons = new List<string>();
        for (int s = 0; s < 12; s++)
        {
            var idle = Second(r, s);
            foreach (var n in idle) if (c.IdleReasonOf(n) is { } why) reasons.Add(why);
            output.WriteLine($"{s}: {idle.Count} miners idle{(idle.Count > 0 ? ": " + string.Join(" | ", idle.Select(c.IdleReasonOf).Distinct()) : "")}");
        }
        // The shaft opens a few cells at a time: most miners wait, and they say why.
        Assert.NotEmpty(reasons);
        Assert.DoesNotContain(reasons, why => why.Contains("buried (dug once", StringComparison.Ordinal));
        var mine = c.Builds.First(b => b.Kind == "mine");
        Assert.Contains(reasons, why => why.StartsWith($"waiting: build {mine.Id}: its ", StringComparison.Ordinal)
                                        && why.Contains("being dug by other pawns", StringComparison.Ordinal));
    }
}
