using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the cheap-only reply path (WP06 refactor of <c>BotAgent.TryQuickAsync</c>): for scripted small talk on a fixed clock,
/// the exact quick-reply request payloads (and any brain requests that follow), what went to chat, the outcome and the
/// committed episodes. Hashed (line-ending independent); on a mismatch the dump lands in the temp dir.
/// </summary>
public class QuickReplyGoldenTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["final"] = "2C46900D874F8ECC428ADD84DBA5A96B2EDDF1E6715738DF28809872B6F87090",
        ["needs_brain"] = "DD44F55CBDAC54783BD660695F195073AE15DDBFA64BEF02343A5DD88E66BD56",
        ["lint_fail"] = "E412FF5AC83A05776606D54F5A1F7D3A2C1B23474079B04A3536298EB9E68C42",
        ["correction"] = "85AC2B0108F94D1BE04BA3D64617946B9883B3D7CB4EF877CCCB8A56644C3475",
        ["speech_gate"] = "CF8DF458E1329C4C2DC9213D656DA37D77CBFF9FC3B880E2946917251B68E438",
        ["whisper_emote"] = "BE9FD13ADADB43CD3DA06CF1F02CBA4163D9861C858DF7CA1151232548CEB4F5",
        ["skips"] = "B4D15034678B3C36B655CB5E6CC9026A0E8DC1A4D5DE97606F42F937F42D8031",
        ["call_failed"] = "4E6C883D8AFF493FD447F1D36D345BC32AE4FFAAA990F4FCCA4FE3C7FB61CE09",
        ["cheap_off"] = "76490228751AA4E3F113DD0216289A53D7617B09654B0B7B8E01D0BC90690081",
    };

    private static LlmResponse Haiku(string text) => new([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(60, 6, 0, 0), "claude-haiku-5-5");

    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Create(string quick, Action<AgentOptions>? configure = null)
    {
        var (rt, llm, sent) = TestRuntime.Create(o =>
        {
            o.Llm.ApiKey = "sk-test-not-real";
            o.Brainstem.Reply.QuickAck = false;
            o.Brainstem.Reply.AckAfterMs = 60_000;
            configure?.Invoke(o);
        }, time: new ManualTime());
        llm.QuickHandler = (r, _) => Task.FromResult(Haiku(quick));
        rt.Agent.StateHeader = () => "pos 1,64,2 hp 20/20 food 20 held air";
        return (rt, llm, sent);
    }

    private static string Dump(AgentRuntime rt, FakeLlm llm, List<string> sent, IEnumerable<EpisodeOutcome> outcomes)
    {
        var client = new AnthropicLlmClient(rt.Options.Llm);
        var sb = new StringBuilder();
        void Req(string tag, LlmRequest r)
        {
            sb.Append("== ").Append(tag).Append(" tier=").Append(r.Tier).Append(" purpose=").Append(r.Purpose).Append(" maxTokens=")
              .Append(r.MaxTokens?.ToString() ?? "-").Append(" cacheHistory=").Append(r.CacheHistory).Append('\n');
            sb.Append(JsonSerializer.Serialize(client.BuildParams(r, r.Model ?? rt.Options.Llm.ModelFor(r.Tier)))).Append('\n');
        }
        lock (llm.QuickRequests) foreach (var r in llm.QuickRequests) Req("quick", r);
        lock (llm.Requests) foreach (var r in llm.Requests) Req("brain", r);
        lock (sent) foreach (var s in sent) sb.Append("> ").Append(s).Append('\n');
        foreach (var o in outcomes)
            sb.Append("== outcome ").Append(o.Tier?.ToString() ?? "-").Append(' ').Append(o.Calls).Append(' ').Append(o.Usage)
              .Append(' ').Append(o.CostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(' ').Append(string.Join(" | ", o.Said)).Append('\n');
        foreach (var ep in rt.Context.Episodes)
        {
            sb.Append("== episode ").Append(ep.Tier).Append(' ').Append(ep.IsTest).Append(' ').Append(ep.Events.Count).Append('\n');
            foreach (var m in ep.Messages) sb.Append(m.Role).Append(": ").Append(m.Text).Append('\n');
        }
        sb.Append("== cheap ").Append(rt.Latency.CheapCounts()).Append('\n');
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\r\\n", "\\n", StringComparison.Ordinal);
    }

    private static void Check(string name, string dump)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden", "quick-replies");
        if (!Expected.TryGetValue(name, out var want) || want != hash)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), dump);
        }
        Assert.True(Expected.TryGetValue(name, out want), $"no golden for {name}: [\"{name}\"] = \"{hash}\",");
        Assert.True(want == hash, $"{name} payload changed: {hash} (dump in {dir})");
    }

    private static EventBatch Chat(string text, string player = "kiwi") => new([GameEvent.Chat(player, text)]);

    [Fact]
    public async Task Small_talk_answered_by_the_cheap_tier()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"haiii robin~"}""");
        using var _ = rt;
        var o = await rt.Agent.HandleBatchAsync(Chat("hi clawd"), default);
        Check("final", Dump(rt, llm, sent, [o]));
    }

    [Fact]
    public async Task Needs_brain_hands_its_ack_to_the_brain()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":true,"say":"ooh which one?"}""");
        using var _ = rt;
        llm.ThenText("you're the cutest robin on the island");
        var o = await rt.Agent.HandleBatchAsync(Chat("clawd good girl"), default);
        Check("needs_brain", Dump(rt, llm, sent, [o]));
    }

    [Fact]
    public async Task A_final_failing_the_lint_goes_to_the_brain()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"hai! i'll bring you 5 logs"}""");
        using var _ = rt;
        llm.ThenText("haii robin");
        var o = await rt.Agent.HandleBatchAsync(Chat("hey clawd"), default);
        Check("lint_fail", Dump(rt, llm, sent, [o]));
    }

    [Fact]
    public async Task A_correction_escalates_with_a_prelude()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"haii, just chilling~"}""");
        using var _ = rt;
        var o1 = await rt.Agent.HandleBatchAsync(Chat("what are you doing clawd"), default);
        llm.ThenText("oh! sorry robin, omw to the chest");
        var o2 = await rt.Agent.HandleBatchAsync(Chat("no, i meant what are you doing with the chest"), default);
        llm.ThenText("hi fox");
        var o3 = await rt.Agent.HandleBatchAsync(Chat("hi clawd", "fox"), default);
        Check("correction", Dump(rt, llm, sent, [o1, o2, o3]));
    }

    [Fact]
    public async Task The_speech_gate_and_meta_speech_shape_the_cheap_line()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"haiii kiwi, how are you~"}""");
        using var _ = rt;
        var muted = new List<string>();
        rt.Agent.Muted += t => muted.Add(t);
        rt.Agent.SpeechGate = (_, text, _) => text.Contains("kiwi", StringComparison.Ordinal) ? text.Replace("haiii", "hewwo", StringComparison.Ordinal) : null;
        var o1 = await rt.Agent.HandleBatchAsync(Chat("hi clawd"), default);
        llm.QuickHandler = (_, _) => Task.FromResult(Haiku("""{"needs_brain":false,"say":"nyaa~"}"""));
        var o2 = await rt.Agent.HandleBatchAsync(Chat("hey clawd", "fox"), default);
        Check("speech_gate", Dump(rt, llm, sent, [o1, o2]) + "muted: " + string.Join(" | ", muted));
    }

    [Fact]
    public async Task A_whisper_and_an_emote()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"hehe hi~"}""");
        using var _ = rt;
        var o1 = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Whisper("fox", "hi clawd")]), default);
        var o2 = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Emote("kiwi", "waves at clawd")]), default);
        Check("whisper_emote", Dump(rt, llm, sent, [o1, o2]));
    }

    [Fact]
    public async Task Cases_that_skip_the_cheap_tier()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"hi"}""");
        using var _ = rt;
        llm.ThenText("brain 1").ThenText("brain 2").ThenText("brain 3").ThenText("brain 4");
        var o1 = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("kiwi", "hi clawd") with { AddressConfidence = 0.1 }]), default);
        var o2 = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("kiwi", "hi clawd"), GameEvent.LowHealth(4)]), default);
        var o3 = await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("kiwi", "hi clawd"), GameEvent.TaskDone("mined 8 dirt")]), default);
        var o4 = await rt.Agent.HandleBatchAsync(Chat("clawd where is the chest?"), default);
        Check("skips", Dump(rt, llm, sent, [o1, o2, o3, o4]));
    }

    [Fact]
    public async Task A_failed_cheap_call_falls_back_to_the_brain()
    {
        var (rt, llm, sent) = Create("x");
        using var _ = rt;
        llm.QuickHandler = (_, _) => throw new InvalidOperationException("overloaded");
        llm.ThenText("hi robin");
        var o = await rt.Agent.HandleBatchAsync(Chat("hi clawd"), default);
        Check("call_failed", Dump(rt, llm, sent, [o]));
    }

    [Fact]
    public async Task Cheap_only_is_off_in_saver_modes()
    {
        var (rt, llm, sent) = Create("""{"needs_brain":false,"say":"hi"}""", o => o.Brainstem.Reply.CheapOnly = false);
        using var _ = rt;
        llm.ThenText("hi from the brain");
        var o = await rt.Agent.HandleBatchAsync(Chat("hi clawd"), default);
        Check("cheap_off", Dump(rt, llm, sent, [o]));
    }
}
