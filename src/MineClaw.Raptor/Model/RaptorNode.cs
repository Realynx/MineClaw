namespace MineClaw.Raptor.Model;

/// <summary>
/// tree_structures.py <c>Node</c> (index, text, children set, embeddings) plus layer, token count, parents
/// (derived; soft clustering gives a node several parents), leaf metadata and maintenance flags.
/// </summary>
public sealed class RaptorNode
{
    public RaptorNode(int index, int layer, string text, float[] embedding, int tokenCount)
    {
        Index = index;
        Layer = layer;
        Text = text;
        Embedding = embedding;
        TokenCount = tokenCount;
    }

    public int Index { get; internal set; }
    /// <summary>0 = leaf chunk, 1..NumLayers = summaries.</summary>
    public int Layer { get; internal set; }
    public string Text { get; internal set; }
    /// <summary>L2-normalized embedding of <see cref="Text"/>.</summary>
    public float[] Embedding { get; internal set; }
    public int TokenCount { get; internal set; }
    /// <summary>Child node indices (one layer down). Sorted for deterministic traversal order.</summary>
    public SortedSet<int> Children { get; } = new();
    /// <summary>Parent node indices (one layer up). Derived from <see cref="Children"/>.</summary>
    public SortedSet<int> Parents { get; } = new();

    // -------- leaf metadata (null on summaries) --------

    /// <summary>Caller's document key (e.g. the SQLite docs.ref); shared by all chunks of one document.</summary>
    public string? Key { get; internal set; }
    /// <summary>Chunk position within its document.</summary>
    public int Chunk { get; internal set; }
    public string? Kind { get; internal set; }
    public string? Source { get; internal set; }
    /// <summary>Pinned leaves are always offered to retrieval first (see RaptorQuery.IncludePinned).</summary>
    public bool Pinned { get; internal set; }

    // -------- maintenance --------

    /// <summary>Children changed since this summary was written; RefreshDirty re-summarizes and re-embeds it.</summary>
    public bool Dirty { get; internal set; }
    /// <summary>The summary is an extractive placeholder because the summary budget ran out.</summary>
    public bool SummaryPending { get; internal set; }
    /// <summary>For leaves inserted after the last build: monotonically increasing insertion sequence (0 = from build).</summary>
    public long InsertedSeq { get; internal set; }

    public bool IsLeaf => Layer == 0;

    public override string ToString() => $"#{Index} L{Layer} [{Children.Count}] {(Text.Length > 40 ? Text[..40] + "…" : Text)}";
}
