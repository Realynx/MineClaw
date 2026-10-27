using MineClaw.Agent;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// The brainstem on a bot host: instant head turn, reflex commands, the [here] block, speculative first steps (adopted,
/// cancelled, never unsafe), social idle (never across an edge) and the priority stack.
/// </summary>
public class BrainstemBotTests
{
    private static (BotHost H, FakeGameClient C) Bot(Action<AgentOptions>? configure = null)
    {
        var c = new FakeGameClient();
        var o = TestBot.Options();
        o.Self.TrustedPlayers = ["kiwi"];
        configure?.Invoke(o);
        var h = BotHost.Create(c, o, null, new NoLlm(), persist: false);
        c.Floor(64, 10);
        return (h, c);
    }

    private static async Task Until(Func<bool> cond, int ms = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Addressed_chat_turns_the_head_to_the_speaker_at_once_with_a_nod()
    {
        var (_, c) = Bot();
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-3.5, 65, 0.5), 0, 0);
        c.RaiseChat("<kiwi> claudechan how's it going?");
        // Synchronously, before any debounce or model call: the next tick sends this look.
        Assert.True(c.Looks.TryPeek(out var look));
        Assert.Equal(-3.5, look.X, 3);
        Assert.Equal(65 + PlayerPhysics.EyeHeight, look.Y, 3);
        Assert.Equal(1, c.Swings);
        Assert.Empty(c.Said); // no auto text: the persona speaks through the brain
    }

    [Fact]
    public async Task Stop_reflex_cancels_the_running_job_instantly_and_tells_the_brain()
    {
        var (h, c) = Bot();
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-3.5, 65, 0.5), 0, 0);
        var gate = new TaskCompletionSource();
        c.GotoGate = ct => gate.Task.WaitAsync(ct);
        h.Runtime.Tools.TryGet("goto", out var go);
        _ = h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "5,65,5" }))], TimeSpan.Zero, default);
        await Until(() => h.Runtime.Activities.IsBusy && c.CurrentAction == "goto");
        c.RaiseChat("<kiwi> claudechan stop!");
        await Until(() => !h.Runtime.Activities.IsBusy);
        var batch = h.Runtime.Events.TryTakeBatch() ?? new EventBatch([GameEvent.Chat("kiwi", "claudechan stop!")]);
        Assert.StartsWith("[reflex] stopped goto", h.Brainstem!.TakeNotes(batch));
    }

    [Fact]
    public async Task Come_and_follow_reflexes_walk_to_the_speaker_without_parkour_or_building()
    {
        var (h, c) = Bot();
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-6.5, 65, 0.5), 0, 0);
        c.RaiseChat("<kiwi> claudechan come here");
        await Until(() => !c.Gotos.IsEmpty);
        var (goal, options) = c.Gotos.First();
        Assert.IsType<GoalNearPosition>(goal);
        Assert.Equal(ParkourMode.Off, options!.Parkour);
        Assert.False(options.AllowBreak);
        Assert.False(options.AllowPlace);
        c.RaiseChat("<kiwi> claudechan follow me");
        await Until(() => c.CallList.Any(x => x.StartsWith("follow kiwi")));
    }

    [Fact]
    public async Task Give_reflex_runs_only_for_trusted_players_and_held_items()
    {
        var (h, c) = Bot();
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-3.5, 65, 0.5), 0, 0);
        c.World.Entities.SpawnPlayer(8, "robin", new Vec3d(-3.5, 65, 2.5), 0, 0);
        c.Give(36, Blocks.Cobblestone, 10);
        var bs = h.Brainstem!;
        Assert.Null(bs.TryReflex("robin", "give me 5 cobblestone"));       // not trusted: the brain decides
        Assert.Null(bs.TryReflex("kiwi", "give me 5 diamonds"));            // not held
        Assert.Null(bs.TryReflex("kiwi", "give me a hand"));                // not an item
        var r = bs.TryReflex("kiwi", "give me 5 cobblestone");
        Assert.NotNull(r);
        Assert.Equal("giving kiwi 5 cobblestone", r!.Value.Note);
        await Until(() => !c.Gotos.IsEmpty); // give_items walks over, then drops
    }

    [Theory]
    [InlineData("claudechan wait that's the bridge not the edge where I was")]
    [InlineData("claudechan don't stop")]
    [InlineData("claudechan jump in the hole")]
    [InlineData("claudechan you're mining the flooor")]
    public async Task Ambiguous_lines_never_trigger_a_reflex(string line)
    {
        var (h, c) = Bot();
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-3.5, 65, 0.5), 0, 0);
        var gate = new TaskCompletionSource();
        c.GotoGate = ct => gate.Task.WaitAsync(ct);
        h.Runtime.Tools.TryGet("goto", out var go);
        _ = h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "5,65,5" }))], TimeSpan.Zero, default);
        await Until(() => c.CurrentAction == "goto");
        c.RaiseChat("<kiwi> " + line);
        await Task.Delay(100);
        Assert.True(h.Runtime.Activities.IsBusy); // nothing was stopped
        Assert.Single(c.Gotos);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("act") || x.StartsWith("stop"));
        gate.SetResult();
    }

    [Fact]
    public void Here_block_is_compact_and_never_stale()
    {
        var (h, c) = Bot();
        h.Runtime.Memory.SetWaypoint("cobble_gen", -4, 65, 0);
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-3.5, 65, 0.5), 90, 0); // yaw 90 = facing west
        c.Give(36, Blocks.Cobblestone, 64);
        c.Give(37, Items.StonePickaxe, 1);
        var batch = new EventBatch([GameEvent.Chat("kiwi", "claudechan grab cobble")]);
        var here = new Brainstem.HereBlock(c, h.Runtime.Memory, h.Runtime.Observations, (x, y, z) => Agent.Memory.Landmarks.Near(h.Runtime.Memory, h.Runtime.Observations, x, y, z));
        var first = here.Build(batch)!;
        Assert.StartsWith("[here] you: ", first);
        Assert.Contains("of cobble_gen", first);
        Assert.Contains("inv: cobblestone x64, stone pickaxe", first);
        Assert.Contains("kiwi 4 W of you, facing W", first);
        Assert.Contains("near: cobble_gen 4 W", first);
        Assert.True(first.Length / 3.5 <= 250, $"≈{first.Length / 3.5:0} tokens");

        // The world changes: the next build reflects it (nothing is cached between turns).
        c.World.Inventory.SetSlot(0, 36, ItemStack.Empty);
        c.World.Entities.Teleport(7, new Vec3d(2.5, 65, 0.5), 0, 0);
        var second = here.Build(batch)!;
        Assert.DoesNotContain("cobblestone", second);
        Assert.Contains("inv: stone pickaxe", second);
        Assert.Contains("kiwi 2 E of you, facing S", second);
        Assert.Equal(h.Runtime.Agent.HereBlock!(batch), second); // the host wires the same live builder
    }

    [Fact]
    public async Task Speculation_walks_toward_the_gen_and_the_brains_move_adopts_it()
    {
        var (h, c) = Bot();
        h.Runtime.Memory.SetWaypoint("cobble_gen", 9, 65, 0);
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-1.5, 65, 0.5), 0, 0);
        var gate = new TaskCompletionSource();
        c.GotoGate = ct => gate.Task.WaitAsync(ct);
        bool? adopted = null;
        h.Runtime.Activities.SpeculationResolved += (a, _) => adopted = a;

        var target = h.Brainstem!.TrySpeculate("kiwi", "can you mine some cobble for me");
        Assert.Equal(new BlockPos(9, 65, 0), target);
        await Until(() => c.CurrentAction == "goto");
        var (_, opts) = Assert.Single(c.Gotos);
        Assert.False(opts!.AllowBreak); // walking only: never dig, place or parkour speculatively
        Assert.False(opts.AllowPlace);
        Assert.Equal(ParkourMode.Off, opts.Parkour);
        Assert.False(h.Runtime.Activities.IsBusy); // speculation is not a job

        h.Runtime.Tools.TryGet("goto", out var go);
        var brain = h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "cobble_gen" }))], TimeSpan.FromMilliseconds(50), default, ActivityKind.Move);
        var res = await brain;
        Assert.True(adopted);
        Assert.StartsWith("already on the way", res[0].Text);
        Assert.Single(c.Gotos); // not restarted
        Assert.True(h.Runtime.Activities.IsBusy);
        gate.SetResult();
        await Until(() => !h.Runtime.Activities.IsBusy);
    }

    [Fact]
    public async Task Speculation_is_cancelled_and_redirected_when_the_brain_decides_otherwise()
    {
        var (h, c) = Bot();
        h.Runtime.Memory.SetWaypoint("cobble_gen", 9, 65, 0);
        var gate = new TaskCompletionSource();
        c.GotoGate = ct => gate.Task.WaitAsync(ct);
        bool? adopted = null;
        h.Runtime.Activities.SpeculationResolved += (a, _) => adopted = a;
        Assert.NotNull(h.Brainstem!.TrySpeculate("kiwi", "mine 64 cobble"));
        await Until(() => c.CurrentAction == "goto");
        h.Runtime.Tools.TryGet("goto", out var go);
        _ = h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "-9,65,-9" }))], TimeSpan.FromMilliseconds(50), default, ActivityKind.Move);
        await Until(() => c.Gotos.Count == 2);
        Assert.False(adopted);
        Assert.IsType<GoalNear>(c.Gotos.Last().Goal);
        gate.SetResult();
    }

    [Fact]
    public async Task Speculation_is_never_unsafe_and_never_overrides_a_job_or_a_safety_reflex()
    {
        var (h, c) = Bot();
        h.Runtime.Memory.SetWaypoint("cobble_gen", 9, 65, 0);
        h.Runtime.Memory.SetWaypoint("big_tree", -9, 65, 3);
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(-8.5, 65, 0.5), 0, 0);
        c.Give(36, Blocks.Cobblestone, 10);
        foreach (var line in new[] { "mine 64 cobble", "go to the big tree", "bring me cobblestone", "grab cobble and come here" })
        {
            c.Gotos.Clear();
            var gate = new TaskCompletionSource();
            c.GotoGate = ct => gate.Task.WaitAsync(ct);
            Assert.NotNull(h.Brainstem!.TrySpeculate("kiwi", line));
            await Until(() => !c.Gotos.IsEmpty);
            var (_, o) = c.Gotos.Single();
            Assert.False(o!.AllowBreak || o.AllowPlace);
            Assert.Equal(ParkourMode.Off, o.Parkour);
            h.Runtime.Activities.CancelSpeculative("test");
            gate.SetResult();
            await Until(() => h.Runtime.Activities.CurrentKind == null);
        }
        // Only walking ever happened: no digs, places, containers, items.
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("dig") || x.StartsWith("place") || x.StartsWith("open") || x.StartsWith("withdraw") || x.StartsWith("deposit"));

        // A job holds the body: no speculation.
        var jobGate = new TaskCompletionSource();
        c.GotoGate = ct => jobGate.Task.WaitAsync(ct);
        h.Runtime.Tools.TryGet("goto", out var go);
        _ = h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "1,65,5" }))], TimeSpan.Zero, default);
        await Until(() => h.Runtime.Activities.IsBusy);
        Assert.Null(h.Brainstem!.TrySpeculate("kiwi", "mine 64 cobble"));
        Assert.Equal(BodyLayer.Job, h.Brainstem.Stack.Top);
        jobGate.SetResult();
        await Until(() => !h.Runtime.Activities.IsBusy);

        // A safety reflex owns the body: nothing below it acts.
        c.Safety = true;
        Assert.Equal(BodyLayer.Safety, h.Brainstem.Stack.Top);
        Assert.Null(h.Brainstem.TrySpeculate("kiwi", "mine 64 cobble"));
        Assert.Null(h.Brainstem.SocialTick());
        Assert.Null(h.Brainstem.TryReflex("kiwi", "come here"));
    }

    [Fact]
    public async Task Social_loose_follow_never_crosses_an_edge()
    {
        var (h, c) = Bot();
        var bs = h.Brainstem!;
        // Partner walked off onto the void side (beyond the floor): no follow.
        c.World.Entities.SpawnPlayer(7, "kiwi", new Vec3d(13.5, 65, 0.5), 0, 0);
        bs.OnAddressed("kiwi");
        await Task.Delay(30);
        Assert.NotEqual("follow kiwi", bs.SocialTick());
        Assert.Empty(c.Gotos);
        // Partner on the island, 10 blocks away: a loose, plain walk (no parkour, no building), stopping short of them.
        c.World.Entities.Teleport(7, new Vec3d(-9.5, 65, 0.5), 0, 0);
        Assert.Equal("follow kiwi", bs.SocialTick());
        await Until(() => !c.Gotos.IsEmpty);
        var (goal, o) = c.Gotos.Single();
        Assert.Equal(ParkourMode.Off, o!.Parkour);
        Assert.False(o.AllowBreak || o.AllowPlace);
        Assert.Equal(ActivityKind.Social, h.Runtime.Activities.CurrentKind ?? ActivityKind.Social);
        Assert.False(Brainstem.BrainstemController.SafeToFollow(c.World, c.World.Player.Position, new Vec3d(13.5, 65, 0.5)));
        Assert.True(Brainstem.BrainstemController.SafeToFollow(c.World, c.World.Player.Position, new Vec3d(-9.5, 65, 0.5)));
    }

    [Fact]
    public async Task Social_tracking_glances_and_yields_to_every_higher_layer()
    {
        var (h, c) = Bot();
        var bs = h.Brainstem!;
        c.World.Entities.SpawnPlayer(8, "robin", new Vec3d(3.5, 65, 0.5), 0, 0);
        Assert.Equal("glance robin", bs.SocialTick());
        await Task.Delay(1600); // the glance hold expires
        Assert.Equal("track robin", bs.SocialTick());

        // A job outranks social behaviour.
        var gate = new TaskCompletionSource();
        c.GotoGate = ct => gate.Task.WaitAsync(ct);
        h.Runtime.Tools.TryGet("goto", out var go);
        _ = h.Runtime.Activities.RunAsync([(go, TestBot.J(new { to = "1,65,5" }))], TimeSpan.Zero, default);
        await Until(() => h.Runtime.Activities.IsBusy);
        Assert.Null(bs.SocialTick());
        gate.SetResult();
    }
}
