using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>
/// Skyblock positioning for fights. Knockback (EntityLiving.knockBack) pushes the victim directly away from the
/// attacker, so the rule is: the ground behind the bot — on the far side from the threat — must hold for
/// <c>depth</c> blocks; i.e. the bot fights with its back to the island interior. Pure functions over
/// <see cref="IBlockAccess"/>; the always-on EdgeGuard still filters every resulting input.
/// </summary>
public static class CombatGeometry
{
    private static readonly double[] Spread = [0, 35 * Math.PI / 180, -35 * Math.PI / 180];

    /// <summary>A deadly drop (void, lava, &gt;3 fall) within <paramref name="depth"/> blocks behind <paramref name="me"/>
    /// in the knockback direction from <paramref name="threat"/> (±35°).</summary>
    public static bool EdgeBehind(IBlockAccess w, Vec3d me, Vec3d threat, double depth, double maxFall = 3)
    {
        double dx = me.X - threat.X, dz = me.Z - threat.Z, len = Math.Sqrt(dx * dx + dz * dz);
        int feetY = (int)Math.Floor(me.Y + 1e-6);
        if (len < 1e-3)
            return Safety.EdgeDistance(w, (int)Math.Floor(me.X), feetY, (int)Math.Floor(me.Z), (int)Math.Ceiling(depth), maxFall) <= depth;
        dx /= len;
        dz /= len;
        foreach (var a in Spread)
        {
            double cx = dx * Math.Cos(a) - dz * Math.Sin(a), cz = dx * Math.Sin(a) + dz * Math.Cos(a);
            for (double t = 0.5; t <= depth + 1e-9; t += 0.5)
                if (Safety.IsDeadlyColumn(w, (int)Math.Floor(me.X + cx * t), feetY, (int)Math.Floor(me.Z + cz * t), maxFall)) return true;
        }
        return false;
    }

    /// <summary>Straight walk from <paramref name="from"/> to the center of <paramref name="to"/> crosses only safe
    /// columns at least <paramref name="minEdge"/> from a drop (sampled every 0.25 blocks).</summary>
    public static bool WalkSafe(IBlockAccess w, Vec3d from, BlockPos to, int minEdge = 1)
    {
        double tx = to.X + 0.5, tz = to.Z + 0.5, dx = tx - from.X, dz = tz - from.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(1, (int)Math.Ceiling(len / 0.25));
        int y = to.Y;
        for (int i = 1; i <= steps; i++)
        {
            int x = (int)Math.Floor(from.X + dx * i / steps), z = (int)Math.Floor(from.Z + dz * i / steps);
            if (Safety.IsDeadlyColumn(w, x, y, z, 3)) return false;
            if (BlockShapes.HasCollision(w, x, y, z) || BlockShapes.HasCollision(w, x, y + 1, z)) return false;
            if (minEdge > 1 && Safety.EdgeDistance(w, x, y, z, minEdge - 1) < minEdge) return false;
        }
        return true;
    }

    /// <summary>
    /// Where to stand against <paramref name="threat"/>: a standable cell within <paramref name="radius"/>, reachable
    /// by a safe straight walk, with no edge behind it relative to the threat. Prefers the island interior, then short
    /// moves, and keeps <paramref name="keepAway"/> from the threat (let it come). Null when nothing qualifies.
    /// </summary>
    public static BlockPos? ChooseStance(IBlockAccess w, Vec3d me, Vec3d threat, double depth, int radius = 5, double keepAway = 1.5)
    {
        int y = (int)Math.Floor(me.Y + 1e-6);
        int cx = (int)Math.Floor(me.X), cz = (int)Math.Floor(me.Z);
        BlockPos? best = null;
        double bestScore = double.MinValue;
        for (int x = cx - radius; x <= cx + radius; x++)
        for (int z = cz - radius; z <= cz + radius; z++)
        {
            var c = new Vec3d(x + 0.5, y, z + 0.5);
            double d = Math.Sqrt((c.X - me.X) * (c.X - me.X) + (c.Z - me.Z) * (c.Z - me.Z));
            if (d > radius + 0.5 || !Safety.IsStandable(w, x, y, z)) continue;
            double toThreat = Math.Sqrt((c.X - threat.X) * (c.X - threat.X) + (c.Z - threat.Z) * (c.Z - threat.Z));
            if (toThreat < keepAway) continue;
            if (EdgeBehind(w, c, threat, depth)) continue;
            if (!WalkSafe(w, me, new BlockPos(x, y, z))) continue;
            double score = Math.Min(Safety.EdgeDistance(w, x, y, z, 5), 5) * 3 - d * 1.5;
            if (score > bestScore) { bestScore = score; best = new BlockPos(x, y, z); }
        }
        return best;
    }

