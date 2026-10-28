using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Admin.Shards;

/// <summary>The knowledge pack a db holds: the version-tagged seed game data (recipes, items, blocks, smelting, dig facts).</summary>
public sealed record KnowledgePackInfo(string Version, string? Name = null, DateTimeOffset? Installed = null, string? Shard = null)
{
    public bool IsDefault => Version.Equals(KnowledgePack.DefaultVersion, StringComparison.OrdinalIgnoreCase);
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
