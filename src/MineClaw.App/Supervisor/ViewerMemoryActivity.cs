using System.Text.RegularExpressions;
using MineClaw.Agent.Telemetry;

namespace MineClaw.App.Supervisor;

/// <summary>
/// One memory read by a brain (<c>/api/viewer/memory-activity</c> <c>events</c>). <see cref="MemoryId"/> is the doc id the prompt
/// shows as <c>#id</c>; <see cref="RaptorNodeId"/> is that doc's RAPTOR tree node (<c>docs.raptor_idx</c>) as of the read of the
/// route: a leaf memory → its leaf node, a RAPTOR summary doc → its summary node, null = not in the tree. <see cref="Source"/>:
/// <c>rag</c> (the auto <c>[memory]</c> block) | <c>recall</c> | <c>tool</c> (<c>recall_detail</c>) | <c>search</c> (skill search) | <c>other</c>.
/// <see cref="Turn"/>: the brain-context turn that read it (rag: the turn it was retrieved for), null = unknown or capture off.
/// </summary>
public sealed record ViewerMemoryEvent(long Seq, DateTimeOffset At, string Brain, string? Body, string MemoryKind, long MemoryId, int? RaptorNodeId,
    string Source, double? Score, long? Turn);

/// <summary>
/// A memory present in a brain's live context window right now (<c>in_context</c>): when it entered, when a prompt last carried
/// it (<see cref="LastSeenAt"/> = the newest brain call's time), how many brain turns it has ridden along, and how many messages
/// from the end of the window its newest copy sits (0 = the newest message).
/// </summary>
public sealed record ViewerInContext(string Brain, string MemoryKind, long MemoryId, int? RaptorNodeId, DateTimeOffset AddedAt, DateTimeOffset LastSeenAt,
    int TurnsInContext, int? PositionFromEnd);

/// <summary>What a doc id is right now: its kind and RAPTOR node (null = not in the tree).</summary>
public readonly record struct MemoryDocInfo(string Kind, int? RaptorNode);