    /// <summary>A cell to flee to: away from every threat (beyond <paramref name="minDistance"/> if possible), inward,
    /// reachable by a safe walk.</summary>
    public static BlockPos? RetreatCell(IBlockAccess w, Vec3d me, IReadOnlyList<Vec3d> threats, int radius = 7, double minDistance = 0)
    {
        int y = (int)Math.Floor(me.Y + 1e-6);
        int cx = (int)Math.Floor(me.X), cz = (int)Math.Floor(me.Z);
        BlockPos? best = null;
        double bestScore = double.MinValue;
        for (int x = cx - radius; x <= cx + radius; x++)
        for (int z = cz - radius; z <= cz + radius; z++)
        {
            if (!Safety.IsStandable(w, x, y, z)) continue;
            var c = new Vec3d(x + 0.5, y, z + 0.5);
            double near = threats.Count == 0 ? radius : threats.Min(t => Math.Sqrt((c.X - t.X) * (c.X - t.X) + (c.Z - t.Z) * (c.Z - t.Z)));
            int edge = Safety.EdgeDistance(w, x, y, z, 4);
            if (edge < 2) continue;
            if (!WalkSafe(w, me, new BlockPos(x, y, z))) continue;
            double score = Math.Min(near, Math.Max(minDistance, 1) + 4) * 2 + edge * 1.5 + (near >= minDistance ? 10 : 0);
            if (score > bestScore) { bestScore = score; best = new BlockPos(x, y, z); }
        }
        return best;
    }

    /// <summary>Nearest safe cell (edge distance ≥ 2) where a block hides the bot's eyes from <paramref name="shooterEye"/>.</summary>
    public static BlockPos? CoverCell(IBlockAccess w, Vec3d me, Vec3d shooterEye, int radius = 5)
    {
        int y = (int)Math.Floor(me.Y + 1e-6);
        int cx = (int)Math.Floor(me.X), cz = (int)Math.Floor(me.Z);
        BlockPos? best = null;
        double bestD = double.MaxValue;
        for (int x = cx - radius; x <= cx + radius; x++)
        for (int z = cz - radius; z <= cz + radius; z++)
        {
            if (!Safety.IsStandable(w, x, y, z) || Safety.EdgeDistance(w, x, y, z, 2) < 2) continue;
            var eye = new Vec3d(x + 0.5, y + PlayerPhysics.EyeHeight, z + 0.5);
            if (HasLineOfSight(w, shooterEye, eye)) continue;
            double d = (x + 0.5 - me.X) * (x + 0.5 - me.X) + (z + 0.5 - me.Z) * (z + 0.5 - me.Z);
            if (d >= bestD || !WalkSafe(w, me, new BlockPos(x, y, z))) continue;
            bestD = d;
            best = new BlockPos(x, y, z);
        }
        return best;
    }

    /// <summary>No block selection box between the two points (EntityLiving.canEntityBeSeen uses a block ray trace).</summary>
    public static bool HasLineOfSight(IBlockAccess w, Vec3d from, Vec3d to)
    {
        var d = to - from;
        double len = d.Length;
        if (len < 1e-6) return true;
        var hit = Raycast.Cast(w, from, d, len);
        return hit is null || hit.Value.Distance >= len - 0.05;
    }

    /// <summary>
    /// Input that moves toward <paramref name="dest"/> (horizontal) while looking at (<paramref name="yaw"/>,
    /// <paramref name="pitch"/>): forward/strafe are solved from vanilla Entity.moveFlying
    /// (vx = s·cos − f·sin, vz = f·cos + s·sin), so the bot can back-pedal or circle while facing its target.
    /// </summary>
    public static MoveInput SteerTo(Vec3d me, Vec3d dest, float yaw, float pitch, bool sneak = false, double stopWithin = 0.2)
    {
        double dx = dest.X - me.X, dz = dest.Z - me.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < stopWithin) return new MoveInput(Yaw: yaw, Pitch: pitch, Sneak: sneak);
        dx /= len;
        dz /= len;
        double r = yaw * Math.PI / 180, sin = Math.Sin(r), cos = Math.Cos(r);
        double f = dz * cos - dx * sin, s = dx * cos + dz * sin;
        double scale = len < 0.8 ? Math.Max(0.35, len / 0.8) : 1; // ease in, don't overshoot the cell
        return new MoveInput(Forward: (float)(f * scale), Strafe: (float)(s * scale), Yaw: yaw, Pitch: pitch, Sneak: sneak);
    }
}
