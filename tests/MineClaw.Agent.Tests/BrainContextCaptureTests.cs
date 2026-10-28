using System.Diagnostics;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Xunit.Abstractions;

namespace MineClaw.Agent.Tests;

/// <summary>The brain context capture behind the Explorer's Controller panel (BrainContextCapture).</summary>
public class BrainContextCaptureTests(ITestOutputHelper output)
{
    private const string ApiKey = "sk-ant-api03-ZZtopSecretKeyValue0123456789";

    /// <summary>Records every response the client returned, between telemetry and the fake (the capture must hold these objects).</summary>
    private sealed class Recorder(ILlmClient inner) : ILlmClient
    {
        public List<(LlmRequest Request, LlmResponse Response)> Calls { get; } = new();

        public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            var r = await inner.CompleteAsync(request, ct);
            lock (Calls) Calls.Add((request, r));
            return r;
        }
    }

    private static (AgentRuntime Rt, FakeLlm Llm, Recorder Rec, PromptTelemetry Tel) Runtime(BrainCaptureOptions? capture = null)
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
        options.Brainstem.Reply.QuickAck = false;
        options.Brainstem.Reply.CheapOnly = false;
        options.Llm.ApiKey = ApiKey;
        var fake = new FakeLlm();
        var rec = new Recorder(fake);
        var tel = new PromptTelemetry(options, new TelemetryOptions { Capture = capture ?? new BrainCaptureOptions() });
        var rt = AgentRuntime.Create(options, (_, _) => Task.CompletedTask, tel.Wrap(rec), persist: false);
        tel.Attach(rt);
        return (rt, fake, rec, tel);
    }

    private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    [Fact]
    public async Task Capture_keeps_the_exact_request_and_response_of_every_brain_call()
    {
        var (rt, llm, rec, tel) = Runtime();
        rt.Agent.StateHeader = () => "pos 1,64,2 hp 20/20 food 20 held air";
        rt.Tools.Register("count_blocks", "Count blocks.", ToolSchema.Create().Str("block").Build(),
            (input, _) => Task.FromResult(ToolResult.Success($"12 {input.Str("block")}")));
        llm.ThenTool("count_blocks", new { block = "cobblestone" }, text: "lemme check", id: "tu_a").ThenText("you've got 12 cobble!");

        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "clawd can you count the cobble?")]), default);

        var brain = tel.Capture.Calls().Where(c => c.Lane == BrainContextCapture.Lanes.Brain).ToList();
        var sent = rec.Calls.Where(c => c.Request.Purpose is "task" or "chat").ToList();
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent.Count, brain.Count);
        for (int i = 0; i < sent.Count; i++)
        {
            // the request object the brain built, its message list as it left, and the parsed response: the same objects
            Assert.Same(sent[i].Request, brain[i].Request);
            Assert.Equal(sent[i].Request.Messages.Count, brain[i].Messages.Length);
            for (int m = 0; m < brain[i].Messages.Length; m++) Assert.Same(sent[i].Request.Messages[m], brain[i].Messages[m]);
            Assert.Equal(sent[i].Request.System, brain[i].System);
            Assert.Same(sent[i].Response, brain[i].Response);
            Assert.NotNull(brain[i].Record);
        }
        Assert.Equal(1, tel.Capture.TurnCount(BrainContextCapture.Lanes.Brain)); // both rounds are one turn

        // The window = the last request as sent + its response; the tool call's input and result are in it, the turn input labelled.
        var page = tel.Capture.Page(0);
        Assert.NotNull(page.Window);
        var w = page.Window!;
        Assert.Equal(brain[1].Seq, w.CallSeq);
        Assert.Equal(sent[1].Request.Messages.Count + 1, w.Messages.Count);
        Assert.Equal("response", w.Messages[^1].Tag);
        Assert.Equal("you've got 12 cobble!", w.Messages[^1].Blocks.Single().Text);
        var use = w.Messages.SelectMany(m => m.Blocks).Single(b => b.Kind == "tool_use");
        Assert.Equal("count_blocks", use.Name);
        Assert.Contains("cobblestone", use.Input);
        Assert.Contains(w.Messages.SelectMany(m => m.Blocks), b => b.Kind == "tool_result" && b.Text == "12 cobblestone" && b.Id == "tu_a");
        var input = w.Messages.Single(m => m.Tag == "turn");
        Assert.Contains(input.Blocks, b => b.Label == "state" && b.Text!.Contains("hp 20/20"));
        Assert.Contains(input.Blocks, b => b.Label == "chat" && b.Text!.Contains("count the cobble"));
        Assert.Equal(sent[1].Request.System.Count, w.System.Count);
        Assert.Equal(sent[1].Request.System[0].Text, w.System[0].Text); // nothing re-rendered: the text as sent (no secrets in it)
        Assert.True(w.PromptTokens > 0 && w.Limit == 100_000);

        // The turn in full: call 1 added the turn input, call 2 the tool result; usage and the stop reasons per call.
        var detail = tel.Capture.Turn(page.Turns.Single(t => t.Lane == "brain").Id)!;
        Assert.Equal(2, detail.Calls.Count);
        Assert.Equal("turn", detail.Calls[0].Added[0].Tag);
        Assert.Equal(StopReasons.ToolUse, detail.Calls[0].StopReason);
        Assert.Contains(detail.Calls[1].Added.SelectMany(m => m.Blocks), b => b.Kind == "tool_result" && b.Text == "12 cobblestone");
        Assert.DoesNotContain(detail.Calls[1].Added, m => m.Tag == "turn");
        Assert.All(detail.Calls, c => Assert.True(c.Usage!.In > 0 && c.Usage.Cost > 0));
        Assert.Equal(["count_blocks"], page.Turns.Single(t => t.Lane == "brain").Tools);

        // Nothing changed since: an empty page.
        var again = tel.Capture.Page(page.Version);
        Assert.Empty(again.Turns);
        Assert.Null(again.Window);
    }

    [Fact]
    public void Secrets_are_redacted_and_keys_never_appear()
    {
        var cap = new BrainContextCapture();
        cap.AddSecrets([ApiKey, "hunter2-supervisor-token"]);
        var req = new LlmRequest
        {
            Tier = ModelTier.Main, Purpose = "task",
            System = [new LlmSystemBlock($"you are a bot. key {ApiKey}", Cache: true)],
            Messages =
            [
                LlmMessage.User("[state] hp 20\nAlice: my bearer abcdefghijklmnopqrstuvwxyz123 and hunter2-supervisor-token"),
                LlmMessage.Assistant(new LlmToolUse("t1", "remember", JsonSerializer.SerializeToElement(new { text = "ghp_abcdefghijklmnopqrstuvwxyz0123" }))),
                LlmMessage.User(new LlmToolResult("t1", $"saved {ApiKey}")),
            ],
        };
        var call = cap.Begin(req, null);
        cap.End(call, new LlmResponse([new LlmThinking($"the key is {ApiKey}", "sig"), new LlmText("ok")], StopReasons.EndTurn, new LlmUsage(1, 1, 0, 0), "claude-opus-5-5"), null, null);

        var page = Json(cap.Page(0));
        var turn = Json(cap.Turn(1)!);
        foreach (var text in new[] { page, turn })
        {
            Assert.DoesNotContain("ZZtopSecretKeyValue", text);
            Assert.DoesNotContain("hunter2-supervisor-token", text);
            Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz123", text);
            Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz0123", text);
            Assert.Contains("[redacted]", text);
        }
    }

    [Fact]
    public void Ring_keeps_the_last_turns_and_reports_evictions()
    {
        var cap = new BrainContextCapture(new BrainCaptureOptions { Turns = 3, CallsPerTurn = 4 });
        long v0 = 0;
        for (int i = 1; i <= 5; i++)
        {
            var c = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User($"turn {i}")] }, null);
            cap.End(c, new LlmResponse([new LlmText("ok")], StopReasons.EndTurn, LlmUsage.Zero, "m"), null, null);
            if (i == 3) v0 = cap.Page(0).Version;
        }
        Assert.Equal(3, cap.TurnCount("brain"));
        var page = cap.Page(0);
        Assert.Equal([3L, 4L, 5L], page.Turns.Select(t => t.Id));
        var since = cap.Page(v0);
        Assert.Equal([1L, 2L], since.Evicted.Order());
        Assert.Equal([4L, 5L], since.Turns.Select(t => t.Id));
        Assert.Null(cap.Turn(1));

        // Calls per turn: one long worker run keeps its first call and the newest ones.
        var first = LlmMessage.User("task: chop 20 logs");
        var history = new List<LlmMessage> { first };
        for (int r = 0; r < 10; r++)
        {
            var c = cap.Begin(new LlmRequest { Tier = ModelTier.Worker, Purpose = "worker", Messages = history }, null);
            cap.End(c, new LlmResponse([new LlmToolUse("t" + r, "chop", JsonSerializer.SerializeToElement(new { }))], StopReasons.ToolUse, LlmUsage.Zero, "w"), null, null);
            history.Add(LlmMessage.Assistant(new LlmToolUse("t" + r, "chop", JsonSerializer.SerializeToElement(new { }))));
            history.Add(LlmMessage.User(new LlmToolResult("t" + r, "ok")));
        }
        var worker = cap.Page(0).Turns.Single(t => t.Lane == "worker");
        Assert.Equal(10, worker.Calls);
        Assert.Equal(6, worker.DroppedCalls);
        Assert.Equal(4, cap.Turn(worker.Id)!.Calls.Count);
        Assert.Equal(0, cap.Turn(worker.Id)!.Calls[0].Round);
    }

    [Fact]
    public void Thinking_redacted_thinking_and_reasoning_come_back_labelled()
    {
        var cap = new BrainContextCapture();
        var c = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User("hi")] }, null);
        Assert.True(cap.Page(0).InFlight is { Lane: "brain" }); // "now thinking…"
        cap.End(c, new LlmResponse([new LlmThinking("Alice wants cobble; count first.", "sig123"), new LlmRedactedThinking("ENCRYPTEDBLOB=="),
            new LlmThinking("", "sigonly"), new LlmText("on it")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5") { Reasoning = "deepseek: let me think" }, null, null);
        var page = cap.Page(0);
        Assert.Null(page.InFlight);
        Assert.True(page.Turns.Single().HasThinking);
        var blocks = page.Window!.Messages[^1].Blocks;
        Assert.Equal(["reasoning", "thinking", "redacted_thinking", "thinking", "text"], blocks.Select(b => b.Kind));
        Assert.Equal(BrainContextCapture.ReasoningLabel, blocks[0].Label);
        Assert.Equal("deepseek: let me think", blocks[0].Text);
        Assert.Equal(BrainContextCapture.ThinkingLabel, blocks[1].Label);
        Assert.Equal("Alice wants cobble; count first.", blocks[1].Text);
        Assert.Equal(BrainContextCapture.RedactedThinkingLabel, blocks[2].Label);
        Assert.Null(blocks[2].Text); // the encrypted payload is never sent, only its size
        Assert.Equal(15, blocks[2].Bytes);
        Assert.Equal(BrainContextCapture.ThinkingOmittedLabel, blocks[3].Label);
    }

    [Fact]
    public void Subhive_filter_marks_the_turns_and_tool_calls_that_targeted_the_body()
    {
        var cap = new BrainContextCapture();
        LlmToolUse Use(string id, string name, object input) => new(id, name, JsonSerializer.SerializeToElement(input));
        void Turn(string text, string? body, params LlmToolUse[] uses)
        {
            var c = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User(text)] }, body);
            cap.End(c, new LlmResponse([.. uses, new LlmText("ok")], uses.Length > 0 ? StopReasons.ToolUse : StopReasons.EndTurn, LlmUsage.Zero, "m"), null, null);
        }
        Turn("1: 67 mine the gen", null, Use("a", "do", new { action = "mine", body = "67" }));
        Turn("2: 69 chop", "maid_chan_69", Use("b", "do", new { action = "chop" }));
        Turn("3: spawn 67", null, Use("c", "bodies", new { op = "spawn", name = "maid_chan_67" }));
        Turn("4: hello", null);
        Turn("5: crew", null, Use("d", "do", new { action = "goto", then = new[] { new { action = "mine", body = new[] { "69", "67" } } } }));
        Turn("6: everyone", null, Use("e", "stop", new { body = "all" }));
        Turn("7: worked for 67", "maid_chan_67");

        var page = cap.Page(0, "67");
        Assert.Equal("67", page.Filter);
        Assert.Equal([1L, 3L, 5L, 6L, 7L], page.Turns.Where(t => t.Targets).Select(t => t.Id));
        var d1 = cap.Turn(1, "67")!;
        Assert.True(d1.Calls[0].Response.Single(b => b.Kind == "tool_use").Target);
        var d2 = cap.Turn(2, "67")!;
        Assert.False(d2.Calls[0].Response.Single(b => b.Kind == "tool_use").Target);
        Assert.True(BrainContextCapture.SameBody("67", "MAID_CHAN_67"));
        Assert.False(BrainContextCapture.SameBody("67", "maid_chan_670"));

        // A tool result inherits its call's mark (the viewer highlights the pair).
        var c2 = cap.Begin(new LlmRequest
        {
            Tier = ModelTier.Main, Purpose = "task",
            Messages = [LlmMessage.User("8: go"), LlmMessage.Assistant(Use("f", "do", new { action = "goto", body = "67" })), LlmMessage.User(new LlmToolResult("f", "arrived"))],
        }, null);
        cap.End(c2, new LlmResponse([new LlmText("done")], StopReasons.EndTurn, LlmUsage.Zero, "m"), null, null);
        var w = cap.Page(0, "67").Window!;
        Assert.True(w.Messages.SelectMany(m => m.Blocks).Single(b => b.Kind == "tool_result").Target);
    }

    [Fact]
    public void Sub_entries_link_to_the_brain_turn_whose_context_they_inherited()
    {
        var cap = new BrainContextCapture();
        var info = new TurnInfo("Alice", ["Alice"], false);
        TurnContext.Current = info;
        try
        {
            var b = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User("chop wood")] }, null);
            cap.End(b, new LlmResponse([new LlmText("ok")], StopReasons.EndTurn, LlmUsage.Zero, "m"), null, null);
            TurnContext.Current = new TurnInfo("Bob", ["Bob"], false); // the next episode's flow
            var other = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User("another turn")] }, null);
            cap.End(other, null, new OperationCanceledException(), null);
            // a worker started from the first turn's flow (same ambient TurnInfo instance) runs on after the second turn began
            TurnContext.Current = info;
            var c = cap.Begin(new LlmRequest { Tier = ModelTier.Worker, Purpose = "worker", Messages = [LlmMessage.User("task: chop 5 logs")] }, "maid_chan_67");
            cap.End(c, new LlmResponse([new LlmText("ok: chopped 5")], StopReasons.EndTurn, LlmUsage.Zero, "w"), null, null);
        }
        finally { TurnContext.Current = null; }
        var page = cap.Page(0);
        var worker = page.Turns.Single(t => t.Lane == "worker");
        Assert.Equal(1, worker.Parent); // not the newest brain turn (2): the one whose context it inherited
        Assert.Equal("maid_chan_67", worker.Body);
        Assert.Equal("interrupted", page.Turns.Single(t => t.Id == 2).Error);
        Assert.Contains(cap.Turn(1)!.Children, t => t.Id == worker.Id);
        Assert.Empty(cap.Turn(2)!.Children);
    }

    [Fact]
    public void Capturing_costs_only_reference_keeping()
    {
        var cap = new BrainContextCapture();
        var history = new List<LlmMessage>();
        for (int i = 0; i < 150; i++)
        {
            history.Add(LlmMessage.User(new string('u', 400) + i));
            history.Add(LlmMessage.Assistant(new string('a', 300) + i));
        }
        history.Add(LlmMessage.User("[state] hp 20\nAlice: hi"));
        var req = new LlmRequest
        {
            Tier = ModelTier.Main, Purpose = "task", Messages = history,
            System = [new LlmSystemBlock(new string('s', 40_000), Cache: true, Ttl: "1h"), new LlmSystemBlock(new string('i', 2_000), Cache: true)],
        };
        var resp = new LlmResponse([new LlmText("hey")], StopReasons.EndTurn, new LlmUsage(10, 5, 30_000, 0), "claude-opus-5-5");
        for (int i = 0; i < 2_000; i++) cap.End(cap.Begin(req, null), resp, null, null); // warm-up
        const int n = 50_000;
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) cap.End(cap.Begin(req, null), resp, null, null);
        sw.Stop();
        long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / n;
        double us = sw.Elapsed.TotalMilliseconds * 1000 / n;
        output.WriteLine($"capture of a 301-message, 42K-char-system request: {us:0.00} µs and {bytes} bytes per call (Begin+End), renders {cap.Renders}");
        Assert.Equal(0, cap.Renders); // nobody watched: nothing was rendered, redacted or serialized
        Assert.True(bytes < 4_000, $"{bytes} bytes per call"); // the message pointer copy (301 × 8) + two small objects
        Assert.True(us < 50, $"{us:0.00} µs per call"); // generous for a loaded CI box; ~1-3 µs here
    }
}
