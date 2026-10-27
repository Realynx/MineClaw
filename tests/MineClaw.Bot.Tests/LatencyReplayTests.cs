using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Replays real chat lines from the live log (mineclaw-20261027.log) through the whole bot host with a fake LLM on a virtual
/// clock: Opus 2.5 s per round, Sonnet 1.2 s, Haiku 1.35 s, tools as fast as logged (~0). "before" = the logged tool
/// sequence on the old pipeline (no brainstem, 1.8 s debounce, delegate for macros); "after" = the brainstem on, the brain
/// acting in round 0 with [here] (do / move-first, streaming at ~60 % of the round), reflexes in C#. Measured by the real
/// <see cref="LatencyTracker"/>. The "after" brain decisions are modelled on the new prompt rules, not observed.
/// </summary>
public class LatencyReplayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class VirtualClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 10, 27, 7, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    }

    private sealed record Call(string Tool, object Input);

    /// <summary>One brain round: its tool calls (empty = a text-only reply that ends the turn).</summary>
    private sealed record Round(params Call[] Calls);

    private sealed record Case(string Type, string Line, Round[] Before, string[][] WorkerBefore, Round[] After, string[][]? WorkerAfter = null);

    private sealed class ReplayLlm(VirtualClock clock, Round[] brain, string[][] worker, bool streaming) : ILlmClient
    {
        private int _b, _w;
        public int BrainCalls => _b;

        public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            await Task.Yield();
            switch (r.Tier)
            {
                case ModelTier.Cheap:
                    clock.Advance(1.35);
                    return Resp(r, StopReasons.EndTurn, new LlmText("doors: use_block opens and closes them (#12)"));
                case ModelTier.Worker:
                {
                    clock.Advance(1.2);
                    int i = Interlocked.Increment(ref _w) - 1;
                    if (i >= worker.Length) return Resp(r, StopReasons.EndTurn, new LlmText("ok: done"));
                    var uses = worker[i].Select((t, k) => (LlmBlock)new LlmToolUse($"w{i}_{k}", t, JsonSerializer.SerializeToElement(WorkerArgs(t)))).ToArray();
                    return Resp(r, StopReasons.ToolUse, uses);
                }
                default:
                {
                    int i = Interlocked.Increment(ref _b) - 1;
                    if (i >= brain.Length || brain[i].Calls.Length == 0) { clock.Advance(2.5); return Resp(r, StopReasons.EndTurn, new LlmText("(silent)")); }
                    var uses = brain[i].Calls.Select((c, k) => new LlmToolUse($"b{i}_{k}", c.Tool, JsonSerializer.SerializeToElement(c.Input))).ToList();
                    bool walkFirst = uses[0].Name is "goto" or "follow" || (uses[0].Name == "do" && uses[0].Input.GetProperty("action").GetString() is "goto" or "follow");
                    if (streaming && r.OnToolUse != null && walkFirst)
                    {
                        clock.Advance(1.5); // the first tool_use block completes while the model still writes
                        r.OnToolUse(uses[0]);
                        await Task.Delay(20, ct);
                        clock.Advance(1.0);
                    }
                    else clock.Advance(2.5);
                    return Resp(r, StopReasons.ToolUse, uses.Cast<LlmBlock>().ToArray());
                }
            }
        }

        private static object WorkerArgs(string tool) => tool switch
        {
            "withdraw" => new { item = "cobblestone", count = 64 },
            "goto" => new { to = "cobble_gen" },
            "harvest_generator" => new { at = "cobble_gen", count = 64 },
            "craft" => new { item = "cobblestone slab", count = 30 },
            "find_skill" => new { query = "expand island" },
            "skill" => new { name = "expand_island", args = new { rows = 2 } },
            "use_block" => new { at = "2,65,0" },
            "where_is" => new { item = "cobblestone" },
            _ => new { },
        };

        private static LlmResponse Resp(LlmRequest r, string stop, params LlmBlock[] b) => new(b, stop, LlmUsage.Zero, r.Model ?? "claude-opus-5-5");
    }

    private static readonly object Fox = new { };

    private static Case[] Cases() =>
    [
        // ---- real lines from the live log (before = the logged tool sequence)
        new("fetch+come", "grab some cobble stone and come here I nee you to build for me",
            [new(new Call("inventory", Fox)), new(new Call("where_is", new { item = "cobblestone" })),
             new(new Call("delegate", new { task = "take 64 cobblestone from the chest", done_when = "64 in inventory" })), new(new Call("goto", new { to = "PoofimaFox" })), new()],
            [["withdraw"]],
            [new(new Call("do", new { action = "withdraw", args = new { item = "cobblestone", count = 64 }, then = new[] { new { action = "goto", args = new { to = "PoofimaFox" } } } })), new()]),
        new("move", "opened the door for you",
            [new(new Call("goto", new { to = "PoofimaFox" })), new()], [],
            [new(new Call("do", new { action = "goto", args = new { to = "PoofimaFox" } })), new()]),
        new("question", "see this ledge I'm by?",
            [new(new Call("look", Fox)), new()], [],
            [new()]),
        new("build (delegate)", "yes we're extending this way 2 rows just this side of the doors",
            [new(new Call("delegate", new { task = "extend the island 2 rows west by the doors", done_when = "2 rows placed" })), new()],
            [["look", "find_skill"], ["skill"]],
            [new(new Call("do", new { action = "goto", args = new { to = "PoofimaFox" } }),
                 new Call("delegate", new { task = "extend the island 2 rows west by the doors", done_when = "2 rows placed" })), new()],
            [["look", "find_skill"], ["skill"]]),
        new("stop (ambiguous)", "wait that's the bridge not the edhe where I was",
            [new(new Call("stop", Fox)), new()], [],
            [new(new Call("stop", Fox)), new()]),
        new("stop", "oh my got both of you stop!",
            [new(new Call("stop", Fox), new Call("hive", new { op = "recall", clone = "67" })), new()], [],
            [new(new Call("hive", new { op = "recall", clone = "67" })), new()]),
        new("gen job (hive)", "maid, can you get 67 and go work the cobble gen for me all night?",
            [new(new Call("ask_memory", new { question = "which gen cell is 67's" })), new(new Call("recall_detail", new { id = 3 })), new(new Call("recall_detail", new { id = 4 })),
             new(new Call("list_memory", Fox)), new(new Call("recall_detail", new { id = 7 })), new(new Call("hive", new { op = "spawn", clone = "67", task = "work the other gen cell" })),
             new(new Call("delegate", new { task = "harvest the cobble gen all night", done_when = "stopped" })), new()],
            [["look", "inventory"], ["goto"], ["harvest_generator"]],
            [new(new Call("hive", new { op = "spawn", clone = "67", task = "work the other gen cell" }),
                 new Call("do", new { action = "goto", args = new { to = "cobble_gen" }, then = new[] { new { action = "harvest_generator", args = new { at = "cobble_gen", count = 20000 } } } })), new()]),
        new("craft", "can you make me 30 cobble slabs? :3",
            [new(new Call("delegate", new { task = "craft 30 cobblestone slabs", done_when = "30 slabs" })), new()],
            [["inventory", "where_is"], ["craft"]],
            [new(new Call("do", new { action = "craft", args = new { item = "cobblestone slab", count = 30 } })), new()]),
        new("trees (hive)", "yea :3, in the meantime can you harvest trees with 67? have 69 on tool crafting duty",
            [new(new Call("hive", new { op = "spawn", clone = "67", task = "trees" }), new Call("chop_tree", Fox)), new()], [],
            [new(new Call("hive", new { op = "spawn", clone = "67", task = "trees" }), new Call("do", new { action = "chop_tree" })), new()]),
        new("trees", "I see more trees to be cut",
            [new(new Call("chop_tree", Fox)), new()], [],
            [new(new Call("do", new { action = "chop_tree" })), new()]),
        new("knowledge+job", "are you able to open and close doors yet?",
            [new(new Call("ask_memory", new { question = "can I open doors" })), new(new Call("delegate", new { task = "open and close the door", done_when = "door toggled" })), new()],
            [["look"], ["use_block"]],
            [new(new Call("ask_memory", new { question = "can I open doors" })), new(new Call("delegate", new { task = "open and close the door", done_when = "door toggled" })), new()],
            [["look"], ["use_block"]]),
        // ---- simple commands in the players' usual phrasing (before = what the old brain does: one round, one tool)
        new("come (simple)", "come here", [new(new Call("goto", new { to = "PoofimaFox" })), new()], [], [new()]),
        new("follow (simple)", "follow me", [new(new Call("follow", new { player = "PoofimaFox" })), new()], [], [new()]),
        new("gesture (simple)", "jump", [new(new Call("act", new { move = "jump" })), new()], [], [new()]),
        new("stop (simple)", "stop", [new(new Call("stop", Fox)), new()], [], [new()]),
    ];

    private async Task<LatencyRecord?> RunAsync(Case k, bool after)
    {
        var clock = new VirtualClock();
        var llm = new ReplayLlm(clock, after ? k.After : k.Before, after ? k.WorkerAfter ?? k.WorkerBefore : k.WorkerBefore, streaming: after);
        var c = new FakeGameClient();
        var o = TestBot.Options();
        o.InlineToolWaitSeconds = 20;
        o.Self.TrustedPlayers = ["PoofimaFox"];
        o.Brainstem.Enabled = after;
        o.Brainstem.Social.Enabled = false;
        o.Brainstem.MoveInlineWaitMs = 100; // real ms (the virtual clock is unaffected)
        var h = BotHost.Create(c, o, null, llm, time: clock, persist: false);
        c.Floor(64, 10);
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(-8.5, 65, 0.5), 90, 0);
        c.Give(36, Items.StonePickaxe, 1);
        h.Runtime.Memory.SetWaypoint("cobble_gen", 9, 65, 1);
        h.Runtime.Observations.Upsert(new Agent.Rag.Observation("tree", "tree@-7,65,8", "oak tree") { Bounds = Agent.Rag.Box.Of(-7, 65, 8, -7, 70, 8) });
        h.Runtime.Tools.Register("hive", "hive", ToolSchema.Create().Str("op").Build(), (_, _) => Task.FromResult(ToolResult.Success("ok")), tiers: ToolTiers.Main);
        var records = new ConcurrentQueue<LatencyRecord>();
        h.Runtime.Latency.Recorded += records.Enqueue;

        // Walking takes time: a speculative walk is still under way while the brain plans (released after its turn).
        var walking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        c.GotoGate = ct => ActivityManager.RunningKind == ActivityKind.Speculative ? walking.Task.WaitAsync(ct) : Task.CompletedTask;
        c.RaiseChat($"<PoofimaFox> claudechan {k.Line}");
        await Task.Delay(50); // the brainstem's C# reaction runs in real (ms) time, before the virtual debounce elapses
        var d = h.Runtime.Options.ChatDebounce;
        var text = "claudechan " + k.Line;
        double debounce = d.IsImmediate(k.Line) || (d.ImmediateOnQuestion && text.TrimEnd().EndsWith('?')) ? 0
            : after && d.LooksComplete(k.Line) ? 1.2 : 1.8;
        clock.Advance(debounce);
        var batch = h.Runtime.Events.TryTakeBatch();
        Assert.NotNull(batch);
        await h.Runtime.Agent.HandleBatchAsync(batch!, default);
        walking.TrySetResult();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((h.Runtime.Activities.IsBusy || records.IsEmpty) && sw.ElapsedMilliseconds < 3000) await Task.Delay(10);
        clock.Advance(200);
        h.Runtime.Latency.Tick();
        h.Dispose();
        return records.FirstOrDefault();
    }

    [Fact]
    public async Task Replay_live_chat_before_vs_after()
    {
        var rows = new List<(string Type, LatencyRecord? B, LatencyRecord? A)>();
        foreach (var k in Cases()) rows.Add((k.Type, await RunAsync(k, after: false), await RunAsync(k, after: true)));

        static string S(long? ms) => ms is { } v ? $"{v / 1000.0:0.0}s" : "-";
        output.WriteLine($"{"type",-18} {"react b→a",-13} {"move b→a",-13} {"action b→a",-13} {"rounds b→a",-10} spec");
        foreach (var (type, b, a) in rows)
            output.WriteLine($"{type,-18} {S(b?.ReactionMs) + "→" + S(a?.ReactionMs),-13} {S(b?.FirstMoveMs) + "→" + S(a?.FirstMoveMs),-13} " +
                $"{S(b?.FirstActionMs) + "→" + S(a?.FirstActionMs),-13} {(b?.RoundsBeforeAction?.ToString() ?? "-") + "→" + (a?.RoundsBeforeAction?.ToString() ?? "-"),-10} {a?.Speculation}");
        long? P50(IEnumerable<long?> v) => LatencyTracker.P50(v);
        var act = rows.Where(r => r.B?.FirstActionMs != null).ToList();
        output.WriteLine($"p50 first action: {S(P50(act.Select(r => r.B!.FirstActionMs)))} → {S(P50(act.Select(r => r.A?.FirstActionMs)))}; " +
            $"p50 reaction: {S(P50(rows.Select(r => r.B?.ReactionMs)))} → {S(P50(rows.Select(r => r.A?.ReactionMs)))}");
        var simple = rows.Where(r => r.Type is "move" or "craft" or "trees" or "stop" or "come (simple)" or "follow (simple)" or "gesture (simple)" or "stop (simple)").ToList();
        output.WriteLine($"p50 first action, simple commands: {S(P50(simple.Select(r => r.B?.FirstActionMs)))} → {S(P50(simple.Select(r => r.A?.FirstActionMs)))}");

        // Targets: simple commands act within 4 s (p50), and nothing gets slower.
        Assert.True(P50(simple.Select(r => r.A?.FirstActionMs)) <= 4000);
        foreach (var (type, b, a) in act)
            Assert.True(a?.FirstActionMs <= b!.FirstActionMs, $"{type}: {S(b.FirstActionMs)} → {S(a?.FirstActionMs)}");
        foreach (var (type, b, a) in rows)
            Assert.True(a?.ReactionMs is { } ar && (b?.ReactionMs is not { } br || ar <= br), $"{type}: reaction {S(b?.ReactionMs)} → {S(a?.ReactionMs)}");
    }
}
