namespace MineClaw.App.Supervisor;

/// <summary>
/// One line of the web viewer's live feed (<c>/api/viewer/feed</c>). <see cref="Kind"/>: <c>chat</c> (a player's server line) |
/// <c>whisper</c> (to a bot) | <c>said</c> (a bot's own outgoing text, whole) | <c>internal</c> (quiet-turn text that stayed private) |
/// <c>muted</c> (text a gate kept out of chat) | <c>system</c> (server notices, joins and leaves) | <c>viewer</c> (a line typed in the
/// viewer). <see cref="Origin"/>: <c>game</c> | <c>bot</c> | <c>viewer</c>.
/// </summary>
public sealed record ViewerLine(long Seq, DateTimeOffset At, string Bot, string Kind, string? From, string Text, string Origin, string? RequestId = null);

/// <summary>
/// The viewer's structured chat feed: a bounded ring (1000) with increasing <see cref="ViewerLine.Seq"/>, fed by every bot's router
/// chat, <c>ChatRouter.Said</c>, <c>BotAgent.InternalSpeech</c>/<c>Muted</c> and accepted viewer lines (<see cref="ViewerTaps"/>).
/// Thread-safe; texts are one line, capped.
/// </summary>
public sealed class ViewerFeed(int capacity = 1000, TimeProvider? time = null)
{
    public const int MaxText = 600;
    private readonly LinkedList<ViewerLine> _lines = new();
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _next;

    public int Capacity => capacity;

    /// <summary>Adds a line; returns its seq (0 = empty text, nothing added).</summary>
    public long Add(string bot, string kind, string? from, string text, string origin, string? requestId = null)
    {
        var line = OneLine(text);
        if (line.Length == 0) return 0;
        lock (_lock)
        {
            var at = _time.GetUtcNow();
            // A hive bot's public line comes back from the server after its Said (whole text, split into ≤100-char lines): the
            // server copy adds nothing when that bot's own feed already has it.
            if (kind == "said" && origin == "game" && _lines.Reverse().TakeWhile(l => at - l.At < TimeSpan.FromSeconds(20))
                    .Any(l => l.Kind == "said" && l.Origin == "bot" && l.Bot.Equals(bot, StringComparison.OrdinalIgnoreCase) && l.Text.Contains(line, StringComparison.Ordinal)))
                return 0;
            // Quiet-turn text raises Muted then InternalSpeech with the same words: one "internal" line, not two.
            if (kind == "internal" && _lines.Last is { } last && last.Value is { Kind: "muted" } m && m.Text == line
                && m.Bot.Equals(bot, StringComparison.OrdinalIgnoreCase) && at - m.At < TimeSpan.FromSeconds(5))
            {
                last.Value = m with { Kind = "internal" };
                return m.Seq;
            }
            var seq = ++_next;
            _lines.AddLast(new ViewerLine(seq, at, bot, kind, from, line, origin, requestId));
            while (_lines.Count > capacity) _lines.RemoveFirst();
            return seq;
        }
    }

    /// <summary>Lines after <paramref name="since"/>, oldest first, at most <paramref name="max"/> (page forward with the last seq).</summary>
    public IReadOnlyList<ViewerLine> Since(long since, int max)
    {
        lock (_lock) return _lines.Where(l => l.Seq > since).Take(Math.Max(1, max)).ToList();
    }

    public long Last { get { lock (_lock) return _next; } }

    private static string OneLine(string? text)
    {
        var t = (text ?? "").Replace("\r", "").Replace('\n', ' ').Trim();
        return t.Length > MaxText ? t[..(MaxText - 1)] + "…" : t;
    }
}
