namespace MineClaw.Agent.Rag.Raptor;

/// <summary>
/// Which docs become RAPTOR leaves. In: episodes and activity rollups, knowledge chunks, structures, notes/player
/// facts and the seed game-data corpus. Out (flat FTS/vector/spatial and structured tables only): raw activity events
/// and container snapshots (never docs at all, or kind "world"), world observations, waypoints/coordinates, routines,
/// chat, summaries themselves, test-sourced data and the personality-change log.
/// </summary>
public sealed class RaptorTreePolicy
{
    private readonly HashSet<string> _kinds;
    private readonly HashSet<string> _excludedSources;
    private readonly string[] _excludedPrefixes;

    public RaptorTreePolicy(RaptorMemoryOptions options)
    {
        _kinds = new HashSet<string>(options.TreeKinds, StringComparer.OrdinalIgnoreCase);
        _kinds.Remove(RagIndex.SummaryKind);
        _excludedSources = new HashSet<string>(options.ExcludeSources, StringComparer.OrdinalIgnoreCase);
        _excludedPrefixes = options.ExcludeRefPrefixes;
    }

    public IReadOnlyCollection<string> Kinds => _kinds;
    public IReadOnlyCollection<string> ExcludedSources => _excludedSources;
    public IReadOnlyList<string> ExcludedRefPrefixes => _excludedPrefixes;

    public bool Includes(string reference, string kind, string? source, int level)
    {
        if (level != 0 || !_kinds.Contains(kind)) return false;
        if (source != null && _excludedSources.Contains(source)) return false;
        foreach (var p in _excludedPrefixes)
            if (reference.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>The leaf text of a doc (identical to what <see cref="RagIndex"/> embeds, so its vector is reused).</summary>
    public static string LeafText(string title, string body) => title + "\n" + body;
}
