using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>The block-id lookup tables (station kind, farm type, natural nether) answer exactly what the original switches did,
/// for every id.</summary>
public class IndexKindsTableTests
{
    [Fact]
    public void Station_farm_and_nether_tables_match_the_switches_for_every_id()
    {
        for (int id = -16; id < 4200; id++)
        {
            Assert.Equal(StationSwitch(id), IndexKinds.StationKind(id));
            Assert.Equal(FarmSwitch(id), BlockClass.FarmType(id));
            Assert.Equal(NetherSwitch(id), BlockClass.NaturalNether(id));
        }
    }

    private static string? StationSwitch(int blockId) => blockId switch
    {
        Blocks.Chest or Ids1122.TrappedChest => IndexKinds.Chest,
        Ids1122.Hopper => IndexKinds.Hopper,
        Ids1122.Dropper => IndexKinds.Dropper,
        Ids1122.EnderChest => IndexKinds.EnderChest,
        Ids1122.BrewingStand => IndexKinds.BrewingStand,
        Ids1122.EnchantingTable => IndexKinds.EnchantingTable,
        Ids1122.Anvil => IndexKinds.Anvil,
        >= Ids1122.ShulkerFirst and <= Ids1122.ShulkerLast => IndexKinds.ShulkerBox,
        Blocks.Furnace or Blocks.LitFurnace => IndexKinds.Furnace,
        Blocks.Workbench => IndexKinds.CraftingTable,
        Blocks.Dispenser => IndexKinds.Dispenser,
        Blocks.Bed => IndexKinds.Bed,
        Blocks.Jukebox => IndexKinds.Jukebox,
        Blocks.NoteBlock => IndexKinds.NoteBlock,
        Blocks.MobSpawner => IndexKinds.Spawner,
        _ => null,
    };

    private static string? FarmSwitch(int id) => id switch
    {
        Blocks.Crops or Blocks.Farmland => "wheat",
        Blocks.SugarCane => "sugar_cane",
        Blocks.Cactus => "cactus",
        Blocks.MelonStem or Blocks.Melon => "melon",
        Blocks.PumpkinStem or Blocks.Pumpkin => "pumpkin",
        Blocks.BrownMushroom or Blocks.RedMushroom => "mushroom",
        Ids1122.Carrots => "carrot",
        Ids1122.Potatoes => "potato",
        Ids1122.Beetroots => "beetroot",
        Ids1122.NetherWart => "nether_wart",
        _ => null,
    };

    private static bool NetherSwitch(int id) =>
        id is Blocks.Netherrack or Blocks.SoulSand or Blocks.Glowstone or Blocks.Gravel or Blocks.Obsidian or Blocks.BrownMushroom
            or Blocks.RedMushroom or Blocks.Fire or Ids1122.NetherBrick or Ids1122.NetherBrickFence or Ids1122.NetherBrickStairs
            or Ids1122.NetherWart or Ids1122.QuartzOre or Ids1122.Magma or Ids1122.NetherWartBlock;
}
