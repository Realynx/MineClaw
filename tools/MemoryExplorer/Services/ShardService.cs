using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Rag;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>One <c>.mcshard</c> on disk as the Shards page lists it (the manifest only; a broken file shows why).</summary>
public sealed record ShardFileInfo(string Path, string Name, string Kind, int Records, string? PackVersion, DateTimeOffset? Created, long Bytes,
    string? Description, string? Error, string Counts, string? EmbeddingModel, string? PackMode = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeText => MemoryBank.FormatBytes(Bytes);
    public string CreatedText => Created is { } c ? c.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
    public string KindLabel => Kind switch
    {
        "person" => "about a person",
        "pack" when PackMode != null => $"mode pack {PackMode} ({PackVersion})",
        "pack" => "knowledge pack " + PackVersion,
        "subtree" => "RAPTOR subtree",
        "filter" => "filter",
        _ => Kind,
    };
    public bool Ok => Error == null;
    public bool IsModePack => Kind == "pack" && PackMode != null;
}

/// <summary>One default pack (shards/defaults): its index.json entry, whether the file is there and verifies.</summary>
public sealed record DefaultPackInfo(PackIndexEntry Entry, string Path, string? Error)
{
    public string Name => Entry.Name;
    public bool IsMode => Entry.IsMode;
    public bool IsVersion => !Entry.IsMode;
    public bool Ok => Error == null;
    public string KindLabel => IsMode ? $"mode pack · {Entry.Mode} · {Entry.Version}" : $"version pack · {Entry.Version}";
    public string Description => Entry.Description ?? "";
    public string CountsText => string.Join(" · ", Entry.Counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value:N0} {c.Key}"));
    public string DetailText => $"{MemoryBank.FormatBytes(Entry.Bytes)} · " +
        (Entry.Vectors > 0 ? $"{Entry.Vectors:N0} vectors {Entry.EmbeddingModel}@{Entry.Dims}" : "no vectors") +
        (Entry.NeedsEmbedding > 0 ? $" · {Entry.NeedsEmbedding} to embed on import" : "") +
        $" · {Entry.Builder} · source {Short(Entry.SourceHash)}";
    public string FilesText => Entry.Files.Count > 0 ? "docs: " + string.Join(", ", Entry.Files) : "";

    private static string Short(string? h) => h is { Length: > 12 } ? h[..12] : h ?? "?";
}

/// <summary>
/// Explorer side of memory shards: exports from the opened (read-only) bank with the bot's own <see cref="ShardExporter"/>, the
/// shard folder (<c>data/shards</c>), the person names the bot knows, and the export/import history from the audit trail. Imports
/// go through <see cref="IAdminTarget"/> (local file or live bot), like every other write.
/// </summary>
public static class ShardService
{
    public static string ShardsDir => System.IO.Path.Combine(ExplorerPaths.DataDir, "shards");

    /// <summary>Browser kind key → shard record kind (null = not exportable: world observations, chat, activity, routines).</summary>
    public static string? KindOf(string browserKey) => browserKey != null && ShardKindOf.TryGetValue(browserKey, out var kind) ? kind : null;

    private static readonly Dictionary<string, string> ShardKindOf = new(StringComparer.Ordinal)
    {
        ["facts"] = ShardKinds.Fact,
        ["claims"] = ShardKinds.Claim,
        ["beliefs"] = ShardKinds.Claim,
        ["personality"] = ShardKinds.Trait,
        ["structures"] = ShardKinds.Structure,
        ["places"] = ShardKinds.Place,
        ["lessons"] = ShardKinds.Lesson,
        ["episodes"] = ShardKinds.Episode,
        ["recipes"] = ShardKinds.GameData,
        ["knowledge"] = ShardKinds.Knowledge,
    };

