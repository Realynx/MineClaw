using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>One <c>.mcshard</c> on disk as the Shards page lists it (the manifest only; a broken file shows why).</summary>
public sealed record ShardFileInfo(string Path, string Name, string Kind, int Records, string? PackVersion, DateTimeOffset? Created, long Bytes,
    string? Description, string? Error, string Counts, string? EmbeddingModel)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeText => MemoryBank.FormatBytes(Bytes);
    public string CreatedText => Created is { } c ? c.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
    public string KindLabel => Kind switch
    {
        "person" => "about a person",
        "pack" => "knowledge pack " + PackVersion,
        "subtree" => "RAPTOR subtree",
        "filter" => "filter",
        _ => Kind,
    };
    public bool Ok => Error == null;
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
    public static string? KindOf(string browserKey) => browserKey switch
    {
        "facts" => ShardKinds.Fact,
        "claims" or "beliefs" => ShardKinds.Claim,
        "personality" => ShardKinds.Trait,
        "structures" => ShardKinds.Structure,
        "places" => ShardKinds.Place,
        "lessons" => ShardKinds.Lesson,
        "episodes" => ShardKinds.Episode,
        "recipes" => ShardKinds.GameData,
        "knowledge" => ShardKinds.Knowledge,
        _ => null,
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
                string.Join(" · ", m.Counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key}")), m.Embeddings?.Model);
        }
        catch (Exception ex) when (ex is ShardFormatException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new ShardFileInfo(path, System.IO.Path.GetFileNameWithoutExtension(path), "?", 0, null, null, bytes, null, ex.Message, "", null);
        }
    }

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
