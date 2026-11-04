using MineClaw.Agent.Rag;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;

namespace MineClaw.Agent.Admin.Shards;

/// <param name="IncludeEmbeddings">Carry the docs' vectors (reused on import when the model and dims match).</param>
/// <param name="IncludeSummaries">Carry the RAPTOR summaries wholly inside the selection as a cache (never as truth).</param>
public sealed record ShardExportRequest(string Name, ShardSelection Selection, string? Description = null, bool IncludeEmbeddings = true,
    bool IncludeSummaries = true, string? CreatedBy = null, string? Bot = null);

/// <summary>What an export would write (counts and the exact file size), before anything is written.</summary>
public sealed record ShardPreview(IReadOnlyDictionary<string, int> Counts, int Total, int Vectors, string? EmbeddingModel, int Dims, int Summaries,
    long Bytes, int SecretsRedacted, IReadOnlyList<string> Samples, IReadOnlyList<string> Notes, string? PackVersion);

/// <summary>
/// Cuts a shard out of a db: the selection's records (<see cref="ShardCollector"/>), secrets dropped or redacted
/// (<see cref="ShardRedactor"/>), their vectors from the <c>vectors</c> table keyed by doc material hash, and optionally the
/// RAPTOR summaries whose every leaf is in the shard (a cache with their cluster-text hash). Read-only: works on a snapshot opened
/// with <see cref="AgentDb.OpenReadOnly"/>.
/// </summary>
public sealed class ShardExporter
{
    private readonly AgentDb _db;
    private readonly RaptorIndex? _tree;

    /// <param name="tree">The db's loaded RAPTOR tree (null = no summaries cache).</param>
    public ShardExporter(AgentDb db, RaptorIndex? tree = null)
    {
        _db = db;
        _tree = tree;
    }

