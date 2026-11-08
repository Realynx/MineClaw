using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

public sealed class SneakPlacementSupportTests
{
    private static WorldState Loaded(bool modern = true)
    {
        var w = modern ? new WorldState(Release1122GameData.Instance.Block, 256, physics: Release1122PhysicsRules.Instance) : new WorldState();
        w.ApplyChunkRegion(0, 0, 0, 16, w.WorldHeight, 16, new byte[16 * w.WorldHeight * 16], null, null, null);
        return w;
    }

    [Theory]
    [InlineData(23)]
    [InlineData(54)]
    [InlineData(58)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(130)]
    [InlineData(146)]
    [InlineData(154)]
    [InlineData(158)]
    [InlineData(219)]
    [InlineData(234)]
    public void Modern_containers_require_explicit_sneak_placement_context(int block)
    {
        var w = Loaded();
        var support = new BlockPos(0, 64, 0);
        w.SetBlock(0, 64, 0, block, 0);
        Assert.False(PlaceRules.IsSupport(w, support));
        Assert.True(PlaceRules.IsSupport(w, support, allowSneakInteraction: true));
        Assert.False(PlaceRules.IsWallSupport(w, support));
    }

    [Theory]
    [InlineData(54)]
    [InlineData(58)]
    [InlineData(61)]
    public void Beta_containers_still_refuse_placement_even_with_the_opt_in(int block)
    {
        var w = Loaded(modern: false);
        w.SetBlock(0, 64, 0, block, 0);
        Assert.False(PlaceRules.IsSupport(w, new(0, 64, 0), allowSneakInteraction: true));
        Assert.NotNull(PlaceRules.NoSupportProblem(w, new(0, 65, 0), allowSneakInteraction: true));
    }

    [Theory]
    [InlineData(26)] // bed
    [InlineData(64)] // door
    [InlineData(96)] // trapdoor
    [InlineData(9)] // water
    [InlineData(0)]
    public void The_opt_in_does_not_make_other_interactions_or_empty_cells_placement_supports(int block)
    {
        var w = Loaded();
        w.SetBlock(0, 64, 0, block, 0);
        Assert.False(PlaceRules.IsSupport(w, new(0, 64, 0), allowSneakInteraction: true));
    }

    [Fact]
    public void A_stacked_chest_has_a_visible_top_face_only_for_placement_not_bucket_pouring()
    {
        var w = Loaded();
        var support = new BlockPos(0, 64, 0);
        var target = support.Up();
        var eye = new Vec3d(2.5, 65.62, 0.5);
        w.SetBlock(0, 64, 0, Blocks.Chest, 3);
        Assert.NotNull(PlaceRules.NoSupportProblem(w, target));
        Assert.Null(Aiming.FindPlaceAim(w, eye, target));
        Assert.Null(Aiming.FindPourAim(w, eye, target));
        Assert.Null(PlaceRules.NoSupportProblem(w, target, allowSneakInteraction: true));
        var aim = Assert.IsType<PlaceAim>(Aiming.FindPlaceAim(w, eye, target, allowSneakInteraction: true));
        Assert.Equal(support, aim.Against);
        Assert.Equal(BlockFace.Top, aim.Face);
        Assert.NotNull(PlaceRules.BlindAim(w, eye, target, 4.5, allowSneakInteraction: true));
        Assert.Null(Aiming.FindPlaceAim(w, eye, target, playerBox: AABB.Block(target.X, target.Y, target.Z), allowSneakInteraction: true));
    }
}
