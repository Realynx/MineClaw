using System.Text;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;

namespace MineClaw.Agent.Admin.Shards.Packs;

public sealed record PackBuildOptions
{
    /// <summary>The version's game data (<see cref="MineClawDataSource"/> for b1.8.1, <see cref="MinecraftDataSource"/> later).</summary>
    public required IGameDataSource Source { get; init; }
    /// <summary>docs/knowledge: files whose pack marker names this version (and its modes) go into the packs.</summary>
    public string? KnowledgeDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    /// <summary>The pristine seed db (data/seed/mineclaw-seed.db) to take vectors from by material hash; null = no vectors.</summary>
    public string? SeedPath { get; init; }
    /// <summary>Which vectors (model + dims); null = the model most of the seed's vectors use.</summary>
    public string? EmbeddingModel { get; init; }
    public int? Dims { get; init; }
    public bool IncludeVectors { get; init; } = true;
    /// <summary>A pack over this size is stored without vectors (the target embeds on import).</summary>
    public long MaxPackBytes { get; init; } = 20L * 1024 * 1024;
    /// <summary>Chunking (the live indexer's defaults, so a bot's own index of the same doc gives the same chunks).</summary>
    public RagOptions Rag { get; init; } = new();
    /// <summary>Only build these packs (names); null = the version pack and every mode pack of the version.</summary>
    public IReadOnlyCollection<string>? Only { get; init; }
    /// <summary>Mode packs generated from code rather than docs/knowledge (e.g. <c>sdk-js</c>: the scripting SDK reference).</summary>
    public IReadOnlyList<GeneratedPack> Generated { get; init; } = [];
    /// <summary>False = only the <see cref="Generated"/> packs (no version pack, no doc mode packs).</summary>
    public bool IncludeVersionPacks { get; init; } = true;
}

/// <summary>
/// A mode pack whose knowledge records are produced by code (the scripting SDK reference, generated from the SDK's own doc comments so
/// it never drifts). <see cref="Docs"/> are knowledge chunks with refs <c>kb:{FileName}:{n}</c>; <see cref="Version"/> is
/// <see cref="KnowledgePack.AnyVersion"/> when the pack holds in every world.
/// </summary>
public sealed record GeneratedPack(string Name, string Mode, string FileName, string Description, string Source, IReadOnlyList<RagDoc> Docs)
{
    public string Version { get; init; } = KnowledgePack.AnyVersion;
    /// <summary>Optional cached-prompt lines (null = retrieval only).</summary>
    public string? Core { get; init; }
}

public sealed record PackBuildResult(IReadOnlyList<PackIndexEntry> Packs, IReadOnlyList<string> Notes, string IndexPath);

/// <summary>
/// Builds the default knowledge packs of a Minecraft version from PRISTINE sources (never a live or mutated db): the version pack
/// (<c>{version}-gamedata</c>: the game data of an <see cref="IGameDataSource"/> + the docs marked <c>pack version=…</c>) and one mode
/// pack per <c>pack mode=…</c> doc set of that version (e.g. <c>skyblock</c>). Each pack goes through a scratch in-memory db and the
/// bot's own <see cref="ShardExporter"/> (so ids, hashes and redaction match what any target computes), with vectors copied from the
/// seed by material hash when it has them for the chosen model + dims (the rest marked as needing embedding). Equal inputs give
/// byte-identical <c>.mcshard</c> files and index.json (sorted records, the fixed seed timestamp, no wall-clock anywhere).
/// </summary>
public static class DefaultPackBuilder
{
    /// <summary>Bumped when the builder's output for the same inputs changes.</summary>
    public const string BuilderVersion = "ShardPacks/1";

    private sealed record DocFile(string Name, string Text, KnowledgeDocPack Pack);

    private sealed record PackPlan(string Name, string Kind, string? Mode, List<DocFile> Files, bool GameData)
    {
        public GeneratedPack? Generated { get; init; }
    }

