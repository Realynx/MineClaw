using System.Runtime.CompilerServices;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Client.Trees;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>How far the planner may go for a need: <see cref="Cheap"/> = inventory, chests, hive, crafting from what's
/// reachable; <see cref="Full"/> adds the from-scratch bootstrap (tree → planks → sticks → wood tool → gen cobble → stone
/// tool) and, last, asking a player.</summary>
public enum AcquireDepth { Cheap, Full }

/// <summary>
/// One requirement of a job: a tool kind of <see cref="Loadout.Kinds"/> ("pickaxe", "axe", "shovel", "sword", "food") at
/// a minimum harvest level (0 wood, 1 stone, 2 iron, 3 diamond), or a plain item ("cobblestone", "bucket", "torch") × Count.
/// </summary>
public sealed record Need(string What, int Count = 1, int MinTier = 0, AcquireDepth Depth = AcquireDepth.Full)
{
    public override string ToString() => Count > 1 ? $"{Count} {What}" : What;
}

/// <summary>A hive handoff request: any of <paramref name="ItemIds"/> (best first) × <paramref name="Count"/>.</summary>
public sealed record HandoffAsk(string Need, IReadOnlyList<int> ItemIds, int Count);

/// <summary>Bounds of one acquisition (all steps and nested needs share them).</summary>
public sealed record AcquisitionLimits
{
    /// <summary>Game actions (withdraw, craft, chop, gen batch, handoff, ask) per acquisition.</summary>
    public int MaxSteps { get; init; } = 14;
    public TimeSpan MaxTime { get; init; } = TimeSpan.FromMinutes(4);
    /// <summary>How long a player question waits; no answer = the default (give up honestly).</summary>
    public TimeSpan AskTimeout { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// What the acquisition planner can draw on besides the game client. Every member is optional: a missing source is
/// skipped. Hosts attach one per bot (<see cref="Acquisition.Attach"/>); other layers fill their seams later (the hive sets
/// <see cref="RequestHandoff"/>).
/// </summary>
public sealed class AcquisitionSources
{
    /// <summary>WorldIndexer container snapshots (chests with tools, logs, cobble, saplings).</summary>
    public ObservationStore? Observations { get; set; }

    /// <summary>
    /// Seam (c), owned by the hive: ask main / another clone holding a spare of one of the items to hand it over. Returns the
    /// giver's name once the items were handed over (tossed at this bot's feet or already in its inventory), else null
    /// (nobody has a spare, no hive, refused, timed out). The planner collects drops and verifies the inventory itself.
    /// </summary>
    public Func<HandoffAsk, CancellationToken, Task<string?>>? RequestHandoff { get; set; }

    /// <summary>Seam (f): says the question to the players and waits up to the timeout for a reply (null = no answer/muted).</summary>
    public Func<string, TimeSpan, CancellationToken, Task<string?>>? AskPlayer { get; set; }

    /// <summary>Known generator dig cells (taught, regen-confirmed, geometry); null = a geometry scan around the bot.</summary>
    public Func<IReadOnlyList<BlockPos>>? GeneratorCells { get; set; }

    /// <summary>All dig cells of the generator around a cell (claim-aware cell choice, <see cref="GameToolSet.PickGeneratorCell"/>).</summary>
    public Func<BlockPos, IReadOnlyList<BlockPos>>? CellsNear { get; set; }

    /// <summary>The one-line plan of a non-trivial acquisition ("no pick, making one: logs→planks→..."): log/TUI.</summary>
    public Action<string>? Announce { get; set; }

    /// <summary>Tree stewardship: never fell a tree that would leave fewer than this many (planted saplings count).</summary>
    public int MinTrees { get; set; } = TreeStewardship.DefaultMinTrees;

    public AcquisitionLimits Limits { get; set; } = new();
}

/// <summary>Per-bot acquisition sources, looked up by game client (a claim-aware wrapper finds its inner client's).</summary>
public static class Acquisition
{
    private static readonly ConditionalWeakTable<IGameClient, AcquisitionSources> Table = new();

    public static AcquisitionSources Attach(IGameClient client, AcquisitionSources sources)
    {
        Table.AddOrUpdate(client, sources);
        return sources;
    }

    public static AcquisitionSources? Of(IGameClient client) =>
        Table.TryGetValue(client, out var s) ? s
        : client is ClaimAwareGameClient w && Table.TryGetValue(w.Inner, out var inner) ? inner : null;

    /// <summary>A planner over the client's attached sources (or bare ones with <paramref name="observations"/>).</summary>
    public static AcquisitionPlanner Planner(IGameClient client, ObservationStore? observations = null)
    {
        var s = Of(client);
        if (s == null) s = new AcquisitionSources { Observations = observations };
        else if (s.Observations == null && observations != null) s.Observations = observations;
        return new AcquisitionPlanner(client, s);
    }
}
