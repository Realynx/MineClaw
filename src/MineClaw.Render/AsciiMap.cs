using System.Globalization;
using System.Text;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>
/// The cheap text map behind <c>look {map:true}</c>: a top-down grid, one character per column at the bot's feet level
/// (walls, doors, containers) over what is underfoot (floor, lower ground, void), x coordinates on top and z on the left
/// every 5 blocks, then a one-line legend. ~15×15 ≈ 130 tokens, 25×25 ≈ 300.
/// </summary>
public static class AsciiMap
{
    public const string Legend = "@you #wall .floor =step ,lower _deep ' 'void ?unknown ~water !lava C chest F furnace W bench D door T log *leaves o torch/plant";

    /// <summary>Glyph of one column at feet level <paramref name="y"/>.</summary>
    public static char Cell(IBlockAccess w, int x, int y, int z)
    {
        if (!w.IsLoaded(x, z)) return '?';
        int feet = w.GetBlock(x, y, z);
        if (feet != 0 && Obstacle(feet) is { } o) return o;
        return Ground(w, x, y, z, deco: feet != 0);
    }

    /// <summary>Feet-level blocks with their own glyph (other solid blocks are walls).</summary>
    private static readonly Dictionary<int, char> Obstacles = new()
    {
        [Blocks.Chest] = 'C', [Blocks.LockedChest] = 'C', [Blocks.Furnace] = 'F', [Blocks.LitFurnace] = 'F', [Blocks.Workbench] = 'W',
        [Blocks.WoodenDoor] = 'D', [Blocks.IronDoor] = 'D', [Blocks.Log] = 'T', [Blocks.Leaves] = '*', [Blocks.Water] = '~', [Blocks.FlowingWater] = '~',
        [Blocks.Lava] = '!', [Blocks.FlowingLava] = '!', [Blocks.Slab] = '=', [Blocks.SnowLayer] = '=', [Blocks.Farmland] = '=',
    };

    /// <summary>The glyph of a feet-level block, null when it is see-through (look at the ground).</summary>
    private static char? Obstacle(int feet) => Obstacles.TryGetValue(feet, out var g) ? g : Blocks.Get(feet).Solid ? '#' : null;

    /// <summary>What is underfoot: fluid, floor (o with a plant/torch on it), lower ground, deep ground or void.</summary>
    private static char Ground(IBlockAccess w, int x, int y, int z, bool deco)
    {
        int under = w.GetBlock(x, y - 1, z);
        if (Blocks.IsWater(under)) return '~';
        if (Blocks.IsLava(under)) return '!';
        if (under != 0 && (Blocks.Get(under).Solid || under is Blocks.Slab or Blocks.Farmland)) return deco ? 'o' : '.';
        for (int d = 2; d <= 4; d++) if (w.GetBlock(x, y - d, z) != 0) return ',';
        for (int yy = y - 5; yy >= 0; yy--) if (w.GetBlock(x, yy, z) != 0) return '_';
        return ' ';
    }

    /// <summary>
    /// Role letter of a column (vision v2): the feet block's role, else the role of the block below it (the floor). Only roles
    /// with a glyph (G gen cell, P portal, X never-break, B player build, + own placed, $ protected) replace the block glyph.
    /// </summary>
    public static char? RoleGlyph(IBlockAccess w, RoleMap roles, int x, int y, int z, out BlockRole role)
    {
        role = BlockRole.None;
        if (!w.IsLoaded(x, z)) return null;
        try
        {
            role = roles(x, y, z);
            if (BlockRoles.Glyph(role) is null && (w.GetBlock(x, y, z) == 0 || role == BlockRole.None)) role = roles(x, y - 1, z);
        }
        catch (Exception) { role = BlockRole.None; }
        return BlockRoles.Glyph(role);
    }

