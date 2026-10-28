using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Agent.Admin.Shards.Packs;

/// <summary>One default pack as <c>shards/defaults/index.json</c> lists it.</summary>
public sealed record PackIndexEntry
{
    public required string File { get; init; }
    public required string Name { get; init; }
    /// <summary>version | mode</summary>
    public required string Kind { get; init; }
    public required string Version { get; init; }
    public string? Mode { get; init; }
    public string? Description { get; init; }
    public SortedDictionary<string, int> Counts { get; init; } = new(StringComparer.Ordinal);
    public int Records { get; init; }
    public long Bytes { get; init; }
    public int Vectors { get; init; }
    public int NeedsEmbedding { get; init; }
    public string? EmbeddingModel { get; init; }
    public int Dims { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
    public string? Source { get; init; }
    public string? SourceHash { get; init; }
    public string? ContentHash { get; init; }
    public string? Builder { get; init; }

    [JsonIgnore] public bool IsMode => Kind == ShardPackInfo.ModeKind;
}

/// <summary>
/// <c>index.json</c> of a default-packs folder: every pack's version, kind, description, counts, size, vectors, builder version and
/// source hash. Written sorted and without timestamps, so a rebuild from the same inputs leaves it byte-identical.
/// </summary>
public sealed record PackIndex
{
    public const string FileName = "index.json";
    public int Format { get; init; } = 1;
    public string? Note { get; init; }
    public IReadOnlyList<PackIndexEntry> Packs { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(ShardFile.Json)
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PackIndex Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return new PackIndex();
        try { return JsonSerializer.Deserialize<PackIndex>(File.ReadAllText(path), Json) ?? new PackIndex(); }
        catch (JsonException) { return new PackIndex(); }
    }

    /// <summary>Replaces the entries for the same files, drops entries whose file is gone, sorts by file name.</summary>
    public PackIndex Upsert(IEnumerable<PackIndexEntry> entries, string dir)
    {
        var map = Packs.Where(p => File.Exists(Path.Combine(dir, p.File))).ToDictionary(p => p.File, StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) map[e.File] = e;
        return this with { Packs = map.Values.OrderBy(p => p.File, StringComparer.Ordinal).ToList() };
    }

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var text = JsonSerializer.Serialize(this, Json).Replace("\r\n", "\n") + "\n";
        var path = Path.Combine(dir, FileName);
        var tmp = path + ".writing";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
