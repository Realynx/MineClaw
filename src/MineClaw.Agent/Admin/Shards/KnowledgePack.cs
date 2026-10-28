using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Admin.Shards;

/// <summary>
/// The knowledge packs a db holds (meta <c>knowledge_pack</c>): ONE version pack (the version-tagged seed game data: recipes, items,
/// blocks, smelting, dig facts, plus that version's general mechanics docs) and any number of mode packs (world-mode advice such as
/// skyblock). <see cref="Modes"/> null = a legacy db (no meta, or a swap from before mode packs): every mode doc on disk counts.
/// </summary>
public sealed record KnowledgePackInfo(string Version, string? Name = null, DateTimeOffset? Installed = null, string? Shard = null)
{
    public bool IsDefault => Version.Equals(KnowledgePack.DefaultVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Active mode packs; null = legacy (all mode docs on disk are in), empty = no mode (a plain world).</summary>
    public IReadOnlyList<KnowledgeModeInfo>? Modes { get; init; }

    /// <summary>The version pack's core block (the cached-prompt lines), used when none of its <see cref="Files"/> is on disk.</summary>
    public string? Core { get; init; }

    /// <summary>The knowledge doc files the version pack carries (file names under docs/knowledge).</summary>
    public IReadOnlyList<string>? Files { get; init; }

    public bool HasMode(string mode) => Modes == null || Modes.Any(m => m.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));

    /// <summary>"b1.8.1-gamedata + skyblock" / "b1.8.1 (built-in seed) + all mode docs" / "b1.8.1-gamedata, no mode".</summary>
    public string Label
    {
        get
        {
            var head = Name is { Length: > 0 } n && n != "built-in seed" ? $"{n} ({Version})" : $"{Version} (built-in seed)";
            return Modes == null ? head + " + every mode doc on disk"
                : Modes.Count == 0 ? head + ", no mode"
                : head + " + " + string.Join(", ", Modes.Select(m => m.Mode));
        }
    }
}

