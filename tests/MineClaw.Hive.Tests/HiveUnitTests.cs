using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

public class ClaimBoardTests
{
    [Fact]
    public void Conflicts_leases_renewal_expiry_and_release_all()
    {
        var t = new ManualTime();
        var b = new ClaimBoard(TimeSpan.FromSeconds(60), t);
        var blk = ClaimKey.Block(1, 64, 1);
        Assert.True(b.TryClaim("maid_chan_67", blk).Ok);
        // Another clone can't mine the same block, nor claim an area containing it.
        var r = b.TryClaim("maid_chan_69", blk);
        Assert.False(r.Ok);
        Assert.Equal("maid_chan_67", r.Holder);
        Assert.False(b.TryClaim("maid_chan_69", ClaimKey.Area(new BlockPos(0, 60, 0), new BlockPos(3, 70, 3))).Ok);
        Assert.True(b.TryClaim("maid_chan_69", ClaimKey.Area(new BlockPos(5, 60, 5), new BlockPos(8, 70, 8))).Ok);
        Assert.False(b.TryClaim("maid_chan_67", ClaimKey.Block(6, 65, 6)).Ok); // inside 69's area
        Assert.False(b.IsFree("maid_chan_67", ClaimKey.Block(6, 65, 6)));
        Assert.True(b.IsFree("maid_chan_69", ClaimKey.Block(6, 65, 6)));        // own area

        // Chests: one bot at a time; the holder renews; chests don't conflict with block/area claims.
        var chest = ClaimKey.Chest(new BlockPos(0, 64, 0));
        Assert.True(b.TryClaim("maid_chan", chest).Ok);
        Assert.False(b.TryClaim("maid_chan_67", chest).Ok);
        t.Now += TimeSpan.FromSeconds(50);
        Assert.True(b.TryClaim("maid_chan", chest, TimeSpan.FromSeconds(60)).Ok); // renewed
        t.Now += TimeSpan.FromSeconds(30);
        Assert.False(b.TryClaim("maid_chan_67", chest).Ok);                         // still held (renewed)
        t.Now += TimeSpan.FromSeconds(31);
        Assert.True(b.TryClaim("maid_chan_67", chest).Ok);                          // expired → free

        Assert.True(b.TryClaim("maid_chan_69", ClaimKey.Task("cobble")).Ok);
        Assert.False(b.TryClaim("maid_chan_67", ClaimKey.Task(" Cobble ")).Ok);
        Assert.Equal(1, b.ReleaseAll("maid_chan_69")); // its area lease already expired (111 s > 60 s TTL); the task goes now
        Assert.True(b.TryClaim("maid_chan_67", ClaimKey.Task("cobble")).Ok);
    }

    [Fact]
    public void Keys_round_trip()
    {
        foreach (var k in new[] { ClaimKey.Block(-3, 64, 7), ClaimKey.Area(new BlockPos(4, 1, 4), new BlockPos(-2, 9, 0)), ClaimKey.Chest(new BlockPos(1, 2, 3)),
                     ClaimKey.Furnace(new BlockPos(0, 0, 0)), ClaimKey.Task("smelt iron") })
        {
            Assert.True(ClaimKey.TryParse(k.Id, out var back));
            Assert.Equal(k, back);
        }
        Assert.False(ClaimKey.TryParse("block:1,2", out _));
    }
}

public class ChatArbiterTests
{
    private static readonly string[] Clones = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private sealed class Bot
    {
        public required string Name;
        public required ChatArbiter Arbiter;
        public required ChatRouter Router;
        public required EventQueue Events;
        public int Seen;
        public int TakeAddressed()
        {
            var all = Events.Snapshot();
            var n = all.Skip(Seen).Count(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && e.Priority >= EventPriority.Normal);
            Seen = all.Count;
            return n;
        }
    }

