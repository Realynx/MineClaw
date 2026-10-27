namespace MineClaw.Hive;

/// <summary>
/// Supervisor/IPC settings, bound from the top-level "Hive" section (the same section is also bound into
/// <c>AgentOptions.Hive</c>, which decides each process's personality scope). Times are seconds.
/// </summary>
public sealed class HiveConfig
{
    /// <summary>Off = no clones, no supervisor, no hive tools (the TUI still lists the roster greyed out).</summary>
    public bool Enabled { get; set; } = true;
    public int MaxClones { get; set; } = 4;
    public string[] CloneUsernames { get; set; } = [];

    /// <summary>Regular players: one slot stays free for each of them while they're offline. Empty = Agent:Self:TrustedPlayers.</summary>
    public string[] RegularPlayers { get; set; } = [];
    /// <summary>Used when the login packet's max-players is unknown (0).</summary>
    public int MaxPlayersFallback { get; set; } = 7;

    // ---- supervision ----
    /// <summary>Both sides send a heartbeat this often.</summary>
    public double HeartbeatSeconds { get; set; } = 2;
    /// <summary>No message at all for this long = probe the other side (ping/pong, <see cref="PingTimeoutSeconds"/>); only a
    /// failed probe means it's dead (clone: killed and treated as a crash; main: pipe lost). Heartbeats come from a dedicated
    /// thread on each side, so a busy agent loop or db never stalls them; this is the tolerance for everything else.</summary>
    public double HeartbeatTimeoutSeconds { get; set; } = 45;
    /// <summary>How long the ping/pong probe waits for an answer before the other side is declared dead.</summary>
    public double PingTimeoutSeconds { get; set; } = 10;
    /// <summary>A launched clone must connect and say hello within this.</summary>
    public double StartTimeoutSeconds { get; set; } = 30;
    /// <summary>Crashes within <see cref="CrashWindowMinutes"/> before a clone is retired for this session.</summary>
    public int MaxCrashes { get; set; } = 3;
    public double CrashWindowMinutes { get; set; } = 10;
    /// <summary>Restart backoff: base * 2^(crashes-1), capped.</summary>
    public double BackoffBaseSeconds { get; set; } = 2;
    public double BackoffMaxSeconds { get; set; } = 60;
    /// <summary>A clone kicked because someone else logged in with its name (a player's viewer) isn't launched again for this long.</summary>
    public double ExternalLoginCooldownMinutes { get; set; } = 10;
    /// <summary>A clone of ours that just logged out may linger on the tab list this long; it isn't taken for someone else's.</summary>
    public double OwnLogoutGraceSeconds { get; set; } = 20;
    /// <summary>Minimum spacing between clone launches (the server's login throttle rejects joins from one IP &lt; ~4 s apart).</summary>
    public double LaunchGapSeconds { get; set; } = 4.5;

    // ---- logout / inventory ----
    /// <summary>Planned logout: how long the clone keeps trying to empty its inventory before it gives up (items recorded as stranded).</summary>
    public double DepositTimeoutSeconds { get; set; } = 180;
    /// <summary>After a failed task the clone stays online, idle and safe, this long for main to reassign or retry with a fix
    /// (live 10-27 21:53: 420 logged out 4 s after a transient gen stall). 0 = log out at once (old behaviour).</summary>
    public double FailureGraceSeconds { get; set; } = 180;
    /// <summary>Main process gone: the clone deposits only if it can within this, then logs out anyway.</summary>
    public double ParentLossDepositSeconds { get; set; } = 20;
    /// <summary>Main shutting down (SIGTERM): time the clones get to deposit and log out (keep systemd TimeoutStopSec above Host:ShutdownTimeoutSeconds).</summary>
    public double ShutdownRecallSeconds { get; set; } = 45;
    /// <summary>In-game clone death: "respawn" (keep working) or "logout".</summary>
    public string OnCloneDeath { get; set; } = "respawn";

    // ---- streaming / claims / chat ----
    public double SnapshotSeconds { get; set; } = 1;
    public double ClaimTtlSeconds { get; set; } = 60;
    /// <summary>A player who talked to a specific hive bot keeps talking to it for this long (follow-ups without a name).</summary>
    public double StickySeconds { get; set; } = 45;
    /// <summary>A clone keeps a player's unnamed follow-ups only this long after it last heard or answered them (else main).</summary>
    public double CloneThreadSeconds { get; set; } = 30;
    /// <summary>Clone chat name: {0} = main display name, {1} = the clone's number ("maid-chan 67").</summary>
    public string CloneDisplayFormat { get; set; } = "{0} {1}";

    /// <summary>Unix only: directory for the pipe sockets (default: .NET's temp dir). Windows ignores it.</summary>
    public string? PipeDirectory { get; set; }

    public HiveBudgetConfig Budget { get; set; } = new();

    public IReadOnlyList<string> Clones() =>
        CloneUsernames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, MaxClones)).ToList();

    public TimeSpan Heartbeat => TimeSpan.FromSeconds(Math.Clamp(HeartbeatSeconds, 0.2, 30));
    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(Math.Max(HeartbeatTimeoutSeconds, HeartbeatSeconds * 3));
    public TimeSpan PingTimeout => TimeSpan.FromSeconds(Math.Clamp(PingTimeoutSeconds, 0.5, 60));
}

/// <summary>"Hive:Budget": the caps themselves are Agent:Budget (shared by the whole hive); these are the clone sub-caps.</summary>
public sealed class HiveBudgetConfig
{
    /// <summary>Optional per-clone daily sub-cap (USD). 0 or unset = no sub-cap: the clone runs under main's hard caps
    /// (Agent:Budget, shared by the whole hive and enforced by main's ledger).</summary>
    public decimal CloneDailyCapUsd { get; set; } = 0m;
    /// <summary>Optional per-clone monthly sub-cap (USD). 0 or unset = no sub-cap (main's hard caps apply).</summary>
    public decimal CloneMonthlyCapUsd { get; set; } = 0m;
    /// <summary>Hive spend (main + clones) at this fraction of a cap: no new clones and running clones are recalled. Default
    /// 1.0 = only at the hard cap itself (no early throttling); values outside (0, 1] mean 1.</summary>
    public double RecallAtFraction { get; set; } = 1.0;

    /// <summary><see cref="RecallAtFraction"/> clamped: (0, 1], anything else = 1 (the hard cap).</summary>
    public double RecallFraction => RecallAtFraction is > 0 and <= 1 ? RecallAtFraction : 1.0;

    /// <summary>The daily cap a clone process runs with: its sub-cap when set, else main's hard cap.</summary>
    public decimal CloneDailyCap(decimal mainDailyCapUsd) => CloneDailyCapUsd > 0 ? CloneDailyCapUsd : mainDailyCapUsd;

    /// <summary>The monthly cap a clone process runs with: its sub-cap when set, else main's hard cap.</summary>
    public decimal CloneMonthlyCap(decimal mainMonthlyCapUsd) => CloneMonthlyCapUsd > 0 ? CloneMonthlyCapUsd : mainMonthlyCapUsd;
    /// <summary>Spend authorisation is granted in chunks of at least this much (a "batch"); the clone asks again when it runs low.</summary>
    public decimal GrantUsd { get; set; } = 0.10m;
    /// <summary>Per-call estimates used to size a request (USD).</summary>
    public decimal EstimateMainUsd { get; set; } = 0.04m;
    public decimal EstimateWorkerUsd { get; set; } = 0.015m;
    public decimal EstimateCheapUsd { get; set; } = 0.003m;
}
