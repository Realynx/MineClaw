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
    /// <summary>harvest_generator with every dig cell leased to other bots: wait this long for one to free up (0 = fail at once).</summary>
    public int GeneratorQueueSeconds { get; set; } = 60;

    /// <summary>Radius (blocks) around the teaching player in which block changes are recorded.</summary>
    public double DemoRadius { get; set; } = 6;

    /// <summary>Protection policy knobs ("Bot:Protection"): permits, confirmations, op mode.</summary>
    public ProtectionOptions Protection { get; set; } = new();

    /// <summary>Script engine limits and interrupt policy ("Bot:Scripts").</summary>
    public MineClaw.Scripting.ScriptOptions Scripts { get; set; } = new();

    /// <summary>The <c>see</c> tool: picture budget, textures (client jar path), eye-view size ("Bot:Vision").</summary>
    public GameTools.VisionOptions Vision { get; set; } = new();

    /// <summary>Build from a description: design loop, review, construction ("Bot:Blueprints").</summary>
    public Blueprints.BlueprintOptions Blueprints { get; set; } = new();
}

/// <summary>"Bot:Protection" (ARCHITECTURE.md "Protection policy").</summary>
public sealed class ProtectionOptions
{
    /// <summary>The bot account is op: spawn protection doesn't apply, so a remembered spawn-protection region is dropped at
    /// startup and never adopted from memory; only real server reverts this session re-learn it.</summary>
    public bool ServerOp { get; set; } = true;
    /// <summary>The server has no spawn protection (server.properties spawn-protection=0): any remembered spawn-protection region
    /// is dropped at startup for EVERY account, clones included (a learned fact otherwise binds non-op clones until each one
    /// edits inside it, which they otherwise refuse to try).</summary>
    public bool SpawnProtectionOff { get; set; }
    /// <summary>A trusted permit covering more blocks than this asks for a one-line confirmation first.</summary>
    public int ConfirmAboveBlocks { get; set; } = 32;
    /// <summary>How long a trusted "you can break it" permit lasts.</summary>
    public int PermitMinutes { get; set; } = 30;
    /// <summary>The same refusal again within this window says "already asked" instead of asking again.</summary>
    public int RepeatRefusalMinutes { get; set; } = 10;
    /// <summary>"it/that" after a refusal refers to the refused block for this long.</summary>
    public int RefusalContextMinutes { get; set; } = 10;
}
