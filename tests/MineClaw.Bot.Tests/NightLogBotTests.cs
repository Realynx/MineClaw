using MineClaw.Agent.Rag;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Memory;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Tests;

/// <summary>Regressions from the 2026-10-27 night log review (B10, B14, L1, L6, L7 and the demo punch classification).</summary>
public class NightLogBotTests
{
    // ---------------------------------------------------------------- B10: shared spawn-protection region

    private static void UpsertRegion(BotHost h) =>
        h.Runtime.Observations.Upsert(new Observation("protected_region", "spawn", "spawn protection: can't break blocks within 16 of 0,66,-3 (learned by main)")
        {
            Bounds = Box.Of(-16, 0, -19, 16, 127, 13),
            Dimension = 0,
        });

    [Fact]
    public void A_clone_drops_its_region_when_any_hive_process_retracted_it_and_adopts_a_shared_one()
    {
        var (h, _) = TestBot.Create();
        var clone = new MinecraftClient(new ClientOptions { Username = "maid_chan_67" });
        // 67 at 07:40:48 learned 0,66,-3 r16; main retracted it at 07:46:12 ("edit at (16, 67, 3) worked inside the remembered
        // spawn protection: retracted it"), but 67 kept its own copy and refused to replant at 13,65,3.
        UpsertRegion(h);
        BotHost.SyncProtectedRegion(clone, h.Runtime, null);
        Assert.NotNull(clone.ProtectedRegion);               // learned elsewhere: adopted
        Assert.Equal(16, clone.ProtectedRegion!.Radius);
        Assert.Equal(new BlockPos(0, 0, -3), clone.ProtectedRegion.Center with { Y = 0 });

        h.Runtime.Observations.Remove("protected_region", "spawn", tombstone: false, dimension: 0); // main's success retracts it
        BotHost.SyncProtectedRegion(clone, h.Runtime, null);
        // The adopted region keeps the learner's time (now) → within the 30 s write grace; age it past that.
        var aged = clone.ProtectedRegion! with { LearnedAt = DateTime.UtcNow.AddMinutes(-1) };
        clone.ForgetProtectedRegion("test reset");
        clone.AdoptProtectedRegion(aged);
        BotHost.SyncProtectedRegion(clone, h.Runtime, null);
        Assert.Null(clone.ProtectedRegion);
        Assert.False(clone.EditBlocked(new BlockPos(13, 65, 3)));
    }

    [Fact]
    public void An_edit_next_to_another_hive_bot_is_contested()
    {
        var clone = new MinecraftClient(new ClientOptions { Username = "maid_chan_67" });
        clone.World.Entities.SpawnPlayer(5, "maid_chan", new Vec3d(13.5, 65, 1.5), 0, 0);
        Func<IEnumerable<string>> hive = () => ["maid_chan", "maid_chan_67", "maid_chan_69"];
        Assert.True(BotHost.IsContested(clone, new BlockPos(13, 70, 2), null, hive));  // the crown both bots were chopping
        Assert.False(BotHost.IsContested(clone, new BlockPos(30, 65, 2), null, hive));
        Assert.False(BotHost.IsContested(clone, new BlockPos(13, 70, 2), null, () => ["maid_chan_67"]));
    }

    // ---------------------------------------------------------------- L1: demos keep both cells, absolute coordinates

    [Fact]
    public void A_second_demo_is_appended_as_a_variant_not_a_replacement()
    {
        var (h, _) = TestBot.Create();
        var adapter = h.MemoryAdapter;
        // live2 00:46:52 then 00:52:23: the second demo wiped the first one's east-cell steps.
        adapter.SaveSteps("dual cobble generator", ["stood at 8,64,8 facing cobblestone", "punched cobblestone at 6,64,8"], "PoofimaFox");
        adapter.SaveSteps("dual cobble generator", ["stood at 3,64,5 facing cobblestone", "punched cobblestone at 4,64,6"], "PoofimaFox");
        adapter.SaveSteps("dual cobble generator", ["stood at 3,64,5 facing cobblestone", "punched cobblestone at 4,64,6"], "PoofimaFox"); // same again
        var steps = h.Runtime.Memory.GetStructure("dual cobble generator")!.Steps;
        Assert.Equal(["stood at 8,64,8 facing cobblestone", "punched cobblestone at 6,64,8", "demo 2 by PoofimaFox (variant):",
            "stood at 3,64,5 facing cobblestone", "punched cobblestone at 4,64,6"], steps);
    }

    [Fact]
    public async Task Watch_demo_stores_absolute_coordinates_and_punches_with_a_block_in_hand_are_punches()
    {
        var (h, c) = TestBot.Create();
        var w = c.World;
        for (int x = -2; x <= 7; x++) for (int z = -3; z <= 4; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(0, 64, 0, Blocks.Water, 0);
        w.SetBlock(1, 64, 0, Blocks.Cobblestone, 0);
        w.SetBlock(2, 64, 0, Blocks.Lava, 0);
        w.SetBlock(3, 64, 0, Blocks.Cobblestone, 0);
        w.SetBlock(4, 64, 0, Blocks.Water, 0);
        h.Runtime.Memory.UpsertStructure("dual cobble generator", s =>
        {
            s.Min = new MineClaw.Agent.Memory.BlockPos(0, 63, -1);
            s.Max = new MineClaw.Agent.Memory.BlockPos(4, 64, 1);
        });
        // Alice 07:00:57: "right-clicked cobblestone on cobblestone at +0,+1,+0" — two punches with cobblestone in hand.
        w.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(3.5, 64, 2.5), 180, 29, Blocks.Cobblestone);
        var run = h.Runtime.Tools.ExecuteAsync("watch_demo", TestBot.J(new { player = "PoofimaFox", structure = "dual cobble generator" }), CancellationToken.None);
        await Task.Delay(150);
        c.RaiseSwing(7);
        await Task.Delay(50);
        h.Runtime.Chat.OnServerChat("<PoofimaFox> did it");
        var r = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(r.Ok, r.Text);
        Assert.Contains("punched cobblestone at 3,64,0", r.Text);   // absolute, and a punch
        Assert.DoesNotContain("right-clicked cobblestone", r.Text);
        Assert.DoesNotContain("+0,", r.Text);
    }

