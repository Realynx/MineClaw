using MineClaw.Agent.Telemetry;

namespace MineClaw.Render;

/// <summary>Isometric view settings (mutable, owned by the TUI loop): rotation in 90° steps, zoom 0-2, cutaway, y labels.</summary>
public sealed class IsoView
{
    public const int MaxZoom = 2;
    public int Rotation { get; set; }
    public int Zoom { get; set; } = 1;
    public bool Cutaway { get; set; }
    public bool Labels { get; set; }

    public void Rotate(int steps) => Rotation = ((Rotation + steps) % 4 + 4) % 4;
    public void ZoomBy(int d) => Zoom = Math.Clamp(Zoom + d, 0, MaxZoom);
}

/// <summary>One pixel of a block sprite (sprite-local; a pixel is half a character cell, so pixels are ~square).</summary>
/// <param name="Row">Row within its face, counted from the face's upper edge (0 = the row touching the top face for sides).</param>
/// <param name="Edge">On the outline of its face.</param>
/// <param name="Centre">Nearest pixel to the face centre (latches, furnace mouths).</param>
/// <param name="Inner">Top face: inner half (log rings).</param>
public readonly record struct SpritePx(int X, int Y, IsoFace Face, int Row, bool Edge, bool Centre, bool Inner);

/// <summary>
/// A block sprite: a hexagon <c>2A</c> pixels wide and <c>2B + C</c> tall — top rhombus (A, B half-axes) plus left and right
/// side faces C pixels high. Neighbours: +u is (+A, +B), +v is (−A, +B), +y is (0, −C).
/// </summary>
public sealed record IsoSprite(int Zoom, int A, int B, int C, SpritePx[] Full, SpritePx[] Small, SpritePx[] Flat)
{
    public int W => 2 * A;
    public int H => 2 * B + C;
}

/// <summary>
/// Projection maths of the isometric viewport. World (x east, z south, y up) is rotated by <c>rot</c> quarter turns into view
/// axes (u, v): rot 0 → u = x, v = z, so north points up-right (N↗); each '.' press turns the compass clockwise (N↘, N↙, N↖).
/// Screen (pixels): <c>X = (u − v)·A</c>, <c>Y = (u + v)·B − y·C</c>. The viewer sits at +u +v +y, so the visible side faces are
/// +v (left) and +u (right), and painter's depth is <c>(u + v)·C + 2B·y</c> (higher = nearer, drawn later).
/// </summary>
public static class IsoProjection
{
    public static readonly string[] Arrows = ["↗", "↘", "↙", "↖"];
    public static readonly string[] AsciiArrows = ["NE", "SE", "SW", "NW"];

    public static (int U, int V) Rotate(int dx, int dz, int rot) => (rot & 3) switch
    {
        0 => (dx, dz),
        1 => (-dz, dx),
        2 => (-dx, -dz),
        _ => (dz, -dx),
    };

    public static (int X, int Y) Project(int u, int v, int dy, IsoSprite s) => ((u - v) * s.A, (u + v) * s.B - dy * s.C);

    public static int Depth(int u, int v, int dy, IsoSprite s) => (u + v) * s.C + 2 * s.B * dy;

    /// <summary>Screen direction (corner index 0 up-right, 1 down-right, 2 down-left, 3 up-left) of compass point d (0 N, 1 E, 2 S, 3 W).</summary>
    public static int Corner(int d, int rot) => ((d + rot) % 4 + 4) % 4;

    private static readonly IsoSprite[] _sprites = [Make(0, 1, 1, 1), Make(1, 2, 1, 2), Make(2, 4, 2, 4)];

