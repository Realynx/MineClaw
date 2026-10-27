namespace MineClaw.Agent.Llm;

/// <summary>The brain's (cached) guidance for picking a model profile for delegate / hive spawn.</summary>
public static class ProfilePrompts
{
    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase) { "opus", "sonnet", "haiku" };

    /// <summary>
    /// One short line, only when a choice exists (≥2 usable profiles): cheapest that fits; built-in guidance plus the notes of
    /// extra usable profiles ("local: ..."). Unusable profiles (no model, provider unconfigured) are never mentioned.
    /// </summary>
    public static string? GuidanceLine(LlmOptions o)
    {
        var usable = o.UsableProfiles().ToList();
        if (usable.Count < 2) return null;
        var cheap = string.Join("/", usable.Where(p => p.Equals("haiku", StringComparison.OrdinalIgnoreCase) || p.Equals("local", StringComparison.OrdinalIgnoreCase)));
        var parts = new List<string>();
        if (cheap.Length > 0) parts.Add($"{cheap} for simple repetitive chores (mining loops, hauling, sorting)");
        if (usable.Contains("sonnet", StringComparer.OrdinalIgnoreCase)) parts.Add("sonnet (the default worker) for normal multi-step work");
        if (usable.Contains("opus", StringComparer.OrdinalIgnoreCase)) parts.Add("opus only as a clone's brain (hive spawn); a delegate worker never runs on opus");
        var extra = usable.Where(p => !BuiltIn.Contains(p) && o.Profile(p)?.Notes is { Length: > 0 })
            .Select(p => $"{p}: {o.Profile(p)!.Notes!.Trim()}{(o.Profile(p)!.CostHint is { Length: > 0 } c ? $" ({c})" : "")}");
        var line = "Model profiles (delegate/hive spawn profile=): pick the cheapest that fits: " + string.Join("; ", parts) + ".";
        var notes = string.Join("; ", extra);
        return notes.Length > 0 ? line + " " + notes + "." : line;
    }
}
