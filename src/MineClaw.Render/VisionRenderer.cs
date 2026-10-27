using System.Globalization;
using System.Text;
using MineClaw.Agent.Telemetry;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>
/// Renders <c>see</c> pictures for the model: pixel-art blocks with a 1-block grid, world coordinates every 5 blocks on the
/// borders, a compass, the bot (@), labelled players / hive bots, labelled structures from memory, void shaded distinctly,
/// the target highlighted and the latest pointed block outlined. Sizes stay at or under ~384×384 (≈200 tokens).
/// Results are cached by a hash of the blocks in view plus the overlays, so asking again about an unchanged area does not
/// re-render. One instance per bot (thread-safe).
/// </summary>
public sealed class VisionRenderer
{
    public const int MaxSide = 384;
    private readonly object _lock = new();
    private readonly LinkedList<VisionImage> _cache = new();
    private readonly IsoRenderer _iso = new();

    public int CacheCapacity { get; init; } = 8;
    public int Renders { get; private set; }
    public int CacheHits { get; private set; }

    // ------------------------------------------------------------------ colours

    private const uint Margin = 0x0d0f14, VoidC = 0x0a1020, VoidHatch = 0x1a2846, UnknownC = 0x2a2a2f, UnknownDot = 0x55555c,
        DeepC = 0x1d2230, Gold = 0xffd84a, Magenta = 0xff4fd8, Cyan = 0x5fe8ff, SelfC = 0xffd75f, HiveC = 0xff9f43, MobC = 0xff3b3b,
        Ink = 0x14161c, Light = 0xe8ecf4, NorthBg = 0xb02a2a, ChipBg = 0x2c313c;

    private static readonly uint[] PlayerColours = [0x00e5ff, 0xff6bff, 0x6bff8f, 0x9fb4ff, 0xffef5f, 0x5fd7ff];

    public static uint PlayerColour(string? name)
    {
        if (string.IsNullOrEmpty(name)) return PlayerColours[0];
        unchecked
        {
            int h = 17;
            foreach (char ch in name.ToLowerInvariant()) h = h * 31 + ch;
            return PlayerColours[(h & 0x7fffffff) % PlayerColours.Length];
        }
    }

    // ------------------------------------------------------------------ volume

    /// <summary>Blocks around the centre: (2R+1)² columns × the vertical window, VoxelVolume layout, plus per-column flags.</summary>
    internal sealed class Slab
    {
        public int X0, Y0, Z0, N, Sy;
        public byte[] Cells = [];
        public byte[] Meta = [];
        public bool[] Loaded = [];
        /// <summary>Something exists below the window in that column (not open void).</summary>
        public bool[] Below = [];
        public ulong Hash;

        public int At(int x, int y, int z)
        {
            int ix = x - X0, iy = y - Y0, iz = z - Z0;
            if ((uint)ix >= (uint)N || (uint)iy >= (uint)Sy || (uint)iz >= (uint)N) return 0;
            return Cells[(iy * N + iz) * N + ix];
        }

        public int MetaAt(int x, int y, int z)
        {
            int ix = x - X0, iy = y - Y0, iz = z - Z0;
            if ((uint)ix >= (uint)N || (uint)iy >= (uint)Sy || (uint)iz >= (uint)N) return 0;
            return Meta[(iy * N + iz) * N + ix];
        }

        public bool IsLoaded(int x, int z)
        {
            int ix = x - X0, iz = z - Z0;
            return (uint)ix < (uint)N && (uint)iz < (uint)N && Loaded[iz * N + ix];
        }

        public bool HasBelow(int x, int z)
        {
            int ix = x - X0, iz = z - Z0;
            return (uint)ix < (uint)N && (uint)iz < (uint)N && Below[iz * N + ix];
        }
    }

    internal static Slab Extract(VisionScene s)
    {
        int r = Math.Clamp(s.Radius, 2, 32), n = 2 * r + 1;
        int down = Math.Max(s.Down, Math.Max(s.SideDown, s.IsoDown)), up = Math.Max(s.Up, Math.Max(s.SideUp, s.IsoUp));
        var slab = new Slab { X0 = s.Cx - r, Z0 = s.Cz - r, Y0 = s.Cy - down, N = n, Sy = up + down + 1 };
        slab.Cells = new byte[n * slab.Sy * n];
        slab.Meta = new byte[n * slab.Sy * n];
        slab.Loaded = new bool[n * n];
        slab.Below = new bool[n * n];
        ulong h = 14695981039346656037UL;
        void Mix(int v) { unchecked { for (int k = 0; k < 4; k++) { h ^= (byte)(v >> (8 * k)); h *= 1099511628211UL; } } }
        Mix(slab.X0); Mix(slab.Y0); Mix(slab.Z0); Mix(n); Mix(slab.Sy);
        for (int iz = 0; iz < n; iz++)
            for (int ix = 0; ix < n; ix++)
            {
                int wx = slab.X0 + ix, wz = slab.Z0 + iz;
                bool loaded = s.Blocks.IsLoaded(wx, wz);
                slab.Loaded[iz * n + ix] = loaded;
                if (!loaded) { Mix(-1); continue; }
                for (int iy = 0; iy < slab.Sy; iy++)
                {
                    int wy = slab.Y0 + iy;
                    if ((uint)wy >= WorldState.Height) continue;
                    int id = s.Blocks.GetBlock(wx, wy, wz);
                    byte b = (byte)Math.Clamp(id, 0, 255);
                    slab.Cells[(iy * n + iz) * n + ix] = b;
                    unchecked { h ^= b; h *= 1099511628211UL; }
                    if (b != 0)
                    {
                        byte mb = (byte)(s.Blocks.GetMeta(wx, wy, wz) & 15);
                        slab.Meta[(iy * n + iz) * n + ix] = mb;
                        unchecked { h ^= mb; h *= 1099511628211UL; }
                    }
                }
                bool below = false;
                for (int wy = slab.Y0 - 1; wy >= 0 && !below; wy--) below = s.Blocks.GetBlock(wx, wy, wz) != 0;
                slab.Below[iz * n + ix] = below;
                Mix(below ? 1 : 0);
            }
        slab.Hash = h;
        return slab;
    }