    public static PackBuildResult Build(PackBuildOptions o)
    {
        var version = o.Source.Version;
        Validate(o, version);
        var notes = new List<string>();
        var outDir = Path.GetFullPath(o.OutputDirectory);
        Directory.CreateDirectory(outDir);

        // 1) Knowledge docs of this version, by pack.
        var plans = Plans(o, version, VersionKnowledgeFiles(o, version, notes));
        // 2) Vectors from the pristine seed (material hash → vector, one model + dims).
        var (vectors, model, dims) = SeedVectors(o, notes);

        var gameDocs = plans.Any(p => p.GameData) ? o.Source.Docs() : [];
        var entries = new List<PackIndexEntry>();
        foreach (var plan in plans)
            entries.Add(WritePack(o, plan, version, gameDocs, vectors, model, dims, notes, outDir));

        var index = PackIndex.Load(outDir).Upsert(entries, outDir) with
        {
            Note = "Default knowledge packs, built by tools/ShardPacks from pristine sources (never a live db). Rebuild: see README.md.",
        };
        index.Save(outDir);
        return new PackBuildResult(entries, notes, Path.Combine(outDir, PackIndex.FileName));
    }

    private static void Validate(PackBuildOptions o, string version)
    {
        if (!KnowledgePack.ValidVersion(version)) throw new ArgumentException($"'{version}' is not a version tag");
        foreach (var g in o.Generated)
            if (g.Docs.Any(d => d.Kind != KnowledgeIndexer.Kind || !d.Ref.StartsWith($"kb:{g.FileName}:", StringComparison.Ordinal)))
                throw new ArgumentException($"generated pack {g.Name}: every doc must be a knowledge chunk kb:{g.FileName}:n");
    }

