using System.Text.Json;
using System.Text.Json.Serialization;

namespace MineClaw.Agent.Admin.Shards;

/// <summary>
/// The record kinds a memory shard carries (one JSONL file each). World/block memory (observations, containers, chunks), the
/// inventory ledger, build plans, chat and routines are never part of a shard. Places and structures are "world" kinds: exported
/// and imported only when explicitly chosen; standing rules only when "include rules" is ticked.
/// </summary>
public static class ShardKinds
{
    public const string Fact = "fact";
    public const string Claim = "claim";
    public const string Rule = "rule";
    public const string Trait = "trait";
    public const string Lesson = "lesson";
    public const string Place = "place";
    public const string Structure = "structure";
    public const string Episode = "episode";
    public const string Knowledge = "knowledge";
    public const string GameData = "gamedata";

    public static IReadOnlyList<string> All { get; } = [Fact, Claim, Rule, Trait, Lesson, Place, Structure, Episode, Knowledge, GameData];
    /// <summary>Kinds stored as plain RAG docs (the doc is the record).</summary>
    public static IReadOnlyList<string> DocKinds { get; } = [Episode, Knowledge, GameData];
    /// <summary>World memory: only with <see cref="ShardSelection.IncludeWorld"/> / <see cref="ShardImportOptions.IncludeWorld"/>.</summary>
    public static IReadOnlyList<string> World { get; } = [Place, Structure];

    public static bool IsDoc(string kind) => DocKinds.Contains(kind);
    public static bool IsWorld(string kind) => World.Contains(kind);

    /// <summary>Other words for a kind (after a plural 's' of a known kind is dropped).</summary>
    private static readonly Dictionary<string, string> KindAliases = new(StringComparer.Ordinal)
    {
        ["note"] = Fact, ["player"] = Fact, ["directive"] = Rule, ["waypoint"] = Place,
        ["recipe"] = GameData, ["recipes"] = GameData, ["game"] = GameData, ["kb"] = Knowledge,
    };

    public static string? Normalize(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var k = kind.Trim().ToLowerInvariant();
        if (k.EndsWith('s') && All.Contains(k[..^1])) k = k[..^1];
        if (KindAliases.TryGetValue(k, out var alias)) return alias;
        return All.Contains(k) ? k : null;
    }

    /// <summary>The kind name the RAPTOR section rebuilder takes for a shard kind (null = not in the tree).</summary>
    public static string? RebuildKind(string kind) => kind switch
    {
        Fact => MemoryKinds.Fact,
        Structure => MemoryKinds.Structure,
        Lesson => MemoryKinds.Lesson,
        Episode => "episode",
        Knowledge => "knowledge",
        GameData => "gamedata",
        _ => null, // claims, rules, traits, places: flat index only
    };
}

/// <summary>How a shard's records were chosen (stored in the manifest; replace-section re-applies it to the target).</summary>
/// <param name="Mode">subtree | filter | person | pack</param>
public sealed record ShardSelection
{
    public string Mode { get; init; } = "filter";
    /// <summary>subtree: the RAPTOR node the shard was cut from (indexes don't transfer between dbs; informational).</summary>
    public int? Node { get; init; }
    /// <summary>subtree: the leaf doc refs under the node (resolved by the caller from the loaded tree).</summary>
    public IReadOnlyList<string>? LeafRefs { get; init; }
    /// <summary>filter: record kinds (null/empty = every kind the flags allow).</summary>
    public IReadOnlyList<string>? Kinds { get; init; }
    /// <summary>filter: only records in this dimension (0 overworld, -1 nether); positionless records then don't match.</summary>
    public int? Dimension { get; init; }
    /// <summary>filter: text every matching record contains (all words), or a semantic query when <see cref="Semantic"/>.</summary>
    public string? Query { get; init; }
    public bool Semantic { get; init; }
    /// <summary>person: the login and every name the bot or players use (Fox / PoofimaFox / foxy).</summary>
    public IReadOnlyList<string>? People { get; init; }
    /// <summary>pack: the Minecraft version the knowledge pack is for ("b1.8.1"); null = the db's active version.</summary>
    public string? PackVersion { get; init; }
    /// <summary>pack: also carry the docs/knowledge chunks (default: game data only).</summary>
    public bool IncludeKnowledgeDocs { get; init; }
    /// <summary>pack: a mode pack (e.g. "skyblock"): only the knowledge chunks of <see cref="KnowledgeFiles"/>, no game data.</summary>
    public string? PackMode { get; init; }
    /// <summary>pack: the knowledge doc files the pack carries (null = every chunk when <see cref="IncludeKnowledgeDocs"/>).</summary>
    public IReadOnlyList<string>? KnowledgeFiles { get; init; }
    /// <summary>Standing rules (directives) from trusted players.</summary>
    public bool IncludeRules { get; init; }
    /// <summary>Places and structures.</summary>
    public bool IncludeWorld { get; init; }

