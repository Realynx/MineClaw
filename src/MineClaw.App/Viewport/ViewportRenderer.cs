using MineClaw.Agent.Telemetry;

namespace MineClaw.App.Viewport;

/// <summary>Viewport modes, in the order <c>v</c> cycles through them.</summary>
public enum ViewMode { Top, Side, Iso }

/// <summary>What a cell shows (the TUI maps it to a colour).</summary>
public enum CellKind { Void, Stone, Dirt, Water, Lava, Log, Leaves, Chest, Furnace, Workbench, Other, Self, Hive, Player, Mob, Item }

public readonly record struct ViewCell(char Glyph, CellKind Kind, string? Name = null);

/// <summary>
/// Fits a <see cref="ViewportGrid"/> into a panel: crops the square around the bot to the panel size (bot centred), draws
/// each column two characters wide when the panel is wide enough (terminal cells are ~2:1), and overlays markers:
/// <c>@</c> the displayed bot, hive members as their glyph (<see cref="HiveRoster.Glyph"/>), other players by the first
/// letter of their name, <c>m</c> hostile mobs, <c>i</c> items. North is up; the side view looks north (west left).
/// </summary>
public static class ViewportRenderer
{
    public static ViewCell[][] Render(ViewportGrid grid, ViewMode mode, int width, int height, Func<string, char?>? hiveGlyph = null)
    {
        if (width <= 0 || height <= 0) return [];
        int n = 2 * grid.Radius + 1;
        if (mode == ViewMode.Iso) mode = ViewMode.Top; // iso has its own renderer (IsoRenderer); this is its 2D fallback
        var src = mode == ViewMode.Top ? grid.Top : grid.Side;
        int srcRows = src.Length;
        if (srcRows == 0 || n <= 0) return [];
        int cellW = width >= 2 * n ? 2 : 1;
        int cols = Math.Min(n, width / cellW);
        int rows = Math.Min(srcRows, height);
        int centreCol = grid.Radius;
        int centreRow = mode == ViewMode.Top ? grid.Radius : grid.SideTopY - grid.Cy; // feet row in the side slice
        int col0 = Math.Clamp(centreCol - cols / 2, 0, n - cols);
        int row0 = Math.Clamp(centreRow - rows / 2, 0, srcRows - rows);

        var cells = new ViewCell[rows][];
        for (int i = 0; i < rows; i++)
        {
            var line = src[row0 + i];
            var outRow = new ViewCell[cols * cellW];
            for (int j = 0; j < cols; j++)
            {
                char g = col0 + j < line.Length ? line[col0 + j] : ViewportBuilder.Void;
                var cell = new ViewCell(g, KindOf(g));
                for (int k = 0; k < cellW; k++) outRow[j * cellW + k] = cell;
            }
            cells[i] = outRow;
        }

        // Markers, lowest priority first so higher ones overwrite.
        foreach (var m in grid.Markers.OrderBy(m => Priority(m, hiveGlyph)))
        {
            int sr, sc = m.Dx + grid.Radius;
            if (mode == ViewMode.Top) sr = m.Dz + grid.Radius;
            else
            {
                if (Math.Abs(m.Dz) > 1) continue;
                sr = grid.SideTopY - (grid.Cy + m.Dy);
            }
            Put(cells, sr - row0, sc - col0, cellW, MarkerCell(m, hiveGlyph));
        }
        // The bot itself (side view: feet and head).
        Put(cells, centreRow - row0, centreCol - col0, cellW, new ViewCell('@', CellKind.Self));
        if (mode == ViewMode.Side) Put(cells, centreRow - 1 - row0, centreCol - col0, cellW, new ViewCell('@', CellKind.Self));
        return cells;
    }

    /// <summary>Plain-text rendering (tests, <c>--frame</c> dumps).</summary>
    public static string[] RenderPlain(ViewportGrid grid, ViewMode mode, int width, int height, Func<string, char?>? hiveGlyph = null) =>
        Render(grid, mode, width, height, hiveGlyph).Select(r => new string(r.Select(c => c.Glyph).ToArray())).ToArray();

    private static void Put(ViewCell[][] cells, int r, int c, int cellW, ViewCell cell)
    {
        if (r < 0 || r >= cells.Length || c < 0 || (c + 1) * cellW > cells[r].Length) return;
        cells[r][c * cellW] = cell; // in 2-wide mode the second half keeps the terrain glyph
    }

    private static int Priority(ViewMarker m, Func<string, char?>? hive) => m.Kind switch
    {
        "item" => 0,
        "mob" => 1,
        "player" when m.Name != null && hive?.Invoke(m.Name) != null => 3,
        _ => 2,
    };

    private static ViewCell MarkerCell(ViewMarker m, Func<string, char?>? hive)
    {
        switch (m.Kind)
        {
            case "mob": return new ViewCell('m', CellKind.Mob, m.Name);
            case "item": return new ViewCell('i', CellKind.Item);
            default:
                if (m.Name != null && hive?.Invoke(m.Name) is { } h) return new ViewCell(h, CellKind.Hive, m.Name);
                char g = string.IsNullOrEmpty(m.Name) ? 'P' : m.Name[0];
                return new ViewCell(g, CellKind.Player, m.Name);
        }
    }

    public static CellKind KindOf(char g) => g switch
    {
        '#' => CellKind.Stone, '.' => CellKind.Dirt, '~' => CellKind.Water, '!' => CellKind.Lava, 'T' => CellKind.Log,
        '*' => CellKind.Leaves, 'C' => CellKind.Chest, 'F' => CellKind.Furnace, 'W' => CellKind.Workbench,
        ' ' => CellKind.Void, _ => CellKind.Other,
    };
}

/// <summary>Hive members in display order (index 0 = main bot) and their map glyphs.</summary>
public sealed class HiveRoster
{
    private readonly Dictionary<string, char> _glyphs = new(StringComparer.OrdinalIgnoreCase);

    public HiveRoster(IEnumerable<string> names)
    {
        Names = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var used = new HashSet<char>();
        foreach (var name in Names)
        {
            var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
            char g = 'M';
            if (digits.Length > 0)
            {
                // First digit of the numeric suffix, else its last digit, else any unused digit: 67→6, 69→9, 420→4, 1337→1.
                g = new[] { digits[0], digits[^1] }.Concat("0123456789").First(d => !used.Contains(d));
            }
            used.Add(g);
            _glyphs[name] = g;
        }
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>Display colours of hive members by roster index (0xRRGGBB).</summary>
    public static readonly uint[] BotColours = [0xffd75f, 0x5fd7ff, 0xff87d7, 0x87ff5f, 0xffaf5f, 0xaf87ff];
    private static readonly uint[] PlayerColours = [0x00ffff, 0x5fff87, 0xff5fff, 0xffaf00, 0x00afff, 0xff5faf];

    /// <summary>Colour of a hive member (by roster index) or another player (stable by name).</summary>
    public uint ColourOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase)) return BotColours[i % BotColours.Length];
        unchecked
        {
            int h = 17;
            foreach (char ch in name.ToLowerInvariant()) h = h * 31 + ch;
            return PlayerColours[(h & 0x7fffffff) % PlayerColours.Length];
        }
    }

    /// <summary>Map glyph of a hive member (null for anyone else). The main bot (no numeric suffix) is <c>M</c>.</summary>
    public char? Glyph(string name) => _glyphs.TryGetValue(name, out var g) ? g : null;
}
