namespace MineClaw.World;

/// <summary>Ore identity and face connectivity shared by mining execution and mine-layout estimates.</summary>
public static class OreIdentity
{
    /// <summary>Six face neighbors; corner-only and edge-only contact is not a connected ore vein.</summary>
    public static IReadOnlyList<(int Dx, int Dy, int Dz)> Neighbors { get; } = Array.AsReadOnly<(int, int, int)>(
        [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)]);

    /// <summary>The ore's stable block id, or zero for a non-ore/unsupported block. Lit redstone remains redstone.</summary>
    public static int Canonical(int id, bool modern) => id switch
    {
        14 or 15 or 16 or 21 or 56 or 73 => id,
        74 => 73,
        129 or 153 when modern => id,
        _ => 0,
    };

    /// <summary>
    /// Whether removing this block has no known adjacent liquid or falling block immediately above it. Unknown neighbors
    /// are unsafe. This is only an exposure guard: callers also check ownership, access support, reach and pawn safety.
    /// </summary>
    public static bool ExposureSafe(IBlockAccess world, BlockPos at)
    {
        if (at.Y < 1 || at.Y >= world.WorldHeight || !world.IsLoaded(at.X, at.Z)) return false;
        foreach (var (dx, dy, dz) in Neighbors)
        {
            int x = at.X + dx, y = at.Y + dy, z = at.Z + dz;
            if (y < 0 || y >= world.WorldHeight || !world.IsLoaded(x, z)) return false;
            int id = world.GetBlock(x, y, z);
            if (id is 8 or 9 or 10 or 11 || dy == 1 && id is 12 or 13 or 145) return false;
        }
        return true;
    }
}
