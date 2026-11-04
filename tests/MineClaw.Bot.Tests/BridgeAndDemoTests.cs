using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Bot.Demo;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public class BridgeTests
{
    [Theory]
    [InlineData(DangerKind.FallingIntoVoid, EventKind.VoidFall, EventPriority.Critical, true)]
    [InlineData(DangerKind.LowHealth, EventKind.LowHealth, EventPriority.High, true)]
    [InlineData(DangerKind.Lava, EventKind.LowHealth, EventPriority.High, true)]
    [InlineData(DangerKind.HostileNearby, EventKind.Notice, EventPriority.Low, true)]
    [InlineData(DangerKind.Hunger, EventKind.Notice, EventPriority.Low, true)]
    [InlineData(DangerKind.Damage, EventKind.Notice, EventPriority.Background, false)]
    public void Danger_maps_to_prioritised_events(DangerKind kind, EventKind expected, EventPriority priority, bool wakes)
    {
        var e = GameBridge.MapDanger(new DangerEvent(kind, "msg"), 6, 12);
        Assert.Equal(expected, e.Kind);
        Assert.Equal(priority, e.Priority);
        Assert.Equal(wakes, e.Wakes);
    }

    [Fact]
    public void Survival_events_escalate_with_severity()
    {
        Assert.Equal(EventPriority.Normal, GameBridge.MapDanger(new DangerEvent(DangerKind.Hunger, "starving: food 5, no food in inventory") { Level = 2 }, 14, 64).Priority);
        // Food 0 wakes the brain but never interrupts a job (below the interrupt priority) until hp reaches the low line.
        Assert.Equal(EventPriority.Normal, GameBridge.MapDanger(new DangerEvent(DangerKind.Hunger, "starving: food 0") { Level = 3 }, 12, 64).Priority);
        Assert.True(GameBridge.MapDanger(new DangerEvent(DangerKind.Hunger, "starving: food 0") { Level = 3 }, 12, 64).Priority < new MineClaw.Agent.AgentOptions().InterruptPriority);
        Assert.Equal(EventPriority.High, GameBridge.MapDanger(new DangerEvent(DangerKind.Hunger, "starving: food 0") { Level = 3 }, 5, 64).Priority);
        var crit = GameBridge.MapDanger(new DangerEvent(DangerKind.LowHealth, "critical hp 3/20 (food 9)") { Critical = true }, 3, 64);
        Assert.Equal(EventPriority.Critical, crit.Priority);
        Assert.Equal("critical hp 3/20 (food 9)", crit.Text); // no duplicated "hp x/20"
    }

    [Fact]
    public void Kick_is_background_context()
    {
        var e = GameBridge.MapKick("Flying is not enabled");
        Assert.False(e.Wakes);
        Assert.Contains("Flying", e.Text);
    }

    [Fact]
    public void Chat_death_and_kick_reach_the_event_queue()
    {
        var (h, c) = TestBot.Create();
        c.RaiseChat("<poofimafox> claudechan what do you see?");
        c.RaiseDied();
        c.RaiseKicked("bye");
        var events = h.Runtime.Events.Snapshot();
        Assert.Contains(events, e => e.Kind == EventKind.Chat && e.Player == "poofimafox" && e.Wakes);
        Assert.Contains(events, e => e.Kind == EventKind.Death && e.Priority == EventPriority.Critical);
        Assert.Contains(events, e => e.Kind == EventKind.Notice && e.Text.StartsWith("kicked") && !e.Wakes);
    }

    [Fact]
    public async Task Runtime_chat_goes_out_through_the_client()
    {
        var (h, c) = TestBot.Create();
        await h.Runtime.Chat.SayAsync("hi fox");
        Assert.Equal(["hi fox"], c.Said);
    }

    [Fact]
    public async Task Player_list_join_is_posted_once_even_with_a_chat_join_line()
    {
        var c = new FakeGameClient();
        var rt = AgentRuntime.Create(TestBot.Options(), (_, _) => Task.CompletedTask, new NoLlm(), persist: false);
        var bridge = new GameBridge(c, rt) { JoinDedupeDelay = TimeSpan.FromMilliseconds(50) };
        bridge.Attach();

        c.RaiseJoined("kiwi");                     // player list first...
        rt.Chat.OnServerChat("kiwi joined the game."); // ...then the chat line
        c.RaiseJoined("poofimafox");               // player list only
        await Task.Delay(300);
        var joins = rt.Events.Snapshot().Where(e => e.Kind == EventKind.PlayerJoined).ToList();
        Assert.Single(joins, e => e.Player == "kiwi");
        Assert.Equal(1, joins.Single(e => e.Player == "kiwi").Count);
        Assert.Single(joins, e => e.Player == "poofimafox");
    }

    [Fact]
    public void State_header_is_one_short_line()
    {
        var (h, c) = TestBot.Create();
        c.Floor();
        c.Give(36, Blocks.Cobblestone, 12);
        c.CurrentAction = "goto";
        var s = h.Runtime.Agent.StateHeader!();
        Assert.Equal("pos 0,65,0 hp 20/20 food 20 held cobblestone x12 day | doing goto", s);
        c.World.Player.SetHealth(14, 9, 0);
        Assert.StartsWith("pos 0,65,0 hp 14/20 food 9 (no food!) held", h.Runtime.Agent.StateHeader!());
        c.IsPlaying = false;
        Assert.Equal("offline (reconnecting)", h.Runtime.Agent.StateHeader!());
    }
}

