using System.Text.Json;
using MineClaw.Agent.Admin;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Admin.Shards.Packs;
using MineClaw.Agent.Rag;

namespace MineClaw.ViewerBridge.Data;

public sealed record PackEntryDto(string File, string Name, string Kind, string Version, string? Mode, string? Description, IReadOnlyDictionary<string, int> Counts,
    int Records, long Bytes, int Vectors, int NeedsEmbedding, string? EmbeddingModel, int Dims, IReadOnlyList<string> Files, string? Source, string? SourceHash,
    string? ContentHash, string? Builder, string KindLabel, string? Error, bool Active);
public sealed record ShardFileInfoDto(string File, long Bytes, ShardManifest? Manifest, string KindLabel, string? Error);
public sealed record ShardPacksResponse(IReadOnlyList<PackEntryDto> Packs, IReadOnlyList<ShardFileInfoDto> Extra, string? ActivePack, IReadOnlyList<string> ActiveModes);
public sealed record ShardImportPreviewResponse(ShardManifest Manifest, ShardDiff Diff);

/// <summary>Body of <c>/v1/shards/export[/preview]</c> (contracts ShardExportRequest, snake_case).</summary>
public sealed record ShardExportBody(string Name, ShardSelection Selection, string? Description = null, bool IncludeEmbeddings = true, bool IncludeSummaries = true);

/// <summary>The <c>options</c> part of a shard upload (contracts ShardImportOptions); <c>who</c> is never accepted from it.</summary>
public sealed record ShardImportOptionsBody(string Mode = "merge", string Conflict = "keep-newer", bool IncludeRules = false, bool IncludeWorld = false, decimal MaxUsd = 0.25m);

