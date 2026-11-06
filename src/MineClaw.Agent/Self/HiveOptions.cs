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
/// The hive ("Hive" section; the host binds it into <see cref="AgentOptions.Hive"/>): clones allocated per spawn, each its own
/// process, all sharing ONE memory (one SQLite db, one self profile: <see cref="SharedSelf"/>). The old per-clone shard scopes
/// (<c>hive</c> | clone username | <c>main</c>) remain only behind <c>SharedSelf=false</c>.
/// </summary>
public sealed class HiveOptions
{
    /// <summary>Trims only the legacy configured roster (<see cref="AllClones"/>); 0 (default) = no trim. Spawns are capped by
    /// <c>HiveConfig.MaxClones</c> (also 0 = none), the player cap and the memory ceiling, never by this.</summary>
    public int MaxClones { get; set; }
    /// <summary>Plain username list (legacy shape of the section); merged with <see cref="Clones"/>.</summary>
    public string[] CloneUsernames { get; set; } = [];
    public List<HiveClone> Clones { get; set; } = [];
    /// <summary>The main bot's login (so clones treat its chat as a bot's, not a player's).</summary>
    public string? MainUsername { get; set; }

    /// <summary>
    /// One memory for every hive (default): every hive account uses main's self profile ("main" scope), no per-clone personality
    /// shard. False = the old per-clone shards (frozen seeds + own growth), kept for the shard tests and old dbs.
    /// </summary>
    public bool SharedSelf { get; set; } = true;

    private volatile string[] _dynamic = [];
    private readonly object _dynLock = new();

    /// <summary>A clone account main allocated at runtime (no fixed roster), or this process's own clone login.</summary>
    public void Register(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        lock (_dynLock)
            if (!_dynamic.Contains(username.Trim(), StringComparer.OrdinalIgnoreCase)) _dynamic = [.. _dynamic, username.Trim()];
    }

    /// <summary>Drops the configured fixed roster (dynamic accounts). Returns the names it had.</summary>
    public IReadOnlyList<string> IgnoreLegacyRoster()
    {
        var names = CloneUsernames.Concat(Clones.Select(c => c.Username)).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        CloneUsernames = [];
        Clones = [];
        return names;
    }

    public IReadOnlyList<HiveClone> AllClones()
    {
        var list = Clones.Where(c => !string.IsNullOrWhiteSpace(c.Username)).ToList();
        foreach (var u in CloneUsernames)
            if (!string.IsNullOrWhiteSpace(u) && !list.Any(c => c.Username.Equals(u, StringComparison.OrdinalIgnoreCase)))
                list.Add(new HiveClone { Username = u });
        var configured = list.Take(MaxClones > 0 ? MaxClones : int.MaxValue).ToList();
        foreach (var u in _dynamic)
            if (!configured.Any(c => c.Username.Equals(u, StringComparison.OrdinalIgnoreCase))) configured.Add(new HiveClone { Username = u });
        return configured;
    }

    public HiveClone? CloneFor(string? username) =>
        username == null ? null : AllClones().FirstOrDefault(c => c.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    /// <summary>The personality scope of a process logged in as <paramref name="username"/>.</summary>
    public string ScopeFor(string? username) => !SharedSelf && CloneFor(username) is { } c ? c.Username.ToLowerInvariant() : SelfSchema.MainScope;

    public IReadOnlyList<string> CloneScopes() => AllClones().Select(c => c.Username.ToLowerInvariant()).ToList();

    public bool IsBot(string? player) =>
        player != null && (CloneFor(player) != null || (MainUsername != null && player.Equals(MainUsername, StringComparison.OrdinalIgnoreCase)));
}
