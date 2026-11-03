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
            if (DeepWater(w, x, y, z)) return false;
            if (minEdge > 1 && Safety.EdgeDistance(w, x, y, z, minEdge - 1) < minEdge) return false;
        }
        return true;
    }

    /// <summary>A one-block rise at <paramref name="cell"/> (my feet level) that a jump climbs: its collision top at most one
    /// block up (no fence or wall), two free cells over it, head room over me for the jump, and a safe walk on top.</summary>
    public static bool CanStepUp(IBlockAccess w, Vec3d from, BlockPos cell)
    {
        int y = cell.Y, mx = (int)Math.Floor(from.X), mz = (int)Math.Floor(from.Z);
        if (!BlockShapes.HasCollision(w, cell.X, y, cell.Z) || BlockShapes.CollisionTop(w, cell.X, y, cell.Z) > 1.0001) return false;
        if (BlockShapes.HasCollision(w, mx, y + 2, mz)) return false;
        return WalkSafe(w, from, cell with { Y = y + 1 });
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

    /// <summary>
    /// Can the bot walk (standable cells, steps of ±1, no gap jumps, no digging/placing) to a cell within
    /// <paramref name="reach"/> (horizontal, |dy| ≤ 2) of <paramref name="target"/>? A bounded flood fill (≤ <paramref name="maxNodes"/>
    /// cells, ≤ <paramref name="radius"/> from the bot): a creeper across a void gap or a skeleton on a ledge is unreachable
    /// (live 10-27/28: both held the body as a 0-hit fight). Water over the head isn't walking (live 10-29: a zombie chased under).
    /// </summary>
    public static bool CanWalkNear(IBlockAccess w, Vec3d me, Vec3d target, double reach, int radius = 16, int maxNodes = 600)
    {
        int sx = (int)Math.Floor(me.X), sy = (int)Math.Floor(me.Y + 1e-6), sz = (int)Math.Floor(me.Z);
        int ty = (int)Math.Floor(target.Y + 1e-6);
        bool Near(int x, int y, int z)
        {
            double dx = x + 0.5 - target.X, dz = z + 0.5 - target.Z;
            return dx * dx + dz * dz <= reach * reach && Math.Abs(y - ty) <= 2;
        }
        if (Near(sx, sy, sz)) return true;
        var seen = new HashSet<BlockPos> { new(sx, sy, sz) };
        var q = new Queue<BlockPos>();
        q.Enqueue(new BlockPos(sx, sy, sz));
        int[] dxs = [1, -1, 0, 0], dzs = [0, 0, 1, -1];
        while (q.Count > 0 && seen.Count < maxNodes)
        {
            var c = q.Dequeue();
            for (int i = 0; i < 4; i++)
            {
                int nx = c.X + dxs[i], nz = c.Z + dzs[i];
                if (Math.Abs(nx - sx) > radius || Math.Abs(nz - sz) > radius) continue;
                for (int dy = 1; dy >= -2; dy--)
                {
                    int ny = c.Y + dy;
                    var n = new BlockPos(nx, ny, nz);
                    if (seen.Contains(n) || !Safety.IsStandable(w, nx, ny, nz) || DeepWater(w, nx, ny, nz)) continue;
                    // A step up needs head room above the current cell; a drop needs the column above the landing clear.
                    if (dy == 1 && BlockShapes.HasCollision(w, c.X, c.Y + 2, c.Z)) continue;
                    if (dy < 0 && (BlockShapes.HasCollision(w, nx, c.Y, nz) || BlockShapes.HasCollision(w, nx, c.Y + 1, nz))) continue;
                    seen.Add(n);
                    if (Near(nx, ny, nz)) return true;
                    q.Enqueue(n);
                    break;
                }
            }
        }
        return false;
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

    /// <summary>
    /// The mob can get at a body standing at <paramref name="me"/> (feet): touching it, in sight of its eyes or feet (vanilla mobs
    /// only target what they see: EntityAINearestAttackableTarget's sight check, an archer's clear shot, a creeper's swell), or on
    /// about the same level within 8 with a walk to the bot (behind a tree or a corner, it walks round). A mob in a cave under the
    /// floor, on a ledge out of sight or in the next cave behind solid stone is no danger yet (live 10-29: a creeper below the edge
    /// west of the tunnel broke every job of four bodies; 10-30: zombies in a cave beside the stair shaft boxed main in again and
    /// again, Fox: "you're not being attacked, they're behind the wall").
    /// </summary>
    public static bool CanGetAt(IBlockAccess w, Entity mob, Vec3d me)
    {
        double dx = mob.Position.X - me.X, dy = mob.Position.Y - me.Y, dz = mob.Position.Z - me.Z, h2 = dx * dx + dz * dz;
        if (h2 + dy * dy <= 1.5 * 1.5) return true;
        var box = CombatRules.Hitbox(mob);
        var eye = new Vec3d(mob.Position.X, box.MinY + (box.MaxY - box.MinY) * 0.85, mob.Position.Z);
        if (HasLineOfSight(w, eye, me + new Vec3d(0, PlayerPhysics.EyeHeight, 0)) || HasLineOfSight(w, eye, me + new Vec3d(0, 0.3, 0))) return true;
        // Out of sight on our level: only if it can walk round to us (no digging, no gap jumps).
        return Math.Abs(dy) < 2 && h2 <= 8 * 8 && CanWalkNear(w, mob.Position, me, 1.5, radius: 10, maxNodes: 400);
    }

    /// <summary>Water at the feet and the head: standing there puts the head under (no fight footing, the air runs out).</summary>
    public static bool DeepWater(IBlockAccess w, int x, int y, int z) =>
        MineClaw.Data.Blocks.IsWater(w.GetBlock(x, y, z)) && MineClaw.Data.Blocks.IsWater(w.GetBlock(x, y + 1, z));

    /// <summary><see cref="DeepWater(IBlockAccess,int,int,int)"/> at an entity's feet.</summary>
    public static bool DeepWater(IBlockAccess w, Vec3d feet) =>
        DeepWater(w, (int)Math.Floor(feet.X), (int)Math.Floor(feet.Y + 1e-6), (int)Math.Floor(feet.Z));

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
