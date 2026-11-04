using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// <see cref="MinecraftClient.PlaceTurnedAsync"/> against the fake server's vanilla placement rules (<c>OrientBlocks</c>): a furnace
/// facing east, stairs rising each way, a wall torch, a log on its side and a top slab (1.12.2), a piston pointing up (walking to a
/// stand over it). The world ends with exactly the wanted metas, both versions.
/// </summary>
public class TurnedPlacementTests
{
    /// <summary>The stone platform (6..10 × 6..10 at y63), a cobble post at 5,64,8 (a log's side support) and at 11,64,8 (a torch's
    /// wall), and the items in the hotbar.</summary>
    private static void World(FakeServer s)
    {
        s.OrientBlocks = true;
        s.Blocks[(5, 64, 8)] = Blocks.Cobblestone;
        s.Blocks[(11, 64, 8)] = Blocks.Cobblestone;
        s.Inventory[36] = Harness.Stack(Blocks.Furnace, 4);
        s.Inventory[37] = Harness.Stack(Blocks.WoodenStairs, 8);
        s.Inventory[38] = Harness.Stack(Blocks.Torch, 4);
        s.Inventory[39] = Harness.Stack(Blocks.Log, 4, 1);
        s.Inventory[40] = Harness.Stack(Blocks.Piston, 2);
        if (s.Modern) s.Inventory[41] = Harness.Stack(126, 4, 0); // 1.12.2 oak slab
    }

    private static async Task Place(MinecraftClient c, FakeServer s, int x, int y, int z, int item, int meta, int? damage = null)
    {
        var r = await c.PlaceTurnedAsync(new BlockPos(x, y, z), item, damage, meta);
        Assert.True(r.Ok, r.Message);
        s.Metas.TryGetValue((x, y, z), out int got);
        Assert.True(BlockOrientation.Same(BlockOrientation.BlockOf(item), meta, got, c.Options.Profile.Data.VersionId != "b1.8.1"),
            $"{item} at {x},{y},{z}: server meta {got}, wanted {meta} ({r.Message})");
        Assert.Equal(got, c.World.GetMeta(new BlockPos(x, y, z)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_furnace_stairs_each_way_and_a_wall_torch_end_with_the_designed_metas(bool modern)
    {
        var (c, s) = await Harness.StartAsync(World, release1122: modern);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(11, 64, 8) == Blocks.Cobblestone));
        await Place(c, s, 6, 64, 6, Blocks.Furnace, 5);          // front east
        await Place(c, s, 7, 64, 10, Blocks.WoodenStairs, 0);    // rising east
        await Place(c, s, 8, 64, 10, Blocks.WoodenStairs, 1);    // west
        await Place(c, s, 9, 64, 10, Blocks.WoodenStairs, 2);    // south
        await Place(c, s, 10, 64, 10, Blocks.WoodenStairs, 3);   // north
        await Place(c, s, 10, 64, 8, Blocks.Torch, 2);           // on the east wall (the post at 11,64,8)
        if (modern)
        {
            await Place(c, s, 6, 64, 8, Blocks.Log, 1 | 4, damage: 1); // a spruce log along x (the post at 5,64,8)
            await Place(c, s, 7, 64, 8, Blocks.WoodenStairs, 3 | 4);   // north, upside down: high on the log's east side
            await Place(c, s, 6, 64, 10, 126, 8);                      // a top slab
        }
        Assert.Equal(Blocks.Furnace, s.Blocks[(6, 64, 6)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_piston_pointing_up_is_placed_from_a_stand_over_it(bool modern)
    {
        var (c, s) = await Harness.StartAsync(st =>
        {
            World(st);
            st.Blocks[(9, 64, 9)] = Blocks.Cobblestone; // a step: the stand over the piston's level
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(9, 64, 9) == Blocks.Cobblestone));
        await Place(c, s, 8, 64, 9, Blocks.Piston, 1);
        Assert.True(c.World.Player.Position.Y >= 65 - 1e-6, $"stood at {c.World.Player.Position}");
        await Place(c, s, 7, 64, 7, Blocks.Piston, 4); // facing west: level, looking east
    }
}