/// <summary>
/// The viewer's memory map data: a ring (2000) of memory reads per brain, fed by <see cref="Agent.Rag.MemoryAccess"/> through
/// <see cref="ViewerTaps"/>, and each brain's in-context set, derived from its brain-context capture window (no hooks in the
/// prompt builder): the <c>#id</c> lines of <c>[memory]</c> blocks and of <c>recall</c>/<c>recall_detail</c> results in the
/// newest brain call's messages. A memory leaves <c>in_context</c> when compaction or eviction drops its messages.
/// </summary>
public sealed class ViewerMemoryActivity(int capacity = 2000, TimeProvider? time = null)
{
    private static readonly Regex IdLine = new(@"^#(?<id>\d+)\b", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly string[] RecallTools = ["recall", "recall_detail"];

    private readonly LinkedList<ViewerMemoryEvent> _events = new();
    private readonly Dictionary<(string Brain, long Id), (DateTimeOffset AddedAt, long? AddedTurn)> _present = new();
    private readonly object _lock = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _next;

    public int Capacity => capacity;

    /// <summary>Records reads (from <see cref="Agent.Rag.MemoryAccess.Accessed"/>); the RAPTOR node is resolved when the route is read.</summary>
    public void Add(string brain, string? body, string source, IEnumerable<Agent.Rag.MemoryAccessHit> hits)
    {
        var at = _time.GetUtcNow();
        lock (_lock)
            foreach (var h in hits)
            {
                _events.AddLast(new ViewerMemoryEvent(++_next, at, brain, body, h.Kind, h.Id, null, source, h.Score, null));
                while (_events.Count > capacity) _events.RemoveFirst();
            }
    }

    public long Last { get { lock (_lock) return _next; } }

    /// <summary>Reads after <paramref name="since"/> (oldest first, at most <paramref name="max"/>), optionally one brain's.</summary>
    public IReadOnlyList<ViewerMemoryEvent> Since(long since, string? brain, int max)
    {
        lock (_lock)
            return _events.Where(e => e.Seq > since && (brain == null || e.Brain.Equals(brain, StringComparison.OrdinalIgnoreCase)))
                .Take(Math.Max(1, max)).ToList();
    }

    /// <summary>
    /// The turn a read belongs to, from the brain's capture page: the brain turn running at that moment, else (a rag block is
    /// built just before its call) the first brain turn that started within 30 s after it.
    /// </summary>
    public static long? TurnOf(DateTimeOffset at, BrainContextPage? page)
    {
        if (page == null) return null;
        var brain = page.Turns.Where(t => t.Lane == "brain").ToList();
        var running = brain.LastOrDefault(t => t.Start <= at && (t.End ?? page.At) >= at);
        if (running != null) return running.Id;
        return brain.FirstOrDefault(t => t.Start >= at && t.Start - at <= TimeSpan.FromSeconds(30))?.Id;
    }

    /// <summary>
    /// Updates one brain's presence table from its capture page and returns what is in its window now. New ids enter at their
    /// latest read in the ring (else the window's time); ids no longer in the window are dropped (evicted).
    /// </summary>
    public IReadOnlyList<ViewerInContext> InContext(string brain, BrainContextPage? page, Func<IReadOnlyCollection<long>, IReadOnlyDictionary<long, MemoryDocInfo>>? docs = null)
    {
        var window = page?.Window;
        var found = window == null ? new Dictionary<long, int>() : MemoryIds(window);
        var info = found.Count > 0 && docs != null ? docs(found.Keys) : new Dictionary<long, MemoryDocInfo>();
        var brainTurns = page?.Turns.Where(t => t.Lane == "brain").Select(t => t.Id).ToList() ?? [];
        var list = new List<ViewerInContext>();
        lock (_lock)
        {
            foreach (var key in _present.Keys.Where(k => k.Brain.Equals(brain, StringComparison.OrdinalIgnoreCase) && !found.ContainsKey(k.Id)).ToList())
                _present.Remove(key);
            foreach (var (id, fromEnd) in found.OrderBy(kv => kv.Value))
            {
                var key = (brain, id);
                if (!_present.TryGetValue(key, out var p))
                {
                    var read = _events.Reverse().FirstOrDefault(e => e.MemoryId == id && e.Brain.Equals(brain, StringComparison.OrdinalIgnoreCase));
                    var addedAt = read?.At ?? window!.At;
                    p = (addedAt, TurnOf(addedAt, page) ?? window!.TurnId);
                    _present[key] = p;
                }
                int turns = p.AddedTurn is { } t0 ? Math.Max(1, brainTurns.Count(t => t >= t0 && t <= window!.TurnId)) : 1;
                var kind = info.TryGetValue(id, out var di) ? di.Kind : KindOfRead(brain, id) ?? "memory";
                list.Add(new ViewerInContext(brain, kind, id, info.TryGetValue(id, out var dr) ? dr.RaptorNode : null, p.AddedAt, window!.At, turns, fromEnd));
            }
        }
        return list;
    }

    // caller holds _lock
    private string? KindOfRead(string brain, long id) =>
        _events.Reverse().FirstOrDefault(e => e.MemoryId == id && e.Brain.Equals(brain, StringComparison.OrdinalIgnoreCase))?.MemoryKind;

    /// <summary>
    /// Memory ids in a window → messages from the end of its newest copy: <c>#id</c> lines in text blocks that carry a
    /// <c>[memory]</c> block, and in the results of <c>recall</c>/<c>recall_detail</c> calls.
    /// </summary>
    public static Dictionary<long, int> MemoryIds(CtxWindow window)
    {
        var found = new Dictionary<long, int>();
        var recallCalls = new HashSet<string>(StringComparer.Ordinal);
        int n = window.Messages.Count;
        for (int i = 0; i < n; i++)
        {
            int fromEnd = n - 1 - i;
            foreach (var b in window.Messages[i].Blocks)
            {
                if (b.Kind == "tool_use" && b.Id != null && b.Name != null && RecallTools.Contains(b.Name)) recallCalls.Add(b.Id);
                string? text = b.Kind switch
                {
                    "text" when b.Text != null && (b.Label == "memory" || b.Text.Contains("[memory]", StringComparison.Ordinal)) => MemorySection(b.Text),
                    "tool_result" when b.Id != null && recallCalls.Contains(b.Id) => b.Text,
                    _ => null,
                };
                if (text == null) continue;
                foreach (Match m in IdLine.Matches(text))
                    if (long.TryParse(m.Groups["id"].Value, out var id)) found[id] = fromEnd; // later messages overwrite: the newest copy
            }
        }
        return found;
    }

    // The [memory] block's lines (it runs to the next blank line or "[section]" header).
    private static string MemorySection(string text)
    {
        int i = text.IndexOf("[memory]", StringComparison.Ordinal);
        if (i < 0) return text;
        var rest = text[(i + "[memory]".Length)..];
        var end = Regex.Match(rest, @"\n\s*\n|\n\[[a-z_ ]+\]");
        return end.Success ? rest[..end.Index] : rest;
    }
}
