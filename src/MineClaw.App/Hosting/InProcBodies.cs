using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Bot;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Client;
using MineClaw.Hive;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Db;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Slots;
using MineClaw.World;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Hosting;

/// <summary>
/// <c>Bodies:Mode=inproc</c> (one brain, many bodies, step 2): the clone accounts run as bodies in main's process instead of
/// one process each. This is the registry's host: clone clients and their per-body wiring (<see cref="BodyFactory.CreateClone"/>),
/// main's tab list and the hive's SlotPolicy, the logout DepositProcedure with main as the in-process hand-off receiver,
/// stranded items and void spots in the shared HiveStore, the claim board (hosted here), one world-memory flusher for every
/// body, hive vision from the registry, and the brain's <c>hive</c> tool over the registry (spawn = spawn the body, then
/// delegate{body} or run its script). MainHive isn't created in this mode.
/// </summary>
public sealed class InProcBodies : IBodyHost, IAsyncDisposable
{
    private readonly HiveConfig _hive;
    private readonly BodiesOptions _o;
    private readonly MinecraftSettings _mc;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly string _main;
    private readonly List<Body> _built = new();
    private BotHost? _bot;
    private MinecraftClient? _mainClient;
    private HiveStore? _store;
    private IReadOnlyList<string> _regulars = [];
    private CancellationToken _shutdown;

    private InProcBodies(HiveConfig hive, BodiesOptions o, MinecraftSettings mc, ILoggerFactory loggers, IReadOnlyList<string> accounts)
    {
        _hive = hive;
        _o = o;
        _mc = mc;
        _loggers = loggers;
        _log = loggers.CreateLogger("MineClaw.Bodies");
        _main = mc.Username;
        Accounts = accounts;
        Board = new ClaimBoard(TimeSpan.FromSeconds(hive.ClaimTtlSeconds));
        Flusher = new WorldMemoryFlusher(log: _log);
    }

    public IReadOnlyList<string> Accounts { get; }
    public ClaimBoard Board { get; }
    public WorldMemoryFlusher Flusher { get; }
    public IClaimBoard MainClaims => Board.ViewFor(_main);
    public IEnumerable<string> HiveBots() => [_main, .. Accounts];
    public BodyRegistry? Registry => _bot?.Bodies;
    /// <summary>The brain's multi-body view (step 3), after <see cref="Attach"/>.</summary>
    public BodyDirectory? Directory { get; private set; }

    /// <summary>Null unless <c>Bodies:Mode=inproc</c> with at least one clone account (Bodies:Accounts, else Hive:CloneUsernames).</summary>
    public static InProcBodies? Create(HiveConfig hive, BodiesOptions o, MinecraftSettings mc, AgentOptions agent, ILoggerFactory loggers)
    {
        if (!o.IsInProc || !hive.Enabled) return null;
        var accounts = o.AccountsOr(hive.Clones()).Where(n => !n.Equals(mc.Username, StringComparison.OrdinalIgnoreCase)).ToList();
        if (accounts.Count == 0) return null;
        // The brain's cached prefix (step 3): one mind in several bodies, the body parameter, the bodies tool. Static text, so
        // one cold cache write when the mode flips; never per turn.
        if (!agent.PersonaExtra.Contains(Agent.Prompts.BodiesHint, StringComparison.Ordinal))
            agent.PersonaExtra = (agent.PersonaExtra + "\n" + Agent.Prompts.BodiesHint).Trim();
        return new InProcBodies(hive, o, mc, loggers, accounts);
    }