    /// <summary>Zoom 0: 2×1 chars (compact), 1: 4×2 chars, 2: 8×4 chars.</summary>
    public static IsoSprite Sprite(int zoom) => _sprites[Math.Clamp(zoom, 0, _sprites.Length - 1)];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, int), IsoSprite> _custom = new();

    /// <summary>A bigger sprite for images (half-axes a, b; side height c), e.g. (6, 3, 6). Treated like zoom 2+ (outlines, seams).</summary>
    public static IsoSprite Custom(int a, int b, int c) => _custom.GetOrAdd((a, b, c), k => Make(3, k.Item1, k.Item2, k.Item3));

    private static IsoSprite Make(int zoom, int a, int b, int c)
    {
        var face = FaceMap(zoom, a, b, c);
        var full = FullPixels(face, zoom, a, b);
        // Thin sprite: the two centre columns, from the rhombus centre down; its first row is the "tip" (Top colour).
        int w = 2 * a, h = 2 * b + c;
        var small = new List<SpritePx>();
        for (int py = b; py < h - (zoom == 0 ? 0 : 1); py++)
            for (int px = Math.Max(0, a - 1); px <= Math.Min(w - 1, a); px++)
                small.Add(new SpritePx(px, py, py == b ? IsoFace.Top : px < a ? IsoFace.Left : IsoFace.Right, py - b, false, false, false));
        // Flat: the top face moved down onto the floor of the cell.
        var flat = full.Where(p => p.Face == IsoFace.Top).Select(p => p with { Y = p.Y + c }).ToArray();
        return new IsoSprite(zoom, a, b, c, full.ToArray(), small.ToArray(), flat);
    }

    /// <summary>Which face each pixel of the 2a × (2b + c) sprite belongs to: the top rhombus, the left and right sides.</summary>
    private static IsoFace[,] FaceMap(int zoom, int a, int b, int c)
    {
        int w = 2 * a, h = 2 * b + c;
        var face = new IsoFace[w, h];
        if (zoom == 0)
        {
            // Compact: "TT" over "LR".
            face[0, 0] = face[1, 0] = IsoFace.Top;
            face[0, 1] = IsoFace.Left;
            face[1, 1] = IsoFace.Right;
            return face;
        }
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++) face[px, py] = FaceAt(px + 0.5, py + 0.5, a, b, c, w);
        return face;
    }

    private static IsoFace FaceAt(double x, double y, int a, int b, int c, int w)
    {
        double t = Math.Abs(x - a) / a + Math.Abs(y - b) / b;
        if (t < 1) return IsoFace.Top;
        if (y < b) return IsoFace.None;
        double upper = x < a ? b + x / a * b : b + (w - x) / a * b;
        return y < upper + c ? (x < a ? IsoFace.Left : IsoFace.Right) : IsoFace.None;
    }

    /// <summary>The sprite's pixels with their face, row within the face, outline, face-centre pixel and inner-top flags.</summary>
    private static List<SpritePx> FullPixels(IsoFace[,] face, int zoom, int a, int b)
    {
        int w = face.GetLength(0), h = face.GetLength(1);
        var best = CentrePixels(face);
        var full = new List<SpritePx>();
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                var f = face[px, py];
                if (f == IsoFace.None) continue;
                int row = 0;
                for (int y = py - 1; y >= 0 && face[px, y] == f; y--) row++;
                double tv = Math.Abs(px + 0.5 - a) / a + Math.Abs(py + 0.5 - b) / b;
                full.Add(new SpritePx(px, py, f, row, OnFaceEdge(face, px, py), best[f].X == px && best[f].Y == py, f == IsoFace.Top && (zoom == 0 || tv < 0.55)));
            }
        return full;
    }

    /// <summary>A pixel with a 4-neighbour outside its face (or the sprite).</summary>
    private static bool OnFaceEdge(IsoFace[,] face, int px, int py)
    {
        var f = face[px, py];
        bool Same(int x, int y) => x >= 0 && y >= 0 && x < face.GetLength(0) && y < face.GetLength(1) && face[x, y] == f;
        return !Same(px - 1, py) || !Same(px + 1, py) || !Same(px, py - 1) || !Same(px, py + 1);
    }

    /// <summary>Per face, the pixel nearest its centroid (Manhattan; the first in scan order on a tie).</summary>
    private static Dictionary<IsoFace, (int X, int Y, double D)> CentrePixels(IsoFace[,] face)
    {
        int w = face.GetLength(0), h = face.GetLength(1);
        var centres = new Dictionary<IsoFace, (double X, double Y, int N)>();
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
                if (face[px, py] is var f and not IsoFace.None)
                {
                    var (sx, sy, n) = centres.GetValueOrDefault(f);
                    centres[f] = (sx + px, sy + py, n + 1);
                }
        var centrePx = centres.ToDictionary(kv => kv.Key, kv => (X: kv.Value.X / kv.Value.N, Y: kv.Value.Y / kv.Value.N));
        var best = new Dictionary<IsoFace, (int X, int Y, double D)>();
        for (int py = 0; py < h; py++)
            for (int px = 0; px < w; px++)
            {
                var f = face[px, py];
                if (f == IsoFace.None) continue;
                double d = Math.Abs(px - centrePx[f].X) + Math.Abs(py - centrePx[f].Y);
                if (!best.TryGetValue(f, out var cur) || d < cur.D) best[f] = (px, py, d);
            }
        return best;
    }
}

[Flags]
public enum IsoCellFlags : byte { None = 0, Fg = 1, Bg = 2, Bold = 4, Entity = 8, Hidden = 16, Label = 32 }

/// <summary>One character cell of the rendered canvas. Colours are 0xRRGGBB, or <see cref="IsoPalette.Indexed"/> | index.</summary>
public struct IsoCell
{
    public char Ch;
    public uint Fg;
    public uint Bg;
    public IsoCellFlags Flags;
}

/// <summary>An entity drawn on the canvas (for tests and the legend).</summary>
public readonly record struct IsoMark(char Glyph, int Col, int Row, bool Hidden, string Kind, string? Name);

/// <summary>The rendered character grid (reused between frames by its <see cref="IsoRenderer"/>).</summary>
public sealed class IsoFrame
{
    public int Width { get; internal set; }
    public int Height { get; internal set; }
    public IsoCell[] Cells { get; internal set; } = [];
    public int Zoom { get; internal set; }
    public int Rotation { get; internal set; }
    public int? CutY { get; internal set; }
    public int BlocksDrawn { get; internal set; }
    public List<IsoMark> Marks { get; } = new();

    public ref IsoCell At(int col, int row) => ref Cells[row * Width + col];

    public string[] PlainRows()
    {
        var rows = new string[Height];
        var buf = new char[Width];
        for (int r = 0; r < Height; r++)
        {
            for (int c = 0; c < Width; c++) buf[c] = Cells[r * Width + c].Ch;
            rows[r] = new string(buf);
        }
        return rows;
    }
}

/// <summary>Everything one iso render needs.</summary>
public sealed record IsoInput(VoxelVolume Volume, IReadOnlyList<ViewMarker> Markers, IsoView View, IsoColours Colours, bool Unicode,
    bool Nether = false, Func<string, char?>? HiveGlyph = null, Func<string, uint>? NameColour = null, uint SelfColour = 0xffd75f);

/// <summary>
/// Software rasteriser for the isometric viewport: draws every non-air cell of a <see cref="VoxelVolume"/> back to front
/// (painter's algorithm, precomputed order per rotation) into a half-block pixel buffer (two pixels per character cell),
/// shades faces (top ×1, left ×0.78, right ×0.58, darker with depth below the bot), then overlays the path/target, entities
/// (hidden ones as inverted markers), y labels and a compass. Buffers are reused, so a frame allocates almost nothing.
/// One instance per TUI (not thread-safe).
/// Deprecated for pictures: it only draws the TUI's character-cell viewport (where one cell must be one block). Every image
/// the AI or a person sees (iso, top, side, layers, sheets, previews, supervisor and Live Hive views) comes from
/// <see cref="EyeRenderer"/>.
/// </summary>
public sealed class IsoRenderer
{
    private uint[] _px = [];
    private int[] _depth = [];
    private byte[] _mat = [];
    private IsoFace[] _face = [];
    private bool[] _grid = [];
    private uint[] _bgRow = [];
    private readonly IsoFrame _frame = new();
    private (int Sx, int Sy, int Sz, int Rot, int Ratio, int[] Order)? _order;

