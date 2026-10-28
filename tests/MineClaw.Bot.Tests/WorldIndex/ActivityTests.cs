using MineClaw.Agent.Events;
using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>World + indexer + tracker + SQLite activity log, all on one manual clock.</summary>
public sealed class ActivityFixture : IDisposable
{
    public IndexFixture F { get; } = new();
    public ManualPlayerEvents Events { get; } = new();
    public ActivityOptions Options { get; } = new();
    public ActivityLog Log { get; }
    public PlayerActivityTracker Tracker { get; }
    public List<GameEvent> Posted { get; } = new();
    public List<ActivityEvent> Recorded { get; } = new();
    private int _nextId = 100;

    public ActivityFixture()
    {
        F.Island(0, 0, 4);
        Log = new ActivityLog(F.Db, F.Rag, Options, F.Clock);
        Tracker = new PlayerActivityTracker(F.World, Events, Log, F.Indexer, Options, "maid_chan", time: F.Clock).Attach();
        Tracker.Wake += Posted.Add;
        Tracker.EventRecorded += Recorded.Add;
        F.Indexer.PumpAll();
    }

    public WorldState World => F.World;
    public int Wakes => Posted.Count(e => e.Wakes);

    public int Player(string name, double x, double y, double z, bool online = true)
    {
        int id = _nextId++;
        World.Entities.SpawnPlayer(id, name, new Vec3d(x, y, z), 0, 0);
        if (online) World.Entities.SetPlayerListEntry(name, true);
        return id;
    }

    public void Look(int id, BlockPos at)
    {
        var e = World.Entities.Get(id)!;
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(e.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0), at.Center);
        World.Entities.Look(id, yaw, pitch);
    }

    public void LookAway(int id, BlockPos from)
    {
        var e = World.Entities.Get(id)!;
        var (yaw, _) = PlayerPhysics.YawPitchTo(e.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0), from.Center);
        World.Entities.Look(id, yaw + 180, -30);
    }

    public void MoveTo(int id, double x, double y, double z)
    {
        var e = World.Entities.Get(id)!;
        World.Entities.Teleport(id, new Vec3d(x, y, z), e.Yaw, e.Pitch);
    }

    /// <summary>Advance time and run both passes.</summary>
    public void Step(double seconds = 0.25)
    {
        F.Clock.Seconds(seconds);
        F.Indexer.PumpAll();
        Tracker.Pump();
    }

    public void Dispose()
    {
        Tracker.Dispose();
        F.Dispose();
    }
}

public class ActivityAttributionTests(ITestOutputHelper output)
{
    private static readonly BlockPos Target = new(3, 64, 0);

