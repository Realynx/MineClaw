using MineClaw.Client.Survival;
using MineClaw.Client.Combat;
using MineClaw.Protocol;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>Configuration for <see cref="MinecraftClient"/>.</summary>
public sealed record ClientOptions
{
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = ProtocolInfo.DefaultPort;
    /// <summary>Offline-mode username (max 16 chars).</summary>
    public string Username { get; init; } = "MineClaw";

    /// <summary>Send 0x09 respawn automatically after death (after <see cref="RespawnDelay"/>).</summary>
    public bool AutoRespawn { get; init; } = true;
    public TimeSpan RespawnDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Reconnect with exponential backoff when the connection drops (not after <see cref="MinecraftClient.DisconnectAsync"/>).</summary>
    public bool AutoReconnect { get; init; }
    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>First reconnect delay after a "logged in from another location" kick: someone else is using this account
    /// (e.g. a dev test), so don't instantly reconnect and kick them back.</summary>
    public TimeSpan DuplicateLoginBackoff { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Time allowed from TCP connect to the first server position (login + spawn).</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Client tick length. 50 ms = vanilla 20 TPS (tests may shorten it).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Minimum gap between chat lines sent (server spam protection).</summary>
    public TimeSpan ChatInterval { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Reach used for digging/placing/using (vanilla survival ≈ 4.5).</summary>
    public double Reach { get; init; } = 4.25;

    /// <summary>Survival reflexes: auto-eat, low-health retreat and action gating, out-of-food events (see
    /// <see cref="SurvivalOptions"/>; its LowHealth replaces the old LowHealthThreshold).</summary>
    public SurvivalOptions Survival { get; init; } = new();
    /// <summary>Cancel the running action on danger (void fall, lava; at low health only risky actions, at critical health any).</summary>
    public bool AbortActionsOnDanger { get; init; } = true;
    /// <summary>Distance at which a hostile mob triggers <see cref="DangerKind.HostileNearby"/>.</summary>
    public double HostileAlertRadius { get; init; } = 8;

    /// <summary>Sneak automatically whenever idle on a block next to the void.</summary>
    public bool SneakAtEdgesWhenIdle { get; init; } = true;

    /// <summary>Void protection applied to every tick of movement, whatever action runs (see <see cref="MineClaw.World.EdgeGuard"/>).</summary>
    public EdgeGuardOptions EdgeGuard { get; init; } = new();
    /// <summary>When falling with nothing below, place any block from the inventory under/beside the bot.</summary>
    public bool ClutchEnabled { get; init; } = true;
    /// <summary>Hostile mobs within this distance while the bot is near an edge make it step toward the island interior (0 = off).</summary>
    public double EdgeRetreatHostileRadius { get; init; } = 4;
    /// <summary>Freeze movement after a teleport/respawn until the chunks below are loaded and there is ground under the bot.</summary>
    public bool FreezeUntilSupportVerified { get; init; } = true;
    /// <summary>Config switch: gap jumps over the void (a miss = death) are planned for every goto. Off by default: then only a
    /// goto with <see cref="ParkourMode.Void"/> asked for by a trusted player within <see cref="VoidParkourRequestWindow"/>
    /// may plan them. Either way the bot needs a clutch block in its hotbar and the jump must pass the strict margin.</summary>
    public bool AllowVoidParkour { get; init; }
    /// <summary>Players whose recent chat authorizes a <see cref="ParkourMode.Void"/> goto.</summary>
    public IReadOnlyList<string> ParkourTrustedPlayers { get; init; } = ["poofimafox", "kiwi"];
    /// <summary>How recent a trusted player's chat must be for a void-parkour goto.</summary>
    public TimeSpan VoidParkourRequestWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Combat engine: reflex defense, guarding, sparring, telemetry (see <see cref="CombatOptions"/>).</summary>
    public CombatOptions Combat { get; init; } = new();

    /// <summary>Blocks the bot must never break (player builds, generator walls...). Evaluated for (position, block id).
    /// May be replaced at runtime via <see cref="MinecraftClient.IsProtected"/>.</summary>
    public Func<BlockPos, int, bool>? IsProtected { get; init; }

    /// <summary>Item ids the bot must not eat or deposit automatically (needed for tasks).</summary>
    public IReadOnlySet<int>? ReservedItems { get; init; }

    /// <summary>Diagnostic log sink (one line per call). Null = silent.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Debug-level sink (safety interventions, knockback). Null = silent.</summary>
    public Action<string>? DebugLog { get; init; }

    /// <summary>Packet recorder: when set, every connection records both directions to
    /// <c>&lt;dir&gt;/yyyyMMdd-HHmmss.mcrec</c> (see <see cref="MineClaw.Protocol.Recording.RecordingFormat"/>). Null = off,
    /// unless the environment variable <c>MINECLAW_RECORD</c> is <c>1</c> (→ <c>logs/recordings</c>) or a directory.</summary>
    public string? RecordDirectory { get; init; }
    /// <summary>How long a learned protected region (server undid my edits near spawn) is skipped up front before one
    /// probe edit there is allowed again.</summary>
    public TimeSpan ProtectedRegionRetry { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Gzip the recording body (chunks are already zlib'd; the rest shrinks ~5x).</summary>
    public bool RecordGzip { get; init; } = true;

    /// <summary>Opens the connection. Default: <see cref="Profile"/>'s protocol adapter, TCP to <see cref="Host"/>:<see cref="Port"/>.
    /// Tests inject in-memory streams.</summary>
    public Func<CancellationToken, MineClaw.Protocol.Versioning.IGameConnection>? ConnectionFactory { get; init; }

    /// <summary>The game version this client speaks (docs/design/multi-version.md): resolved once per process by the App's
    /// composition root (config or server ping) and shared by every client in it. Default b1.8.1.</summary>
    public Versioning.GameVersionProfile Profile { get; init; } = Versioning.GameVersionProfiles.Beta181;
}
