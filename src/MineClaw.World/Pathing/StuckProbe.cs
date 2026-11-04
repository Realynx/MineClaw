namespace MineClaw.World;

/// <summary>
/// Why a body is stuck and which way it can still move: the collision boxes it runs into nudging toward a step target (with
/// the 0.5 step-up the physics would try), and the cardinal directions it can move in from where it stands. Used by the goto
/// loop's "stuck N times at the same spot" escape (live 10-28: three bots stuck at 7.5,64.5,6.3 stepping north off the gen
/// slab with a leaf block at head height).
/// </summary>
public static class StuckProbe
{
    /// <summary>A block in the way and whether it is at head height (above the feet + step height) or at the feet.</summary>
    public readonly record struct Obstruction(BlockPos Pos, bool Head);

    /// <summary>Blocks the body at <paramref name="feet"/> collides with when moved <paramref name="probe"/> blocks toward
    /// <paramref name="target"/> (horizontal). A box the body could step up onto (top ≤ feet + 0.5) is not in the way by
    /// itself; the head is checked at that stepped-up height too.</summary>
    public static IReadOnlyList<Obstruction> Obstructions(IBlockAccess w, Vec3d feet, Vec3d target, double probe = 0.35)
    {
        double dx = target.X - feet.X, dz = target.Z - feet.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6) return [];
        return Probe(w, feet, dx / len * probe, dz / len * probe);
    }

    /// <summary>Cardinal unit directions (dx, dz) the body can move <paramref name="probe"/> blocks toward without hitting
    /// anything (step-ups ≤ 0.5 allowed), nearest to <paramref name="prefer"/> first.</summary>
    public static IReadOnlyList<(int Dx, int Dz)> FreeDirections(IBlockAccess w, Vec3d feet, Vec3d? prefer = null, double probe = 0.45)
    {
        var dirs = new List<(int, int)>();
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            if (Probe(w, feet, dx * probe, dz * probe).Count == 0) dirs.Add((dx, dz));
        if (prefer is { } p)
            dirs.Sort((a, b) => Dist(feet, a, p).CompareTo(Dist(feet, b, p)));
        return dirs;
    }

    private static double Dist(Vec3d feet, (int Dx, int Dz) d, Vec3d p)
    {
        double x = feet.X + d.Dx - p.X, z = feet.Z + d.Dz - p.Z;
        return x * x + z * z;
    }

    private static List<Obstruction> Probe(IBlockAccess w, Vec3d feet, double ox, double oz)
    {
        var here = AABB.ForPlayer(feet);
        var moved = here.Offset(ox, 0, oz);
        var raised = moved.Offset(0, w.Physics.StepHeight, 0);
        var found = new List<Obstruction>();
        var boxes = new List<AABB>(4);
        int x0 = (int)Math.Floor(moved.MinX), x1 = (int)Math.Floor(moved.MaxX - 1e-9);
        int z0 = (int)Math.Floor(moved.MinZ), z1 = (int)Math.Floor(moved.MaxZ - 1e-9);
        int y0 = (int)Math.Floor(moved.MinY) - 1, y1 = (int)Math.Floor(raised.MaxY - 1e-9);
        for (int x = x0; x <= x1; x++)
        for (int z = z0; z <= z1; z++)
        for (int y = Math.Max(0, y0); y <= Math.Min(w.WorldHeight - 1, y1); y++)
        {
            int id = w.GetBlock(x, y, z);
            if (id == 0) continue;
            boxes.Clear();
            BlockShapes.AddCollisionBoxes(w, id, w.GetMeta(x, y, z), x, y, z, boxes);
            if (InTheWay(w, boxes, feet, here, moved, raised, out bool head)) found.Add(new Obstruction(new BlockPos(x, y, z), head));
        }
        return found;
    }

    /// <summary>A block's boxes stop the move: one the moved body hits above a step, or one the stepped-up body hits; at head
    /// height when it reaches a block above the feet (or only the stepped-up body hits it).</summary>
    private static bool InTheWay(IBlockAccess w, List<AABB> boxes, Vec3d feet, AABB here, AABB moved, AABB raised, out bool head)
    {
        head = false;
        bool block = false;
        foreach (var b in boxes)
        {
            if (b.Intersects(here)) continue; // already touching/inside it: not what stops this move
            if (b.Intersects(moved) && b.MaxY > feet.Y + w.Physics.StepHeight + 1e-6) { block = true; head |= b.MinY >= feet.Y + 1 - 1e-6; }
            else if (b.Intersects(raised) && !b.Intersects(moved)) { block = true; head = true; }
        }
        return block;
    }
}
