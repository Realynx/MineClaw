namespace MineClaw.Bot;

/// <summary>Game-layer settings; bind from appsettings section "Bot". Arrays so binding replaces the defaults.</summary>
public sealed class BotOptions
{
    /// <summary>Block names the bot never breaks (mining, pathing, dig). Player builds from memory are added on top.</summary>
    public string[] ProtectedBlocks { get; set; } =
        ["chest", "furnace", "lit_furnace", "crafting_table", "bed", "wooden_door", "sign", "wall_sign", "dispenser", "sapling", "farmland"];

    /// <summary>Extra protected boxes, "x1,y1,z1:x2,y2,z2" (inclusive).</summary>
    public string[] ProtectedRegions { get; set; } = [];

    /// <summary>Item names never eaten or deposited automatically.</summary>
    public string[] ReservedItems { get; set; } = [];

    /// <summary>Default count/radius for <c>mine</c> when the model omits them.</summary>
    public int DefaultMineCount { get; set; } = 8;
    public int DefaultMineRadius { get; set; } = 16;

    /// <summary>How often memory-derived protection/reserved lists are refreshed.</summary>
    public int MemoryRefreshSeconds { get; set; } = 10;

    /// <summary>Max seconds <c>ask_stand</c> waits for a player.</summary>
    public int AskStandTimeoutSeconds { get; set; } = 90;

    /// <summary>Radius (blocks) around the teaching player in which block changes are recorded.</summary>
    public double DemoRadius { get; set; } = 6;

    /// <summary>Script engine limits and interrupt policy ("Bot:Scripts").</summary>
    public MineClaw.Scripting.ScriptOptions Scripts { get; set; } = new();
}
