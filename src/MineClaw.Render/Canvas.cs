namespace MineClaw.Render;

/// <summary>A tiny 0xRRGGBB pixel canvas with the few primitives the image views need. Everything clips.</summary>
public sealed class Canvas
{
    public int Width { get; }
    public int Height { get; }
    public uint[] Px { get; }

    public Canvas(int width, int height, uint fill = 0)
    {
        Width = width;
        Height = height;
        Px = new uint[width * height];
        if (fill != 0) Array.Fill(Px, fill);
    }

    public Canvas(int width, int height, uint[] px)
    {
        if (px.Length < width * height) throw new ArgumentException("pixel buffer too small");
        Width = width;
        Height = height;
        Px = px;
    }

    public uint Get(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Px[y * Width + x] : 0;

    public void Set(int x, int y, uint c)
    {
        if ((uint)x < (uint)Width && (uint)y < (uint)Height) Px[y * Width + x] = c & 0xffffff;
    }

    /// <summary>Mixes <paramref name="c"/> over the pixel (alpha 0..1).</summary>
    public void Blend(int x, int y, uint c, float alpha)
    {
        if ((uint)x < (uint)Width && (uint)y < (uint)Height) Px[y * Width + x] = IsoPalette.Mix(Px[y * Width + x], c, alpha);
    }

    public void Fill(int x, int y, int w, int h, uint c)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        for (int yy = y0; yy < y1; yy++) Array.Fill(Px, c & 0xffffff, yy * Width + x0, Math.Max(0, x1 - x0));
    }

    public void FillBlend(int x, int y, int w, int h, uint c, float alpha)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++) Blend(xx, yy, c, alpha);
    }

    /// <summary>Rectangle outline <paramref name="t"/> pixels thick, drawn inside (x, y, w, h); dash &gt; 0 = dashed.</summary>
    public void Outline(int x, int y, int w, int h, uint c, int t = 1, int dash = 0)
    {
        for (int k = 0; k < t; k++)
        {
            for (int i = x + k; i < x + w - k; i++)
            {
                if (dash > 0 && (i - x) / dash % 2 == 1) continue;
                Set(i, y + k, c);
                Set(i, y + h - 1 - k, c);
            }
            for (int j = y + k; j < y + h - k; j++)
            {
                if (dash > 0 && (j - y) / dash % 2 == 1) continue;
                Set(x + k, j, c);
                Set(x + w - 1 - k, j, c);
            }
        }
    }

    public void Disc(int cx, int cy, int r, uint c)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r + r / 2) Set(cx + dx, cy + dy, c);
    }

    public void Ring(int cx, int cy, int r, uint c)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int d = dx * dx + dy * dy;
                if (d <= r * r + r / 2 && d > (r - 1) * (r - 1) + (r - 1) / 2) Set(cx + dx, cy + dy, c);
            }
    }

    public void Line(int x0, int y0, int x1, int y1, uint c)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1, err = dx + dy;
        for (int guard = 0; guard < 4096; guard++)
        {
            Set(x0, y0, c);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    public void Text(int x, int y, string text, uint c) => PixelFont.Draw(this, x, y, text, c);

    /// <summary>Text on a dark chip (1 px padding). Returns the chip rectangle.</summary>
    public (int X, int Y, int W, int H) Chip(int x, int y, string text, uint fg, uint bg = 0x101318, float alpha = 0.82f)
    {
        int w = PixelFont.Width(text) + 4, h = PixelFont.H + 4;
        FillBlend(x, y, w, h, bg, alpha);
        Text(x + 2, y + 2, text, fg);
        return (x, y, w, h);
    }
}