    private static Bot Make(string name, ChatArbiter a, string display)
    {
        var q = new EventQueue(new EventQueueOptions { DedupeWindowSeconds = 0 });
        var r = new ChatRouter(new ChatOptions { Username = name, DisplayName = display, Aliases = name == "maid_chan" ? ["claude chan"] : [], LineDelayMs = 0 }, q,
            (_, _) => Task.CompletedTask)
        { IsHiveBot = a.IsHiveBot, Arbiter = a.Decide };
        r.SetOnlinePlayers(["poofimafox", "kiwi", .. Clones, "maid_chan"]);
        return new Bot { Name = name, Arbiter = a, Router = r, Events = q };
    }

    [Fact]
    public void Exactly_one_bot_replies_and_the_addressed_clone_answers()
    {
        var main = new ChatArbiter("maid_chan", "maid_chan", Clones, ["maid-chan", "claude chan", "maid_chan"]);
        main.SetLive(["maid_chan_67", "maid_chan_69"]);
        var bots = new List<Bot> { Make("maid_chan", main, "maid-chan") };
        foreach (var c in new[] { "maid_chan_67", "maid_chan_69" })
        {
            var a = new ChatArbiter(c, "main", Clones);
            a.Apply(main.ToRoster());
            bots.Add(Make(c, a, "maid-chan " + ChatArbiter.NumberOf(c)));
        }

        string? Said(string line)
        {
            foreach (var b in bots) b.Router.OnServerChat(line);
            foreach (var b in bots.Skip(1)) b.Arbiter.Apply(main.ToRoster()); // main's roster push
            var who = bots.Where(b => b.TakeAddressed() > 0).Select(b => b.Name).ToList();
            Assert.True(who.Count <= 1, $"'{line}' answered by {string.Join(", ", who)}");
            return who.SingleOrDefault();
        }

        Assert.Equal("maid_chan", Said("<poofimafox> maid chan, how many of you are there?"));
        Assert.Equal("maid_chan_67", Said("<poofimafox> 67, come here"));
        Assert.Equal("maid_chan_67", Said("<poofimafox> thanks!"));                 // sticky follow-up
        Assert.Equal("maid_chan_69", Said("<kiwi> maid chan 69 can you mine some cobble"));
        Assert.Equal("maid_chan_69", Said("<kiwi> #69 actually make it 20"));
        Assert.Equal("maid_chan", Said("<poofimafox> maid chan 420 are you there?")); // 420 isn't out: main answers
        Assert.Null(Said("<kiwi> poofimafox look at this"));                        // to another player: nobody
        Assert.Equal("maid_chan_67", Said("* poofimafox pats 67"));
        // Hive-internal: a clone's own chat and /me are never treated as a player's.
        Assert.Null(Said("<maid_chan_67> on my way"));
        Assert.Null(Said("* maid_chan_69 waves at poofimafox"));
        Assert.Null(Said("<maid_chan> 67, come back"));
        Assert.Equal("maid_chan", Said("<poofimafox> claude chan what's up"));       // explicit main name ends 67's sticky? no: main named
    }

    [Fact]
    public void Clone_joins_are_not_player_events()
    {
        var a = new ChatArbiter("maid_chan", "maid_chan", Clones);
        var b = Make("maid_chan", a, "maid-chan");
        b.Router.OnServerChat("maid_chan_67 joined the game.");
        b.Router.OnServerChat("kiwi joined the game.");
        var e = b.Events.Snapshot();
        Assert.Single(e, x => x.Kind == EventKind.PlayerJoined);
        Assert.Equal("kiwi", e.Single(x => x.Kind == EventKind.PlayerJoined).Player);
        Assert.DoesNotContain("maid_chan_67", b.Router.OnlinePlayers);
    }
}

