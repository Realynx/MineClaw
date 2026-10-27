using MineClaw.Agent.Events;

namespace MineClaw.Bot.Brainstem;

/// <summary>What the bot claims to be doing right now (a job, a script, a standing order). Passive work (follow, guard, a
/// script waiting on an event) may stand still legitimately.</summary>
public sealed record ClaimedWork(string Label, bool Passive = false);

/// <summary>
/// Stall watchdog (zero tokens): while a job, script or standing order says the bot is working but the body hasn't changed for
/// <see cref="IdleAfter"/> (no movement, no inventory change, no new action), it posts ONE
/// <c>stalled: &lt;job&gt; idle 45s; last: ...</c> wake (<see cref="GameEvent.MustWake"/>, normal priority: it reaches a sleeping
/// brain but never interrupts the body). Re-arms once the body changes or the work does.
/// </summary>
public sealed class StallWatchdog : IDisposable
{
    private readonly Func<ClaimedWork?> _work;
    private readonly Func<string> _body;
    private readonly Func<string?> _lastResult;
    private readonly Action<GameEvent> _post;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private ITimer? _timer;
    private string? _label, _sig;
    private DateTimeOffset _since;
    private bool _fired;

    public StallWatchdog(Func<ClaimedWork?> work, Func<string> bodySignature, Func<string?> lastResult, Action<GameEvent> post,
        TimeProvider? time = null)
    {
        _work = work;
        _body = bodySignature;
        _lastResult = lastResult;
        _post = post;
        _time = time ?? TimeProvider.System;
    }

    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan CheckEvery { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Stalls reported so far (telemetry/tests).</summary>
    public int Fired { get; private set; }

    public void Start()
    {
        lock (_lock) _timer ??= _time.CreateTimer(_ => { try { Tick(); } catch (Exception) { /* never kill the timer */ } }, null, CheckEvery, CheckEvery);
    }

    /// <summary>One check (the timer calls it; tests call it directly).</summary>
    public void Tick()
    {
        var w = _work();
        var now = _time.GetUtcNow();
        GameEvent? e = null;
        lock (_lock)
        {
            if (w == null) { _label = null; _sig = null; _fired = false; return; }
            var sig = _body();
            if (w.Label != _label || sig != _sig)
            {
                _label = w.Label;
                _sig = sig;
                _since = now;
                _fired = false;
                return;
            }
            if (_fired || w.Passive || now - _since < IdleAfter) return;
            _fired = true;
            Fired++;
            var last = _lastResult();
            e = GameEvent.Notice($"stalled: {w.Label} idle {(int)(now - _since).TotalSeconds}s" +
                (string.IsNullOrWhiteSpace(last) ? "" : "; last: " + Trunc(last, 140)), EventPriority.Normal) with { MustWake = true };
        }
        _post(e);
    }

    private static string Trunc(string s, int n)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    public void Dispose()
    {
        lock (_lock) { _timer?.Dispose(); _timer = null; }
    }
}
