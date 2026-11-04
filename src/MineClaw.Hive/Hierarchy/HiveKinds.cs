namespace MineClaw.Hive.Hierarchy;

/// <summary>
/// What a clone (a "subagent" in game) is. Main is the default/root hive and is not a clone kind.
/// <list type="bullet">
/// <item><see cref="Hive"/>: its own brain loop (Opus 5.5 or DeepSeek via Ollama) driving its own character(s); same tools as main.</item>
/// <item><see cref="Subhive"/>: another character driven by its owning hive's brain (one brain, many bodies).</item>
/// <item><see cref="Script"/>: a character that runs one saved script, brain asleep; owned by whoever spawned it.</item>
/// </list>
/// </summary>
public enum CloneKind { Hive, Subhive, Script }

/// <summary>The brain of a <see cref="CloneKind.Hive"/> clone. Subhive/script bodies have no brain of their own.</summary>
public enum BrainKind { Opus, DeepSeek }

public static class HiveKinds
{
    public static string Name(CloneKind k) => k switch { CloneKind.Hive => "hive", CloneKind.Subhive => "subhive", _ => "script" };
    public static string Name(BrainKind b) => b == BrainKind.DeepSeek ? "deepseek" : "opus";

    /// <summary>"hive" | "subhive" | "sub" | "body" | "script" | "script-only" → the kind; null input = null; unknown = error.</summary>
    public static (CloneKind? Kind, string? Error) ParseKind(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        var x = s.Trim().ToLowerInvariant().Replace("_", "-");
        return KindWords.TryGetValue(x, out var k) ? (k, null) : (null, $"kind '{x}' is hive|subhive|script");
    }

    private static readonly Dictionary<string, CloneKind> KindWords = new(StringComparer.Ordinal)
    {
        ["hive"] = CloneKind.Hive, ["brain"] = CloneKind.Hive,
        ["subhive"] = CloneKind.Subhive, ["sub-hive"] = CloneKind.Subhive, ["sub"] = CloneKind.Subhive, ["body"] = CloneKind.Subhive,
        ["script"] = CloneKind.Script, ["script-only"] = CloneKind.Script, ["scriptonly"] = CloneKind.Script,
    };

    /// <summary>"opus" | "deepseek" → the brain; anything else is treated as a model profile name (legacy <c>profile</c>).</summary>
    public static (BrainKind? Brain, string? Profile) ParseBrain(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (null, null);
        var t = s.Trim().ToLowerInvariant();
        if (t is "opus" or "claude" or "default") return (BrainKind.Opus, null);
        if (t.StartsWith("deepseek", StringComparison.Ordinal)) return (BrainKind.DeepSeek, null);
        return (null, s.Trim());
    }
}
