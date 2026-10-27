using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Bot.GameTools;
using MineClaw.Hive.Ipc;
using MineClaw.Scripting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Clone;

/// <summary>
/// A clone spawned on a script (<c>hive spawn script|code</c>): semi-sleep. The script starts right after the loadout with NO
/// brain turn (C# re-issues it as this clone's standing order), and the brain makes zero calls until a real wake: the script
/// failed / hit a limit, its <c>wake"…"</c> / <c>ask</c> / pause notices, danger, chat addressed to this clone (one turn, then
/// back to sleep), a trusted "stop", or main changing the order. Checkpoints (<c>cp</c>) are written into the standing order
/// (<c>resume_from</c>), so a restart after a crash or a respawn resumes there, not from scratch. A normal end sends main one
/// result line, clears the order, and logs out (deposit first) or stays online asleep (<c>after: "sleep"</c>).
/// </summary>
public sealed class CloneScriptRunner : IDisposable
{
    /// <summary><see cref="StandingOrder.Player"/> of orders this runner owns (main drops them on a new assignment).</summary>
    public const string OrderPlayer = "hive";

    private readonly AgentRuntime _rt;
    private readonly ScriptBridge _bridge;
    private readonly string _bot;
    private readonly Func<string, double?, Task> _progress;
    private readonly Func<bool, string, bool, Task> _result;
    private readonly ILogger _log;
    private readonly ITimer _timer;
    private readonly object _lock = new();
    private TaskMsg? _task;
    private volatile bool _asleep;
    private bool _started, _pendingStart;
    private int _startTries;
    private string? _lastCp, _swallow;
    private double _lastPct = -1;
    [ThreadStatic] private static bool _posting;

