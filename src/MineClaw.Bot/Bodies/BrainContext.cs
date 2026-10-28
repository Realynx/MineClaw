using MineClaw.Agent;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.World.Claims;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// What <see cref="BotHost.CreateBrain"/> built (one brain, many bodies): the runtime, the shared stores and the tools
/// (registered against <see cref="Tools"/> / <see cref="Adapter"/>, the body routers), plus what <see cref="BodyFactory"/>
/// needs to wire one body. The main body's own perception (indexer, world memory, mechanisms, gestures) stays here too:
/// in step 1 the brain sees through main.
/// </summary>
public sealed class BrainContext
{
    public required AgentRuntime Runtime { get; init; }
    public required AgentOptions AgentOptions { get; init; }
    public required BotOptions BotOptions { get; init; }
    public required BodyRegistry Bodies { get; init; }
    /// <summary>The router the game tools act through (each body's claim-aware <see cref="IBody.Game"/>).</summary>
    public required BodyRoutingGameClient Tools { get; init; }
    /// <summary>The router over each body's plain adapter (skills, ask_stand, act, blueprints' site reads).</summary>
    public required BodyRoutingGameClient Adapter { get; init; }

    public required Protection Protection { get; init; }
    public required MemoryStoreAdapter Memory { get; init; }
    public required DemonstrationRecorder Recorder { get; init; }
    public required Perception.DigCells DigCells { get; init; }
    public required Perception.GestureTracker Gestures { get; init; }
    public required AcquisitionSources Acquisition { get; init; }
    public required SkillHub Skills { get; init; }
    public required Func<double, double, double, string?> Landmark { get; init; }
    public required Func<MineClaw.World.BlockPos, IReadOnlyList<MineClaw.World.BlockPos>> GenCells { get; init; }
    /// <summary>A hive bot's (or a known bot's) name: never a person.</summary>
    public required Func<string, bool> IsBot { get; init; }

    public WorldIndexer? Indexer { get; init; }
    public PlayerActivityTracker? Activity { get; init; }
    public WorldMemory? WorldMemory { get; init; }
    public MechanismWatcher? Mechanisms { get; init; }
    public Blueprints.BlueprintWiring? Blueprints { get; init; }
    public PlacedBlockStore? PlacedBlocks { get; init; }
    public ProtectionMarks? ProtectionMarks { get; init; }
    public ProtectionCommands? ProtectionCommands { get; init; }
    public ProtectionTelemetry? ProtectionTelemetry { get; init; }
    public System.Threading.Timer? ProtectionSync { get; init; }
    public IClaimBoard? Claims { get; init; }
    public Func<IEnumerable<string>>? HiveBots { get; init; }

    public ILoggerFactory? Loggers { get; init; }
    public ILogger? Log { get; init; }
    public TimeProvider? Time { get; init; }
    public bool Persist { get; init; }

    /// <summary>
    /// One brain, many bodies, step 3: the brain's view of its bodies (body routing, focus, [bodies], addressing), set by
    /// <see cref="BodyDirectory.Enable"/> in in-process mode; null = one body (hive mode), every body path as before.
    /// </summary>
    public BodyDirectory? Directory { get; internal set; }

    /// <summary>Vision v2 (set by <see cref="BotHost.FinishTools"/>).</summary>
    public Perception.Vision.VisionWiring.Result? Vision { get; internal set; }
    /// <summary>see + do are registered (after every body's macro and the script tool): bodies attached later skip it.</summary>
    public bool ToolsFinished { get; internal set; }

    /// <summary>The main body (always online, the only chat sender).</summary>
    public Body Main => (Body)Bodies.Main;
}
