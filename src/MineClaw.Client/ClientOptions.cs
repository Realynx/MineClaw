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

    /// <summary>Time allowed from TCP connect to the first server position (login + spawn).</summary>
    public TimeSpan LoginTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Client tick length. 50 ms = vanilla 20 TPS (tests may shorten it).</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Minimum gap between chat lines sent (server spam protection).</summary>
    public TimeSpan ChatInterval { get; init; } = TimeSpan.FromMilliseconds(1200);

    /// <summary>Reach used for digging/placing/using (vanilla survival ≈ 4.5).</summary>
    public double Reach { get; init; } = 4.25;

    /// <summary>Health (half-hearts) at or below which <see cref="DangerKind.LowHealth"/> fires.</summary>
    public int LowHealthThreshold { get; init; } = 8;
    /// <summary>Cancel the running action when health drops to <see cref="LowHealthThreshold"/> or the player starts falling into the void.</summary>
    public bool AbortActionsOnDanger { get; init; } = true;
    /// <summary>Distance at which a hostile mob triggers <see cref="DangerKind.HostileNearby"/>.</summary>
    public double HostileAlertRadius { get; init; } = 8;

    /// <summary>Sneak automatically whenever idle on a block next to the void.</summary>
    public bool SneakAtEdgesWhenIdle { get; init; } = true;

    /// <summary>Blocks the bot must never break (player builds, generator walls...). Evaluated for (position, block id).
    /// May be replaced at runtime via <see cref="MinecraftClient.IsProtected"/>.</summary>
    public Func<BlockPos, int, bool>? IsProtected { get; init; }

    /// <summary>Item ids the bot must not eat or deposit automatically (needed for tasks).</summary>
    public IReadOnlySet<int>? ReservedItems { get; init; }

    /// <summary>Diagnostic log sink (one line per call). Null = silent.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Opens the connection. Default: TCP to <see cref="Host"/>:<see cref="Port"/>. Tests inject in-memory streams.</summary>
    public Func<CancellationToken, PacketConnection>? ConnectionFactory { get; init; }
}