    /// <param name="progress">One progress line to main (text, pct 0..1).</param>
    /// <param name="result">The script's result to main (ok, line, then log out?).</param>
    public CloneScriptRunner(AgentRuntime rt, ScriptBridge bridge, Func<string, double?, Task> progress, Func<bool, string, bool, Task> result,
        ILogger? log = null, TimeProvider? time = null, TimeSpan? poll = null)
    {
        _rt = rt;
        _bridge = bridge;
        _bot = rt.Options.Chat.Username;
        _progress = progress;
        _result = result;
        _log = log ?? NullLogger.Instance;
        var prev = rt.Events.Gate;
        rt.Events.Gate = prev == null ? Gate : e => prev(e) is { } kept ? Gate(kept) : null;
        bridge.Controller.Ended += OnEnded;
        bridge.IdleState = () => _asleep ? "asleep" : null;
        var every = poll ?? TimeSpan.FromSeconds(2);
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Tick(), null, every, every);
    }

    /// <summary>The brain is in semi-sleep (a script task): only real wakes reach it.</summary>
    public bool Asleep => _asleep;
    public TaskMsg? Task => _task;

    public static bool Handles(TaskMsg? t) => !string.IsNullOrWhiteSpace(t?.Script);

    /// <summary>Process start with a script task (before connecting): asleep at once; the standing order waits for the loadout.</summary>
    public void Prepare(TaskMsg t)
    {
        if (!Handles(t)) return;
        _asleep = true;
        if (_rt.Agent.Orders is { } o) o.Held = true;
    }

    /// <summary>
    /// Starts (or, for a new task from main, replaces) the script as this clone's standing order — zero tokens. A restarted task
    /// resumes from the checkpoint the order recorded. False = it couldn't start yet (retried on the next tick).
    /// </summary>
    public bool Start(TaskMsg t)
    {
        if (!Handles(t)) return false;
        var orders = _rt.Agent.Orders;
        if (orders == null) { _log.LogWarning("clone {Bot}: no standing orders; can't run script {Script}", _bot, t.Script); return false; }
        bool change;
        string? resume = null;
        lock (_lock)
        {
            change = _started && _task != null && _task.Id != t.Id;
            var existing = _rt.Memory.GetStandingOrder(_bot);
            if (t.Restarted && existing is { Player: OrderPlayer } && ScriptOf(existing) == t.Script) resume = ResumeOf(existing);
            _task = t;
            _asleep = true;
            _swallow = null;
            _lastCp = resume;
            _lastPct = -1;
            _started = true;
            _startTries = 0;
        }
        if (change && _bridge.Controller.Current is { } cur) cur.Stop("replaced by main");
        // Main saved it in the shared db moments ago: reload now rather than wait for the data_version watcher.
        if (_bridge.Library.Get(t.Script!) == null) try { _rt.Memory.Reload(); } catch (Exception ex) { _log.LogDebug(ex, "memory reload failed"); }
        var input = new JsonObject { ["op"] = "run", ["name"] = t.Script };
        if (!string.IsNullOrWhiteSpace(t.Args)) try { input["args"] = JsonNode.Parse(t.Args); } catch (JsonException) { }
        if (resume != null) input["resume_from"] = resume;
        _rt.Memory.SetStandingOrder(new StandingOrder
        {
            Bot = _bot, Player = OrderPlayer, Job = $"script {t.Script}", Tool = ScriptTools.ToolName, Input = JsonSerializer.SerializeToElement(input),
        });
        _rt.Memory.UpdateStandingOrder(_bot, o => { o.LastResumed = null; o.FailuresInRow = 0; });
        orders.Held = false;
        bool ok = orders.ResumeIfIdle(change ? "hive order changed" : "hive script");
        lock (_lock) _pendingStart = !ok;
        _log.LogInformation("clone {Bot}: script {Script} {State}{Resume} (brain asleep{Profile})", _bot, t.Script, ok ? "started" : "queued",
            resume != null ? $" from cp '{resume}'" : "", t.Profile != null ? ", profile " + t.Profile : "");
        if (change) PostOwn(GameEvent.Notice($"[hive] main changed your standing order: script {t.Script}", EventPriority.Normal));
        return ok;
    }

    /// <summary>Main gave a normal (brain) task: our script stops, the brain is awake again.</summary>
    public void Release(string reason)
    {
        TaskMsg? t;
        lock (_lock) { t = _task; _task = null; _asleep = false; _pendingStart = false; }
        if (t?.Script is { } name && _bridge.Controller.Current is { } s && string.Equals(s.Name, ScriptController.CleanName(name), StringComparison.OrdinalIgnoreCase))
            s.Stop(reason);
        if (_rt.Memory.GetStandingOrder(_bot) is { Player: OrderPlayer }) _rt.Memory.ClearStandingOrder(_bot, reason);
    }

    private static string? ScriptOf(StandingOrder o) => o.Input.ValueKind == JsonValueKind.Object && o.Input.TryGetProperty("name", out var n) ? n.GetString() : null;
    private static string? ResumeOf(StandingOrder o) => o.Input.ValueKind == JsonValueKind.Object && o.Input.TryGetProperty("resume_from", out var r) ? r.GetString() : null;

    private void OnEnded(ScriptSession s)
    {
        TaskMsg? t;
        lock (_lock) t = _task;
        if (t?.Script is not { } name || !string.Equals(s.Name, ScriptController.CleanName(name), StringComparison.OrdinalIgnoreCase)) return;
        if (s.Status == ScriptStatus.Cancelled) return; // stop / replace / recall: whoever stopped it reports
        var raw = (s.Outcome ?? (s.Ok ? "ok" : "failed")).ReplaceLineEndings(" ").Trim();
        var line = raw.Length > 200 ? raw[..199] + "…" : raw;
        if (s.Ok)
        {
            lock (_lock) _swallow = s.Name; // its [task ok] doesn't wake the brain: C# reports it
            _rt.Memory.ClearStandingOrder(_bot, "script done");
            _log.LogInformation("clone {Bot}: script {Script} done ({Line}); {After}", _bot, name, line, t.SleepsAfter ? "sleeping" : "logging out");
            _ = _result(true, line, !t.SleepsAfter);
        }
        else
        {
            // Failed / hit a limit: the brain wakes on [task failed]; main hears it now (one line).
            _ = _progress($"script {name} failed: {line}", null);
        }
    }

    /// <summary>Checkpoint → standing order resume point + one progress line to main; retries a start that was queued.</summary>
    internal void Tick()
    {
        try
        {
            TaskMsg? t;
            bool pending;
            lock (_lock) { t = _task; pending = _pendingStart; }
            if (t?.Script is not { } name) return;
            if (pending)
            {
                bool ok = _rt.Agent.Orders?.ResumeIfIdle("hive script") == true;
                lock (_lock)
                {
                    _pendingStart = !ok && ++_startTries < 30;
                    if (!ok && !_pendingStart) PostOwn(GameEvent.Notice($"[hive] script {name} couldn't start: {_bridge.Controller.Status()}", EventPriority.Normal));
                }
            }
            if (_bridge.Controller.Current is not { } s || !string.Equals(s.Name, ScriptController.CleanName(name), StringComparison.OrdinalIgnoreCase)) return;
            double? pct = s.ProgressPct is { } p ? Math.Clamp(p / 100.0, 0, 1) : s.CheckpointTotal > 0 ? (double)s.CheckpointsPassed / s.CheckpointTotal : null;
            string? cp = s.Checkpoint;
            bool newCp;
            lock (_lock)
            {
                newCp = cp != null && cp != _lastCp;
                if (newCp) _lastCp = cp;
                if (!newCp && (pct is not { } q || Math.Abs(q - _lastPct) < 0.05)) return;
                if (pct is { } q2) _lastPct = q2;
            }
            if (newCp)
                _rt.Memory.UpdateStandingOrder(_bot, o =>
                {
                    if (o.Player != OrderPlayer || o.Input.ValueKind != JsonValueKind.Object) return;
                    var node = JsonNode.Parse(o.Input.GetRawText())!.AsObject();
                    node["resume_from"] = cp;
                    o.Input = JsonSerializer.SerializeToElement(node);
                });
            _ = _progress($"script {name}" + (cp != null ? $" cp '{cp}'" : "") + (pct is { } r ? $" {r * 100:0}%" : ""), pct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "clone script tick failed"); }
    }

    /// <summary>
    /// Semi-sleep gate (after the script bridge's own gate): while asleep only real wakes wake the brain; everything else stays as
    /// non-waking context, idle check-ins are dropped, and the normal end of our own script is reported by C# instead.
    /// </summary>
    internal GameEvent? Gate(GameEvent e)
    {
        if (!_asleep || _posting) return e;
        switch (e.Kind)
        {
            case EventKind.IdleTick:
                return null;
            case EventKind.TaskCompleted:
                // The standing-order call handing the script over to its own background activity: not a result.
                if (e.Text.Contains("running in background as task", StringComparison.Ordinal)) return Quiet(e);
                lock (_lock)
                {
                    if (_swallow != null && e.Text.Contains("script " + _swallow, StringComparison.OrdinalIgnoreCase))
                    {
                        _swallow = null;
                        return e with { Wakes = false };
                    }
                }
                return e; // results of work the (woken) brain started itself
            case EventKind.TaskFailed or EventKind.LowHealth or EventKind.VoidFall or EventKind.Death:
                return e;
            case EventKind.Chat or EventKind.Whisper or EventKind.Emote:
                // Addressed to this clone (ClonePolicy already dropped what isn't): one turn, then asleep again.
                return e.Wakes && e.Priority >= EventPriority.Normal && e.Player != null && _rt.Chat.IsHiveBot?.Invoke(e.Player) != true ? e : Quiet(e);
            case EventKind.Notice when e.Text.StartsWith("script wake:", StringComparison.Ordinal) || e.Text.StartsWith("script paused:", StringComparison.Ordinal)
                                       || e.Text.StartsWith("standing order keeps failing", StringComparison.Ordinal):
                return e;
            default:
                return e.Wakes && e.Priority >= _rt.Options.InterruptPriority ? e : Quiet(e);
        }
    }

    private static GameEvent Quiet(GameEvent e) => e.Wakes ? e with { Wakes = false } : e;

    /// <summary>Our own notices (an order change from main) pass the gate.</summary>
    private void PostOwn(GameEvent e)
    {
        _posting = true;
        try { _rt.Events.Post(e); }
        finally { _posting = false; }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _bridge.Controller.Ended -= OnEnded;
    }
}
