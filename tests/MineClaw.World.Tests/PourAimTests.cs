using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Pouring a full bucket into a dug hole. The server raytraces the sent look from its eye, which is 0.08 lower while
/// sneaking on 1.12.2, so the aim has to put the water in the hole from either eye height.
/// </summary>
public class PourAimTests
{
    /// <summary>Grass field at y=63 (dirt under it) with a one-deep hole at (0,63,0).</summary>
    private static WorldState HoleInGrass()
    {
        var w = TestWorld.Create();
        w.Fill(-7, 60, -7, 7, 62, 7, Dirt);
        w.Fill(-7, 63, -7, 7, 63, 7, Grass);
        w.Set(0, 63, 0, Air);
        return w;
    }

    /// <summary>The server side of a full bucket, written out independently of the client's raycast: small steps along the
    /// look, passing air and liquids, the first solid cell is hit through the face it was entered by; the liquid goes into
    /// that cell offset by the face (ItemBucket.onItemRightClick). Null = no hit within 5 blocks, or an edge-on hit.</summary>
    private static BlockPos? ServerPour(WorldState w, Vec3d eye, float yaw, float pitch)
    {
        var dir = PlayerPhysics.LookVector(yaw, pitch);
        var prev = eye.ToBlockPos();
        for (double t = 0.0005; t <= Aiming.BucketReach; t += 0.0005)
        {
            var c = (eye + dir * t).ToBlockPos();
            if (c == prev) continue;
            int id = w.GetBlock(c.X, c.Y, c.Z);
            if (id != 0 && !BlockShapes.IsLiquid(id))
            {
                int dx = prev.X - c.X, dy = prev.Y - c.Y, dz = prev.Z - c.Z;
                if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) != 1) return null; // through an edge: the face is a coin toss
                return new BlockPos(c.X + dx, c.Y + dy, c.Z + dz);
            }
            prev = c;
        }
        return null;
    }

    private static IEnumerable<Vec3d> Stands()
    {
        foreach (int x in Enumerable.Range(-3, 7))
        foreach (int z in Enumerable.Range(-3, 7))
        foreach (var (ox, oz) in new[] { (0.5, 0.5), (0.3, 0.5), (0.7, 0.5), (0.5, 0.3), (0.5, 0.7), (0.3, 0.3), (0.7, 0.7) })
            if (x != 0 || z != 0) yield return new Vec3d(x + ox, 64, z + oz);
    }

    [Fact]
    public void Pour_aim_lands_the_water_in_the_hole_from_the_standing_and_the_sneaking_eye()
    {
        var w = HoleInGrass();
        var hole = new BlockPos(0, 63, 0);
        int aimed = 0;
        foreach (var feet in Stands())
        {
            var eye = feet + new Vec3d(0, 1.62, 0);
            if (Aiming.FindPourAim(w, eye, hole, 4.5) is not { } a) continue;
            aimed++;
            Assert.Equal(hole, ServerPour(w, eye, a.Yaw, a.Pitch));
            Assert.Equal(hole, ServerPour(w, eye with { Y = eye.Y - Aiming.SneakEyeDrop }, a.Yaw, a.Pitch));
        }
        Assert.True(aimed >= 100, $"only {aimed} stands found a pour aim"); // every cell around the hole works from somewhere on it
    }

    [Fact]
    public void Place_aim_alone_is_not_enough_for_a_bucket_from_the_sneaking_eye()
    {
        // Why the pour has its own aim: the block-placement aim (nearest visible point; b1.8.1's 0x0F names the face, nothing
        // raytraces it) lands a sneaking pour on the grass beside the hole from some stands.
        var w = HoleInGrass();
        var hole = new BlockPos(0, 63, 0);
        var misses = Stands().Where(feet =>
        {
            var eye = feet + new Vec3d(0, 1.62, 0);
            return Aiming.FindPlaceAim(w, eye, hole, 4.5) is { } a
                   && ServerPour(w, eye with { Y = eye.Y - Aiming.SneakEyeDrop }, a.Yaw, a.Pitch) is { } at && at != hole;
        }).ToList();
        Assert.NotEmpty(misses);
    }

    [Fact]
    public void Pour_aim_needs_an_empty_target_and_a_solid_non_container_neighbour()
    {
        var w = HoleInGrass();
        var eye = new Vec3d(1.5, 65.62, 0.5);
        Assert.Null(Aiming.FindPourAim(w, eye, new BlockPos(1, 63, 1), 4.5)); // grass: not empty
        w.Fill(-1, 62, -1, 1, 62, 1, Air); // the hole's floor and walls gone below: still the side walls of y=63
        w.Set(0, 62, 0, Chest);
        var a = Aiming.FindPourAim(w, eye, new BlockPos(0, 63, 0), 4.5);
        Assert.NotNull(a);
        Assert.NotEqual(new BlockPos(0, 62, 0), a!.Value.Against); // never clicks the chest under it
    }
}