    public Shard Build(ShardExportRequest req, IReadOnlyCollection<string>? semanticRefs = null)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) throw new ArgumentException("a shard needs a name");
        var (sel, pack) = PackScoped(req.Selection);
        var picked = new ShardCollector(_db).Select(sel, semanticRefs);
        var (records, secrets) = WithoutSecrets(picked);

        var embeddings = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var embInfo = req.IncludeEmbeddings ? AddVectors(records, embeddings) : null;
        var summaries = req.IncludeSummaries && _tree != null ? CachedSummaries(records, embInfo, embeddings) : new List<ShardSummary>();

        var manifest = new ShardManifest
        {
            Name = req.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : ShardRedactor.Redact(req.Description.Trim(), out _),
            Created = DateTimeOffset.UtcNow,
            CreatedBy = req.CreatedBy,
            Source = new ShardSourceInfo { Db = _db.Path != null ? Path.GetFileName(_db.Path) : "memory", SchemaVersion = _db.SchemaVersion, Bot = req.Bot },
            Kind = sel.Mode,
            Selection = sel with { LeafRefs = null }, // ids are in the records; refs are local to the exporting db
            PackVersion = pack,
            Embeddings = embInfo,
            SecretsRedacted = secrets,
        };
        return new Shard { Manifest = manifest, Records = records, Embeddings = embeddings, Summaries = summaries };
    }

    /// <summary>A pack selection pinned to its version (the db's active one by default), without rules or world memory.</summary>
    private (ShardSelection Selection, string? Pack) PackScoped(ShardSelection sel)
    {
        if (!sel.IsPack) return (sel, null);
        var pack = (sel.PackVersion ?? KnowledgePack.ActiveVersion(_db)).Trim();
        if (!KnowledgePack.ValidVersion(pack)) throw new ArgumentException($"'{pack}' is not a version tag (e.g. b1.8.1)");
        return (sel with { PackVersion = pack, IncludeRules = false, IncludeWorld = false }, pack);
    }

    /// <summary>Drops records about secrets and redacts token-shaped strings in the rest; counts both.</summary>
    private static (List<ShardRecord> Records, int Secrets) WithoutSecrets(List<ShardRecord> picked)
    {
        int secrets = 0;
        var records = new List<ShardRecord>(picked.Count);
        foreach (var r in picked)
        {
            if (r.Fields.Values.Concat(r.Info?.Values ?? Enumerable.Empty<string?>()).Any(ShardRedactor.IsSecret)) { secrets++; continue; }
            var (fields, n) = RedactFields(r);
            if (n > 0)
            {
                secrets++;
                records.Add(r with { Fields = fields, Hash = ShardFile.RecordHash(r.Kind, fields), DocHash = null });
            }
            else records.Add(r);
        }
        return (records, secrets);
    }

    private static (SortedDictionary<string, string?> Fields, int Redacted) RedactFields(ShardRecord r)
    {
        int n = 0;
        var fields = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in r.Fields)
        {
            if (v == null || k is "patterns" or "data" or "ref") { fields[k] = v; continue; }
            fields[k] = ShardRedactor.Redact(v, out var c);
            n += c;
        }
        return (fields, n);
    }

    /// <summary>The records' current vectors into <paramref name="embeddings"/>; their model, or null when none is stored.</summary>
    private ShardEmbeddingInfo? AddVectors(IReadOnlyList<ShardRecord> records, Dictionary<string, float[]> embeddings)
    {
        var (model, dims, vecs) = Vectors(records);
        if (model == null) return null;
        foreach (var (h, v) in vecs) embeddings[h] = v;
        return new ShardEmbeddingInfo { Model = model, Dims = dims };
    }

    /// <summary>The tree summaries wholly inside the records (a cache), with their vectors when the dims match.</summary>
    private List<ShardSummary> CachedSummaries(IReadOnlyList<ShardRecord> records, ShardEmbeddingInfo? embInfo, Dictionary<string, float[]> embeddings)
    {
        var summaries = new List<ShardSummary>();
        var refs = records.Where(r => r.Ref != null).Select(r => r.Ref!).ToHashSet(StringComparer.Ordinal);
        foreach (var (s, emb) in Summaries(refs))
        {
            summaries.Add(s);
            if (embInfo != null && emb is { } e && e.Length == embInfo.Dims) embeddings.TryAdd(VectorMath.Hash(s.Text), e);
        }
        return summaries;
    }

    public ShardPreview Preview(ShardExportRequest req, IReadOnlyCollection<string>? semanticRefs = null)
    {
        var shard = Build(req, semanticRefs);
        long bytes = ShardFile.ToBytes(shard).LongLength;
        var notes = new List<string>();
        AddContentNotes(notes, req, shard);
        AddSelectionNotes(notes, req.Selection);
        return new ShardPreview(
            shard.Records.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()), shard.Records.Count,
            shard.Embeddings.Count, shard.Manifest.Embeddings?.Model, shard.Manifest.Embeddings?.Dims ?? 0, shard.Summaries.Count, bytes,
            shard.Manifest.SecretsRedacted, shard.Records.Take(8).Select(r => $"{r.Kind}: {Trim(r.Label, 110)}").ToList(), notes, shard.Manifest.PackVersion);
    }

    private static void AddContentNotes(List<string> notes, ShardExportRequest req, Shard shard)
    {
        if (shard.Records.Count == 0) notes.Add("nothing matches this selection");
        if (req.IncludeEmbeddings && shard.Manifest.Embeddings == null && shard.Records.Count > 0) notes.Add("no stored vectors: the target re-embeds");
        if (shard.Manifest.SecretsRedacted > 0) notes.Add($"{shard.Manifest.SecretsRedacted} record(s) dropped or redacted as secrets");
    }

    private static void AddSelectionNotes(List<string> notes, ShardSelection sel)
    {
        if (!sel.IncludeRules && sel.Mode is "person" or "filter" or "subtree") notes.Add("standing rules left out (tick \"include rules\")");
        if (!sel.IncludeWorld && sel.Mode is not "pack") notes.Add("places/structures left out (tick \"include world memory\")");
    }

    /// <summary>Builds and writes the shard. Returns it with the bytes written.</summary>
    public (Shard Shard, long Bytes) Export(ShardExportRequest req, string path, IReadOnlyCollection<string>? semanticRefs = null)
    {
        var shard = Build(req, semanticRefs);
        long bytes = ShardFile.Write(path, shard);
        return (ShardFile.Read(path), bytes); // re-read: what's on disk verifies
    }

    private static string Trim(string s, int n)
    {
        var t = s.ReplaceLineEndings(" ");
        return t.Length > n ? t[..n] + "…" : t;
    }

    /// <summary>Vectors of the records' docs (current: the vector's hash is the doc's), in the model most of them use.</summary>
    private (string? Model, int Dims, Dictionary<string, float[]> Vecs) Vectors(IReadOnlyList<ShardRecord> records)
    {
        var refs = records.Where(r => r.Ref != null && r.DocHash != null).Select(r => r.Ref!).Distinct(StringComparer.Ordinal).ToList();
        var rows = new List<(string Hash, string Model, int Dims, byte[] Vec)>();
        for (int i = 0; i < refs.Count; i += 400)
        {
            var chunk = refs.Skip(i).Take(400).ToList();
            var args = chunk.Select((r, k) => ("$r" + k, (object?)r)).ToArray();
            try
            {
                rows.AddRange(_db.Query($"""
                    SELECT d.hash, v.model, v.dims, v.vec FROM docs d JOIN vectors v ON v.doc_id = d.id AND v.hash = d.hash
                    WHERE d.ref IN ({string.Join(",", args.Select(a => a.Item1))})
                    """, r => (r.GetString(0), r.GetString(1), (int)r.GetInt64(2), (byte[])r.GetValue(3)), args));
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { return (null, 0, new()); }
        }
        if (rows.Count == 0) return (null, 0, new());
        var best = rows.GroupBy(r => (r.Model, r.Dims)).OrderByDescending(g => g.Count()).First().Key;
        var vecs = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var r in rows.Where(r => r.Model == best.Model && r.Dims == best.Dims)) vecs.TryAdd(r.Hash, VectorMath.FromBlob(r.Vec));
        return (best.Model, best.Dims, vecs);
    }

    /// <summary>Clean summaries whose every leaf is in <paramref name="refs"/> (their exact cluster text hashed, as SummaryCache does).</summary>
    private List<(ShardSummary Summary, float[]? Embedding)> Summaries(HashSet<string> refs) => _tree!.Read(t =>
    {
        var inside = new Dictionary<int, bool>();
        bool Inside(RaptorNode n)
        {
            if (inside.TryGetValue(n.Index, out var known)) return known;
            inside[n.Index] = false; // cycle guard
            bool v = n.IsLeaf ? n.Key != null && refs.Contains(n.Key) : n.Children.Count > 0 && n.Children.All(c => t.TryGet(c, out var cn) && Inside(cn));
            inside[n.Index] = v;
            return v;
        }
        var list = new List<(ShardSummary, float[]?)>();
        foreach (var n in t.AllNodes.Values.OrderBy(n => n.Index))
        {
            if (n.IsLeaf || n.Dirty || n.SummaryPending || n.Children.Count == 0 || !Inside(n)) continue;
            var context = RaptorTreeBuilder.GetText(n.Children.Select(c => t[c]));
            list.Add((new ShardSummary(SummaryCache.Hash(context), n.Layer, n.Text), n.Embedding));
        }
        return list;
    });
}
