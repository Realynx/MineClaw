using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Llm;
using MineClaw.App.Hosting;
using MineClaw.Bot;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Game;
using MineClaw.Bot.Tests;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Hive;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// Live 10-28 02:03, Fox: "spawn up a deepseek hive, to deforest this whole area, 15 agents", meaning ONE DeepSeek hive driving 15
/// bodies. End to end at 3 bodies: the hive process (its own client on a fake server, a fake LLM on the deepseek profile) hosts its
/// subhive bodies in-process (InProcBodies, subhive mode); main allocates and whitelists every account over the bus; the DeepSeek
/// brain runs a saved crew script, and the three bodies chop in parallel lanes, each on its own connection; releasing the hive
/// logs its bodies out first (deposit, then logout, then main's whitelist remove).
/// </summary>
[Collection("Release1122World")]
public sealed class SubhiveHostTests(ITestOutputHelper output)
{
    private const string Me = "maid_chan_101";
    private static readonly string[] BodyNames = ["maid_chan_102", "maid_chan_103", "maid_chan_105"];
    private static readonly Dictionary<string, (int X, int Y, int Z)> Log = new()
    {
        ["maid_chan_102"] = (9, 64, 9), ["maid_chan_103"] = (7, 64, 9), ["maid_chan_105"] = (9, 64, 7),
    };

    private static FakeServer Server()
    {
        var s = new FakeServer();
        foreach (var t in Log.Values) s.Blocks[(t.X, t.Y, t.Z)] = Blocks.Log; // every server has all three: a wrong lane shows
        s.SpawnAt = (8.5, 64, 8.5);
        return s;
    }

    private static ClientOptions Fast(ClientOptions o, FakeServer s) => o with
    {
        TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero, RespawnDelay = TimeSpan.FromMilliseconds(10),
        ConnectionFactory = s.CreateClientConnection, HostileAlertRadius = 0, AutoReconnect = false,
    };

