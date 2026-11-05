using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 11-04: a seed lying inside a farm was written off "1 drop(s) left near the void" by the collect while the colony offered it: the
/// client only counted a full-height block as somewhere to stand, and 1.12.2 farmland is 15/16 high. Now a pickup spot stands on
/// anything that holds a player up (<see cref="Safety.StandFeet"/>), in both versions.
/// </summary>
public class FarmDropTests
{
    private const int Farmland = 60;

    /// <summary>A dirt yard (0..20 × 0..16, surface at 63) with a 9×9 tilled field at x 10..18, z 4..12; the bot spawns on the dirt.</summary>
    private static Task<(MinecraftClient C, FakeServer S)> Farm(bool release1122, Action<FakeServer>? more = null) =>
        Harness.StartAsync(srv =>
        {
            for (int x = 0; x <= 20; x++)
            for (int z = 0; z <= 16; z++)
            {
                srv.Blocks[(x, 62, z)] = Blocks.Stone;
                srv.Blocks[(x, 63, z)] = x is >= 10 and <= 18 && z is >= 4 and <= 12 ? Farmland : Blocks.Dirt;
            }
            srv.StrictPickup = true;
            more?.Invoke(srv);
        }, release1122: release1122);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_drop_inside_a_tilled_field_is_collected_from_the_farmland_next_to_it(bool release1122)
    {
        var (c, s) = await Farm(release1122);
        await using var _ = c;
        using var __ = s;
        double rest = 63 + BlockShapes.CollisionTop(c.World, 14, 63, 8); // 1.12.2: 63.9375; b1.8.1 (a full collision box): 64
        Assert.Equal(release1122 ? 63.9375 : 64.0, rest, 6);
        s.SpawnDrop(14.5, rest, 8.5, new ItemStack(Items.Seeds, 1, 0));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));

        var r = await c.CollectDropsAsync(8);

        Assert.True(r.Ok, r.ToString());
        Assert.DoesNotContain("void", r.Message);
        Assert.Equal(1, c.World.Inventory.CountItem(Items.Seeds));
        var p = c.World.Player.Position;
        Assert.Equal(rest, p.Y, 2);                                                        // standing on the field
        Assert.Equal(Farmland, c.World.GetBlock((int)Math.Floor(p.X), 63, (int)Math.Floor(p.Z)));
        Assert.True(Math.Abs(p.X - 14.5) < 1.5 && Math.Abs(p.Z - 8.5) < 1.5, $"picked up from {p}");
    }

    /// <summary>A full bag says so: "none in sight … with room for it: 1 in sight with no room in my bag" (the colony then doesn't count a
    /// miss at the drop's spot; live 11-04 the bare "none in sight" couldn't tell a full bag from a drop someone else took).</summary>
    [Fact]
    public async Task A_full_bag_says_the_drop_is_in_sight_with_no_room_for_it()
    {
        var (c, s) = await Farm(true, srv => { for (int i = 9; i < 45; i++) srv.Inventory[i] = Harness.Stack(Blocks.Stone, 64); });
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.Inventory.RoomFor(new ItemStack(Items.Seeds, 1, 0)) == 0));
        s.SpawnDrop(9.5, 64, 8.5, new ItemStack(Items.Seeds, 1, 0));
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));

        var r = await c.CollectDropsAsync(3);

        Assert.False(r.Ok);
        Assert.Contains("(none in sight within 3 with room for it: 1 in sight with no room in my bag)", r.Message);
        Assert.DoesNotContain(";", r.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_drop_over_the_void_beside_the_field_is_still_written_off(bool release1122)
    {
        var (c, s) = await Farm(release1122);
        await using var _ = c;
        using var __ = s;
        s.SpawnDrop(23.5, 64.1, 8.5, new ItemStack(Items.Seeds, 1, 0)); // two blocks past the yard's east rim
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count > 0));

        var r = await c.CollectDropsAsync(16);

        Assert.False(r.Ok);
        Assert.Contains("near the void", r.Message);
        Assert.Equal(0, c.World.Inventory.CountItem(Items.Seeds));
    }
}
