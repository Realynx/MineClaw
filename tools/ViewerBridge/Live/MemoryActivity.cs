using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Raptor.Model;
using MineClaw.ViewerBridge.Supervisor;

namespace MineClaw.ViewerBridge.Live;

/// <summary>One memory read (supervisor <c>/api/viewer/memory-activity</c> <c>events</c>, memory_id as a string per the contract).</summary>
public sealed record MemoryEvent(long Seq, DateTimeOffset At, string Brain, string? Body, string MemoryKind, string MemoryId, int? RaptorNodeId, string Source,
    double? Score, long? Turn);

/// <summary>A memory in (or evicted from) a brain's live context (<c>in_context</c>); <see cref="PositionFromEnd"/> null = evicted.</summary>
public sealed record InContext(string Brain, string MemoryKind, string MemoryId, int? RaptorNodeId, DateTimeOffset AddedAt, DateTimeOffset LastSeenAt,
    int TurnsInContext, int? PositionFromEnd);

/// <summary>
/// The bridge's copy of the supervisor's memory-access ring: pulled forward from its own cursor (pages of ≤ 500), kept for
/// 24 h (≤ 20 000 events), plus the latest in-context snapshot. One fetcher for both the live stream and the heat aggregate.
/// </summary>
public sealed class MemoryActivityStore(SupervisorClient sup, TimeProvider time)
{
    public const int MaxEvents = 20_000;
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    private readonly LinkedList<MemoryEvent> _events = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<InContext> _inContext = [];
    private JsonArray _inContextJson = [];
    private long _cursor;
    private bool _primed;
    private DateTimeOffset _lastRefresh;

    public sealed record Refresh(IReadOnlyList<MemoryEvent> New, JsonArray NewJson, JsonArray InContextJson, long Last, bool Initial);

    public long Cursor => _cursor;

    /// <summary>Pulls everything new (≤ 10 pages); <paramref name="minInterval"/> skips the call when one just ran.</summary>
    public async Task<Refresh> RefreshAsync(CancellationToken ct, TimeSpan? minInterval = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (minInterval is { } mi && time.GetUtcNow() - _lastRefresh < mi)
                return new Refresh([], [], (JsonArray)_inContextJson.DeepClone(), _cursor, false);
            bool initial = !_primed;
            var fresh = new List<MemoryEvent>();
            var freshJson = new JsonArray();
            JsonArray? inCtx = null;
            for (int page = 0; page < 10; page++)
            {
                var (status, body, _, _) = await sup.GetAsync($"api/viewer/memory-activity?since={_cursor.ToString(CultureInfo.InvariantCulture)}", TimeSpan.FromSeconds(5), ct);
                if (status != 200) throw new SupervisorUnavailableException($"memory activity answered {status}");
                if (JsonNode.Parse(body) is not JsonObject o) break;
                SupervisorProxy.FixMemoryActivity(o);
                inCtx = o["in_context"] as JsonArray ?? [];
                var events = o["events"] as JsonArray ?? [];
                foreach (var e in events.OfType<JsonObject>())
                {
                    var ev = e.Deserialize<MemoryEvent>(BridgeJson.Options);
                    if (ev == null) continue;
                    fresh.Add(ev);
                    freshJson.Add(e.DeepClone());
                    _events.AddLast(ev);
                }
                long last = o["last"]?.GetValue<long>() ?? _cursor;
                // The bot restarted (its ring starts over): take its numbering from here.
                if (last < _cursor) { _cursor = last; break; }
                _cursor = last;
                if (events.Count < 500) break;
            }
            if (inCtx != null)
            {
                _inContextJson = inCtx;
                _inContext = inCtx.OfType<JsonObject>().Select(x => x.Deserialize<InContext>(BridgeJson.Options)).OfType<InContext>().ToList();
            }
            var cutoff = time.GetUtcNow() - Keep;
            while (_events.Count > 0 && (_events.Count > MaxEvents || _events.First!.Value.At < cutoff)) _events.RemoveFirst();
            _primed = true;
            _lastRefresh = time.GetUtcNow();
            return new Refresh(fresh, freshJson, (JsonArray)_inContextJson.DeepClone(), _cursor, initial);
        }
        finally { _gate.Release(); }
    }

    public (IReadOnlyList<MemoryEvent> Events, IReadOnlyList<InContext> InContext) Snapshot()
    {
        _gate.Wait();
        try { return (_events.ToList(), _inContext); }
        finally { _gate.Release(); }
    }
}

public sealed record NodeBrainHeat(string Brain, DateTimeOffset? LastAccessAt, bool InContext, double? StalenessS, bool Evicted);
public sealed record NodeHeat(int NodeId, DateTimeOffset? LastAccessAt, int AccessCount, double Heat, IReadOnlyList<NodeBrainHeat> Brains);
public sealed record MemoryHeatResponse(DateTimeOffset At, string? Brain, int WindowS, double HalfLifeS, string StateKey, IReadOnlyList<string> Brains, IReadOnlyList<NodeHeat> Nodes);

