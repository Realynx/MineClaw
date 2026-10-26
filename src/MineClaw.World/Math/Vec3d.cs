namespace MineClaw.World;

/// <summary>Immutable double-precision 3D vector (entity positions, velocities, ray directions).</summary>
public readonly record struct Vec3d(double X, double Y, double Z)
{
    public static readonly Vec3d Zero = new(0, 0, 0);

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);

    public double LengthSquared => X * X + Y * Y + Z * Z;
    public double Length => Math.Sqrt(LengthSquared);
    public double HorizontalLength => Math.Sqrt(X * X + Z * Z);

    public Vec3d Normalized()
    {
        double l = Length;
        return l < 1e-12 ? Zero : new Vec3d(X / l, Y / l, Z / l);
    }

    public double Dot(Vec3d o) => X * o.X + Y * o.Y + Z * o.Z;
    public double DistanceTo(Vec3d o) => (this - o).Length;
    public double DistanceSquaredTo(Vec3d o) => (this - o).LengthSquared;

    /// <summary>The block containing this point.</summary>
    public BlockPos ToBlockPos() => new((int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Floor(Z));

    public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
}

/// <summary>Integer block coordinate.</summary>
public readonly record struct BlockPos(int X, int Y, int Z)
{
    public BlockPos Offset(int dx, int dy, int dz) => new(X + dx, Y + dy, Z + dz);
    public BlockPos Offset(BlockFace face) => this + face.Normal();
    public static BlockPos operator +(BlockPos a, BlockPos b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static BlockPos operator -(BlockPos a, BlockPos b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public BlockPos Up(int n = 1) => new(X, Y + n, Z);
    public BlockPos Down(int n = 1) => new(X, Y - n, Z);

    /// <summary>Center of the block.</summary>
    public Vec3d Center => new(X + 0.5, Y + 0.5, Z + 0.5);

    /// <summary>Center of the block's bottom face (feet position of an entity standing in this cell on a full block).</summary>
    public Vec3d BottomCenter => new(X + 0.5, Y, Z + 0.5);

    public double DistanceSquaredTo(BlockPos o)
    {
        double dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    public int ManhattanTo(BlockPos o) => Math.Abs(X - o.X) + Math.Abs(Y - o.Y) + Math.Abs(Z - o.Z);

    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>Block faces with b1.8.1 protocol numbering (0x0E dig / 0x0F place "direction").</summary>
public enum BlockFace : sbyte
{
    None = -1,
    Bottom = 0, // -Y
    Top = 1,    // +Y
    North = 2,  // -Z
    South = 3,  // +Z
    West = 4,   // -X
    East = 5,   // +X
}

public static class BlockFaceExtensions
{
    public static readonly BlockFace[] All =
        [BlockFace.Bottom, BlockFace.Top, BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East];

    public static BlockPos Normal(this BlockFace f) => f switch
    {
        BlockFace.Bottom => new(0, -1, 0),
        BlockFace.Top => new(0, 1, 0),
        BlockFace.North => new(0, 0, -1),
        BlockFace.South => new(0, 0, 1),
        BlockFace.West => new(-1, 0, 0),
        BlockFace.East => new(1, 0, 0),
        _ => new(0, 0, 0),
    };

    public static BlockFace Opposite(this BlockFace f) => f switch
    {
        BlockFace.Bottom => BlockFace.Top,
        BlockFace.Top => BlockFace.Bottom,
        BlockFace.North => BlockFace.South,
        BlockFace.South => BlockFace.North,
        BlockFace.West => BlockFace.East,
        BlockFace.East => BlockFace.West,
        _ => BlockFace.None,
    };

    /// <summary>Face whose normal equals the given unit offset, or None.</summary>
    public static BlockFace FromOffset(int dx, int dy, int dz) => (dx, dy, dz) switch
    {
        (0, -1, 0) => BlockFace.Bottom,
        (0, 1, 0) => BlockFace.Top,
        (0, 0, -1) => BlockFace.North,
        (0, 0, 1) => BlockFace.South,
        (-1, 0, 0) => BlockFace.West,
        (1, 0, 0) => BlockFace.East,
        _ => BlockFace.None,
    };
}
