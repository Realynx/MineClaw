using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Result of <see cref="Safety.WaterLanding"/>.</summary>
public readonly record struct WaterLandingInfo(bool BreaksFall, double FallToFloor);

/// <summary>Void / hazard safety checks shared by the world queries, pathfinder and path follower.</summary>
public static class Safety
{
    /// <summary>True when no block with a collision box exists at or below y in column (x,z).
    /// Unloaded columns count as void (unknown = unsafe).</summary>
    public static bool IsOverVoid(IBlockAccess w, int x, int y, int z)
    {
        if (!w.IsLoaded(x, z)) return true;
        for (int yy = Math.Min(y, w.WorldHeight - 1); yy >= 0; yy--)
            if (BlockShapes.HasCollision(w, x, yy, z)) return false;
        return true;
    }

    /// <summary>Highest y ≤ fromY in column (x,z) with a collision box, or -1 (void / unloaded).</summary>
    public static int SupportBelow(IBlockAccess w, int x, int fromY, int z)
    {
        if (!w.IsLoaded(x, z)) return -1;
        for (int yy = Math.Min(fromY, w.WorldHeight - 1); yy >= 0; yy--)
            if (BlockShapes.HasCollision(w, x, yy, z)) return yy;
        return -1;
    }

    /// <summary>True when every column overlapped by the player's hitbox at <paramref name="feet"/> is void below the feet.</summary>
    public static bool IsPlayerOverVoid(IBlockAccess w, Vec3d feet)
    {
        var box = AABB.ForPlayer(feet);
        // Same overlap rule as collision (any positive overlap supports): columns [floor(min), ceil(max) - 1].
        int x0 = (int)Math.Floor(box.MinX), x1 = (int)Math.Ceiling(box.MaxX) - 1;
        int z0 = (int)Math.Floor(box.MinZ), z1 = (int)Math.Ceiling(box.MaxZ) - 1;
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

    /// <summary>
    /// True when stepping into column (x,z) with feet at block level <paramref name="feetY"/> means falling into the
    /// void, into lava, into water that hangs over the void, or further than <paramref name="maxFall"/> blocks.
    /// A wall (collision at feet level) is not a drop. Unloaded columns are deadly (unknown = unsafe).
    /// </summary>
    /// <summary>
    /// A fall at (x, z) from <paramref name="feetY"/> would be fatal whatever its height: the void, lava, or water over the void
    /// (unloaded counts as fatal). A pit with a floor (a quarry, a ravine, a cliff) is not: falling there hurts, and the edge guard's
    /// simulation never lets the body step off it, so it is walked normally (no forced sneak, no edge cost). Live 11-04: pawns
    /// crouch-walked a quarry's stair beside its pit all the way down.
    /// </summary>
    public static bool IsFatalColumn(IBlockAccess w, int x, int feetY, int z)
    {
        if (!w.IsLoaded(x, z)) return true;
        for (int yy = Math.Min(feetY - 1, w.WorldHeight - 1); yy >= 0; yy--)
        {
            int id = w.GetBlock(x, yy, z);
            if (id == 0) continue;
            if (BlockShapes.IsLava(id)) return true;
            if (BlockShapes.IsWater(id)) return IsOverVoid(w, x, yy, z);
            if (BlockShapes.HasCollision(w, x, yy, z)) return false;
        }
        return true;
    }

    public static bool IsDeadlyColumn(IBlockAccess w, int x, int feetY, int z, double maxFall)
    {
        if (!w.IsLoaded(x, z)) return true;
        if (feetY >= 0 && feetY < w.WorldHeight && BlockShapes.HasCollision(w, x, feetY, z)) return false;
        for (int yy = Math.Min(feetY - 1, w.WorldHeight - 1); yy >= 0; yy--)
        {
            int id = w.GetBlock(x, yy, z);
            if (id == 0) continue;
            if (BlockShapes.IsLava(id)) return true;
            if (BlockShapes.IsWater(id))
            {
                if (IsOverVoid(w, x, yy, z)) return true;
                var landing = WaterLanding(w, x, z, feetY, yy);
                return !landing.BreaksFall && landing.FallToFloor > maxFall + 1e-6;
            }
            if (BlockShapes.HasCollision(w, x, yy, z))
                return feetY - (yy + BlockShapes.CollisionTop(w, x, yy, z)) > maxFall + 1e-6;
        }
        return true;
    }

    /// <summary>Feet must be below this offset above the top water cell for Entity.f_() to see the water: the check box is
    /// the hitbox shrunk by 0.4 vertically (and 0.001), so feet + 0.401 &lt; waterTop + 1. mc-dev Entity.java:563-565.</summary>
    public const double WaterWindowAboveTop = 0.599;

    /// <summary>
    /// Whether a fall from feet height <paramref name="feetY"/> into the water column whose top water cell is
    /// <paramref name="waterTopY"/> is guaranteed to cancel fall damage. In b1.8 the fall distance is only reset when
    /// the per-tick water check (Entity.aa → f_(), run before the move; server: NetServerHandler.java:165 before the
    /// position is applied) samples the feet inside the window [floor, waterTop + 0.6). A tick step longer than that
    /// window can skip it and land on the floor with the whole fall (EntityLiving.a(float): ceil(fall − 3)). 1-deep water
    /// (window 0.6) is no better than dry ground (a 3.35-block fall already steps 0.65); 2-deep (1.6) holds up to ≈ 23 blocks.
    /// <see cref="WaterLandingInfo.FallToFloor"/> is the fall onto the floor under the water (∞ over the void).
    /// </summary>
    public static WaterLandingInfo WaterLanding(IBlockAccess w, int x, int z, double feetY, int waterTopY)
    {
        int bottom = waterTopY;
        while (bottom - 1 >= 0 && BlockShapes.IsWater(w.GetBlock(x, bottom - 1, z))) bottom--;
        int below = SupportBelow(w, x, bottom - 1, z);
        if (below < 0) return new WaterLandingInfo(false, double.PositiveInfinity);
        double floorTop = below + BlockShapes.CollisionTop(w, x, below, z);
        double fall = feetY - floorTop;
        if (fall <= 3 + 1e-9) return new WaterLandingInfo(true, fall); // no fall damage either way
        double window = waterTopY + WaterWindowAboveTop - floorTop;
        return new WaterLandingInfo(MaxFallStep(fall) <= window - 1e-6, fall);
    }

    /// <summary>Largest per-tick vertical step while falling <paramref name="fall"/> blocks from rest (gravity 0.08, drag
    /// 0.98, move before gravity): an upper bound for the step that crosses any window ending at the floor.</summary>
    public static double MaxFallStep(double fall)
    {
        double v = 0, d = 0;
        for (int i = 0; i < 1000; i++)
        {
            v = (v - 0.08) * 0.98;
            d -= v;
            if (d >= fall) return -v;
        }
        return -v;
    }

    /// <summary>
    /// Chebyshev distance (1..max) from foot cell (x,y,z) to the nearest deadly column at that level, or max+1 when
    /// none is within <paramref name="max"/>. 1 = the cell is on the rim of the island.
    /// </summary>
    public static int EdgeDistance(IBlockAccess w, int x, int y, int z, int max = 3, double maxFall = 3)
    {
        for (int r = 1; r <= max; r++)
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
            if (IsDeadlyColumn(w, x + dx, y, z + dz, maxFall)) return r;
        }
        return max + 1;
    }