public class DemoTests
{
    private static DemoAction A(DemoActionKind k, int x, int y, int z, string? block = null, string? detail = null, int sec = 0) =>
        new(DateTimeOffset.UnixEpoch.AddSeconds(sec), k, x, y, z, block, detail);

    [Fact]
    public void Summary_matches_the_teaching_style()
    {
        var steps = DemoSummary.Summarize(
        [
            A(DemoActionKind.Move, 2, 65, -2),
            A(DemoActionKind.Move, 3, 65, -2, detail: "facing lava"),
            A(DemoActionKind.UseBlock, 4, 64, -2, "lava", "water bucket"),
            A(DemoActionKind.BlockPlaced, 5, 64, -2, "cobblestone", "appeared"),
            A(DemoActionKind.BlockBroken, 5, 64, -2, "cobblestone"),
            A(DemoActionKind.BlockPlaced, 5, 64, -2, "cobblestone", "appeared"),
            A(DemoActionKind.BlockBroken, 5, 64, -2, "cobblestone"),
            A(DemoActionKind.OpenContainer, 6, 65, -2, "chest"),
        ]);
        Assert.Equal(
        [
            "stood at 3,65,-2 facing lava",
            "right-clicked water bucket on lava at 4,64,-2",
            "cobblestone appeared at 5,64,-2",
            "broke cobblestone at 5,64,-2",
            "cobblestone appeared at 5,64,-2",
            "broke cobblestone at 5,64,-2",
            "opened chest at 6,65,-2",
        ], steps);
    }

    [Fact]
    public void Summary_words_every_action_kind()
    {
        var steps = DemoSummary.Summarize(
        [
            A(DemoActionKind.Move, 0, 64, 0),
            A(DemoActionKind.UseBlock, 1, 64, 0, "stone", MineClaw.Bot.Perception.Gestures.Punched),
            A(DemoActionKind.Chat, 0, 0, 0, detail: "hi"),
            A(DemoActionKind.UseBlock, 1, 64, 1),
            A(DemoActionKind.BlockPlaced, 2, 64, 0, "water", "poured"),
            A(DemoActionKind.BlockPlaced, 2, 64, 1, "dirt"),
            A(DemoActionKind.BlockBroken, 3, 64, 0, "sand", "vanished"),
            A(DemoActionKind.Move, 0, 64, 0),
            A(DemoActionKind.BlockBroken, 3, 64, 1, "water", "scooped"),
            A(DemoActionKind.BlockBroken, 3, 64, 2, "dirt"),
            A(DemoActionKind.ClickSlot, 0, 0, 0),
            A(DemoActionKind.ClickSlot, 0, 0, 0, detail: "took 12 cobblestone"),
            A(DemoActionKind.Move, 9, 64, 9),
        ]);
        Assert.Equal(
        [
            "stood at 0,64,0",
            "punched stone at 1,64,0",
            "used block at 1,64,1",
            "poured water at 2,64,0",
            "placed dirt at 2,64,1",
            "sand vanished at 3,64,0",
            "scooped water at 3,64,1",
            "broke dirt at 3,64,2",
            "clicked a slot",
            "took 12 cobblestone",
        ], steps);
        Assert.Equal(["stood at 4,5,6 facing x"],
            DemoSummary.Summarize([A(DemoActionKind.Move, 1, 2, 3), A(DemoActionKind.Move, 4, 5, 6, detail: "facing x")]));
        Assert.Equal(["stood at -1,+0,+2"], DemoSummary.Summarize([A(DemoActionKind.Move, 4, 64, 7)], origin: new MineClaw.World.BlockPos(5, 64, 5)));
        Assert.Empty(DemoSummary.Summarize([A(DemoActionKind.Chat, 0, 0, 0, detail: "hi")]));
    }