    public const uint HighlightColour = 0xffe14d, PathColour = 0x5fe8ff;

    /// <summary>Cell indices of a volume in painter's order for a rotation and sprite (back to front). Cached.</summary>
    public int[] PaintOrder(VoxelVolume vol, int rot, IsoSprite s)
    {
        int ratio = s.C * 100 / (2 * s.B);
        if (_order is { } o && o.Sx == vol.SizeX && o.Sy == vol.SizeY && o.Sz == vol.SizeZ && o.Rot == rot && o.Ratio == ratio) return o.Order;
        int n = vol.Count;
        var keys = new int[n];
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            int x = i % vol.SizeX, z = i / vol.SizeX % vol.SizeZ, y = i / (vol.SizeX * vol.SizeZ);
            var (u, v) = IsoProjection.Rotate(x + vol.X0 - vol.Cx, z + vol.Z0 - vol.Cz, rot);
            keys[i] = IsoProjection.Depth(u, v, y + vol.Y0 - vol.Cy, s);
            order[i] = i;
        }
        Array.Sort(keys, order);
        _order = (vol.SizeX, vol.SizeY, vol.SizeZ, rot, ratio, order);
        return order;
    }

    /// <summary>Zoom actually used for a panel size (small panels drop to the compact sprite).</summary>
    public static int EffectiveZoom(int zoom, int width, int height) =>
        width < 40 || height < 12 ? 0 : zoom >= 2 && (width < 60 || height < 18) ? 1 : Math.Clamp(zoom, 0, IsoView.MaxZoom);

    /// <summary>Horizontal radius drawn at a zoom (zoom 2 shows the nearer blocks only).</summary>
    public static int RadiusFor(int zoom, int volumeRadius) => zoom >= 2 ? Math.Min(volumeRadius, 7) : volumeRadius;

    public IsoFrame Render(IsoInput input, int width, int height)
    {
        var f = _frame;
        f.Marks.Clear();
        f.Width = Math.Max(0, width);
        f.Height = Math.Max(0, height);
        int nCells = f.Width * f.Height;
        if (f.Cells.Length != nCells) f.Cells = new IsoCell[nCells];
        if (nCells == 0) return f;

        var vol = input.Volume;
        var view = input.View;
        bool glyphMode = !input.Unicode || input.Colours == IsoColours.None;
        int zoom = EffectiveZoom(view.Zoom, width, height);
        var s = IsoProjection.Sprite(zoom);
        int rot = view.Rotation & 3;
        int volR = Math.Max(vol.SizeX, vol.SizeZ) / 2;
        int r = RadiusFor(zoom, volR);
        int? cutY = view.Cutaway ? vol.Cy + 1 : null;
        f.Zoom = zoom;
        f.Rotation = rot;
        f.CutY = cutY;

        int W = width, PH = height * 2, nPx = W * PH;
        if (_px.Length < nPx) { _px = new uint[nPx]; _depth = new int[nPx]; _mat = new byte[nPx]; _face = new IsoFace[nPx]; _grid = new bool[nPx]; }
        if (_bgRow.Length < PH) _bgRow = new uint[PH];
        int ox = W / 2 - s.A;
        int oy = (int)(PH * 0.45) - (s.B + s.C / 2);
        f.BlocksDrawn = Rasterize(input, s, zoom, r, cutY, W, PH, ox, oy, glyphMode);

        PixelsToCells(f, input, height, W, glyphMode);
        PathDots(f, input, rot, s, ox, oy, W, PH);
        Entities(f, input, rot, s, ox, oy, W, PH);
        if (view.Labels) YLabels(f, input, zoom, s, oy, height, W);
        if (W >= 20 && height >= 4) CompassRose(f, input, rot, W);
        return f;
    }

    /// <summary>5. Pixels → character cells: half blocks (two pixels a cell) or, in glyph mode, the material's glyph.</summary>
    private void PixelsToCells(IsoFrame f, IsoInput input, int height, int W, bool glyphMode)
    {
        for (int row = 0; row < height; row++)
        {
            int kt = (2 * row) * W, kb = kt + W;
            for (int col = 0; col < W; col++)
            {
                ref var cell = ref f.Cells[row * W + col];
                if (glyphMode) GlyphCell(ref cell, input, kt + col, kb + col, _bgRow[2 * row]);
                else HalfBlockCell(ref cell, _px[kt + col], _px[kb + col]);
            }
        }
    }

    private static void HalfBlockCell(ref IsoCell cell, uint t, uint b)
    {
        if (t == b) { cell.Ch = ' '; cell.Fg = 0; cell.Bg = t; cell.Flags = IsoCellFlags.Bg; }
        else { cell.Ch = '▀'; cell.Fg = t; cell.Bg = b; cell.Flags = IsoCellFlags.Fg | IsoCellFlags.Bg; }
    }

    private void GlyphCell(ref IsoCell cell, IsoInput input, int top, int bottom, uint bg)
    {
        int k = _mat[top] != 0 || (_grid[top] && _mat[bottom] == 0) ? top : bottom;
        cell.Ch = GlyphAt(k, input.Unicode);
        if (input.Colours == IsoColours.None) { cell.Fg = cell.Bg = 0; cell.Flags = IsoCellFlags.None; }
        else { cell.Fg = _px[k]; cell.Bg = bg; cell.Flags = IsoCellFlags.Fg | IsoCellFlags.Bg; }
    }

    /// <summary>The glyph of a buffer pixel: the target marker, the block's face glyph, the lattice dot, or blank.</summary>
    private char GlyphAt(int k, bool unicode)
    {
        if (_mat[k] == 255) return unicode ? '◇' : 'o';
        if (_mat[k] != 0 && IsoPalette.Mat(_mat[k]) is { } m) return _face[k] == IsoFace.Top ? m.GTop : m.GSide;
        return _grid[k] ? '.' : ' ';
    }

    /// <summary>6. Path dots (only where visible).</summary>
    private void PathDots(IsoFrame f, IsoInput input, int rot, IsoSprite s, int ox, int oy, int W, int PH)
    {
        var vol = input.Volume;
        if (vol.Path is not { Length: >= 3 } path) return;
        var colours = input.Colours;
        uint pc = Colour(PathColour, colours, 14);
        for (int j = 0; j + 2 < path.Length; j += 3)
        {
            var (col, row, _, hidden) = Anchor(path[j] - vol.Cx, path[j + 1] - vol.Cy, path[j + 2] - vol.Cz, rot, s, ox, oy, W, PH);
            if (col < 0 || hidden) continue;
            ref var cell = ref f.At(col, row);
            cell.Ch = input.Unicode ? '•' : '.';
            cell.Fg = pc;
            cell.Flags |= IsoCellFlags.Fg;
            if (colours == IsoColours.None) cell.Flags = IsoCellFlags.None;
        }
    }

    /// <summary>Draw order of a marker: items, mobs, players, hive bots (the bot itself goes last).</summary>
    private static int MarkerPass(ViewMarker m, IsoInput input) =>
        m.Kind switch { "item" => 0, "mob" => 1, _ => m.Name != null && input.HiveGlyph?.Invoke(m.Name) != null ? 3 : 2 };

    /// <summary>7. Entities (lowest priority first; the bot itself last).</summary>
    private void Entities(IsoFrame f, IsoInput input, int rot, IsoSprite s, int ox, int oy, int W, int PH)
    {
        for (int pass = 0; pass < 4; pass++)
            foreach (var m in input.Markers)
            {
                if (MarkerPass(m, input) != pass) continue;
                var (glyph, colour) = MarkerLook(m, input);
                DrawMark(f, glyph, colour, m.Kind, m.Name, m.Dx, m.Dy, m.Dz, rot, s, ox, oy, W, PH, input.Colours, input.Unicode);
            }
        DrawMark(f, '@', input.SelfColour, "self", null, 0, 0, 0, rot, s, ox, oy, W, PH, input.Colours, input.Unicode);
    }

    /// <summary>8. y labels: a height ruler through the bot's column.</summary>
    private static void YLabels(IsoFrame f, IsoInput input, int zoom, IsoSprite s, int oy, int height, int W)
    {
        var vol = input.Volume;
        var colours = input.Colours;
        int step = zoom >= 2 ? 1 : 2, lastRow = -2;
        uint lc = Colour(0x9aa3b5, colours, 8);
        for (int wy = vol.Y0 + vol.SizeY - 1; wy >= vol.Y0; wy--)
        {
            if ((wy - vol.Cy) % step != 0) continue;
            int prow = oy - (wy - vol.Cy) * s.C + s.B;
            int row = prow >> 1;
            if (prow < 0 || row >= height || row - lastRow < 2) continue;
            lastRow = row;
            YLabel(f, input, wy, row, W, wy == vol.Cy ? Colour(0xffffff, colours, 15) : lc);
        }
    }

    private static void YLabel(IsoFrame f, IsoInput input, int wy, int row, int W, uint fg)
    {
        var text = wy + (input.Unicode ? "─" : "-");
        for (int c = 0; c < text.Length && c < W; c++)
        {
            ref var cell = ref f.At(c, row);
            cell.Ch = text[c];
            cell.Fg = fg;
            cell.Flags = (cell.Flags | IsoCellFlags.Fg | IsoCellFlags.Label) & ~IsoCellFlags.Hidden;
            if (input.Colours == IsoColours.None) cell.Flags = IsoCellFlags.Label;
        }
    }

    private static readonly (int X, int Y)[] CompassCorners = [(2, 0), (2, 2), (0, 2), (0, 0)];

    /// <summary>9. Compass rose (top right): the four points sit on the screen corners their direction faces.</summary>
    private static void CompassRose(IsoFrame f, IsoInput input, int rot, int W)
    {
        var colours = input.Colours;
        int cx0 = W - 4;
        ref var mid = ref f.At(cx0 + 1, 1);
        mid.Ch = input.Unicode ? '┼' : '+';
        mid.Fg = Colour(0x707888, colours, 8);
        mid.Flags = colours == IsoColours.None ? IsoCellFlags.Label : (mid.Flags | IsoCellFlags.Fg | IsoCellFlags.Label);
        for (int d = 0; d < 4; d++)
        {
            var (x, y) = CompassCorners[IsoProjection.Corner(d, rot)];
            ref var cell = ref f.At(cx0 + x, y);
            cell.Ch = "NESW"[d];
            cell.Fg = d == 0 ? Colour(0xff6b6b, colours, 9) : Colour(0xc8ccd6, colours, 7);
            cell.Flags = colours == IsoColours.None ? IsoCellFlags.Label : (cell.Flags | IsoCellFlags.Fg | IsoCellFlags.Label | (d == 0 ? IsoCellFlags.Bold : 0));
        }
    }

    /// <summary>
    /// Steps 1-4 of a render into the pixel buffers (<c>W</c>×<c>PH</c> pixels, origin offset ox/oy): void background, the faint
    /// lattice under the volume, the blocks back to front, the target outline. Returns the number of blocks drawn.
    /// </summary>
    private int Rasterize(IsoInput input, IsoSprite s, int zoom, int r, int? cutY, int W, int PH, int ox, int oy, bool glyphMode)
    {
        int nPx = W * PH;
        if (_px.Length < nPx) { _px = new uint[nPx]; _depth = new int[nPx]; _mat = new byte[nPx]; _face = new IsoFace[nPx]; _grid = new bool[nPx]; }
        if (_bgRow.Length < PH) _bgRow = new uint[PH];
        Background(input, W, PH);
        // 16 colours: plain black void (any grid colour is too loud).
        if (input.Colours is IsoColours.TrueColor or IsoColours.Ansi256) Lattice(input, s, zoom, r, W, PH, ox, oy, glyphMode);
        int drawn = Blocks(input, s, r, cutY, W, PH, ox, oy, glyphMode);
        if (input.Volume.Target is { Length: >= 3 } tgt) TargetOutline(input, tgt, s, zoom, W, PH, ox, oy);
        return drawn;
    }

    /// <summary>1. Void background (vertical gradient; reddish in the Nether).</summary>
    private void Background(IsoInput input, int W, int PH)
    {
        for (int py = 0; py < PH; py++)
        {
            uint bg = VoidColour(input, py, PH);
            _bgRow[py] = bg;
            int row = py * W;
            Array.Fill(_px, bg, row, W);
            Array.Fill(_depth, int.MinValue, row, W);
            Array.Clear(_mat, row, W);
            Array.Clear(_face, row, W);
            Array.Clear(_grid, row, W);
        }
    }

    private static uint VoidColour(IsoInput input, int py, int PH) => input.Colours switch
    {
        IsoColours.TrueColor => IsoPalette.Mix(IsoPalette.VoidTop(input.Nether), IsoPalette.VoidBottom(input.Nether), PH <= 1 ? 0 : py / (float)(PH - 1)),
        IsoColours.Ansi256 => IsoPalette.Indexed | (uint)IsoPalette.To256(IsoPalette.Mix(IsoPalette.VoidTop(input.Nether), IsoPalette.VoidBottom(input.Nether), PH <= 1 ? 0 : py / (float)(PH - 1))),
        IsoColours.Ansi16 => IsoPalette.Indexed | 0u,
        _ => 0u,
    };

    /// <summary>2. Faint lattice one level below the volume, so the drop off the island edge reads as void.</summary>
    private void Lattice(IsoInput input, IsoSprite s, int zoom, int r, int W, int PH, int ox, int oy, bool glyphMode)
    {
        var vol = input.Volume;
        uint grid = Colour(IsoPalette.VoidGrid(input.Nether), input.Colours, input.Nether ? 1 : 4);
        int gdy = vol.Y0 - 1 - vol.Cy;
        // Compact sprites and ASCII get a sparser lattice (every other tile), or it turns into a checkerboard.
        int gstep = zoom == 0 || glyphMode ? 2 : 1;
        for (int du = -r; du <= r; du += gstep)
            for (int dv = -r; dv <= r; dv += gstep)
            {
                var (bx, by) = IsoProjection.Project(du, dv, gdy, s);
                bx += ox; by += oy;
                if (bx + s.W < 0 || bx >= W || by + s.H < 0 || by >= PH) continue;
                LatticeTile(s, zoom, bx, by, W, PH, grid);
            }
    }

    private void LatticeTile(IsoSprite s, int zoom, int bx, int by, int W, int PH, uint grid)
    {
        foreach (var p in s.Full)
        {
            if (p.Face != IsoFace.Top) continue;
            bool on = zoom >= 2 ? p.Edge && p.Y < s.B : p.Y == 0 && p.X == s.A - 1;
            if (!on) continue;
            int X = bx + p.X, Y = by + p.Y;
            if ((uint)X >= (uint)W || (uint)Y >= (uint)PH) continue;
            int k = Y * W + X;
            _px[k] = grid;
            _grid[k] = true;
        }
    }

    /// <summary>3. Blocks, back to front. Returns how many were drawn.</summary>
    private int Blocks(IsoInput input, IsoSprite s, int r, int? cutY, int W, int PH, int ox, int oy, bool glyphMode)
    {
        var vol = input.Volume;
        int rot = input.View.Rotation & 3;
        var cells = vol.Cells();
        var order = PaintOrder(vol, rot, s);
        int sxz = vol.SizeX * vol.SizeZ, drawn = 0;
        foreach (int i in order)
        {
            int id = cells[i];
            if (id == 0) continue;
            int wy = vol.Y0 + i / sxz;
            if (cutY is { } cy && wy > cy) continue;
            int dx = i % vol.SizeX + vol.X0 - vol.Cx, dz = i / vol.SizeX % vol.SizeZ + vol.Z0 - vol.Cz;
            if (Math.Abs(dx) > r || Math.Abs(dz) > r) continue;
            if (IsoPalette.Mat(id) is not { } mat) continue;
            if (DrawBlock(input, s, rot, id, mat, dx, wy, dz, W, PH, ox, oy, glyphMode)) drawn++;
        }
        return drawn;
    }

    /// <summary>One block's sprite, depth-shaded; false when it falls outside the buffer.</summary>
    private bool DrawBlock(IsoInput input, IsoSprite s, int rot, int id, IsoMat mat, int dx, int wy, int dz, int W, int PH, int ox, int oy, bool glyphMode)
    {
        var vol = input.Volume;
        var (u, v) = IsoProjection.Rotate(dx, dz, rot);
        int dy = wy - vol.Cy;
        var (bx, by) = IsoProjection.Project(u, v, dy, s);
        bx += ox; by += oy;
        if (bx + s.W <= 0 || bx >= W || by + s.H <= 0 || by >= PH) return false;
        int key = IsoProjection.Depth(u, v, dy, s);
        var flags = mat.Flags;
        var pixels = (flags & MatFlags.Small) != 0 ? s.Small : (flags & MatFlags.Flat) != 0 ? s.Flat : s.Full;
        float df = 1f - 0.045f * Math.Max(0, -dy) - 0.012f * Math.Max(0, -(u + v));
        df = Math.Clamp(df, 0.5f, 1f);
        if ((flags & MatFlags.Emissive) != 0) df = 1f - (1f - df) * 0.3f;
        var at = new BlockAt(id, mat, vol.Cx + dx, wy, vol.Cz + dz, bx, by, key, df);
        foreach (var p in pixels) DrawPixel(input, s, at, p, W, PH, glyphMode);
        return true;
    }

    /// <summary>A block being drawn: id, material, world cell, sprite origin, painter's depth and depth shade.</summary>
    private readonly record struct BlockAt(int Id, IsoMat Mat, int Wx, int Wy, int Wz, int Bx, int By, int Key, float Df);

    private void DrawPixel(IsoInput input, IsoSprite s, in BlockAt b, SpritePx p, int W, int PH, bool glyphMode)
    {
        var flags = b.Mat.Flags;
        bool outline = (flags & MatFlags.Outline) != 0, translucent = (flags & MatFlags.Translucent) != 0;
        if (outline && !p.Edge) return;
        int X = b.Bx + p.X, Y = b.By + p.Y;
        if ((uint)X >= (uint)W || (uint)Y >= (uint)PH) return;
        int k = Y * W + X;
        var colours = input.Colours;
        uint c = glyphMode && colours == IsoColours.None ? 0 : PixelColour(s, b, p, colours);
        if (translucent && colours == IsoColours.TrueColor && (_px[k] & IsoPalette.Indexed) == 0) c = IsoPalette.Mix(_px[k], c, 0.62f);
        _px[k] = c;
        _mat[k] = (byte)b.Id;
        _face[k] = p.Face;
        _grid[k] = false;
        if (!outline && !translucent) _depth[k] = b.Key;
    }

    /// <summary>A sprite pixel's colour: the image texturer's, else the palette shading.</summary>
    private uint PixelColour(IsoSprite s, in BlockAt b, SpritePx p, IsoColours colours) =>
        _texturer?.Invoke(b.Id, b.Wx, b.Wy, b.Wz, p, s) is { } tc ? IsoPalette.Scale(tc, b.Df) : Shade(b.Mat, p, b.Wx, b.Wy, b.Wz, b.Df, colours, s);

    /// <summary>4. Target outline.</summary>
    private void TargetOutline(IsoInput input, int[] tgt, IsoSprite s, int zoom, int W, int PH, int ox, int oy)
    {
        var vol = input.Volume;
        int rot = input.View.Rotation & 3;
        int dx = tgt[0] - vol.Cx, dy = tgt[1] - vol.Cy, dz = tgt[2] - vol.Cz;
        var (u, v) = IsoProjection.Rotate(dx, dz, rot);
        var (bx, by) = IsoProjection.Project(u, v, dy, s);
        uint hc = Colour(HighlightColour, input.Colours, 11);
        // Big sprites: a solid outline. Small ones: a yellow tint, so the block underneath (lava, chest...) still reads.
        bool tint = zoom < 2 && input.Colours == IsoColours.TrueColor;
        foreach (var p in s.Full)
        {
            if (!p.Edge && !tint) continue;
            int X = bx + ox + p.X, Y = by + oy + p.Y;
            if ((uint)X >= (uint)W || (uint)Y >= (uint)PH) continue;
            int k = Y * W + X;
            _px[k] = tint && (_px[k] & IsoPalette.Indexed) == 0 ? IsoPalette.Mix(_px[k], hc, 0.5f) : hc;
            if (_mat[k] == 0) { _mat[k] = 255; _face[k] = IsoFace.Top; }
        }
    }

    /// <summary>
    /// True-colour pixel render for images (the <c>see</c> tool): the same rasteriser as the TUI with any sprite (e.g.
    /// <see cref="IsoProjection.Custom"/>), <paramref name="radius"/> blocks around the volume's centre, origin at (ox, oy).
    /// Returns a copy of the W×H 0xRRGGBB pixels; <see cref="PixelAnchor"/> then places overlays.
    /// </summary>
    /// <summary>Per-pixel texture hook for image renders: (id, world x, y, z, sprite pixel, sprite) → 0xRRGGBB already lit, or
    /// null for the palette shading. Only <see cref="RenderPixels"/> uses it; the TUI path never does.</summary>
    public delegate uint? Texturer(int id, int wx, int wy, int wz, SpritePx p, IsoSprite s);

    private Texturer? _texturer;

    public uint[] RenderPixels(IsoInput input, IsoSprite s, int radius, int W, int H, int ox, int oy, out int drawn, Texturer? texturer)
    {
        _texturer = texturer;
        try { return RenderPixels(input, s, radius, W, H, ox, oy, out drawn); }
        finally { _texturer = null; }
    }

    public uint[] RenderPixels(IsoInput input, IsoSprite s, int radius, int W, int H, int ox, int oy, out int drawn)
    {
        var tc = input with { Colours = IsoColours.TrueColor, Unicode = true };
        int? cutY = input.View.Cutaway ? input.Volume.Cy + 1 : null;
        drawn = Rasterize(tc, s, Math.Max(s.Zoom, 2), radius, cutY, W, H, ox, oy, glyphMode: false);
        return _px.AsSpan(0, W * H).ToArray();
    }

    /// <summary>Pixel where an entity standing in cell (dx, dy, dz) (relative to the volume centre) is drawn after
    /// <see cref="RenderPixels"/>, and whether a nearer opaque block covers it. Null when off the image.</summary>
    public (int X, int Y, bool Hidden)? PixelAnchor(int dx, int dy, int dz, int rot, IsoSprite s, int ox, int oy, int W, int H)
    {
        var (u, v) = IsoProjection.Rotate(dx, dz, rot);
        var (bx, by) = IsoProjection.Project(u, v, dy, s);
        int x = ox + bx + s.A - 1, y = oy + by + s.B + s.C / 2;
        if ((uint)x >= (uint)W || (uint)y >= (uint)H) return null;
        return (x, y, _depth[y * W + x] > IsoProjection.Depth(u, v, dy, s));
    }

    /// <summary>Character cell where an entity standing in cell (dx, dy, dz) is drawn, its depth, and whether a nearer opaque
    /// block covers that spot. col = −1 when off-canvas.</summary>
    private (int Col, int Row, int Key, bool Hidden) Anchor(int dx, int dy, int dz, int rot, IsoSprite s, int ox, int oy, int W, int PH)
    {
        var (u, v) = IsoProjection.Rotate(dx, dz, rot);
        var (bx, by) = IsoProjection.Project(u, v, dy, s);
        int col = ox + bx + s.A - 1;
        int prow = oy + by + s.B + s.C / 2;
        if ((uint)col >= (uint)W || (uint)prow >= (uint)PH) return (-1, -1, 0, false);
        int key = IsoProjection.Depth(u, v, dy, s);
        bool hidden = _depth[prow * W + col] > key;
        return (col, prow >> 1, key, hidden);
    }

    private void DrawMark(IsoFrame f, char glyph, uint colour, string kind, string? name, int dx, int dy, int dz, int rot, IsoSprite s,
        int ox, int oy, int W, int PH, IsoColours colours, bool unicode)
    {
        var (col, row, _, hidden) = Anchor(dx, dy, dz, rot, s, ox, oy, W, PH);
        if (col < 0) return;
        ref var cell = ref f.At(col, row);
        cell.Ch = glyph;
        uint c = Colour(colour, colours, IsoPalette.To16(colour));
        if (colours == IsoColours.None) cell.Flags = IsoCellFlags.Entity | (hidden ? IsoCellFlags.Hidden : 0);
        else if (hidden)
        {
            // Behind something: an inverted marker on top of the occluder, so it's never lost.
            cell.Fg = Colour(0x101010, colours, 0);
            cell.Bg = c;
            cell.Flags = IsoCellFlags.Fg | IsoCellFlags.Bg | IsoCellFlags.Bold | IsoCellFlags.Entity | IsoCellFlags.Hidden;
        }
        else
        {
            cell.Fg = c;
            // Keep the pixel behind as background (bottom half: where the entity stands).
            if (!unicode || (cell.Flags & IsoCellFlags.Bg) == 0) cell.Bg = _px[Math.Min(PH - 1, 2 * row + 1) * W + col];
            cell.Flags = IsoCellFlags.Fg | IsoCellFlags.Bg | IsoCellFlags.Bold | IsoCellFlags.Entity;
        }
        f.Marks.Add(new IsoMark(glyph, col, row, hidden, kind, name));
    }

    /// <summary>Glyph and colour of a marker: hive bots by roster glyph, players by first letter, mobs by type, items 'i'.</summary>
    public static (char Glyph, uint Colour) MarkerLook(ViewMarker m, IsoInput input)
    {
        switch (m.Kind)
        {
            case "item": return ('i', 0xf2e46a);
            case "mob":
                return (m.Name ?? "").ToLowerInvariant() switch
                {
                    "zombie" => ('z', 0x52b08e),
                    "skeleton" => ('s', 0xe8e8e8),
                    "creeper" => ('c', 0x3ce83c),
                    "spider" or "cave_spider" => ('S', 0xd04848),
                    "enderman" => ('e', 0xc77dff),
                    "slime" => ('o', 0x8de08d),
                    "zombie_pigman" => ('p', 0xf0a0a0),
                    "ghast" => ('g', 0xf4f4f4),
                    _ => ('m', 0xff5555),
                };
            default:
                uint pc = m.Name != null && input.NameColour != null ? input.NameColour(m.Name) : 0x5fd7ff;
                if (m.Name != null && input.HiveGlyph?.Invoke(m.Name) is { } h) return (h, pc);
                return (string.IsNullOrEmpty(m.Name) ? 'P' : m.Name[0], pc);
        }
    }

    private static uint Colour(uint rgb, IsoColours c, int idx16) => c switch
    {
        IsoColours.TrueColor => rgb,
        IsoColours.Ansi256 => IsoPalette.Indexed | (uint)IsoPalette.To256(rgb),
        IsoColours.Ansi16 => IsoPalette.Indexed | (uint)idx16,
        _ => 0,
    };

    private static int Noise(int x, int y, int z, int px, int py, IsoFace f)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)(px * 2654435761) ^ (uint)(py * 40503) ^ (uint)f * 97u;
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (int)(h & 255);
        }
    }

    /// <summary>Colour of one sprite pixel of a block (face shade × texture × depth), in the terminal's colour system.</summary>
    internal static uint Shade(IsoMat mat, SpritePx p, int wx, int wy, int wz, float depth, IsoColours colours, IsoSprite s)
    {
        int n = Noise(wx, wy, wz, p.X, p.Y, p.Face);
        bool accent = mat.Accent != 0 && p.Face != IsoFace.Top && p.Centre && AccentFace(mat.Tex, p.Face);
        return colours switch
        {
            IsoColours.Ansi16 => Shade16(mat, p, accent),
            IsoColours.Ansi256 => Shade256(mat, p, n, accent),
            _ => ShadeTrue(mat, p, n, accent, depth, s),
        };
    }

    private static int FaceIndex(IsoFace f) => f == IsoFace.Top ? 0 : f == IsoFace.Left ? 1 : 2;

    /// <summary>The faces that show a block's accent pixel: the chest latch and furnace mouth on the left face, a bench on any side.</summary>
    private static bool AccentFace(MatTex tex, IsoFace f) => tex switch
    {
        MatTex.Chest or MatTex.Furnace or MatTex.LitFurnace => f == IsoFace.Left,
        MatTex.Bench => true,
        _ => false,
    };

    /// <summary>The green top row of a grass block's sides.</summary>
    private static bool GrassFringe(IsoMat mat, SpritePx p) => mat.Tex == MatTex.Grass && p.Face != IsoFace.Top && p.Row == 0;

    private static uint Shade16(IsoMat mat, SpritePx p, bool accent)
    {
        if (accent) return IsoPalette.Indexed | (uint)IsoPalette.To16(mat.Accent);
        if (GrassFringe(mat, p)) return IsoPalette.Indexed | mat.C16[0];
        return IsoPalette.Indexed | mat.C16[FaceIndex(p.Face)];
    }

    private static uint Shade256(IsoMat mat, SpritePx p, int n, bool accent)
    {
        if (accent) return IsoPalette.Indexed | (uint)IsoPalette.To256(mat.Accent);
        if (GrassFringe(mat, p)) return IsoPalette.Indexed | mat.C256[0];
        if (mat.Tex is MatTex.Speckle or MatTex.Leaves && n < 60)
            return IsoPalette.Indexed | (uint)IsoPalette.To256(IsoPalette.Scale(p.Face == IsoFace.Top ? mat.Top : mat.Side, IsoPalette.FaceShade(p.Face) * 0.7f));
        if (mat.Tex == MatTex.Lava && n < 80) return IsoPalette.Indexed | (uint)IsoPalette.To256(mat.Accent);
        return IsoPalette.Indexed | mat.C256[FaceIndex(p.Face)];
    }

    private static uint ShadeTrue(IsoMat mat, SpritePx p, int n, bool accent, float depth, IsoSprite s)
    {
        uint baseC = p.Face == IsoFace.Top ? mat.Top : mat.Side;
        float k = IsoPalette.FaceShade(p.Face);
        if ((mat.Flags & MatFlags.Emissive) != 0) k = 1f - (1f - k) * 0.4f;
        if ((uint)mat.Tex < (uint)Textures.Length && Textures[(int)mat.Tex] is { } texture) texture(mat, p, n, accent, s, ref baseC, ref k);
        return IsoPalette.Scale(baseC, k * depth);
    }

    /// <summary>A material texture: varies the base colour and the shade of one sprite pixel (n = its noise, 0..255).</summary>
    private delegate void Texture(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k);

    /// <summary>The texture per <see cref="MatTex"/> (null = plain).</summary>
    private static readonly Texture?[] Textures = BuildTextures();

    private static Texture?[] BuildTextures()
    {
        var t = new Texture?[Enum.GetValues<MatTex>().Length];
        t[(int)MatTex.Noise] = NoiseTex;
        t[(int)MatTex.Speckle] = SpeckleTex;
        t[(int)MatTex.Grass] = GrassTex;
        t[(int)MatTex.Log] = LogTex;
        t[(int)MatTex.Leaves] = LeavesTex;
        t[(int)MatTex.Planks] = PlanksTex;
        t[(int)MatTex.Chest] = ChestTex;
        t[(int)MatTex.Furnace] = FurnaceTex;
        t[(int)MatTex.LitFurnace] = FurnaceTex;
        t[(int)MatTex.Bench] = BenchTex;
        t[(int)MatTex.Lava] = LavaTex;
        t[(int)MatTex.Water] = WaterTex;
        return t;
    }

    private static void NoiseTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k) => k *= 0.94f + n % 13 / 100f;

    private static void SpeckleTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (mat.Accent != 0 && n % 11 == 0) baseC = mat.Accent;
        else if (n < 70) k *= 0.74f;
        else if (n > 215) k *= 1.16f;
    }

    private static void GrassTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (p.Face == IsoFace.Top) k *= 0.9f + n % 19 / 100f;
        else if (p.Row == 0) { baseC = mat.Accent; k *= 0.95f; }
        else k *= 0.93f + n % 13 / 100f;
    }

    private static void LogTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (p.Face == IsoFace.Top) { if (!p.Inner) baseC = mat.Side; else if (n % 3 == 0) baseC = mat.Accent; }
        else if ((p.X & 1) == 0) k *= 0.8f;
    }

    private static void LeavesTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (n < 64) k *= 0.6f;
        else if (n > 200) k *= 1.2f;
    }

    private static void PlanksTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if ((p.Y & 1) == 1) k *= 0.87f;
    }

    private static void ChestTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (accent) { baseC = mat.Accent; k = 1f; }
        else if (p.Face != IsoFace.Top && p.Row == (s.C > 2 ? 1 : 0)) k *= 0.62f; // lid seam
        else if (p.Face == IsoFace.Top && p.Edge && s.Zoom >= 2) k *= 0.8f;
    }

    private static void FurnaceTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (accent) { baseC = mat.Accent; k = 1f; }
        else k *= 0.95f + n % 9 / 100f;
    }

    private static void BenchTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (accent) { baseC = mat.Accent; }
        else if (p.Face == IsoFace.Top && ((p.X + p.Y) & 1) == 0) baseC = IsoPalette.Mix(mat.Top, mat.Accent, 0.35f);
    }

    private static void LavaTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (n < 45) baseC = mat.Accent;
        else k *= 0.88f + n % 21 / 100f;
    }

    private static void WaterTex(IsoMat mat, SpritePx p, int n, bool accent, IsoSprite s, ref uint baseC, ref float k)
    {
        if (p.Face == IsoFace.Top && n % 5 == 0) k *= 1.15f;
    }
}
