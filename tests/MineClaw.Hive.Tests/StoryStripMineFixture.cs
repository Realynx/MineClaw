using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// A synthetic colony of the live strip-mine colony's shape and size (11-10: 30 pawns, 47,552 open build cells, picks of 0.8 s and
/// more): twelve mine builds of about 3,900 dig cells each (branch mines: a corridor along x and twenty 193-long branches every
/// four blocks, on four levels, y 17, 16, 12 and 11), the first with a stair shaft down from the surface; a 74-cell mine build of
/// digs and torches; a 1,367-cell stone brick tower with one cell left (blocked); a blocked 142-cell leaves build; small freecam
/// edits; finished builds (planks, 666 chests, glass, floors) whose blocks stand in the world; 374 storages, 30 benches (six with a
/// bill), four zones (a large farm, a quarry, a guard zone, a fishing zone) and three designations. 16 generalists and 14 miners
/// with the live colony's priorities. Part of the mine lies outside the colony's view (it reads as air there, as it did live).
/// Every value is generated here, at made-up coordinates: only the counts and the shape follow the live colony, never its positions.
/// Deterministic: the same rig every time.
/// </summary>
internal static class StoryStripMineFixture
{
    public static readonly P3 Home = new(1807, 63, 1213);

    /// <summary>The four dig levels and the three x bands; each mine build is one band of one level.</summary>
    private static readonly int[] Levels = [17, 16, 12, 11];
    private static readonly int[] Bands = [1680, 1760, 1840];
    public const int BranchZ1 = 1026, BranchZ2 = 1218, CorridorZ = 1122, Branches = 20;

    internal sealed record Rig(StoryColony C, FakeStoryHost H, FakeStoryWorld W, StoryLiveShapeBenchmarkTests.SimClock Clock, List<string> Names);

