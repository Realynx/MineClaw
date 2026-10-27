using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// Rate-limits a repeating failure log: the first occurrence of a failure (type + message) logs its stack; repeats within
/// <see cref="Window"/> are counted and summarised once per window without a stack. A different failure logs at once.
/// </summary>
internal sealed class FailureThrottle(string what)
{
    private readonly object _lock = new();
    private string? _key;
    private DateTimeOffset _windowStart;
    private int _suppressed;

    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Returns true when something was logged.</summary>
    public bool Report(Exception ex, DateTimeOffset now, ILogger log)
    {
        var key = ex.GetType().FullName + ": " + ex.Message;
        int suppressed;
        bool first;
        lock (_lock)
        {
            first = key != _key;
            if (!first && now - _windowStart < Window) { _suppressed++; return false; }
            suppressed = first ? 0 : _suppressed;
            _key = key;
            _windowStart = now;
            _suppressed = 0;
        }
        if (first) log.LogWarning(ex, "{What}", what);
        else log.LogWarning("{What} again ({N} more in the last {S:0}s): {Type}: {Message}", what, suppressed + 1, Window.TotalSeconds, ex.GetType().Name, ex.Message);
        return true;
    }
}
