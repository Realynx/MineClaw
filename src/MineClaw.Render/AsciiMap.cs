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
        char? obstacle = feet switch
        {
            0 => null,
            Blocks.Chest or Blocks.LockedChest => 'C',
            Blocks.Furnace or Blocks.LitFurnace => 'F',
            Blocks.Workbench => 'W',
            Blocks.WoodenDoor or Blocks.IronDoor => 'D',
            Blocks.Log => 'T',
            Blocks.Leaves => '*',
            Blocks.Water or Blocks.FlowingWater => '~',
            Blocks.Lava or Blocks.FlowingLava => '!',
            Blocks.Slab or Blocks.SnowLayer or Blocks.Farmland => '=',
            _ => Blocks.Get(feet).Solid ? '#' : null,
        };
        if (obstacle is { } o) return o;
        bool deco = feet != 0;
        int under = w.GetBlock(x, y - 1, z);
        if (Blocks.IsWater(under)) return '~';
        if (Blocks.IsLava(under)) return '!';
        if (under != 0 && (Blocks.Get(under).Solid || under is Blocks.Slab or Blocks.Farmland)) return deco ? 'o' : '.';
        for (int d = 2; d <= 4; d++) if (w.GetBlock(x, y - d, z) != 0) return ',';
        for (int yy = y - 5; yy >= 0; yy--) if (w.GetBlock(x, yy, z) != 0) return '_';
        return ' ';
    }

    /// <summary>Renders the map around (cx, cy, cz). Entities: players by lower-case initial, hive bots by their glyph,
    /// mobs 'm'. Returns the text (header, x ruler, rows, legend + who is who).</summary>
    public static string Render(IBlockAccess w, int cx, int cy, int cz, int size = 15, (int X, int Y, int Z)? self = null,
        IReadOnlyList<VisionEntity>? entities = null, IReadOnlyList<VisionMark>? marks = null)
    {
        size = Math.Clamp(size | 1, 5, 25);
        int r = size / 2, x0 = cx - r, z0 = cz - r;
        var grid = new char[size, size];
        for (int iz = 0; iz < size; iz++)
            for (int ix = 0; ix < size; ix++) grid[iz, ix] = Cell(w, x0 + ix, cy, z0 + iz);
        var who = new List<string>();
        foreach (var e in entities ?? [])
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
        if (self is { } me && (uint)(me.X - x0) < (uint)size && (uint)(me.Z - z0) < (uint)size) grid[me.Z - z0, me.X - x0] = '@';

        const int pad = 4;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"map {size}x{size} x{x0}..{x0 + size - 1} z{z0}..{z0 + size - 1} at feet y{cy}, N up, 1 char=1 block\n");
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
        sb.Append(Legend);
        if (who.Count > 0) sb.Append("; ").Append(string.Join(' ', who));
        var inView = (marks ?? []).Where(m => m.MaxX >= x0 && m.MinX < x0 + size && m.MaxZ >= z0 && m.MinZ < z0 + size).Take(6).ToList();
        if (inView.Count > 0)
            sb.Append("; marks: ").Append(string.Join("; ", inView.Select(m =>
                $"{VisionRenderer.ShortLabel(m.Text)} {Rng(m.MinX, m.MaxX)},{Rng(m.MinY, m.MaxY)},{Rng(m.MinZ, m.MaxZ)}")));
        return sb.ToString();
    }

    private static string Rng(int a, int b) => a == b ? a.ToString(CultureInfo.InvariantCulture) : $"{a}..{b}";
}
