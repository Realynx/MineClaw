namespace MineClaw.Agent.Admin.Shards.Packs;

/// <summary>One file of a default-packs folder compared with a fresh rebuild.</summary>
/// <param name="Status">same | differs | missing (committed but not rebuilt, or rebuilt but not committed)</param>
public sealed record PackFileCheck(string File, string Status, string? Note = null)
{
    public bool Ok => Status == "same";
}

/// <summary>
/// <c>ShardPacks verify</c>: the committed packs (<c>shards/defaults</c>) must be byte-identical to a rebuild from the sources, so a stale
/// pack (an SDK <c>def</c>, a knowledge doc or a facts file changed without a rebuild) fails the check.
/// </summary>
public static class PackVerifier
{
    /// <summary>Compares every <c>.mcshard</c> and <c>index.json</c> of both folders (sorted by name); a same file is also opened and verified.</summary>
    public static IReadOnlyList<PackFileCheck> Compare(string committedDir, string rebuiltDir)
    {
        static IEnumerable<string> Files(string d) => Directory.Exists(d)
            ? Directory.GetFiles(d).Select(Path.GetFileName).OfType<string>()
                .Where(f => f.EndsWith(ShardFile.Extension, StringComparison.OrdinalIgnoreCase) || f == PackIndex.FileName)
            : [];
        var names = Files(committedDir).Concat(Files(rebuiltDir)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var list = new List<PackFileCheck>();
        foreach (var f in names)
        {
            string mine = Path.Combine(committedDir, f), fresh = Path.Combine(rebuiltDir, f);
            if (!File.Exists(fresh)) { list.Add(new PackFileCheck(f, "missing", "committed, but the sources don't build it")); continue; }
            if (!File.Exists(mine)) { list.Add(new PackFileCheck(f, "missing", "built from the sources, but not committed")); continue; }
            bool same = File.ReadAllBytes(mine).AsSpan().SequenceEqual(File.ReadAllBytes(fresh));
            if (same && f != PackIndex.FileName) ShardFile.Read(mine);
            list.Add(new PackFileCheck(f, same ? "same" : "differs", same ? null : "stale: run `build` and commit"));
        }
        return list;
    }
}
