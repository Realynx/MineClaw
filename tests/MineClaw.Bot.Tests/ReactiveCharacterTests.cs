using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Telemetry;
using MineClaw.Bot.GameTools;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Act first, think in parallel (docs/design/reactive-character.md), through the whole bot host with a slow fake LLM (each
/// brain round 2-4 s of real time): instant lines, the turn's "[already said]/[already doing]", standing bindings that run with
/// no model in the loop, their trust and safety rules, and the latency benchmark (baseline = brainstem off).
/// </summary>
public class ReactiveCharacterTests(ITestOutputHelper output)
{
    /// <summary>Brain rounds take 2-4 s (real time); the line prefetch answers at once with lines for every moment asked.</summary>
    private sealed class SlowLlm(Func<LlmRequest, LlmResponse>? brain = null, int minMs = 2000, int maxMs = 4000) : ILlmClient
    {
        public readonly ConcurrentQueue<LlmRequest> Brain = new();
        public int Prefetches;
        private readonly Random _rng = new(7);

        public async Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            if (r.Purpose == Agent.Character.LinePrefetcher.Purpose)
            {
                Interlocked.Increment(ref Prefetches);
                var user = string.Join(" ", r.Messages.SelectMany(m => m.Content).OfType<LlmText>().Select(t => t.Text));
                var keys = System.Text.RegularExpressions.Regex.Match(user, @"\[moments\] (.*?) \(").Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries);
                var map = keys.ToDictionary(k => k, k => new[] { Line(k, 1), Line(k, 2), Line(k, 3) });
                return new LlmResponse([new LlmText(JsonSerializer.Serialize(map))], StopReasons.EndTurn, LlmUsage.Zero, "claude-haiku-5-5");
            }
            if (r.Purpose.StartsWith("quick_", StringComparison.Ordinal))
                return new LlmResponse([new LlmText("""{"needs_brain":true,"say":""}""")], StopReasons.EndTurn, LlmUsage.Zero, "claude-haiku-5-5");
            Brain.Enqueue(r);
            int ms;
            lock (_rng) ms = _rng.Next(minMs, maxMs);
            await Task.Delay(ms, ct);
            return brain?.Invoke(r) ?? new LlmResponse([new LlmText("(silent)")], StopReasons.EndTurn, LlmUsage.Zero, r.Model ?? "claude-opus-5-5");
        }

        private static string Line(string key, int i) => key.Split('@')[0] switch
        {
            "greet" => (i == 1 ? "heyy " : i == 2 ? "oh hi " : "hii uwu ") + (key.Contains('@') ? key.Split('@')[1].ToLowerInvariant() : "you") + "~",
            "come" => i == 1 ? "omw foxy~" : i == 2 ? "coming coming!" : "right there in a sec",
            "follow" => i == 1 ? "right behind you~" : i == 2 ? "lead the way!" : "following!",
            "thanks" => i == 1 ? "aww anytime~" : i == 2 ? "hehe np foxy" : "for you? always",
            "stop" => i == 1 ? "okok stopping" : i == 2 ? "freezing!" : "stopped~",
            var m => $"{m} line {i}",
        };
    }

    private static (BotHost H, FakeGameClient C, SlowLlm Llm) Host(bool brainstem = true, Func<LlmRequest, LlmResponse>? brain = null)
    {
        var c = new FakeGameClient();
        var o = TestBot.Options();
        o.Self.TrustedPlayers = ["PoofimaFox"];
        o.Self.RegularPlayers = ["kiwi"];
        o.Brainstem.Enabled = brainstem;
        o.Brainstem.Social.Enabled = false;
        o.Brainstem.MoveInlineWaitMs = 50;
        o.Brainstem.Reply.AckAfterMs = 60_000; // the cheap ack is its own path; this measures instant lines
        var llm = new SlowLlm(brain);
        var h = BotHost.Create(c, o, null, llm, persist: false);
        c.Floor(64, 10);
        c.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        c.World.Player.SetHealth(20, 20, 5);
        c.World.Entities.SpawnPlayer(7, "PoofimaFox", new Vec3d(-6.5, 65, 0.5), 270, 0);
        c.World.Entities.SetPlayerListEntry("PoofimaFox", true);
        c.World.Entities.SpawnPlayer(9, "kiwi", new Vec3d(4.5, 65, 4.5), 0, 0);
        c.World.Entities.SetPlayerListEntry("kiwi", true);
        c.Give(36, Items.StonePickaxe, 1);
        return (h, c, llm);
    }

    private static async Task Prefetch(BotHost h)
    {
        var p = h.Runtime.Agent.Voice?.Prefetcher;
        Assert.NotNull(p);
        await p!.RefreshAsync();
    }

    /// <summary>One chat line end to end; returns the latency record and what went to chat.</summary>
    private static async Task<(LatencyRecord? Rec, List<string> Said)> Chat(BotHost h, FakeGameClient c, string line, string who = "PoofimaFox")
    {
        var recs = new ConcurrentQueue<LatencyRecord>();
        h.Runtime.Latency.Recorded += recs.Enqueue;
        int before = c.Said.Count;
        c.RaiseChat($"<{who}> claudechan {line}");
        var sw = Stopwatch.StartNew();
        EventBatch? batch = null;
        while (batch == null && sw.ElapsedMilliseconds < 3000) { await Task.Delay(20); batch = h.Runtime.Events.TryTakeBatch(); }
        Assert.NotNull(batch);
        await h.Runtime.Agent.HandleBatchAsync(batch!, default);
        sw.Restart();
        while (recs.IsEmpty && sw.ElapsedMilliseconds < 3000) { await Task.Delay(20); h.Runtime.Latency.Tick(); }
        h.Runtime.Latency.Recorded -= recs.Enqueue;
        List<string> said;
        lock (c.Said) said = c.Said.Skip(before).ToList();
        return (recs.FirstOrDefault(), said);
    }

    [Fact]
    public async Task Benchmark_first_visible_reaction_under_300ms_with_a_slow_llm()
    {
        string[] lines = ["come here", "follow me", "thanks!", "hi", "stop"];
        var rows = new List<(string Line, LatencyRecord? Before, LatencyRecord? After)>();
        foreach (var line in lines)
        {
            var (hb, cb, _) = Host(brainstem: false, brain: r => new LlmResponse([new LlmText("okie foxy~")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5"));
            var before = await Chat(hb, cb, line);
            hb.Dispose();
            var (ha, ca, _) = Host(brain: r => new LlmResponse([new LlmText("(silent)")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5"));
            await Prefetch(ha);
            var after = await Chat(ha, ca, line);
            ha.Dispose();
            rows.Add((line, before.Rec, after.Rec));
        }
        static string S(long? ms) => ms is { } v ? $"{v} ms" : "-";
        output.WriteLine($"{"line",-12} {"react before",-14} {"react after",-12} {"first line before",-18} first line after");
        foreach (var (l, b, a) in rows)
            output.WriteLine($"{l,-12} {S(b?.ReactionMs),-14} {S(a?.ReactionMs),-12} {S(b?.FirstChatMs),-18} {S(a?.FirstChatMs)} ({a?.ReactionBy}, {a?.Kind})");
        foreach (var (l, _, a) in rows)
        {
            Assert.NotNull(a);
            Assert.True(a!.ReactionMs is < 300, $"{l}: first visible reaction {S(a.ReactionMs)}");
        }
        // Small talk answered by the instant line needs no model call at all; the line lands within the typing cap.
        Assert.All(rows.Where(r => r.Line is "thanks!" or "hi"), r => Assert.True(r.After!.FirstChatMs < 1500, $"{r.Line}: {S(r.After!.FirstChatMs)}"));
        Assert.All(rows, r => Assert.True(r.Before?.ReactionMs is null or >= 1500)); // baseline: nothing visible until the brain spoke
    }

    [Fact]
    public async Task Instant_small_talk_is_final_with_no_model_call_and_never_repeats()
    {
        var (h, c, llm) = Host();
        await Prefetch(h);
        var (_, said1) = await Chat(h, c, "thanks!");
        var (_, said2) = await Chat(h, c, "thanks!");
        Assert.Empty(llm.Brain);
        Assert.Single(said1);
        Assert.Single(said2);
        Assert.NotEqual(said1[0], said2[0]); // phrase memory: no repeat
        h.Dispose();
    }

    [Fact]
    public async Task Reflex_turn_sees_already_said_and_doing_and_a_repeat_never_goes_out()
    {
        // The brain (slow) answers with the same line the instant reaction already said: it must not go out twice.
        var (h, c, llm) = Host(brain: r => new LlmResponse([new LlmText("omw foxy~!!")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5"));
        await Prefetch(h);
        var bank = h.Runtime.Agent.Voice!.Bank;
        while (bank.Take("come") != null) { }
        bank.Add("come", null, "omw foxy~", bank.Context); // the one line it will say
        var (rec, said) = await Chat(h, c, "come here");
        Assert.NotNull(rec);
        Assert.Single(said, s => s.Contains("omw", StringComparison.OrdinalIgnoreCase)); // one exchange, not two replies
        var turn = string.Join("\n", llm.Brain.First().Messages.Last().Content.OfType<LlmText>().Select(t => t.Text));
        Assert.Contains("[already said] \"omw foxy~\"", turn);
        Assert.Contains("[reflex]", turn);
        h.Dispose();
    }

    [Fact]
    public async Task Safety_reflexes_preempt_instant_lines_and_bindings()
    {
        var (h, c, _) = Host();
        await Prefetch(h);
        c.Safety = true;
        var (_, said) = await Chat(h, c, "come here");
        Assert.DoesNotContain(said, s => s.Contains("omw") || s.Contains("coming"));
        await SaveAndBind(h, "punch_dig", "dig(args.pos)", new { on = "player.punchBlock", held = "pickaxe" });
        h.Body.Signals!.OnSwing(PunchingFox(c));
        Assert.Contains("safety", h.Body.Bindings!.LastSkip);
        await Task.Delay(100);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("dig"));
        h.Dispose();
    }

    private static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond()) { if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met"); await Task.Delay(20); }
    }

    private static int PunchingFox(FakeGameClient c)
    {
        c.World.Entities.Teleport(7, new Vec3d(3.5, 65, 3.5), 0, 90);
        c.World.Entities.SetHeldItem(7, Items.StonePickaxe);
        return 7;
    }

    private static async Task<string> SaveAndBind(BotHost h, string name, string code, object spec, string speaker = "PoofimaFox")
    {
        var saved = await h.Run("script", new { op = "save", name, code, force = "test: same body, different trigger" });
        Assert.True(saved.Ok, saved.Text);
        TurnContext.Current = new TurnInfo(speaker, [speaker], false);
        try
        {
            var r = await h.Run("script", new { op = "bind", name, @params = spec });
            return (r.Ok ? "ok " : "error ") + r.Text;
        }
        finally { TurnContext.Current = null; }
    }

    [Fact]
    public async Task Binding_runs_instantly_from_the_packet_while_the_brain_is_slow()
    {
        var (h, c, llm) = Host();
        var bound = await SaveAndBind(h, "punch_dig", "// dig what Fox punches\ndig(args.pos)", new { on = "player.punchBlock", player = "PoofimaFox", held = "pickaxe" });
        Assert.StartsWith("ok", bound);
        // The brain is busy thinking (3 s) about something else.
        c.RaiseChat("<PoofimaFox> claudechan what do you think of the tower design so far, honestly?");
        await Task.Delay(100);
        var batch = h.Runtime.Events.TryTakeBatch();
        var thinking = batch != null ? h.Runtime.Agent.HandleBatchAsync(batch, default) : Task.CompletedTask;
        var started = new TaskCompletionSource<double>();
        var sw = new Stopwatch();
        h.Runtime.Tools.Executing += (name, _) => { if (name == "dig") started.TrySetResult(sw.Elapsed.TotalMilliseconds); };
        sw.Start();
        h.Body.Signals!.OnSwing(PunchingFox(c)); // the packet
        var ms = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"first fire (cold engine): {ms:0.0} ms");
        await Until(() => ScriptTools.Of(h.Runtime)!.Controller.Current is not { IsLive: true });
        await Until(() => ScriptTools.Of(h.Runtime)!.Library.Get("punch_dig")!.Script!.Binding!.Fires >= 1);
        started = new TaskCompletionSource<double>();
        c.World.Entities.Teleport(7, new Vec3d(4.5, 65, 3.5), 0, 90); // the next block
        sw.Restart();
        h.Body.Signals!.OnSwing(7);
        ms = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"packet → dig start: {ms:0.0} ms (brain still thinking: {!thinking.IsCompleted})");
        Assert.True(ms < 100, $"{ms} ms");
        await Until(() => c.CallList.Any(x => x.StartsWith("dig 3,64,3")));
        await thinking;
        await Until(() => ScriptTools.Of(h.Runtime)!.Library.Get("punch_dig")!.Script!.Binding!.Fires >= 1);
        h.Dispose();
    }

    [Fact]
    public async Task Bindings_persist_list_and_unbind_and_trust_rules_hold()
    {
        var (h, c, _) = Host();
        var lib = ScriptTools.Of(h.Runtime)!.Library;
        // A regular (kiwi) can't have a world-acting binding; a harmless one is fine.
        var refused = await SaveAndBind(h, "dig_for_kiwi", "dig(args.pos)", new { on = "player.punchBlock" }, speaker: "kiwi");
        Assert.StartsWith("error", refused);
        Assert.Contains("trusted", refused);
        var wave = await SaveAndBind(h, "wave_back", "character.gesture('wave', args.player)", new { on = "player.join", minutes = 30 }, speaker: "kiwi");
        Assert.StartsWith("ok", wave);
        // The brain's own reaction must be harmless even when a trusted player is talking.
        var reaction = await SaveAndBind(h, "thanks_dig", "dig(args.pos)", new { on = "thanks", kind = "reaction" });
        Assert.StartsWith("error", reaction);
        var punch = await SaveAndBind(h, "punch_dig", "dig(args.pos)", new { on = "player.punchBlock", held = "pickaxe", @for = "PoofimaFox" });
        Assert.StartsWith("ok", punch);

        // Persisted on the routine row: a fresh library over the same memory (a restart) still has them.
        var again = new ScriptLibrary(lib.Memory);
        Assert.Contains(again.Bindings(), x => x.Name == "punch_dig" && x.Binding.For == "PoofimaFox" && x.Binding.World);
        Assert.Contains(again.Bindings(), x => x.Name == "wave_back" && x.Binding.Expires != null && !x.Binding.World);
        var list = await h.Run("script", new { op = "bindings" });
        Assert.Contains("punch_dig: player.punchBlock", list.Text);

        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        var off = await h.Run("script", new { op = "unbind", name = "punch_dig" });
        TurnContext.Current = null;
        Assert.True(off.Ok, off.Text);
        Assert.DoesNotContain(lib.Bindings(), x => x.Name == "punch_dig");
        h.Body.Bindings!.Invalidate();
        h.Body.Signals!.OnSwing(PunchingFox(c));
        await Task.Delay(150);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("dig"));
        h.Dispose();
    }

    [Theory]
    [InlineData("(silent)", "kept")]
    [InlineData("also the wheat is ready btw", "amended")]
    [InlineData("actually wait, I can't bow, I'm holding lava", "corrected")]
    public async Task Reconcile_keeps_amends_or_undoes_a_reaction(string brainSays, string verdict)
    {
        var (h, c, _) = Host(brain: r => new LlmResponse([new LlmText(brainSays)], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5"));
        h.Runtime.Options.Brainstem.Character.InstantLines = false; // the brain answers (this measures the reaction's verdict)
        var r = await SaveAndBind(h, "thanks_bow", "// bow when thanked\ncharacter.gesture('bow', args.player)", new { on = "thanks", kind = "reaction" });
        Assert.StartsWith("ok", r);
        await Chat(h, c, "thanks!");
        var b = ScriptTools.Of(h.Runtime)!.Library.Get("thanks_bow")!.Script!.Binding!;
        Assert.True(b.Fires + b.Skipped >= 1, $"fires {b.Fires} skipped {b.Skipped}");
        Assert.Equal(1, verdict switch { "kept" => b.Kept, "amended" => b.Amended, _ => b.Corrected });
        h.Dispose();
    }

    [Fact]
    public async Task A_reaction_corrected_twice_is_demoted()
    {
        var (h, _, _) = Host();
        var r = await SaveAndBind(h, "thanks_bow", "character.gesture('bow', args.player)", new { on = "thanks", kind = "reaction" });
        Assert.StartsWith("ok", r);
        var runner = h.Body.Bindings!;
        runner.Verdict("thanks_bow", "kept");
        runner.Verdict("thanks_bow", "corrected");
        Assert.True(ScriptTools.Of(h.Runtime)!.Library.Get("thanks_bow")!.Script!.Binding!.Enabled);
        runner.Verdict("thanks_bow", "corrected");
        var b = ScriptTools.Of(h.Runtime)!.Library.Get("thanks_bow")!.Script!.Binding!;
        Assert.False(b.Enabled);
        Assert.StartsWith("demoted", b.OffNote);
        h.Dispose();
    }
}
