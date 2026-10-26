namespace MineClaw.World;

/// <summary>Void / hazard safety checks shared by the world queries, pathfinder and path follower.</summary>
public static class Safety
{
    /// <summary>True when no block with a collision box exists at or below y in column (x,z).
    /// Unloaded columns count as void (unknown = unsafe).</summary>
    public static bool IsOverVoid(IBlockAccess w, int x, int y, int z)
    {
        if (!w.IsLoaded(x, z)) return true;
        for (int yy = Math.Min(y, WorldState.Height - 1); yy >= 0; yy--)
            if (BlockShapes.HasCollision(w, x, yy, z)) return false;
        return true;
    }

    /// <summary>Highest y ≤ fromY in column (x,z) with a collision box, or -1 (void / unloaded).</summary>
    public static int SupportBelow(IBlockAccess w, int x, int fromY, int z)
    {
        if (!w.IsLoaded(x, z)) return -1;
        for (int yy = Math.Min(fromY, WorldState.Height - 1); yy >= 0; yy--)
            if (BlockShapes.HasCollision(w, x, yy, z)) return yy;
        return -1;
    }

    /// <summary>True when every column overlapped by the player's hitbox at <paramref name="feet"/> is void below the feet.</summary>
    public static bool IsPlayerOverVoid(IBlockAccess w, Vec3d feet)
    {
        var box = AABB.ForPlayer(feet);
        int x0 = (int)Math.Floor(box.MinX), x1 = (int)Math.Floor(box.MaxX - 1e-9);
        int z0 = (int)Math.Floor(box.MinZ), z1 = (int)Math.Floor(box.MaxZ - 1e-9);
        int y = (int)Math.Floor(feet.Y + 1e-6);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
            if (!IsOverVoid(w, x, y, z)) return false;
        return true;
    }

    /// <summary>
    /// Number of the 8 horizontal neighbours of foot cell (x,y,z) that drop into the void (passable and nothing below).
    /// Positions with a non-zero count are "edge" positions where the follower should sneak.
    /// </summary>
    public static int VoidNeighbourCount(IBlockAccess w, int x, int y, int z)
    {
        int n = 0;
        for (int dx = -1; dx <= 1; dx++)
        for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            if (!BlockShapes.HasCollision(w, x + dx, y, z + dz) && IsOverVoid(w, x + dx, y, z + dz)) n++;
        }
        return n;
    }

    /// <summary>Lava, fire, cactus or any block flagged <c>Hazard</c>.</summary>
    public static bool IsHazard(IBlockAccess w, int id) =>
        id != 0 && (BlockShapes.IsLava(id) || id == BlockShapes.Fire || id == BlockShapes.Cactus || w.Info(id).Hazard);
}