    [JsonIgnore] public bool IsPack => Mode == "pack";
}

/// <summary>One record of a shard: a stable id, its kind, the fields that make it up (hashed) and provenance.</summary>
public sealed record ShardRecord
{
    /// <summary>Stable across dbs: a doc ref for doc kinds, a natural key (place/structure/lesson name) or a content hash.</summary>
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public int? Dimension { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
    /// <summary>The doc ref in the exporting db (provenance; ids of source rows are local).</summary>
    public string? Ref { get; init; }
    /// <summary>Material hash of the doc the record produces (the embedding sidecar key).</summary>
    public string? DocHash { get; init; }
    /// <summary>Where it came from: store source ("memory", "seed", "tool"...), and the clone that wrote it.</summary>
    public string? Source { get; init; }
    public string? CloneId { get; init; }
    /// <summary>The record's content (all values as strings; hashed into <see cref="Hash"/>).</summary>
    public required SortedDictionary<string, string?> Fields { get; init; }
    /// <summary>Informational values that drift on their own (strength, reinforced): carried, never hashed or diffed.</summary>
    public SortedDictionary<string, string?>? Info { get; init; }
    /// <summary>sha256 of kind + fields (change detection).</summary>
    public string Hash { get; init; } = "";

    public string? Get(string field) => Fields.TryGetValue(field, out var v) ? v : null;

    /// <summary>A one-line label for diffs and previews.</summary>
    [JsonIgnore]
    public string Label => Kind switch
    {
        ShardKinds.Fact => $"{Get("subject")}: {Get("text")}",
        ShardKinds.Claim => $"{Get("claimant")} says {Get("text")} ({Get("status")})",
        ShardKinds.Rule => $"{Get("player")}: {Get("text")}",
        ShardKinds.Trait => $"{Get("section")}{(Get("player") is { Length: > 0 } p ? " " + p : "")}: {Get("text")}",
        ShardKinds.Lesson => $"{Get("title")}: {Get("correction")}",
        ShardKinds.Place => $"{Get("name")} {Get("x")},{Get("y")},{Get("z")}",
        ShardKinds.Structure => $"{Get("name")} ({Get("kind")})",
        _ => $"{Get("title")}: {Get("body")}",
    };
}

public sealed record ShardSourceInfo
{
    /// <summary>File name only (never a full path).</summary>
    public string? Db { get; init; }
    public int SchemaVersion { get; init; }
    public string? Bot { get; init; }
}

public sealed record ShardEmbeddingInfo
{
    public required string Model { get; init; }
    public int Dims { get; init; }
    public int Count { get; init; }
    public string Sha256 { get; init; } = "";
}

public sealed record ShardSummaryInfo
{
    public int Count { get; init; }
    public string Sha256 { get; init; } = "";
}

/// <summary>manifest.json of a <c>.mcshard</c>.</summary>
public sealed record ShardManifest
{
    /// <summary>File format version (this build reads <see cref="ShardFile.FormatVersion"/>).</summary>
    public int Format { get; init; } = ShardFile.FormatVersion;
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset Created { get; init; }
    public string? CreatedBy { get; init; }
    public ShardSourceInfo Source { get; init; } = new();
    /// <summary>subtree | filter | person | pack</summary>
    public string Kind { get; init; } = "filter";
    public ShardSelection Selection { get; init; } = new();
    /// <summary>Knowledge packs: the Minecraft version tag ("b1.8.1").</summary>
    public string? PackVersion { get; init; }
    public Dictionary<string, int> Counts { get; init; } = new();
    public int Total { get; init; }
    /// <summary>sha256 over the records (kinds in order, one canonical JSON line per record).</summary>
    public string ContentHash { get; init; } = "";
    public ShardEmbeddingInfo? Embeddings { get; init; }
    /// <summary>Optional RAPTOR summaries cache (never truth: the tree is rebuilt from leaves on import).</summary>
    public ShardSummaryInfo? Summaries { get; init; }
    /// <summary>Records dropped or redacted because they looked like secrets.</summary>
    public int SecretsRedacted { get; init; }
    /// <summary>Default packs (shards/defaults, built by tools/ShardPacks): version or mode, core, files, builder and source hash.</summary>
    public ShardPackInfo? Pack { get; init; }
}

/// <summary>
/// What a default knowledge pack is: the version pack of a Minecraft version (game data + that version's general mechanics docs) or a
/// mode pack (world-mode advice such as skyblock), built reproducibly from pristine sources (never from a live db).
/// </summary>
public sealed record ShardPackInfo
{
    public const string VersionKind = "version", ModeKind = "mode";