    /// <summary>After the bot exists: the registry's lifecycle, the shared flusher, vision, the hive tool and the chat hooks.</summary>
    public void Attach(BotHost bot, MinecraftClient mainClient, CancellationToken shutdown)
    {
        _bot = bot;
        _mainClient = mainClient;
        _shutdown = shutdown;
        var rt = bot.Runtime;
        _store = new HiveStore(rt.Db, _main);
        _regulars = _o.RegularPlayers.Length > 0 ? _o.RegularPlayers : _hive.RegularPlayers.Length > 0 ? _hive.RegularPlayers : rt.Options.Self.TrustedPlayers;
        PathfindGate.MaxConcurrent = _o.MaxPathfinds;
        rt.Chat.IsHiveBot ??= n => HiveBots().Contains(n, StringComparer.OrdinalIgnoreCase);
        // One flusher pumps every body's remembered world (main's included).
        if (bot.WorldMemory is { } wm) Flusher.Add(wm);
        Flusher.Start();
        bot.Bodies!.Configure(this, Accounts, _o, _log);
        // Step 3: the multi-body brain interface (body routing, focus, [bodies], wake digest, addressing, worker pool, "any").
        var dir = BodyDirectory.Enable(bot.Brain!, _log);
        var selector = new BodySelector(bot.Bodies);
        dir.Picker = selector.Pick;
        rt.Events.Posted += selector.OnEvent;
        rt.Chat.Accepted += m => { try { selector.OnPlayerChat(m.Text); } catch (Exception) { } };
        Directory = dir;
        if (bot.Vision?.Hub is { } hub)
            hub.Hive = new BodyVisionSource(bot.Bodies, () => Board.Leases().Select(l => new HiveLease(l.Key.Id, l.Owner)).ToList());
        // A trusted player saying they'll /tp a body lets its void login spot go ahead for 10 min ("I'll tp 420", "log 1337 in, I'll tp it").
        rt.Chat.Accepted += m =>
        {
            try
            {
                if (m.Player is not { } p || !rt.Options.Self.TrustedPlayers.Contains(p, StringComparer.OrdinalIgnoreCase) || !TpWords.IsMatch(m.Text)) return;
                foreach (var a in Accounts)
                {
                    var shortName = Body.ShortName(a, false);
                    if (Regex.IsMatch(m.Text, $@"\b(?:{Regex.Escape(shortName)}|{Regex.Escape(a)})\b", RegexOptions.IgnoreCase) && bot.Bodies.ConfirmVoid(a))
                        _log.LogInformation("bodies: {Player} will /tp {Body}; its void login is allowed for 10 min", p, a);
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "bodies: void confirm hook failed"); }
        };
        RegisterHiveTool(rt);
        _log.LogInformation("bodies: in-process mode, {N} clone account(s) ({Names}); regulars {Regulars}", Accounts.Count, string.Join(", ", Accounts),
            string.Join(", ", _regulars));
    }

