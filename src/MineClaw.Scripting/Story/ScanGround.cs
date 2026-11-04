using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Scripting.Story;

/// <summary>
/// The cell tests <see cref="PitScan"/> and <see cref="ReachScan"/> share: liquid, solid (collides), free (room for a body: not solid,
/// no liquid, no fire/cactus/web), standable, and the pillar blocks the body carries.
/// </summary>
internal class ScanGround(WorldState w, IGameData data)
{
    /// <summary>Sand, gravel, dragon egg, anvil, concrete powder: they fall when the block under them goes.</summary>
    public static readonly HashSet<int> Falling = [12, 13, 122, 145, 252];

    /// <summary>The five cells beside and over a cell (a liquid there floods it once it's dug).</summary>
    private static readonly (int Dx, int Dy, int Dz)[] SidesAndTop = [(1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0)];

    private readonly int height = w.WorldHeight;

    public WorldState World => w;
    public IGameData Data => data;
    public int Height => height;

    public bool Liquid(int x, int y, int z) => w.GetBlock(x, y, z) is >= 8 and <= 11;

    public bool Lava(int x, int y, int z) => w.GetBlock(x, y, z) is 10 or 11;

    public bool Solid(int x, int y, int z) => y >= 0 && y < height && BlockShapes.HasCollision(w, x, y, z);

    public bool Free(int x, int y, int z) =>
        y >= 0 && y < height && !Solid(x, y, z) && !Liquid(x, y, z) && w.GetBlock(x, y, z) is not (51 or 81 or 30);

    public bool Stand(int x, int y, int z) => Solid(x, y - 1, z) && !Liquid(x, y - 1, z) && Free(x, y, z) && Free(x, y + 1, z);

    /// <summary>Liquid beside or over the cell.</summary>
    public bool LiquidAround(int x, int y, int z)
    {
        foreach (var (dx, dy, dz) in SidesAndTop)
            if (Liquid(x + dx, y + dy, z + dz)) return true;
        return false;
    }

    /// <summary>How many of <paramref name="ids"/> the body carries, and the name of the first one it has (the block it pillars with).</summary>
    public (string? Block, int Blocks) Stock(int[] ids)
    {
        int blocks = 0;
        string? block = null;
        foreach (int id in ids)
        {
            if (!data.ItemExists(id)) continue;
            int n = w.Inventory.CountItem(id);
            if (n <= 0) continue;
            blocks += n;
            block ??= data.Item(id).Name;
        }
        return (block, blocks);
    }

    public static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
}
