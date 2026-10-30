namespace MineClaw.Render;

/// <summary>
/// Which chest cells draw as one large chest, for the renderers (live 10-29, Fox: "chests don't combine into one big chest when
/// they're placed next to each other, so the bot can't tell": every 1.12.2 chest was its own 14×14×14 box, so a pair read as two
/// singles in every iso/orbit panel and the freecam). The pairing is <see cref="MineClaw.World.ChestPairs"/>'s rule, the one the
/// bot's chest memory, windows and claims use, read here over <see cref="IVoxelWorld"/> so the picture and the knowledge always
/// agree: a chest pairs with its ONE horizontally touching block of the same kind (chest 54 with 54, trapped 146 with 146; the
/// ender chest 130 never pairs), never stacked, never diagonal; a block with two same-kind neighbours (a layout vanilla placement
/// can't build) and its neighbours read as singles. Ported in our own words from the 1.12.2 client (TileEntityChest's adjacent
/// check, BlockChest.checkForSurroundingChests, TileEntityChestRenderer):
/// <list type="bullet">
/// <item>the pair faces the way its west/north half (the renderer's "master": no chest to its west or north) faces; a facing
/// along the pair's axis (a world edit) is turned the way BlockChest would: the partner's facing when that one is across,
/// else south for a pair along x and east for a pair along z;</item>
/// <item>each cell draws its half of the 30-wide large-chest model (<see cref="ModelHalf"/>), lid and latch across the seam.</item>
/// </list>
/// </summary>
public static class LargeChest
{
    /// <summary>Chest kinds that pair (b1.8.1 and 1.12.2 chest 54, 1.12.2 trapped chest 146).</summary>
    public static bool Pairs(int id) => MineClaw.World.ChestPairs.IsPairable(id);

    private static readonly (int Dx, int Dz)[] Sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    /// <summary>The partner of the chest at (x, y, z) as (dx, dz), or null for a single (or not a pairing chest).</summary>
    public static (int Dx, int Dz)? Partner(IVoxelWorld w, int x, int y, int z)
    {
        int id = w.Block(x, y, z);
        if (!Pairs(id) || Only(w, x, y, z, id) is not { } o) return null;
        return Only(w, x + o.Dx, y, z + o.Dz, id) == (-o.Dx, -o.Dz) ? o : null;
    }

    /// <summary>The one same-kind horizontal neighbour; null for none or several.</summary>
    private static (int Dx, int Dz)? Only(IVoxelWorld w, int x, int y, int z, int id)
    {
        (int, int)? found = null;
        foreach (var (dx, dz) in Sides)
        {
            if (w.Block(x + dx, y, z + dz) != id) continue;
            if (found != null) return null;
            found = (dx, dz);
        }
        return found;
    }

    /// <summary>
    /// The facing (EnumFacing index 2..5) both halves of a pair draw with: the west/north half's, unless it points along the
    /// pair, then the other half's when that one is across, else south (pair along x) / east (pair along z).
    /// </summary>
    public static int PairFacing(IVoxelWorld w, int x, int y, int z, (int Dx, int Dz) partner)
    {
        bool alongX = partner.Dx != 0;
        bool masterHere = partner.Dx > 0 || partner.Dz > 0;
        int mf = masterHere ? w.Meta(x, y, z) & 7 : w.Meta(x + partner.Dx, y, z + partner.Dz) & 7;
        int of = masterHere ? w.Meta(x + partner.Dx, y, z + partner.Dz) & 7 : w.Meta(x, y, z) & 7;
        bool Across(int f) => alongX ? f is 2 or 3 : f is 4 or 5;
        if (Across(mf)) return mf;
        if (Across(of)) return of;
        return alongX ? 3 : 5;
    }

    /// <summary>
    /// The half of the north-facing large-chest model this cell is (the blockstate rotates it to the pair's facing): "west"
    /// when its partner is on the model's east, else "east". Quarter turns clockwise from north: N 0, E 1, S 2, W 3.
    /// </summary>
    public static string ModelHalf(int facing, (int Dx, int Dz) partner)
    {
        int rot = facing switch { 5 => 1, 3 => 2, 4 => 3, _ => 0 };
        // Rotate the world direction counter-clockwise by rot quarter turns: (dx, dz) → (dz, −dx) per turn.
        int dx = partner.Dx, dz = partner.Dz;
        for (int i = 0; i < rot; i++) (dx, dz) = (dz, -dx);
        return dx > 0 ? "west" : "east";
    }
}