    private static ulong OverlayHash(VisionScene s)
    {
        var sb = new StringBuilder();
        sb.Append(s.View).Append('|').Append(s.Radius).Append('|').Append(s.Rotation).Append('|').Append(s.Nether).Append('|')
          .Append(s.Atlas != null).Append(s.Light != null ? Lighting.SkylightSubtracted(s.Time) : -1).Append('|')
          .Append(s.Self).Append('|').Append(s.Target).Append('|').Append(s.Pointed).Append(s.PointedBy).Append('|');
        foreach (var e in s.Entities) sb.Append(e.Kind).Append(e.Name).Append((int)Math.Floor(e.X)).Append(',').Append((int)Math.Floor(e.Y)).Append(',').Append((int)Math.Floor(e.Z)).Append(';');
        foreach (var m in s.Marks) sb.Append(m).Append(';');
        foreach (var p in s.Path) sb.Append(p).Append(';');
        ulong h = 14695981039346656037UL;
        unchecked { foreach (char c in sb.ToString()) { h ^= c; h *= 1099511628211UL; } }
        return h;
    }

    /// <summary>Renders (or returns the cached picture of) a scene.</summary>
    public VisionImage Render(VisionScene s)
    {
        var slab = Extract(s);
        ulong key = slab.Hash * 31 + OverlayHash(s);
        lock (_lock)
        {
            for (var node = _cache.First; node != null; node = node.Next)
                if (node.Value.Key == key)
                {
                    _cache.Remove(node);
                    _cache.AddFirst(node);
                    CacheHits++;
                    return node.Value;
                }
        }
        var img = s.View switch
        {
            VisionView.Side => RenderSide(s, slab, key),
            VisionView.Iso => RenderIso(s, slab, key),
            _ => RenderTop(s, slab, key),
        };
        lock (_lock)
        {
            Renders++;
            _cache.AddFirst(img);
            while (_cache.Count > Math.Max(1, CacheCapacity)) _cache.RemoveLast();
        }
        return img;
    }

    // ------------------------------------------------------------------ shared helpers

    /// <summary>Cell size in pixels so that 2R+1 cells fit in ~352 px (6..16).</summary>
    public static int CellSize(int radius) => Math.Clamp(352 / (2 * Math.Clamp(radius, 2, 32) + 1), 6, 16);

    private static bool IsDoor(int id) => id is Blocks.WoodenDoor or Blocks.IronDoor;

    private static char? Letter(int id) => id switch
    {
        Blocks.Chest or Blocks.LockedChest => 'C',
        Blocks.Furnace or Blocks.LitFurnace => 'F',
        Blocks.Workbench => 'W',
        Blocks.WoodenDoor or Blocks.IronDoor => 'D',
        Blocks.Portal => 'P',
        _ => null,
    };

    private static uint Lum(uint c) => (((c >> 16) & 0xff) * 3 + ((c >> 8) & 0xff) * 6 + (c & 0xff)) / 10;

    private static uint InkFor(uint bg) => Lum(bg) > 110 ? Ink : Light;

    private static string Fmt(int x, int y, int z) => string.Create(CultureInfo.InvariantCulture, $"{x},{y},{z}");

    private static string Range(int a, int b) => a == b ? a.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{a}..{b}");

    private static string MarkWhere(VisionMark m) => $"{Range(m.MinX, m.MaxX)},{Range(m.MinY, m.MaxY)},{Range(m.MinZ, m.MaxZ)}";

    /// <summary>Short label text: "cobble generator" → "gen", at most 16 characters.</summary>
    public static string ShortLabel(string text)
    {
        var t = text.Replace("cobble generator", "gen", StringComparison.OrdinalIgnoreCase)
            .Replace("cobble_generator", "gen", StringComparison.OrdinalIgnoreCase)
            .Replace("generator", "gen", StringComparison.OrdinalIgnoreCase)
            .Replace('_', ' ').Trim();
        var sb = new StringBuilder(t.Length);
        foreach (char c in t) sb.Append(PixelFont.Has(c) ? c : ' ');
        t = sb.ToString().Trim();
        return t.Length <= 16 ? t : t[..16].TrimEnd();
    }

    internal sealed class Placer(int x0, int y0, int x1, int y1)
    {
        private readonly List<(int X, int Y, int W, int H)> _taken = new();

        public void Reserve(int x, int y, int w, int h) => _taken.Add((x, y, w, h));

        private bool Free(int x, int y, int w, int h)
        {
            if (x < x0 || y < y0 || x + w > x1 || y + h > y1) return false;
            foreach (var t in _taken)
                if (x < t.X + t.W && t.X < x + w && y < t.Y + t.H && t.Y < y + h) return false;
            return true;
        }

        /// <summary>A free spot for a chip of w×h next to an anchor rectangle (right, left, above, below, diagonals).</summary>
        public (int X, int Y)? Find(int ax, int ay, int aw, int ah, int w, int h)
        {
            (int, int)[] c =
            [
                (ax + aw + 1, ay + ah / 2 - h / 2), (ax - w - 1, ay + ah / 2 - h / 2),
                (ax + aw / 2 - w / 2, ay - h - 1), (ax + aw / 2 - w / 2, ay + ah + 1),
                (ax + aw + 1, ay - h), (ax - w - 1, ay - h), (ax + aw + 1, ay + ah), (ax - w - 1, ay + ah),
            ];
            foreach (var (x, y) in c)
                if (Free(x, y, w, h)) { Reserve(x, y, w, h); return (x, y); }
            return null;
        }