    /// <summary>Renders the map around (cx, cy, cz). Entities: players by lower-case initial, hive bots by their glyph,
    /// mobs 'm'. With <paramref name="roles"/>, role letters replace block glyphs (and their legend is appended).
    /// Returns the text (header, x ruler, rows, legend + who is who).</summary>
    public static string Render(IBlockAccess w, int cx, int cy, int cz, int size = 15, (int X, int Y, int Z)? self = null,
        IReadOnlyList<VisionEntity>? entities = null, IReadOnlyList<VisionMark>? marks = null, RoleMap? roles = null)
    {
        size = Math.Clamp(size | 1, 5, 25);
        int r = size / 2, x0 = cx - r, z0 = cz - r;
        var present = new HashSet<BlockRole>();
        var grid = Grid(w, x0, cy, z0, size, roles, present);
        var who = PlaceEntities(grid, entities ?? [], x0, z0, size);
        if (self is { } me && (uint)(me.X - x0) < (uint)size && (uint)(me.Z - z0) < (uint)size) grid[me.Z - z0, me.X - x0] = '@';

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"map {size}x{size} x{x0}..{x0 + size - 1} z{z0}..{z0 + size - 1} at feet y{cy}, N up, 1 char=1 block\n");
        AppendRows(sb, grid, x0, z0, size);
        sb.Append(Legend);
        if (present.Count > 0) sb.Append("; roles: ").Append(BlockRoles.Legend(present));
        if (who.Count > 0) sb.Append("; ").Append(string.Join(' ', who));
        var inView = (marks ?? []).Where(m => m.MaxX >= x0 && m.MinX < x0 + size && m.MaxZ >= z0 && m.MinZ < z0 + size).Take(6).ToList();
        if (inView.Count > 0)
            sb.Append("; marks: ").Append(string.Join("; ", inView.Select(m =>
                $"{VisionRenderer.ShortLabel(m.Text)} {Rng(m.MinX, m.MaxX)},{Rng(m.MinY, m.MaxY)},{Rng(m.MinZ, m.MaxZ)}")));
        return sb.ToString();
    }

    /// <summary>The block glyphs, role letters over them (the roles shown go to <paramref name="present"/>).</summary>
    private static char[,] Grid(IBlockAccess w, int x0, int cy, int z0, int size, RoleMap? roles, HashSet<BlockRole> present)
    {
        var grid = new char[size, size];
        for (int iz = 0; iz < size; iz++)
            for (int ix = 0; ix < size; ix++)
            {
                grid[iz, ix] = Cell(w, x0 + ix, cy, z0 + iz);
                if (roles != null && RoleGlyph(w, roles, x0 + ix, cy, z0 + iz, out var role) is { } g) { grid[iz, ix] = g; present.Add(role); }
            }
        return grid;
    }

    /// <summary>Entities on the grid (players by initial, mobs 'm'); returns "glyph=name" for the players and bots.</summary>
    private static List<string> PlaceEntities(char[,] grid, IReadOnlyList<VisionEntity> entities, int x0, int z0, int size)
    {
        var who = new List<string>();
        foreach (var e in entities)
        {
            int ix = (int)Math.Floor(e.X) - x0, iz = (int)Math.Floor(e.Z) - z0;
            if ((uint)ix >= (uint)size || (uint)iz >= (uint)size) continue;
            char g = e.Kind switch
            {
                "mob" => 'm',
                "item" => grid[iz, ix],
                _ => e.Glyph ?? (string.IsNullOrEmpty(e.Name) ? 'p' : char.ToLowerInvariant(e.Name[0])),
            };
            grid[iz, ix] = g;
            if (e.Kind is "player" or "hive" && e.Name is { Length: > 0 } n && !who.Contains($"{g}={n}")) who.Add($"{g}={n}");
        }
        return who;
    }

    /// <summary>The x ruler (every 5th x) and the rows with z labels (every 5th z).</summary>
    private static void AppendRows(StringBuilder sb, char[,] grid, int x0, int z0, int size)
    {
        const int pad = 4;
        var ruler = new char[pad + size + 4];
        Array.Fill(ruler, ' ');
        for (int ix = 0; ix < size; ix++)
        {
            int x = x0 + ix;
            if (((x % 5) + 5) % 5 != 0) continue;
            var t = x.ToString(CultureInfo.InvariantCulture);
            for (int k = 0; k < t.Length && pad + ix + k < ruler.Length; k++) ruler[pad + ix + k] = t[k];
        }
        sb.Append(new string(ruler).TrimEnd()).Append('\n');
        for (int iz = 0; iz < size; iz++)
        {
            int z = z0 + iz;
            sb.Append(((z % 5) + 5) % 5 == 0 ? z.ToString(CultureInfo.InvariantCulture).PadLeft(pad - 1) + " " : new string(' ', pad));
            for (int ix = 0; ix < size; ix++) sb.Append(grid[iz, ix]);
            sb.Append('|').Append('\n');
        }
    }
    private static string Rng(int a, int b) => a == b ? a.ToString(CultureInfo.InvariantCulture) : $"{a}..{b}";
}
