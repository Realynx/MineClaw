using System.Diagnostics;
using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;
using MineClaw.World.Chunks;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The director with the live shape (11-04: ~150 % CPU on CT 114, story state p95 365 ms, the architect 644 ms): ten pawn bodies and
/// main, each a real <see cref="WorldState"/> on one <see cref="SharedChunkStore"/>, main ~160 blocks from home (so every lookup
/// near home misses main first), the colony's sight a <see cref="MergedStoryWorld"/>, each pawn's tracker holding the same drops,
/// mobs and players (server-wide ids), the pawns' <see cref="PawnLive"/> read off their inventories like the live host does;
/// ~200 designations, a farm, storages with stock, a build. One "second" = every pawn asks or reports, the watchdog tick, the
/// browser's state delta, and the Architect every fourth second (the web polled it every ~4 s).
/// </summary>
public sealed class StoryLiveShapeBenchmarkTests(ITestOutputHelper output)
{
    private static readonly P3 Home = new(200, 64, 400);

    /// <summary>The live host's shape: <see cref="MergedStoryWorld"/> over main + the pawns' worlds, <see cref="PawnLiveCache"/> reads.</summary>
    internal sealed class LiveShapeHost : IStoryHost
    {
        public readonly WorldState Main;
        public readonly Dictionary<string, WorldState> Bodies = new(StringComparer.OrdinalIgnoreCase);
        public readonly CountingWorld Counting;
        private readonly PawnLiveCache _live = new();
        public readonly Dictionary<P3, List<Stack>> Chests = new();
        public int LiveCalls;

        public LiveShapeHost(WorldState main, IGameData data)
        {
            Main = main;
            Counting = new CountingWorld(new MergedStoryWorld(() => Main, () => Bodies.Values.ToList(), data));
        }

