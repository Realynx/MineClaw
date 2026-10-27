using System.Text;
using MineClaw.App.Viewport;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MineClaw.App.Tui;

/// <summary>
/// Spectre renderable over an <see cref="IsoFrame"/>: one segment per run of equal style per row (no markup parsing), styles
/// cached across frames. Colours are passed through as RGB or palette index; Spectre downgrades for the terminal's profile.
/// </summary>
public sealed class CanvasRenderable : IRenderable
{
    private readonly IsoFrame _frame;
    private readonly Dictionary<(uint, uint, IsoCellFlags), Style> _styles;

    public CanvasRenderable(IsoFrame frame, Dictionary<(uint, uint, IsoCellFlags), Style> styleCache)
    {
        _frame = frame;
        _styles = styleCache;
    }

    public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(_frame.Width, maxWidth), Math.Min(_frame.Width, maxWidth));

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        var f = _frame;
        int w = Math.Min(f.Width, maxWidth);
        var segs = new List<Segment>(f.Height * 8);
        var sb = new StringBuilder(w);
        for (int r = 0; r < f.Height; r++)
        {
            int c = 0;
            while (c < w)
            {
                ref var first = ref f.Cells[r * f.Width + c];
                var key = Key(first);
                sb.Clear();
                while (c < w)
                {
                    ref var cell = ref f.Cells[r * f.Width + c];
                    if (Key(cell) != key) break;
                    sb.Append(cell.Ch);
                    c++;
                }
                segs.Add(new Segment(sb.ToString(), StyleOf(key)));
            }
            if (r < f.Height - 1) segs.Add(Segment.LineBreak);
        }
        return segs;
    }

    private static (uint, uint, IsoCellFlags) Key(in IsoCell c)
    {
        var fl = c.Flags & (IsoCellFlags.Fg | IsoCellFlags.Bg | IsoCellFlags.Bold);
        return ((fl & IsoCellFlags.Fg) != 0 ? c.Fg : 0, (fl & IsoCellFlags.Bg) != 0 ? c.Bg : 0, fl);
    }

    private Style StyleOf((uint Fg, uint Bg, IsoCellFlags Flags) k)
    {
        if (_styles.TryGetValue(k, out var s)) return s;
        if (_styles.Count > 8192) _styles.Clear();
        s = new Style(
            (k.Flags & IsoCellFlags.Fg) != 0 ? ToColor(k.Fg) : null,
            (k.Flags & IsoCellFlags.Bg) != 0 ? ToColor(k.Bg) : null,
            (k.Flags & IsoCellFlags.Bold) != 0 ? Decoration.Bold : Decoration.None);
        _styles[k] = s;
        return s;
    }

    public static Color ToColor(uint c) =>
        (c & IsoPalette.Indexed) != 0 ? Color.FromInt32((int)(c & 0xff)) : new Color((byte)(c >> 16), (byte)(c >> 8), (byte)c);
}