    /// <summary>version | mode</summary>
    public required string Kind { get; init; }
    /// <summary>"b1.8.1-gamedata" / "skyblock".</summary>
    public required string Name { get; init; }
    public required string Version { get; init; }
    /// <summary>Mode packs: the mode ("skyblock").</summary>
    public string? Mode { get; init; }
    /// <summary>The pack's cached-prompt core block (the knowledge docs' core sections, in file order).</summary>
    public string? Core { get; init; }
    /// <summary>Knowledge doc files the pack carries (their chunks are the pack's knowledge records).</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
    /// <summary>Where the game data came from ("MineClaw.Data b1.8.1 tables", "minecraft-data pc/1.8").</summary>
    public string? Source { get; init; }
    /// <summary>sha256 over every input (game-data docs + knowledge files): equal inputs, equal pack.</summary>
    public string? SourceHash { get; init; }
    public string? Builder { get; init; }
    /// <summary>Embeddable records without a vector in the pack: the target embeds them on import.</summary>
    public int NeedsEmbedding { get; init; }

    [JsonIgnore] public bool IsMode => Kind == ModeKind;
}

/// <summary>A cached RAPTOR summary: reused by a rebuild that reproduces the identical cluster (no summarizer call).</summary>
public sealed record ShardSummary(string ContextHash, int Layer, string Text);

/// <summary>An opened shard: manifest, records per kind, embeddings (hash → vector) and the summaries cache.</summary>
public sealed class Shard
{
    public required ShardManifest Manifest { get; init; }
    public required IReadOnlyList<ShardRecord> Records { get; init; }
    public IReadOnlyDictionary<string, float[]> Embeddings { get; init; } = new Dictionary<string, float[]>();
    public IReadOnlyList<ShardSummary> Summaries { get; init; } = [];
}

/// <summary>Enums travel as kebab-case strings ("replace-section", "keep-newer") in every serializer.</summary>
public sealed class KebabEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.KebabCaseLower) where T : struct, Enum;

[JsonConverter(typeof(KebabEnumConverter<ShardImportMode>))]
public enum ShardImportMode { Merge, ReplaceSection, SwapPack, AddMode, RemoveMode }

/// <summary>What wins when a record exists on both sides with different content.</summary>
[JsonConverter(typeof(KebabEnumConverter<ShardConflictPolicy>))]
public enum ShardConflictPolicy { KeepNewer, PreferShard, KeepMine }

public sealed record ShardImportOptions
{
    public ShardImportMode Mode { get; init; } = ShardImportMode.Merge;
    public ShardConflictPolicy Conflict { get; init; } = ShardConflictPolicy.KeepNewer;
    /// <summary>Import standing rules (directives) from trusted players.</summary>
    public bool IncludeRules { get; init; }
    /// <summary>Import places and structures.</summary>
    public bool IncludeWorld { get; init; }
    public string Who { get; init; } = "admin";
    /// <summary>Cap for each RAPTOR section rebuild the import queues.</summary>
    public decimal RebuildMaxUsd { get; init; } = 0.25m;

    private static readonly Dictionary<string, ShardImportMode> ModeWords = new(StringComparer.Ordinal)
    {
        ["replace"] = ShardImportMode.ReplaceSection, ["replace-section"] = ShardImportMode.ReplaceSection,
        ["replacesection"] = ShardImportMode.ReplaceSection, ["replace_section"] = ShardImportMode.ReplaceSection,
        ["swap"] = ShardImportMode.SwapPack, ["swap-pack"] = ShardImportMode.SwapPack, ["swappack"] = ShardImportMode.SwapPack, ["swap_pack"] = ShardImportMode.SwapPack,
        ["add-mode"] = ShardImportMode.AddMode, ["addmode"] = ShardImportMode.AddMode, ["add_mode"] = ShardImportMode.AddMode, ["add"] = ShardImportMode.AddMode,
        ["remove-mode"] = ShardImportMode.RemoveMode, ["removemode"] = ShardImportMode.RemoveMode, ["remove_mode"] = ShardImportMode.RemoveMode,
        ["remove"] = ShardImportMode.RemoveMode,
    };

