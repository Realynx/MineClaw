namespace MineClaw.Raptor.Model;

/// <summary>
/// Input to build/insert. The text is chunked with split_text (≤ ChunkMaxTokens per leaf, paper §3) unless
/// <see cref="Chunk"/> is false; every chunk inherits the metadata.
/// </summary>
public sealed record RaptorDocument(string Text, string? Key = null, string? Kind = null, string? Source = null, bool Pinned = false)
{
    /// <summary>False keeps the text as one leaf even if it exceeds ChunkMaxTokens.</summary>
    public bool Chunk { get; init; } = true;
    /// <summary>Optional precomputed embedding (only honoured when the text yields a single chunk).</summary>
    public float[]? Embedding { get; init; }
}

/// <summary>Metadata filter applied at the leaf level (see README "Filters").</summary>
public sealed record RaptorFilter
{
    /// <summary>Allowed leaf kinds (case-insensitive); null = any.</summary>
    public IReadOnlyCollection<string>? Kinds { get; init; }
    /// <summary>Allowed leaf sources (case-insensitive); null = any.</summary>
    public IReadOnlyCollection<string>? Sources { get; init; }
    /// <summary>Only pinned leaves.</summary>
    public bool PinnedOnly { get; init; }
    /// <summary>Extra leaf predicate.</summary>
    public Func<RaptorNode, bool>? Predicate { get; init; }

    public bool IsEmpty => Kinds == null && Sources == null && !PinnedOnly && Predicate == null;

    public bool Matches(RaptorNode leaf)
    {
        if (PinnedOnly && !leaf.Pinned) return false;
        if (Kinds != null && !Kinds.Any(k => string.Equals(k, leaf.Kind, StringComparison.OrdinalIgnoreCase))) return false;
        if (Sources != null && !Sources.Any(s => string.Equals(s, leaf.Source, StringComparison.OrdinalIgnoreCase))) return false;
        return Predicate == null || Predicate(leaf);
    }
}