    // ---------------------------------------------------------------- L6: ask_stand never takes chatter as the answer

    [Fact]
    public async Task Ask_stand_timeout_with_only_chatter_is_not_confirmed()
    {
        var (h, c) = TestBot.Create(new BotOptions { AskStandTimeoutSeconds = 1 });
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(-0.5, 64, 1.5), 0, 0);
        var run = h.Runtime.Tools.ExecuteAsync("ask_stand", TestBot.J(new { player = "PoofimaFox" }), CancellationToken.None);
        await Task.Delay(100);
        // live2 01:12:31: "PoofimaFox stands at -1,64,1 (said "…that is wood not cobble 67")".
        h.Runtime.Chat.OnServerChat("<PoofimaFox> that is wood not cobble 67");
        var r = await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(r.Ok, r.Text);
        Assert.StartsWith("not confirmed: PoofimaFox didn't say \"here\"", r.Text);
        Assert.Contains("that is wood not cobble 67", r.Text);
        Assert.DoesNotContain("stands at", r.Text);
    }

    // ---------------------------------------------------------------- B14: hunger reported once

    [Fact]
    public void Hunger_is_reported_once_and_the_latch_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hunger-latch-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var latch = new HungerLatch(path, clock);
            DangerEvent D(int level, int food) => new(DangerKind.Hunger, $"starving: food {food}, no food in inventory") { Level = level };
            Assert.True(latch.ShouldWake(D(1, 14), 20));          // 06:49:33 first report
            Assert.True(latch.ShouldWake(D(2, 6), 20));           // 06:58:56 worse: once more
            Assert.True(latch.OnChat("dw about food for now"));   // 07:01:43
            Assert.False(latch.ShouldWake(D(3, 0), 18));          // 07:13:35 worse, but acknowledged
            Assert.False(latch.OnChat("can you mine some cobble"));
            // Restarts at 07:09/07:27/07:52 re-fired "danger Hunger: starving": the latch is on disk.
            var after = new HungerLatch(path, clock);
            Assert.True(after.Acknowledged);
            Assert.False(after.ShouldWake(D(3, 0), 14));
            Assert.True(after.ShouldWake(D(3, 0), 6));            // health crossed the low line: once
            Assert.False(after.ShouldWake(D(3, 0), 5));
            after.Observe(food: 0, hasFood: true, eatAtFood: 14); // food in the inventory: cleared
            Assert.Equal(0, after.Level);
            Assert.True(after.ShouldWake(D(1, 12), 20));
            Assert.True(new HungerLatch(path, clock).Level > 0);
            Assert.True(latch.OnChat("I'm working on the food situation <3"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_latched_hunger_signal_goes_to_context_without_waking()
    {
        var (h, c) = TestBot.Create();
        c.RaiseDanger(new DangerEvent(DangerKind.Hunger, "starving: food 6, no food in inventory (no sprint, no regen)") { Level = 2 });
        c.RaiseChat("<PoofimaFox> dw about food for now");
        c.RaiseDanger(new DangerEvent(DangerKind.Hunger, "starving: food 0, losing hp (stops at 10), no food in inventory") { Level = 3 });
        var hunger = h.Runtime.Events.Snapshot().Where(e => e.Text.Contains("starving")).ToList();
        Assert.Equal(2, hunger.Count);
        Assert.Equal(Agent.Events.EventPriority.Normal, hunger[0].Priority);
        Assert.Equal(Agent.Events.EventPriority.Background, hunger[1].Priority);
        Assert.Contains("already reported", hunger[1].Text);
    }

    // ---------------------------------------------------------------- L7: indexer failure log storm

    [Fact]
    public void A_repeating_indexer_failure_logs_one_stack_then_a_count_per_minute()
    {
        var log = new ListLogger();
        var t = new FailureThrottle("world indexer pass failed");
        var now = DateTimeOffset.UtcNow;
        var ex = new KeyNotFoundException("The given key '42' was not present in the dictionary.");
        Assert.True(t.Report(ex, now, log));
        for (int i = 1; i < 60; i++) Assert.False(t.Report(ex, now.AddSeconds(i * 0.99), log)); // once a second
        Assert.True(t.Report(ex, now.AddSeconds(61), log));
        Assert.True(t.Report(new InvalidOperationException("other"), now.AddSeconds(62), log));
        Assert.Equal(3, log.Lines.Count);
        Assert.NotNull(log.Lines[0].Ex);
        Assert.Null(log.Lines[1].Ex);
        Assert.Contains("60 more", log.Lines[1].Text);
    }

    private sealed class ListLogger : ILogger
    {
        public List<(string Text, Exception? Ex)> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((formatter(state, exception), exception));
    }
}
