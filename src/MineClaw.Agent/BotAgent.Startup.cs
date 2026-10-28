using System.Text.RegularExpressions;
using MineClaw.Agent.Events;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// After a restart, the brain re-plans once, and an idle main with stuck work wakes again. Live 10-28 07:37-08:45: five
/// restarts, a standing order whose auto-resume had paused itself in the db, a paused build told only as a Background notice
/// → no turn for 70 minutes. The startup wake (one Normal event per process start) and the idle watchdog (idle N min while
/// an order is stuck or a paused job waits, backing off on "nothing to do") close that gap; hive clones keep both off.
/// </summary>
public sealed partial class BotAgent
{
    public const string StartupTag = "restarted";
    public const string WatchdogTag = "idle watchdog";

    private int _startHandled;
    private int _startupWakes;
    private int _watchdogStreak;
    private DateTimeOffset _lastWatchdog = DateTimeOffset.MinValue;
    private readonly List<Func<string?>> _pendingWork = new();

    /// <summary>Post the startup wake once per process start (off in hive clones: their brain waits for a real wake).</summary>
    public bool WakeOnStart { get; set; } = true;

    /// <summary>Run the idle watchdog (off in hive clones).</summary>
    public bool IdleWatchdog { get; set; } = true;

    /// <summary>Startup wakes posted by this process (0 or 1; tests).</summary>
    public int StartupWakes => Volatile.Read(ref _startupWakes);

    /// <summary>Watchdog wakes in a row without the bot getting busy in between (the backoff exponent).</summary>
    public int WatchdogStreak => _watchdogStreak;

    /// <summary>
    /// Work waiting outside the standing order, told in the startup wake and counted by the watchdog: e.g. a build paused by the
    /// restart ("the mob drop tower build (#4, 20/14 placed) was paused by a restart; ...; it's night now"). Null = none now.
    /// </summary>
    public void AddPendingWork(Func<string?> source)
    {
        lock (_pendingWork) _pendingWork.Add(source);
    }

    private List<string> PendingWorkNow()
    {
        Func<string?>[] sources;
        lock (_pendingWork) sources = _pendingWork.ToArray();
        var lines = new List<string>();
        foreach (var s in sources)
        {
            try { if (s() is { Length: > 0 } x) lines.Add(x.Trim()); }
            catch (Exception ex) { _log.LogDebug(ex, "pending work source failed"); }
        }
        return lines;
    }

    /// <summary>"standing order "mine the gen" (Fox): resumed" / ": not running (auto-resume paused ...)", or null.</summary>
    private string? OrderLine(bool? resumed, string? extra = null)
    {
        var o = Orders?.Current;
        if (o == null) return null;
        string? why = null;
        try { why = Orders!.WhyNot(); } catch (Exception) { }
        var state = resumed == true ? "resumed" : why != null ? "not running (" + why + ")" : resumed == false ? "resumes when idle" : "not running";
        return $"standing order \"{o.Job}\"{(o.Player != null ? $" ({o.Player})" : "")}: {state}" + (extra != null ? "; " + extra : "");
    }

    /// <summary>
    /// The rules that apply to <paramref name="text"/> (the "[standing rules: ...]" line), minus the rules the text already
    /// quotes (a paused build's line carries its design's rules).
    /// </summary>
    private string? RulesFor(string text)
    {
        string? line = null;
        try { line = StandingRules?.Invoke(text); } catch (Exception ex) { _log.LogDebug(ex, "standing rules failed"); }
        if (string.IsNullOrWhiteSpace(line)) return null;
        var body = line.Trim();
        if (body.StartsWith("[standing rules:", StringComparison.Ordinal) && body.EndsWith(']'))
            body = body["[standing rules:".Length..^1].Trim();
        var keep = body.Split(" | ").Where(r => Regex.Match(r, @"^rule#\d+") is not { Success: true } m || !text.Contains(m.Value, StringComparison.Ordinal))
            .ToList();
        return keep.Count == 0 ? null : "rules: " + string.Join(" | ", keep);
    }

    /// <summary>
    /// One Normal wake after the start resume: what was running before the restart and what the rules say, so the brain re-plans
    /// once (a single turn). Nothing to tell (no order, nothing paused) = no wake.
    /// </summary>
    /// <summary>
    /// The body's own safety check after a login (the client's spawn safety: an exposed body walks home or boxes itself in first;
    /// live 10-28 16:20 a restart woke main on the drop tower at night and zombies killed it before anything moved). Awaited
    /// (at most <see cref="SafetyFirstWait"/>) before the standing order resumes and before the startup wake; its line ("woke up
    /// stranded at …: boxed myself in …") leads that wake. Null = no check.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? SafetyFirst { get; set; }

    public TimeSpan SafetyFirstWait { get; set; } = TimeSpan.FromSeconds(45);

    private void PostStartupWake(bool resumed, string? lifted, string? safety = null)
    {
        var parts = new List<string>();
        if (safety != null) parts.Add(safety);
        if (OrderLine(resumed, lifted) is { } order) parts.Add(order);
        foreach (var w in PendingWorkNow()) parts.Add(w);
        if (parts.Count == 0) return;
        var text = string.Join("; ", parts);
        if (RulesFor(text) is { } rules) text += "; " + rules;
        Interlocked.Increment(ref _startupWakes);
        _log.LogInformation("startup wake: {Text}", text);
        _events.Post(GameEvent.Notice($"{StartupTag}: {text}. Re-plan once by your rules (keep the order going unless they say otherwise)",
            EventPriority.Normal) with { MustWake = true });
    }

    /// <summary>
    /// Idle watchdog: nothing running, nothing waking, quiet for <see cref="Human.IdleOptions.WatchdogMinutes"/> (doubling per
    /// wake that didn't get the bot busy, capped) while the standing order is stuck or paused work waits → one Normal wake.
    /// A standing order merely backing off ("nothing to do yet") or snoozed after a stop is C#'s wait, not a stall.
    /// </summary>
    private bool WatchdogTick(DateTimeOffset now, DateTimeOffset quietSince, Human.IdleOptions idle)
    {
        if (!IdleWatchdog || idle.WatchdogMinutes <= 0) return false;
        var parts = new List<string>();
        if (Orders?.Current != null)
        {
            string? why = null;
            try { why = Orders.WhyNot(); } catch (Exception) { }
            if (why != null && !why.StartsWith("nothing to do", StringComparison.Ordinal) && !why.StartsWith("snoozed", StringComparison.Ordinal)
                && why != "held")
                parts.Add(OrderLine(null)!);
        }
        parts.AddRange(PendingWorkNow());
        if (parts.Count == 0) { _watchdogStreak = 0; return false; }

        double wait = Math.Min(idle.WatchdogMinutes * Math.Pow(2, Math.Min(_watchdogStreak, 10)), Math.Max(idle.WatchdogMinutes, idle.WatchdogMaxMinutes));
        var since = quietSince > _lastWatchdog ? quietSince : _lastWatchdog;
        if (now - since < TimeSpan.FromMinutes(wait)) return false;
        _watchdogStreak++;
        _lastWatchdog = now;
        var text = string.Join("; ", parts);
        if (RulesFor(text) is { } rules) text += "; " + rules;
        int idleMin = (int)Math.Round((now - quietSince).TotalMinutes);
        _log.LogInformation("idle watchdog ({N}x): {Text}", _watchdogStreak, text);
        _events.Post(GameEvent.Notice($"{WatchdogTag}: idle {idleMin}m, nothing running: {text}. Do what your rules call for, or say nothing to do",
            EventPriority.Normal));
        return true;
    }
}
