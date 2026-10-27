using System.Text;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Rag;

/// <summary>
/// Zero-tool-call retrieval: before each turn, one local hybrid query built from the batch (speakers + message/task
/// text) plus the bot's position; the top hits that pass the relevance gate become a compact <c>[memory]</c> block in
/// the newest user message. Hard-capped (<see cref="RagOptions.AutoMaxChars"/>), and deduped against what's already in
/// the window: episodes still in context and (main tier) anything injected in a live episode. Costs zero Claude tokens
/// to compute; the block itself is the only token cost and is omitted when nothing is relevant.
/// </summary>
public sealed class AutoRetriever
{
    private static readonly string[] SpatialKinds = ["waypoint", "structure", "world"];

    private readonly IRetrievalIndex _index;
    private readonly RagOptions _o;
    private readonly ContextManager _context;
    private readonly EpisodeIndexer? _episodes;
    private readonly Dictionary<long, long> _injectedAt = new();
    private readonly object _lock = new();

    public AutoRetriever(RagIndex index, ContextManager context, EpisodeIndexer? episodes = null)
        : this(index, index.Options, context, episodes)
    {
    }

    /// <summary>Any retriever (flat hybrid, tree, or a fusion of both) behind <see cref="IRetrievalIndex"/>.</summary>
    public AutoRetriever(IRetrievalIndex index, RagOptions options, ContextManager context, EpisodeIndexer? episodes = null)
    {
        _index = index;
        _o = options;
        _context = context;
        _episodes = episodes;
    }

    /// <summary>Query text for a batch: who's talking and what about (event tags excluded).</summary>
    public static string QueryText(EventBatch batch)
    {
        var sb = new StringBuilder();
        foreach (var e in batch.Events)
        {
            if (e.Kind is EventKind.IdleTick) continue;
            if (e.Player != null) sb.Append(e.Player).Append(' ');
            if (e.Kind is not (EventKind.PlayerJoined or EventKind.PlayerLeft)) sb.Append(e.Text).Append(' ');
        }
        return sb.ToString().Trim();
    }

    /// <returns>"[memory]\n#id ..." or "" when nothing relevant.</returns>
    public async Task<string> BuildAsync(EventBatch batch, ModelTier tier, BlockPos? position, IEnumerable<string>? ignoreWords, CancellationToken ct)
    {
        if (!_o.AutoRetrieve || _o.AutoTopK <= 0) return "";
        var text = QueryText(batch);
        if (text.Length == 0 && position == null) return "";

        long oldest = _context.OldestSeq, next = _context.NextSeq;
        bool Excluded(long id)
        {
            if (_episodes?.IsInWindow(id) == true) return true;
            if (tier != ModelTier.Main) return false; // cheap transcript drops [memory] lines, so re-inject there
            lock (_lock) return _injectedAt.TryGetValue(id, out var seq) && seq >= oldest && seq < next;
        }

        var hits = await _index.SearchAsync(new RagQuery
        {
            Text = text,
            Near = position is { } p && _o.AutoNearRadius > 0 ? (p.X, p.Y, p.Z) : null,
            Radius = _o.AutoNearRadius,
            NearKinds = SpatialKinds,
            // Spatial memories of the other dimension never ride along (their coordinates would mislead); text-only docs pass.
            Dimension = _index.CurrentDim,
            Limit = _o.AutoTopK,
            Strict = true,
            IgnoreWords = ignoreWords,
            Exclude = Excluded,
        }, ct).ConfigureAwait(false);
        if (hits.Count == 0) return "";

        var sb = new StringBuilder("[memory]");
        var used = new List<long>();
        foreach (var h in hits)
        {
            var line = _index.FormatHit(h, _o.AutoLineChars);
            if (sb.Length + 1 + line.Length > _o.AutoMaxChars) continue;
            sb.Append('\n').Append(line);
            used.Add(h.Id);
        }
        if (used.Count == 0) return "";
        lock (_lock)
        {
            foreach (var id in used) _injectedAt[id] = next;
            if (_injectedAt.Count > 2000)
                foreach (var k in _injectedAt.Where(kv => kv.Value < oldest).Select(kv => kv.Key).ToList()) _injectedAt.Remove(k);
        }
        return sb.ToString();
    }
}