/// <summary>
/// Shards (explorer-spec §8) on the read-only db: the default packs folder (index.json verified against each file), export
/// preview and download through the bot's own <see cref="ShardExporter"/> (secrets redacted), and the import diff through
/// <see cref="ShardImporter.Preview"/> on a read-only admin context (nothing is written; apply goes to the bot).
/// </summary>
public static class ShardViews
{
    public static ShardPacksResponse Packs(MemoryBank bank, string dir)
    {
        KnowledgePackInfo? active = null;
        try { active = KnowledgePack.Active(bank.Db); } catch (Exception) { }
        var packs = new List<PackEntryDto>();
        var extra = new List<ShardFileInfoDto>();
        if (Directory.Exists(dir))
        {
            PackIndex index;
            try { index = PackIndex.Load(dir); }
            catch (Exception ex) when (ex is IOException or JsonException) { index = new PackIndex(); }
            foreach (var e in index.Packs.OrderBy(p => p.IsMode).ThenBy(p => p.Version, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                var path = Path.Combine(dir, e.File);
                string? error = null;
                if (!File.Exists(path)) error = "missing: " + e.File + " (rebuild with tools/ShardPacks)";
                else
                    try
                    {
                        if (ShardFile.ReadManifest(path).ContentHash != e.ContentHash) error = "the file doesn't match index.json (rebuild with tools/ShardPacks)";
                    }
                    catch (Exception ex) when (ex is ShardFormatException or IOException or InvalidDataException) { error = ex.Message; }
                bool isActive = active != null && (e.IsMode
                    ? active.Modes?.Any(m => m.Mode.Equals(e.Mode, StringComparison.OrdinalIgnoreCase)) == true
                    : active.Version.Equals(e.Version, StringComparison.OrdinalIgnoreCase));
                packs.Add(new PackEntryDto(e.File, e.Name, e.IsMode ? "mode" : "version", e.Version, e.Mode, e.Description, e.Counts, e.Records, e.Bytes, e.Vectors,
                    e.NeedsEmbedding, e.EmbeddingModel, e.Dims, e.Files, e.Source, e.SourceHash, e.ContentHash, e.Builder,
                    e.IsMode ? $"mode pack {e.Mode} ({e.Version})" : $"knowledge pack {e.Version}", error, isActive));
            }
            foreach (var path in Directory.EnumerateFiles(dir, "*" + ShardFile.Extension).Order(StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.GetFileName(path);
                if (index.Packs.Any(p => p.File.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
                long bytes = new FileInfo(path).Length;
                try
                {
                    var m = ShardFile.ReadManifest(path);
                    extra.Add(new ShardFileInfoDto(file, bytes, m, KindLabel(m), null));
                }
                catch (Exception ex) when (ex is ShardFormatException or IOException or InvalidDataException) { extra.Add(new ShardFileInfoDto(file, bytes, null, "?", ex.Message)); }
            }
        }
        return new ShardPacksResponse(packs, extra, active?.Label, active?.Modes?.Select(m => m.Mode).ToList() ?? []);
    }

    public static string KindLabel(ShardManifest m) => m.Kind switch
    {
        "person" => "about a person",
        "pack" when (m.Pack?.Mode ?? m.Selection.PackMode) is { } mode => $"mode pack {mode} ({m.PackVersion})",
        "pack" => "knowledge pack " + m.PackVersion,
        "subtree" => "RAPTOR subtree",
        "filter" => "filter",
        _ => m.Kind,
    };

    public static ShardExportRequest Request(ShardExportBody b, string who, string? bot) =>
        new(b.Name.Trim(), b.Selection, b.Description, b.IncludeEmbeddings, b.IncludeSummaries, who, bot);

    /// <summary>For a semantic filter: the doc refs the bot's own hybrid search returns.</summary>
    private static async Task<IReadOnlyCollection<string>?> SemanticRefsAsync(MemoryBank bank, ShardSelection s, CancellationToken ct)
    {
        if (!s.Semantic || string.IsNullOrWhiteSpace(s.Query)) return null;
        var hits = await bank.Rag.SearchAsync(new RagQuery { Text = s.Query, Limit = 200 }, ct);
        return hits.Select(h => h.Ref).ToHashSet(StringComparer.Ordinal);
    }

    public static async Task<ShardPreview> PreviewAsync(MemoryBank bank, ShardExportRequest req, CancellationToken ct)
    {
        var refs = await SemanticRefsAsync(bank, req.Selection, ct);
        return await Task.Run(() => new ShardExporter(bank.Db, bank.Raptor?.Index).Preview(req, refs), ct);
    }

    public static async Task<(byte[] Bytes, ShardManifest Manifest)> ExportAsync(MemoryBank bank, ShardExportRequest req, CancellationToken ct)
    {
        var refs = await SemanticRefsAsync(bank, req.Selection, ct);
        return await Task.Run(() =>
        {
            var shard = new ShardExporter(bank.Db, bank.Raptor?.Index).Build(req, refs);
            return (ShardFile.ToBytes(shard), shard.Manifest);
        }, ct);
    }

    public static string FileName(string name, DateTimeOffset at)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        if (slug.Length == 0) slug = "shard";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return $"{slug}-{at:yyyyMMdd-HHmm}{ShardFile.Extension}";
    }

    public static ShardImportOptions Options(ShardImportOptionsBody o, string who) => new()
    {
        Mode = ShardImportOptions.ParseMode(o.Mode),
        Conflict = ParseConflict(o.Conflict),
        IncludeRules = o.IncludeRules,
        IncludeWorld = o.IncludeWorld,
        Who = who,
        RebuildMaxUsd = Math.Clamp(o.MaxUsd, 0m, 5m),
    };

    private static ShardConflictPolicy ParseConflict(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "prefer-shard" => ShardConflictPolicy.PreferShard,
        "keep-mine" => ShardConflictPolicy.KeepMine,
        _ => ShardConflictPolicy.KeepNewer,
    };

    /// <summary>The import diff against the db, on a read-only admin context opened for this call (nothing can be written).</summary>
    public static ShardImportPreviewResponse ImportPreview(BridgeConfig config, Shard shard, ShardImportOptions options, string scratchDir)
    {
        using var ctx = MemoryAdminContext.OpenLocal(config.DbPath, config.Agent, config.CreateEmbeddings(), llm: null, readOnly: true);
        Directory.CreateDirectory(scratchDir);
        var audit = new AdminAudit(Path.Combine(scratchDir, "preview-audit.jsonl"));
        var diff = new ShardImporter(ctx, audit, "bridge-preview").Preview(shard, options);
        return new ShardImportPreviewResponse(shard.Manifest, diff);
    }
}
