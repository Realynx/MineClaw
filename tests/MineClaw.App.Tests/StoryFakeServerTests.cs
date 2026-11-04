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

    private static ClientOptions Fast(ClientOptions o, FakeServer s) => o with
    {
        TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
        ConnectionFactory = s.CreateConnection, HostileAlertRadius = 0, AutoReconnect = false,
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
        // It chops, then carries the logs to the Wood chest (the director's record, kept after each deposit; the 1.12.2 fake
        // server keeps the chest's slots too).
        Assert.True(await Until(() => c.StorageAt(WoodChest)!.Contents.Any(s => s.Id == Blocks.Log), 90_000), "logs stored: " + x.Diag());
        var srv = x.Servers[pawn];
        if (x.Modern) Assert.True(await Until(() => InChest(srv, WoodChest, i => i.Id == Blocks.Log) >= 1, 5000), "logs in the server's chest");
        Assert.Empty(c.StorageAt(FoodChest)!.Contents); // the Food chest takes no wood
        Assert.Equal(0, InChest(srv, FoodChest, _ => true));
        Assert.Contains(srv.Dug, d => d.Id == Blocks.Log && d.X == 12 && d.Z == 8);
        Assert.Empty(c.Designations); // the tree's job is done
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("No storage accepts Cobblestone")); // kept, never dropped
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
        Assert.True(await Until(() => x.Pawns.All(p => c.CurrentOf(p)?.Label == "Idle (wandering)"), 30_000), "wandering: " + x.Diag());
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
        Assert.Empty(c.Benches.Single().Bills); // the auto bills went with the need
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
        Assert.True(await Until(() => srv.Inventory.Any(i => !i.IsEmpty && i.Id == 15) || InChest(srv, WoodChest, i => i.Id == 15) > 0, 10_000),
            "the iron ore dropped and was picked up (dug with the right pickaxe)");
        Assert.DoesNotContain(c.Alerts, a => a.Text.Contains("failed"));
    }, s =>
    {
        EmptyHanded(s);
        s.Blocks[(6, 64, 4)] = 15; // iron ore
        s.Chests[(WoodChest.X, WoodChest.Y, WoodChest.Z)][0] = new ItemStack(274, 1, 0);
    });
}