    /// <summary>A player centered in foot cell (x,y,z) would stand on solid ground with a clear body, out of hazards,
    /// and not over the void.</summary>
    public static bool IsStandable(IBlockAccess w, int x, int y, int z)
    {
        if (!w.IsLoaded(x, z) || y < 1 || y + 1 >= w.WorldHeight) return false;
        if (!BlockShapes.HasCollision(w, x, y - 1, z) || BlockShapes.CollisionTop(w, x, y - 1, z) < 1 - 1e-9) return false;
        if (BlockShapes.HasCollision(w, x, y, z) || BlockShapes.HasCollision(w, x, y + 1, z)) return false;
        return !IsHazard(w, w.GetBlock(x, y, z)) && !IsHazard(w, w.GetBlock(x, y + 1, z)) && !IsHazard(w, w.GetBlock(x, y - 1, z));
    }

    /// <summary>The box overlaps a cell holding lava, fire or cactus (contact = damage).</summary>
    public static bool TouchesHazard(IBlockAccess w, AABB box)
    {
        var bb = box.Grow(-0.001, -0.001, -0.001);
        int x0 = (int)Math.Floor(bb.MinX), x1 = (int)Math.Floor(bb.MaxX);
        int y0 = (int)Math.Floor(bb.MinY), y1 = (int)Math.Floor(bb.MaxY);
        int z0 = (int)Math.Floor(bb.MinZ), z1 = (int)Math.Floor(bb.MaxZ);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
        {
            int id = w.GetBlock(x, y, z);
            if (id != 0 && (BlockShapes.IsLava(id) || id == BlockShapes.Fire || id == BlockShapes.Cactus)) return true;
        }
        return false;
    }

