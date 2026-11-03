namespace MineClaw.Agent.Rag;

/// <summary>One memory a brain just read: its doc id (the <c>#id</c> the prompt shows), kind and retrieval score.</summary>
public sealed record MemoryAccessHit(long Id, string Kind, double? Score);

/// <summary>
/// Observer for memory reads (the web viewer's "memory activity" map): auto-retrieval's <c>[memory]</c> block, <c>recall</c>,
/// <c>recall_detail</c> and the skill search call <see cref="Note"/> with what they put in front of a brain. Display only: it
/// never changes retrieval, ranking or budgets, and costs one null check when nobody listens. <c>owner</c> is the retriever or
/// index instance that read (one per runtime), so a listener can tell the hive's brains apart in one process.
/// </summary>
public static class MemoryAccess
{
    /// <summary>(owner, source: rag|recall|tool|search, hits). Raised on the retrieving flow; handlers must be quick and never throw.</summary>
    public static event Action<object, string, IReadOnlyList<MemoryAccessHit>>? Accessed;

    public static void Note(object owner, string source, IEnumerable<RagHit> hits)
    {
        if (Accessed is not { } handler) return;
        try
        {
            var list = hits.Select(h => new MemoryAccessHit(h.Id, h.Kind, h.Score)).ToList();
            if (list.Count > 0) handler(owner, source, list);
        }
        catch (Exception) { /* an observer never breaks a read */ }
    }
}