public class BudgetLedgerTests
{
    private static (BudgetLedger L, Func<decimal> Main, Action<decimal> SetMain) Make(HiveBudgetConfig? o = null)
    {
        decimal main = 0;
        var l = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190, WorkerBrainFraction = 0.6, CheapOnlyFraction = 0.8 },
            o ?? new HiveBudgetConfig { CloneDailyCapUsd = 1.0m, GrantUsd = 0.25m, RecallAtFraction = 0.5 }, () => (main, main));
        return (l, () => main, v => main = v);
    }

    [Fact]
    public void Grants_in_chunks_enforces_sub_caps_and_the_recall_line()
    {
        var (l, _, setMain) = Make();
        var g = l.Authorize("maid_chan_67", 0.04m);
        Assert.True(g.Ok);
        Assert.Equal(0.25m, g.GrantUsd);                    // a batch, not per call
        for (int i = 0; i < 3; i++) l.Record("maid_chan_67", 0.25m);
        Assert.Equal(0.75m, l.CloneSpend("maid_chan_67").Today);
        Assert.True(l.Authorize("maid_chan_67", 0.04m) is { Ok: true, GrantUsd: <= 0.25m }); // clipped to the sub-cap
        l.Record("maid_chan_67", 0.25m);
        var refused = l.Authorize("maid_chan_67", 0.04m);
        Assert.False(refused.Ok);
        Assert.Contains("sub-cap", refused.Reason);
        Assert.True(l.Authorize("maid_chan_69", 0.04m).Ok);  // other clones have their own sub-cap

        setMain(4.9m);                                        // hive total ≈ 5.15 + outstanding ≥ 0.5 × $10
        Assert.False(l.Authorize("maid_chan_420", 0.04m).Ok);
        Assert.True(l.AtRecallLine(out _));
    }

    [Fact]
    public void Main_tracker_sees_hive_spend()
    {
        var (l, _, _) = Make(new HiveBudgetConfig { CloneDailyCapUsd = 100, RecallAtFraction = 1 });
        var usage = new UsageTracker(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190, WorkerBrainFraction = 0.6, CheapOnlyFraction = 0.8 }, path: null) { ExternalSpend = l.ClonesSpend };
        Assert.Equal(BudgetMode.Normal, usage.CheckMode());
        l.Record("maid_chan_67", 6.5m);
        Assert.Equal(BudgetMode.Economy, usage.CheckMode()); // clones' spend counts toward main's caps
        l.Record("maid_chan_69", 2m);
        Assert.Equal(BudgetMode.CheapOnly, usage.CheckMode());
    }
}

public class SlotPolicyTests
{
    private static readonly string[] Regulars = ["poofimafox", "kiwi"];

    [Fact]
    public void Keeps_a_slot_per_offline_regular()
    {
        // 7 slots, main only online, both regulars offline: 6 free, 2 reserved → up to 4 clones.
        var online = new List<string> { "maid_chan" };
        for (int i = 0; i < 4; i++)
        {
            Assert.True(SlotPolicy.CanSpawn(new SlotFacts(7, online), Regulars).Ok, $"clone {i + 1}");
            online.Add("clone" + i);
        }
        var no = SlotPolicy.CanSpawn(new SlotFacts(7, online), Regulars);
        Assert.False(no.Ok);
        Assert.Equal(2, no.Reserved);
        // Fox comes online: one reservation released, but the server is still at 6/7 with Robin's slot kept.
        online.Add("poofimafox");
        Assert.False(SlotPolicy.CanSpawn(new SlotFacts(7, online), Regulars).Ok);
        // Unknown max players → fallback 7; launching clones count as pending.
        Assert.False(SlotPolicy.CanSpawn(new SlotFacts(0, ["maid_chan", "poofimafox"]), Regulars, pending: 4).Ok);
        Assert.True(SlotPolicy.CanSpawn(new SlotFacts(0, ["maid_chan", "poofimafox"]), Regulars, pending: 3).Ok);
    }