    /// <summary>Lava within Chebyshev distance <paramref name="r"/> of the feet or head cell of foot cell (x,y,z).</summary>
    public static bool LavaNear(IBlockAccess w, int x, int y, int z, int r = 1)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -r; dy <= r + 1; dy++)
        for (int dz = -r; dz <= r; dz++)
            if (BlockShapes.IsLava(w.GetBlock(x + dx, y + dy, z + dz))) return true;
        return false;
    }

    /// <summary>
    /// Whether removing block <paramref name="target"/> would leave a player at <paramref name="feet"/> with nothing but
    /// void below the hitbox (it is "the last block between the player and the void").
    /// </summary>
    public static bool RemovalLeavesOverVoid(IBlockAccess w, Vec3d feet, BlockPos target)
    {
        if (target.Y > feet.Y + 1e-6) return false;
        var overlay = new BlockOverlay(w);
        overlay.Set(target, 0);
        return IsPlayerOverVoid(overlay, feet) && !IsPlayerOverVoid(w, feet);
    }

    /// <summary>Blocks directly holding up a player at <paramref name="feet"/> (top layer under the hitbox).</summary>
    public static List<BlockPos> SupportBlocks(IBlockAccess w, Vec3d feet)
    {
        var box = AABB.ForPlayer(feet);
        int y = (int)Math.Floor(feet.Y - 0.01);
        var list = new List<BlockPos>();
        for (int x = (int)Math.Floor(box.MinX); x <= (int)Math.Ceiling(box.MaxX) - 1; x++)
        for (int z = (int)Math.Floor(box.MinZ); z <= (int)Math.Ceiling(box.MaxZ) - 1; z++)
            if (BlockShapes.HasCollision(w, x, y, z)) list.Add(new BlockPos(x, y, z));
        return list;
    }
    /// <summary>
    /// Blocks that hold up another player (a hive bot or a human): the top block layer under each tracked player's hitbox
    /// whose feet rest on it. Breaking one drops that player (live 10-27: main broke the crown leaves clone 67 stood on, a
    /// 6-block fall to hp 7). Hard safety: never dug, leaves included.
    /// </summary>
    public static HashSet<BlockPos> OtherPlayersSupport(WorldState w)
    {
        var set = new HashSet<BlockPos>();
        foreach (var e in w.Entities.All())
        {
            if (e.Kind != EntityKind.Player || e.Id == w.Entities.SelfId || e.Id == w.Player.EntityId) continue;
            var feet = e.Position;
            double frac = feet.Y - Math.Floor(feet.Y);
            int y = frac < 0.05 ? (int)Math.Floor(feet.Y) - 1 : (int)Math.Floor(feet.Y - 0.01);
            var box = AABB.ForPlayer(feet);
            for (int x = (int)Math.Floor(box.MinX); x <= (int)Math.Ceiling(box.MaxX) - 1; x++)
            for (int z = (int)Math.Floor(box.MinZ); z <= (int)Math.Ceiling(box.MaxZ) - 1; z++)
            {
                // Standing on it, or hanging just above it (a remote position lags a little).
                for (int yy = y; yy >= y - 1; yy--)
                    if (BlockShapes.HasCollision(w, x, yy, z)) { set.Add(new BlockPos(x, yy, z)); break; }
            }
        }
        return set;
    }

    /// <summary>
    /// Removing <paramref name="target"/> (one of the blocks I stand on) drops me onto solid footing at most
    /// <paramref name="maxDrop"/> below, never onto leaves, never over the void: the safe "mine down my own pillar" step.
    /// </summary>
    public static bool RemovalIsShortSafeDrop(IBlockAccess w, Vec3d feet, BlockPos target, double maxDrop = 1.0)
    {
        var overlay = new BlockOverlay(w);
        overlay.Set(target, 0);
        if (IsPlayerOverVoid(overlay, feet)) return false;
        var box = AABB.ForPlayer(feet);
        int fy = (int)Math.Floor(feet.Y - 0.01);
        double landing = double.MinValue;
        int landingId = 0;
        for (int x = (int)Math.Floor(box.MinX); x <= (int)Math.Ceiling(box.MaxX) - 1; x++)
        for (int z = (int)Math.Floor(box.MinZ); z <= (int)Math.Ceiling(box.MaxZ) - 1; z++)
        {
            int s = SupportBelow(overlay, x, fy, z);
            if (s < 0) continue;
            double top = s + BlockShapes.CollisionTop(overlay, x, s, z);
            if (top > landing) { landing = top; landingId = overlay.GetBlock(x, s, z); }
        }
        if (landing == double.MinValue) return false;
        if (landingId is Blocks.Leaves or 161) return false; // leaves / leaves2 (1.12.2)
        return feet.Y - landing <= maxDrop + 1e-6;
    }
}