    /// <summary>The colony. <paramref name="viewChunks"/>: the columns within that many chunks of home's chunk are loaded, the rest
    /// read as air (unloaded). <paramref name="store"/>: where the tick saves the colony (none: never saved).</summary>
    public static Rig Make(int pawns = 30, int viewChunks = 8, StoryStore? store = null)
    {
        var clock = new StoryLiveShapeBenchmarkTests.SimClock { Now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero) };
        var w = new FakeStoryWorld();
        var h = new FakeStoryHost(w);
        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Strip", Home = Home, Created = clock.Now, Wanted = pawns }, h, clock, store)
        {
            DoneGrace = TimeSpan.Zero,
            Rng = new Random(11),
        };
        c.Meta.Seq = 400_000; // the builds' ids below are under it: new ones never clash
        Unload(w, viewChunks);
        var names = Enumerable.Range(0, pawns).Select(i => "maid_chan_" + (101 + i)).ToList();
        h.Spawn("story-1", pawns, names);
        c.Joined(names);
        SetPawns(c, h, names);
        long rev = 1000;
        int order = 0;
        AddFinishedBuilds(c, w, ref rev, ref order);
        AddTower(c, w, ref rev, ref order);
        AddLeaves(c, ref rev, ref order);
        AddEdits(c, w, ref rev, ref order);
        AddMines(c, w, ref rev, ref order);
        c.SetRev(rev + 10);
        AddStorages(c, h, clock.Now);
        AddBenches(c, w);
        AddZones(c, w);
        AddDesignations(c);
        return new Rig(c, h, w, clock, names);
    }

    /// <summary>Columns of the mine's area farther than <paramref name="viewChunks"/> chunks from home's chunk: not in view.</summary>
    private static void Unload(FakeStoryWorld w, int viewChunks)
    {
        int hcx = Home.X >> 4, hcz = Home.Z >> 4;
        for (int x = Bands[0] - 4; x <= Bands[^1] + 4 * Branches + 4; x++)
            for (int z = BranchZ1 - 2; z <= BranchZ2 + 2; z++)
                if (Math.Abs((x >> 4) - hcx) > viewChunks || Math.Abs((z >> 4) - hcz) > viewChunks) w.Unloaded.Add((x, z));
    }

    /// <summary>16 generalists (every work 3, haul 2) and 14 miners (mine 1, haul 3, build 4, nothing else), as the live colony had
    /// them; around home and the chests, a few miners by the shaft's top.</summary>
    private static void SetPawns(StoryColony c, FakeStoryHost h, List<string> names)
    {
        for (int i = 0; i < names.Count; i++)
        {
            var p = c.Pawns[names[i]];
            bool miner = i % 15 >= 8; // 8 generalists then 7 miners, twice: 16 + 14
            foreach (var t in WorkTypes.All) p.Priorities[t] = miner ? 0 : 3;
            p.Priorities[WorkTypes.Haul] = miner ? 3 : 2;
            if (miner) { p.Priorities[WorkTypes.Mine] = 1; p.Priorities[WorkTypes.Build] = 4; }
            p.Role = miner ? "miner" : "generalist";
            p.Schedule = Enumerable.Repeat(ScheduleBlocks.Anything, 24).ToArray();
            p.Traits.Clear();
            var at = miner && i % 2 == 0 ? new P3(1756 + i % 3, 64, 1070 + i % 4) : new P3(1796 + i % 8 * 2, 64, 1202 + i / 8 * 2);
            h.Lives[names[i]] = miner ? FakeStoryHost.Live(at.X, at.Y, at.Z, 20, 20, new Stack(4, 0, 20)) : FakeStoryHost.Live(at.X, at.Y, at.Z);
        }
    }

    private static GhostState Dig(P3 at, int id, string state, long rev, string? reason = null) =>
        new() { At = at, Id = id, Meta = 0, Item = null, Job = true, Remove = true, State = state, Reason = reason, Rev = rev };

    private static GhostState Place(P3 at, int id, int meta, string item, string state, long rev, string? reason = null, bool job = true) =>
        new() { At = at, Id = id, Meta = meta, Item = item, Job = job, Remove = false, State = state, Reason = reason, Rev = rev };

    private static BuildState Build(string id, string kind, string material, int order, long rev, List<GhostState> ghosts, string status, string? reason = null)
    {
        var spec = new JsonObject
        {
            ["kind"] = kind,
            ["blocks"] = new JsonArray(ghosts.Select(g => (JsonNode)new JsonArray(g.At.X, g.At.Y, g.At.Z)).ToArray()),
        };
        if (material.Length > 0) spec["material"] = material;
        var b = new BuildState
        {
            Id = id, Kind = kind, Material = material, Order = order, Rev = rev, Status = status, Reason = reason, Spec = spec, Ghosts = ghosts,
            Created = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero),
        };
        b.Label = $"{kind} ({ghosts.Count(g => g.Job)} blocks{(material.Length > 0 ? ", " + material : "")})";
        return b;
    }

    /// <summary>The ore a tunnel cell holds (a few coal, fewer iron), else stone.</summary>
    private static int OreAt(int x, int y, int z) =>
        (x * 31 + y * 17 + z * 7) % 401 == 0 ? 15 : (x * 13 + y * 7 + z * 29) % 53 == 0 ? 16 : 1;

    /// <summary>One band of one level: a corridor along x at <see cref="CorridorZ"/> and <see cref="Branches"/> branches along z.</summary>
    public static List<P3> MineCells(int band, int level)
    {
        int x0 = Bands[band] + (level % 2 == 1 ? 2 : 0), y = Levels[level];
        var cells = new List<P3>();
        for (int x = x0; x < x0 + 4 * Branches; x++) cells.Add(new P3(x, y, CorridorZ));
        for (int k = 0; k < Branches; k++)
            for (int z = BranchZ1; z <= BranchZ2; z++)
                if (z != CorridorZ) cells.Add(new P3(x0 + 4 * k, y, z));
        return cells;
    }

    /// <summary>The stair shaft of the first mine: one cell wide, three high, one step down and along z per level, from the surface
    /// (its top cells are air: done) to the corridor at y 17.</summary>
    public static List<P3> ShaftCells()
    {
        var cells = new List<P3>();
        for (int k = 0; k <= 63 - 18; k++)
            for (int dy = 0; dy < 3; dy++)
                cells.Add(new P3(1758, 63 - k + dy, CorridorZ - 45 + k));
        return cells;
    }

    private static void AddMines(StoryColony c, FakeStoryWorld w, ref long rev, ref int order)
    {
        for (int i = 0; i < 12; i++)
        {
            long r = ++rev;
            var cells = (i == 0 ? ShaftCells() : []).Concat(MineCells(i % 3, i / 3)).ToList();
            var ghosts = new List<GhostState>(cells.Count);
            foreach (var at in cells)
            {
                int id = w.Block(at.X, at.Y, at.Z).Id;
                if (at.Y <= 63 && (id = OreAt(at.X, at.Y, at.Z)) != 1) w.Set(at.X, at.Y, at.Z, id);
                bool air = at.Y > 63;
                ghosts.Add(Dig(at, air ? 0 : id, air ? "done" : "todo", r));
            }
            // The first: under way (its top shaft cells dug, a few cells out of reach); the second waits for a tool; the third under way.
            if (i == 0)
            {
                foreach (var g in ghosts.Where(g => g.State == "todo").Take(2)) { g.State = "done"; w.Set(g.At.X, g.At.Y, g.At.Z, 0); }
                foreach (var g in ghosts.Skip(400).Take(22)) g.Reason = "out of reach even from a pillar (3 tries)";
            }
            string status = i == 0 || i == 2 ? "active" : i == 1 ? "waiting" : "queued";
            string? reason = i == 1 ? "needs a wooden pickaxe — one is in stock" : null;
            c.Builds.Add(Build("b" + (300_000 + i), "mine", "", 132 + order++, r, ghosts, status, reason));
        }
        // The last item: digs down a small shaft and torches along a tunnel.
        long rl = ++rev;
        var last = new List<GhostState>();
        for (int k = 0; k < 7; k++)
            for (int dy = 0; dy < 3 && last.Count < 20; dy++)
                last.Add(Dig(new P3(1920, 10 - k + dy, 1100 + k), 1, "todo", rl));
        for (int t = 0; t < 54; t++) last.Add(Place(new P3(1682 + 4 * (t % 27), t < 27 ? 18 : 13, 1130 + t % 5), 50, 4, "torch", "todo", rl));
        c.Builds.Add(Build("b300012", "mine", "", 132 + order++, rl, last, "queued"));
    }

    /// <summary>The stone brick tower: a 17×17 ring from y 63 to 82 and a floor at 83, all built but one cell out of reach.</summary>
    private static void AddTower(StoryColony c, FakeStoryWorld w, ref long rev, ref int order)
    {
        long r = ++rev;
        var ghosts = new List<GhostState>();
        for (int y = 63; y <= 82; y++)
            for (int x = 1822; x <= 1838; x++)
                for (int z = 1178; z <= 1194; z++)
                    if (x is 1822 or 1838 || z is 1178 or 1194) ghosts.Add(Place(new P3(x, y, z), 98, 0, "stonebrick", "done", r));
        for (int x = 1822; x <= 1838 && ghosts.Count < 1367; x++)
            for (int z = 1178; z <= 1194 && ghosts.Count < 1367; z++)
                ghosts.Add(Place(new P3(x, 83, z), 98, 0, "stonebrick", "done", r));
        var left = ghosts[^1];
        left.State = "blocked";
        left.Reason = "can't place at " + left.At + ": no path to block (timeout); it's 5 blocks up; unreachable even by pillaring/bridging";
        foreach (var g in ghosts) if (g.State == "done") w.Set(g.At.X, g.At.Y, g.At.Z, 98);
        c.Builds.Add(Build("b200001", "block", "stonebrick", order++, r, ghosts, "active", left.Reason));
    }

    /// <summary>Leaves the colony has none of: blocked, two rings of 142 cells around the tower.</summary>
    private static void AddLeaves(StoryColony c, ref long rev, ref int order)
    {
        long r = ++rev;
        var ghosts = new List<GhostState>();
        for (int ring = 0; ring < 2; ring++)
            for (int x = 1820 + ring; x <= 1840 - ring; x++)
                for (int z = 1176 + ring; z <= 1196 - ring; z++)
                    if ((x == 1820 + ring || x == 1840 - ring || z == 1176 + ring || z == 1196 - ring) && ghosts.Count < 142)
                        ghosts.Add(Place(new P3(x, 64, z), 18, 4, "leaves", "todo", r));
        c.Builds.Add(Build("b200002", "block", "leaves", order++, r, ghosts, "blocked", "need 142 oak leaves"));
    }

    /// <summary>Freecam edits: a dig and a dirt block each, the dirt blocked ("the requested item variant doesn't exist").</summary>
    private static void AddEdits(StoryColony c, FakeStoryWorld w, ref long rev, ref int order)
    {
        const string why = "the requested item variant doesn't exist on this server";
        for (int i = 0; i < 5; i++)
        {
            long r = ++rev;
            var at = new P3(1800 + 2 * i, 62, 1195);
            var ghosts = new List<GhostState>();
            if (i < 2) { ghosts.Add(Dig(at.Add(0, -1, 0), 1, "done", r)); w.Set(at.X, at.Y - 1, at.Z, 0); }
            ghosts.Add(Place(at, 3, 0, "dirt", "blocked", r, why));
            if (i == 2) ghosts.Add(Place(at.Add(1, 0, 0), 3, 0, "dirt", "blocked", r, why));
            var b = Build("b200" + (100 + i), "block", "dirt", order++, r, ghosts, "active");
            b.Spec!["group"] = "freecam-" + i;
            c.Builds.Add(b);
        }
    }

    /// <summary>What was built before the mine: planks, 666 chests (the storages stand in them), glass, torches, stairs, doors and
    /// dirt floors. Their blocks stand in the world, so the reconcile pass finds them built.</summary>
    private static void AddFinishedBuilds(StoryColony c, FakeStoryWorld w, ref long revRef, ref int orderRef)
    {
        long rev = revRef;
        int order = orderRef;
        void Done(string id, string kind, string material, List<GhostState> ghosts)
        {
            foreach (var g in ghosts) if (g.Job) w.Set(g.At.X, g.At.Y, g.At.Z, g.Id, g.Meta);
            c.Builds.Add(Build(id, kind, material, order++, ghosts[0].Rev, ghosts, "done"));
        }
        revRef = Finished(rev, Done);
        orderRef = order;
    }

    /// <summary>The finished builds, handed to <paramref name="Done"/> one by one; the last revision used.</summary>
    private static long Finished(long rev, Action<string, string, string, List<GhostState>> Done)
    {
        long r = ++rev;
        var floors = new[] { 1, 4, 1, 1, 1, 1, 39, 39, 39, 10, 10, 21, 6, 21, 18, 6, 97, 50, 4, 4, 40, 36, 18 };
        int fx = 1750;
        for (int i = 0; i < floors.Length; i++)
        {
            var g = new List<GhostState>();
            for (int k = 0; k < floors[i]; k++) g.Add(Place(new P3(fx + k % 10, 63, 1232 + k / 10), 3, 0, "dirt", "done", r));
            fx += 11;
            Done("b100" + (100 + i), "floor", "dirt", g);
        }
        var planks = new List<GhostState>();
        for (int y = 64; y <= 78 && planks.Count < 876; y++)
            for (int x = 1823; x <= 1837; x++)
                for (int z = 1179; z <= 1193; z++)
                    if ((x is 1823 or 1837 || z is 1179 or 1193) && planks.Count < 876) planks.Add(Place(new P3(x, y, z), 5, 0, "planks", "done", ++rev));
        for (int x = 1824; x <= 1836 && planks.Count < 876; x++) planks.Add(Place(new P3(x, 79, 1186), 5, 0, "planks", "done", rev));
        Done("b100001", "block", "planks", planks);
        Done("b100002", "block", "chest", ChestCells().Select(p => Place(p, 54, 0, "chest", "done", rev)).ToList());
        var glass = new List<GhostState>();
        for (int y = 65; y <= 81 && glass.Count < 198; y++)
            for (int z = 1178; z <= 1189 && glass.Count < 198; z++) glass.Add(Place(new P3(1841, y, z), 102, 0, "glass_pane", "done", ++rev));
        Done("b100003", "block", "glass_pane", glass);
        Done("b100004", "block", "torch", Enumerable.Range(0, 6).Select(k => Place(new P3(1824 + k, 70 + k, 1180), 50, 5, "torch", "done", rev)).ToList());
        Done("b100005", "block", "wooden_stairs", Enumerable.Range(0, 24).Select(k => Place(new P3(1825 + k % 12, 64 + k / 12, 1185), 53, k % 4, "wooden_stairs", "done", rev)).ToList());
        Done("b100006", "block", "wooden_door", [Place(new P3(1830, 64, 1181), 64, 1, "wooden_door", "done", rev), Place(new P3(1830, 65, 1181), 64, 9, "wooden_door", "done", rev, job: false)]);
        return rev;
    }

    /// <summary>666 chest cells in pairs (double chests) along x, rows two apart, on two levels.</summary>
    public static List<P3> ChestCells()
    {
        var cells = new List<P3>();
        for (int y = 64; y <= 65; y++)
            for (int j = 0; j < 18; j++)
                for (int i = 0; i < 10; i++)
                    for (int h = 0; h < 2 && cells.Count < 666; h++) cells.Add(new P3(1790 + 3 * i + h, y, 1196 + 2 * j));
        return cells;
    }

    /// <summary>374 storages in the chests: most empty, some with a few stacks, 37 missing; most take anything, some nothing.</summary>
    private static void AddStorages(StoryColony c, FakeStoryHost h, DateTimeOffset now)
    {
        int[] items = [4, 3, 263, 15, 5, 17, 280, 1, 13, 12];
        var cells = ChestCells().Take(374).ToList();
        for (int i = 0; i < cells.Count; i++)
        {
            int n = i % 16 switch { < 10 => 0, < 12 => 1, 12 => 2, 13 => 3, 14 => 5, _ => 2 };
            var contents = Enumerable.Range(0, n).Select(k => new Stack(items[(i + k * 3) % items.Length], 0, 8 + (i * 7 + k * 13) % 57)).ToList();
            if (i == 40) contents.Add(new Stack(270, 0, 1)); // a wooden pickaxe in stock
            var s = new StorageState
            {
                Chest = cells[i], Missing = i % 10 == 3, Priority = i % 41 == 5 ? "important" : "normal",
                Filter = new StorageFilter { Allow = i % 11 == 7 ? [] : i % 90 == 1 ? ["food_meals"] : ["*"] },
                Contents = contents, FreeSlots = 27 - contents.Count, KnownAt = now, Rev = 900 + i,
            };
            c.Storages.Add(s);
            h.Chests[s.Chest] = contents.ToList();
        }
    }

    /// <summary>29 furnaces and a crafting table in a row by the chests; six furnaces smelt iron.</summary>
    private static void AddBenches(StoryColony c, FakeStoryWorld w)
    {
        for (int i = 0; i < 30; i++)
        {
            var at = new P3(1778 + i, 64, 1193);
            w.Set(at.X, at.Y, at.Z, i == 0 ? 58 : 61);
            Assert.Null(c.WorkbenchOp("designate", at).Error);
            if (i is > 0 and <= 6)
                Assert.Null(c.BillsOp("add", new JsonObject { ["bench"] = at.Json(), ["bill"] = new JsonObject { ["recipe"] = "smelt:iron_ore", ["mode"] = "until", ["target"] = 16 } }).Error);
        }
    }

    /// <summary>A large farm (2,460 columns), a quarry by home, a guard zone over the tower and a fishing zone.</summary>
    private static void AddZones(StoryColony c, FakeStoryWorld w)
    {
        for (int x = 1764; x <= 1774; x++)
            for (int z = 1170; z <= 1180; z++) w.Set(x, 63, z, 9); // the fishing zone's pond
        Assert.Null(c.ZoneOp("add", "farm", Area.Of(1778, 63, 1179, 1837, 63, 1219)).Error);
        Assert.Null(c.ZoneOp("add", "mine", Area.Of(1776, 63, 1166, 1795, 63, 1175)).Error);
        Assert.Null(c.ZoneOp("add", "guard", Area.Of(1822, 63, 1178, 1838, 63, 1194)).Error);
        Assert.Null(c.ZoneOp("add", "fish", Area.Of(1764, 63, 1170, 1774, 63, 1180)).Error);
    }

    /// <summary>A mine mark, a failed mine mark and a deconstruct mark.</summary>
    private static void AddDesignations(StoryColony c)
    {
        Assert.Null(c.Designate("add", "mine", null, [new P3(1818, 63, 1201)], null).Error);
        Assert.Null(c.Designate("add", "mine", null, [new P3(1750, 63, 1100)], null).Error);
        var failed = c.Designations[^1];
        failed.Status = "failed";
        failed.Fails = 3;
        failed.Reason = "can't reach stone at 1750,63,1100";
        Assert.Null(c.Designate("add", "deconstruct", null, [new P3(1841, 65, 1178)], null).Error); // a glass pane
        Assert.Equal(3, c.Designations.Count);
    }

    /// <summary>Open build cells (to do or out with a pawn).</summary>
    public static int OpenCells(StoryColony c) => c.Builds.Sum(b => b.Ghosts.Count(g => g.Job && g.State is "todo" or "leased"));
}
