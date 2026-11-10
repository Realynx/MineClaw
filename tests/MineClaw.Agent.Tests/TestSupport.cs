using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Tests;

/// <summary>Scripted ILlmClient: each call pops the next handler; records every request.</summary>
public sealed class FakeLlm : ILlmClient
{
    private readonly ConcurrentQueue<Func<LlmRequest, CancellationToken, Task<LlmResponse>>> _script = new();
    public List<LlmRequest> Requests { get; } = new();
    public LlmUsage UsagePerCall { get; set; } = new(100, 20, 1000, 0);
    public Func<LlmRequest, LlmResponse>? Fallback { get; set; }

    public FakeLlm Then(Func<LlmRequest, LlmResponse> handler) { _script.Enqueue((r, _) => Task.FromResult(handler(r))); return this; }

    /// <summary>Async handler with the call's token (controllable timing; cancellation = an interrupted call).</summary>
    public FakeLlm ThenAsync(Func<LlmRequest, CancellationToken, Task<LlmResponse>> handler) { _script.Enqueue(handler); return this; }

    /// <summary>Raised when a call starts (tests post chat "while the model is generating").</summary>
    public event Action<LlmRequest>? CallStarted;

    public FakeLlm ThenText(string text) => Then(r => Resp(r, StopReasons.EndTurn, new LlmText(text)));

    public FakeLlm ThenTool(string name, object input, string? text = null, string id = "tu_1") => Then(r =>
    {
        var blocks = new List<LlmBlock>();
        if (text != null) blocks.Add(new LlmText(text));
        blocks.Add(new LlmToolUse(id, name, JsonSerializer.SerializeToElement(input)));
        return Resp(r, StopReasons.ToolUse, blocks.ToArray());
    });

    public LlmResponse Resp(LlmRequest r, string stop, params LlmBlock[] blocks) =>
        new(blocks, stop, UsagePerCall, r.Model ?? (r.Tier == ModelTier.Cheap ? "claude-haiku-5-5" : "claude-sonnet-5-5"));

    /// <summary>
    /// Two-phase reply calls (quick ack / cheap-only triage, Purpose "quick_*") never touch the script or <see cref="Requests"/>:
    /// they go here (default: an empty answer = no ack, the brain answers) and are recorded in <see cref="QuickRequests"/>.
    /// </summary>
    public Func<LlmRequest, CancellationToken, Task<LlmResponse>>? QuickHandler { get; set; }
    public List<LlmRequest> QuickRequests { get; } = new();

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (request.Purpose.StartsWith("quick_", StringComparison.Ordinal))
        {
            lock (QuickRequests) QuickRequests.Add(request);
            return QuickHandler?.Invoke(request, ct) ?? Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, "claude-haiku-5-5"));
        }
        lock (Requests) Requests.Add(request);
        CallStarted?.Invoke(request);
        if (_script.TryDequeue(out var h)) return h(request, ct);
        var fb = Fallback ?? throw new InvalidOperationException("FakeLlm: no scripted response left");
        return Task.FromResult(fb(request));
    }
}

/// <summary>TimeProvider with a settable clock (timers still run on real time).</summary>
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mineclaw-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

/// <summary>The test server's regulars and their names, set the way a deployment configures them (the code defaults name nobody).</summary>
public static class TestPlayers
{
    public static Dictionary<string, string[]> Nicknames() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["poofimafox"] = ["fox", "foxy", "master"],
        ["kiwi"] = ["robin"],
    };

    /// <summary>Robin's own bot (a foreign bot on the test server).</summary>
    public static string[] OtherBots() => ["robinchan"];

    /// <summary>The regulars' nicknames and the foreign bot on these chat options.</summary>
    public static Agent.Chat.ChatOptions Regulars(this Agent.Chat.ChatOptions o)
    {
        o.PlayerNicknames = Nicknames();
        o.OtherBots = OtherBots();
        return o;
    }

    /// <summary>Claim lines' short names ("fox says: ...").</summary>
    public static Agent.Rag.Beliefs.BeliefOptions Beliefs() => new()
    {
        Nicknames = new(StringComparer.OrdinalIgnoreCase) { ["poofimafox"] = "fox", ["kiwi"] = "robin" },
    };
}

public static class TestRuntime
{
    public static (AgentRuntime Rt, FakeLlm Llm, List<string> Sent) Create(Action<AgentOptions>? configure = null, TimeProvider? time = null,
        MineClaw.Agent.Rag.IEmbeddingProvider? embeddings = null, Func<MineClaw.Agent.Memory.BlockPos?>? position = null)
    {
        var options = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        options.Chat.Username = "Clawd_bot";
        options.Chat.DisplayName = "Clawd";
        options.Chat.Aliases = [];
        options.Chat.LineDelayMs = 0;
        options.Events.CoalesceWindowMs = 10;
        options.Events.MaxBatchDelayMs = 50;
        options.ChatDebounce.QuietMs = 10;
        options.ChatDebounce.MaxMs = 50;
        options.ChatDebounce.ReplyQuietMs = 0;
        options.Chat.Regulars();
        configure?.Invoke(options);
        var llm = new FakeLlm();
        var sent = new List<string>();
        var rt = AgentRuntime.Create(options, (line, _) => { lock (sent) sent.Add(line); return Task.CompletedTask; },
            llm, position: position, persist: false, time: time, embeddings: embeddings);
        return (rt, llm, sent);
    }
}

/// <summary>Await a condition instead of sleeping a fixed time (deterministic under full-suite load).</summary>
public static class TestWaits
{
    public static async Task UntilAsync(Func<bool> cond, int ms = 20000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met within " + ms + " ms");
            await Task.Delay(5);
        }
    }
}