        public IStoryWorld? World => Counting;
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer) => (Bodies.Keys.Take(n).ToList(), null);
        public int Release(string colony) => 0;
        public PawnLive Live(string account)
        {
            Interlocked.Increment(ref LiveCalls);
            return Bodies.TryGetValue(account, out var w) ? _live.Read(account, w, Counting.Data, true) : PawnLive.Offline;
        }
        public string? StartPawn(string account, string colony) => null;
        public void Interrupt(string account, string why) { }
        public IReadOnlyList<Stack>? KnownChest(P3 at) => Chests.TryGetValue(at, out var c) ? c : null;
    }

    /// <summary>Counts the director's calls into its sight.</summary>
    internal sealed class CountingWorld(IStoryWorld inner) : IStoryWorld
    {
        public long Blocks, Loads, EntityLists, Lights;
        public IGameData Data => inner.Data;
        public long TimeOfDay => inner.TimeOfDay;
        public bool TimeFrozen => inner.TimeFrozen;
        public int Height => inner.Height;
        public int Dimension => inner.Dimension;
        public (int Id, int Meta) Block(int x, int y, int z) { Interlocked.Increment(ref Blocks); return inner.Block(x, y, z); }
        public IReadOnlyList<StoryEntity> Entities() { Interlocked.Increment(ref EntityLists); return inner.Entities(); }
        public bool Loaded(int x, int z) { Interlocked.Increment(ref Loads); return inner.Loaded(x, z); }
        public int? NightLight(int x, int y, int z) { Interlocked.Increment(ref Lights); return inner.NightLight(x, y, z); }
        public void Reset() => Blocks = Loads = EntityLists = Lights = 0;
    }

    /// <summary>A plain column: stone to 59, dirt 60..62, grass 63, a tree every so often, ore here and there.</summary>
    private static void LoadColumn(WorldState w, int cx, int cz)
    {
        var ids = new byte[16 * 128 * 16];
        var sky = new byte[ids.Length];
        for (int dx = 0; dx < 16; dx++)
        for (int dz = 0; dz < 16; dz++)
        {
            int b = (dx * 16 + dz) * 128, x = cx * 16 + dx, z = cz * 16 + dz;
            for (int y = 0; y < 128; y++)
            {
                byte id = y == 0 ? (byte)7 : y < 60 ? (byte)1 : y < 63 ? (byte)3 : y == 63 ? (byte)2 : (byte)0;
                if (y is > 5 and < 50 && (x * 31 + y * 17 + z * 7) % 97 == 0) id = 16; // coal
                ids[b + y] = id;
                sky[b + y] = y >= 64 ? (byte)15 : (byte)0;
            }
            if ((x * 7 + z * 13) % 53 == 0 && Math.Abs(x - Home.X) > 6) // trees, never on the home plot
            {
                for (int y = 64; y < 68; y++) ids[b + y] = 17;
                ids[b + 68] = 18;
            }
            else if ((x * 11 + z * 3) % 7 == 0) ids[b + 64] = 31; // tall grass
        }
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, ids, null, null, sky);
    }

    internal static (StoryColony C, LiveShapeHost H) Rig(int pawns = 10, int viewChunks = 5)
    {
        var data = Beta181GameData.Instance;
        var store = new SharedChunkStore(128);
        var main = new WorldState(chunkStore: store);
        var h = new LiveShapeHost(main, data);
        // Main ~160 blocks west of home: its columns never cover home.
        int mcx = (Home.X - 160) >> 4, mcz = Home.Z >> 4, hcx = Home.X >> 4, hcz = Home.Z >> 4;
        for (int cx = mcx - viewChunks; cx <= mcx + viewChunks; cx++)
        for (int cz = mcz - viewChunks; cz <= mcz + viewChunks; cz++) LoadColumn(main, cx, cz);
        main.Player.ServerTeleport(new Vec3d(Home.X - 160 + 0.5, 64, Home.Z + 0.5), 65.62, 0, 0, true);
        var rng = new Random(7);
        var names = Enumerable.Range(0, pawns).Select(i => "maid_chan_" + (101 + i)).ToList();
        for (int i = 0; i < pawns; i++)
        {
            var w = new WorldState(chunkStore: store);
            int ox = (i % 5) - 2, oz = (i / 5) - 1; // the pawns spread a little: their views overlap
            for (int cx = hcx + ox - viewChunks; cx <= hcx + ox + viewChunks; cx++)
            for (int cz = hcz + oz - viewChunks; cz <= hcz + oz + viewChunks; cz++) LoadColumn(w, cx, cz);
            w.Player.ServerTeleport(new Vec3d(Home.X + ox * 6 + 0.5, 64, Home.Z + oz * 6 + 0.5), 65.62, 0, 0, true);
            w.Player.SetHealth(20, 20, 5);
            // A working pawn's pockets: a pickaxe, an axe, a hoe, some blocks, food.
            w.Inventory.SetSlot(0, 36, new ItemStack(274, 1, 3));
            w.Inventory.SetSlot(0, 37, new ItemStack(275, 1, 10));
            w.Inventory.SetSlot(0, 38, new ItemStack(291, 1, 0));
            w.Inventory.SetSlot(0, 39, new ItemStack(4, 32, 0));
            w.Inventory.SetSlot(0, 40, new ItemStack(3, 20, 0));
            w.Inventory.SetSlot(0, 41, new ItemStack(297, 5, 0));
            for (int s = 9; s < 15; s++) w.Inventory.SetSlot(0, s, new ItemStack((short)(s % 2 == 0 ? 17 : 5), 16, 0));
            w.Entities.SelfId = 5000 + i;
            h.Bodies[names[i]] = w;
        }
        // What every pawn's tracker holds (the same server ids): drops around home, animals and monsters, the other pawns.
        var ents = new List<Action<WorldState>>();
        for (int e = 0; e < 40; e++)
        {
            int id = 1000 + e;
            var pos = new Vec3d(Home.X - 20 + rng.Next(40) + 0.5, 64, Home.Z - 20 + rng.Next(40) + 0.5);
            short item = (short)new[] { 4, 3, 17, 295, 296, 287 }[rng.Next(6)];
            ents.Add(w => w.Entities.SpawnItem(id, new ItemStack(item, 1, 0), pos));
        }
        for (int e = 0; e < 25; e++)
        {
            int id = 2000 + e, type = new[] { 90, 91, 92, 93, 54, 50, 51 }[rng.Next(7)];
            var pos = new Vec3d(Home.X - 40 + rng.Next(80) + 0.5, 64, Home.Z - 40 + rng.Next(80) + 0.5);
            ents.Add(w => w.Entities.SpawnMob(id, type, pos, 0, 0));
        }
        for (int i = 0; i < pawns; i++)
        {
            int id = 5000 + i;
            string n = names[i];
            var pos = h.Bodies[n].Player.Position;
            ents.Add(w => { if (w.Entities.SelfId != id) w.Entities.SpawnPlayer(id, n, pos, 0, 0); });
        }
        foreach (var w in h.Bodies.Values) foreach (var a in ents) a(w);

        var c = new StoryColony(new ColonyMeta { Id = "story-1", Name = "Bench", Home = Home }, h) { DoneGrace = TimeSpan.Zero };
        c.Joined(names);
        foreach (var p in c.Pawns.Values)
        {
            foreach (var t in WorkTypes.All) p.Priorities[t] = 3;
            p.Schedule = Enumerable.Repeat(ScheduleBlocks.Anything, 24).ToArray();
        }
        // ~200 designations: a mine box, trees to chop, grass to cut; a farm; three storages with stock; a wall to build.
        Assert.Null(c.Designate("add", "mine", Area.Of(Home.X + 20, 55, Home.Z - 10, Home.X + 29, 59, Home.Z - 8), null, "all").Error);
        var trees = new List<P3>();
        for (int x = Home.X - 60; x < Home.X + 60 && trees.Count < 30; x++)
        for (int z = Home.Z - 60; z < Home.Z + 60 && trees.Count < 30; z++)
            if (h.Counting.Block(x, 64, z).Id == 17) trees.Add(new P3(x, 64, z));
        Assert.Null(c.Designate("add", "chop", null, trees, null).Error);
        Assert.Null(c.Designate("add", "cut", Area.Of(Home.X - 30, 64, Home.Z + 10, Home.X - 10, 64, Home.Z + 20), null, null).Error);
        Assert.Null(c.ZoneOp("add", "farm", Area.Of(Home.X + 5, 63, Home.Z + 5, Home.X + 14, 63, Home.Z + 14), grow: new JsonObject { ["plant"] = "wheat" }).Error);
        var chests = new[] { new P3(Home.X + 2, 64, Home.Z), new P3(Home.X + 4, 64, Home.Z), new P3(Home.X + 6, 64, Home.Z) };
        foreach (var ch in chests)
        {
            foreach (var w in h.Bodies.Values.Append(main)) w.SetBlock(ch.X, ch.Y, ch.Z, 54, 0);
            h.Chests[ch] = [new Stack(4, 0, 64), new Stack(5, 0, 64), new Stack(17, 0, 30), new Stack(3, 0, 64), new Stack(295, 0, 12)];
        }
        Assert.Null(c.StorageOp("set", chests, null, "normal", null).Error);
        Assert.Null(c.BuildsOp("add", new JsonArray(
            new JsonObject { ["kind"] = "wall", ["area"] = new JsonArray(Home.X - 8, 64, Home.Z - 8, Home.X - 2, 64, Home.Z - 2), ["material"] = "cobblestone", ["height"] = 3 }), null).Error);
        return (c, h);
    }

    internal sealed record Numbers(double PickMs, double ReportMs, double TickMs, double StateMs, double ArchitectMs, double ArchitectP95, double StateP95,
        double SecondMs, long Blocks, long Loads, long EntityLists, int LiveCalls, int Jobs);

    /// <summary>Runs <paramref name="seconds"/> simulated seconds (warm-up excluded) and returns the costs per second.</summary>
    internal static Numbers Run(StoryColony c, LiveShapeHost h, int seconds)
    {
        var names = c.Pawns.Keys.ToList();
        long since = c.State()["rev"]!.GetValue<long>();
        c.Architect();
        var picks = new Stopwatch(); var reports = new Stopwatch(); var ticks = new Stopwatch(); var states = new Stopwatch(); var archs = new Stopwatch();
        var stateTimes = new List<double>(); var archTimes = new List<double>();
        h.Counting.Reset();
        h.LiveCalls = 0;
        int jobs = 0;
        var all = Stopwatch.StartNew();
        for (int s = 0; s < seconds; s++)
        {
            for (int i = 0; i < names.Count; i++)
            {
                var n = names[i];
                picks.Start();
                var j = c.Next(n);
                picks.Stop();
                if (j == null) continue;
                jobs++;
                var id = j["id"]!.GetValue<string>();
                reports.Start();
                // A third of the pawns finish each second (a dig really happens), the rest report progress.
                if ((s + i) % 3 == 0)
                {
                    if (j["kind"]?.GetValue<string>() == "dig" && j["at"] is { } at)
                    {
                        var xyz = at.GetValue<string>().Split(',').Select(int.Parse).ToArray();
                        foreach (var w in h.Bodies.Values) w.SetBlock(xyz[0], xyz[1], xyz[2], 0, 0);
                    }
                    c.Report(n, id, (s + i) % 6 == 0 ? "failed" : "done", "bench", null);
                }
                else c.Report(n, id, "progress", null, 0.5);
                reports.Stop();
            }
            ticks.Start(); c.Tick(); ticks.Stop();
            var t0 = states.Elapsed.TotalMilliseconds;
            states.Start();
            var st = c.State(since);
            since = st["rev"]!.GetValue<long>();
            _ = st.ToJsonString();
            states.Stop();
            stateTimes.Add(states.Elapsed.TotalMilliseconds - t0);
            if (s % 4 == 0)
            {
                var a0 = archs.Elapsed.TotalMilliseconds;
                archs.Start(); _ = c.Architect().ToJsonString(); archs.Stop();
                archTimes.Add(archs.Elapsed.TotalMilliseconds - a0);
            }
        }
        all.Stop();
        static double P95(List<double> l) => l.Count == 0 ? 0 : l.OrderBy(x => x).ElementAt(Math.Min(l.Count - 1, (int)(l.Count * 0.95)));
        double per(Stopwatch sw) => sw.Elapsed.TotalMilliseconds / seconds;
        return new Numbers(per(picks), per(reports), per(ticks), per(states), archTimes.Count == 0 ? 0 : archTimes.Average(), P95(archTimes), P95(stateTimes),
            all.Elapsed.TotalMilliseconds / seconds, h.Counting.Blocks / seconds, h.Counting.Loads / seconds, h.Counting.EntityLists / seconds,
            h.LiveCalls / seconds, jobs);
    }

    internal static string Format(Numbers n) =>
        $"per second: total {n.SecondMs:0.00} ms = picks {n.PickMs:0.00} + reports {n.ReportMs:0.00} + tick {n.TickMs:0.00} + state {n.StateMs:0.00} (p95 {n.StateP95:0.00}); " +
        $"architect {n.ArchitectMs:0.00} ms (p95 {n.ArchitectP95:0.00}); world calls/s: block {n.Blocks}, loaded {n.Loads}, entity lists {n.EntityLists}; live reads/s {n.LiveCalls}; {n.Jobs} jobs";

    [Fact]
    public void Ten_pawns_on_shared_worlds_with_a_busy_colony()
    {
        var (c, h) = Rig();
        Run(c, h, 10); // warm-up (JIT, caches)
        var n = Run(c, h, 60);
        output.WriteLine(Format(n));
        // Generous (CI machines vary): one second of play must cost the director a small part of one core.
        Assert.True(n.SecondMs < 250, Format(n));
    }
}
