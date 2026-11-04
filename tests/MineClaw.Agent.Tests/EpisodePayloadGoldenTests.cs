using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins the exact provider payloads of the brain loop (WP06 refactor of <c>BotAgent.RunEpisodeAsync</c> /
/// <c>BuildTurnTextAsync</c>): for scripted episodes on a fixed clock, every request the Anthropic client would send (system
/// blocks, tools in order, messages in order, cache_control placements, effort/thinking) plus what went to chat. The dumps are
/// hashed (line-ending independent) against <see cref="Expected"/>; on a mismatch the dump lands in the temp dir for diffing.
/// Prompt bytes are a cost contract (prompt caching): a refactor must leave every hash unchanged.
/// </summary>
public class EpisodePayloadGoldenTests
{
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["tool_then_text"] = "C2221DC9C410C61705935070A292D0AAE3303ADFFA37B745808CA4C84AF3875A",
        ["cheap_escalate"] = "DD186163562F17A539393165E6F6E10F4CFF57F18AA2E5ACC871FFC8E2AE17B8",
        ["silent"] = "875CD6A410EE9250EEA68AD7A5176E63F540CDA76F4B6F1875286056137DD08E",
        ["quiet_task"] = "7C7FB1E7DC60D433821BD7E6906AC8213257979A9D979C109536E9AF3141256B",
        ["max_tokens"] = "64C49A45EC846F77B8665B8A8101421CF06F6A1255B3081A4000EE01719C4776",
        ["refusal"] = "780BABA8C470699408AB49EBDD35BC7FD0A3EBB7E3ABD73AA7B3B8DC2FC6F5F8",
        ["midturn_inject"] = "2DBC4B310B1444485E86BDD685003FB024F8F71129FFCB6083CC523C5F3FE5CC",
        ["danger"] = "C445F6E7C5B1121797F95E5D31317D665E87DAA28DFF39ACE6AEA92378AFA692",
        ["two_rounds"] = "39A45CACFAA6AFC8027F8D1841926D845BC582829FC7F2F3E43A1736CC8F2EF4",
        ["restart_cut"] = "5A4B191C1AC8F5C64AF7B9EEE66ACD37D7FF4B798659A3BDB1C5D449ADEC54FC",
        ["restart_pending"] = "49E6715BA78E5D538F2980252288C361C365E9EB7E522F9294F706518735A984",
        ["hooks"] = "B7618F3D49496EF1A1283F9B22913BB770BB657A68B84796CE612C612F512B1D",
    };

    private static EventBatch Chat(string player, string text) => new([GameEvent.Chat(player, text)]);

    private static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Create(Action<AgentOptions>? configure = null) =>
        TestRuntime.Create(o => { o.Llm.ApiKey = "sk-test-not-real"; configure?.Invoke(o); }, time: new ManualTime());

    private static string Dump(AgentRuntime rt, FakeLlm llm, List<string> sent)
    {
        var client = new AnthropicLlmClient(rt.Options.Llm);
        var sb = new StringBuilder();
        List<LlmRequest> reqs;
        lock (llm.Requests) reqs = [.. llm.Requests];
        foreach (var r in reqs)
        {
            sb.Append("== request tier=").Append(r.Tier).Append(" model=").Append(r.Model ?? "-").Append(" purpose=").Append(r.Purpose)
              .Append(" interruptible=").Append(r.Interruptible).Append(" effort=").Append(r.Effort ?? "-").Append(" thinking=").Append(r.Thinking ?? "-")
              .Append(" cacheHistory=").Append(r.CacheHistory).Append(" maxTokens=").Append(r.MaxTokens?.ToString() ?? "-")
              .Append(" stream=").Append(r.OnTextDelta != null).Append(" early=").Append(r.OnToolUse != null).Append('\n');
            sb.Append(JsonSerializer.Serialize(client.BuildParams(r, r.Model ?? rt.Options.Llm.ModelFor(r.Tier)))).Append('\n');
        }
        lock (llm.QuickRequests) sb.Append("== quick requests ").Append(llm.QuickRequests.Count).Append('\n');
        lock (sent) foreach (var s in sent) sb.Append("> ").Append(s).Append('\n');
        foreach (var ep in rt.Context.Episodes)
        {
            sb.Append("== episode ").Append(ep.Tier).Append(' ').Append(ep.Messages.Count).Append('\n');
            foreach (var m in ep.Messages)
                sb.Append(m.Role).Append(": ").Append(JsonSerializer.Serialize(m.Content.Select(Block))).Append('\n');
        }
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\\r\\n", "\\n", StringComparison.Ordinal);
    }

    private static string Block(LlmBlock b) => b switch
    {
        LlmText t => "text:" + t.Text,
        LlmToolUse u => $"use:{u.Id}:{u.Name}:{u.Input.GetRawText()}",
        LlmToolResult r => $"result:{r.ToolUseId}:{r.IsError}:{r.Content}",
        _ => b.GetType().Name,
    };

    private static void Check(string name, string dump)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden", "episode-payloads");
        if (!Expected.TryGetValue(name, out var want) || want != hash)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), dump);
        }
        Assert.True(Expected.TryGetValue(name, out want), $"no golden for {name}: [\"{name}\"] = \"{hash}\",");
        Assert.True(want == hash, $"{name} payload changed: {hash} (dump in {dir})");
    }

    private static void RegisterCount(AgentRuntime rt) =>
        rt.Tools.Register("count_blocks", "Count blocks.", ToolSchema.Create().Str("block").Build(),
            (input, _) => Task.FromResult(ToolResult.Success($"12 {input.Str("block")}")));

    [Fact]
    public async Task Tool_round_then_text_then_a_second_turn()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        RegisterCount(rt);
        rt.Agent.StateHeader = () => "pos 1,64,2 hp 20/20 food 20 held air";
        llm.ThenTool("count_blocks", new { block = "cobblestone" }, text: "lemme check", id: "tu_a")
           .ThenText("you've got 12 cobble!")
           .ThenText("np fox");
        await rt.Agent.HandleBatchAsync(Chat("Alice", "clawd can you mine some cobble?"), default);
        await rt.Agent.HandleBatchAsync(Chat("Alice", "thanks clawd"), default);
        Check("tool_then_text", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Cheap_tier_escalates_to_main()
    {
        var (rt, llm, sent) = Create(o => o.Triage.CheapFirstForChat = true);
        using var _ = rt;
        llm.ThenTool(BotAgent.EscalateToolName, new { reason = "needs a plan" }, text: "ooh ok one sec")
           .ThenText("plan: build a cobble gen next to the tree");
        await rt.Agent.HandleBatchAsync(Chat("Bob", "clawd what should we do about the island"), default);
        Check("cheap_escalate", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Silent_reply()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        llm.Then(r => llm.Resp(r, StopReasons.EndTurn));
        await rt.Agent.HandleBatchAsync(Chat("Bob", "lol"), default);
        Check("silent", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Quiet_task_turn_keeps_its_text_internal()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        llm.ThenText("I finished mining the cobble and put it in the chest.");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.TaskDone("mined 12 cobblestone")]), default);
        Check("quiet_task", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Max_tokens_truncates_the_tool_batch()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        RegisterCount(rt);
        llm.Then(r => llm.Resp(r, StopReasons.MaxTokens,
            new LlmText("counting"), new LlmToolUse("t1", "count_blocks", JsonSerializer.SerializeToElement(new { block = "dirt" }))));
        await rt.Agent.HandleBatchAsync(Chat("fox", "clawd count the dirt"), default);
        Check("max_tokens", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Refusal_ends_the_turn()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        llm.Then(r => llm.Resp(r, StopReasons.Refusal, new LlmText("no")));
        await rt.Agent.HandleBatchAsync(Chat("fox", "clawd do something bad"), default);
        Check("refusal", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Mid_turn_chat_is_injected_after_the_tool_results()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        rt.Tools.Register("look", "Look around.", ToolSchema.Empty, (_, _) =>
        {
            rt.Events.Post(GameEvent.Chat("kiwi", "clawd bring me 10 sand too"));
            return Task.FromResult(ToolResult.Success("sand at 3,60,4"));
        });
        llm.ThenTool("look", new { }, id: "tu_l")
           .ThenText("ok fox, and sand for robin");
        await rt.Agent.HandleBatchAsync(Chat("fox", "clawd where is the sand"), default);
        Check("midturn_inject", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Danger_turn_says_only_the_warning()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        llm.ThenText("ouch, my hp is low! I'm also heading back to the cobble generator now and I'll keep mining.");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.LowHealth(5, cause: "zombie")]), default);
        Check("danger", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Two_tool_rounds_hold_the_later_line()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        RegisterCount(rt);
        llm.ThenTool("count_blocks", new { block = "sand" }, text: "checking", id: "tu_1")
           .ThenTool("count_blocks", new { block = "glass" }, text: "and the glass too", id: "tu_2")
           .ThenText("12 sand and 12 glass")
           .ThenText("12 cobble too");
        await rt.Agent.HandleBatchAsync(Chat("fox", "clawd how much sand and glass do we have"), default);
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Whisper("fox", "and cobble?")]), default);
        Check("two_rounds", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task A_cut_first_call_restarts_with_every_line()
    {
        var (rt, llm, sent) = Create(o => { o.ChatDebounce.QuietMs = 10; o.ChatDebounce.MaxMs = 200; });
        using var _ = rt;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        llm.ThenAsync(async (r, ct) =>
        {
            firstStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new LlmInterruptedException(new LlmUsage(12, 9, 1500, 0), "claude-opus-5-5"); }
            throw new InvalidOperationException("unreachable");
        });
        llm.ThenText("left cell it is");
        var turn = rt.Agent.HandleBatchAsync(Chat("fox", "mine the gen"), default);
        await firstStarted.Task;
        rt.Events.Post(GameEvent.Chat("fox", "actually the left cell"));
        await turn;
        Check("restart_cut", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Pending_chat_before_the_first_call_restarts_without_calling()
    {
        var (rt, llm, sent) = Create(o => { o.ChatDebounce.QuietMs = 10; o.ChatDebounce.MaxMs = 200; });
        using var _ = rt;
        llm.ThenText("ok");
        rt.Events.Post(GameEvent.Chat("fox", "and some sand"));
        await rt.Agent.HandleBatchAsync(Chat("fox", "get me dirt"), default);
        Check("restart_pending", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task Every_turn_head_hook_and_the_speech_gate()
    {
        var (rt, llm, sent) = Create();
        using var _ = rt;
        RegisterCount(rt);
        var a = rt.Agent;
        a.StateHeader = () => "[pos 1,64,2 hp 20/20 food 18 held stone_pickaxe | task: mining cobble";
        a.TurnPerception = () => "[see] a creeper 6 blocks east  ";
        a.TurnHint = b => b.HasTypedChat ? "[hint] fox usually means the west gen" : null;
        a.StandingRules = ctx => ctx.Contains("cobble", StringComparison.Ordinal) ? "[rules] fox: never mine the gen floor" : null;
        a.TurnDigest = _ => "[since] kiwi left; 3 cobble mined";
        a.BusyDetail = () => "mining cobble (12/64)";
        a.HereBlock = _ => "[here] gen west cell; facing north | inv: 12 cobblestone; stone_pickaxe";
        a.SpeechGate = (_, text, _) => text.Replace("cobble", "cobblestone", StringComparison.Ordinal);
        llm.ThenTool("count_blocks", new { block = "cobblestone" }, id: "tu_h")
           .ThenText("12 cobble so far, fox");
        llm.Fallback = r => llm.Resp(r, StopReasons.EndTurn, new LlmText("hey kiwi"));
        await a.HandleBatchAsync(Chat("fox", "clawd how much cobble now?"), default);
        await a.HandleBatchAsync(new EventBatch([GameEvent.Joined("kiwi")]), default);
        Check("hooks", Dump(rt, llm, sent));
    }

    [Fact]
    public async Task The_dump_is_stable_across_runs()
    {
        async Task<string> Once()
        {
            var (rt, llm, sent) = Create();
            using var _ = rt;
            RegisterCount(rt);
            llm.ThenTool("count_blocks", new { block = "cobblestone" }, id: "tu_a").ThenText("12");
            await rt.Agent.HandleBatchAsync(Chat("fox", "clawd count cobble"), default);
            return Dump(rt, llm, sent);
        }
        Assert.Equal(await Once(), await Once());
    }
}
