using MineClaw.Agent.Context;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Rag;

/// <summary>
/// Writes every finished episode (mechanical summary: who said what, what was done, outcome — zero tokens) and
/// every compacted journal into <see cref="RagIndex"/> as "episode" docs, so the rolling window can stay small and
/// older history is retrieved on demand. Tracks which episode docs are still in the window (for dedupe).
/// </summary>
public sealed class EpisodeIndexer
{
    public const string Kind = "episode";

    /// <summary>Doc ref prefix of test-driven episodes (source=test).</summary>
    public const string TestRefPrefix = "eptest:";

    private readonly RagIndex _index;
    private readonly ContextManager _context;
    private readonly RagOptions _o;
    private readonly Func<BlockPos?>? _position;
    private readonly ILogger _log;
    private readonly string _run;
    private readonly Dictionary<long, long> _docSeq = new();
    private readonly object _lock = new();
    private long _journalDoc;
    private int _journals;

    public EpisodeIndexer(RagIndex index, ContextManager context, Func<BlockPos?>? position = null, TimeProvider? time = null, ILogger? log = null)
    {
        _index = index;
        _context = context;
        _o = index.Options;
        _position = position;
        _log = log ?? NullLogger.Instance;
        _run = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyyMMddHHmmss");
        context.Committed += OnCommitted;
        context.Compacted += OnCompacted;
    }

    /// <summary>True if the doc is an episode/journal whose content is still in the rolling window.</summary>
    public bool IsInWindow(long docId)
    {
        lock (_lock)
        {
            if (docId == _journalDoc && _context.Journal.Length > 0) return true;
            return _docSeq.TryGetValue(docId, out var seq) && seq >= _context.OldestSeq;
        }
    }

    /// <summary>Newest journal stored (for restoring continuity after a restart).</summary>
    public string? LatestJournal() => _index.Recent(Kind, null, 1, "journal:").FirstOrDefault()?.Body is { } b ? StripDate(b) : null;

    private static string StripDate(string body)
    {
        int i = body.IndexOf(": ", StringComparison.Ordinal);
        return i > 0 && i < 20 ? body[(i + 2)..] : body;
    }

    private void OnCommitted(Episode e)
    {
        try
        {
            var lines = ContextManager.Flatten(e, 100).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return;
            BlockPos? pos = null;
            try { pos = _position?.Invoke(); } catch (Exception) { }
            var title = TextUtil.Truncate(lines[0], 90);
            var body = $"{e.Started.ToLocalTime():MM-dd HH:mm}" + (pos != null ? $" @{pos}" : "") + ": " + string.Join(" | ", lines.Select(l => l.Trim()));
            // Test-driven episodes (console /as impersonating a player) stay searchable but are tagged and kept out of reflection.
            var id = _index.Upsert(new RagDoc($"{(e.IsTest ? TestRefPrefix : "ep:")}{_run}:{e.Seq}", Kind, title,
                TextUtil.Truncate((e.IsTest ? "[test] " : "") + body, _o.EpisodeSummaryChars))
            {
                Source = e.IsTest ? "test" : "episode",
                Bounds = pos != null ? Box.Point(pos.X, pos.Y, pos.Z) : null,
                At = e.Started,
            });
            lock (_lock) _docSeq[id] = e.Seq;
        }
        catch (Exception ex) { _log.LogWarning(ex, "episode indexing failed"); }
    }

    private void OnCompacted(IReadOnlyList<Episode> folded, string journal)
    {
        try
        {
            if (journal.Length == 0) return;
            int n = Interlocked.Increment(ref _journals);
            var id = _index.Upsert(new RagDoc($"journal:{_run}:{n}", Kind, "journal", $"{DateTimeOffset.Now:MM-dd HH:mm}: {journal}") { Source = "journal" });
            lock (_lock) _journalDoc = id;
        }
        catch (Exception ex) { _log.LogWarning(ex, "journal indexing failed"); }
    }
}
