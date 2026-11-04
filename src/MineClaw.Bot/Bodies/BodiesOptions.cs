namespace MineClaw.Bot.Bodies;

/// <summary>
/// The "Bodies" config section (one brain, many bodies, docs/design/one-brain-many-bodies.md). Step 2: <see cref="Mode"/>
/// <c>inproc</c> runs the clone accounts as bodies in main's process; the default <c>hive</c> keeps one process per clone.
/// Times are seconds unless named otherwise. Unset values fall back to the matching <c>Hive:</c> settings.
/// </summary>
public sealed class BodiesOptions
{
    public const string Hive = "hive";
    public const string InProc = "inproc";

    /// <summary><c>hive</c> (clone processes, default) or <c>inproc</c> (clone bodies in this process).</summary>
    public string Mode { get; set; } = Hive;

    /// <summary>The clone accounts (was <c>Hive:CloneUsernames</c>, which is used when this is empty).</summary>
    public string[] Accounts { get; set; } = [];

    /// <summary>Regular players: one slot stays free for each of them while they're offline. Empty = the hive's / trusted players.</summary>
    public string[] RegularPlayers { get; set; } = [];

    /// <summary>Server max-players when the login packet's is unknown (0).</summary>
    public int MaxPlayersFallback { get; set; } = 7;

    // ---- the runner (ported from the hive supervisor's CloneSlot rules)
    /// <summary>Crashes within <see cref="CrashWindowMinutes"/> before a body is retired for this session.</summary>
    public int MaxCrashes { get; set; } = 3;
    public double CrashWindowMinutes { get; set; } = 10;
    /// <summary>Restart backoff after a crash: base * 2^(crashes-1), capped.</summary>
    public double BackoffBaseSeconds { get; set; } = 2;
    public double BackoffMaxSeconds { get; set; } = 60;
    /// <summary>Reconnect backoff after a lost connection (kick, network): first wait, doubled up to <see cref="BackoffMaxSeconds"/>.</summary>
    public double ReconnectSeconds { get; set; } = 5;

    // ---- the registry
    /// <summary>Spacing between login batches (b1.8.1's server throttles joins from one IP less than ~4 s apart).</summary>
    public double LaunchGapSeconds { get; set; } = 4.5;
    /// <summary>Logins at once (<see cref="JoinPacer"/>): 10 on 1.12.2 (no throttle); the b1.8.1 profile forces 1.</summary>
    public int LaunchBatch { get; set; } = 10;
    /// <summary>A body whose account someone else logged in to (a player's viewer) isn't spawned again for this long.</summary>
    public double ExternalLoginCooldownMinutes { get; set; } = 10;
    /// <summary>A body of ours that just logged out may linger on the tab list this long; it isn't taken for someone else's.</summary>
    public double OwnLogoutGraceSeconds { get; set; } = 20;
    /// <summary>How often the slot policy checks whether a body must log out to make room for an offline regular.</summary>
    public double YieldCheckSeconds { get; set; } = 10;
    /// <summary>Despawn: how long the body keeps trying to empty its inventory before it gives up (items recorded as stranded).</summary>
    public double DepositTimeoutSeconds { get; set; } = 180;
    /// <summary>Main shutting down: time the clones get to deposit and log out, in parallel.</summary>
    public double ShutdownRecallSeconds { get; set; } = 45;
    /// <summary>A body held still over the void this long with nobody /tp'ing it logs out (the spot is saved).</summary>
    public double VoidHoldMinutes { get; set; } = 10;
    /// <summary>How long a spawn waits for the body to be in game before its job starts anyway (the job then waits on its own).</summary>
    public double JoinTimeoutSeconds { get; set; } = 60;

    // ---- process
    /// <summary>Concurrent A* searches across all bodies (MineClaw.Client.PathfindGate): half the cores, 2..4 (the 4-core host: 2).</summary>
    public int MaxPathfinds { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
    /// <summary>How often each body's "tick behind" count is checked and logged when it grew (0 = never).</summary>
    public double TickReportSeconds { get; set; } = 60;

    public bool IsInProc => string.Equals(Mode?.Trim(), InProc, StringComparison.OrdinalIgnoreCase);

    /// <summary>The configured accounts, else <paramref name="fallback"/> (the hive's clone names), trimmed and distinct.</summary>
    public IReadOnlyList<string> AccountsOr(IEnumerable<string> fallback) =>
        (Accounts.Any(a => !string.IsNullOrWhiteSpace(a)) ? Accounts : fallback)
        .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