    private static readonly Regex TpWords = new(@"\b(?:tp|teleport|tphere)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // ================================================================ IBodyHost

    public Body Create(string name)
    {
        var bot = _bot ?? throw new InvalidOperationException("not attached");
        var clientLog = _loggers.CreateLogger("MineClaw.Client." + Body.ShortName(name, false));
        var client = new MinecraftClient(new ClientOptions
        {
            Host = _mc.Host, Port = _mc.Port, Username = name,
            AutoReconnect = false, // the body's runner owns reconnects (backoff, launch gap, someone else's login)
            AutoRespawn = !_hive.OnCloneDeath.Equals("logout", StringComparison.OrdinalIgnoreCase),
            Log = m => clientLog.LogDebug("{Msg}", m),
        });
        if (_mainClient is { } main) client.World.Chops = main.World.Chops; // a remnant any body left is the hive's unfinished tree
        client.Kicked += r => _log.LogWarning("{Body} kicked: {Reason}", name, r);
        client.Spawned += () => _log.LogInformation("{Body} joined {Host}:{Port}", name, _mc.Host, _mc.Port);
        client.Danger += d => _log.LogWarning("{Body} danger {Kind}: {Msg}", name, d.Kind, d.Message);
        var body = BodyFactory.CreateClone(bot.Brain!, client, Board.ViewFor(name), Flusher);
        lock (_built) _built.Add(body);
        return body;
    }

    public Task ConnectAsync(IBody body, CancellationToken ct)
    {
        _log.LogInformation("bodies: connecting {Body} to {Host}:{Port}", body.Name, _mc.Host, _mc.Port);
        return ClientBodySessions.ConnectAsync(body.Client!, ct);
    }

    public Task<BodyExit> RunAsync(IBody body, CancellationToken ct) => ClientBodySessions.RunAsync(body.Client!, ct);

    public async Task DisconnectAsync(IBody body, string reason)
    {
        if (body.Client is { } c) await ClientBodySessions.DisconnectAsync(c, reason).ConfigureAwait(false);
        Board.ReleaseAll(body.Name); // its leases go with it
    }

    private SlotFacts Facts()
    {
        var w = _mainClient!.World;
        return new SlotFacts(w.MaxPlayers, w.Entities.OnlinePlayers);
    }

    public BodySlots Slots() { var f = Facts(); return new BodySlots(f.MaxPlayers, f.Online); }

    public BodySlotDecision CanSpawn(int pending)
    {
        var d = SlotPolicy.CanSpawn(Facts(), _regulars, pending, _o.MaxPlayersFallback);
        return new BodySlotDecision(d.Ok, d.Reason);
    }

    public IReadOnlyList<string> MustYield(IReadOnlyList<BodyProgress> running) =>
        SlotPolicy.MustYield(Facts(), _regulars, running.Select(r => new CloneProgress(r.Name, r.Progress, r.Started)), _o.MaxPlayersFallback);

    public async Task<BodyDeposit> DepositAsync(IBody body, TimeSpan timeout, CancellationToken ct)
    {
        var rt = _bot!.Runtime;
        var port = new GameInventoryPort(body.Adapter, rt.Observations, rt.Memory);
        var handoff = _mainClient is { } m ? new MainHandoff(m, body) : null;
        var r = await new DepositProcedure(port, Board.ViewFor(body.Name), "hive_storage_" + body.Name, handoff, _log)
            .RunAsync(timeout, ct).ConfigureAwait(false);
        return new BodyDeposit(r.Empty, r.Remaining, r.Text);
    }

    public void Stranded(IBody body, string reason, int remaining)
    {
        if (_store == null) return;
        if (remaining > 0) _store.MarkStranded(body.Name, reason, GameInventoryPort.Snapshot(body.Adapter.World));
        else _store.ClearStranded(body.Name);
    }

    public string? VoidSpot(string name) =>
        _store?.VoidSpawn(name) is { } v ? $"{(int)Math.Floor(v.X)},{(int)Math.Floor(v.Y)},{(int)Math.Floor(v.Z)}" : null;

    public void MarkVoid(string name, Vec3d at) => _store?.MarkVoidSpawn(name, at.X, at.Y, at.Z);

    public void ClearVoid(string name) => _store?.ClearVoidSpawn(name);

    public IReadOnlyList<string> Trusted() => _bot?.Runtime.Options.Self.TrustedPlayers ?? [];

    public Task SayAsync(string text) => _bot?.Runtime.Chat.SayAsync(text) ?? Task.CompletedTask;

    public void Post(GameEvent e) => _bot?.Runtime.Events.Post(e);

    // ================================================================ the brain's hive tool over the registry

    /// <summary>The brain's tool over its bodies (step 3: replaces <c>hive</c> in this mode; pinned ≤ ToolBudgets.HiveTool).</summary>
    public const string BodiesTool = "bodies";

    private void RegisterHiveTool(AgentRuntime rt)
    {
        var bridge = Bot.GameTools.ScriptTools.Of(rt);
        // Saved scripts by name only: inline Lua is written with the script tool (body=67), where the Opus-only policy applies.
        var schema = ToolSchema.Create().Str("op", enumValues: ["list", "spawn", "despawn", "focus"]).Str("name", required: false).Str("task", required: false);
        if (bridge != null) schema = schema.Str("script", required: false).Raw("args", """{"type":"object"}""", required: false);
        rt.Tools.Remove(HiveTools.MainTool);
        rt.Tools.Remove(BodiesTool);
        rt.Tools.Register(BodiesTool,
            "Your bodies: list; spawn name? (task/script starts its job); despawn name|all; focus name (your default body).",
            schema.Build(),
            (input, _) =>
            {
                var op = input.ReqStr("op").Trim().ToLowerInvariant();
                var r = op switch
                {
                    "spawn" => Spawn(rt, input),
                    "despawn" or "recall" => Recall(input.Str("name") ?? "all"),
                    "focus" => Focus(input.Str("name")),
                    "list" => Registry!.List(),
                    _ => "error: op is list|spawn|despawn|focus",
                };
                return Task.FromResult(r.StartsWith("error", StringComparison.Ordinal) ? ToolResult.Error(r) : ToolResult.Success(r));
            }, tiers: ToolTiers.Main);
    }

    private string Focus(string? name)
    {
        var reg = Registry!;
        if (!string.IsNullOrWhiteSpace(name) && Directory?.Canonical(name) is null) return $"error: no body called {name}";
        if (!string.IsNullOrWhiteSpace(name) && Directory?.Canonical(name) is { } want && want != "main" && reg.Find(want) is not { Adapter.IsPlaying: true })
            return $"error: {want} isn't in game (bodies op=spawn name={want})";
        var f = reg.SetFocus(name);
        return f.IsMain ? "ok: focus is main" : $"ok: focus is {f.Short} (body tools default to it)";
    }

    private string Spawn(AgentRuntime rt, JsonElement input)
    {
        var reg = Registry!;
        var task = input.Str("task") ?? "";
        string? script = input.Str("script"), code = null; // inline Lua goes through the script tool (Opus-only policy)
        string? args = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;
        string? scriptName = null;
        if (!string.IsNullOrWhiteSpace(script) || !string.IsNullOrWhiteSpace(code))
        {
            if (Bot.GameTools.ScriptTools.Of(rt) is not { } bridge) return "error: scripts are off";
            var prep = bridge.PrepareForHive(script, code, ToolCaller.Current?.Tag);
            if (prep.Error != null) return "error: " + prep.Error;
            scriptName = prep.Name;
        }
        if (scriptName == null && string.IsNullOrWhiteSpace(task)) return "error: spawn needs task (or script)";
        var s = reg.Spawn(input.Str("name"));
        if (!s.Ok || s.Body is not Body body) return s.Text;
        _ = RunJobAsync(rt, body, task, scriptName, args);
        var job = scriptName != null ? "runs script " + scriptName : "job: " + (task.Length > 60 ? task[..59] + "…" : task);
        return $"{s.Text}; {job} once it's in game (its [{body.Short} task] events report back)";
    }

    /// <summary>Waits for the body to be in game, then starts its job: its script on its own lane, or a delegate on its slot.</summary>
    private async Task RunJobAsync(AgentRuntime rt, Body body, string task, string? script, string? args)
    {
        try
        {
            if (!await Registry!.WaitOnlineAsync(body, TimeSpan.FromSeconds(_o.JoinTimeoutSeconds), _shutdown).ConfigureAwait(false))
            {
                Post(GameEvent.Notice($"[bodies] {body.Short} didn't get in game; its job didn't start", EventPriority.Normal) with { Body = body.Short, MustWake = true });
                return;
            }
            if (body.Activities.Current != null) body.Activities.Cancel("superseded: new hive task");
            if (script != null)
            {
                var r = await body.RunScriptAsync(script, args, _shutdown).ConfigureAwait(false);
                if (!r.Ok) Post(GameEvent.TaskFail($"script {script}: {r.Text}", true) with { Body = body.Short, MustWake = true });
                return;
            }
            // The worker pool (step 3) gives this body a worker of its own, within the cap; without one, wait for the one worker.
            for (int i = 0; i < 300 && rt.Worker is { Pool: null, IsRunning: true } && !_shutdown.IsCancellationRequested; i++)
                await Task.Delay(2000, _shutdown).ConfigureAwait(false);
            if (!rt.Tools.TryGet(Agent.Worker.WorkerAgent.ToolName, out var del))
            {
                Post(GameEvent.TaskFail("no worker for " + body.Short, true) with { Body = body.Short });
                return;
            }
            _ = body.Activities.RunAsync([(del, JsonSerializer.SerializeToElement(new { task }))], TimeSpan.Zero, _shutdown);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "bodies: {Body}'s job didn't start", body.Name); }
    }

