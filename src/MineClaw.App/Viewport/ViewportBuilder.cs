using MineClaw.Agent.Telemetry;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Viewport;

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
        switch (id)
        {
            case Blocks.Air: return null;
            case Blocks.Stone or Blocks.Cobblestone or Blocks.MossyCobblestone or Blocks.StoneBricks: return '#';
            case Blocks.Grass or Blocks.Dirt or Blocks.Farmland: return '.';
            case Blocks.Water or Blocks.FlowingWater: return '~';
            case Blocks.Lava or Blocks.FlowingLava: return '!';
            case Blocks.Log: return 'T';
            case Blocks.Leaves: return '*';
            case Blocks.Chest or Blocks.LockedChest: return 'C';
            case Blocks.Furnace or Blocks.LitFurnace: return 'F';
            case Blocks.Workbench: return 'W';
        }
        var info = Blocks.Get(id);
        return info.Solid ? '+' : null;
    }

    public static ViewportGrid Build(WorldState world, Vec3d feet, ViewportOptions? o = null, string? selfName = null)
    {
        o ??= new ViewportOptions();
        int r = Math.Clamp(o.Radius, 1, 64);
        var c = feet.ToBlockPos();
        int n = 2 * r + 1;

        var top = new string[n];
        var row = new char[n];
        for (int dz = -r; dz <= r; dz++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                int x = c.X + dx, z = c.Z + dz;
                char g = Void;
                if (world.IsLoaded(x, z))
                {
                    for (int y = Math.Min(WorldState.Height - 1, c.Y + o.Up); y >= Math.Max(0, c.Y - o.Down); y--)
                        if (Glyph(world.GetBlock(x, y, z)) is { } ch) { g = ch; break; }
                }
                row[dx + r] = g;
            }
            top[dz + r] = new string(row);
        }

        int sideTop = c.Y + o.SideUp, sideRows = o.SideUp + o.SideDown + 1;
        var side = new string[sideRows];
        for (int i = 0; i < sideRows; i++)
        {
            int y = sideTop - i;
            for (int dx = -r; dx <= r; dx++)
            {
                int x = c.X + dx;
                row[dx + r] = (uint)y < WorldState.Height && world.IsLoaded(x, c.Z) && Glyph(world.GetBlock(x, y, c.Z)) is { } ch ? ch : Void;
            }
            side[i] = new string(row);
        }

        var markers = new List<ViewMarker>();
        foreach (var e in world.Entities.Nearby(feet, r * 1.5 + 1))
        {
            string? kind = e.Kind switch
            {
                EntityKind.Player => "player",
                EntityKind.Mob when e.IsHostileMob => "mob",
                EntityKind.Item => "item",
                _ => null,
            };
            if (kind == null) continue;
            if (kind == "player" && selfName != null && string.Equals(e.Name, selfName, StringComparison.OrdinalIgnoreCase)) continue;
            var p = e.Position.ToBlockPos();
            int mdx = p.X - c.X, mdz = p.Z - c.Z;
            if (Math.Abs(mdx) > r || Math.Abs(mdz) > r) continue;
            markers.Add(new ViewMarker(kind, kind == "item" ? null : e.Kind == EntityKind.Player ? e.Name : e.DisplayName, mdx, p.Y - c.Y, mdz));
        }

        return new ViewportGrid { Radius = r, Cx = c.X, Cy = c.Y, Cz = c.Z, Top = top, Side = side, SideTopY = sideTop, Markers = markers };
    }
}