/// <summary>
/// Memory heat per RAPTOR node (api.md §6): the accesses of the window (optionally one brain's), each mapped to its tree node
/// (the bridge's own doc → node map first, so the ids match <c>/v1/raptor/graph</c>; the event's node id otherwise), then
/// propagated to every ancestor through all parents. Per node: last access (max), access count (distinct accesses below),
/// <c>heat = 0.5^((at − last)/half_life)</c> (so max-propagated), and per brain the context state: in context (any below),
/// staleness = seconds since the freshest in-context copy below was last carried, evicted = was in context and dropped.
/// </summary>
public static class MemoryHeat
{
    public static MemoryHeatResponse Compute(IReadOnlyList<MemoryEvent> events, IReadOnlyList<InContext> inContext, RaptorTree? tree, Func<long, int?> nodeOfDoc,
        string stateKey, DateTimeOffset at, int windowS, double halfLifeS, string? brain)
    {
        var since = at - TimeSpan.FromSeconds(windowS);
        bool BrainOk(string b) => brain == null || b.Equals(brain, StringComparison.OrdinalIgnoreCase);
        int? NodeOf(string memoryId, int? fallback) =>
            long.TryParse(memoryId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var docId) && nodeOfDoc(docId) is { } n ? n : fallback;

        var lastAccess = new Dictionary<int, DateTimeOffset>();
        var counts = new Dictionary<int, int>();
        // (node, brain) → state
        var perBrain = new Dictionary<(int Node, string Brain), (DateTimeOffset? Last, bool In, double? Stale, bool Evicted)>();
        var brains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in events)
        {
            if (e.At < since || e.At > at.AddSeconds(5) || !BrainOk(e.Brain)) continue;
            brains.Add(e.Brain);
            if (NodeOf(e.MemoryId, e.RaptorNodeId) is not { } leaf) continue;
            foreach (var n in Ancestry(tree, leaf))
            {
                if (!lastAccess.TryGetValue(n, out var la) || e.At > la) lastAccess[n] = e.At;
                counts[n] = counts.GetValueOrDefault(n) + 1;
                var k = (n, e.Brain);
                var cur = perBrain.GetValueOrDefault(k);
                perBrain[k] = (cur.Last is { } l && l >= e.At ? l : e.At, cur.In, cur.Stale, cur.Evicted);
            }
        }
        foreach (var c in inContext)
        {
            if (!BrainOk(c.Brain)) continue;
            bool evicted = c.PositionFromEnd == null;
            if (evicted && c.LastSeenAt < since) continue;
            brains.Add(c.Brain);
            if (NodeOf(c.MemoryId, c.RaptorNodeId) is not { } leaf) continue;
            double stale = Math.Max(0, (at - c.LastSeenAt).TotalSeconds);
            foreach (var n in Ancestry(tree, leaf))
            {
                var k = (n, c.Brain);
                var cur = perBrain.GetValueOrDefault(k);
                bool inCtx = cur.In || !evicted;
                double? s = evicted ? cur.Stale : cur.Stale is { } p ? Math.Min(p, stale) : stale;
                perBrain[k] = (cur.Last, inCtx, s, !inCtx && (cur.Evicted || evicted));
                lastAccess.TryAdd(n, default);
                counts.TryAdd(n, 0);
            }
        }

        var nodes = lastAccess.Keys.Order().Select(n =>
        {
            var la = lastAccess[n];
            DateTimeOffset? last = la == default ? null : la;
            double heat = last is { } t && halfLifeS > 0 ? Math.Round(Math.Pow(0.5, Math.Max(0, (at - t).TotalSeconds) / halfLifeS), 5) : 0;
            var bs = perBrain.Where(kv => kv.Key.Node == n).OrderBy(kv => kv.Key.Brain, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new NodeBrainHeat(kv.Key.Brain, kv.Value.Last, kv.Value.In, kv.Value.In ? Math.Round(kv.Value.Stale ?? 0, 1) : null, kv.Value.Evicted)).ToList();
            return new NodeHeat(n, last, counts.GetValueOrDefault(n), heat, bs);
        }).ToList();
        return new MemoryHeatResponse(at, brain, windowS, halfLifeS, stateKey, brains.ToList(), nodes);
    }

    /// <summary>The node and every ancestor through all parents (soft clusters included), each once.</summary>
    public static IEnumerable<int> Ancestry(RaptorTree? tree, int node)
    {
        if (tree == null || !tree.TryGet(node, out _)) { yield return node; yield break; }
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (!seen.Add(i)) continue;
            yield return i;
            if (tree.TryGet(i, out var n)) foreach (var p in n.Parents) stack.Push(p);
        }
    }
}
