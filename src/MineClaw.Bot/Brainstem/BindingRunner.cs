using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Events;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Perception;
using MineClaw.Scripting.Events;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// Runs standing bindings and learned reactions (docs/design/reactive-character.md): every <see cref="GameSignal"/> is checked
/// against the live bindings in the script library (event or chat intent, scope player, expiry, filter, cooldown) and a match
/// starts that saved script at once with the event as its args — no model call between the packet and the first action. Also
/// delivers every signal to the running script (its <c>on()</c>/<c>react()</c> handlers and machines).
/// <para>Rules: never during a safety reflex (EdgeGuard, survival, combat, shelter), never over a running script or a job a
/// player asked for (a filler or an idle standing order is fine), only main's body, a world-acting binding only if a trusted
/// player made it. Outcomes and reconcile verdicts (the brain's turn after a reaction kept, amended or corrected it; the
/// player correcting it) go to the binding's stats; a reaction corrected more often than kept is demoted.</para>
/// </summary>
public sealed class BindingRunner : IDisposable
{
    private readonly AgentRuntime _rt;
    private readonly IGameClient _c;
    private readonly ScriptBridge _bridge;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Json, EventFilter Filter)> _filters = new(StringComparer.Ordinal);
    private readonly List<(string Name, string? Player, DateTimeOffset At)> _awaitingVerdict = new();
    private IReadOnlyList<(string Name, ScriptBinding Binding)> _cache = [];
    private DateTimeOffset _cacheAt = DateTimeOffset.MinValue;
    private int _running;

    public BindingRunner(AgentRuntime rt, IGameClient client, ScriptBridge bridge, TimeProvider? time = null, ILogger? log = null)
    {
        _rt = rt;
        _c = client;
        _bridge = bridge;
        _time = time ?? TimeProvider.System;
        _log = log;
        bridge.Bindings = this;
        rt.Agent.EpisodeCompleted += OnEpisode;
        rt.Chat.Accepted += OnPlayerLine;
        // The first JS run in a process parses the SDK and JITs the engine (~170 ms); do it now, off the hot path, so a
        // binding's first fire starts in a few ms like every later one.
        _ = Task.Run(() => { try { Scripting.ScriptDryRun.Run("log('warm')", bridge.Controller.Host); } catch (Exception) { } });
    }

    public void Dispose()
    {
        _rt.Agent.EpisodeCompleted -= OnEpisode;
        _rt.Chat.Accepted -= OnPlayerLine;
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>(binding, ms from the signal to the script's start, started?) per fire: telemetry and tests.</summary>
    public event Action<string, double, bool>? Fired;

    /// <summary>Last skip reason per binding (tests, debug).</summary>
    public string? LastSkip { get; private set; }

    /// <summary>A binding changed: re-read the library on the next signal.</summary>
    public void Invalidate() { lock (_lock) _cacheAt = DateTimeOffset.MinValue; }

    private IReadOnlyList<(string Name, ScriptBinding Binding)> Live()
    {
        lock (_lock)
        {
            if (Now - _cacheAt < TimeSpan.FromSeconds(5)) return _cache;
            try { _cache = _bridge.Library.Bindings(); } catch (Exception) { _cache = []; }
            _cacheAt = Now;
            return _cache;
        }
    }

    /// <summary>One observed signal: to the running script, then every matching binding.</summary>
    public void OnSignal(GameSignal s)
    {
        try { _bridge.Controller.Deliver(s.ToScriptEvent()); } catch (Exception ex) { _log?.LogDebug(ex, "signal delivery failed"); }
        var live = Live();
        if (live.Count == 0) return;
        foreach (var (name, b) in live)
        {
            if (!Matches(name, b, s)) continue;
            Start(name, b, s);
            break; // one binding per signal: the body does one thing at a time
        }
    }

    private bool Matches(string name, ScriptBinding b, GameSignal s)
    {
        if (!b.Live(Now)) return false;
        bool intent = GameEventCatalog.Canonical(b.On) == null;
        if (intent ? s.Name != "player.say" || !string.Equals(EventFilter.Str(s.Data, "intent"), b.On, StringComparison.OrdinalIgnoreCase)
                   : !string.Equals(s.Name, b.On, StringComparison.OrdinalIgnoreCase)) return false;
        if (b.For != null && !b.For.Equals(s.Player, StringComparison.OrdinalIgnoreCase)) return false;
        return Filter(name, b).Accepts(s.Data);
    }

    private EventFilter Filter(string name, ScriptBinding b)
    {
        var json = b.Filter ?? "{}";
        lock (_lock)
        {
            if (_filters.TryGetValue(name, out var f) && f.Json == json) return f.Filter;
            JsonNode? node = null;
            try { node = JsonNode.Parse(json); } catch (JsonException) { }
            var ef = EventFilter.Parse(node);
            _filters[name] = (json, ef);
            return ef;
        }
    }

    /// <summary>Why a binding can't run now, or null: safety first, then the body.</summary>
    internal string? WhyNot(ScriptBinding b)
    {
        if (!_c.IsPlaying) return "not in game";
        if (_c.SafetyEngaged) return "safety reflex has the body";
        if (b.World && !b.CreatorTrusted) return "acts on the world but no trusted player made it";
        if (_bridge.Controller.Current is { IsLive: true } cur) return $"script {cur.Name} has the body";
        if (_rt.Activities.Current is { Kind: ActivityKind.Job or ActivityKind.Move } a && !IsStandingOrder(a)) return $"busy: {a.Label}";
        if (Volatile.Read(ref _running) > 0) return "another binding is running";
        return null;
    }

    private bool IsStandingOrder(RunningActivity a)
    {
        try { return _rt.Agent.Orders?.HeaderTag() is { } tag && tag.Contains(a.Label, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return false; }
    }

    private void Start(string name, ScriptBinding b, GameSignal s)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (WhyNot(b) is { } why)
        {
            LastSkip = why;
            _bridge.Library.Update(name, x => x.Skipped++);
            _log?.LogInformation("binding {Name} skipped ({Why}) for {Signal}", name, why, s.Name);
            Fired?.Invoke(name, sw.Elapsed.TotalMilliseconds, false);
            return;
        }
        if (!Filter(name, b).TryFire(Now)) return; // cooling down / once spent
        Interlocked.Increment(ref _running);
        var args = (JsonObject)s.Data.DeepClone();
        args["event"] = s.Name;
        var input = JsonSerializer.SerializeToElement(new { op = "run", name, args });
        _log?.LogInformation("binding {Name}: {Signal} → running now (no model)", name, s.Text);
        if (b.Kind == "reaction") lock (_lock) { _awaitingVerdict.Add((name, s.Player, Now)); if (_awaitingVerdict.Count > 16) _awaitingVerdict.RemoveAt(0); }
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                Fired?.Invoke(name, sw.Elapsed.TotalMilliseconds, true);
                var r = await _bridge.ExecuteAsync(input, _rt.Activities.Shutdown).ConfigureAwait(false);
                ok = r.Ok;
            }
            catch (Exception ex) { _log?.LogWarning(ex, "binding {Name} failed", name); }
            finally
            {
                Interlocked.Decrement(ref _running);
                _bridge.Library.Update(name, x => { x.Fires++; x.LastFired = Now; if (ok) x.Ok++; else x.Failed++; });
                Invalidate();
            }
        });
    }

    // ------------------------------------------------------------------ reconcile verdicts on learned reactions

    private static readonly Regex Correcting = new(@"^\W*(?:actually|wait|oops|sorry|nvm|never ?mind|no+\b|hold on|scratch that)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The brain's turn after a reaction: silent = kept, more lines = amended, a correction ("actually…") = corrected.</summary>
    private void OnEpisode(EpisodeOutcome o)
    {
        List<(string Name, string? Player, DateTimeOffset At)> due;
        lock (_lock)
        {
            due = _awaitingVerdict.Where(v => Now - v.At <= TimeSpan.FromSeconds(60)).ToList();
            _awaitingVerdict.Clear();
        }
        foreach (var (name, _, _) in due)
        {
            // No model call (an instant reply) or nothing more said = kept; more lines = amended; "actually…" = corrected (undo).
            string verdict = o.Calls == 0 || o.Said.Count == 0 ? "kept" : o.Said.Any(l => Correcting.IsMatch(l)) ? "corrected" : "amended";
            Verdict(name, verdict);
        }
    }

    /// <summary>A player's "no, I meant…" right after a reaction for them corrects it.</summary>
    private void OnPlayerLine(Agent.Chat.ChatMessage m)
    {
        if (m.Player is not { } p || !QuickReply.IsCorrection(m.Text)) return;
        List<string> hit;
        lock (_lock) hit = _awaitingVerdict.Where(v => p.Equals(v.Player, StringComparison.OrdinalIgnoreCase) && Now - v.At <= TimeSpan.FromSeconds(30)).Select(v => v.Name).ToList();
        foreach (var n in hit) Verdict(n, "corrected");
    }

    /// <summary>Records a reconcile verdict (kept | amended | corrected); public for the brain's reconcile and tests.</summary>
    public ScriptBinding? Verdict(string name, string verdict)
    {
        var b = _bridge.Library.Update(name, x =>
        {
            if (verdict == "kept") x.Kept++;
            else if (verdict == "amended") x.Amended++;
            else x.Corrected++;
        });
        if (b is { Enabled: false, OffNote: { } note } && note.StartsWith("demoted", StringComparison.Ordinal))
        {
            _log?.LogWarning("reaction {Name} {Note}: the brain answers that moment again", name, note);
            _rt.Events.Post(GameEvent.Notice($"reaction {name} {note}; rewrite it (script op=bind) or leave it off", EventPriority.Background));
        }
        Invalidate();
        return b;
    }
}