    [Fact]
    public void Summary_merges_repeats_uses_relative_coords_and_caps()
    {
        var acts = Enumerable.Range(0, 3).Select(_ => A(DemoActionKind.UseBlock, 1, 64, 1, "lever")).ToList();
        var steps = DemoSummary.Summarize(acts, origin: new MineClaw.World.BlockPos(0, 64, 0));
        Assert.Equal(["used lever at +1,+0,+1 (x3)"], steps);

        var many = Enumerable.Range(0, 30).Select(i => A(DemoActionKind.BlockPlaced, i, 64, 0, "dirt")).ToList();
        var capped = DemoSummary.Summarize(many, maxSteps: 5);
        Assert.Equal(5, capped.Count);
        Assert.StartsWith("...(", capped[^1]);
    }

    [Fact]
    public async Task Recorder_captures_stand_swings_block_changes_and_containers()
    {
        var c = new FakeGameClient();
        var w = c.World;
        for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(5, 64, 0, Blocks.Lava, 0);
        w.SetBlock(6, 64, 2, Blocks.Chest, 0);
        var rec = new DemonstrationRecorder(c) { PollInterval = TimeSpan.FromMilliseconds(20) };
        // Teacher stands at 3,64,0 looking east (+X, yaw -90) and slightly down at the lava.
        var e = w.Entities.SpawnPlayer(42, "kiwi", new Vec3d(3.5, 64, 0.5), -90, 30, Items.WaterBucket);

        var session = await rec.StartAsync("kiwi", null, CancellationToken.None);
        await Task.Delay(80);
        c.RaiseSwing(42);                                   // right-click bucket at the lava
        w.SetBlock(4, 64, 1, Blocks.Water, 0);              // water source poured
        w.SetBlock(4, 64, 2, Blocks.Water, 3);              // flow: ignored
        w.SetBlock(5, 64, 1, Blocks.Cobblestone, 0);        // lava + water → cobble: the world, not the player
        w.Entities.Teleport(42, new Vec3d(5.5, 64, 2.5), 0, 0);
        await Task.Delay(80);
        c.RaiseBlockAction(new MineClaw.World.BlockPos(6, 64, 2), 1, 1); // chest lid opens
        var actions = await session.StopAsync(CancellationToken.None);
        await session.DisposeAsync();

        var steps = DemoSummary.Summarize(actions);
        Assert.Equal("stood at 3,64,0 facing lava", steps[0]);
        Assert.Equal("right-clicked water bucket on lava at 5,64,0", steps[1]);
        Assert.Equal("poured water at 4,64,1", steps[2]);
        Assert.Equal("cobblestone appeared at 5,64,1", steps[3]);
        Assert.Contains("opened chest at 6,64,2", steps);
        Assert.DoesNotContain(steps, s => s.Contains("4,64,2"));
        _ = e;
    }

    [Fact]
    public async Task Digging_swings_collapse_into_the_break()
    {
        var c = new FakeGameClient();
        var w = c.World;
        w.SetBlock(4, 64, 0, Blocks.Cobblestone, 0);
        var rec = new DemonstrationRecorder(c) { PollInterval = TimeSpan.FromMilliseconds(20) };
        w.Entities.SpawnPlayer(7, "poofimafox", new Vec3d(2.5, 64, 0.5), -90, 20, Items.StonePickaxe);
        await using var session = await rec.StartAsync("poofimafox", null, CancellationToken.None);
        for (int i = 0; i < 4; i++) c.RaiseSwing(7);
        w.SetBlock(4, 64, 0, 0, 0);
        var steps = DemoSummary.Summarize(await session.StopAsync(CancellationToken.None));
        Assert.Equal(["stood at 2,64,0 facing cobblestone", "broke cobblestone at 4,64,0"], steps);
    }

    [Fact]
    public async Task Ask_stand_confirms_by_position()
    {
        var (h, c) = TestBot.Create();
        c.World.Entities.SpawnPlayer(9, "kiwi", new Vec3d(0.5, 64, 0.5), 0, 0);
        var run = h.Runtime.Tools.ExecuteAsync("ask_stand", TestBot.J(new { player = "kiwi", at = "4,64,4" }), CancellationToken.None);
        await Task.Delay(200);
        Assert.False(run.IsCompleted);
        c.World.Entities.Teleport(9, new Vec3d(4.5, 64, 4.4), 0, 0);
        var r = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(r.Ok, r.Text);
        Assert.Equal("kiwi is at 4,64,4", r.Text);
        Assert.Contains(c.Said, s => s.Contains("4,64,4"));
    }

    [Fact]
    public async Task Ask_stand_without_target_reports_where_they_stand()
    {
        var (h, c) = TestBot.Create();
        c.World.Entities.SpawnPlayer(9, "kiwi", new Vec3d(2.5, 64, -1.5), 0, 0);
        var run = h.Runtime.Tools.ExecuteAsync("ask_stand", TestBot.J(new { player = "kiwi" }), CancellationToken.None);
        await Task.Delay(100);
        h.Runtime.Chat.OnServerChat("<kiwi> here");
        var r = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("kiwi stands at 2,64,-2", r.Text);
    }
}