    [Fact]
    public void Furthest_from_finishing_yields_first()
    {
        var t0 = DateTimeOffset.UtcNow;
        var running = new[]
        {
            new CloneProgress("maid_chan_67", 0.8, t0),
            new CloneProgress("maid_chan_69", 0.1, t0),
            new CloneProgress("maid_chan_420", 0.1, t0.AddMinutes(1)),
        };
        // A stranger filled the server: 7/7 with kiwi offline → one clone must go: the newest of the least done.
        var online = new[] { "maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "poofimafox", "stranger1", "stranger2" };
        Assert.Equal(["maid_chan_420"], SlotPolicy.MustYield(new SlotFacts(7, online), Regulars, running));
        Assert.Empty(SlotPolicy.MustYield(new SlotFacts(7, online[..6]), Regulars, running));
        Assert.Equal(["maid_chan_420", "maid_chan_69", "maid_chan_67"], SlotPolicy.YieldOrder(running).Select(c => c.Name));
    }
}

public class SharedDbTests
{
    [Fact]
    public void Rows_are_tagged_with_the_writing_bot_and_other_processes_writes_are_noticed()
    {
        var path = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"), "m.db");
        using var a = AgentDb.Open(path);
        using var b = AgentDb.Open(path); // a second connection = another process's
        _ = new HiveStore(a, "maid_chan");
        _ = new HiveStore(b, "maid_chan_67");
        a.Exec("INSERT INTO notes(subject, text, created) VALUES ('x', 'from main', 1)");
        b.Exec("INSERT INTO notes(subject, text, created) VALUES ('x', 'from 67', 1)");
        b.Exec("INSERT INTO chat_log(at, kind, player, text) VALUES (1, 'chat', 'kiwi', 'hi')");
        Assert.Equal("maid_chan", a.Scalar("SELECT clone_id FROM notes WHERE text = 'from main'"));
        Assert.Equal("maid_chan_67", a.Scalar("SELECT clone_id FROM notes WHERE text = 'from 67'"));
        Assert.Equal("maid_chan_67", a.Scalar("SELECT clone_id FROM chat_log WHERE text = 'hi'"));

        int changes = 0;
        using var w = new DataVersionWatcher(a, () => changes++, TimeSpan.FromHours(1));
        Assert.False(w.Tick());
        a.Exec("INSERT INTO notes(subject, text, created) VALUES ('x', 'own write', 1)");
        Assert.False(w.Tick()); // own commits don't count
        b.Exec("INSERT INTO notes(subject, text, created) VALUES ('x', 'other write', 1)");
        Assert.True(w.Tick());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Stranded_inventory_and_once_only_events()
    {
        using var db = AgentDb.Open(null);
        var s = new HiveStore(db, "maid_chan");
        s.SaveInventory("maid_chan_67", [new ItemMsg(4, 0, 20, "cobblestone"), new ItemMsg(17, 0, 3, "log")]);
        Assert.Null(s.Stranded("maid_chan_67"));
        Assert.Equal(23, s.MarkStranded("maid_chan_67", "killed"));
        Assert.Equal(23, s.Stranded("maid_chan_67")!.Count);
        s.ClearStranded("maid_chan_67");
        Assert.Null(s.Stranded("maid_chan_67"));
        Assert.True(s.RecordOnce("death", "death:maid_chan_67:1", "maid_chan_67", "fell"));
        Assert.False(s.RecordOnce("death", "death:maid_chan_67:1", "maid_chan_67", "fell"));
        Assert.Equal(1, s.CountEvents("death", "maid_chan_67"));
    }
}

public class FrameTests
{
    [Fact]
    public async Task Frames_round_trip_and_eof_is_null()
    {
        var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, new HiveMessage { T = Msg.Claim, Id = 7, D = HiveJson.ToElement(new ClaimMsg("block:1,2,3", 30)) }, default);
        ms.Position = 0;
        var m = await FrameCodec.ReadAsync(ms, default);
        Assert.Equal(Msg.Claim, m!.T);
        Assert.Equal(7, m.Id);
        Assert.Equal("block:1,2,3", m.As<ClaimMsg>()!.Key);
        Assert.Null(await FrameCodec.ReadAsync(ms, default));
    }
}
