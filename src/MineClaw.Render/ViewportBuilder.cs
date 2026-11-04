using MineClaw.Agent.Telemetry;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>Viewport window settings (host section "Host:Tui").</summary>
public sealed class ViewportOptions
{
    /// <summary>Grid radius in blocks (the panel shows as much of the 2R+1 square as fits).</summary>
    public int Radius { get; set; } = 12;
    /// <summary>Top-down view: scan from feet+Up down to feet−Down for each column's topmost block.</summary>
    public int Up { get; set; } = 2;
    public int Down { get; set; } = 10;
    /// <summary>Side view rows: feet+SideUp down to feet−SideDown.</summary>
    public int SideUp { get; set; } = 6;
    public int SideDown { get; set; } = 8;
    /// <summary>Isometric view volume: (2·VoxelRadius+1)² columns, feet−VoxelDown .. feet+VoxelUp (default 25×25×16).</summary>
    public int VoxelRadius { get; set; } = 12;
    public int VoxelUp { get; set; } = 7;
    public int VoxelDown { get; set; } = 8;
    /// <summary>Max rebuilds per second of the voxel volume (the snapshot reuses the last one in between).</summary>
    public double VoxelHz { get; set; } = 2;
}

/// <summary>
/// Builds a <see cref="ViewportGrid"/> from a <see cref="WorldState"/> around a position. Reads the world copy only
/// (call it from the UI thread, never the network thread); ~(2R+1)² × window block reads.
/// </summary>
public static class ViewportBuilder
{
    public const char Void = ' ';

    /// <summary>Map glyph of a block id (legend: # stone/cobble, . grass/dirt, ~ water, ! lava, T log, * leaves, C chest,
    /// F furnace, W workbench, + other solid). Null = see-through for the top-down scan (air, torches, flowers...).</summary>
    public static char? Glyph(int id)
    {
        if (id == Blocks.Air) return null;
        if (Glyphs.TryGetValue(id, out var g)) return g;
        return Blocks.Get(id).Solid ? '+' : null;
    }

    private static readonly Dictionary<int, char> Glyphs = new()
    {
        [Blocks.Stone] = '#', [Blocks.Cobblestone] = '#', [Blocks.MossyCobblestone] = '#', [Blocks.StoneBricks] = '#',
        [Blocks.Grass] = '.', [Blocks.Dirt] = '.', [Blocks.Farmland] = '.', [Blocks.Water] = '~', [Blocks.FlowingWater] = '~',
        [Blocks.Lava] = '!', [Blocks.FlowingLava] = '!', [Blocks.Log] = 'T', [Blocks.Leaves] = '*', [Blocks.Chest] = 'C', [Blocks.LockedChest] = 'C',
        [Blocks.Furnace] = 'F', [Blocks.LitFurnace] = 'F', [Blocks.Workbench] = 'W',
    };

    public static ViewportGrid Build(WorldState world, Vec3d feet, ViewportOptions? o = null, string? selfName = null)
    {
        o ??= new ViewportOptions();
        int r = Math.Clamp(o.Radius, 1, 64);
        var c = feet.ToBlockPos();
        var top = TopRows(world, c, r, o);
        int sideTop = c.Y + o.SideUp;
        var side = SideRows(world, c, r, sideTop, o.SideUp + o.SideDown + 1);
        var markers = new List<ViewMarker>();
        foreach (var e in world.Entities.Nearby(feet, r * 1.5 + 1))
            if (Marker(e, c, r, selfName) is { } m) markers.Add(m);
        return new ViewportGrid { Radius = r, Cx = c.X, Cy = c.Y, Cz = c.Z, Top = top, Side = side, SideTopY = sideTop, Markers = markers };
    }

    /// <summary>Top-down rows: each column's topmost glyph from feet+Up down to feet−Down.</summary>
    private static string[] TopRows(WorldState world, BlockPos c, int r, ViewportOptions o)
    {
        int n = 2 * r + 1, height = world.WorldHeight;
        var top = new string[n];
        var row = new char[n];
        for (int dz = -r; dz <= r; dz++)
        {
            for (int dx = -r; dx <= r; dx++) row[dx + r] = TopGlyph(world, c.X + dx, c.Z + dz, Math.Min(height - 1, c.Y + o.Up), Math.Max(0, c.Y - o.Down));
            top[dz + r] = new string(row);
        }
        return top;
    }

    private static char TopGlyph(WorldState world, int x, int z, int yTop, int yBottom)
    {
        if (!world.IsLoaded(x, z)) return Void;
        for (int y = yTop; y >= yBottom; y--)
            if (Glyph(world.GetBlock(x, y, z)) is { } ch) return ch;
        return Void;
    }

    /// <summary>Side rows: the slice along x through the feet, from <paramref name="sideTop"/> down.</summary>
    private static string[] SideRows(WorldState world, BlockPos c, int r, int sideTop, int sideRows)
    {
        int height = world.WorldHeight;
        var side = new string[sideRows];
        var row = new char[2 * r + 1];
        for (int i = 0; i < sideRows; i++)
        {
            int y = sideTop - i;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = c.X + dx;
                row[dx + r] = (uint)y < height && world.IsLoaded(x, c.Z) && Glyph(world.GetBlock(x, y, c.Z)) is { } ch ? ch : Void;
            }
            side[i] = new string(row);
        }
        return side;
    }

    /// <summary>A player (not the bot itself), hostile mob or item within the grid, as a marker relative to the feet.</summary>
    private static ViewMarker? Marker(Entity e, BlockPos c, int r, string? selfName)
    {
        string? kind = e.Kind switch
        {
            EntityKind.Player => "player",
            EntityKind.Mob when e.IsHostileMob => "mob",
            EntityKind.Item => "item",
            _ => null,
        };
        if (kind == null) return null;
        if (kind == "player" && selfName != null && string.Equals(e.Name, selfName, StringComparison.OrdinalIgnoreCase)) return null;
        var p = e.Position.ToBlockPos();
        int mdx = p.X - c.X, mdz = p.Z - c.Z;
        if (Math.Abs(mdx) > r || Math.Abs(mdz) > r) return null;
        return new ViewMarker(kind, kind == "item" ? null : e.Kind == EntityKind.Player ? e.Name : e.DisplayName, mdx, p.Y - c.Y, mdz);
    }
}