    /// <summary>"PoofimaFox (fox, foxy, master)" style choices: every configured player with the names the bot and players use.</summary>
    public static IReadOnlyList<PersonChoice> People(ExplorerConfig config)
    {
        var o = config.Agent;
        var logins = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && !logins.Contains(p.Trim(), StringComparer.OrdinalIgnoreCase)) logins.Add(p.Trim());
        }
        foreach (var p in o.Self.TrustedPlayers) Add(p);
        foreach (var p in o.Self.RegularPlayers) Add(p);
        foreach (var p in o.Chat.PlayerNicknames.Keys) Add(p);
        return logins.Select(l =>
        {
            var names = ShardCollector.PersonNames([l], o.Chat.PlayerNicknames);
            if (new MineClaw.Agent.Rag.Beliefs.BeliefOptions().Nicknames.TryGetValue(l, out var n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            return new PersonChoice(l, names);
        }).ToList();
    }

    public static IReadOnlyList<ShardFileInfo> ListFiles(string? dir = null)
    {
        dir ??= ShardsDir;
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*" + ShardFile.Extension).Select(Describe).OrderByDescending(f => f.Created ?? DateTimeOffset.MinValue).ToList();
    }

    public static ShardFileInfo Describe(string path)
    {
        long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
        try
        {
            var m = ShardFile.ReadManifest(path);
            return new ShardFileInfo(path, m.Name, m.Kind, m.Total, m.PackVersion, m.Created, bytes, m.Description, null,
                string.Join(" · ", m.Counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key}")), m.Embeddings?.Model,
                m.Kind == "pack" ? m.Pack?.Mode ?? m.Selection.PackMode : null);
        }
        catch (Exception ex) when (ex is ShardFormatException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new ShardFileInfo(path, System.IO.Path.GetFileNameWithoutExtension(path), "?", 0, null, null, bytes, null, ex.Message, "", null);
        }
    }

    // ------------------------------------------------------------------ default packs

    /// <summary>
    /// The default knowledge packs folder: <c>Explorer:DefaultPacksDir</c> (settings or env <c>MINECLAW_Explorer__DefaultPacksDir</c>), else
    /// <c>shards/defaults</c> next to the exe when it ships there, else the repo's <c>shards/defaults</c>.
    /// </summary>
    public static string DefaultPacksDir(ExplorerConfig? config)
    {
        if (config?.Raw["Explorer:DefaultPacksDir"] is { Length: > 0 } d) return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(d) ? d : System.IO.Path.Combine(ExplorerPaths.Root, d));
        var beside = System.IO.Path.Combine(AppContext.BaseDirectory, "shards", "defaults");
        return Directory.Exists(beside) ? beside : System.IO.Path.Combine(ExplorerPaths.Root, "shards", "defaults");
    }

    /// <summary>
    /// The packs of a defaults folder: index.json's entries (version packs first, then modes), each checked against its file (missing or
    /// not verifying = an error on the row); a .mcshard there without an index entry is listed from its own manifest.
    /// </summary>
    public static IReadOnlyList<DefaultPackInfo> ListDefaults(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        var index = PackIndex.Load(dir);
        var list = new List<DefaultPackInfo>();
        foreach (var e in index.Packs)
        {
            var path = System.IO.Path.Combine(dir, e.File);
            list.Add(new DefaultPackInfo(e, path, IndexedPackError(path, e)));
        }
        foreach (var path in Directory.EnumerateFiles(dir, "*" + ShardFile.Extension).Order(StringComparer.OrdinalIgnoreCase))
            if (UnindexedPack(index, path) is { } info) list.Add(info);
        return list.OrderBy(p => p.IsMode).ThenBy(p => p.Entry.Version, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static bool Unreadable(Exception ex) => ex is ShardFormatException or IOException or InvalidDataException;

    /// <summary>Why an index.json entry's file can't be used (missing, not matching the index, unreadable); null = fine.</summary>
    private static string? IndexedPackError(string path, PackIndexEntry e)
    {
        if (!File.Exists(path)) return "missing: " + e.File + " (rebuild with tools/ShardPacks)";
        try
        {
            var m = ShardFile.ReadManifest(path);
            return m.ContentHash != e.ContentHash ? "the file doesn't match index.json (rebuild with tools/ShardPacks)" : null;
        }
        catch (Exception ex) when (Unreadable(ex)) { return ex.Message; }
    }

    /// <summary>A shard in the folder that index.json doesn't list, described from its own manifest (null = listed, or unreadable).</summary>
    private static DefaultPackInfo? UnindexedPack(PackIndex index, string path)
    {
        var file = System.IO.Path.GetFileName(path);
        if (index.Packs.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase))) return null;
        try
        {
            var m = ShardFile.ReadManifest(path);
            return new DefaultPackInfo(EntryOf(file, path, m), path, m.Kind == "pack" ? null : "not a knowledge pack");
        }
        catch (Exception ex) when (Unreadable(ex)) { return null; }
    }

    private static PackIndexEntry EntryOf(string file, string path, ShardManifest m)
    {
        var pi = m.Pack;
        return new PackIndexEntry
        {
            File = file, Name = pi?.Name ?? m.Name, Kind = PackKindOf(m),
            Version = pi?.Version ?? m.PackVersion ?? "?", Mode = pi?.Mode ?? m.Selection.PackMode, Description = m.Description,
            Counts = new SortedDictionary<string, int>(m.Counts, StringComparer.Ordinal), Records = m.Total, Bytes = new FileInfo(path).Length,
            Vectors = m.Embeddings?.Count ?? 0, EmbeddingModel = m.Embeddings?.Model, Dims = m.Embeddings?.Dims ?? 0, Files = pi?.Files ?? [],
            SourceHash = pi?.SourceHash, ContentHash = m.ContentHash, Builder = pi?.Builder ?? "not in index.json",
        };
    }

    private static string PackKindOf(ShardManifest m) => m.Pack?.Kind ?? (m.Selection.PackMode != null ? ShardPackInfo.ModeKind : ShardPackInfo.VersionKind);

    /// <summary>A file name for a new shard: data/shards/{slug}-{yyyyMMdd-HHmm}.mcshard.</summary>
    public static string SuggestPath(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '.' ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "shard";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return System.IO.Path.Combine(ShardsDir, $"{slug}-{DateTime.Now:yyyyMMdd-HHmm}{ShardFile.Extension}");
    }

    /// <summary>For a semantic filter: the doc refs the bot's own hybrid search returns (vectors + RAPTOR when configured).</summary>
    private static async Task<IReadOnlyCollection<string>?> SemanticRefsAsync(MemoryBank bank, ShardSelection s, CancellationToken ct)
    {
        if (!s.Semantic || string.IsNullOrWhiteSpace(s.Query)) return null;
        var hits = await bank.Rag.SearchAsync(new RagQuery { Text = s.Query, Limit = 200 }, ct);
        return hits.Select(h => h.Ref).ToHashSet(StringComparer.Ordinal);
    }

    public static async Task<ShardPreview> PreviewAsync(MemoryBank bank, ShardExportRequest req, CancellationToken ct = default)
    {
        var refs = await SemanticRefsAsync(bank, req.Selection, ct);
        return await Task.Run(() => new ShardExporter(bank.Db, bank.Raptor?.Index).Preview(req, refs), ct);
    }

    /// <summary>Writes the shard and records an "export" line in the bank's local audit trail (who, what, the content hash).</summary>
    public static async Task<(Shard Shard, long Bytes)> ExportAsync(MemoryBank bank, ShardExportRequest req, string path, CancellationToken ct = default)
    {
        var refs = await SemanticRefsAsync(bank, req.Selection, ct);
        var result = await Task.Run(() => new ShardExporter(bank.Db, bank.Raptor?.Index).Export(req, path, refs), ct);
        var m = result.Shard.Manifest;
        try
        {
            new AdminAudit(LocalAdminTarget.AuditPathFor(bank.Path)).Write(new AuditEntry(DateTimeOffset.UtcNow, req.CreatedBy ?? "admin", "local:" + bank.Path,
                "export", "shard", m.Name, null,
                JsonSerializer.Serialize(new { file = ExplorerPaths.Display(path), kind = m.Kind, records = m.Total, content_hash = m.ContentHash, pack = m.PackVersion }, ShardFile.Json),
                m.Description, true, $"exported {m.Total} records ({MemoryBank.FormatBytes(result.Bytes)})"));
        }
        catch (Exception) { /* the audit is best effort for a read-only export */ }
        return result;
    }
}

/// <summary>A player and every name to match them by.</summary>
public sealed record PersonChoice(string Login, IReadOnlyList<string> Names)
{
    public string Label => Names.Count > 1 ? $"{Login} ({string.Join(", ", Names.Where(n => !n.Equals(Login, StringComparison.OrdinalIgnoreCase)))})" : Login;
    public override string ToString() => Label;
}
