using MineClaw.Data;

namespace MineClaw.Bot;

/// <summary>
/// Block ids above b1.8.1's range (1.12.2, multi-version P6) that the Bot's own classifiers name: protection, the world
/// index, chest storage, vision roles. A b1.8.1 world never holds these ids (its blocks stop at 109), so every check here is a
/// no-op there and b1.8.1 behaviour is unchanged. Wood (161/162) lives in <see cref="MineClaw.Client.Trees.Wood"/>.
/// </summary>
internal static class Ids1122
{
    public const int
        Leaves2 = MineClaw.Client.Trees.Wood.Leaves2, Log2 = MineClaw.Client.Trees.Wood.Log2,
        NetherBrick = 112, NetherBrickFence = 113, NetherBrickStairs = 114, NetherWart = 115,
        EnchantingTable = 116, BrewingStand = 117, EnderChest = 130, Carrots = 141, Potatoes = 142, Anvil = 145,
        TrappedChest = 146, QuartzOre = 153, Hopper = 154, Dropper = 158, CoalBlock = 173, DoublePlant = 175,
        Magma = 213, NetherWartBlock = 214, ShulkerFirst = 219, ShulkerLast = 234, Beetroots = 207;

    /// <summary>A chest that pairs and stores (plain or trapped).</summary>
    public static bool IsChest(int id) => id is Blocks.Chest or TrappedChest;

    public static bool IsShulkerBox(int id) => id is >= ShulkerFirst and <= ShulkerLast;

    /// <summary>Crops that ripen by metadata: (ripe stage). Wheat/carrots/potatoes 7, beetroot and nether wart 3.</summary>
    public static int? RipeStage(int id) => id switch
    {
        Blocks.Crops or Carrots or Potatoes => 7,
        Beetroots or NetherWart => 3,
        _ => null,
    };
}