    private string Recall(string who)
    {
        var reg = Registry!;
        var names = who.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? reg.All.Where(b => !b.IsMain).Select(b => b.Name).ToList()
            : reg.Resolve(who) is { } one ? [one] : [];
        if (names.Count == 0) return who.Equals("all", StringComparison.OrdinalIgnoreCase) ? "no clone is online" : $"error: no body called {who}";
        foreach (var n in names) _ = reg.DespawnAsync(n, "recalled");
        return $"ok: {string.Join(", ", names.Select(n => Body.ShortName(n, false)))} logging out (deposits first)";
    }

    // ================================================================ shutdown

    /// <summary>Main shutting down: the clones deposit and log out in parallel while main is still online to take items.</summary>
    public Task RecallAllAsync(string reason, TimeSpan timeout) => Registry?.DespawnAllAsync(reason, timeout) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (Registry is { } reg) await reg.StopAllAsync().ConfigureAwait(false);
        Body[] built;
        lock (_built) built = _built.ToArray();
        foreach (var b in built)
        {
            BodyFactory.DisposeClone(b, Flusher);
            if (b.Client is { } c) try { await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { }
        }
        Flusher.Dispose();
    }

    /// <summary>
    /// Main as the deposit's in-process hand-off receiver: main stands still (its job paused, not aborted) while the clone tosses
    /// at its feet, and counts what it picks up (0x16 Collect Item for its own entity).
    /// </summary>
    private sealed class MainHandoff(MinecraftClient main, IBody from) : IHandoffTarget
    {
        private int _collected;
        private IDisposable? _hold;
        private Action<int, int, MineClaw.Data.ItemStack?>? _onCollect;

        public Task<Vec3d?> OfferAsync(int items, CancellationToken ct)
        {
            if (!main.IsPlaying || from.Adapter.World.Dimension != main.World.Dimension) return Task.FromResult<Vec3d?>(null);
            Interlocked.Exchange(ref _collected, 0);
            if (_onCollect == null)
            {
                _onCollect = (collector, _, stack) =>
                {
                    if (collector == main.World.Player.EntityId) Interlocked.Add(ref _collected, Math.Max(1, (int)(stack?.Count ?? 1)));
                };
                main.ItemCollected += _onCollect;
            }
            _hold?.Dispose();
            _hold = main.HoldStill(TimeSpan.FromSeconds(15));
            return Task.FromResult<Vec3d?>(main.World.Player.Position);
        }

        public Task<int> ConfirmAsync(CancellationToken ct)
        {
            _hold?.Dispose();
            _hold = null;
            if (_onCollect != null) { main.ItemCollected -= _onCollect; _onCollect = null; }
            return Task.FromResult(Interlocked.Exchange(ref _collected, 0));
        }
    }
}
