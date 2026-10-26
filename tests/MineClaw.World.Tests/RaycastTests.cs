using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

public class RaycastTests
{
    [Fact]
    public void LookingDown_HitsTopFace()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Stone);
        var hit = Raycast.Cast(w, new Vec3d(0.5, 65.62, 0.5), 0, 90, 4.5);
        Assert.NotNull(hit);
        Assert.Equal(new BlockPos(0, 63, 0), hit!.Value.Pos);
        Assert.Equal(BlockFace.Top, hit.Value.Face);
        Assert.Equal(64, hit.Value.Point.Y, 6);
        Assert.Equal(1.62, hit.Value.Distance, 6);
    }

    [Fact]
    public void LookingEast_HitsWestFace_AndRespectsRange()
    {
        var w = TestWorld.Create();
        w.Set(3, 65, 0, Stone);
        var eye = new Vec3d(0.5, 65.5, 0.5);
        var hit = Raycast.Cast(w, eye, new Vec3d(1, 0, 0), 4.5);
        Assert.Equal(BlockFace.West, hit!.Value.Face);
        Assert.Equal(new BlockPos(3, 65, 0), hit.Value.Pos);
        Assert.Null(Raycast.Cast(w, eye, new Vec3d(1, 0, 0), 2.0));
        Assert.Null(Raycast.Cast(w, eye, new Vec3d(-1, 0, 0), 10));
    }

    [Fact]
    public void Slab_SelectionBoxIsHalfHeight()
    {
        var w = TestWorld.Create();
        w.Set(2, 64, 0, Slab);
        // Ray at y=64.75 passes over the slab.
        Assert.Null(Raycast.Cast(w, new Vec3d(0.5, 64.75, 0.5), new Vec3d(1, 0, 0), 5));
        Assert.NotNull(Raycast.Cast(w, new Vec3d(0.5, 64.25, 0.5), new Vec3d(1, 0, 0), 5));
    }

    [Fact]
    public void LookAngles_RoundTrip()
    {
        var from = new Vec3d(0, 0, 0);
        var to = new Vec3d(-3, -2, 5);
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(from, to);
        var dir = PlayerPhysics.LookVector(yaw, pitch);
        var expect = to.Normalized();
        Assert.Equal(expect.X, dir.X, 4);
        Assert.Equal(expect.Y, dir.Y, 4);
        Assert.Equal(expect.Z, dir.Z, 4);
    }

    [Fact]
    public void DigAim_ChoosesVisibleFace()
    {
        var w = TestWorld.Create();
        w.Fill(-2, 63, -2, 4, 63, 2, Stone);
        w.Set(3, 64, 0, Stone);
        var eye = new Vec3d(0.5, 65.62, 0.5);
        var aim = Aiming.FindDigAim(w, eye, new BlockPos(3, 64, 0));
        Assert.NotNull(aim);
        Assert.Contains(aim!.Value.Face, new[] { BlockFace.West, BlockFace.Top });
        var check = Raycast.Cast(w, eye, aim.Value.Yaw, aim.Value.Pitch, 4.5);
        Assert.Equal(new BlockPos(3, 64, 0), check!.Value.Pos);
        // Fully enclosed block can't be targeted.
        Assert.Null(Aiming.FindDigAim(w, eye, new BlockPos(0, 63, 3)));
    }

    [Fact]
    public void PlaceAim_ForBridging_NeedsEyePastTheEdge()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Cobble);
        var target = new BlockPos(1, 63, 0);
        // From the block center the side face is hidden.
        var center = new Vec3d(0.5, 64, 0.5);
        Assert.Null(Aiming.FindPlaceAim(w, center + new Vec3d(0, 1.62, 0), target, 4.5, AABB.ForPlayer(center)));
        // Sneaked out to the edge: eye is past x = 1, so the +X face of the block underfoot is visible.
        var edge = new Vec3d(1.25, 64, 0.5);
        var aim = Aiming.FindPlaceAim(w, edge + new Vec3d(0, 1.62, 0), target, 4.5, AABB.ForPlayer(edge));
        Assert.NotNull(aim);
        Assert.Equal(new BlockPos(0, 63, 0), aim!.Value.Against);
        Assert.Equal(BlockFace.East, aim.Value.Face);
    }

    [Fact]
    public void PlaceAim_RefusesInsidePlayer_AndInteractables()
    {
        var w = TestWorld.Create();
        w.Set(0, 63, 0, Cobble);
        var feet = new Vec3d(0.5, 64, 0.5);
        Assert.Null(Aiming.FindPlaceAim(w, feet + new Vec3d(0, 1.62, 0), new BlockPos(0, 64, 0), 4.5, AABB.ForPlayer(feet)));
        w.Set(3, 64, 0, Chest);
        Assert.Null(Aiming.FindPlaceAim(w, feet + new Vec3d(0, 1.62, 0), new BlockPos(3, 65, 0), 4.5, AABB.ForPlayer(feet)));
    }
}
