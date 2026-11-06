using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using MineClaw.App.Supervisor;
using MineClaw.Bot;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Bot.Tests;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Hive;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Story;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// Story Mode end to end on the fake servers, both versions: a colony's script-only pawns come from main's hive hierarchy (allocated,
/// whitelisted, logged in by main's subhive host), each runs the shipped <c>story_pawn</c> as a lane with no model, and the director
/// hands out the work. Each body has its own fake server (block changes mirrored between the pawns' servers), so the colony sees
/// through its first pawn's world; in real play there is one server.
/// </summary>
[Collection(GameDataDefaultCollection.Name)]
public sealed class StoryFakeServerTests(ITestOutputHelper output)
{
    private static readonly P3 WoodChest = new(8, 64, 12), FoodChest = new(5, 64, 12), Table = new(11, 64, 12);
    private readonly object OneWorld = new();

    private static void World(FakeServer s, bool modern)
    {
        // A plain world: a grass field over stone (the fake server's default is a 5x5 island), a tree east of spawn, two chests
        // and a crafting table south of it.
        for (int x = -4; x <= 22; x++)
            for (int z = -4; z <= 22; z++)
            {
                for (int y = 58; y <= 62; y++) s.Blocks[(x, y, z)] = Blocks.Stone;
                s.Blocks[(x, 63, z)] = Blocks.Grass;
            }
        for (int y = 64; y < 68; y++) s.Blocks[(12, y, 8)] = Blocks.Log;
        for (int y = 66; y <= 68; y++)
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    if (!(dx == 0 && dz == 0 && y < 68)) s.Blocks[(12 + dx, y, 8 + dz)] = Blocks.Leaves;
        foreach (var c in new[] { WoodChest, FoodChest })
        {
            s.Blocks[(c.X, c.Y, c.Z)] = Blocks.Chest;
            s.Chests[(c.X, c.Y, c.Z)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        }
        s.Blocks[(Table.X, Table.Y, Table.Z)] = Blocks.CraftingTable;
        s.Workbenches.Add((Table.X, Table.Y, Table.Z));
        s.SpawnAt = (8.5, 64, 8.5);
        s.Inventory[37] = new ItemStack((short)Blocks.Cobblestone, 64, 0); // clutch blocks for work at height, and building stone
        s.DropOnDig = true;
        s.ConsumeOnPlace = true;
        if (modern)
            s.DigDrop = (pos, id, meta) => Release1122GameData.Instance.DropsFor(id, meta, s.Held.IsEmpty ? -1 : s.Held.Id).Where(x => x.Chance >= 1f).ToList() is { Count: > 0 } d
                ? new ItemStack((short)d[0].ItemId, (byte)Math.Max(1, d[0].Min), (short)d[0].Damage) : ItemStack.Empty;
    }

    /// <summary>The clients' chop/pillar/dig lines (diagnostics for the climbing tests).</summary>
    private static readonly ConcurrentQueue<string> ClientLog = new();

    private static ClientOptions Fast(ClientOptions o, FakeServer s) => o with
    {
        TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
        ConnectionFactory = s.CreateConnection, HostileAlertRadius = 0, AutoReconnect = false,
        Log = m => { o.Log?.Invoke(m); if (m.Contains("chop") || m.Contains("pillar") || m.Contains("dig")) { ClientLog.Enqueue(m); while (ClientLog.Count > 60) ClientLog.TryDequeue(out _); } },
    };

    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(20); }
        return cond();
    }

    private sealed class QuietLlm : ILlmClient
    {
        public int Calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            if (!r.Purpose.StartsWith("quick_", StringComparison.Ordinal)) Interlocked.Increment(ref Calls);
            return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, new LlmUsage(10, 1, 0, 0), r.Model ?? "claude-opus-5-5"));
        }
    }

    private sealed class NoClones : IHiveBodies
    {
        public string Spawn(string task, string? name, bool handover, HiveSpawnOptions options) => "error: no clone processes here";
        public string Recall(string name, string reason) => "no: not out";
        public Task WaitGoneAsync(string name, TimeSpan timeout) => Task.CompletedTask;
        public IReadOnlyCollection<string> Running() => [];
        public SlotFacts Slots() => new(20, ["maid_chan", "poofimafox"]);
        public int Pending() => 0;
        public string List() => "";
    }

    private sealed class ListLogger(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) lock (lines) lines.Add(formatter(state, exception) + (exception != null ? " !! " + exception.Message : ""));
        }
        public void Dispose() { }
    }

    private static int InChest(FakeServer s, P3 c, Func<ItemStack, bool> pick) =>
        s.Chests.TryGetValue((c.X, c.Y, c.Z), out var items) ? items.Where(i => !i.IsEmpty && pick(i)).Sum(i => i.Count) : 0;

    /// <summary>One colony in game: the hierarchy, main, its pawns, their servers.</summary>
    private sealed record Ctx(StoryColony C, StoryColonies Colonies, HiveHierarchy H, BotHost Bot, ConcurrentDictionary<string, FakeServer> Servers,
        List<string> Log, bool Modern, QuietLlm Llm, ConcurrentDictionary<string, int> Placed)
    {
        public IReadOnlyList<string> Pawns => C.Pawns.Keys.ToList();
        public List<PawnState> All => C.Resolve(Pawns).Pawns;
        public string Diag()
        {
            List<string> lines;
            lock (Log) lines = Log.TakeLast(14).ToList();
            var parts = Pawns.Select(p =>
            {
                var s = Servers.GetValueOrDefault(p);
                var b = Bot.Bodies!.Find(p) as Body;
                return $"{p}: inv [{string.Join(" ", s?.Inventory.Where(i => !i.IsEmpty).Select(i => $"{i.Id}x{i.Count}") ?? [])}] at {b?.Adapter.World.Player.Position} " +
                    $"script {b?.ScriptBridge?.Controller.Current?.State() ?? b?.ScriptBridge?.Controller.Last?.Outcome}";
            });
            return string.Join(" / ", lines) + " | " + string.Join(" | ", parts);
        }
    }

    private async Task Colony(string version, int pawns, Func<Ctx, Task> scenario, Action<FakeServer>? tune = null)
    {
        bool modern = version == "1.12.2";
        using var data = modern ? GameData.Override(Release1122GameData.Instance) : null;
        using var mode = WorldMode.Override(WorldMode.Normal); // a normal world, not the skyblock default (before the clients: their threads inherit it)
        var profile = modern ? MineClaw.Client.Versioning.GameVersionProfiles.Java1122 : MineClaw.Client.Versioning.GameVersionProfiles.Beta181;
        var log = new List<string>();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new ListLogger(log)));
        var servers = new ConcurrentDictionary<string, FakeServer>(StringComparer.OrdinalIgnoreCase);
        var placed = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        FakeServer Server(string account)
        {
            var s = new FakeServer(release1122: modern) { BlockLock = OneWorld }; // one lock for every server: mirrored writes never deadlock
            World(s, modern);
            tune?.Invoke(s);
            // One world for the pawns: what one places or digs, the others' servers get too.
            s.Mirror = (x, y, z, id, meta) =>
            {
                placed.AddOrUpdate(account, 1, (_, n) => n + 1);
                foreach (var (other, os) in servers)
                    if (!other.Equals(account, StringComparison.OrdinalIgnoreCase) && os.LoggedIn)
                        try { lock (OneWorld) os.SetBlock(x, y, z, id, meta); } catch (Exception) { }
            };
            return s;
        }
        var mainServer = servers.GetOrAdd("maid_chan", Server);

        var cfg = new HiveConfig { Subhives = { ViewDistance = 3 } };
        var gate = new CommandGate(new CommandOptions(), () => ["poofimafox"]);
        var managed = new ManagedAccounts(null);
        var whitelist = new WhitelistManager(managed, cfg.Accounts, "maid_chan", gate.ArmManagedWhitelist, (line, _) =>
        {
            bool ok = gate.AllowOutgoing(line);
            if (ok) lock (log) log.Add("sent " + line);
            return Task.FromResult(ok);
        }, loggers.CreateLogger("whitelist"));
        gate.ManagedAccount = whitelist.Accepts;
        var h = new HiveHierarchy(cfg.Accounts, "maid_chan", managed, whitelist, regulars: () => ["poofimafox"], log: loggers.CreateLogger("hierarchy"))
        {
            Bodies = new NoClones(),
        };
        var agent = TestBot.Options();
        agent.Llm.ApiKey = "sk-ant-test";
        if (modern) agent.GameVersion = "1.12.2";
        var mc = new MinecraftSettings { Host = "127.0.0.1", Port = 1, Username = "maid_chan" };
        var board = new MineClaw.Hive.Claims.ClaimBoard(TimeSpan.FromSeconds(60));
        await using var host = InProcBodies.CreateSubhives(cfg, new BodiesOptions { LaunchGapSeconds = 0.5, LaunchBatch = 1, YieldCheckSeconds = 0, TickReportSeconds = 0 },
            mc, agent, loggers, profile, board)!;
        var mainClient = new MinecraftClient(Fast(new ClientOptions { Username = "maid_chan", Profile = profile }, mainServer));
        StoryColonies? colonies = null;
        // The fake pipes time out after 30 s without a packet: the servers keep their clients alive as a real one does.
        using var stop = new CancellationTokenSource();
        var keepAlive = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var srv in servers.Values.Where(x => x.LoggedIn))
                    try { srv.Send(new MineClaw.Protocol.Packets.TimeUpdatePacket { Time = 6000 }); } catch (Exception) { }
                try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await mainClient.ConnectAsync(cts.Token);
            Assert.True(await Until(() => mainClient.World.Player.OnGround, 10000));
            var llm = new QuietLlm();
            using var bot = BotHost.Create(new MinecraftGameClient(mainClient), agent, null, llm, loggers, persist: false,
                hiveBots: () => ["maid_chan", .. host.Registry?.Accounts ?? []]);
            var rt = bot.Runtime;
            host.Attach(bot, mainClient, CancellationToken.None);
            SubhiveWiring.AttachMain(h, host, cfg, loggers.CreateLogger("main"));
            h.Resources = null;
            var setup = host.ClientSetup!;
            host.ClientSetup = o => Fast(setup(o), servers.GetOrAdd(o.Username, Server));
            var scripts = MineClaw.Bot.GameTools.ScriptTools.Of(rt)!;
            var crewBoard = new DbCrewBoard(rt.Memory.Db);
            var own = new BodyCrewPort(bot.Bodies!, rt.Tools, crewBoard);
            scripts.Crew = own;
            SubhiveWiring.AttachScriptHives(h, scripts, own, bot.Bodies!, rt.Tools, crewBoard, loggers.CreateLogger("hive"));

            // The shipped pawn script is installed by default; the colony is created as the route does it.
            Assert.Equal("installed", StoryColonies.Install(scripts.Library));
            var storyHost = new StoryHost(h, () => bot.Bodies,
                () => bot.Bodies!.All.OfType<Body>().FirstOrDefault(x => !x.IsMain && x.State == BodyState.Online)?.Adapter.World ?? mainClient.World,
                profile.Data, () => scripts, loggers.CreateLogger("story"));
            colonies = new StoryColonies(storyHost, new StoryStore(rt.Memory.Db), log: loggers.CreateLogger("story"));
            var (c, err) = colonies.Create("Test", pawns, new P3(8, 64, 8));
            Assert.True(c != null, err);
            Assert.Equal(pawns, c!.Pawns.Count);
            Assert.All(c.Pawns.Keys, p => Assert.Equal("story-1", h.Tree.Get(p)!.Crew)); // members of the colony group, owned by main
            Assert.Empty(own.Bodies()); // main's own crews never get a pawn
            colonies.Start(TimeSpan.FromMilliseconds(250));
            Assert.True(await Until(() => c.Pawns.Keys.All(p => bot.Bodies!.Find(p) is Body { State: BodyState.Online } pb && pb.Adapter.World.GetBlock(12, 64, 8) == Blocks.Log), 40000),
                "the pawns are in and see the world");
            var ctx = new Ctx(c, colonies, h, bot, servers, log, modern, llm, placed);
            foreach (var p in ctx.All) p.Schedule = Enumerable.Repeat(ScheduleBlocks.Anything, 24).ToArray();
            await scenario(ctx);
            Assert.Equal(0, llm.Calls); // no model, ever: not for the pawns, not for main hearing about them
            output.WriteLine(c.State()["pawns"]!.ToJsonString());

            // Released: the pawns deposit and log out, the colony's state stays.
            colonies.Release(c);
            Assert.True(await Until(() => c.Pawns.Keys.All(p => h.Tree.Get(p) == null), 90000), "released: " + string.Join(", ", c.Pawns.Keys.Select(p => $"{p} {h.Tree.Get(p)?.State ?? "gone"}")) + " | " + ctx.Diag());
            Assert.False(c.Meta.Active);
        }
        finally
        {
            stop.Cancel();
            await keepAlive;
            colonies?.Dispose();
            await mainClient.DisposeAsync();
            foreach (var srv in servers.Values) srv.Dispose();
        }
    }

    private static void Only(Ctx x, params string[] work)
    {
        foreach (var p in x.All) foreach (var t in WorkTypes.All) p.Priorities[t] = work.Contains(t) ? 1 : 0;
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_pawn_chops_a_designated_tree_and_stores_the_logs_in_the_wood_chest(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var pawn = x.Pawns[0];
        Assert.Null(c.StorageOp("set", [WoodChest], new StorageFilter { Allow = ["wood"] }, "critical", "Wood").Error);
        Assert.Null(c.StorageOp("set", [FoodChest], new StorageFilter { Allow = ["food"] }, "critical", "Food").Error);
        Assert.Equal(1, c.Designate("add", "chop", Area.Of(10, 60, 6, 14, 72, 10), null, null).Result!["count"]!.GetValue<int>());
        Only(x, WorkTypes.Chop, WorkTypes.Haul);
        c.SetHaulOptions(null, false, null); // no fallback chest: what no filter takes stays with the pawn
        // It chops, then carries the logs to the Wood chest (the director's record, kept after each deposit; the 1.12.2 fake
        // server keeps the chest's slots too).
        Assert.True(await Until(() => c.StorageAt(WoodChest)!.Contents.Any(s => s.Id == Blocks.Log), 90_000), "logs stored: " + x.Diag());
        var srv = x.Servers[pawn];
        if (x.Modern) Assert.True(await Until(() => InChest(srv, WoodChest, i => i.Id == Blocks.Log) >= 1, 5000), "logs in the server's chest");
        Assert.Empty(c.StorageAt(FoodChest)!.Contents); // the Food chest takes no wood
        Assert.Equal(0, InChest(srv, FoodChest, _ => true));
        Assert.Contains(srv.Dug, d => d.Id == Blocks.Log && d.X == 12 && d.Z == 8);
        Assert.Empty(c.Designations); // the tree's job is done
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("No storage for:") && a.Text.Contains("Cobblestone")); // kept, never dropped
        var chop = c.Pawns[pawn].Skills[WorkTypes.Chop];
        Assert.True(chop.Xp > 0 || chop.Level > StoryCharacter.Generate(pawn).Skills[WorkTypes.Chop].Level, "chop experience");
    });

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task Two_pawns_build_a_hut_with_a_flat_roof_from_the_queue(string version) => Colony(version, 2, async x =>
    {
        var c = x.C;
        Only(x, WorkTypes.Build);
        var add = c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "wall", ["area"] = new JsonArray(15, 64, 1, 19, 64, 5), ["material"] = "cobblestone", ["height"] = 2,
            ["roof"] = new JsonObject { ["style"] = "flat" },
        }), null);
        Assert.Null(add.Error);
        var b = c.Builds.Single();
        Assert.Equal(16 * 2 + 25, b.BlocksTotal);
        Assert.True(await Until(() => b.Status == "done", 150_000), $"hut: {b.BlocksDone}/{b.BlocksTotal} {b.Status} {b.Reason}: " + x.Diag());
        var srv = x.Servers[x.Pawns[0]];
        foreach (var g in b.Ghosts) Assert.True(srv.Blocks.GetValueOrDefault((g.At.X, g.At.Y, g.At.Z)) == Blocks.Cobblestone, $"{g.At} built");
        Assert.All(x.Pawns, p => Assert.True(x.Placed.GetValueOrDefault(p) > 0, p + " placed some")); // both pawns worked on it
        Assert.Contains(c.Alerts, a => a.Text.Contains("is done"));
    });

    /// <summary>
    /// Building fast (docs/web/story-mode.md "Building fast"; live 11-03 "building floors goes very slowly"): three builders split a
    /// 6×6 planks floor into batches, each fetches its share in one chest session and lays it from one stand spot. No cell fails
    /// (no "busy" chest, no "too far", no "occupied" for a cell that is already built) and the floor is done in well under a minute.
    /// </summary>
    [Fact]
    public Task Three_pawns_lay_a_6x6_floor_from_one_chest_fast_and_without_failures() => Colony("1.12.2", 3, async x =>
    {
        var c = x.C;
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Wood").Error);
        c.AddContents(c.StorageAt(WoodChest)!, new Stack(Blocks.Planks, 0, 64)); // what the bot remembers of the chest
        Only(x, WorkTypes.Build);
        int logBefore;
        lock (x.Log) logBefore = x.Log.Count;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject
        {
            ["kind"] = "floor", ["area"] = new JsonArray(14, 64, 14, 19, 64, 19), ["material"] = "planks",
        }), null).Error);
        var b = c.Builds.Single();
        Assert.Equal(36, b.BlocksTotal);
        Assert.True(await Until(() => b.Status == "done", 120_000), $"floor: {b.BlocksDone}/{b.BlocksTotal} {b.Status} {b.Reason}: " + x.Diag());
        sw.Stop();
        var srv = x.Servers[x.Pawns[0]];
        foreach (var g in b.Ghosts) Assert.Equal(Blocks.Planks, srv.Blocks.GetValueOrDefault((g.At.X, g.At.Y, g.At.Z)));
        List<string> lines;
        lock (x.Log) lines = x.Log.Skip(logBefore).Where(l => l.StartsWith("story story-1:", StringComparison.Ordinal) && l.Contains(" failed: ")).ToList();
        output.WriteLine($"6x6 floor by 3 pawns: {sw.Elapsed.TotalSeconds:0.0} s; placed per pawn: " +
                         string.Join(", ", x.Pawns.Select(p => $"{p} {x.Placed.GetValueOrDefault(p)}")));
        Assert.True(lines.Count == 0, "failures: " + string.Join(" | ", lines));
        Assert.True(x.Pawns.Count(p => x.Placed.GetValueOrDefault(p) > 0) >= 2, "the floor was shared: " + string.Join(", ", x.Pawns.Select(p => $"{p} {x.Placed.GetValueOrDefault(p)}")));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed.TotalSeconds:0} s");
    }, s => s.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)][0] = new ItemStack((short)Blocks.Planks, 64, 0));

    /// <summary>
    /// Rotation (docs/web/story-mode.md "Rotation"; Fox 11-04: "they don't take into consideration the direction they're facing when
    /// they place it"): a pawn builds a small design with a furnace facing east, stairs rising each way, a wall torch and (1.12.2) a log
    /// on its side, on a fake server that turns placed blocks by the vanilla rules. The world ends with exactly the designed metas.
    /// </summary>
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_pawn_builds_a_design_with_each_block_turned_as_drawn(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        Only(x, WorkTypes.Build);
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Wood").Error);
        var chest = c.StorageAt(WoodChest)!;
        c.AddContents(chest, new Stack(Blocks.Furnace, 0, 1));
        c.AddContents(chest, new Stack(Blocks.WoodenStairs, 0, 4));
        c.AddContents(chest, new Stack(Blocks.Torch, 0, 1));
        c.AddContents(chest, new Stack(Blocks.Log, 0, 1));
        // The design: [x, y, z, id, meta] (the posts at 13,64,17 and 18,64,17 are the log's and the torch's walls).
        var want = new List<(int X, int Y, int Z, int Id, int Meta)>
        {
            (15, 64, 15, Blocks.Furnace, 5),                                    // front east
            (17, 64, 15, Blocks.WoodenStairs, 0), (18, 64, 15, Blocks.WoodenStairs, 1), // rising east, west
            (19, 64, 15, Blocks.WoodenStairs, 2), (20, 64, 15, Blocks.WoodenStairs, 3), // south, north
            (17, 64, 17, Blocks.Torch, 2),                                      // on the east wall
        };
        if (x.Modern) want.Add((14, 64, 17, Blocks.Log, 4));                     // along x
        var items = new JsonArray(want.Select(w => (JsonNode)new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(w.X, w.Y, w.Z)), ["material"] = new JsonArray(w.Id, w.Meta), ["name"] = "turned",
        }).ToArray());
        Assert.Null(c.BuildsOp("add", items, null).Error);
        Assert.True(await Until(() => c.Builds.All(b => b.Status == "done"), 180_000),
            string.Join("; ", c.Builds.Select(b => $"{b.Label} {b.Status} {b.Reason}")) + " | " + x.Diag());
        var srv = x.Servers[x.Pawns[0]];
        foreach (var w in want)
        {
            Assert.Equal(w.Id, srv.Blocks.GetValueOrDefault((w.X, w.Y, w.Z)));
            Assert.True(srv.Metas.GetValueOrDefault((w.X, w.Y, w.Z)) == w.Meta, $"{w}: meta {srv.Metas.GetValueOrDefault((w.X, w.Y, w.Z))}");
        }
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.State == "misrotated");
    }, s =>
    {
        s.OrientBlocks = true;
        s.Blocks[(13, 64, 17)] = Blocks.Cobblestone;
        s.Blocks[(18, 64, 17)] = Blocks.Cobblestone;
        var box = s.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)];
        box[0] = new ItemStack((short)Blocks.Furnace, 1, 0);
        box[1] = new ItemStack((short)Blocks.WoodenStairs, 4, 0);
        box[2] = new ItemStack((short)Blocks.Torch, 1, 0);
        box[3] = new ItemStack((short)Blocks.Log, 1, 0);
    });

    /// <summary>
    /// Builds reconcile with the world (docs/web/story-mode.md; Fox 11-05: wrongly turned stairs not detected, "no way to place" where
    /// there was one, blocks in the way). A stair already in its cell turned east where the design rises south is taken back and placed
    /// again turned right; a stone in a wall cell is dug out (the pawn has a pickaxe) and the planks go in; a cell whose only neighbour
    /// is a slab is placed against it. No cell ends blocked or misrotated, and nothing says "occupied by" or "nothing solid".
    /// </summary>
    [Theory]
    [InlineData("1.12.2")]
    [InlineData("b1.8.1")]
    public Task A_pawn_fixes_a_wrong_stair_clears_a_stone_and_places_against_a_slab(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        Only(x, WorkTypes.Build);
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Wood").Error);
        var chest = c.StorageAt(WoodChest)!;
        c.AddContents(chest, new Stack(Blocks.Planks, 0, 8));
        c.AddContents(chest, new Stack(Blocks.WoodenStairs, 0, 2));
        var want = new List<(int X, int Y, int Z, int Id, int Meta)>
        {
            (15, 64, 15, Blocks.Planks, 0), (16, 64, 15, Blocks.Planks, 0), // 15,64,15 holds stone
            (18, 64, 15, Blocks.WoodenStairs, 2),                           // there already, rising east
            (20, 65, 15, Blocks.Planks, 0),                                 // over a slab, nothing else beside it
        };
        var items = new JsonArray(want.Select(w => (JsonNode)new JsonObject
        {
            ["kind"] = "block", ["blocks"] = new JsonArray(new JsonArray(w.X, w.Y, w.Z)), ["material"] = new JsonArray(w.Id, w.Meta), ["name"] = "house",
        }).ToArray());
        Assert.Null(c.BuildsOp("add", items, null).Error);
        Assert.True(await Until(() => c.Builds.All(b => b.Status == "done"), 180_000),
            string.Join("; ", c.Builds.Select(b => $"{b.Label} {b.Status} {b.Reason}")) + " | " + x.Diag());
        var srv = x.Servers[x.Pawns[0]];
        foreach (var w in want)
        {
            Assert.Equal(w.Id, srv.Blocks.GetValueOrDefault((w.X, w.Y, w.Z)));
            Assert.True(srv.Metas.GetValueOrDefault((w.X, w.Y, w.Z)) == w.Meta, $"{w}: meta {srv.Metas.GetValueOrDefault((w.X, w.Y, w.Z))}");
        }
        Assert.DoesNotContain(c.Builds.SelectMany(b => b.Ghosts), g => g.State is "misrotated" or "blocked");
        lock (x.Log) Assert.DoesNotContain(x.Log, l => l.Contains("is occupied by") || l.Contains("nothing solid on any side"));
    }, s =>
    {
        s.OrientBlocks = true;
        s.Blocks[(15, 64, 15)] = Blocks.Stone;
        s.Blocks[(18, 64, 15)] = Blocks.WoodenStairs;
        s.Metas[(18, 64, 15)] = 0;
        s.Blocks[(20, 64, 15)] = Blocks.Slab;
        s.Inventory[38] = new ItemStack((short)Items.StonePickaxe, 1, 0);
        var box = s.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)];
        box[0] = new ItemStack((short)Blocks.Planks, 8, 0);
        box[1] = new ItemStack((short)Blocks.WoodenStairs, 2, 0);
    });

    /// <summary>
    /// Water (docs/web/story-mode.md "Water"; live 11-04 "the pawns have a really difficult time pouring water in the canals"). The
    /// pawn, holding two full buckets, digs and pours a 2×2 infinite pool (two corners: the fake server's water settles the other two),
    /// then digs a 4-long canal and pours it every other cell, filling its buckets at the colony's own pool (which refills). The
    /// first pass runs flowing water into the canal's end before the second pass pours into it: a cell holding flowing water takes
    /// the pour (aimed at the trench floor from the bank) and becomes a source. Every cell ends a water source; nothing is refused
    /// as "no block face" or "protected".
    /// </summary>
    /// <summary>The colony's fill and pour lines (why a bucket didn't fill, which cells failed): a failing water run says them.</summary>
    private static string FillLines(Ctx x)
    {
        lock (x.Log)
            return string.Join(" | ", x.Log.Where(l => l.Contains("couldn't fill") || l.Contains("cell ") && l.Contains("failed")).TakeLast(8));
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_pawn_pours_an_infinite_pool_then_a_canal_every_other_cell_filling_at_its_own_pool(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        Only(x, WorkTypes.Build);
        JsonArray Cells(params (int X, int Z)[] cs) => new(cs.Select(p => (JsonNode)new JsonArray(p.X, 63, p.Z)).ToArray());
        var pool = Cells((4, 16), (5, 16), (4, 17), (5, 17));
        var canal = Cells((15, 16), (16, 16), (17, 16), (18, 16));
        Assert.Null(c.BuildsOp("add", new JsonArray(
            new JsonObject { ["kind"] = "remove", ["blocks"] = pool.DeepClone(), ["group"] = "pool" },
            new JsonObject { ["kind"] = "water", ["blocks"] = pool.DeepClone(), ["group"] = "pool", ["infinite"] = true },
            new JsonObject { ["kind"] = "remove", ["blocks"] = canal.DeepClone(), ["group"] = "canal" },
            new JsonObject { ["kind"] = "water", ["blocks"] = canal.DeepClone(), ["group"] = "canal" }), null).Error);
        var waters = c.Builds.Where(b => b.Kind == "water").ToList();
        Assert.True(await Until(() => waters.All(b => b.Status == "done"), 300_000), // generous: the whole Story suite runs beside it
            string.Join("; ", waters.Select(b => $"{b.Label}: {b.BlocksDone}/{b.BlocksTotal} {b.Status} {b.Reason}")) + " || fills: " + FillLines(x) + " || " + x.Diag());
        var srv = x.Servers[x.Pawns[0]];
        foreach (var g in waters.SelectMany(b => b.Ghosts))
            Assert.True(srv.Blocks.GetValueOrDefault((g.At.X, g.At.Y, g.At.Z)) == 9 && srv.Metas.GetValueOrDefault((g.At.X, g.At.Y, g.At.Z)) == 0, $"{g.At} is a source");
        Assert.True(c.Renewable(waters[0]), "the pool refills");
        Assert.NotEmpty(srv.Scoops); // the canal's buckets were filled at the colony's own pool
        Assert.All(srv.Scoops, s => Assert.True(s.X is 4 or 5 && s.Z is 16 or 17, $"scooped at {s}"));
        Assert.Contains(srv.PouredInto, p => p.Z == 16 && p.X is >= 15 and <= 18 && p.Id is 8 or 9 && p.Meta != 0); // into flowing water
        Assert.True(srv.Pours.Count(p => p.Z == 16 && p.X is >= 15 and <= 18) < 4, "the cell between two sources filled by itself");
        List<string> lines;
        lock (x.Log) lines = x.Log.Where(l => l.Contains("no block face") || l.Contains("protected water") || l.Contains("drown")).ToList();
        Assert.True(lines.Count == 0, string.Join(" | ", lines));
    }, s =>
    {
        s.Buckets = true;
        s.LiquidFlow = true;
        s.Inventory[38] = new ItemStack((short)Items.WaterBucket, 1, 0);
        s.Inventory[39] = new ItemStack((short)Items.WaterBucket, 1, 0);
    });

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_bill_for_8_sticks_takes_planks_from_storage_crafts_and_stores_the_sticks(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var pawn = x.Pawns[0];
        var srv = x.Servers[pawn];
        lock (srv.BlockLock) srv.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)][0] = new ItemStack((short)Blocks.Planks, 8, 0);
        Assert.Null(c.StorageOp("set", [WoodChest], new StorageFilter { Allow = ["wood"] }, "normal", "Wood").Error);
        Assert.Null(c.StorageOp("set", [FoodChest], new StorageFilter { Allow = ["food"] }, "critical", "Food").Error);
        c.AddContents(c.StorageAt(WoodChest)!, new Stack(Blocks.Planks, 0, 8)); // what the bot remembers of the chest
        var bill = c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = "craft:stick", ["mode"] = "count", ["target"] = 8 } });
        Assert.Null(bill.Error);
        Only(x, WorkTypes.Craft);
        var b = c.Benches.Single(w => w.Pos == Table).Bills.Single();
        Assert.True(await Until(() => b.Done >= 8, 90_000), $"bill {b.Done}/{b.Target} {b.SuspendedReason}: " + x.Diag());
        Assert.True(c.StorageAt(WoodChest)!.Contents.Where(s => s.Id == Items.Stick).Sum(s => s.Count) >= 8, "sticks stored in the Wood chest");
        if (x.Modern) Assert.True(await Until(() => InChest(srv, WoodChest, i => i.Id == Items.Stick) >= 8, 5000), "sticks in the server's chest");
        Assert.Empty(c.StorageAt(FoodChest)!.Contents);
        Assert.NotEqual("craft", c.CurrentOf(pawn)?.Kind); // done: no more
    });

    // ================================================================ live 11-03: every designation kind, from an empty inventory

    private static void EmptyHanded(FakeServer s) => s.Inventory[37] = ItemStack.Empty; // no clutch cobble: pawns start with nothing

    /// <summary>A tree of <paramref name="h"/> logs from y 64 with a 3x3 crown over its top two logs and a cap.</summary>
    private static void Tree(FakeServer s, int x, int z, int h)
    {
        int top = 64 + h - 1;
        for (int y = top - 1; y <= top + 1; y++)
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                    s.Blocks[(x + dx, y, z + dz)] = Blocks.Leaves;
        for (int y = 64; y <= top; y++) s.Blocks[(x, y, z)] = Blocks.Log;
    }

    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task An_empty_handed_pawn_does_every_designation_kind(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var pawn = x.Pawns[0];
        var srv = x.Servers[pawn];
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Everything").Error);
        Only(x, WorkTypes.Chop, WorkTypes.Farm, WorkTypes.Mine, WorkTypes.Build, WorkTypes.Haul);
        // A drop to haul, seen by the colony.
        srv.SpawnDrop(3.5, 64.1, 6.5, new ItemStack((short)Blocks.Cobblestone, 3, 0));
        Assert.True(await Until(() => c.Host.World!.Entities().Any(e => e.Kind == "item" && e.ItemId == Blocks.Cobblestone), 5000), "the drop is seen");
        Assert.Equal(1, c.Designate("add", "cut", null, [new P3(4, 64, 4)], null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "harvest", null, [new P3(5, 64, 4)], null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "mine", null, [new P3(6, 64, 4)], null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "deconstruct", null, [new P3(7, 64, 4)], null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "haul", Area.Of(2, 63, 5, 4, 66, 7), null, null).Result!["count"]!.GetValue<int>());
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(12, 68, 8)], null).Result!["count"]!.GetValue<int>()); // the crown's cap
        Assert.True(await Until(() => c.Designations.Count == 0, 150_000),
            "all done: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.Fails} {d.Reason}")) + " | " + x.Diag());
        int B(int bx, int by, int bz) => srv.Blocks.GetValueOrDefault((bx, by, bz));
        Assert.Equal(0, B(4, 64, 4));                                         // the tall grass is cut
        Assert.True(B(5, 64, 4) == 0 || srv.Metas.GetValueOrDefault((5, 64, 4)) < 7, "the wheat is harvested");
        Assert.Equal(0, B(6, 64, 4));                                         // mined
        Assert.Equal(0, B(7, 64, 4));                                         // deconstructed
        Assert.All(Enumerable.Range(64, 4), y => Assert.Equal(0, B(12, y, 8))); // the tree is down
        Assert.True(await Until(() => !c.Host.World!.Entities().Any(e => e.Kind == "item" && e.ItemId == Blocks.Cobblestone && e.X < 5), 5000), "hauled");
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed 3 times"));
    }, s =>
    {
        EmptyHanded(s);
        s.Blocks[(4, 64, 4)] = 31; s.Metas[(4, 64, 4)] = 1;   // tall grass
        s.Blocks[(5, 63, 4)] = 60;                            // farmland
        s.Blocks[(5, 64, 4)] = 59; s.Metas[(5, 64, 4)] = 7;   // ripe wheat
        s.Blocks[(6, 64, 4)] = Blocks.Dirt;                   // a dirt block to mine
        s.Blocks[(7, 64, 4)] = Blocks.Planks;                 // a built block to deconstruct
    });

    /// <summary>
    /// Live 11-04: seeds inside the farm after a harvest were offered by the colony and written off by the pawn's client ("1 drop(s) left
    /// near the void"): 1.12.2 farmland is 15/16 high and the client stood only on full blocks. Now the pawn walks onto the field, picks
    /// them up and stores them; no spot is rested as unreachable.
    /// </summary>
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_seed_inside_a_tilled_field_is_hauled_from_the_farmland(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Everything").Error);
        Only(x, WorkTypes.Haul);
        double rest = x.Modern ? 63.9375 : 64.0; // on 1.12.2 farmland's 15/16 top; b1.8.1 BlockSoil collides as a full cube
        srv.SpawnDrop(18.5, rest, 4.5, new ItemStack((short)Items.Seeds, 2, 0));
        Assert.True(await Until(() => c.Host.World!.Entities().Any(e => e.Kind == "item" && e.ItemId == Items.Seeds), 5000), "the drop is seen");
        Assert.True(await Until(() => c.StorageAt(WoodChest)!.Contents.Any(s => s.Id == Items.Seeds), 60_000), "seeds stored: " + x.Diag());
        lock (x.Log)
        {
            Assert.DoesNotContain(x.Log, l => l.Contains("unreachable") || l.Contains("near the void"));
        }
    }, s =>
    {
        EmptyHanded(s);
        for (int fx = 14; fx <= 22; fx++)
            for (int fz = 0; fz <= 8; fz++) s.Blocks[(fx, 63, fz)] = 60; // a 9×9 tilled field east of spawn
    });

    /// <summary>Live 11-04: pawns in a ravine under home "looks stuck … 3 targets out of reach in a row" ×6 and kept cycling tasks.
    /// Now: stuck after three, only escape work, a staircase cut up the ravine wall toward home, then back to its work.</summary>
    [Fact]
    public Task A_pawn_on_a_ravine_floor_cuts_a_stair_up_the_wall_and_goes_back_to_work() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Chop);
        Assert.Equal(3, c.Designate("add", "cut", null, [new P3(4, 64, 4), new P3(5, 64, 4), new P3(6, 64, 4)], null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => c.Designations.Count == 0, 240_000),
            "cut: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.Fails} {d.Reason}")) + " | " + x.Diag() + " | " + string.Join(" / ", ClientLog.TakeLast(20)));
        Assert.False(c.IsStuck(x.Pawns[0]));
        Assert.Contains(srv.Dug, d => d.Z < 15 && d.Y >= 52 && d.Y <= 63); // the stair in the ravine's north wall
        Assert.DoesNotContain(srv.Dug, d => d.Y < 50);                      // never below its floor
    }, s =>
    {
        EmptyHanded(s);
        for (int x = -4; x <= 22; x++)
            for (int z = -4; z <= 22; z++)
                for (int y = 50; y <= 62; y++) s.Blocks[(x, y, z)] = Blocks.Dirt; // deep dirt (quick to dig) under the grass
        for (int x = 2; x <= 20; x++)
            for (int z = 15; z <= 17; z++)
                for (int y = 51; y <= 63; y++) s.Blocks.Remove((x, y, z));     // a ravine 3 wide, 13 deep, 19 long, no slope
        foreach (var at in new[] { (4, 64, 4), (5, 64, 4), (6, 64, 4) }) { s.Blocks[at] = 31; s.Metas[at] = 1; } // tall grass at home
        s.SpawnAt = (10.5, 51, 16.5); // the pawns come in on its floor
    });

    /// <summary>Fox 11-04: "it should nerd-pole up to reach those blocks on its own and destroy the nerd pole on the way back down." A
    /// 14-log tree is felled whole from a dirt pillar that is dug back down, its dirt collected again.</summary>
    [Fact]
    public Task A_pawn_nerd_poles_up_a_tall_tree_takes_it_all_and_leaves_no_pillar() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Chop);
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(16, 64, 16)], null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => c.Designations.Count == 0, 180_000),
            "done: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.Fails} {d.Reason}")) + " | " + x.Diag());
        Assert.Equal(0, Enumerable.Range(64, 14).Count(y => srv.Blocks.GetValueOrDefault((16, y, 16)) == Blocks.Log)); // the whole trunk
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("took what it could reach"));
        // No pillar left: no dirt standing above the ground around the trunk.
        var left = srv.Blocks.Where(b => b.Value == Blocks.Dirt && b.Key.Item2 >= 64 && Math.Abs(b.Key.Item1 - 16) <= 2 && Math.Abs(b.Key.Item3 - 16) <= 2).ToList();
        string PillarLog() => string.Join(" / ", ClientLog.TakeLast(40));
        Assert.True(left.Count == 0, $"pillar left: {string.Join(" ", left.Select(b => b.Key))} | {PillarLog()}");
        Assert.True(srv.Inventory.Where(i => !i.IsEmpty && i.Id == Blocks.Dirt).Sum(i => i.Count) >= 28, "the pillar's dirt came back");
    }, s => { EmptyHanded(s); s.Inventory[37] = new ItemStack((short)Blocks.Dirt, 32, 0); Tree(s, 16, 16, 14); });

    /// <summary>Live 11-04 story-1: "no clutch blocks on me (no cobble/planks to be had) … 4 log(s) out of reach; chopped 0/4", Fox: "make it
    /// farm trees". A pawn with nothing at all on it fells a 12-log tree whole: it climbs on the junk it has (the tree's own felled logs,
    /// dirt dug beside it), digs the pillar back down and leaves no pillar and no hole.</summary>
    [Fact]
    public Task An_empty_handed_pawn_fells_a_tall_tree_whole_with_no_cobble_anywhere() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Chop);
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(16, 64, 16)], null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => c.Designations.Count == 0, 180_000),
            "done: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.At} {d.Fails} {d.Reason}")) + " | " + x.Diag() + " | " + string.Join(" / ", ClientLog.TakeLast(30)));
        Assert.Equal(0, Enumerable.Range(64, 12).Count(y => srv.Blocks.GetValueOrDefault((16, y, 16)) == Blocks.Log)); // the whole trunk
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("took what it could reach"));
        var left = srv.Blocks.Where(b => b.Key.Item2 >= 64 && b.Value is Blocks.Dirt or Blocks.Log && Math.Abs(b.Key.Item1 - 16) <= 2 && Math.Abs(b.Key.Item3 - 16) <= 2).ToList();
        Assert.True(left.Count == 0, $"pillar left: {string.Join(" ", left.Select(b => b.Key))}");
        Assert.DoesNotContain(srv.Blocks, b => b.Value == Blocks.Cobblestone); // there never was any
    }, s => { EmptyHanded(s); Tree(s, 16, 16, 12); });

    /// <summary>Live 11-04: maid_chan_114 stood for good in a hole two deep ("no path … it's 2 blocks up"). Now it digs a stair out
    /// (no blocks on it) and goes on to its job.</summary>
    [Fact]
    public Task An_empty_handed_pawn_in_a_two_deep_hole_digs_a_stair_out_and_cuts_its_grass() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Chop);
        Assert.Equal(1, c.Designate("add", "cut", null, [new P3(16, 64, 8)], null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => c.Designations.Count == 0, 120_000),
            "cut: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.Fails} {d.Reason}")) + " | " + x.Diag());
        Assert.Equal(0, srv.Blocks.GetValueOrDefault((16, 64, 8)));
        Assert.Contains(srv.Dug, d => d.Y >= 62 && Math.Abs(d.X - 8) + Math.Abs(d.Z - 8) == 1); // a stair step out of the hole
        Assert.DoesNotContain(srv.Dug, d => d.X == 8 && d.Z == 8 && d.Y < 62);                // never down
        Assert.DoesNotContain(c.Alerts, a => a.Level == "warn" && !a.Text.StartsWith("No storage for")); // the stair's dirt has no chest here: that is said
    }, s =>
    {
        EmptyHanded(s);
        s.Blocks[(8, 62, 8)] = 0; s.Blocks[(8, 63, 8)] = 0;    // a 1×1 hole two deep where the pawns come in
        s.Blocks[(16, 64, 8)] = 31; s.Metas[(16, 64, 8)] = 1;  // tall grass to cut
    });

    [Fact]
    public Task An_empty_handed_pawn_chops_a_tall_tree_or_takes_what_it_can_reach_and_says_so() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Chop);
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(16, 73, 16)], null).Result!["count"]!.GetValue<int>()); // its canopy top
        Assert.Equal(new P3(16, 64, 16), c.Designations.Single().At);
        Assert.True(await Until(() => c.Designations.Count == 0, 150_000),
            "done: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.Fails} {d.Reason}")) + " | " + x.Diag());
        int left = Enumerable.Range(64, 9).Count(y => srv.Blocks.GetValueOrDefault((16, y, 16)) == Blocks.Log);
        Assert.True(srv.Dug.Count(d => d.Id == Blocks.Log && d.X == 16 && d.Z == 16) >= 5, "most of the trunk came down");
        if (left > 0) Assert.Contains(c.Alerts, a => a.Text.Contains("took what it could reach")); // partial: said, not silent
    }, s => { EmptyHanded(s); Tree(s, 16, 16, 9); });

    [Fact]
    public Task Four_pawns_take_four_designated_trees_one_each() => Colony("1.12.2", 4, async x =>
    {
        var c = x.C;
        Only(x, WorkTypes.Chop);
        Assert.Equal(4, c.Designate("add", "chop", Area.Of(0, 60, 15, 20, 75, 17), null, null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => c.Designations.Count == 0, 150_000),
            "done: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.At} {d.Fails} {d.Reason}")) + " | " + x.Diag());
        var byTree = new Dictionary<int, HashSet<string>>();
        foreach (var p in x.Pawns)
            foreach (var d in x.Servers[p].Dug.Where(d => d.Id == Blocks.Log && d.Z == 16))
                (byTree.TryGetValue(d.X, out var set) ? set : byTree[d.X] = new HashSet<string>()).Add(p);
        Assert.Equal(new[] { 1, 6, 11, 16 }, byTree.Keys.Order().ToArray());
        Assert.All(byTree, t => Assert.True(t.Value.Count == 1, $"tree x={t.Key} chopped by {string.Join(", ", t.Value)}"));
        Assert.True(byTree.Values.SelectMany(v => v).Distinct().Count() >= 3, "the work spread over the pawns: " + string.Join("; ", byTree.Select(t => $"{t.Key}: {string.Join(",", t.Value)}")));
    }, s => { foreach (var tx in new[] { 1, 6, 11, 16 }) Tree(s, tx, 16, 4); });

    // ================================================================ live 11-03: idle pawns wander apart; growing zones

    [Fact]
    public Task Three_idle_pawns_wander_apart_and_one_takes_a_designated_tree_at_once() => Colony("1.12.2", 3, async x =>
    {
        var c = x.C;
        Only(x, WorkTypes.Chop);
        // Nothing to do: each wanders the home area on its own cell.
        Assert.True(await Until(() => x.Pawns.All(p => c.CurrentOf(p) is { Kind: "idle", Source: "wander" }), 30_000), "wandering: " + x.Diag());
        var cells = x.Pawns.ToDictionary(p => p, _ => new HashSet<P3>());
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            var spots = c.Spots();
            Assert.Equal(spots.Count, spots.Values.Distinct().Count()); // never two pawns headed to one cell
            foreach (var p in x.Pawns) cells[p].Add(c.Host.Live(p).Block);
            await Task.Delay(250);
        }
        Assert.All(cells, kv => Assert.True(kv.Value.Count >= 2, $"{kv.Key} moved about: {string.Join(" ", kv.Value)}"));
        // A designation: one pawn takes it at once (its leg is cut, not finished), and it is done.
        Assert.Equal(1, c.Designate("add", "chop", Area.Of(10, 60, 6, 14, 72, 10), null, null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => x.Pawns.Any(p => c.CurrentOf(p)?.Kind == "chop"), 5000), "taken at once: " + x.Diag());
        Assert.True(await Until(() => c.Designations.Count == 0, 90_000), "chopped: " + x.Diag());
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed", StringComparison.OrdinalIgnoreCase));
    });

    [Fact]
    public Task A_pawn_sows_a_wheat_zone_and_harvests_and_sows_it_again() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Farm);
        var (res, err) = c.ZoneOp("add", "farm", Area.Of(16, 63, 2, 17, 63, 2), name: "Wheat", grow: new JsonObject { ["plant"] = "wheat" });
        Assert.True(err == null, err);
        var zone = res!["id"]!.GetValue<string>();
        Assert.True(await Until(() => srv.Blocks.GetValueOrDefault((16, 64, 2)) == 59 && srv.Blocks.GetValueOrDefault((17, 64, 2)) == 59, 60_000),
            "sown: " + x.Diag());
        // It grows ripe: the pawn harvests and sows it again.
        foreach (var os in x.Servers.Values.Where(o => o.LoggedIn))
            lock (OneWorld) { os.SetBlock(16, 64, 2, 59, 7); }
        Assert.True(await Until(() => srv.Dug.Any(d => d.Id == 59 && d.X == 16), 60_000), "harvested: " + x.Diag());
        Assert.True(await Until(() => srv.Blocks.GetValueOrDefault((16, 64, 2)) == 59 && srv.Metas.GetValueOrDefault((16, 64, 2)) == 0, 30_000),
            "sown again: " + x.Diag());
        Assert.Contains("sown", c.State()["zones"]!.AsArray().Single(z => z!["id"]!.GetValue<string>() == zone)!["status"]!.GetValue<string>());
    }, s =>
    {
        s.Blocks[(16, 63, 2)] = 60; s.Blocks[(17, 63, 2)] = 60; // farmland
        s.Blocks[(16, 63, 4)] = 9;                               // water near it
        s.Inventory[38] = new ItemStack((short)295, 8, 0);       // wheat seeds
    });

    /// <summary>
    /// Live 11-06 (story-1): "chest at 216,64,400 didn't open (blocked above, or protected?)" ×146: oak planks lay on the nearest seed
    /// chest and every sowing fetched there. The colony sees the block on the lid, takes the seeds from the other chest, and says once
    /// which chest can't open and why; the pawn never walks to the shut one.
    /// </summary>
    [Fact]
    public Task Seeds_come_from_the_chest_that_opens_when_the_nearest_has_a_block_on_its_lid() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Farm);
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Near").Error);
        Assert.Null(c.StorageOp("set", [FoodChest], null, "normal", "Far").Error);
        c.AddContents(c.StorageAt(WoodChest)!, new Stack(295, 0, 16));
        c.AddContents(c.StorageAt(FoodChest)!, new Stack(295, 0, 16));
        var (res, err) = c.ZoneOp("add", "farm", Area.Of(16, 63, 2, 16, 63, 2), name: "Wheat", grow: new JsonObject { ["plant"] = "wheat" });
        Assert.True(err == null, err);
        Assert.True(await Until(() => srv.Blocks.GetValueOrDefault((16, 64, 2)) == 59, 60_000), "sown: " + x.Diag());
        Assert.Equal(16, InChest(srv, WoodChest, i => i.Id == 295)); // the shut chest was never opened
        Assert.True(InChest(srv, FoodChest, i => i.Id == 295) < 16, "the seeds came from the chest that opens");
        lock (x.Log) Assert.DoesNotContain(x.Log, l => l.Contains("didn't open"));
        Assert.Single(c.Alerts, a => a.Text.Contains($"Chest at {WoodChest} can't open"));
    }, s =>
    {
        s.Blocks[(16, 63, 2)] = 60;                                                // farmland
        s.Blocks[(16, 63, 4)] = 9;                                                 // water near it
        s.Blocks[(WoodChest.X, WoodChest.Y + 1, WoodChest.Z)] = Blocks.Planks;     // on the nearer chest's lid
        s.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)][0] = new ItemStack((short)295, 16, 0);
        s.Chests[(FoodChest.X, FoodChest.Y, FoodChest.Z)][0] = new ItemStack((short)295, 16, 0);
    });

    [Fact]
    public Task A_fence_build_has_its_fences_crafted_from_stored_planks_at_the_colony_table_then_builds_them() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        lock (srv.BlockLock) srv.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)][0] = new ItemStack((short)Blocks.Planks, 12, 0);
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Everything").Error);
        c.AddContents(c.StorageAt(WoodChest)!, new Stack(Blocks.Planks, 0, 12)); // what the bot remembers of the chest
        Assert.Null(c.WorkbenchOp("designate", Table).Error); // the colony's table (detection is off)
        Only(x, WorkTypes.Craft, WorkTypes.Build);
        var cells = new JsonArray(new JsonArray(15, 64, 2), new JsonArray(16, 64, 2), new JsonArray(17, 64, 2));
        Assert.Null(c.BuildsOp("add", new JsonArray(new JsonObject { ["kind"] = "fence", ["blocks"] = cells }), null).Error);
        var b = c.Builds.Single();
        Assert.True(await Until(() => b.Status == "done", 150_000),
            $"fence: {b.BlocksDone}/{b.BlocksTotal} {b.Status} {b.Reason} | {string.Join("; ", b.Materials.Select(m => m.Key))} | " + x.Diag());
        foreach (var g in b.Ghosts) Assert.Equal(85, srv.Blocks.GetValueOrDefault((g.At.X, g.At.Y, g.At.Z)));
        Assert.Empty(c.Benches.Single().Tasks); // the craft tasks went with the need
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("short of materials"));
    });

    // ================================================================ tools and drafted combat (docs/web/story-mode.md "Tools and gear")

    [Fact]
    public Task A_drafted_pawn_draws_its_sword_and_kills_the_zombie_it_is_ordered_to_attack() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var pawn = x.Pawns[0];
        var srv = x.Servers[pawn];
        var p = c.Pawns[pawn];
        // Out of the reflex's reach (5 blocks) and of the drafted pawns' own engage radius: only the order sends it there.
        var zombie = srv.SpawnMob(MineClaw.World.MobTypes.Zombie, 16.5, 64, 8.5, health: 8);
        Assert.True(await Until(() => c.Host.World!.Entities().Any(e => e.Id == zombie.Id), 5000), "the colony sees the zombie");
        c.Draft([p], true);
        Assert.True(await Until(() => srv.Held.Id == Items.IronSword, 30_000), "the sword in hand on draft: " + x.Diag());
        var (_, err) = c.GiveOrder([p], new JsonObject { ["kind"] = "attack", ["target"] = new JsonObject { ["entity"] = zombie.Id } }, false);
        Assert.Null(err);
        Assert.True(await Until(() => zombie.Dead, 60_000), "the zombie is dead: " + x.Diag());
        Assert.Contains(srv.MeleeHits, h => h.Target == zombie.Id && h.Damage > 0);
        Assert.True(await Until(() => p.Queue.Count == 0, 10_000), "the order is done");
        c.Draft([p], false);
    }, s => s.Inventory[38] = new ItemStack((short)Items.IronSword, 1, 0));

    [Fact]
    public Task A_pawn_fetches_a_stone_pickaxe_from_storage_before_it_mines_iron_ore() => Colony("1.12.2", 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Assert.Null(c.StorageOp("set", [WoodChest], null, "normal", "Everything").Error);
        c.AddContents(c.StorageAt(WoodChest)!, new Stack(274, 0, 1)); // what the bot remembers of the chest
        Only(x, WorkTypes.Mine);
        Assert.Equal(1, c.Designate("add", "mine", null, [new P3(6, 64, 4)], null).Result!["count"]!.GetValue<int>());
        Assert.True(await Until(() => srv.Blocks.GetValueOrDefault((6, 64, 4)) == 0, 120_000),
            "iron ore mined: " + string.Join(", ", c.Designations.Select(d => $"{d.Kind} {d.Fails} {d.Reason} {d.Wait}")) + " | " + x.Diag());
        Assert.Contains(srv.Dug, d => d.Id == 15 && d.X == 6 && d.Z == 4);
        Assert.True(srv.Inventory.Any(i => !i.IsEmpty && i.Id == 274), "the stone pickaxe came out of the chest");
        // Dug with the right pickaxe, it dropped (only Mine is on, so nobody hauls it: it lies there, or was picked up in passing).
        Assert.True(await Until(() => srv.Inventory.Any(i => !i.IsEmpty && i.Id == 15) || InChest(srv, WoodChest, i => i.Id == 15) > 0
                || c.Host.World!.Entities().Any(e => e.Kind == "item" && e.ItemId == 15), 10_000),
            "the iron ore dropped (dug with the right pickaxe): " + x.Diag());
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed"));
    }, s =>
    {
        EmptyHanded(s);
        s.Blocks[(6, 64, 4)] = 15; // iron ore
        s.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)][0] = new ItemStack(274, 1, 0);
    });

    // ================================================================ quarries (docs/web/story-mode.md "Quarries")

    private const int QuarryX = 14, QuarryZ = 14, QuarrySize = 5, QuarryLayer = 10;

    /// <summary>Step k of the 5×5 quarry at <see cref="QuarryX"/>,<see cref="QuarryZ"/>: its clockwise ring's cell k mod 16 at y 63 − k.</summary>
    private static P3 QuarryStep(int k) =>
        StoryQuarry.Step(StoryQuarry.Ring(ZoneCells.Rect(QuarryX, QuarryZ, QuarryX + QuarrySize - 1, QuarryZ + QuarrySize - 1))!, 63, k);

    /// <summary>
    /// Live 11-04 story-1 (Fox: "The quarry is not being dug any more because the pawns refuse to go into it. They get the idea to do it,
    /// they go to do it, they get almost down the stairs and then cancel that idea"; "dig 181,57,366 failed: can't reach stone" ×14, the
    /// layer held for that cell): a quarry dug 10 layers deep, its layer 10 down to the one cell under stair step 9. The pawn starts at the
    /// surface, walks down the spiral stair and digs it from the floor of the layer (before: it stopped on the step over the cell, "within
    /// reach" with the step between, and gave up).
    /// </summary>
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_pawn_walks_down_a_deep_quarry_stair_and_digs_the_cell_under_a_step(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        var over = QuarryStep(QuarryLayer - 1);
        var target = new P3(over.X, over.Y - 1, over.Z);
        Assert.Equal(Blocks.Stone, srv.Blocks.GetValueOrDefault((target.X, target.Y, target.Z)));
        Only(x, WorkTypes.Mine);
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(QuarryX, 63, QuarryZ, QuarryX + QuarrySize - 1, 63, QuarryZ + QuarrySize - 1));
        Assert.True(err == null, err);
        var zone = c.Zones.Single(z => z.Id == res!["id"]!.GetValue<string>());
        Assert.Equal("quarry", zone.Mode);
        string Failures() { lock (x.Log) return string.Join(" | ", x.Log.Where(l => l.Contains(" failed: ")).TakeLast(6)); }
        Assert.True(await Until(() => srv.Blocks.GetValueOrDefault((target.X, target.Y, target.Z)) == 0, 120_000),
            "the cell under the step is dug: " + Failures() + " | " + x.Diag());
        Assert.Equal(63, zone.QuarryTop);
        for (int k = 0; k <= QuarryLayer; k++) // the stair stands
            Assert.NotEqual(0, srv.Blocks.GetValueOrDefault((QuarryStep(k).X, QuarryStep(k).Y, QuarryStep(k).Z)));
        lock (x.Log) Assert.DoesNotContain(x.Log, l => l.Contains($"dig {target} failed", StringComparison.Ordinal));
    }, s =>
    {
        for (int qx = -4; qx <= 22; qx++)
            for (int qz = -4; qz <= 22; qz++)
                for (int y = 40; y < 58; y++) s.Blocks[(qx, y, qz)] = Blocks.Stone;
        var target = QuarryStep(QuarryLayer - 1).Add(0, -1, 0);
        for (int k = 0; k <= QuarryLayer; k++)
            for (int qx = QuarryX; qx < QuarryX + QuarrySize; qx++)
                for (int qz = QuarryZ; qz < QuarryZ + QuarrySize; qz++)
                {
                    var at = new P3(qx, 63 - k, qz);
                    if (at != QuarryStep(k) && at != target) s.Blocks.Remove((qx, 63 - k, qz));
                }
        s.Inventory[36] = new ItemStack((short)Items.StonePickaxe, 1, 0);
    });

    /// <summary>
    /// Live 11-05 (bot 20261105-011140): "waiting: quarry z17860: 9 cell(s) on y 63 resting after failures", each "won't dig dirt …: it's the
    /// floor of a walkway with the void 1 away (dig only the taught cell)" ×3: the quarry's top-layer edge cells beside its own pit, dug to
    /// bedrock, were taken for a skyblock walkway at the void and the quarry never got past that layer. Here: the 5×5 quarry dug down to
    /// the bedrock at y 39 (its stair standing) but for its west column's grass on y 63; the pawn digs those and the quarry moves on.
    /// </summary>
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_quarry_edge_layer_beside_its_pit_dug_to_bedrock_is_dug_and_the_quarry_moves_on(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Mine);
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(QuarryX, 63, QuarryZ, QuarryX + QuarrySize - 1, 63, QuarryZ + QuarrySize - 1));
        Assert.True(err == null, err);
        var zone = c.Zones.Single(z => z.Id == res!["id"]!.GetValue<string>());
        string Failures() { lock (x.Log) return string.Join(" | ", x.Log.Where(l => l.Contains(" failed: ")).TakeLast(6)); }
        bool EdgeDug() => QuarryEdge().All(e => srv.Blocks.GetValueOrDefault((e.X, e.Y, e.Z)) == 0);
        Assert.True(await Until(() => EdgeDug() && zone.QuarryLayer > 0, 90_000),
            "the edge layer is dug and the quarry moves on: " + Failures() + " | " + x.Diag());
        lock (x.Log) Assert.DoesNotContain(x.Log, l => l.Contains("floor of a walkway", StringComparison.Ordinal));
        for (int k = 0; k < 63 - 39; k++) // the stair stands
            Assert.NotEqual(0, srv.Blocks.GetValueOrDefault((QuarryStep(k).X, QuarryStep(k).Y, QuarryStep(k).Z)));
    }, s =>
    {
        for (int qx = -4; qx <= 22; qx++)
            for (int qz = -4; qz <= 22; qz++)
            {
                s.Blocks[(qx, 39, qz)] = Blocks.Bedrock;
                for (int y = 40; y < 58; y++) s.Blocks[(qx, y, qz)] = Blocks.Stone;
            }
        var edge = QuarryEdge().ToHashSet();
        for (int k = 0; k < 63 - 39; k++)
            for (int qx = QuarryX; qx < QuarryX + QuarrySize; qx++)
                for (int qz = QuarryZ; qz < QuarryZ + QuarrySize; qz++)
                {
                    var at = new P3(qx, 63 - k, qz);
                    if (at != QuarryStep(k) && !edge.Contains(at)) s.Blocks.Remove((qx, 63 - k, qz));
                }
        s.Inventory[36] = new ItemStack((short)Items.StonePickaxe, 1, 0);
        s.Inventory[37] = new ItemStack((short)Items.StoneShovel, 1, 0);
    });

    /// <summary>
    /// Fox 11-05: "the quarry will also need to take into consideration natural caves that it can come into, and lava and other dangerous
    /// things" (docs/web/story-mode.md "Quarry hazards"). The 5×5 quarry dug 10 layers deep; its layer 10 (y 53) is whole. Beside its
    /// east edge a cave tunnel runs at y 53..54 (already open to the dug layer 9) and still lava sits at y 53. The pawn walls the cave's
    /// opening on layer 9 with a block, digs layer 10 but for the cell beside the lava and the cell between the pit and the cave, and
    /// never steps into either; the quarry moves on.
    /// </summary>
    [Theory]
    [InlineData("b1.8.1")]
    [InlineData("1.12.2")]
    public Task A_quarry_walls_off_a_breached_cave_and_leaves_a_seal_by_lava_and_moves_on(string version) => Colony(version, 1, async x =>
    {
        var c = x.C;
        var srv = x.Servers[x.Pawns[0]];
        Only(x, WorkTypes.Mine);
        var (res, err) = c.ZoneOp("add", "mine", Area.Of(QuarryX, 63, QuarryZ, QuarryX + QuarrySize - 1, 63, QuarryZ + QuarrySize - 1));
        Assert.True(err == null, err);
        var zone = c.Zones.Single(z => z.Id == res!["id"]!.GetValue<string>());
        string Failures() { lock (x.Log) return string.Join(" | ", x.Log.Where(l => l.Contains(" failed: ")).TakeLast(6)); }
        Assert.True(await Until(() => zone.QuarryLayer > QuarryLayer, 120_000), "layer 10 is dug and the quarry moves on: " + Failures() + " | " + x.Diag());
        int Id(P3 p) => srv.Blocks.GetValueOrDefault((p.X, p.Y, p.Z));
        Assert.NotEqual(0, Id(HazardSeal)); // the cave's opening on layer 9, walled
        Assert.NotEqual(0, Id(HazardLava.Add(-1, 0, 0))); // the seal by the lava
        Assert.NotEqual(0, Id(HazardCave.Add(-1, 0, 0))); // the wall to the cave
        Assert.Equal(Blocks.Lava, Id(HazardLava));
        for (int qx = QuarryX; qx < QuarryX + QuarrySize; qx++) // the rest of layer 10 is dug
            for (int qz = QuarryZ; qz < QuarryZ + QuarrySize; qz++)
            {
                var at = new P3(qx, 63 - QuarryLayer, qz);
                if (at != QuarryStep(QuarryLayer) && at != HazardLava.Add(-1, 0, 0) && at != HazardCave.Add(-1, 0, 0))
                    Assert.True(Id(at) == 0, $"{at} not dug");
            }
        Assert.DoesNotContain(srv.Accepted, m => m.X >= QuarryX + QuarrySize && m.Y < 58); // never into the cave or the lava
        Assert.DoesNotContain(srv.Dug, d => (d.X, d.Y, d.Z) == (HazardLava.X - 1, HazardLava.Y, HazardLava.Z) || (d.X, d.Y, d.Z) == (HazardCave.X - 1, HazardCave.Y, HazardCave.Z));
        Assert.Contains(c.Alerts, a => a.Text.Contains("breached a cave"));
        Assert.Contains(c.Alerts, a => a.Text.Contains("beside lava"));
    }, s =>
    {
        for (int qx = -4; qx <= 22; qx++)
            for (int qz = -4; qz <= 22; qz++)
                for (int y = 40; y < 58; y++) s.Blocks[(qx, y, qz)] = Blocks.Stone;
        for (int k = 0; k < QuarryLayer; k++)
            for (int qx = QuarryX; qx < QuarryX + QuarrySize; qx++)
                for (int qz = QuarryZ; qz < QuarryZ + QuarrySize; qz++)
                    if (new P3(qx, 63 - k, qz) != QuarryStep(k)) s.Blocks.Remove((qx, 63 - k, qz));
        for (int cx = HazardCave.X; cx <= HazardCave.X + 2; cx++)
            for (int y = HazardCave.Y; y <= HazardCave.Y + 1; y++) s.Blocks.Remove((cx, y, HazardCave.Z));
        s.Blocks[(HazardLava.X, HazardLava.Y, HazardLava.Z)] = Blocks.Lava;
        s.Inventory[36] = new ItemStack((short)Items.StonePickaxe, 1, 0);
    });

    /// <summary>The cave tunnel's first cell beside the quarry's east edge (y 53..54), the lava beside it, the opening walled on layer 9.</summary>
    private static readonly P3 HazardCave = new(QuarryX + QuarrySize, 63 - QuarryLayer, QuarryZ + 3);
    private static readonly P3 HazardLava = new(QuarryX + QuarrySize, 63 - QuarryLayer, QuarryZ + 1);
    private static readonly P3 HazardSeal = new(QuarryX + QuarrySize - 1, 64 - QuarryLayer, QuarryZ + 3);

    /// <summary>The quarry's west column on its top layer (y 63) but for step 0: grass left on the pit's rim.</summary>
    private static IEnumerable<P3> QuarryEdge() =>
        Enumerable.Range(QuarryZ, QuarrySize).Select(z => new P3(QuarryX, 63, z)).Where(p => p != QuarryStep(0));
}
