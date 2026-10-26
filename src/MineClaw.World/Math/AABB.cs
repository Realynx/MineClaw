namespace MineClaw.World;

/// <summary>Axis-aligned bounding box, port of b1.8.1 AxisAlignedBB semantics (strict-overlap intersection, swept offsets).</summary>
public readonly record struct AABB(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ)
{
    /// <summary>Player hitbox: 0.6 wide, 1.8 tall, centered on the feet position.</summary>
    public static AABB ForPlayer(Vec3d feet) => ForEntity(feet, PlayerPhysics.Width, PlayerPhysics.Height);

    public static AABB ForEntity(Vec3d feet, double width, double height)
    {
        double h = width / 2;
        return new AABB(feet.X - h, feet.Y, feet.Z - h, feet.X + h, feet.Y + height, feet.Z + h);
    }

    public static AABB Block(int x, int y, int z) => new(x, y, z, x + 1, y + 1, z + 1);

    public AABB Offset(double dx, double dy, double dz) =>
        new(MinX + dx, MinY + dy, MinZ + dz, MaxX + dx, MaxY + dy, MaxZ + dz);

    /// <summary>Extends the box in the direction of the motion (AxisAlignedBB.addCoord).</summary>
    public AABB Expand(double dx, double dy, double dz) => new(
        dx < 0 ? MinX + dx : MinX, dy < 0 ? MinY + dy : MinY, dz < 0 ? MinZ + dz : MinZ,
        dx > 0 ? MaxX + dx : MaxX, dy > 0 ? MaxY + dy : MaxY, dz > 0 ? MaxZ + dz : MaxZ);

    /// <summary>Grows (or shrinks with negatives) symmetrically on each axis (AxisAlignedBB.expand).</summary>
    public AABB Grow(double x, double y, double z) =>
        new(MinX - x, MinY - y, MinZ - z, MaxX + x, MaxY + y, MaxZ + z);

    public bool Intersects(AABB o) =>
        o.MaxX > MinX && o.MinX < MaxX && o.MaxY > MinY && o.MinY < MaxY && o.MaxZ > MinZ && o.MinZ < MaxZ;

    public bool Contains(Vec3d p) =>
        p.X > MinX && p.X < MaxX && p.Y > MinY && p.Y < MaxY && p.Z > MinZ && p.Z < MaxZ;

    public Vec3d Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2, (MinZ + MaxZ) / 2);

    /// <summary>Clips movement dx of <paramref name="moving"/> against this box (AxisAlignedBB.calculateXOffset).</summary>
    public double CalculateXOffset(AABB moving, double dx)
    {
        if (moving.MaxY <= MinY || moving.MinY >= MaxY || moving.MaxZ <= MinZ || moving.MinZ >= MaxZ) return dx;
        if (dx > 0 && moving.MaxX <= MinX) { double d = MinX - moving.MaxX; if (d < dx) dx = d; }
        if (dx < 0 && moving.MinX >= MaxX) { double d = MaxX - moving.MinX; if (d > dx) dx = d; }
        return dx;
    }

    public double CalculateYOffset(AABB moving, double dy)
    {
        if (moving.MaxX <= MinX || moving.MinX >= MaxX || moving.MaxZ <= MinZ || moving.MinZ >= MaxZ) return dy;
        if (dy > 0 && moving.MaxY <= MinY) { double d = MinY - moving.MaxY; if (d < dy) dy = d; }
        if (dy < 0 && moving.MinY >= MaxY) { double d = MaxY - moving.MinY; if (d > dy) dy = d; }
        return dy;
    }

    public double CalculateZOffset(AABB moving, double dz)
    {
        if (moving.MaxX <= MinX || moving.MinX >= MaxX || moving.MaxY <= MinY || moving.MinY >= MaxY) return dz;
        if (dz > 0 && moving.MaxZ <= MinZ) { double d = MinZ - moving.MaxZ; if (d < dz) dz = d; }
        if (dz < 0 && moving.MinZ >= MaxZ) { double d = MaxZ - moving.MinZ; if (d > dz) dz = d; }
        return dz;
    }

    /// <summary>Ray/box intersection (slab method). Returns entry distance t (≥0) along <paramref name="dir"/> and the face entered, or null
    /// (also null when the origin is inside the box).</summary>
    public (double T, BlockFace Face)? RayIntersect(Vec3d origin, Vec3d dir, double maxT)
    {
        double tMin = 0, tMax = maxT;
        BlockFace face = BlockFace.None;
        if (!Slab(origin.X, dir.X, MinX, MaxX, BlockFace.West, BlockFace.East, ref tMin, ref tMax, ref face)) return null;
        if (!Slab(origin.Y, dir.Y, MinY, MaxY, BlockFace.Bottom, BlockFace.Top, ref tMin, ref tMax, ref face)) return null;
        if (!Slab(origin.Z, dir.Z, MinZ, MaxZ, BlockFace.North, BlockFace.South, ref tMin, ref tMax, ref face)) return null;
        if (face == BlockFace.None) return null;
        return (tMin, face);
    }

    private static bool Slab(double o, double d, double min, double max, BlockFace negFace, BlockFace posFace,
        ref double tMin, ref double tMax, ref BlockFace face)
    {
        if (Math.Abs(d) < 1e-12) return o >= min && o <= max;
        double t1 = (min - o) / d, t2 = (max - o) / d;
        BlockFace f1 = negFace; // moving +: enter through the min plane (the "negative" face)
        if (t1 > t2) { (t1, t2) = (t2, t1); f1 = posFace; }
        if (t1 > tMin) { tMin = t1; face = f1; }
        if (t2 < tMax) tMax = t2;
        return tMin <= tMax;
    }

    public override string ToString() =>
        $"[{MinX:0.###},{MinY:0.###},{MinZ:0.###} -> {MaxX:0.###},{MaxY:0.###},{MaxZ:0.###}]";
}
