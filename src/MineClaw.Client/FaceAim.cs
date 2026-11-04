using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Aiming at one specific face of a block (the face the 0x0F packet will name), as opposed to
/// <see cref="Aiming.FindDigAim"/>, which picks the nearest visible face. Used for "use item on this face" and
/// "place against this face".
/// </summary>
public static class FaceAim
{
    private static readonly (double U, double V)[] Samples =
        [(0.5, 0.5), (0.25, 0.25), (0.75, 0.25), (0.25, 0.75), (0.75, 0.75), (0.5, 0.15), (0.5, 0.85), (0.15, 0.5), (0.85, 0.5)];

    /// <summary>
    /// The nearest point on <paramref name="face"/> of <paramref name="pos"/> within <paramref name="reach"/> that a ray
    /// from <paramref name="eye"/> reaches without hitting anything else first (liquids: nothing solid in front). Null when
    /// that face points away from the eye or is hidden.
    /// </summary>
    public static DigAim? Find(IBlockAccess w, Vec3d eye, BlockPos pos, BlockFace face, double reach)
    {
        int id = w.GetBlock(pos.X, pos.Y, pos.Z);
        bool liquid = BlockShapes.IsLiquid(id);
        var boxes = new List<AABB>(2);
        BlockShapes.AddSelectionBoxes(w, pos.X, pos.Y, pos.Z, boxes);
        if (boxes.Count == 0) boxes.Add(AABB.Block(pos.X, pos.Y, pos.Z));
        DigAim? best = null;
        foreach (var box in boxes)
        {
            if (!FacesEye(box, face, eye)) continue;
            foreach (var (u, v) in Samples)
            {
                var p = PointOnFace(box, face, u, v);
                double d = eye.DistanceTo(p);
                if (d > reach || (best is not null && d >= best.Value.Distance)) continue;
                if (!SeenAt(w, eye, pos, face, liquid || id == 0, p, d)) continue;
                var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, p);
                best = new DigAim(pos, face, p, yaw, pitch, d);
                break;
            }
        }
        return best;
    }

    /// <summary>The ray from the eye reaches <paramref name="p"/> on the face: for air or a liquid nothing in front, else the first hit
    /// is that face of that block.</summary>
    private static bool SeenAt(IBlockAccess w, Vec3d eye, BlockPos pos, BlockFace face, bool clear, Vec3d p, double d)
    {
        var hit = Raycast.Cast(w, eye, p - eye, d + 0.01);
        return clear
            ? hit is null || hit.Value.Distance >= d - 0.01
            : hit is { } h && h.Pos == pos && h.Face == face;
    }

    /// <summary>Center of <paramref name="face"/> of the block's (first) selection box.</summary>
    public static Vec3d FaceCenter(IBlockAccess w, BlockPos pos, BlockFace face)
    {
        var boxes = new List<AABB>(2);
        BlockShapes.AddSelectionBoxes(w, pos.X, pos.Y, pos.Z, boxes);
        var box = boxes.Count > 0 ? boxes[0] : AABB.Block(pos.X, pos.Y, pos.Z);
        return PointOnFace(box, face, 0.5, 0.5);
    }

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

    // ------------------------------------------------------------------ wall-mounted placement

    /// <summary>
    /// The face to click so a wall-mounted block (wall sign, torch, ladder, button, lever, trapdoor) ends up in
    /// <paramref name="target"/> hanging on a horizontal neighbour: the clicked block is <c>target - normal(face)</c>, which
    /// must be a full opaque cube (b1.8.1 <c>isBlockNormalCube</c>) and not interactable (clicking a chest opens it).
    /// Prefers the face whose support is visible from <paramref name="eye"/>, then the nearest. <paramref name="exclude"/>
    /// vetoes supports (e.g. the chest being labelled). Null when no wall is available.
    /// </summary>
    public static BlockFace? WallFace(IBlockAccess w, BlockPos target, Vec3d eye, double reach, Func<BlockPos, bool>? exclude = null)
    {
        BlockFace? best = null;
        double bestScore = double.MaxValue;
        foreach (var face in new[] { BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East })
        {
            var n = face.Normal();
            var support = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
            if (!IsWallSupport(w, support) || exclude?.Invoke(support) == true) continue;
            var aim = Find(w, eye, support, face, reach);
            double score = aim?.Distance ?? 100 + eye.DistanceTo(FaceCenter(w, support, face));
            if (score < bestScore) { bestScore = score; best = face; }
        }
        return best;
    }

    /// <summary>A block a wall sign/torch/ladder/button/lever/trapdoor can hang on: a full cube, not a container/door.</summary>
    public static bool IsWallSupport(IBlockAccess w, BlockPos p) => PlaceRules.IsWallSupport(w, p);
}