        /// <summary>A free spot at (x, y) or stacked just above / below it (centred tags, e.g. name tags in the eye view).</summary>
        public (int X, int Y)? FindNear(int x, int y, int w, int h)
        {
            foreach (int k in new[] { 0, -1, 1, -2, 2, -3 })
            {
                int yy = y + k * (h + 1), xx = Math.Clamp(x, x0, Math.Max(x0, x1 - w));
                if (Free(xx, yy, w, h)) { Reserve(xx, yy, w, h); return (xx, yy); }
            }
            return null;
        }
    }

    private static void SelfMarker(Canvas c, int cx, int cy, int cs)
    {
        int rad = Math.Max(2, cs / 2);
        c.Disc(cx, cy, rad, SelfC);
        c.Ring(cx, cy, rad, Ink);
        if (cs >= 10) c.Text(cx - 2, cy - 3, "@", Ink);
    }

    private static void EntityMarker(Canvas c, VisionEntity e, int cx, int cy, int cs, bool hidden = false)
    {
        switch (e.Kind)
        {
            case "item":
                c.Fill(cx - 1, cy - 1, 3, 3, 0xf2e46a);
                return;
            case "mob":
            {
                int r = Math.Max(2, cs / 3);
                if (hidden) c.Ring(cx, cy, r, MobC); else c.Disc(cx, cy, r, MobC);
                return;
            }
            default:
            {
                uint col = e.Kind == "hive" ? HiveC : PlayerColour(e.Name);
                int r = Math.Max(2, cs / 2);
                if (hidden) { c.Ring(cx, cy, r, col); c.Ring(cx, cy, r - 1, Ink); return; }
                c.Disc(cx, cy, r, col);
                c.Ring(cx, cy, r, Ink);
                char g = e.Glyph ?? (string.IsNullOrEmpty(e.Name) ? 'P' : char.ToUpperInvariant(e.Name[0]));
                if (cs >= 10 && PixelFont.Has(g)) c.Text(cx - 2, cy - 3, g.ToString(), Ink);
                return;
            }
        }
    }

    private static string EntityLabel(VisionEntity e) => e.Name is { Length: > 0 } n ? (n.Length > 14 ? n[..14] : n) : e.Kind;

    // ------------------------------------------------------------------ textures + light

    /// <summary>Texel (0xRRGGBB) of a block face from the atlas, or null (no atlas / no tile / transparent texel).</summary>
    private static uint? Tex(VisionScene s, int id, int meta, Face f, float u, float v)
    {
        if (s.Atlas is not { } a || BlockTextures.Texel(a, id, meta, f, u, v) is not { } t || t >> 24 < 128) return null;
        return t & 0xffffff;
    }

    /// <summary>Vanilla face brightness for the picture (flat per-face light, floored at <see cref="VisionScene.MinBrightness"/>);
    /// 1 without a light source. The face shade factor is left out: each view applies its own.</summary>
    private static float LightK(VisionScene s, int id, int x, int y, int z, Face f, float floor)
    {
        if (s.Light is not { } l || BlockTextures.Emissive(id)) return 1f;
        var (dx, dy, dz) = Lighting.Normal(f);
        // A cut face (side slice) can sit against an opaque block: take the light above it instead.
        if (FaceLight.Opaque(l.Block(x + dx, y + dy, z + dz))) { dx = 0; dy = 1; dz = 0; if (FaceLight.Opaque(l.Block(x, y + 1, z))) return 1f; }
        int lvl = FaceLight.CellLevel(l, x + dx, y + dy, z + dz, s.Nether ? 0 : Lighting.SkylightSubtracted(s.Time));
        if (lvl < 0) return 0.85f;
        return Math.Max(Math.Max(floor, s.MinBrightness), Lighting.Brightness(lvl, s.Nether));
    }

    // ------------------------------------------------------------------ top view

