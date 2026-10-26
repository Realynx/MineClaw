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
        int x = (int)Math.Floor(origin.X), y = (int)Math.Floor(origin.Y), z = (int)Math.Floor(origin.Z);
        int stepX = Math.Sign(dir.X), stepY = Math.Sign(dir.Y), stepZ = Math.Sign(dir.Z);
        double tDeltaX = stepX == 0 ? double.PositiveInfinity : Math.Abs(1 / dir.X);
        double tDeltaY = stepY == 0 ? double.PositiveInfinity : Math.Abs(1 / dir.Y);
        double tDeltaZ = stepZ == 0 ? double.PositiveInfinity : Math.Abs(1 / dir.Z);
        double tMaxX = stepX == 0 ? double.PositiveInfinity : (stepX > 0 ? (x + 1 - origin.X) : (origin.X - x)) * tDeltaX;
        double tMaxY = stepY == 0 ? double.PositiveInfinity : (stepY > 0 ? (y + 1 - origin.Y) : (origin.Y - y)) * tDeltaY;
        double tMaxZ = stepZ == 0 ? double.PositiveInfinity : (stepZ > 0 ? (z + 1 - origin.Z) : (origin.Z - z)) * tDeltaZ;

        var boxes = new List<AABB>(4);
        double t = 0;
        for (int guard = 0; guard < 1024 && t <= maxDistance; guard++)
        {
            if (y >= 0 && y < WorldState.Height)
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
                if (best is not null) return best;
            }

            if (tMaxX < tMaxY && tMaxX < tMaxZ) { x += stepX; t = tMaxX; tMaxX += tDeltaX; }
            else if (tMaxY < tMaxZ) { y += stepY; t = tMaxY; tMaxY += tDeltaY; }
            else { z += stepZ; t = tMaxZ; tMaxZ += tDeltaZ; }
        }
        return null;
    }

    /// <summary>Raycast along a yaw/pitch look direction.</summary>
    public static RaycastHit? Cast(IBlockAccess w, Vec3d eye, float yaw, float pitch, double maxDistance) =>
        Cast(w, eye, PlayerPhysics.LookVector(yaw, pitch), maxDistance);
}
