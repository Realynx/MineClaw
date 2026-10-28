using MineClaw.Agent.Human;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Scripting;

namespace MineClaw.Bot.Bodies;

/// <summary>Where a body is in its life (docs/design/one-brain-many-bodies.md section 1).</summary>
public enum BodyState
{
    Offline,
    Connecting,
    Online,
    /// <summary>Logging out to make room on the server (slot yield).</summary>
    Yielding,
    /// <summary>Someone else is logged in on this account (a player's viewer); left alone for a cooldown.</summary>
    Occupied,
    /// <summary>Crashed too often: never restarted this session.</summary>
    Retired,
}

/// <summary>
/// One body of the one brain: a Minecraft connection plus the per-body behaviour wired by <see cref="BodyFactory"/>. Shared
/// stores (memory, claims, protection, dig cells, placed blocks) live in the brain, never here. Step 1: the registry holds
/// only the main body.
/// </summary>
public interface IBody
{
    // ---- identity
    /// <summary>Login name ("maid_chan", "maid_chan_67").</summary>
    string Name { get; }
    /// <summary>What players call it: the clone number ("67"), "main" for the main body.</summary>
    string Short { get; }
    /// <summary>The main body: default focus, the only chat sender, never yields its slot.</summary>
    bool IsMain { get; }

    // ---- connection
    /// <summary>The connection (null for an offline test fake).</summary>
    MinecraftClient? Client { get; }
    /// <summary>The plain bot-layer adapter over <see cref="Client"/> (or the fake): what skills, gestures and the bridge use.</summary>
    IGameClient Adapter { get; }
    /// <summary>What game tools act on: <see cref="Adapter"/>, behind a <see cref="ClaimAwareGameClient"/> when a claim board is on.</summary>
    IGameClient Game { get; }

    // ---- jobs
    /// <summary>This body's one activity slot (main: the runtime's <c>AgentRuntime.Activities</c>).</summary>
    ActivityManager Activities { get; }
    /// <summary>Standing orders (already keyed by bot name).</summary>
    StandingOrders? Orders { get; }
    /// <summary>The body's script lane.</summary>
    ScriptController? Scripts { get; }

    // ---- per-body behaviour (null until attached)
    GameBridge? Bridge { get; }
    Brainstem.BrainstemController? Brainstem { get; }
    Brainstem.Gestures? Gestures { get; }
    Brainstem.StallWatchdog? Watchdog { get; }
    Brainstem.HandoverWatch? Handovers { get; }
    HungerLatch? Hunger { get; }

    // ---- status
    BodyState State { get; }
    /// <summary>How far its current job is, 0..1 (the slot yield picks the body furthest from finishing).</summary>
    double Progress { get; }
}
