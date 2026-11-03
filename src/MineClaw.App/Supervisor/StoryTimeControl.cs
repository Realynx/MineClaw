using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Supervisor;

/// <summary>An account the bot controls and whether the server made it op (null = unknown: b1.8.1 never says).</summary>
public sealed record OpAccount(string Name, bool IsMain, bool? Op);

/// <summary>
/// Story Mode's time-of-day slider (docs/web/story-mode.md "Time of day"): <c>/time set &lt;n&gt;</c> from an op account the bot controls,
/// main first. Op comes from the server (1.12.2's Entity Status 24..28 on login and on /op, /deop), else from
/// <c>Supervisor:Viewer:OpAccounts</c> (b1.8.1 has no such packet). One change a second at most; one log line per change. When main is
/// known not to be op, a warning is logged once (main should be op: it runs the whitelist).
/// </summary>
public sealed class StoryTimeControl(Func<IReadOnlyList<OpAccount>> accounts, Func<string, string, string, Task<string?>> send,
    TimeProvider? time = null, ILogger? log = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly object _lock = new();
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private bool _warned;

    /// <summary>At most one change per this gap (the slider sends while it is dragged).</summary>
    public static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);
    public const int MaxTime = 23999;

    /// <summary>The account a change would come from: main when it is op, else the first op body. Null = none is op.</summary>
    public string? By()
    {
        var all = accounts();
        return (all.FirstOrDefault(a => a.IsMain && a.Op == true) ?? all.FirstOrDefault(a => a.Op == true))?.Name;
    }

    /// <summary>The state's <c>time_control</c>: available when an op account is there, and which.</summary>
    public (bool Available, string? By) Status() => By() is { } by ? (true, by) : (false, null);

    /// <summary>Main's name when the server said it isn't op (logged once), else null.</summary>
    public string? MainNotOp()
    {
        if (accounts().FirstOrDefault(a => a.IsMain) is not { Op: false } main) return null;
        lock (_lock)
            if (!_warned)
            {
                _warned = true;
                _log.LogWarning("story: main ({Main}) is not op on the server: no time control or whitelist commands from it (/op {Main})", main.Name, main.Name);
            }
        return main.Name;
    }

    public enum Outcome { Ok, NoOp, TooSoon, Failed }

    /// <summary>Sets the time of day (0..23999) for <paramref name="player"/>: (outcome, the account, an error).</summary>
    public async Task<(Outcome Result, string? By, string? Error)> SetAsync(int ticks, string? player)
    {
        if (By() is not { } by) return (Outcome.NoOp, null, "no op account");
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (now - _last < MinGap) return (Outcome.TooSoon, by, "one time change a second");
            _last = now;
        }
        var line = $"/time set {ticks}";
        var why = $"story time slider{(string.IsNullOrWhiteSpace(player) ? "" : " (" + player.Trim() + ")")}";
        string? err;
        try { err = await send(by, line, why).ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }
        if (err != null)
        {
            _log.LogWarning("story: {Line} from {By} failed: {Err}", line, by, err);
            return (Outcome.Failed, by, err);
        }
        _log.LogInformation("story: time set to {Ticks} from {By} ({Why})", ticks, by, why);
        return (Outcome.Ok, by, null);
    }
}
