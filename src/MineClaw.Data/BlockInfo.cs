namespace MineClaw.Data;

/// <summary>Tool categories used for harvest/speed rules.</summary>
public enum ToolType { None, Pickaxe, Axe, Shovel, Sword, Shears, Hoe }

/// <summary>Static properties of a Beta 1.8.1 block id.</summary>
/// <param name="Hardness">Vanilla hardness; -1 = unbreakable (bedrock).</param>
/// <param name="Solid">Has a collision box the player is blocked by / can stand on.</param>
/// <param name="CollisionHeight">Top of the collision box in blocks (1.0 full, 0.5 slab, 1.5 fence, 0.9375 farmland, 0 if not solid).</param>
/// <param name="Transparent">Light/vision passes through (glass, leaves, torches, air...).</param>
/// <param name="Interactable">Right-clicking does something (chest, furnace, workbench, door, lever, button, bed, dispenser, note block, trapdoor, jukebox...).</param>
/// <param name="BestTool">Tool that mines it fastest.</param>
/// <param name="MinHarvestLevel">Tier needed for drops: -1 none needed, 0 wood, 1 stone, 2 iron, 3 diamond (of <see cref="BestTool"/>).</param>
/// <param name="Hazard">Damages the player (lava, fire, cactus).</param>
public sealed record BlockInfo(
    int Id,
    string Name,
    string DisplayName,
    float Hardness,
    bool Solid,
    double CollisionHeight,
    bool Liquid,
    bool Climbable,
    bool Transparent,
    bool Interactable,
    ToolType BestTool,
    int MinHarvestLevel,
    bool Hazard);

/// <summary>Static properties of an item id (blocks-as-items included, ids 0-255).</summary>
/// <param name="MaxDamage">Durability for tools/armor, 0 otherwise.</param>
/// <param name="ToolTier">0 wood, 1 stone, 2 iron, 3 diamond, 4 gold (gold mines like wood tier but fastest); -1 if not a tool.</param>
public sealed record ItemInfo(
    int Id,
    string Name,
    string DisplayName,
    int MaxStack,
    int MaxDamage,
    ToolType Tool,
    int ToolTier,
    bool IsBlock);
