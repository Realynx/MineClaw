namespace MineClaw.World;

/// <summary>Result of a block raycast.</summary>
public readonly record struct RaycastHit(BlockPos Pos, BlockFace Face, Vec3d Point, double Distance, int BlockId);

/// <summary>Block raycasting against selection boxes (what the player can target for digging/using/placing).</summary>
public static class Raycast
{
    /// <summary>Walks the voxels along the ray (Amanatides–Woo) and returns the first selection box hit within
    /// <paramref name="maxDistance"/>, or null. Liquids, air and fire are not targetable.</summary>
    public static RaycastHit? Cast(IBlockAccess w, Vec3d origin, Vec3d direction, double maxDistance)
    {
        var dir = direction.Normalized();
        if (dir == Vec3d.Zero) return null;
        var walk = new VoxelWalk(origin, dir);
        var boxes = new List<AABB>(4);
        for (int guard = 0; guard < 1024 && walk.T <= maxDistance; guard++)
        {
            if (walk.Y >= 0 && walk.Y < w.WorldHeight && HitIn(w, walk.X, walk.Y, walk.Z, origin, dir, maxDistance, boxes) is { } hit) return hit;
            walk.Step();
        }
        return null;
    }

    /// <summary>The nearest of the cell's selection boxes the ray hits within <paramref name="maxDistance"/>, or null.</summary>
    private static RaycastHit? HitIn(IBlockAccess w, int x, int y, int z, Vec3d origin, Vec3d dir, double maxDistance, List<AABB> boxes)
    {
        boxes.Clear();
        BlockShapes.AddSelectionBoxes(w, x, y, z, boxes);
        RaycastHit? best = null;
        foreach (var b in boxes)
        {
            var hit = b.RayIntersect(origin, dir, maxDistance);
            if (hit is { } h && (best is null || h.T < best.Value.Distance))
                best = new RaycastHit(new BlockPos(x, y, z), h.Face, origin + dir * h.T, h.T, w.GetBlock(x, y, z));
        }
        return best;
    }

    /// <summary>Amanatides–Woo voxel traversal: the current cell, the ray distance at its entry, and per axis the step and the
    /// distances to the next boundary.</summary>
    private struct VoxelWalk
    {
        public int X, Y, Z;
        public double T;
        private readonly int _stepX, _stepY, _stepZ;
        private readonly double _deltaX, _deltaY, _deltaZ;
        private double _maxX, _maxY, _maxZ;

        public VoxelWalk(Vec3d origin, Vec3d dir)
        {
            X = (int)Math.Floor(origin.X);
            Y = (int)Math.Floor(origin.Y);
            Z = (int)Math.Floor(origin.Z);
            T = 0;
            Axis(origin.X, dir.X, X, out _stepX, out _deltaX, out _maxX);
            Axis(origin.Y, dir.Y, Y, out _stepY, out _deltaY, out _maxY);
            Axis(origin.Z, dir.Z, Z, out _stepZ, out _deltaZ, out _maxZ);
        }

        private static void Axis(double o, double d, int cell, out int step, out double delta, out double max)
        {
            step = Math.Sign(d);
            delta = step == 0 ? double.PositiveInfinity : Math.Abs(1 / d);
            max = step == 0 ? double.PositiveInfinity : (step > 0 ? (cell + 1 - o) : (o - cell)) * delta;
        }

        /// <summary>Into the next cell across the nearest boundary (ties: z, then y).</summary>
        public void Step()
        {
            if (_maxX < _maxY && _maxX < _maxZ) { X += _stepX; T = _maxX; _maxX += _deltaX; }
            else if (_maxY < _maxZ) { Y += _stepY; T = _maxY; _maxY += _deltaY; }
            else { Z += _stepZ; T = _maxZ; _maxZ += _deltaZ; }
        }
    }

    /// <summary>Raycast along a yaw/pitch look direction.</summary>
    public static RaycastHit? Cast(IBlockAccess w, Vec3d eye, float yaw, float pitch, double maxDistance) =>
        Cast(w, eye, PlayerPhysics.LookVector(yaw, pitch), maxDistance);
}
