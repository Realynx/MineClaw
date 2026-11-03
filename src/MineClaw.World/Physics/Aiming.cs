namespace MineClaw.World;

/// <summary>Where to look to dig/use a block: the face to send in 0x0E/0x0F and the look angles.</summary>
public readonly record struct DigAim(BlockPos Block, BlockFace Face, Vec3d Point, float Yaw, float Pitch, double Distance);

/// <summary>How to place a block into <see cref="Target"/>: click <see cref="Against"/> on <see cref="Face"/>
/// (0x0F uses Against's coordinates + Face).</summary>
public readonly record struct PlaceAim(BlockPos Target, BlockPos Against, BlockFace Face, Vec3d Point, float Yaw, float Pitch, double Distance);

/// <summary>Choosing visible faces and look angles for digging, using and placing within reach.</summary>
public static class Aiming
{
    /// <summary>Default reach used by the bot (vanilla survival reach is 4.0–4.5; servers allow up to 6).</summary>
    public const double DefaultReach = 4.5;

    // Sample points on a face, in face-local (u,v) ∈ [0,1]; center first.
    private static readonly (double U, double V)[] Samples =
        [(0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75), (0.5, 0.1), (0.5, 0.9), (0.1, 0.5), (0.9, 0.5)];

    /// <summary>Finds the nearest visible point on <paramref name="target"/>'s selection box that a ray from
    /// <paramref name="eye"/> hits first, within <paramref name="reach"/>. Null if the block is not targetable from here.</summary>
    public static DigAim? FindDigAim(IBlockAccess w, Vec3d eye, BlockPos target, double reach = DefaultReach)
    {
        var boxes = new List<AABB>();
        BlockShapes.AddSelectionBoxes(w, target.X, target.Y, target.Z, boxes);
        DigAim? best = null;
        foreach (var box in boxes)
        foreach (var face in BlockFaceExtensions.All)
        {
            if (!FacesEye(box, face, eye)) continue;
            foreach (var (u, v) in Samples)
            {
                var p = PointOnFace(box, face, u, v);
                double d = eye.DistanceTo(p);
                if (d > reach || (best is not null && d >= best.Value.Distance)) continue;
                var hit = Raycast.Cast(w, eye, p - eye, d + 0.01);
                if (hit is { } h && h.Pos == target && h.Face == face)
                {
                    var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, p);
                    best = new DigAim(target, face, p, yaw, pitch, d);
                    break;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Finds a solid neighbour of <paramref name="target"/> whose face toward the target is visible within reach.
    /// Returns null when the target cell is occupied, nothing can be clicked, or the new block would overlap
    /// <paramref name="playerBox"/> (the server refuses to place inside an entity). Interactable blocks (chests,
    /// furnaces...) are never used as the clicked block, since clicking them opens them instead.
    /// </summary>
    public static PlaceAim? FindPlaceAim(IBlockAccess w, Vec3d eye, BlockPos target, double reach = DefaultReach,
        AABB? playerBox = null)
    {
        if (!IsReplaceable(w, w.GetBlock(target.X, target.Y, target.Z))) return null;
        if (playerBox is { } pb && pb.Intersects(AABB.Block(target.X, target.Y, target.Z))) return null;

        PlaceAim? best = null;
        var boxes = new List<AABB>();
        foreach (var face in BlockFaceExtensions.All)
        {
            // Neighbour N is at target - normal(face); clicking N on `face` puts the block at target.
            var n = face.Normal();
            var against = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
            int id = w.GetBlock(against.X, against.Y, against.Z);
            if (id == 0 || BlockShapes.IsLiquid(id) || w.Info(id).Interactable) continue;
            if (!BlockShapes.HasCollision(w, against.X, against.Y, against.Z)) continue;
            boxes.Clear();
            BlockShapes.AddSelectionBoxes(w, against.X, against.Y, against.Z, boxes);
            foreach (var box in boxes)
            {
                if (!FacesEye(box, face, eye)) continue;
                foreach (var (u, v) in Samples)
                {
                    var p = PointOnFace(box, face, u, v);
                    double d = eye.DistanceTo(p);
                    if (d > reach || (best is not null && d >= best.Value.Distance)) continue;
                    var hit = Raycast.Cast(w, eye, p - eye, d + 0.01);
                    if (hit is { } h && h.Pos == against && h.Face == face)
                    {
                        var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, p);
                        best = new PlaceAim(target, against, face, p, yaw, pitch, d);
                        break;
                    }
                }
            }
        }
        return best;
    }

    /// <summary>How far the server's bucket raytrace reaches (ItemBucket / Item.rayTrace: 5 blocks, b1.8.1 and 1.12.2).</summary>
    public const double BucketReach = 5.0;

    /// <summary>1.12.2 EntityPlayer.getEyeHeight while sneaking: 1.62 − 0.08 (b1.8.1's server keeps 1.62).</summary>
    public const double SneakEyeDrop = 0.08;

    /// <summary>
    /// Where to look to pour a full bucket into <paramref name="target"/>. A bucket has no onItemUse: the server pours only on
    /// the use-item packet (b1.8.1 0x0F direction −1, 1.12.2 CPacketPlayerTryUseItem), raytracing from ITS idea of my eye along
    /// my last sent look, passing liquids, and fills hit.offset(face). So the look must hit a solid neighbour's face toward
    /// the target from the standing eye <paramref name="eye"/> (feet + 1.62) AND from the 1.12.2 sneaking eye 0.08 lower: a
    /// point barely in view over a hole's rim is hit on the rim's top by the lower ray, and the water lands on the grass beside
    /// the hole. Nearest such point within <paramref name="reach"/> (and the 5-block bucket ray), or null.
    /// </summary>
    public static PlaceAim? FindPourAim(IBlockAccess w, Vec3d eye, BlockPos target, double reach = DefaultReach)
    {
        if (!IsReplaceable(w, w.GetBlock(target.X, target.Y, target.Z))) return null;
        double range = Math.Min(reach, BucketReach);
        var low = eye with { Y = eye.Y - SneakEyeDrop };
        PlaceAim? best = null;
        var boxes = new List<AABB>();
        foreach (var face in BlockFaceExtensions.All)
        {
            var n = face.Normal();
            var against = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
            int id = w.GetBlock(against.X, against.Y, against.Z);
            // A replaceable block (snow layer) hit on its top takes the liquid itself (1.12.2 ItemBucket), not the target.
            if (id == 0 || IsReplaceable(w, id) || w.Info(id).Interactable) continue;
            if (!BlockShapes.HasCollision(w, against.X, against.Y, against.Z)) continue;
            boxes.Clear();
            BlockShapes.AddSelectionBoxes(w, against.X, against.Y, against.Z, boxes);
            foreach (var box in boxes)
            {
                if (!FacesEye(box, face, eye) || !FacesEye(box, face, low)) continue;
                foreach (var (u, v) in Samples)
                {
                    var p = PointOnFace(box, face, u, v);
                    double d = eye.DistanceTo(p);
                    if (d > range || (best is not null && d >= best.Value.Distance)) continue;
                    var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, p);
                    var dir = PlayerPhysics.LookVector(yaw, pitch); // what the server rebuilds from the sent floats
                    if (!HitsFace(w, eye, dir, range, against, face) || !HitsFace(w, low, dir, range, against, face)) continue;
                    best = new PlaceAim(target, against, face, p, yaw, pitch, d);
                    break;
                }
            }
        }
        return best;
    }

    private static bool HitsFace(IBlockAccess w, Vec3d from, Vec3d dir, double range, BlockPos against, BlockFace face) =>
        Raycast.Cast(w, from, dir, range) is { } h && h.Pos == against && h.Face == face;

    /// <summary>Blocks that placing replaces (air, liquids, fire, snow layer, tall grass).</summary>
    public static bool IsReplaceable(IBlockAccess w, int id) =>
        id == 0 || BlockShapes.IsLiquid(id) || id == BlockShapes.Fire || id == BlockShapes.SnowLayer || id == 31;

    private static bool FacesEye(AABB b, BlockFace face, Vec3d eye) => face switch
    {
        BlockFace.Bottom => eye.Y < b.MinY,
        BlockFace.Top => eye.Y > b.MaxY,
        BlockFace.North => eye.Z < b.MinZ,
        BlockFace.South => eye.Z > b.MaxZ,
        BlockFace.West => eye.X < b.MinX,
        BlockFace.East => eye.X > b.MaxX,
        _ => false,
    };

    private static Vec3d PointOnFace(AABB b, BlockFace face, double u, double v)
    {
        double Lx(double t) => b.MinX + (b.MaxX - b.MinX) * t;
        double Ly(double t) => b.MinY + (b.MaxY - b.MinY) * t;
        double Lz(double t) => b.MinZ + (b.MaxZ - b.MinZ) * t;
        return face switch
        {
            BlockFace.Bottom => new Vec3d(Lx(u), b.MinY, Lz(v)),
            BlockFace.Top => new Vec3d(Lx(u), b.MaxY, Lz(v)),
            BlockFace.North => new Vec3d(Lx(u), Ly(v), b.MinZ),
            BlockFace.South => new Vec3d(Lx(u), Ly(v), b.MaxZ),
            BlockFace.West => new Vec3d(b.MinX, Ly(v), Lz(u)),
            BlockFace.East => new Vec3d(b.MaxX, Ly(v), Lz(u)),
            _ => b.Center,
        };
    }
}
