namespace MineClaw.World;

/// <summary>
/// How strongly a block is protected (see ARCHITECTURE.md, "Protection policy").
/// <list type="bullet">
/// <item><b>Soft</b>: only stops the bot's own autonomous choices (picking materials, clearing space): unknown builds,
/// structures, farmland, generator walls, stations/containers as blocks, tree soil, the last trees, other bots' leases.
/// Yields to the bot's own placed blocks and to an explicit trusted-player command.</item>
/// <item><b>Hard</b>: never yields to a command: blocks a player marked "never break" (a trusted "you can break X" unmarks
/// them) and owner-configured regions. Void-support and lava safety are hard too but live in the client's dig vetoes.</item>
/// </list>
/// </summary>
public enum ProtectionLevel { None, Soft, Hard }

/// <param name="Reason">Stable category key for telemetry ("structure", "unknown_build", "farm", "generator", "station",
/// "container", "tree_soil", "last_trees", "claimed", "never_break", "config_region", ...).</param>
/// <param name="Why">Short human reason ("part of the main house build").</param>
/// <param name="Unlock">What a player says to unlock it, or null for the default phrase.</param>
/// <param name="Repeat">The same refusal was already given recently: don't ask again, wait for the unlock phrase.</param>
public readonly record struct ProtectionVerdict(ProtectionLevel Level, string Reason, string Why, string? Unlock = null, bool Repeat = false)
{
    public static readonly ProtectionVerdict None = new(ProtectionLevel.None, "", "");
    public bool Protected => Level != ProtectionLevel.None;
}

/// <summary>The one refusal format every protection refusal uses: why + how to unlock it (policy rule 5).</summary>
public static class ProtectionText
{
    /// <summary>Every refusal contains this; the worker stops at the first one and reports it.</summary>
    public const string Marker = " is protected";
    public const string DefaultUnlock = "you can break it";

    /// <summary>"log at 6,69,-6 is protected (part of the main house build); say 'you can break it' if you want it gone".</summary>
    public static string Refusal(string what, ProtectionVerdict v)
    {
        string why = string.IsNullOrWhiteSpace(v.Why) ? "a protected block" : v.Why;
        string unlock = v.Unlock ?? DefaultUnlock;
        if (v.Repeat) return $"{what}{Marker} ({why}); already asked: waiting for a trusted player to say '{unlock}', don't ask again";
        return v.Level == ProtectionLevel.Hard
            ? $"{what}{Marker} ({why}); only a trusted player saying '{unlock}' unmarks it"
            : $"{what}{Marker} ({why}); say '{unlock}' if you want it gone";
    }

    /// <summary>Generic refusal when no explanation is available (still names the unlock phrase).</summary>
    public static string Generic(string what) => Refusal(what, new ProtectionVerdict(ProtectionLevel.Soft, "unknown", "a protected block"));
}