/// <summary>One active mode pack (e.g. skyblock): its name, the version it was written for, the shard it came from, its core and files.</summary>
public sealed record KnowledgeModeInfo(string Mode, string? Name = null, string? Version = null, DateTimeOffset? Installed = null, string? Shard = null)
{
    public string? Core { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
}

/// <summary>
/// The pack marker of a knowledge doc (<c>&lt;!-- pack version=b1.8.1 --&gt;</c> = that version's pack;
/// <c>&lt;!-- pack mode=skyblock version=b1.8.1 name=skyblock --&gt;</c> = a mode pack). A doc without a marker is always in.
/// </summary>
public sealed partial record KnowledgeDocPack(string? Version, string? Mode, string? Name)
{
    [GeneratedRegex(@"<!--\s*pack\b(?<attrs>[^>]*?)-->", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    [GeneratedRegex(@"(?<k>[a-z]+)\s*=\s*(?<v>[A-Za-z0-9._+-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Attr();

    public bool IsMode => Mode != null;

    /// <summary>The pack's name: the marker's, else "{mode}-{version}" / "{version}-gamedata".</summary>
    public string PackName(string version) => Name ?? (Mode != null ? $"{Mode}-{Version ?? version}" : $"{Version ?? version}-gamedata");

    public static KnowledgeDocPack? Parse(string markdown)
    {
        var m = Marker().Match(markdown);
        if (!m.Success) return null;
        string? version = null, mode = null, name = null;
        foreach (Match a in Attr().Matches(m.Groups["attrs"].Value))
        {
            var v = a.Groups["v"].Value;
            switch (a.Groups["k"].Value.ToLowerInvariant())
            {
                case "version": version = v; break;
                case "mode": mode = v.ToLowerInvariant(); break;
                case "name": name = v; break;
            }
        }
        return version == null && mode == null ? null : new KnowledgeDocPack(version, mode, name);
    }

    /// <summary>Whether a doc with this marker is part of the db's active packs.</summary>
    public bool ActiveIn(KnowledgePackInfo active)
    {
        bool versionOk = Version == null || Version.Equals(active.Version, StringComparison.OrdinalIgnoreCase);
        return Mode != null ? versionOk && active.HasMode(Mode) : versionOk;
    }
}

/// <summary>
/// The active knowledge pack of a db (meta <c>knowledge_pack</c>). A db without the key holds the built-in b1.8.1 seed corpus.
/// Swapping a pack (<see cref="ShardImporter"/>, mode swap) replaces every <c>gamedata</c> doc and sets this; the bot's
/// retrieval only ever sees the active pack's docs, and its prompt names the version when it isn't the default.
/// </summary>
public static partial class KnowledgePack
{
    public const string MetaKey = "knowledge_pack";
    /// <summary>The version the bot plays and the seed corpus describes.</summary>
    public const string DefaultVersion = "b1.8.1";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,31}$")]
    private static partial Regex VersionRx();

    public static bool ValidVersion(string? v) => v != null && VersionRx().IsMatch(v);

    public static KnowledgePackInfo Active(AgentDb db)
    {
        string? json;
        try { json = db.GetMeta(MetaKey); }
        catch (Microsoft.Data.Sqlite.SqliteException) { json = null; }
        if (string.IsNullOrWhiteSpace(json)) return new KnowledgePackInfo(DefaultVersion, "built-in seed");
        try
        {
            var p = JsonSerializer.Deserialize<KnowledgePackInfo>(json, ShardFile.Json);
            return p != null && ValidVersion(p.Version) ? p : new KnowledgePackInfo(DefaultVersion, "built-in seed");
        }
        catch (JsonException) { return new KnowledgePackInfo(DefaultVersion, "built-in seed"); }
    }

    /// <summary>
    /// Core blocks of active packs none of whose files were read from disk (a pack imported into a bot whose docs/knowledge lacks
    /// them): the version pack's first, then each mode's. Files on disk always win (they're the editable truth).
    /// </summary>
    public static IEnumerable<string> StoredCores(KnowledgePackInfo active, IReadOnlySet<string> filesOnDisk)
    {
        bool Missing(IReadOnlyList<string>? files) => files is { Count: > 0 } f && !f.Any(filesOnDisk.Contains);
        if (active.Core is { Length: > 0 } vc && Missing(active.Files)) yield return vc;
        foreach (var m in active.Modes ?? [])
            if (m.Core is { Length: > 0 } mc && Missing(m.Files)
                && (m.Version == null || m.Version.Equals(active.Version, StringComparison.OrdinalIgnoreCase)))
                yield return mc;
    }

    public static string ActiveVersion(AgentDb db) => Active(db).Version;

    public static void SetActive(AgentDb db, KnowledgePackInfo info)
    {
        if (!ValidVersion(info.Version)) throw new ArgumentException($"'{info.Version}' is not a version tag (e.g. b1.8.1)");
        db.SetMeta(MetaKey, JsonSerializer.Serialize(info, ShardFile.Json));
    }

    /// <summary>One cached-prompt line naming a non-default pack (null for the default: the prefix stays byte-identical).</summary>
    public static string? PromptLine(AgentDb db)
    {
        var p = Active(db);
        return p.IsDefault ? null
            : $"Game knowledge pack: Minecraft {p.Version}. Recipes, items and block facts in [memory] and recall follow {p.Version}; prefer them over what you remember of other versions.";
    }
}

/// <summary>Secrets never travel in a shard: records that talk about keys/passwords are dropped, token-shaped strings redacted.</summary>
public static partial class ShardRedactor
{
    [GeneratedRegex(@"\b(?:api[\s_-]?keys?|passwords?|passwd|credentials?|secret\s+keys?|private\s+keys?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretTopic();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{6,}|\b(?:sk|pk|rk|ghp|gho|ghs|glpat|xox[abpr])[-_][A-Za-z0-9_\-]{12,}|\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}(?:\.[A-Za-z0-9_\-]+)?|\b[A-Fa-f0-9]{40,}\b|\b(?:token|bearer|apikey|api_key|x-supervisor-token)\s*[:=]?\s*[A-Za-z0-9_\-./+=]{12,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();

    /// <summary>True when the text is about a secret (the whole record is left out).</summary>
    public static bool IsSecret(string? text) => text != null && SecretTopic().IsMatch(text);

    /// <summary>Token-shaped substrings replaced with "[redacted]"; the count of replacements.</summary>
    public static string Redact(string text, out int count)
    {
        int n = 0;
        var s = TokenShape().Replace(text, _ => { n++; return "[redacted]"; });
        count = n;
        return s;
    }
}
