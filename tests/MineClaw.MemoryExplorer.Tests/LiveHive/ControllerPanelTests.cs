using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.MemoryExplorer.LiveHive;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>The Controller panel's view model over a real capture, through the Supervisor's JSON shapes.</summary>
[Collection("avalonia")]
public class ControllerPanelTests
{
    // what SupervisorJson writes (snake_case, nulls left out); the panel reads it with HiveJson
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static T Trip<T>(T value) => HiveJson.Parse<T>(JsonSerializer.SerializeToUtf8Bytes(value, Wire))!;

    private sealed class Source(BrainContextCapture cap, BodyControllerInfo info) : IControllerSource
    {
        public BodyControllerInfo Info { get; set; } = info;
        public int ContextCalls, TurnCalls;
        public List<long> Sinces { get; } = [];
        public Task<BodyControllerInfo> ControllerAsync(string body, CancellationToken ct) => Task.FromResult(Trip(Info));
        public Task<BrainContextPage> ContextAsync(string brain, long since, string? body, CancellationToken ct)
        {
            ContextCalls++;
            Sinces.Add(since);
            return Task.FromResult(Trip(cap.Page(since, body)));
        }
        public Task<BrainTurnDetail> TurnAsync(string brain, long turn, string? body, CancellationToken ct)
        {
            TurnCalls++;
            return Task.FromResult(Trip(cap.Turn(turn, body)!));
        }
    }

    private sealed class NoScripts : IScriptDebugSource
    {
        public Task<BodyScriptDto> GetAsync(string body, bool code, CancellationToken ct) => Task.FromResult(new BodyScriptDto(body, false, null, "none"));
    }

    private static LlmToolUse Use(string id, string name, object input) => new(id, name, JsonSerializer.SerializeToElement(input));

