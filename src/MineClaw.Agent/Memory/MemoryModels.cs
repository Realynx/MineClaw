using System.Text.Json;

namespace MineClaw.Agent.Memory;

public sealed record BlockPos(int X, int Y, int Z)
{
    public override string ToString() => $"{X},{Y},{Z}";
}

public sealed class Waypoint
{
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset Updated { get; set; }
}

public sealed class KeyBlock
{
    public string Label { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
}

/// <summary>Known structure kinds; free text is allowed too.</summary>
public static class StructureKinds
{
    public const string CobbleGenerator = "cobble_generator";
    public const string ChestStorage = "chest_storage";
    public const string FurnaceBank = "furnace_bank";
    public const string Farm = "farm";
    public const string TreeFarm = "tree_farm";
    public const string MobGrinder = "mob_grinder";
    public const string Unknown = "unknown";
}

/// <summary>A player-built (or bot-built) thing the bot knows how to find and use.</summary>
public sealed class Structure
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = StructureKinds.Unknown;
    public BlockPos? Min { get; set; }
    public BlockPos? Max { get; set; }
    public List<KeyBlock> KeyBlocks { get; set; } = new();
    /// <summary>How to use it, in short imperative steps ("stand at 3,65,-2", "mine cobble at 4,65,-2 repeatedly").</summary>
    public List<string> Steps { get; set; } = new();
    public string? TaughtBy { get; set; }
    /// <summary>0..1 — how sure the bot is about what this is / how to use it.</summary>
    public double Confidence { get; set; } = 0.5;
    public string? Note { get; set; }
    public DateTimeOffset Updated { get; set; }
}

public sealed class Note
{
    public int Id { get; set; }
    /// <summary>Who/what the fact is about: a player name, "world", "base", ...</summary>
    public string Subject { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset Created { get; set; }
}

public sealed class RoutineStep
{
    public string Tool { get; set; } = "";
    public JsonElement Args { get; set; }
}

/// <summary>A named, replayable sequence of tool calls (run in C#, zero tokens per iteration).</summary>
public sealed class Routine
{
    public string Name { get; set; } = "";
    public List<RoutineStep> Steps { get; set; } = new();
    /// <summary>Iterations; 0 = until stopped/error (capped by MaxIterations).</summary>
    public int Repeat { get; set; } = 1;
    /// <summary>Stop (successfully) when any step's result text contains this (case-insensitive), e.g. "inventory full".</summary>
    public string? StopWhen { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset Updated { get; set; }
}

/// <summary>The name(s) players call the bot in chat (separate from its login username).</summary>
public sealed class BotIdentity
{
    public string DisplayName { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string? NamedBy { get; set; }
    public DateTimeOffset Updated { get; set; }
}

public sealed class MemoryData
{
    /// <summary>Null until a player renames the bot (config defaults apply until then).</summary>
    public BotIdentity? Identity { get; set; }

    public List<Waypoint> Waypoints { get; set; } = new();
    public List<Structure> Structures { get; set; } = new();
    public List<Note> Notes { get; set; } = new();
    public List<Routine> Routines { get; set; } = new();
    public int NextNoteId { get; set; } = 1;
}