    /// <summary>The knowledge .md files of this version with a pack marker (a file without one is noted and left out).</summary>
    private static List<DocFile> VersionKnowledgeFiles(PackBuildOptions o, string version, List<string> notes)
    {
        var files = new List<DocFile>();
        if (o.KnowledgeDirectory is not { } kd || !Directory.Exists(kd)) return files;
        foreach (var f in Directory.GetFiles(kd, "*.md").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            var text = File.ReadAllText(f).Replace("\r\n", "\n");
            var name = Path.GetFileName(f);
            var pack = KnowledgeDocPack.Parse(text);
            if (pack == null) { notes.Add($"{name}: no pack marker, left out (a bot reads it from disk in every world)"); continue; }
            if (!(pack.Version ?? version).Equals(version, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(new DocFile(name, text, pack));
        }
        return files;
    }

    /// <summary>The version's game-data pack, one pack per mode, the generated packs; filtered by <see cref="PackBuildOptions.Only"/>.</summary>
    private static List<PackPlan> Plans(PackBuildOptions o, string version, List<DocFile> files)
    {
        var plans = new List<PackPlan> { new($"{version}-gamedata", ShardPackInfo.VersionKind, null, files.Where(f => !f.Pack.IsMode).ToList(), true) };
        foreach (var g in files.Where(f => f.Pack.IsMode).GroupBy(f => f.Pack.PackName(version), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            plans.Add(new PackPlan(g.Key, ShardPackInfo.ModeKind, g.First().Pack.Mode, g.ToList(), false));
        if (!o.IncludeVersionPacks) plans.Clear();
        foreach (var g in o.Generated)
            plans.Add(new PackPlan(g.Name, ShardPackInfo.ModeKind, g.Mode, [], false) { Generated = g });
        if (o.Only is { Count: > 0 } only) plans = plans.Where(p => only.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return plans;
    }

    /// <summary>Vectors by material hash from the pristine seed (one model + dims; a stored doc vector wins over a cache entry).</summary>
    private static (Dictionary<string, byte[]> Vectors, string? Model, int Dims) SeedVectors(PackBuildOptions o, List<string> notes)
    {
        var vectors = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!o.IncludeVectors) return (vectors, null, 0);
        if (o.SeedPath is not { } sp || !File.Exists(sp))
        {
            notes.Add("no seed db given: packs carry no vectors (the target embeds them on import)");
            return (vectors, null, 0);
        }
        using var seed = AgentDb.OpenReadOnly(sp);
        var (model, dims) = o.EmbeddingModel != null && o.Dims is { } d0 ? (o.EmbeddingModel, d0)
            : seed.Query("SELECT model, dims, count(*) FROM vectors GROUP BY model, dims ORDER BY 3 DESC, 1, 2", r => (r.GetString(0), (int)r.GetInt64(1))).FirstOrDefault();
        if (model != null) LoadSeedVectors(seed, model, dims, vectors);
        if (vectors.Count == 0) notes.Add($"the seed has no {o.EmbeddingModel ?? "stored"} vectors: packs are marked as needing embedding");
        return (vectors, model, dims);
    }

    private static void LoadSeedVectors(AgentDb seed, string model, int dims, Dictionary<string, byte[]> vectors)
    {
        foreach (var (h, v) in seed.Query("SELECT hash, vec FROM embed_cache WHERE model = $m AND dims = $d ORDER BY hash",
                     r => (r.GetString(0), (byte[])r.GetValue(1)), ("$m", model), ("$d", dims)))
            vectors.TryAdd(h, v);
        foreach (var (h, v) in seed.Query("SELECT hash, vec FROM vectors WHERE model = $m AND dims = $d ORDER BY doc_id",
                     r => (r.GetString(0), (byte[])r.GetValue(1)), ("$m", model), ("$d", dims)))
            vectors[h] = v; // a stored doc vector wins over a cache entry
    }

    /// <summary>Builds one pack and writes it if changed; over the size cap with vectors, it is stored without them.</summary>
    private static PackIndexEntry WritePack(PackBuildOptions o, PackPlan plan, string version, IReadOnlyList<RagDoc> gameDocs,
        Dictionary<string, byte[]> vectors, string? model, int dims, List<string> notes, string outDir)
    {
        var (shard, entry) = BuildOne(o, plan, plan.Generated?.Version ?? version, gameDocs, vectors, model, dims, notes);
        var path = Path.Combine(outDir, entry.File);
        var bytes = ShardFile.ToBytes(shard);
        if (bytes.LongLength > o.MaxPackBytes && shard.Embeddings.Count > 0)
        {
            notes.Add($"{entry.File}: {bytes.LongLength / 1024.0 / 1024.0:F1} MB with vectors is over {o.MaxPackBytes / 1024 / 1024} MB: stored without vectors");
            (shard, entry) = BuildOne(o, plan, plan.Generated?.Version ?? version, gameDocs, new Dictionary<string, byte[]>(), null, 0, notes);
            bytes = ShardFile.ToBytes(shard);
        }
        WriteIfChanged(path, bytes);
        ShardFile.Read(path); // what's on disk verifies
        return entry with { Bytes = bytes.LongLength };
    }

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        var tmp = path + ".writing";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static (Shard Shard, PackIndexEntry Entry) BuildOne(PackBuildOptions o, PackPlan plan, string version, IReadOnlyList<RagDoc> gameDocs,
        IReadOnlyDictionary<string, byte[]> vectors, string? model, int dims, List<string> notes)
    {
        using var db = AgentDb.Open(null);
        using var index = new RagIndex(db, o.Rag, NullEmbeddingProvider.Instance, usage: null, backgroundEmbedding: false);
        var docs = new List<RagDoc>();
        if (plan.GameData) docs.AddRange(gameDocs);
        var cores = new List<string>();
        if (plan.Generated is { } gen)
        {
            docs.AddRange(gen.Docs.Select(d => d with { At = SeedCorpus.At }));
            if (gen.Core is { Length: > 0 } gc) cores.Add(gc);
        }
        foreach (var f in plan.Files)
        {
            var (fileCore, chunks) = KnowledgeIndexer.Docs(f.Name, f.Text, o.Rag, SeedCorpus.At);
            if (fileCore.Length > 0) cores.Add(fileCore);
            docs.AddRange(chunks);
        }
        index.UpsertMany(docs);

        // Seed vectors by material hash, straight into the scratch db (the exporter reads current vectors from there).
        if (model != null)
            db.Tx(() =>
            {
                foreach (var (id, hash) in db.Query("SELECT id, hash FROM docs WHERE embed = 1 AND level = 0 ORDER BY id", r => (r.GetInt64(0), r.GetString(1))))
                    if (vectors.TryGetValue(hash, out var v))
                        db.Exec("INSERT OR REPLACE INTO vectors(doc_id, model, dims, hash, vec) VALUES ($i, $m, $d, $h, $v)",
                            ("$i", id), ("$m", model), ("$d", dims), ("$h", hash), ("$v", v));
            });

        var fileNames = plan.Generated is { } g0 ? [g0.FileName] : plan.Files.Select(f => f.Name).ToList();
        var selection = new ShardSelection
        {
            Mode = "pack", PackVersion = version, PackMode = plan.Mode,
            IncludeKnowledgeDocs = fileNames.Count > 0, KnowledgeFiles = fileNames.Count > 0 ? fileNames : null,
        };
        bool mobs = plan.GameData && gameDocs.Any(d => d.Ref.StartsWith("gd:mob:", StringComparison.Ordinal));
        var desc = plan.Generated is { } g1 ? g1.Description
            : plan.GameData
            ? $"Minecraft {version} version pack: recipes, items, blocks, smelting{(mobs ? ", mob" : "")} and dig facts" +
              (fileNames.Count > 0 ? $" + the general mechanics doc{(fileNames.Count > 1 ? "s" : "")} {string.Join(", ", fileNames)}" : "") +
              ". Swap it in for any world on this version."
            : $"{plan.Mode} mode pack for {version} ({string.Join(", ", fileNames)}): add it when the world is a {plan.Mode}, remove it for a plain world.";
        var built = new ShardExporter(db).Build(new ShardExportRequest(plan.Name, selection, desc, IncludeEmbeddings: model != null,
            IncludeSummaries: false, CreatedBy: BuilderVersion));
        int embeddable = built.Records.Count(r => r.Get("embed") != "0");
        int withVector = built.Records.Count(r => r.Get("embed") != "0" && r.DocHash != null && built.Embeddings.ContainsKey(r.DocHash));
        var core = ShardRedactor.Redact(string.Join("\n\n", cores), out int coreSecrets);
        if (coreSecrets > 0) notes.Add($"{plan.Name}: {coreSecrets} token-shaped string(s) redacted from the core block");
        var sourceHash = plan.Generated is { } g2
            ? SourceHash("generated " + g2.Source, g2.Docs, [])
            : SourceHash(plan.GameData ? o.Source.Description : null, plan.GameData ? gameDocs : [], plan.Files);
        var sourceLabel = plan.Generated?.Source ?? (plan.GameData ? o.Source.Description : "docs/knowledge");
        var info = new ShardPackInfo
        {
            Kind = plan.Kind, Name = plan.Name, Version = version, Mode = plan.Mode, Core = core.Length > 0 ? core : null, Files = fileNames,
            Source = sourceLabel, SourceHash = sourceHash, Builder = BuilderVersion,
            NeedsEmbedding = embeddable - withVector,
        };
        var shard = new Shard
        {
            Manifest = built.Manifest with
            {
                Created = SeedCorpus.At,
                CreatedBy = BuilderVersion,
                Source = new ShardSourceInfo { Db = null, SchemaVersion = db.SchemaVersion, Bot = null },
                Pack = info,
                SecretsRedacted = built.Manifest.SecretsRedacted + coreSecrets,
            },
            Records = built.Records,
            Embeddings = built.Embeddings,
            Summaries = [],
        };
        var file = plan.Name + ShardFile.Extension;
        // Counts/hash as the file stores them (WriteTo recomputes them from the content).
        var check = ShardFile.Read(ShardFile.ToBytes(shard)).Manifest;
        var entry = new PackIndexEntry
        {
            File = file, Name = plan.Name, Kind = plan.Kind, Version = version, Mode = plan.Mode, Description = desc,
            Counts = new SortedDictionary<string, int>(check.Counts, StringComparer.Ordinal), Records = check.Total,
            Vectors = check.Embeddings?.Count ?? 0, NeedsEmbedding = info.NeedsEmbedding, EmbeddingModel = check.Embeddings?.Model,
            Dims = check.Embeddings?.Dims ?? 0, Files = fileNames, Source = info.Source, SourceHash = sourceHash, ContentHash = check.ContentHash,
            Builder = BuilderVersion,
        };
        if (info.NeedsEmbedding > 0) notes.Add($"{plan.Name}: {info.NeedsEmbedding} of {embeddable} doc(s) have no seed vector (embedded on import)");
        return (shard, entry);
    }

    /// <summary>sha256 over every input: the game-data source + docs (ref, title, body) and each knowledge file (name + text, \n).</summary>
    private static string SourceHash(string? source, IReadOnlyList<RagDoc> gameDocs, IEnumerable<DocFile> files)
    {
        var sb = new StringBuilder(BuilderVersion).Append('\n');
        if (source != null) sb.Append("source ").Append(source).Append('\n');
        foreach (var d in gameDocs) sb.Append(d.Ref).Append('\t').Append(d.Title).Append('\t').Append(d.Body).Append('\n');
        foreach (var f in files) sb.Append("file ").Append(f.Name).Append('\n').Append(f.Text).Append('\n');
        return ShardFile.Sha256(sb.ToString());
    }
}
