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
    /// <summary>No message for this long = the other side is dead (clone: killed and treated as a crash; main: pipe lost).</summary>
    public double HeartbeatTimeoutSeconds { get; set; } = 15;
    /// <summary>A launched clone must connect and say hello within this.</summary>
    public double StartTimeoutSeconds { get; set; } = 30;
    /// <summary>Crashes within <see cref="CrashWindowMinutes"/> before a clone is retired for this session.</summary>
    public int MaxCrashes { get; set; } = 3;
    public double CrashWindowMinutes { get; set; } = 10;
    /// <summary>Restart backoff: base * 2^(crashes-1), capped.</summary>
    public double BackoffBaseSeconds { get; set; } = 2;
    public double BackoffMaxSeconds { get; set; } = 60;

    // ---- logout / inventory ----
    /// <summary>Planned logout: how long the clone keeps trying to empty its inventory before it gives up (items recorded as stranded).</summary>
    public double DepositTimeoutSeconds { get; set; } = 180;
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
}

/// <summary>"Hive:Budget": the caps themselves are Agent:Budget (shared by the whole hive); these are the clone sub-caps.</summary>
public sealed class HiveBudgetConfig
{
    /// <summary>Per-clone daily sub-cap (USD).</summary>
    public decimal CloneDailyCapUsd { get; set; } = 0m;
    public decimal CloneMonthlyCapUsd { get; set; } = 30m;
    /// <summary>Hive spend (main + clones) at this fraction of a cap: no new clones and running clones are recalled — before
    /// the main bot itself drops to Economy (Agent:Budget:WorkerBrainFraction).</summary>
    public double RecallAtFraction { get; set; } = 0.5;
    /// <summary>Spend authorisation is granted in chunks of at least this much (a "batch"); the clone asks again when it runs low.</summary>
    public decimal GrantUsd { get; set; } = 0.10m;
    /// <summary>Per-call estimates used to size a request (USD).</summary>
    public decimal EstimateMainUsd { get; set; } = 0.04m;
    public decimal EstimateWorkerUsd { get; set; } = 0.015m;
    public decimal EstimateCheapUsd { get; set; } = 0.003m;
}