    [Fact]
    public void Facing_swinging_player_wins_over_a_bystander()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 1.5, 64, 0.5);
        int robin = a.Player("robin", 1.5, 64, -1.5);
        a.Step();
        a.Look(kiwi, Target);
        a.LookAway(robin, Target);
        a.Events.Swing(kiwi);
        a.World.SetBlock(Target.X, Target.Y, Target.Z, Blocks.Cobblestone, 0);
        a.Step();
        var e = Assert.Single(a.Recorded, r => r.Kind == ActivityKinds.Placed);
        output.WriteLine($"{e.Text} conf {e.Confidence}");
        Assert.Equal("kiwi", e.Player);
        Assert.True(e.Confidence >= a.Options.HighConfidence);
        Assert.Equal("kiwi placed cobblestone on main island", e.Text);
    }

    [Fact]
    public void Two_players_equally_plausible_is_low_confidence_and_stays_out_of_the_digest()
    {
        using var a = new ActivityFixture();
        int kiwi = a.Player("kiwi", 1.5, 64, 0.5);
        int robin = a.Player("robin", 1.5, 64, -0.5);
        a.Step();
        a.Look(kiwi, Target);
        a.Look(robin, Target);
        a.Events.Swing(kiwi);
        a.Events.Swing(robin);
        a.World.SetBlock(Target.X, Target.Y, Target.Z, Blocks.Cobblestone, 0);
        a.Step();
        Assert.All(a.Recorded.Where(r => r.Kind == ActivityKinds.Placed), r => Assert.True(r.Confidence < a.Options.MediumConfidence, $"{r.Player} {r.Confidence}"));
        a.Step(400);
        Assert.DoesNotContain("placed", a.Tracker.Digest.Since(a.F.Clock.GetUtcNow() - TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Physics_is_never_attributed()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.Step();
        int kiwi = a.Player("kiwi", 0.5, 64, 0.5);
        a.Step();
        void Near(BlockPos p) { a.Look(kiwi, p); a.Events.Swing(kiwi); }

        // Water and lava flowing.
        Near(new BlockPos(2, 64, -2)); a.World.SetBlock(2, 64, -2, Blocks.FlowingWater, 1);
        Near(new BlockPos(2, 64, -3)); a.World.SetBlock(2, 64, -3, Blocks.FlowingLava, 2);
        // Gravel falling: unsupported gravel vanishes, lands below with a falling-block entity around.
        a.World.SetBlock(1, 66, 1, Blocks.Gravel, 0);
        a.Step();
        a.Recorded.Clear();
        Near(new BlockPos(1, 66, 1)); a.World.SetBlock(1, 66, 1, 0, 0);
        a.World.Entities.SpawnObject(900, 70, new Vec3d(1.5, 65.5, 1.5));
        Near(new BlockPos(1, 65, 1)); a.World.SetBlock(1, 65, 1, Blocks.Gravel, 0);
        // Cobble regenerating in the generator right after the player mined it.
        a.World.SetBlock(dig.X, dig.Y, dig.Z, 0, 0);
        a.Step();
        a.Recorded.Clear();
        a.Look(kiwi, dig); a.Events.Swing(kiwi);
        a.World.SetBlock(dig.X, dig.Y, dig.Z, Blocks.Cobblestone, 0);
        // Leaves decaying, crops growing, sugar cane growing, grass spreading.
        a.World.SetBlock(-1, 66, -1, Blocks.Leaves, 0);
        a.World.SetBlock(-2, 63, 2, Blocks.Farmland, 0);
        a.World.SetBlock(-2, 64, 2, Blocks.Crops, 1);
        a.World.SetBlock(2, 63, 3, Blocks.Sand, 0);
        a.World.SetBlock(2, 64, 3, Blocks.SugarCane, 0);
        a.Step();
        a.Recorded.Clear();
        Near(new BlockPos(-1, 66, -1)); a.World.SetBlock(-1, 66, -1, 0, 0);
        Near(new BlockPos(-2, 64, 2)); a.World.SetBlock(-2, 64, 2, Blocks.Crops, 5);
        Near(new BlockPos(2, 65, 3)); a.World.SetBlock(2, 65, 3, Blocks.SugarCane, 0);
        Near(new BlockPos(-3, 63, -3)); a.World.SetBlock(-3, 63, -3, Blocks.Dirt, 0);
        a.Step();
        foreach (var r in a.Recorded) output.WriteLine(r.Text);
        Assert.DoesNotContain(a.Recorded, r => r.Kind is ActivityKinds.Placed or ActivityKinds.Broke or ActivityKinds.Harvested or ActivityKinds.Planted);
    }

    [Fact]
    public void Bot_own_actions_are_not_attributed_to_a_bystander()
    {
        using var a = new ActivityFixture();
        // The bot stands right at the block, busy; a player stands farther away, not swinging.
        var t = new PlayerActivityTracker(a.World, new ManualPlayerEvents(), null, a.F.Indexer, a.Options, "maid_chan", botBusy: () => true, time: a.F.Clock).Attach();
        var rec = new List<ActivityEvent>();
        t.EventRecorded += rec.Add;
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(new Vec3d(2.5, 64 + PlayerPhysics.EyeHeight, 0.5), Target.Center);
        a.World.Player.ServerTeleport(new Vec3d(2.5, 64, 0.5), 65.62, yaw, pitch, true);
        int kiwi = a.Player("kiwi", -0.5, 64, 0.5);
        a.Look(kiwi, Target);
        a.World.SetBlock(Target.X, Target.Y, Target.Z, Blocks.Cobblestone, 0);
        t.Pump();
        Assert.DoesNotContain(rec, r => r.Kind == ActivityKinds.Placed);
        t.Dispose();
    }
}

public class ActivityRollupTests(ITestOutputHelper output)
{
    private static void MineGen(ActivityFixture a, int id, BlockPos dig, int times, double every = 10)
    {
        for (int i = 0; i < times; i++)
        {
            a.Look(id, dig);
            a.Events.Swing(id);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, 0, 0);
            a.Step(0.5);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, Blocks.Cobblestone, 0); // regen: physics
            a.Step(every - 0.5);
        }
    }

    [Fact]
    public void Consecutive_mining_rolls_up_into_one_session_with_templated_text()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.Step();
        int kiwi = a.Player("kiwi", 0.5, 64, 3.5);
        a.Step();
        MineGen(a, kiwi, dig, 23);
        var raw = a.Recorded.Where(r => r.Kind == ActivityKinds.Broke).ToList();
        Assert.Equal(23, raw.Sum(r => r.Count));
        Assert.Equal("kiwi mined cobblestone at the cobble gen", raw[0].Text);
        a.Step(200); // session gap → closed
        var rollup = Assert.Single(a.Log.Rollups("kiwi"));
        output.WriteLine(rollup.Text);
        Assert.StartsWith("kiwi mined ~23 cobblestone at the cobble gen (", rollup.Text);
        Assert.Equal(23, a.Log.Events("kiwi", limit: 100).Count(e => e.Kind == ActivityKinds.Broke && e.Rollup == rollup.Id));
        // Only the rollup is an (embedded) episode doc; raw events are FTS-only rows.
        var hits = a.F.Rag.Search(new RagQuery { Text = "kiwi mined cobblestone", Limit = 5 });
        Assert.Contains(hits, h => h.Ref.StartsWith(ActivityLog.DocPrefix + "kiwi:") && h.Kind == "episode");
    }

    [Fact]
    public void Moving_to_another_place_starts_a_new_session()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.F.Set(3, 64, -3, Blocks.Chest);
        a.Step();
        int kiwi = a.Player("kiwi", 0.5, 64, 3.5);
        a.Step();
        MineGen(a, kiwi, dig, 3, 5);
        a.MoveTo(kiwi, 1.5, 64, -2.5);
        a.Step(1);
        a.Look(kiwi, new BlockPos(3, 64, -2));
        a.Events.Swing(kiwi);
        a.World.SetBlock(3, 64, -2, Blocks.Torch, 0);
        a.Step(300);
        var rollups = a.Log.Rollups("kiwi").OrderBy(r => r.Start).ToList();
        foreach (var r in rollups) output.WriteLine(r.Text);
        Assert.Equal(2, rollups.Count);
        Assert.Contains("mined 3 cobblestone at the cobble gen", rollups[0].Text);
        Assert.Contains("placed torch at the chest", rollups[1].Text);
    }

    [Fact]
    public void Templates_are_deterministic()
    {
        var gen = new PlaceRef(IndexKinds.Generator, "1,2,3", "cobble gen", default, default);
        var chest = new PlaceRef(IndexKinds.Chest, "4,64,1", "chest \"storage\"", default, default);
        var island = new PlaceRef(IndexKinds.Island, "0,0", "main island", default, default);
        var t = DateTimeOffset.UnixEpoch;
        Assert.Equal("kiwi mined cobblestone at the cobble gen", new ActivityEvent("kiwi", ActivityKinds.Broke, t, 1) { BlockId = Blocks.Cobblestone, Place = gen }.Text);
        Assert.Equal("poofimafox opened the chest \"storage\"", new ActivityEvent("poofimafox", ActivityKinds.Opened, t, 1) { Place = chest }.Text);
        Assert.Equal("kiwi picked up 3 sugar cane", new ActivityEvent("kiwi", ActivityKinds.PickedUp, t, 1) { ItemId = Items.SugarCane, Count = 3 }.Text);
        Assert.Equal("fox placed sign \"MOB FARM\" on main island", new ActivityEvent("fox", ActivityKinds.WroteSign, t, 1) { Detail = "MOB FARM", Place = island }.Text);
        Assert.Equal("kiwi fought a zombie, 2 hits taken", new ActivityEvent("kiwi", ActivityKinds.Fought, t, 1) { Detail = "zombie", Count = 2 }.Text);
        Assert.Equal("kiwi placed ~30 cobblestone at 12,64,-5",
            new ActivityEvent("kiwi", ActivityKinds.Placed, t, 1) { BlockId = Blocks.Cobblestone, Count = 30, Pos = new BlockPos(12, 64, -5) }.Text);
    }

    [Fact]
    public void Old_rolled_up_raw_events_are_pruned()
    {
        using var a = new ActivityFixture();
        var now = a.F.Clock.GetUtcNow();
        var old = a.Log.Append([
            new ActivityEvent("kiwi", ActivityKinds.Broke, now - TimeSpan.FromDays(10), 0.9) { BlockId = Blocks.Cobblestone },
            new ActivityEvent("kiwi", ActivityKinds.Broke, now - TimeSpan.FromDays(10), 0.9) { BlockId = Blocks.Cobblestone },
            new ActivityEvent("kiwi", ActivityKinds.Chat, now - TimeSpan.FromDays(10), 1) { Detail = "not rolled up" },
            new ActivityEvent("kiwi", ActivityKinds.Chat, now - TimeSpan.FromDays(20), 1) { Detail = "ancient" },
            new ActivityEvent("kiwi", ActivityKinds.Broke, now - TimeSpan.FromHours(1), 0.9) { BlockId = Blocks.Cobblestone },
        ]);
        a.Log.AddRollup(new ActivityRollup("kiwi", now - TimeSpan.FromDays(10), now - TimeSpan.FromDays(10), "kiwi mined 2 cobblestone", 0.9), old.Take(2).Select(e => e.Id));
        Assert.Equal(3, a.Log.Prune(now)); // 2 rolled up + 1 past the hard limit
        Assert.Equal(2, a.Log.Count("kiwi"));
        Assert.Single(a.Log.Rollups("kiwi")); // rollups stay
        Assert.Empty(a.Log.Search("ancient"));
    }

    [Fact]
    public void What_did_robin_do_today_is_answered_from_fts_and_rollups()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.Step();
        int robin = a.Player("robin", 0.5, 64, 3.5);
        int kiwi = a.Player("kiwi", -2.5, 64, -2.5);
        a.Step();
        MineGen(a, robin, dig, 5, 4);
        a.Events.Chat("<robin> brb food");
        a.Step(1);
        var open = a.Log.Ask("what did robin do today", a.F.Clock.GetUtcNow());
        output.WriteLine(open);
        Assert.Contains("robin mined", open); // raw events before the session closes
        a.Step(400);
        var answer = a.Log.Ask("what did robin do today", a.F.Clock.GetUtcNow());
        output.WriteLine(answer);
        Assert.Contains("robin mined 5 cobblestone at the cobble gen", answer);
        Assert.DoesNotContain("kiwi", answer);
        Assert.NotEmpty(a.Log.Search("robin cobblestone"));
        var hits = a.F.Rag.Search(new RagQuery { Text = "what did robin do today", Limit = 5 });
        Assert.Contains(hits, h => h.Ref.StartsWith(ActivityLog.DocPrefix + "robin:"));
        Assert.DoesNotContain("mined", a.Log.Ask("what did kiwi do today", a.F.Clock.GetUtcNow()));
    }
}