    private static async Task<bool> Until(Func<bool> cond, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(10); }
        return cond();
    }

    /// <summary>The DeepSeek brain: its turns are scripted; every request it got is kept (its model, tier and tools).</summary>
    private sealed class FakeDeepSeek : ILlmClient
    {
        public readonly ConcurrentQueue<Func<LlmRequest, LlmResponse>> Turns = new();
        public readonly ConcurrentQueue<LlmRequest> Seen = new();
        private int _n;

        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct)
        {
            if (r.Tier != ModelTier.Main || r.Purpose.StartsWith("quick_", StringComparison.Ordinal)) return Task.FromResult(Say(r, ""));
            Seen.Enqueue(r);
            return Task.FromResult(Turns.TryDequeue(out var t) ? t(r) : Say(r, ""));
        }

        public LlmResponse Use(LlmRequest r, string tool, object input) =>
            new([new LlmToolUse("tu_" + Interlocked.Increment(ref _n), tool, JsonSerializer.SerializeToElement(input))], StopReasons.ToolUse,
                new LlmUsage(100, 20, 0, 0), r.Model ?? "deepseek-v4.1-flash:cloud");

        public static LlmResponse Say(LlmRequest r, string text) =>
            new([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(100, 20, 0, 0), r.Model ?? "deepseek-v4.1-flash:cloud");
    }

    /// <summary>Main's clone processes as the hierarchy sees them: just the DeepSeek hive (101), recalled by its logout gate.</summary>
    private sealed class MainBodies(Func<HiveHierarchy> h, List<string> log) : IHiveBodies
    {
        public readonly HashSet<string> Live = new(StringComparer.OrdinalIgnoreCase) { Me };
        public string Spawn(string task, string? name, bool handover, HiveSpawnOptions options) => "ok";
        public string Recall(string name, string reason)
        {
            lock (log) log.Add("hive logout " + name);
            Live.Remove(name);
            h().Released(name, reason);
            return "ok";
        }
        public Task WaitGoneAsync(string name, TimeSpan timeout) => Task.CompletedTask;
        public IReadOnlyCollection<string> Running() => Live.ToList();
        public SlotFacts Slots() => new(20, ["maid_chan", Me, "poofimafox"]);
        public int Pending() => 0;
        public string List() => string.Join(" | ", Live);
    }

    private sealed class ListLogger(List<string> lines) : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = formatter(state, exception) + (exception != null ? " !! " + exception.GetType().Name + ": " + exception.Message : "");
            lock (lines) lines.Add(line);
            if (Environment.GetEnvironmentVariable("MINECLAW_TEST_LOG") is { Length: > 0 } f) lock (lines) File.AppendAllText(f, line + Environment.NewLine);
        }
        public void Dispose() { }
    }

    private const string ChopCrew = """
        // purpose: each body chops the log at its spot in its own lane, at once; one result line
        const P = params({ bodies: [], at: {} })
        function chopper(a) {
          const r = dig(a.at)
          crew.inc('logs', ok(r) ? 1 : 0)
          return `${me.body} ${ok(r) ? 'chopped' : r.msg}`
        }
        function main() {
          const r = parallel(P.bodies.map(b => [b, 'chopper', { at: P.at[b] }]))
          return `chop crew: ${crew.get('logs')} logs (${r.join(', ')})`
        }
        """;

    [Fact]
    public async Task A_deepseek_hive_hosts_three_bodies_that_chop_in_parallel_lanes_and_leave_on_release()
    {
        var log = new List<string>();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new ListLogger(log)));
        var servers = new List<string> { Me }.Concat(BodyNames).ToDictionary(n => n, _ => Server(), StringComparer.OrdinalIgnoreCase);

        // ---- main: the allocator, the whitelist (through the command gate's scoped exception) and the bus
        var cfg = new HiveConfig { Subhives = { ViewDistance = 3 } };
        var gate = new CommandGate(new CommandOptions(), () => ["poofimafox"]);
        var managed = new ManagedAccounts(null);
        HiveHierarchy? h = null;
        var whitelist = new WhitelistManager(managed, cfg.Accounts, "maid_chan", gate.ArmManagedWhitelist, (line, _) =>
        {
            bool ok = gate.AllowOutgoing(line);
            if (ok) lock (log) log.Add("sent " + line);
            return Task.FromResult(ok);
        }, loggers.CreateLogger("whitelist"));
        gate.ManagedAccount = whitelist.Accepts;
        h = new HiveHierarchy(cfg.Accounts, "maid_chan", managed, whitelist, regulars: () => ["poofimafox"], log: loggers.CreateLogger("hierarchy"));
        var mainBodies = new MainBodies(() => h, log);
        h.Bodies = mainBodies;
        managed.Upsert(Me, x => x);
        h.Tree.Put(new HiveNode(Me, CloneKind.Hive, BrainKind.DeepSeek, HiveTree.Root) { Task = "deforest this area", State = "running" });
        var bus = new HiveBus("maid_chan") { MainHandler = h.HandleBusAsync };
        h.Bus = bus;
        SubhiveWiring.AttachMain(h, null, cfg, loggers.CreateLogger("main"));
        h.Resources = null; // the test machine's memory isn't the CT's

        // ---- the DeepSeek hive process: its client, its brain (the deepseek profile, a fake model), its subhive host
        var agent = TestBot.Options();
        agent.Llm.ApiKey = "sk-ant-test";
        agent.Llm.Ollama.Endpoint = "http://127.0.0.1:9";
        agent.Llm.Profiles["deepseek"] = new LlmProfile { Provider = LlmProviders.Ollama, Model = "deepseek-v4.1-flash:cloud" };
        var mc = new MinecraftSettings { Host = "127.0.0.1", Port = 1, Username = Me };
        var board = new MineClaw.Hive.Claims.ClaimBoard(TimeSpan.FromSeconds(60));
        await using var host = InProcBodies.CreateSubhives(cfg, new BodiesOptions { LaunchGapSeconds = 0, YieldCheckSeconds = 0, TickReportSeconds = 0 },
            mc, agent, loggers, null, board)!;
        Assert.Contains(Agent.Prompts.SubhivesHint, agent.PersonaExtra); // the body parameter, static in the prompt

        var hiveClient = new MinecraftClient(Fast(new ClientOptions { Username = Me }, servers[Me]));
        try
        {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await hiveClient.ConnectAsync(cts.Token);
        Assert.True(await Until(() => hiveClient.World.Player.OnGround));
        var llm = new FakeDeepSeek();
        using var bot = BotHost.Create(new MinecraftGameClient(hiveClient), agent, null, llm, loggers, persist: false,
            hiveBots: () => ["maid_chan", Me, .. host.Registry?.Accounts ?? []]);
        var rt = bot.Runtime;
        var scripts = MineClaw.Bot.GameTools.ScriptTools.Of(rt)!;
        scripts.Crew = new BodyCrewPort(bot.Bodies!, rt.Tools, new DbCrewBoard(rt.Memory.Db)); // CloneMode: its bodies are its lanes
        // The crew script was saved earlier by the Opus brain (a DeepSeek brain runs saved scripts, never writes them).
        var saved = await bot.Run("script", new { op = "save", name = "chop_crew", code = ChopCrew });
        Assert.True(saved.Ok, saved.Text);
        Assert.Null(agent.Llm.ApplyBrainProfile("deepseek"));
        Assert.False(agent.Llm.BrainAuthorsScripts);
        var (dropped, tokens) = CompactBrainTools.Apply(rt.Tools);
        output.WriteLine($"compact brain tools: {dropped} dropped, ≈{tokens} tokens per call saved");
        Assert.True(dropped >= 5 && tokens >= 500);
        // What it needs to re-plan a used-up area stays on its tier (live 10-29): see answers "trees within 64 of X" exactly,
        // do runs chop_tree at=<a tree beyond> (exhausted results name them), the hive tool and the crew script.
        var brainTools = rt.Tools.Definitions(Agent.Llm.ModelTier.Main).Select(d => d.Name).ToHashSet();
        foreach (var need in new[] { "see", "do", "look", "script", "stop" }) Assert.Contains(need, brainTools);

        string token = "";
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetTotalMemory(true);
        host.Attach(bot, hiveClient, CancellationToken.None);
        Func<BusEnvelope, CancellationToken, Task<BusReply>?>? inbox = null;
        token = bus.Register(Me, (e, ct) => inbox?.Invoke(e, ct) ?? Task.FromResult(new BusReply(true))); // its pipe endpoint + token
        inbox = SubhiveWiring.AttachClone(Me, (type, msg, timeout) => bus.RouteAsync(BusEnvelope.Make(type, Me, "main", msg, token), Me)!,
            host, rt, cfg, wanted: 3, () => hiveClient.IsPlaying, CancellationToken.None, loggers.CreateLogger("hive"));
        var setup = host.ClientSetup!;
        host.ClientSetup = o => Fast(setup(o), servers[o.Username]);
        Assert.Equal(3, host.ViewDistance);

        // ---- it asks main for its 3 bodies once in game: main allocates, whitelists each, then the hive logs them in
        Assert.True(await Until(() => rt.Events.Snapshot().Any(e => e.Text.StartsWith("[hive] your bodies"))), "the bodies notice");
        var notice = rt.Events.Snapshot().First(e => e.Text.StartsWith("[hive] your bodies")).Text;
        output.WriteLine(notice);
        Assert.Contains("ok: 3 subhive bodies (102-105) for 101's brain; none in game yet", notice);
        bool online = await Until(() => BodyNames.All(n => bot.Bodies!.Find(n) is { State: BodyState.Online, Adapter.IsPlaying: true }));
        if (!online) lock (log) output.WriteLine(string.Join(" / ", log));
        Assert.True(online, "3 bodies online: " + bot.Bodies!.List());
        lock (log)
            foreach (var n in BodyNames)
                Assert.True(log.IndexOf($"sent /whitelist add {n}") is >= 0 and var i && i < log.FindIndex(l => l.Contains($"connecting {n}")), n + " whitelisted by main first");
        Assert.All(BodyNames, n => Assert.Equal(Me, h.Tree.Get(n)!.Owner));
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long after = GC.GetTotalMemory(true);
        double perBody = (after - before) / 3.0 / 1024 / 1024;
        output.WriteLine($"managed heap per subhive body (full wiring: client, world view, activities, lane, indexer, memory): {perBody:0.0} MB");
        Assert.True(perBody < 30, $"{perBody:0.0} MB per body");

        // ---- the DeepSeek brain runs the saved crew: one lane per body, at once, each on its own connection
        var at = BodyNames.ToDictionary(n => n[(n.LastIndexOf('_') + 1)..], n => $"{Log[n].X},{Log[n].Y},{Log[n].Z}");
        llm.Turns.Enqueue(r => llm.Use(r, "script", new { op = "run", name = "chop_crew", @params = new { bodies = at.Keys.ToArray(), at } }));
        llm.Turns.Enqueue(r => FakeDeepSeek.Say(r, "Deforesting with all three!"));
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Notice("[hive task] deforest this area", EventPriority.Normal)]), CancellationToken.None);
        var first = Assert.Single(llm.Seen.Take(1));
        Assert.Equal(("deepseek-v4.1-flash:cloud", LlmProviders.Ollama), (first.Model ?? agent.Llm.MainModel, agent.Llm.ProviderFor(ModelTier.Main)));
        Assert.DoesNotContain(first.Tools ?? [], t => t.Name is "design" or "reflect_self"); // the compact set: no cache, every call pays
        // The crew's one result line reaches the brain (its tool result, or the [task] event when it outlives the turn).
        string Seen() => string.Join(" | ", llm.Seen.SelectMany(q => q.Messages).SelectMany(m => m.Content).OfType<LlmToolResult>().Select(t => t.Content)
            .Concat(rt.Events.Snapshot().Select(e => e.Text)));
        Assert.True(await Until(() => Seen().Contains("chop crew:"), 10000), "crew result: " + Seen());
        var result = Seen();
        output.WriteLine(result);
        Assert.Contains("chop crew: 3 logs", result);
        foreach (var n in BodyNames)
            Assert.Equal([Log[n]], servers[n].Dug.Select(d => (d.X, d.Y, d.Z)).Distinct().ToList()); // only its own log, on its own connection
        Assert.Empty(servers[Me].Dug); // the hive's own body stood by

        // ---- release the hive: its bodies deposit and log out first, main takes each off the whitelist, then the hive goes
        var rel = h.Release("101");
        output.WriteLine(rel);
        Assert.Contains("after 3 child clone(s) (102-105)", rel);
        Assert.True(await Until(() => h.Tree.Count == 0, 30000), "released: " + h.Tree.Render());
        Assert.True(await Until(() => BodyNames.All(n => log.Contains($"sent /whitelist remove {n}"))));
        List<string> lines;
        lock (log) lines = log.ToList();
        foreach (var n in BodyNames)
        {
            int deposit = lines.FindIndex(l => l.StartsWith($"bodies: {n}: deposit before logout"));
            Assert.True(deposit >= 0 && deposit < lines.IndexOf($"sent /whitelist remove {n}"), n + " deposits before it leaves the whitelist");
            Assert.True(lines.IndexOf($"sent /whitelist remove {n}") < lines.IndexOf("hive logout " + Me), n + " is gone before its hive");
        }
        Assert.True(await Until(() => BodyNames.All(n => bot.Bodies!.Find(n) == null)));
        Assert.All(managed.All(), a => Assert.False(a.Active));
        }
        finally
        {
            await hiveClient.DisposeAsync();
            foreach (var srv in servers.Values) srv.Dispose();
        }
    }

    [Fact]
    public async Task Main_in_hive_mode_keeps_the_hive_tool_and_its_brain_budget_with_subhive_bodies()
    {
        var (bot, _) = TestBot.Create();
        using var _bot = bot;
        var agent = bot.Runtime.Options;
        var hive = new HiveConfig();
        await using var host = InProcBodies.CreateSubhives(hive, new BodiesOptions { YieldCheckSeconds = 0, TickReportSeconds = 0 },
            new MinecraftSettings { Username = "maid_chan" }, agent, NullLoggerFactoryShim.Instance)!;
        await using var main = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        host.Attach(bot, main, CancellationToken.None);
        Assert.True(host.IsSubhive && bot.Bodies!.HasLifecycle);
        Assert.False(bot.Runtime.Tools.Contains(InProcBodies.BodiesTool)); // the hive tool stays; bodies come from the allocator
        Assert.Empty(bot.Bodies.Accounts);
        // Off: not created at all.
        Assert.Null(InProcBodies.CreateSubhives(new HiveConfig { Subhives = { Enabled = false } }, new BodiesOptions(), new MinecraftSettings(), new AgentOptions(),
            NullLoggerFactoryShim.Instance));

        // The brain's tool definitions plus the hive tool (with bodies, pinned ≤ ToolBudgets.HiveTool by the Hive tests) stay inside
        // the brain cap; the body parameter is one cached prompt line, never in a schema.
        var brain = bot.Runtime.Tools.Definitions(ModelTier.Main);
        int total = brain.Sum(CompactBrainTools.Tokens) + Agent.Tools.ToolBudgets.HiveTool;
        output.WriteLine($"brain tools + hive tool ≈{total} tok, subhives hint ≈{Agent.Prompts.SubhivesHint.Length / 3.5:0} tok");
        Assert.True(total <= Agent.Tools.ToolBudgets.Brain, $"brain tools ≈{total}");
        Assert.True(Agent.Prompts.SubhivesHint.Length / 3.5 <= 200);
        Assert.DoesNotContain(brain, d => d.InputSchema.GetRawText().Contains("\"body\"", StringComparison.Ordinal));
    }

    private static class NullLoggerFactoryShim
    {
        public static readonly ILoggerFactory Instance = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    }
}
