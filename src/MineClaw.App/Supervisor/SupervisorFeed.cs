namespace MineClaw.App.Supervisor;

/// <summary>
/// The danger/event feed the supervisor polls (<c>/api/events?since=id</c>): dangers, deaths, kicks, server moves and hive
/// notices, pushed by the host's event hooks. Bounded ring, thread-safe, ids increase.
/// </summary>
public sealed class SupervisorFeed(int capacity = 400, TimeProvider? time = null)
{
    private readonly LinkedList<FeedEvent> _lines = new();
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _next;

    public void Add(string kind, string text, string level = "info")
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var line = text.Replace('\n', ' ').Replace("\r", "");
        if (line.Length > 400) line = line[..399] + "…";
        lock (_lock)
        {
            _lines.AddLast(new FeedEvent(++_next, _time.GetUtcNow(), kind, level, line));
            while (_lines.Count > capacity) _lines.RemoveFirst();
        }
    }

    public IReadOnlyList<FeedEvent> Since(long id, int max)
    {
        lock (_lock) return _lines.Where(e => e.Id > id).TakeLast(Math.Max(1, max)).ToList();
    }
}