    /// <summary>The import mode a word names; anything else (or nothing) merges.</summary>
    public static ShardImportMode ParseMode(string? s) =>
        s == null ? ShardImportMode.Merge : ModeWords.GetValueOrDefault(s.Trim().ToLowerInvariant(), ShardImportMode.Merge);

    public static ShardConflictPolicy ParseConflict(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "shard" or "prefer-shard" or "prefershard" or "theirs" => ShardConflictPolicy.PreferShard,
        "mine" or "keep-mine" or "keepmine" or "ours" => ShardConflictPolicy.KeepMine,
        _ => ShardConflictPolicy.KeepNewer,
    };

    public static string Name(ShardImportMode m) => m switch
    {
        ShardImportMode.ReplaceSection => "replace-section",
        ShardImportMode.SwapPack => "swap-pack",
        ShardImportMode.AddMode => "add-mode",
        ShardImportMode.RemoveMode => "remove-mode",
        _ => "merge",
    };

    public static string Name(ShardConflictPolicy c) => c switch
    {
        ShardConflictPolicy.PreferShard => "prefer-shard",
        ShardConflictPolicy.KeepMine => "keep-mine",
        _ => "keep-newer",
    };
}

public static class ShardChange
{
    public const string Added = "added", Changed = "changed", Conflict = "conflict", Removed = "removed", Unchanged = "unchanged", Skipped = "skipped";
}

/// <param name="Change">added | changed | conflict | removed | unchanged | skipped</param>
/// <param name="Apply">True when the import will write this item (policy applied).</param>
public sealed record ShardDiffItem(string Id, string Kind, string Change, string Label, bool Apply, string? Note = null);

/// <summary>What an import would do (computed without writing anything).</summary>
public sealed record ShardDiff
{
    public required string Shard { get; init; }
    public ShardImportMode Mode { get; init; }
    public ShardConflictPolicy Conflict { get; init; }
    public IReadOnlyList<ShardDiffItem> Items { get; init; } = [];
    public int Added { get; init; }
    public int Changed { get; init; }
    public int Conflicts { get; init; }
    public int Removed { get; init; }
    public int Unchanged { get; init; }
    public int Skipped { get; init; }
    /// <summary>Records the import writes (added + applied changes/conflicts).</summary>
    public int Writes { get; init; }
    /// <summary>RAPTOR section rebuilds the import queues (only kinds that are in the tree and actually change).</summary>
    public IReadOnlyList<RebuildRequest> Rebuild { get; init; } = [];
    /// <summary>Summed estimate of those rebuilds (null when there's no tree).</summary>
    public RebuildEstimate? Estimate { get; init; }
    /// <summary>Shard vectors the target can reuse (same embedding model and dims) / docs that need a new embedding.</summary>
    public int ReusableVectors { get; init; }
    public int ToEmbed { get; init; }
    public string? PackFrom { get; init; }
    public string? PackTo { get; init; }
    /// <summary>Mode packs: the active modes before / after this import (null = not a pack import).</summary>
    public IReadOnlyList<string>? ModesFrom { get; init; }
    public IReadOnlyList<string>? ModesTo { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Why this import can't run as asked (nothing is written); null = it can.</summary>
    public string? Refused { get; init; }

    [JsonIgnore] public bool Empty => Writes == 0 && Removed == 0;
}

public sealed record ShardImportResult
{
    public bool Ok { get; init; }
    public required string Message { get; init; }
    public ShardDiff? Diff { get; init; }
    public int Written { get; init; }
    public int Removed { get; init; }
    public int ReusedVectors { get; init; }
    public int Embedded { get; init; }
    /// <summary>The scoped rebuilds to run next (the live bot starts them in the background itself).</summary>
    public IReadOnlyList<RebuildRequest> Rebuild { get; init; } = [];
    /// <summary>Live: the background rebuild job.</summary>
    public string? JobId { get; init; }
    public string? ActivePack { get; init; }
}

/// <summary>A shard refused as unreadable, corrupt or from an incompatible build (the message says which and why).</summary>
public sealed class ShardFormatException(string message) : Exception(message);
