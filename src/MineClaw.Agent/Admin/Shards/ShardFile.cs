using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Admin.Shards;

/// <summary>
/// The portable <c>.mcshard</c> file: a zip holding
/// <list type="bullet">
/// <item><c>manifest.json</c>: name, description, created, source db/schema, embedding model/dims, shard kind + selection, counts and
/// the content hash;</item>
/// <item><c>records/{kind}.jsonl</c>: one canonical JSON record per line (stable id, fields, dimension, provenance), sorted by id;</item>
/// <item><c>embeddings.bin</c> (optional): doc material hash → vector (little-endian float32), reused on import when model + dims
/// match;</item>
/// <item><c>summaries.jsonl</c> (optional): RAPTOR summaries keyed by their exact cluster-text hash, a cache only: the tree is
/// rebuilt from the leaves on import, and a rebuild that reproduces an identical cluster reuses the summary.</item>
/// </list>
/// Reading verifies the format version, the schema version, every record hash, the content hash and the sidecar hashes, and
/// refuses with a clear <see cref="ShardFormatException"/> on any mismatch.
/// </summary>
public static class ShardFile
{
    public const int FormatVersion = 1;
    public const string Extension = ".mcshard";
    public const string ManifestEntry = "manifest.json";
    public const string EmbeddingsEntry = "embeddings.bin";
    public const string SummariesEntry = "summaries.jsonl";
    private static readonly byte[] Magic = "MCEMB1\0\0"u8.ToArray();
    /// <summary>Hard limits when reading (a hostile or broken file can't blow up memory).</summary>
    public const long MaxEntryBytes = 512L * 1024 * 1024;
    public const int MaxRecords = 2_000_000;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };

    // ======================= hashing =======================

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Sha256(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    /// <summary>Content hash of one record: kind + every field (ordinal order; null distinct from "").</summary>
    public static string RecordHash(string kind, IReadOnlyDictionary<string, string?> fields)
    {
        var sb = new StringBuilder(kind).Append('\n');
        foreach (var (k, v) in fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(v == null ? "\u0000" : v.Replace("\n", "\\n")).Append('\n');
        return Sha256(sb.ToString())[..32];
    }

    /// <summary>The canonical JSON line of a record.</summary>
    public static string Line(ShardRecord r) => JsonSerializer.Serialize(r, Json);

    /// <summary>Records sorted the way the file stores them (kind order of <see cref="ShardKinds.All"/>, then id).</summary>
    public static List<ShardRecord> Canonical(IEnumerable<ShardRecord> records) =>
        records.OrderBy(r => KindOrder(r.Kind)).ThenBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();

    private static int KindOrder(string kind)
    {
        int i = ShardKinds.All.ToList().IndexOf(kind);
        return i < 0 ? int.MaxValue : i;
    }

    /// <summary>sha256 over "kind\n" + each line + "\n", kinds in file order.</summary>
    public static string ContentHash(IEnumerable<ShardRecord> records)
    {
        var sb = new StringBuilder();
        foreach (var g in Canonical(records).GroupBy(r => r.Kind))
        {
            sb.Append(g.Key).Append('\n');
            foreach (var r in g) sb.Append(Line(r)).Append('\n');
        }
        return Sha256(sb.ToString());
    }

    // ======================= write =======================

    /// <summary>Writes a shard atomically (temp file + move). Returns the bytes written.</summary>
    public static long Write(string path, Shard shard)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".writing";
        using (var fs = File.Create(tmp)) WriteTo(fs, shard);
        File.Move(tmp, full, overwrite: true);
        return new FileInfo(full).Length;
    }

    public static byte[] ToBytes(Shard shard)
    {
        using var ms = new MemoryStream();
        WriteTo(ms, shard);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the zip. The manifest's counts, content hash and sidecar hashes are recomputed from the content (whatever the
    /// caller put there), so a written shard always verifies.
    /// </summary>
    public static void WriteTo(Stream stream, Shard shard)
    {
        var records = Canonical(shard.Records);
        byte[]? emb = shard.Embeddings.Count > 0 && shard.Manifest.Embeddings is { } ei ? EncodeEmbeddings(shard.Embeddings, ei.Dims) : null;
        byte[]? sums = shard.Summaries.Count > 0
            ? Encoding.UTF8.GetBytes(string.Concat(shard.Summaries.OrderBy(s => s.ContextHash, StringComparer.Ordinal)
                .Select(s => JsonSerializer.Serialize(s, Json) + "\n")))
            : null;
        var manifest = shard.Manifest with
        {
            Format = FormatVersion,
            Counts = records.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()),
            Total = records.Count,
            ContentHash = ContentHash(records),
            Embeddings = emb != null ? shard.Manifest.Embeddings! with { Count = shard.Embeddings.Count, Sha256 = Sha256(emb) } : null,
            Summaries = sums != null ? new ShardSummaryInfo { Count = shard.Summaries.Count, Sha256 = Sha256(sums) } : null,
        };
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Put(zip, ManifestEntry, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, Pretty)));
        foreach (var g in records.GroupBy(r => r.Kind))
            Put(zip, $"records/{g.Key}.jsonl", Encoding.UTF8.GetBytes(string.Concat(g.Select(r => Line(r) + "\n"))));
        if (emb != null) Put(zip, EmbeddingsEntry, emb, CompressionLevel.Fastest);
        if (sums != null) Put(zip, SummariesEntry, sums);
    }

    private static void Put(ZipArchive zip, string name, byte[] bytes, CompressionLevel level = CompressionLevel.Optimal)
    {
        var e = zip.CreateEntry(name, level);
        e.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var s = e.Open();
        s.Write(bytes);
    }

    private static byte[] EncodeEmbeddings(IReadOnlyDictionary<string, float[]> vectors, int dims)
    {
        using var ms = new MemoryStream();
        Span<byte> four = stackalloc byte[4];
        ms.Write(Magic);
        BinaryPrimitives.WriteInt32LittleEndian(four, dims); ms.Write(four);
        BinaryPrimitives.WriteInt32LittleEndian(four, vectors.Count); ms.Write(four);
        foreach (var (hash, vec) in vectors.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            if (vec.Length != dims) throw new InvalidOperationException($"vector for {hash} has {vec.Length} dims, the shard {dims}");
            var hb = Encoding.UTF8.GetBytes(hash);
            BinaryPrimitives.WriteInt32LittleEndian(four, hb.Length); ms.Write(four);
            ms.Write(hb);
            foreach (var f in vec) { BinaryPrimitives.WriteSingleLittleEndian(four, f); ms.Write(four); }
        }
        return ms.ToArray();
    }

    // ======================= read =======================

    public static Shard Read(string path)
    {
        if (!File.Exists(path)) throw new ShardFormatException($"no shard at {path}");
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static Shard Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        return Read(ms);
    }

    /// <summary>The manifest alone (lists, previews); still checks the format version.</summary>
    public static ShardManifest ReadManifest(string path)
    {
        using var fs = File.OpenRead(path);
        using var zip = OpenZip(fs);
        return CheckManifest(ParseManifest(zip));
    }

    public static Shard Read(Stream stream)
    {
        using var zip = OpenZip(stream);
        var manifest = CheckManifest(ParseManifest(zip));

        var records = new List<ShardRecord>();
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("records/", StringComparison.Ordinal) && e.FullName.EndsWith(".jsonl", StringComparison.Ordinal))
                     .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            var kind = e.FullName["records/".Length..^".jsonl".Length];
            if (!ShardKinds.All.Contains(kind)) throw new ShardFormatException($"unknown record kind '{kind}' in the shard (written by a newer MineClaw?)");
            foreach (var line in Encoding.UTF8.GetString(Bytes(e)).Split('\n'))
            {
                if (line.Length == 0) continue;
                ShardRecord r;
                try { r = JsonSerializer.Deserialize<ShardRecord>(line, Json) ?? throw new JsonException("null record"); }
                catch (JsonException ex) { throw new ShardFormatException($"unreadable record in {e.FullName}: {ex.Message}"); }
                if (r.Kind != kind) throw new ShardFormatException($"record {r.Id} of kind {r.Kind} is filed under {kind}");
                if (RecordHash(r.Kind, r.Fields) != r.Hash) throw new ShardFormatException($"record {r.Id} fails its hash: the shard is corrupt or was edited");
                records.Add(r);
                if (records.Count > MaxRecords) throw new ShardFormatException($"more than {MaxRecords:N0} records");
            }
        }
        if (records.Count != manifest.Total) throw new ShardFormatException($"the manifest lists {manifest.Total} records, the shard holds {records.Count}");
        if (ContentHash(records) != manifest.ContentHash)
            throw new ShardFormatException("content hash mismatch: the shard is corrupt or was edited after export");

        var embeddings = new Dictionary<string, float[]>(StringComparer.Ordinal);
        if (manifest.Embeddings is { } ei)
        {
            var entry = zip.GetEntry(EmbeddingsEntry) ?? throw new ShardFormatException("the manifest lists embeddings but embeddings.bin is missing");
            var bytes = Bytes(entry);
            if (Sha256(bytes) != ei.Sha256) throw new ShardFormatException("embeddings.bin fails its hash: the shard is corrupt");
            DecodeEmbeddings(bytes, ei.Dims, embeddings);
            if (embeddings.Count != ei.Count) throw new ShardFormatException($"embeddings.bin holds {embeddings.Count} vectors, the manifest {ei.Count}");
        }

        var summaries = new List<ShardSummary>();
        if (manifest.Summaries is { } si && zip.GetEntry(SummariesEntry) is { } se)
        {
            var bytes = Bytes(se);
            if (Sha256(bytes) == si.Sha256) // a broken cache is only a cache: drop it, keep the shard
                foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
                {
                    if (line.Length == 0) continue;
                    try { if (JsonSerializer.Deserialize<ShardSummary>(line, Json) is { } s) summaries.Add(s); }
                    catch (JsonException) { }
                }
        }
        return new Shard { Manifest = manifest, Records = records, Embeddings = embeddings, Summaries = summaries };
    }

    private static ZipArchive OpenZip(Stream stream)
    {
        try { return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new ShardFormatException("not a .mcshard file (not a zip archive)"); }
    }

    private static ShardManifest ParseManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new ShardFormatException("not a .mcshard file (manifest.json is missing)");
        try { return JsonSerializer.Deserialize<ShardManifest>(Bytes(entry), Json) ?? throw new JsonException("empty manifest"); }
        catch (JsonException ex) { throw new ShardFormatException("unreadable manifest.json: " + ex.Message); }
    }

    /// <summary>Format and schema version gate.</summary>
    public static ShardManifest CheckManifest(ShardManifest m)
    {
        if (m.Format != FormatVersion)
            throw new ShardFormatException(m.Format > FormatVersion
                ? $"shard format v{m.Format} is newer than this build reads (v{FormatVersion}); update the Memory Explorer / bot first"
                : $"shard format v{m.Format} is no longer supported (this build reads v{FormatVersion}); re-export it");
        if (m.Source.SchemaVersion > AgentDb.LatestSchemaVersion)
            throw new ShardFormatException($"the shard was exported from memory schema v{m.Source.SchemaVersion}, newer than this build (v{AgentDb.LatestSchemaVersion}); update MineClaw before importing it");
        if (string.IsNullOrWhiteSpace(m.Name)) throw new ShardFormatException("the manifest has no name");
        return m;
    }

    private static byte[] Bytes(ZipArchiveEntry e)
    {
        if (e.Length > MaxEntryBytes) throw new ShardFormatException($"{e.FullName} is over {MaxEntryBytes / 1024 / 1024} MB");
        using var s = e.Open();
        using var ms = new MemoryStream((int)Math.Max(0, Math.Min(e.Length, int.MaxValue)));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static void DecodeEmbeddings(byte[] b, int dims, Dictionary<string, float[]> into)
    {
        try
        {
            if (b.Length < 16 || !b.AsSpan(0, 8).SequenceEqual(Magic)) throw new ShardFormatException("embeddings.bin has no MCEMB1 header");
            int d = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(8));
            int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12));
            if (d != dims || d <= 0 || d > 65536 || n < 0) throw new ShardFormatException($"embeddings.bin is {d}-dimensional, the manifest says {dims}");
            int o = 16;
            for (int i = 0; i < n; i++)
            {
                int hl = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)); o += 4;
                if (hl <= 0 || hl > 256) throw new ShardFormatException("embeddings.bin is corrupt");
                var hash = Encoding.UTF8.GetString(b, o, hl); o += hl;
                var v = new float[d];
                for (int k = 0; k < d; k++) { v[k] = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)); o += 4; }
                into[hash] = v;
            }
            if (o != b.Length) throw new ShardFormatException("embeddings.bin has trailing bytes");
        }
        catch (ArgumentOutOfRangeException) { throw new ShardFormatException("embeddings.bin is truncated"); }
    }
}
