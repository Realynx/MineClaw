namespace MineClaw.Agent.Self;

/// <summary>One hive clone ("Hive:Clones[n]"): its login username and optional frozen seed quirks.</summary>
public sealed class HiveClone
{
    public string Username { get; set; } = "";
    /// <summary>Seed quirks, ';'-separated (e.g. "chaotic gremlin energy; narrates own plays"). Empty = generated once.</summary>
    public string? Seed { get; set; }
    /// <summary>Same as <see cref="Seed"/> as a list (either works).</summary>
    public string[] Seeds { get; set; } = [];

    public IReadOnlyList<string> SeedList() =>
        Seeds.Length > 0 ? Seeds : (Seed ?? "").Split([';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>
/// The hive ("Hive" section; the host binds it into <see cref="AgentOptions.Hive"/>): up to 4 clones, each its own process,
/// all sharing one SQLite db. Personality is scoped: <c>hive</c> (shared profile) | clone username (its shard) |
/// <c>main</c> (the primary bot).
/// </summary>
public sealed class HiveOptions
{
    public int MaxClones { get; set; } = 4;
    /// <summary>Plain username list (legacy shape of the section); merged with <see cref="Clones"/>.</summary>
    public string[] CloneUsernames { get; set; } = [];
    public List<HiveClone> Clones { get; set; } = [];
    /// <summary>The main bot's login (so clones treat its chat as a bot's, not a player's).</summary>
    public string? MainUsername { get; set; }

    public IReadOnlyList<HiveClone> AllClones()
    {
        var list = Clones.Where(c => !string.IsNullOrWhiteSpace(c.Username)).ToList();
        foreach (var u in CloneUsernames)
            if (!string.IsNullOrWhiteSpace(u) && !list.Any(c => c.Username.Equals(u, StringComparison.OrdinalIgnoreCase)))
                list.Add(new HiveClone { Username = u });
        return list.Take(Math.Max(0, MaxClones)).ToList();
    }

    public HiveClone? CloneFor(string? username) =>
        username == null ? null : AllClones().FirstOrDefault(c => c.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>The personality scope of a process logged in as <paramref name="username"/>.</summary>
    public string ScopeFor(string? username) => CloneFor(username) is { } c ? c.Username.ToLowerInvariant() : SelfSchema.MainScope;

    public IReadOnlyList<string> CloneScopes() => AllClones().Select(c => c.Username.ToLowerInvariant()).ToList();

    public bool IsBot(string? player) =>
        player != null && (CloneFor(player) != null || (MainUsername != null && player.Equals(MainUsername, StringComparison.OrdinalIgnoreCase)));
}