    /// <summary>Brain turn 1 (thinking, redacted thinking, a tool call for 67) with a worker sub-entry for 67, brain turn 2 (69), turn 3 in flight.</summary>
    private static BrainContextCapture Capture()
    {
        var cap = new BrainContextCapture { ContextLimit = 100_000, CompactAt = 30_000 };
        var info = new TurnInfo("Fox", ["Fox"], false);
        LlmSystemBlock[] system = [new("you are maid_chan, a helpful maid", Cache: true, Ttl: "1h"), new("identity: playful", Cache: true)];
        TurnContext.Current = info;
        try
        {
            var t1 = LlmMessage.User("pos 1,64,2 hp 20/20\n[memory] the cobble gen is east of spawn\n[chat] Fox: 67 mine some cobble");
            var c1 = cap.Begin(new LlmRequest
            {
                Tier = ModelTier.Main, Purpose = "task", Messages = [t1], System = system,
            }, null);
            cap.End(c1, new LlmResponse([new LlmThinking("67 is idle; send it to the gen.", "sig"), new LlmRedactedThinking("QUJDREVGRw=="),
                Use("t1", "delegate", new { task = "mine 64 cobble", body = "67" })], StopReasons.ToolUse, new LlmUsage(200, 40, 9000, 0), "claude-opus-5-5"), null, null);
            var w = cap.Begin(new LlmRequest { Tier = ModelTier.Worker, Purpose = "worker", Messages = [LlmMessage.User("task: mine 64 cobble")] }, "maid_chan_67");
            cap.End(w, new LlmResponse([new LlmText("ok: mined 64 cobble")], StopReasons.EndTurn, new LlmUsage(50, 10, 0, 0), "claude-sonnet-5-5"), null, null);
            TurnContext.Current = new TurnInfo("Robin", ["Robin"], false);
            var c2 = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [t1, LlmMessage.User("[chat] Robin: 69 chop the tree")], System = system }, null);
            cap.End(c2, new LlmResponse([Use("t2", "do", new { action = "chop_tree", body = "69" })], StopReasons.ToolUse, new LlmUsage(300, 20, 9000, 0), "claude-opus-5-5"), null, null);
            cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [t1, LlmMessage.User("[chat] Fox: thanks!")], System = system }, null); // in flight
        }
        finally { TurnContext.Current = null; }
        return cap;
    }

    private static BodyControllerInfo Subhive67 => new("67", "subhive", "main", "main", "opus", "claude-opus-5-5", "anthropic", false, null, ["brain"], "67",
        new ControllerLinks("api/brains/main/context", "api/bodies/67/script", "main"));

    [Fact]
    public async Task Subhive_view_highlights_its_turns_shows_thinking_and_the_token_bar()
    {
        var cap = Capture();
        var src = new Source(cap, Subhive67);
        var vm = new ControllerPanelViewModel(src, "67", "maid_chan_67 · 67", () => new NoScripts(), post: a => a());
        string? opened = null;
        vm.OpenOwnerRequested += o => opened = o;

        await vm.PollOnceAsync();

        Assert.True(vm.HasBrain);
        Assert.False(vm.ShowTabs);
        Assert.True(vm.ShowBrain);
        Assert.Contains("subhive", vm.KindText);
        Assert.True(vm.HasOwner);
        Assert.True(vm.OnlyTargets);
        // newest first, the worker task nested under the brain turn that started it
        Assert.Equal([4L, 3L, 1L, 2L], vm.Turns.Select(t => t.Id));
        var worker = vm.Turns.Single(t => t.Lane == "worker");
        Assert.Equal(1, worker.Depth);
        Assert.Equal(1L, worker.Turn.Parent);
        Assert.Equal(vm.Turns.IndexOf(vm.Turns.Single(t => t.Id == 1)) + 1, vm.Turns.IndexOf(worker));
        // the filter: only the turns that targeted 67 stay visible
        Assert.Equal([1L, 2L], vm.Turns.Where(t => t.IsVisible).Select(t => t.Id).Order());
        vm.OnlyTargets = false;
        Assert.All(vm.Turns, t => Assert.True(t.IsVisible));
        // "now thinking…" for the call in flight, and the bar (the window = the in-flight call, so the estimate)
        Assert.True(vm.IsThinking);
        Assert.StartsWith("now thinking…", vm.ThinkingText);
        Assert.True(vm.Fill > 0 && vm.Fill < 1);
        Assert.Contains("/ 100k tok", vm.TokenText);
        Assert.Contains("cached prefix", vm.SystemHeader);
        Assert.False(vm.SystemExpanded); // the system prompt starts collapsed
        Assert.All(vm.System, s => Assert.True(s.InCachedPrefix));
        Assert.Contains(vm.Messages, r => r.IsMemory && r.Text.Contains("cobble gen"));

        // expanding a turn fetches its calls: thinking and redacted thinking labelled, the tool call for 67 highlighted
        var t1 = vm.Turns.Single(t => t.Id == 1);
        await vm.ToggleAsync(t1);
        Assert.True(t1.IsExpanded);
        Assert.Contains(t1.Rows, r => r.Kind == "thinking" && r.Header.Contains(BrainContextCapture.ThinkingLabel) && r.Text.Contains("send it to the gen"));
        Assert.Contains(t1.Rows, r => r.Kind == "redacted_thinking" && r.Header.Contains(BrainContextCapture.RedactedThinkingLabel) && r.IsThinking);
        Assert.Contains(t1.Rows, r => r.Kind == "tool_use" && r.IsTarget && r.Header.Contains("delegate") && r.Text.Contains("\"body\": \"67\""));
        Assert.Contains(t1.Rows, r => r.IsCall && r.Header.Contains("cache read 9.0k"));
        Assert.Contains(t1.Rows, r => r.IsCall && r.Header.Contains("worker"));  // the linked sub-entry
        var json = await vm.TurnJsonAsync(t1);
        Assert.Contains("redacted_thinking", json);
        Assert.DoesNotContain("QUJDREVGRw==", json); // the encrypted payload never travels

        // search within the context
        int all = vm.Messages.Count;
        vm.SearchText = "cobble gen";
        Assert.Contains("match", vm.SearchNote);
        Assert.NotEmpty(vm.Messages);
        Assert.True(vm.Messages.Count < all);
        Assert.All(vm.Messages, r => Assert.True(r.Matches("cobble gen")));
        Assert.Equal([1L], vm.Turns.Where(t => t.IsVisible).Select(t => t.Id)); // the turn list follows the search (turn 1's rows mention it)
        vm.SearchText = "";
        Assert.Equal(all, vm.Messages.Count);

        // nothing new: the next poll asks since our version and changes nothing
        int before = vm.Turns.Count;
        await vm.PollOnceAsync();
        Assert.Equal(before, vm.Turns.Count);
        Assert.Equal(vm.Version, src.Sinces[^1]);
        // a new turn arrives by merge
        var c = cap.Begin(new LlmRequest { Tier = ModelTier.Main, Purpose = "task", Messages = [LlmMessage.User("[chat] Fox: 67 come back")] }, null);
        cap.End(c, new LlmResponse([new LlmText("ok")], StopReasons.EndTurn, LlmUsage.Zero, "claude-opus-5-5"), null, null);
        await vm.PollOnceAsync();
        Assert.Equal(before + 1, vm.Turns.Count);
        Assert.Equal(5L, vm.Turns[0].Id);

        // stale badge when nothing arrives
        vm.Tick(DateTime.UtcNow.AddSeconds(10));
        Assert.True(vm.IsStale);
        Assert.StartsWith("stale", vm.StaleText);

        vm.OpenOwnerCommand.Execute(null);
        Assert.Equal("main", opened);
    }

    [Fact]
    public async Task Script_only_body_shows_the_debugger_and_a_hive_running_a_script_shows_both()
    {
        var cap = Capture();
        var src = new Source(cap, new BodyControllerInfo("1337", "script", "main", null, null, null, null, true, "cobble_loop", ["script"], null,
            new ControllerLinks(null, "api/bodies/1337/script", "main")));
        var vm = new ControllerPanelViewModel(src, "1337", "1337", () => new NoScripts(), post: a => a());
        await vm.PollOnceAsync();
        Assert.False(vm.HasBrain);
        Assert.True(vm.HasScript);
        Assert.NotNull(vm.Script);
        Assert.True(vm.ShowScriptPane);
        Assert.False(vm.ShowTabs);
        Assert.Equal(0, src.ContextCalls); // no brain: no context polling
        Assert.Contains("script-only", vm.KindText);

        var hive = new Source(cap, new BodyControllerInfo("101", "hive", "main", "maid_chan_101", "deepseek", null, null, true, "wheat_loop",
            ["brain", "script"], null, new ControllerLinks("api/brains/maid_chan_101/context", "api/bodies/101/script", "main")));
        var vm2 = new ControllerPanelViewModel(hive, "101", "101", () => new NoScripts(), post: a => a()) { InfoEvery = TimeSpan.Zero };
        await vm2.PollOnceAsync();
        Assert.True(vm2.ShowTabs);
        Assert.True(vm2.ShowBrain);
        Assert.False(vm2.ShowScriptPane);
        vm2.SelectedTab = 1;
        Assert.True(vm2.ShowScriptPane);
        Assert.False(vm2.ShowBrain);
        // the script ends: the Script tab goes away on the next info refresh
        hive.Info = hive.Info with { HasScript = false, Tabs = ["brain"] };
        await vm2.PollOnceAsync();
        Assert.False(vm2.ShowTabs);
        Assert.Null(vm2.Script);
        Assert.Equal(0, vm2.SelectedTab);
        Assert.True(vm2.ShowBrain);
        vm2.Dispose();
        vm.Dispose();
    }

    [Fact]
    public Task Panel_renders_headless() => HeadlessUi.Run(() =>
    {
        var cap = Capture();
        var vm = new ControllerPanelViewModel(new Source(cap, Subhive67), "67", "67", () => new NoScripts(), post: a => a());
        vm.ApplyInfo(Subhive67);
        vm.Apply(cap.Page(0, "67"), DateTime.UtcNow);
        var view = new ControllerPanelView { DataContext = vm };
        var window = new Window { Width = 800, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => t != null).ToList();
        Assert.Contains(texts, t => t!.StartsWith("#1 brain turn"));
        Assert.Contains(texts, t => t!.StartsWith("now thinking…"));
        window.Close();
    });
}