    private VisionImage RenderTop(VisionScene s, Slab slab, ulong key)
    {
        int n = slab.N, cs = CellSize(s.Radius);
        int x0 = slab.X0, z0 = slab.Z0;
        int zlw = 0;
        for (int i = 0; i < n; i++) if (Mod5(z0 + i) == 0) zlw = Math.Max(zlw, PixelFont.Width((z0 + i).ToString(CultureInfo.InvariantCulture)));
        int ML = 2 + 9 + 2 + zlw + 3, MT = 11, MR = 12, MB = 12;
        int W = ML + n * cs + MR, H = MT + n * cs + MB;
        var c = new Canvas(W, H, Margin);
        int mx = ML, my = MT;
        int floorY = s.Cy - 1;

        // 1. Columns.
        for (int iz = 0; iz < n; iz++)
            for (int ix = 0; ix < n; ix++)
            {
                int wx = x0 + ix, wz = z0 + iz, px = mx + ix * cs, py = my + iz * cs;
                if (!slab.IsLoaded(wx, wz)) { Unknown(c, px, py, cs); continue; }
                int baseId = 0, baseY = 0, deco = 0;
                for (int wy = s.Cy + s.Up; wy >= s.Cy - s.Down; wy--)
                {
                    int id = slab.At(wx, wy, wz);
                    if (id == 0 || IsoPalette.Mat(id) is not { } m) continue;
                    if (IsDoor(id)) { baseId = id; baseY = wy; break; }
                    if ((m.Flags & (MatFlags.Small | MatFlags.Flat)) != 0) { if (deco == 0) deco = id; continue; }
                    baseId = id; baseY = wy;
                    break;
                }
                if (baseId == 0)
                {
                    if (slab.HasBelow(wx, wz)) c.Fill(px, py, cs, cs, DeepC);
                    else VoidCell(c, px, py, cs);
                    if (deco != 0) Deco(c, px, py, cs, deco);
                    continue;
                }
                var mat = IsoPalette.Mat(baseId)!;
                int rel = baseY - floorY;
                float k = rel >= 1 ? 1.1f : rel == 0 ? 1f : rel == -1 ? 0.7f : rel == -2 ? 0.56f : 0.44f;
                k *= LightK(s, baseId, wx, baseY, wz, Face.Up, 0.65f); // a map: light is a hint, never hides layout
                uint col = IsoPalette.Scale(mat.Top, k);
                c.Fill(px, py, cs, cs, col);
                if (s.Atlas != null)
                {
                    int meta = slab.MetaAt(wx, baseY, wz);
                    for (int ty = 0; ty < cs; ty++)
                        for (int tx = 0; tx < cs; tx++)
                            if (Tex(s, baseId, meta, Face.Up, (tx + 0.5f) / cs, (ty + 0.5f) / cs) is { } t) c.Set(px + tx, py + ty, IsoPalette.Scale(t, k));
                    col = IsoPalette.Scale(BlockTextures.For(baseId, meta, Face.Up) is { IsNone: false } tr && !BlockTextures.UsePalette(baseId)
                        ? BlockTextures.Tinted(s.Atlas.Average(tr.Tile), tr.Tint) & 0xffffff : mat.Top, k);
                }
                if (rel >= 1)
                {
                    // Raised (wall at body height): bevel, light top-left, dark bottom-right.
                    uint hi = IsoPalette.Mix(col, 0xffffff, 0.45f), lo = IsoPalette.Scale(col, 0.45f);
                    int t = cs >= 12 ? 2 : 1;
                    c.Fill(px, py, cs, t, hi);
                    c.Fill(px, py, t, cs, hi);
                    c.Fill(px, py + cs - t, cs, t, lo);
                    c.Fill(px + cs - t, py, t, cs, lo);
                }
                if (IsDoor(baseId)) { c.Fill(px + 1, py + 1, cs - 2, cs - 2, 0x6b4a1e); c.Outline(px, py, cs, cs, 0xfff2b0); col = 0x6b4a1e; }
                if (Letter(baseId) is { } L && cs >= 8) c.Text(px + (cs - 5) / 2, py + (cs - 7) / 2, L.ToString(), IsDoor(baseId) ? 0xfff2b0u : InkFor(col));
                if (deco != 0) Deco(c, px, py, cs, deco);
            }

        // 2. Grid: every cell boundary, stronger on multiples of 5.
        for (int i = 0; i <= n; i++)
        {
            bool major = Mod5(x0 + i) == 0;
            int gx = mx + Math.Min(i * cs, n * cs - 1);
            for (int py = my; py < my + n * cs; py++) GridPx(c, gx, py, major);
            bool majorZ = Mod5(z0 + i) == 0;
            int gy = my + Math.Min(i * cs, n * cs - 1);
            for (int px = mx; px < mx + n * cs; px++) GridPx(c, px, gy, majorZ);
        }

        // 3. Compass chips at the edge midpoints, then coordinate labels where they don't collide.
        var nChip = (X: mx + n * cs / 2 - 6, Y: 0, W: 12, H: 11);
        c.Fill(nChip.X, nChip.Y, nChip.W, nChip.H - 1, NorthBg);
        c.Text(nChip.X + 1, 2, "N", Light);
        c.Text(nChip.X + 7, 2, "^", Light);
        c.Fill(mx + n * cs / 2 - 4, H - 11, 9, 11, ChipBg);
        c.Text(mx + n * cs / 2 - 2, H - 9, "S", Light);
        c.Fill(W - 11, my + n * cs / 2 - 5, 9, 11, ChipBg);
        c.Text(W - 9, my + n * cs / 2 - 3, "E", Light);
        c.Fill(1, my + n * cs / 2 - 5, 9, 11, ChipBg);
        c.Text(3, my + n * cs / 2 - 3, "W", Light);
        for (int i = 0; i < n; i++)
        {
            int wx = x0 + i;
            if (Mod5(wx) != 0) continue;
            var t = wx.ToString(CultureInfo.InvariantCulture);
            int tw = PixelFont.Width(t), tx = mx + i * cs + cs / 2 - tw / 2;
            if (tx < nChip.X + nChip.W + 1 && nChip.X < tx + tw + 1) continue;
            c.Text(tx, 2, t, 0xb8c0d0);
        }
        int wMid = my + n * cs / 2;
        for (int i = 0; i < n; i++)
        {
            int wz = z0 + i;
            if (Mod5(wz) != 0) continue;
            var t = wz.ToString(CultureInfo.InvariantCulture);
            int ty = my + i * cs + cs / 2 - 3;
            c.Text(ML - 3 - PixelFont.Width(t), ty, t, 0xb8c0d0);
        }
        _ = wMid;

        // 4. Overlays.
        var placer = new Placer(mx, my, mx + n * cs, my + n * cs);
        (int X, int Y) Cell(double x, double z) => (mx + ((int)Math.Floor(x) - x0) * cs, my + ((int)Math.Floor(z) - z0) * cs);
        bool InView(double x, double z) => Math.Floor(x) >= x0 && Math.Floor(x) < x0 + n && Math.Floor(z) >= z0 && Math.Floor(z) < z0 + n;
        var labels = new List<string>();

        foreach (var m in s.Marks.Where(m => m.Box))
        {
            int bx0 = Math.Max(m.MinX, x0), bx1 = Math.Min(m.MaxX, x0 + n - 1), bz0 = Math.Max(m.MinZ, z0), bz1 = Math.Min(m.MaxZ, z0 + n - 1);
            if (bx0 > bx1 || bz0 > bz1) continue;
            var (px, py) = Cell(bx0, bz0);
            c.Outline(px, py, (bx1 - bx0 + 1) * cs, (bz1 - bz0 + 1) * cs, Gold, 1, dash: 3);
        }
        foreach (var p in s.Path)
            if (InView(p.X, p.Z)) { var (px, py) = Cell(p.X, p.Z); c.Fill(px + cs / 2 - 1, py + cs / 2 - 1, 3, 3, Cyan); }
        if (s.Target is { } tg && InView(tg.X, tg.Z)) { var (px, py) = Cell(tg.X, tg.Z); c.Outline(px - 1, py - 1, cs + 2, cs + 2, Gold, 2); placer.Reserve(px - 1, py - 1, cs + 2, cs + 2); }
        if (s.Pointed is { } pt && InView(pt.X, pt.Z)) { var (px, py) = Cell(pt.X, pt.Z); c.Outline(px - 1, py - 1, cs + 2, cs + 2, Magenta, 2); placer.Reserve(px - 1, py - 1, cs + 2, cs + 2); }

        // Entities: items, mobs, players, hive, then self on top.
        var ents = s.Entities.Where(e => InView(e.X, e.Z)).OrderBy(e => e.Kind switch { "item" => 0, "mob" => 1, "player" => 2, _ => 3 }).ToList();
        foreach (var e in ents)
        {
            var (px, py) = Cell(e.X, e.Z);
            EntityMarker(c, e, px + cs / 2, py + cs / 2, cs);
            if (e.Kind is "player" or "hive") placer.Reserve(px, py, cs, cs);
        }
        if (s.Self is { } me && InView(me.X, me.Z))
        {
            var (px, py) = Cell(me.X, me.Z);
            SelfMarker(c, px + cs / 2, py + cs / 2, cs);
            placer.Reserve(px, py, cs, cs);
        }

        // Labels: players first, then the pointed block, then marks.
        foreach (var e in ents.Where(e => e.Kind is "player" or "hive"))
        {
            var (px, py) = Cell(e.X, e.Z);
            var t = EntityLabel(e);
            if (placer.Find(px, py, cs, cs, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, e.Kind == "hive" ? HiveC : PlayerColour(e.Name));
        }
        if (s.Pointed is { } pp && InView(pp.X, pp.Z))
        {
            var (px, py) = Cell(pp.X, pp.Z);
            var t = "POINTED";
            if (placer.Find(px - 1, py - 1, cs + 2, cs + 2, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, Magenta);
        }
        foreach (var m in s.Marks)
        {
            if (!InView(Math.Clamp(m.CentreX, x0, x0 + n - 1), Math.Clamp(m.CentreZ, z0, z0 + n - 1))) continue;
            int bx0 = Math.Max(m.MinX, x0), bx1 = Math.Min(m.MaxX, x0 + n - 1), bz0 = Math.Max(m.MinZ, z0), bz1 = Math.Min(m.MaxZ, z0 + n - 1);
            if (bx0 > bx1 || bz0 > bz1) continue;
            var (px, py) = Cell(bx0, bz0);
            int aw = (bx1 - bx0 + 1) * cs, ah = (bz1 - bz0 + 1) * cs;
            var t = ShortLabel(m.Text);
            if (t.Length == 0) continue;
            labels.Add($"{t} {MarkWhere(m)}");
            // Plain chests/furnaces/benches already carry their letter: a chip would only hide neighbouring cells.
            if (IsLettered(t)) continue;
            if (!m.Box) { c.Fill(px + cs / 2 - 1, py + cs / 2 - 1, 3, 3, Light); }
            if (placer.Find(px, py, aw, ah, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, m.Box ? Gold : Light);
        }

        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"top r{s.Radius} centre {Fmt(s.Cx, s.Cy, s.Cz)}, {W}x{H}px ~{(W * H + 749) / 750}tok. ");
        legend.Append(CultureInfo.InvariantCulture, $"N up, 1 cell=1 block, coords every 5 (x top, z left). ");
        if (s.Self is { } self) legend.Append(CultureInfo.InvariantCulture, $"@=you {Fmt(self.X, self.Y, self.Z)}. ");
        legend.Append(CultureInfo.InvariantCulture, $"Bevel=block at y{s.Cy}+ (wall), plain=floor y{floorY}, darker=lower, navy hatch=void, grey dots=unknown. C chest F furnace W bench D door.");
        if (s.Target != null) legend.Append(" Gold box=target.");
        if (s.Pointed is { } ptd) legend.Append(CultureInfo.InvariantCulture, $" Pink box=pointed {Fmt(ptd.X, ptd.Y, ptd.Z)}{(s.PointedBy != null ? " by " + s.PointedBy : "")}.");
        return Finish(c, key, legend.ToString(), labels);
    }

    private static bool IsLettered(string label) => label.ToLowerInvariant() is "chest" or "furnace" or "bench";

    private static int Mod5(int v) => ((v % 5) + 5) % 5;

    private static void GridPx(Canvas c, int x, int y, bool major)
    {
        uint p = c.Get(x, y);
        uint g = Lum(p) < 40 ? (major ? 0x34456cu : 0x1f2a44u) : IsoPalette.Scale(p, major ? 0.42f : 0.68f);
        c.Set(x, y, g);
    }

    private static void VoidCell(Canvas c, int px, int py, int cs)
    {
        c.Fill(px, py, cs, cs, VoidC);
        for (int y = 0; y < cs; y++)
            for (int x = 0; x < cs; x++)
                if (((px + x) + (py + y)) % 5 == 0) c.Set(px + x, py + y, VoidHatch);
    }

    private static void Unknown(Canvas c, int px, int py, int cs)
    {
        c.Fill(px, py, cs, cs, UnknownC);
        for (int y = 1; y < cs; y += 3)
            for (int x = 1; x < cs; x += 3) c.Set(px + x, py + y, UnknownDot);
    }

    private static void Deco(Canvas c, int px, int py, int cs, int id)
    {
        if (IsoPalette.Mat(id) is not { } m) return;
        int d = Math.Max(2, cs / 4);
        c.Fill(px + (cs - d) / 2, py + (cs - d) / 2, d, d, m.Top);
    }

    private static VisionImage Finish(Canvas c, ulong key, string legend, List<string> labels) =>
        new(Png.Encode(c.Px, c.Width, c.Height), c.Width, c.Height, key, legend, labels);

    // ------------------------------------------------------------------ side view (looking north)

    private VisionImage RenderSide(VisionScene s, Slab slab, ulong key)
    {
        int n = slab.N, cs = CellSize(s.Radius);
        int yTop = s.Cy + s.SideUp, yBot = s.Cy - s.SideDown, rows = yTop - yBot + 1;
        int depth = Math.Clamp(s.SideDepth, 0, s.Radius);
        int ylw = 0;
        for (int y = yBot; y <= yTop; y++) if (Mod5(y) == 0 || y == s.Cy) ylw = Math.Max(ylw, PixelFont.Width(y.ToString(CultureInfo.InvariantCulture)));
        int ML = 2 + 9 + 2 + ylw + 3, MT = 12, MR = 12, MB = 11;
        int W = ML + n * cs + MR, H = MT + rows * cs + MB;
        var c = new Canvas(W, H, Margin);
        int mx = ML, my = MT;

        for (int iy = 0; iy < rows; iy++)
            for (int ix = 0; ix < n; ix++)
            {
                int wy = yTop - iy, wx = slab.X0 + ix, px = mx + ix * cs, py = my + iy * cs;
                bool hit = false;
                for (int d = 0; d <= depth && !hit; d++)
                {
                    int wz = s.Cz - d;
                    if (!slab.IsLoaded(wx, wz)) { if (d == 0) { Unknown(c, px, py, cs); hit = true; } break; }
                    int id = slab.At(wx, wy, wz);
                    if (id == 0 || IsoPalette.Mat(id) is not { } m) continue;
                    float k = Math.Max(0.38f, 1f - 0.075f * d) * LightK(s, id, wx, wy, wz, Face.South, 0.55f);
                    uint col = IsoPalette.Scale(m.Side, k);
                    if ((m.Flags & (MatFlags.Small | MatFlags.Flat)) != 0 && !IsDoor(id))
                    {
                        VoidCell(c, px, py, cs);
                        Deco(c, px, py, cs, id);
                        continue; // keep looking behind a torch/flower
                    }
                    c.Fill(px, py, cs, cs, col);
                    if (s.Atlas != null)
                    {
                        int meta = slab.MetaAt(wx, wy, wz);
                        for (int ty = 0; ty < cs; ty++)
                            for (int tx = 0; tx < cs; tx++)
                                if (Tex(s, id, meta, Face.South, (tx + 0.5f) / cs, (ty + 0.5f) / cs) is { } t) c.Set(px + tx, py + ty, IsoPalette.Scale(t, k));
                    }
                    if (d == 0) c.Outline(px, py, cs, cs, IsoPalette.Mix(col, 0xffffff, 0.35f));
                    if (Letter(id) is { } L && cs >= 8) c.Text(px + (cs - 5) / 2, py + (cs - 7) / 2, L.ToString(), InkFor(col));
                    hit = true;
                }
                if (!hit && c.Get(px, py) == Margin) VoidCell(c, px, py, cs);
            }

        for (int i = 0; i <= n; i++)
        {
            int gx = mx + Math.Min(i * cs, n * cs - 1);
            bool major = Mod5(slab.X0 + i) == 0;
            for (int py = my; py < my + rows * cs; py++) GridPx(c, gx, py, major);
        }
        for (int i = 0; i <= rows; i++)
        {
            int gy = my + Math.Min(i * cs, rows * cs - 1);
            bool major = i < rows && Mod5(yTop - i) == 0;
            for (int px = mx; px < mx + n * cs; px++) GridPx(c, px, gy, major);
        }

        c.Chip(mx, 0, "LOOKING N", Light, ChipBg, 1f);
        c.Fill(1, my + rows * cs / 2 - 5, 9, 11, ChipBg);
        c.Text(3, my + rows * cs / 2 - 3, "W", Light);
        c.Fill(W - 11, my + rows * cs / 2 - 5, 9, 11, ChipBg);
        c.Text(W - 9, my + rows * cs / 2 - 3, "E", Light);
        for (int i = 0; i < n; i++)
        {
            int wx = slab.X0 + i;
            if (Mod5(wx) != 0) continue;
            var t = wx.ToString(CultureInfo.InvariantCulture);
            c.Text(mx + i * cs + cs / 2 - PixelFont.Width(t) / 2, H - 9, t, 0xb8c0d0);
        }
        for (int i = 0; i < rows; i++)
        {
            int wy = yTop - i;
            if (Mod5(wy) != 0 && wy != s.Cy) continue;
            var t = wy.ToString(CultureInfo.InvariantCulture);
            c.Text(ML - 3 - PixelFont.Width(t), my + i * cs + cs / 2 - 3, t, wy == s.Cy ? SelfC : 0xb8c0d0);
        }

        var placer = new Placer(mx, my, mx + n * cs, my + rows * cs);
        bool InView(double x, double y, double z) => Math.Floor(x) >= slab.X0 && Math.Floor(x) < slab.X0 + n && Math.Floor(y) <= yTop
            && Math.Floor(y) >= yBot && Math.Floor(z) <= s.Cz + 1 && Math.Floor(z) >= s.Cz - depth;
        (int X, int Y) Cell(double x, double y) => (mx + ((int)Math.Floor(x) - slab.X0) * cs, my + (yTop - (int)Math.Floor(y)) * cs);
        var labels = new List<string>();
        if (s.Target is { } tg && InView(tg.X, tg.Y, tg.Z)) { var (px, py) = Cell(tg.X, tg.Y); c.Outline(px - 1, py - 1, cs + 2, cs + 2, Gold, 2); }
        if (s.Pointed is { } pt && InView(pt.X, pt.Y, pt.Z)) { var (px, py) = Cell(pt.X, pt.Y); c.Outline(px - 1, py - 1, cs + 2, cs + 2, Magenta, 2); placer.Reserve(px - 1, py - 1, cs + 2, cs + 2); }
        foreach (var m in s.Marks.Where(m => m.Box && m.MaxZ >= s.Cz - depth && m.MinZ <= s.Cz + 1))
        {
            int bx0 = Math.Max(m.MinX, slab.X0), bx1 = Math.Min(m.MaxX, slab.X0 + n - 1), by0 = Math.Max(m.MinY, yBot), by1 = Math.Min(m.MaxY, yTop);
            if (bx0 > bx1 || by0 > by1) continue;
            var (px, py) = Cell(bx0, by1);
            c.Outline(px, py, (bx1 - bx0 + 1) * cs, (by1 - by0 + 1) * cs, Gold, 1, dash: 3);
        }
        var ents = s.Entities.Where(e => InView(e.X, e.Y, e.Z)).ToList();
        foreach (var e in ents)
        {
            var (px, py) = Cell(e.X, e.Y);
            EntityMarker(c, e, px + cs / 2, py + cs / 2, cs, hidden: Math.Floor(e.Z) < s.Cz - 1);
            if (e.Kind is "player" or "hive") placer.Reserve(px, py - cs, cs, 2 * cs);
        }
        if (s.Self is { } me && InView(me.X, me.Y, me.Z))
        {
            var (px, py) = Cell(me.X, me.Y);
            SelfMarker(c, px + cs / 2, py + cs / 2, cs);
            SelfMarker(c, px + cs / 2, py - cs / 2, cs);
            placer.Reserve(px, py - cs, cs, 2 * cs);
        }
        foreach (var e in ents.Where(e => e.Kind is "player" or "hive"))
        {
            var (px, py) = Cell(e.X, e.Y);
            var t = EntityLabel(e);
            if (placer.Find(px, py, cs, cs, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, e.Kind == "hive" ? HiveC : PlayerColour(e.Name));
        }
        foreach (var m in s.Marks.Where(m => m.MaxZ >= s.Cz - depth && m.MinZ <= s.Cz + 1))
        {
            int bx0 = Math.Max(m.MinX, slab.X0), bx1 = Math.Min(m.MaxX, slab.X0 + n - 1), by0 = Math.Max(m.MinY, yBot), by1 = Math.Min(m.MaxY, yTop);
            if (bx0 > bx1 || by0 > by1) continue;
            var (px, py) = Cell(bx0, by1);
            var t = ShortLabel(m.Text);
            if (t.Length == 0) continue;
            labels.Add($"{t} {MarkWhere(m)}");
            if (IsLettered(t)) continue;
            if (placer.Find(px, py, (bx1 - bx0 + 1) * cs, (by1 - by0 + 1) * cs, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, m.Box ? Gold : Light);
        }
        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"side looking N through z{s.Cz} (W left, E right; up to {depth} blocks behind, faded), r{s.Radius}, {W}x{H}px ~{(W * H + 749) / 750}tok. ");
        legend.Append("1 cell=1 block; x bottom, y left (yellow = your feet). Outlined = in the slice; navy hatch = air/void.");
        if (s.Self is { } self) legend.Append(CultureInfo.InvariantCulture, $" @=you {Fmt(self.X, self.Y, self.Z)}.");
        return Finish(c, key, legend.ToString(), labels);
    }

    // ------------------------------------------------------------------ iso view

    /// <summary>Iso sprite half-width for a radius so the diamond stays under ~380 px wide (even, 2..8).</summary>
    public static int IsoA(int radius)
    {
        int n = 2 * Math.Clamp(radius, 2, 32) + 1;
        int a = Math.Clamp(380 / (2 * n), 2, 8);
        return a & ~1;
    }

    private VisionImage RenderIso(VisionScene s, Slab slab, ulong key)
    {
        int r = (slab.N - 1) / 2;
        int down = s.Cy - slab.Y0;
        // Only the iso window (Cy−IsoDown .. Cy+IsoUp) goes into the volume.
        int y0 = s.Cy - s.IsoDown, sy = s.IsoUp + s.IsoDown + 1, n = slab.N;
        var cells = new byte[n * sy * n];
        for (int iy = 0; iy < sy; iy++)
        {
            int src = iy + (y0 - slab.Y0);
            if ((uint)src >= (uint)slab.Sy) continue;
            Array.Copy(slab.Cells, src * n * n, cells, iy * n * n, n * n);
        }
        _ = down;
        int[]? target = s.Target is { } t0 ? [t0.X, t0.Y, t0.Z] : null;
        var vol = VoxelVolume.Create(slab.X0, y0, slab.Z0, n, sy, n, s.Cx, s.Cy, s.Cz, cells, 1, target);
        int A = IsoA(r), B = A / 2, C = A;
        var sprite = IsoProjection.Custom(A, B, C);
        int rot = s.Rotation & 3;

        // Tight bounds of the occupied cells (plus the lattice corners) in projected pixels.
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        void Grow(int dx, int dy, int dz)
        {
            var (u, v) = IsoProjection.Rotate(dx, dz, rot);
            var (px, py) = IsoProjection.Project(u, v, dy, sprite);
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px + sprite.W);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py + sprite.H);
        }
        foreach (var (dx, dz) in new[] { (-r, -r), (-r, r), (r, -r), (r, r) }) Grow(dx, y0 - 1 - s.Cy, dz);
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] == 0) continue;
            int x = i % n, z = i / n % n, y = i / (n * n);
            Grow(x + slab.X0 - s.Cx, y + y0 - s.Cy, z + slab.Z0 - s.Cz);
        }
        Grow(0, 2, 0);
        const int pad = 14;
        int W = Math.Min(MaxSide + 16, maxX - minX + 2 * pad), H = Math.Min(MaxSide + 16, maxY - minY + 2 * pad);
        int ox = pad - minX, oy = pad - minY;
        var view = new IsoView { Rotation = rot, Zoom = 2 };
        var input = new IsoInput(vol, [], view, IsoColours.TrueColor, true, s.Nether);
        IsoRenderer.Texturer? tex = s.Atlas == null && s.Light == null ? null : (id, wx, wy, wz, p, sp) =>
        {
            // Sprite pixel → world face + (u, v). Left face = +v, right face = +u of the rotated view.
            Face f = p.Face switch
            {
                IsoFace.Top => Face.Up,
                IsoFace.Left => rot switch { 0 => Face.South, 1 => Face.East, 2 => Face.North, _ => Face.West },
                _ => rot switch { 0 => Face.East, 1 => Face.North, 2 => Face.West, _ => Face.South },
            };
            float u, v;
            if (p.Face == IsoFace.Top)
            {
                float X = p.X + 0.5f - sp.A, Y = p.Y + 0.5f;
                u = Math.Clamp((X / sp.A + Y / sp.B) / 2f, 0f, 0.999f);
                v = Math.Clamp((Y / sp.B - X / sp.A) / 2f, 0f, 0.999f);
            }
            else
            {
                u = Math.Clamp((p.X < sp.A ? p.X + 0.5f : p.X + 0.5f - sp.A) / sp.A, 0f, 0.999f);
                v = Math.Clamp((p.Row + 0.5f) / sp.C, 0f, 0.999f);
            }
            int meta = slab.MetaAt(wx, wy, wz);
            float k = LightK(s, id, wx, wy, wz, f, 0.45f) * (p.Face == IsoFace.Top ? 1f : p.Face == IsoFace.Left ? IsoPalette.LeftShade : IsoPalette.RightShade);
            if (Tex(s, id, meta, f, u, v) is { } t) return IsoPalette.Scale(t, k);
            if (s.Light == null || IsoPalette.Mat(id) is not { } m) return null;
            return IsoPalette.Scale(p.Face == IsoFace.Top ? m.Top : m.Side, k);
        };
        var px0 = _iso.RenderPixels(input, sprite, r, W, H, ox, oy, out _, tex);
        var c = new Canvas(W, H, px0);

        // Overlays at their anchors (hidden = ring only).
        var placer = new Placer(0, 0, W, H);
        var labels = new List<string>();
        (int X, int Y, bool Hidden)? At(double x, double y, double z) =>
            _iso.PixelAnchor((int)Math.Floor(x) - s.Cx, (int)Math.Floor(y) - s.Cy, (int)Math.Floor(z) - s.Cz, rot, sprite, ox, oy, W, H);
        bool InView(double x, double z) => Math.Abs(Math.Floor(x) - s.Cx) <= r && Math.Abs(Math.Floor(z) - s.Cz) <= r;
        foreach (var p in s.Path)
            if (InView(p.X, p.Z) && At(p.X, p.Y, p.Z) is { Hidden: false } a) c.Fill(a.X - 1, a.Y - 1, 3, 3, Cyan);
        if (s.Pointed is { } pt && InView(pt.X, pt.Z) && At(pt.X, pt.Y, pt.Z) is { } pa)
        {
            c.Ring(pa.X, pa.Y - sprite.C / 2, Math.Max(3, A), Magenta);
            c.Ring(pa.X, pa.Y - sprite.C / 2, Math.Max(2, A - 1), Magenta);
            placer.Reserve(pa.X - A, pa.Y - A - sprite.C / 2, 2 * A, 2 * A);
            if (placer.Find(pa.X - A, pa.Y - A - sprite.C / 2, 2 * A, 2 * A, PixelFont.Width("POINTED") + 4, 11) is { } at) c.Chip(at.X, at.Y, "POINTED", Magenta);
        }
        var ents = s.Entities.Where(e => InView(e.X, e.Z)).ToList();
        foreach (var e in ents)
            if (At(e.X, e.Y, e.Z) is { } a)
            {
                EntityMarker(c, e, a.X, a.Y - A / 2, Math.Max(8, 2 * A), a.Hidden);
                if (e.Kind is "player" or "hive") placer.Reserve(a.X - A, a.Y - 2 * A, 2 * A, 2 * A);
            }
        if (s.Self is { } me && At(me.X, me.Y, me.Z) is { } sa)
        {
            SelfMarker(c, sa.X, sa.Y - A / 2, Math.Max(8, 2 * A));
            placer.Reserve(sa.X - A, sa.Y - 2 * A, 2 * A, 2 * A);
        }
        foreach (var e in ents.Where(e => e.Kind is "player" or "hive"))
            if (At(e.X, e.Y, e.Z) is { } a)
            {
                var t = EntityLabel(e);
                if (placer.Find(a.X - A, a.Y - 2 * A, 2 * A, 2 * A, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, e.Kind == "hive" ? HiveC : PlayerColour(e.Name));
            }
        foreach (var m in s.Marks)
        {
            if (!InView(m.CentreX, m.CentreZ)) continue;
            if (At(m.CentreX, m.MaxY, m.CentreZ) is not { } a) continue;
            var t = ShortLabel(m.Text);
            if (t.Length == 0) continue;
            c.Fill(a.X - 1, a.Y - 1, 3, 3, m.Box ? Gold : Light);
            if (placer.Find(a.X - 2, a.Y - 2, 4, 4, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, m.Box ? Gold : Light);
            labels.Add($"{t} {MarkWhere(m)}");
        }

        // Compass: each letter at the image corner its direction faces (0 up-right, 1 down-right, 2 down-left, 3 up-left).
        for (int d = 0; d < 4; d++)
        {
            int corner = IsoProjection.Corner(d, rot);
            int cx = corner is 0 or 1 ? W - 12 : 2, cy = corner is 1 or 2 ? H - 12 : 1;
            c.Fill(cx, cy, 10, 11, d == 0 ? NorthBg : ChipBg);
            c.Text(cx + 3, cy + 2, "NESW"[d].ToString(), Light);
        }
        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"iso r{r} centre {Fmt(s.Cx, s.Cy, s.Cz)}, N {IsoProjection.AsciiArrows[rot]} (corner letters), {W}x{H}px ~{(W * H + 749) / 750}tok. ");
        legend.Append("Lit top, shaded sides; faint lattice below = void.");
        if (s.Self is { } self) legend.Append(CultureInfo.InvariantCulture, $" @=you {Fmt(self.X, self.Y, self.Z)}.");
        return Finish(c, key, legend.ToString(), labels);
    }
}