public class ActivityDigestTests(ITestOutputHelper output)
{
    [Fact]
    public void Digest_ranks_salient_lines_and_marks_medium_confidence()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.Step();
        var since = a.F.Clock.GetUtcNow();
        int kiwi = a.Player("kiwi", 0.5, 64, 3.5);
        int fox = a.Player("fox", 2.5, 64, -2.5);
        a.Step();
        for (int i = 0; i < 20; i++)
        {
            a.Look(kiwi, dig);
            a.Events.Swing(kiwi);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, 0, 0);
            a.Step(0.5);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, Blocks.Cobblestone, 0);
            a.Step(4);
        }
        var signPos = new BlockPos(3, 64, -3);
        a.Look(fox, signPos);
        a.Events.Swing(fox);
        a.World.SetBlock(signPos.X, signPos.Y, signPos.Z, Blocks.SignPost, 0);
        a.Step();
        a.Events.Sign(signPos, ["MOB FARM", "", "", ""]);
        a.Step();
        // A routine lone pickup by someone far away is noise.
        a.Recorded.Clear();
        var digest = a.Tracker.Digest.Since(since, 500, ["fox"]);
        output.WriteLine(digest);
        Assert.StartsWith("since last: ", digest);
        Assert.Contains("kiwi mined ~20 cobblestone at the cobble gen", digest);
        Assert.Contains("fox placed sign \"MOB FARM\"", digest);
        Assert.True(digest.IndexOf("fox", StringComparison.Ordinal) < digest.IndexOf("kiwi", StringComparison.Ordinal), "focus player first");
        Assert.True(digest.Length <= 500);
        Assert.Equal("", a.Tracker.Digest.Since(a.F.Clock.GetUtcNow() + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Sign_naming_the_bot_wakes_once()
    {
        using var a = new ActivityFixture();
        int fox = a.Player("fox", 2.5, 64, -2.5);
        a.Step();
        var p = new BlockPos(3, 64, -3);
        a.Look(fox, p);
        a.Events.Swing(fox);
        a.World.SetBlock(p.X, p.Y, p.Z, Blocks.SignPost, 0);
        a.Step();
        a.Events.Sign(p, ["hi maid_chan", "", "", ""]);
        a.Step();
        a.Events.Sign(p, ["hi maid_chan", "", "", ""]);
        a.Step();
        Assert.Equal(1, a.Wakes);
        Assert.Contains("naming you", a.Posted[0].Text);
        Assert.Equal(EventKind.Activity, a.Posted[0].Kind);
    }
}

public class ActivityToolAndRecorderTests(ITestOutputHelper output)
{
    [Fact]
    public void Activity_tool_answers_bounded_and_is_registered_for_brain_and_ask_memory()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.Step();
        int robin = a.Player("robin", 0.5, 64, 3.5);
        a.Step();
        for (int i = 0; i < 4; i++)
        {
            a.Look(robin, dig);
            a.Events.Swing(robin);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, 0, 0);
            a.Step(0.5);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, Blocks.Cobblestone, 0);
            a.Step(4);
        }
        a.Step(400);
        var now = a.F.Clock.GetUtcNow();
        var text = ActivityTools.Answer(a.Log, "robin", "today", null, now);
        output.WriteLine(text);
        Assert.Contains("robin mined 4 cobblestone at the cobble gen", text);
        Assert.True(text.Length <= ActivityTools.MaxChars);
        Assert.Contains("robin mined", ActivityTools.Answer(a.Log, "robin", "2h", null, now));
        Assert.DoesNotContain("robin mined", ActivityTools.Answer(a.Log, "robin", "1m", null, now));
        Assert.Contains("robin", ActivityTools.Answer(a.Log, null, null, "cobblestone", now));

        var (h, _) = TestBot.Create();
        Assert.True(h.Runtime.Tools.TryGet(ActivityTools.ToolName, out _));
        Assert.Contains(ActivityTools.ToolName, h.Runtime.Options.AskMemory.Tools);
        h.Dispose();
    }

    [Fact]
    public async Task Demonstration_recorder_uses_tracker_attribution()
    {
        var c = new FakeGameClient();
        c.Floor(64, 6);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var tracker = new PlayerActivityTracker(c.World, new GameClientPlayerEvents(c), null, null, new ActivityOptions(), c.Username, time: clock).Attach();
        var recorder = new Demo.DemonstrationRecorder(c, time: clock) { Activity = tracker, PollInterval = TimeSpan.FromMilliseconds(20) };
        c.World.Entities.SpawnPlayer(201, "kiwi", new Vec3d(0.5, 65, 0.5), 0, 0);
        c.World.Entities.SpawnPlayer(202, "robin", new Vec3d(2.5, 65, 2.5), 0, 0);
        var mine = new BlockPos(1, 64, 0);
        var theirs = new BlockPos(2, 64, 1);
        void Face(int id, BlockPos p)
        {
            var e = c.World.Entities.Get(id)!;
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(e.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0), p.Center);
            c.World.Entities.Look(id, yaw, pitch);
        }
        var session = await recorder.StartAsync("kiwi", null, CancellationToken.None);
        Face(201, mine);
        Face(202, theirs);
        c.RaiseSwing(201);
        c.World.SetBlock(mine.X, mine.Y, mine.Z, 0, 0);   // kiwi digs
        c.RaiseSwing(202);
        c.World.SetBlock(theirs.X, theirs.Y, theirs.Z, 0, 0); // robin digs right next to kiwi, 0.1 s later
        tracker.Pump();
        var steps = await session.StopAsync(CancellationToken.None);
        foreach (var s in steps) output.WriteLine($"{s.Kind} {s.X},{s.Y},{s.Z} {s.Block} {s.Detail}");
        Assert.Contains(steps, s => s.Kind == MineClaw.Agent.Human.DemoActionKind.BlockBroken && s.X == mine.X && s.Z == mine.Z && s.Detail == null);
        Assert.Contains(steps, s => s.Kind == MineClaw.Agent.Human.DemoActionKind.BlockBroken && s.X == theirs.X && s.Z == theirs.Z && s.Detail == "vanished");
        tracker.Dispose();
    }
}
