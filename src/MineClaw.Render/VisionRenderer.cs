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
        /// <summary>Role per cell (same layout as <see cref="Cells"/>), only when the scene has a role map.</summary>
        public byte[]? Roles;
        public ulong Hash;

        public BlockRole RoleAt(int x, int y, int z)
        {
            if (Roles == null) return BlockRole.None;
            int ix = x - X0, iy = y - Y0, iz = z - Z0;
            if ((uint)ix >= (uint)N || (uint)iy >= (uint)Sy || (uint)iz >= (uint)N) return BlockRole.None;
            return (BlockRole)Roles[(iy * N + iz) * N + ix];
        }

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
        if (s.Roles is { } roles)
        {
            // Roles of every block in the window, plus the air of the feet layer and the one below (a refilling gen cell).
            slab.Roles = new byte[slab.Cells.Length];
            ulong rh = h;
            for (int iy = 0; iy < slab.Sy; iy++)
            {
                int wy = slab.Y0 + iy;
                bool feetLayers = wy == s.Cy || wy == s.Cy - 1;
                for (int iz = 0; iz < n; iz++)
                    for (int ix = 0; ix < n; ix++)
                    {
                        int k = (iy * n + iz) * n + ix;
                        if (!slab.Loaded[iz * n + ix] || (slab.Cells[k] == 0 && !feetLayers)) continue;
                        BlockRole role;
                        try { role = roles(slab.X0 + ix, wy, slab.Z0 + iz); } catch (Exception) { role = BlockRole.None; }
                        slab.Roles[k] = (byte)role;
                        unchecked { rh ^= (byte)role; rh *= 1099511628211UL; }
                    }
            }
            slab.Hash = rh; // a new never-break mark or a placed block changes the picture even when the ids don't
        }
        return slab;
    }

    private static ulong OverlayHash(VisionScene s)
    {
        var sb = new StringBuilder();
        sb.Append(s.View).Append('|').Append(s.Radius).Append('|').Append(s.Rotation).Append('|').Append(s.Nether).Append('|')
          .Append(s.Atlas != null).Append(s.Light != null ? Lighting.SkylightSubtracted(s.Time) : -1).Append('|')
          .Append(s.Self).Append('|').Append(s.Target).Append('|').Append(s.Pointed).Append(s.PointedBy).Append('|')
          .Append(s.Roles != null).Append(s.Focus).Append('|').Append(s.CellPx).Append('|').Append(s.Axis).Append('|').Append(s.MaxPx).Append('|')
          .Append(s.Up).Append(s.Down).Append(s.SideUp).Append(s.SideDown).Append(s.SideDepth).Append(s.IsoUp).Append(s.IsoDown).Append('|');
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
            VisionView.Layers => RenderLayers(s, slab, key),
            _ => RenderTop(s, slab, key),
        };
        img = img with { BlocksHash = slab.Hash };
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
        int n = slab.N, cs = s.CellPx ?? CellSize(s.Radius);
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
                    // A refilling (dug) gen cell at the feet layer or below is still the place to mine.
                    var airRole = slab.RoleAt(wx, s.Cy, wz) is BlockRole.GenCell ? BlockRole.GenCell : slab.RoleAt(wx, s.Cy - 1, wz);
                    if (airRole == BlockRole.GenCell) RoleCell(c, px, py, cs, airRole);
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
                var role = slab.RoleAt(wx, baseY, wz);
                bool roleLetter = BlockRoles.Glyph(role) != null;
                if (Letter(baseId) is { } L && cs >= 8 && !roleLetter) c.Text(px + (cs - 5) / 2, py + (cs - 7) / 2, L.ToString(), IsDoor(baseId) ? 0xfff2b0u : InkFor(col));
                if (deco != 0) Deco(c, px, py, cs, deco);
                RoleCell(c, px, py, cs, role);
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
        // Focus-centred sheets: N goes to the top-right corner so the focus x tick (yellow) sits where N used to.
        var nChip = (X: s.Focus != null ? mx + n * cs - 12 : mx + n * cs / 2 - 6, Y: 0, W: 12, H: 11);
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
            if (s.Focus is { } fx0 && Math.Abs(fx0.X - wx) <= 2 && fx0.X != wx) continue; // the focus tick wins
            c.Text(tx, 2, t, 0xb8c0d0);
        }
        if (s.Focus is { } fxx && fxx.X >= x0 && fxx.X < x0 + n)
        {
            var t = fxx.X.ToString(CultureInfo.InvariantCulture);
            int tw = PixelFont.Width(t), tx = mx + (fxx.X - x0) * cs + cs / 2 - tw / 2;
            if (!(tx < nChip.X + nChip.W + 1 && nChip.X < tx + tw + 1)) c.Text(tx, 2, t, SelfC);
        }
        int wMid = my + n * cs / 2;
        for (int i = 0; i < n; i++)
        {
            int wz = z0 + i;
            if (Mod5(wz) != 0) continue;
            var t = wz.ToString(CultureInfo.InvariantCulture);
            int ty = my + i * cs + cs / 2 - 3;
            if (s.Focus is { } fz0 && Math.Abs(fz0.Z - wz) * cs < 9 && fz0.Z != wz) continue;
            c.Text(ML - 3 - PixelFont.Width(t), ty, t, 0xb8c0d0);
        }
        if (s.Focus is { } fzz && fzz.Z >= z0 && fzz.Z < z0 + n)
        {
            var t = fzz.Z.ToString(CultureInfo.InvariantCulture);
            c.Text(ML - 3 - PixelFont.Width(t), my + (fzz.Z - z0) * cs + cs / 2 - 3, t, SelfC);
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
        if (s.Focus is { } fc && InView(fc.X, fc.Z)) { var (px, py) = Cell(fc.X, fc.Z); FocusBox(c, px, py, cs); placer.Reserve(px - 3, py - 3, cs + 6, cs + 6); }

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
        if (s.Focus is { } fl) legend.Append(CultureInfo.InvariantCulture, $" Cyan box=focus {Fmt(fl.X, fl.Y, fl.Z)} (yellow ticks).");
        if (BlockRoles.Legend(RolesIn(slab)) is { Length: > 0 } rl) legend.Append(" Roles: ").Append(rl).Append('.');
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
        new(Png.Encode(c.Px, c.Width, c.Height), c.Width, c.Height, key, legend, labels) { Pixels = c.Px };

    // ------------------------------------------------------------------ vision v2: roles + focus

    private const uint FocusC = 0x5fe8ff;

    /// <summary>Role overlay on one cell: tint, a role-coloured inner outline and the role letter (cells ≥ 8 px).</summary>
    private static void RoleCell(Canvas c, int px, int py, int cs, BlockRole r, bool letter = true)
    {
        if (!BlockRoles.Highlight(r)) return;
        uint col = BlockRoles.Colour(r);
        if (r is not (BlockRole.Door or BlockRole.Station)) c.FillBlend(px + 1, py + 1, cs - 2, cs - 2, col, r == BlockRole.GenCell ? 0.5f : 0.38f);
        c.Outline(px + 1, py + 1, cs - 2, cs - 2, col, cs >= 12 ? 2 : 1);
        if (letter && cs >= 8 && BlockRoles.Glyph(r) is { } g && PixelFont.Has(g))
        {
            int tx = px + (cs - 5) / 2, ty = py + (cs - 7) / 2;
            c.FillBlend(tx - 1, ty - 1, 7, 9, Ink, 0.55f);
            c.Text(tx, ty, g.ToString(), col);
        }
    }

    /// <summary>The shared focus marker: a 2 px cyan box one pixel outside the cell plus corner ticks.</summary>
    private static void FocusBox(Canvas c, int px, int py, int cs)
    {
        c.Outline(px - 2, py - 2, cs + 4, cs + 4, Ink, 1);
        c.Outline(px - 1, py - 1, cs + 2, cs + 2, FocusC, 2);
        int t = Math.Max(2, cs / 3);
        foreach (var (x, y, dx, dy) in new[] { (px - 3, py - 3, 1, 1), (px + cs + 2, py - 3, -1, 1), (px - 3, py + cs + 2, 1, -1), (px + cs + 2, py + cs + 2, -1, -1) })
        {
            c.Line(x, y, x + dx * t, y, FocusC);
            c.Line(x, y, x, y + dy * t, FocusC);
        }
    }

    /// <summary>Roles drawn in a panel (for the legend's letter list).</summary>
    private static IEnumerable<BlockRole> RolesIn(Slab slab) =>
        slab.Roles == null ? [] : slab.Roles.Distinct().Select(b => (BlockRole)b).Where(r => BlockRoles.Glyph(r) != null);

    // ------------------------------------------------------------------ side view (looking north, or east)

    private VisionImage RenderSide(VisionScene s, Slab slab, ulong key)
    {
        int n = slab.N, cs = s.CellPx ?? CellSize(s.Radius);
        bool east = s.Axis == SideAxis.LookEast;
        // Screen column i = horizontal world coordinate h0+i (x looking north, z looking east); depth d steps away from the
        // viewer: looking north the blocks behind the slice are at z = Cz-d, looking east at x = Cx+d.
        int h0 = east ? slab.Z0 : slab.X0;
        (int X, int Z) W2(int h, int d) => east ? (s.Cx + d, h) : (h, s.Cz - d);
        double Hof(double x, double z) => east ? z : x;
        double Dof(double x, double z) => east ? Math.Floor(x) - s.Cx : s.Cz - Math.Floor(z);
        Face facing = east ? Face.West : Face.South; // the face the viewer sees
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
                int wy = yTop - iy, px = mx + ix * cs, py = my + iy * cs;
                bool hit = false;
                for (int d = 0; d <= depth && !hit; d++)
                {
                    var (wx, wz) = W2(h0 + ix, d);
                    if (!slab.IsLoaded(wx, wz)) { if (d == 0) { Unknown(c, px, py, cs); hit = true; } break; }
                    int id = slab.At(wx, wy, wz);
                    if (id == 0 || IsoPalette.Mat(id) is not { } m)
                    {
                        if (d == 0 && slab.RoleAt(wx, wy, wz) == BlockRole.GenCell) { VoidCell(c, px, py, cs); RoleCell(c, px, py, cs, BlockRole.GenCell); hit = true; }
                        continue;
                    }
                    float k = Math.Max(0.38f, 1f - 0.075f * d) * LightK(s, id, wx, wy, wz, facing, 0.55f);
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
                                if (Tex(s, id, meta, facing, (tx + 0.5f) / cs, (ty + 0.5f) / cs) is { } t) c.Set(px + tx, py + ty, IsoPalette.Scale(t, k));
                    }
                    if (d == 0) c.Outline(px, py, cs, cs, IsoPalette.Mix(col, 0xffffff, 0.35f));
                    var role = d == 0 ? slab.RoleAt(wx, wy, wz) : BlockRole.None;
                    if (Letter(id) is { } L && cs >= 8 && BlockRoles.Glyph(role) == null) c.Text(px + (cs - 5) / 2, py + (cs - 7) / 2, L.ToString(), InkFor(col));
                    RoleCell(c, px, py, cs, role);
                    hit = true;
                }
                if (!hit && c.Get(px, py) == Margin) VoidCell(c, px, py, cs);
            }

        for (int i = 0; i <= n; i++)
        {
            int gx = mx + Math.Min(i * cs, n * cs - 1);
            bool major = Mod5(h0 + i) == 0;
            for (int py = my; py < my + rows * cs; py++) GridPx(c, gx, py, major);
        }
        for (int i = 0; i <= rows; i++)
        {
            int gy = my + Math.Min(i * cs, rows * cs - 1);
            bool major = i < rows && Mod5(yTop - i) == 0;
            for (int px = mx; px < mx + n * cs; px++) GridPx(c, px, gy, major);
        }

        string leftL = east ? "N" : "W", rightL = east ? "S" : "E";
        c.Chip(mx, 0, east ? "LOOKING E" : "LOOKING N", Light, ChipBg, 1f);
        c.Fill(1, my + rows * cs / 2 - 5, 9, 11, ChipBg);
        c.Text(3, my + rows * cs / 2 - 3, leftL, Light);
        c.Fill(W - 11, my + rows * cs / 2 - 5, 9, 11, ChipBg);
        c.Text(W - 9, my + rows * cs / 2 - 3, rightL, Light);
        int? focusH = s.Focus is { } fh ? (east ? fh.Z : fh.X) : null;
        for (int i = 0; i < n; i++)
        {
            int wh = h0 + i;
            bool isFocus = focusH == wh;
            if (Mod5(wh) != 0 && !isFocus) continue;
            if (!isFocus && focusH is { } f && Math.Abs(f - wh) * cs < 14) continue;
            var t = wh.ToString(CultureInfo.InvariantCulture);
            c.Text(mx + i * cs + cs / 2 - PixelFont.Width(t) / 2, H - 9, t, isFocus ? SelfC : 0xb8c0d0);
        }
        for (int i = 0; i < rows; i++)
        {
            int wy = yTop - i;
            bool isFocus = s.Focus is { } fy && fy.Y == wy;
            if (Mod5(wy) != 0 && wy != s.Cy && !isFocus) continue;
            var t = wy.ToString(CultureInfo.InvariantCulture);
            c.Text(ML - 3 - PixelFont.Width(t), my + i * cs + cs / 2 - 3, t, wy == s.Cy || isFocus ? SelfC : 0xb8c0d0);
        }

        var placer = new Placer(mx, my, mx + n * cs, my + rows * cs);
        bool InView(double x, double y, double z) => Math.Floor(Hof(x, z)) >= h0 && Math.Floor(Hof(x, z)) < h0 + n && Math.Floor(y) <= yTop
            && Math.Floor(y) >= yBot && Dof(x, z) >= -1 && Dof(x, z) <= depth;
        (int X, int Y) Cell(double x, double y, double z) => (mx + ((int)Math.Floor(Hof(x, z)) - h0) * cs, my + (yTop - (int)Math.Floor(y)) * cs);
        var labels = new List<string>();
        if (s.Target is { } tg && InView(tg.X, tg.Y, tg.Z)) { var (px, py) = Cell(tg.X, tg.Y, tg.Z); c.Outline(px - 1, py - 1, cs + 2, cs + 2, Gold, 2); }
        if (s.Pointed is { } pt && InView(pt.X, pt.Y, pt.Z)) { var (px, py) = Cell(pt.X, pt.Y, pt.Z); c.Outline(px - 1, py - 1, cs + 2, cs + 2, Magenta, 2); placer.Reserve(px - 1, py - 1, cs + 2, cs + 2); }
        if (s.Focus is { } fc && InView(fc.X, fc.Y, fc.Z)) { var (px, py) = Cell(fc.X, fc.Y, fc.Z); FocusBox(c, px, py, cs); placer.Reserve(px - 3, py - 3, cs + 6, cs + 6); }
        bool MarkInSlice(VisionMark m) => east ? m.MaxX >= s.Cx - 1 && m.MinX <= s.Cx + depth : m.MaxZ >= s.Cz - depth && m.MinZ <= s.Cz + 1;
        (int Lo, int Hi) MarkH(VisionMark m) => east ? (m.MinZ, m.MaxZ) : (m.MinX, m.MaxX);
        foreach (var m in s.Marks.Where(m => m.Box && MarkInSlice(m)))
        {
            var (lo, hi) = MarkH(m);
            int bh0 = Math.Max(lo, h0), bh1 = Math.Min(hi, h0 + n - 1), by0 = Math.Max(m.MinY, yBot), by1 = Math.Min(m.MaxY, yTop);
            if (bh0 > bh1 || by0 > by1) continue;
            int px = mx + (bh0 - h0) * cs, py = my + (yTop - by1) * cs;
            c.Outline(px, py, (bh1 - bh0 + 1) * cs, (by1 - by0 + 1) * cs, Gold, 1, dash: 3);
        }
        var ents = s.Entities.Where(e => InView(e.X, e.Y, e.Z)).ToList();
        foreach (var e in ents)
        {
            var (px, py) = Cell(e.X, e.Y, e.Z);
            EntityMarker(c, e, px + cs / 2, py + cs / 2, cs, hidden: Dof(e.X, e.Z) > 1);
            if (e.Kind is "player" or "hive") placer.Reserve(px, py - cs, cs, 2 * cs);
        }
        if (s.Self is { } me && InView(me.X, me.Y, me.Z))
        {
            var (px, py) = Cell(me.X, me.Y, me.Z);
            SelfMarker(c, px + cs / 2, py + cs / 2, cs);
            SelfMarker(c, px + cs / 2, py - cs / 2, cs);
            placer.Reserve(px, py - cs, cs, 2 * cs);
        }
        foreach (var e in ents.Where(e => e.Kind is "player" or "hive"))
        {
            var (px, py) = Cell(e.X, e.Y, e.Z);
            var t = EntityLabel(e);
            if (placer.Find(px, py, cs, cs, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, e.Kind == "hive" ? HiveC : PlayerColour(e.Name));
        }
        foreach (var m in s.Marks.Where(MarkInSlice))
        {
            var (lo, hi) = MarkH(m);
            int bh0 = Math.Max(lo, h0), bh1 = Math.Min(hi, h0 + n - 1), by0 = Math.Max(m.MinY, yBot), by1 = Math.Min(m.MaxY, yTop);
            if (bh0 > bh1 || by0 > by1) continue;
            int px = mx + (bh0 - h0) * cs, py = my + (yTop - by1) * cs;
            var t = ShortLabel(m.Text);
            if (t.Length == 0) continue;
            labels.Add($"{t} {MarkWhere(m)}");
            if (IsLettered(t)) continue;
            if (placer.Find(px, py, (bh1 - bh0 + 1) * cs, (by1 - by0 + 1) * cs, PixelFont.Width(t) + 4, 11) is { } at) c.Chip(at.X, at.Y, t, m.Box ? Gold : Light);
        }
        var legend = new StringBuilder();
        legend.Append(east
            ? string.Create(CultureInfo.InvariantCulture, $"side looking E through x{s.Cx} (N left, S right; up to {depth} blocks behind, faded), r{s.Radius}, {W}x{H}px ~{(W * H + 749) / 750}tok. ")
            : string.Create(CultureInfo.InvariantCulture, $"side looking N through z{s.Cz} (W left, E right; up to {depth} blocks behind, faded), r{s.Radius}, {W}x{H}px ~{(W * H + 749) / 750}tok. "));
        legend.Append(east ? "1 cell=1 block; z bottom, y left (yellow = your feet). " : "1 cell=1 block; x bottom, y left (yellow = your feet). ");
        legend.Append("Outlined = in the slice; navy hatch = air/void.");
        if (s.Self is { } self) legend.Append(CultureInfo.InvariantCulture, $" @=you {Fmt(self.X, self.Y, self.Z)}.");
        if (s.Focus is { } fl) legend.Append(CultureInfo.InvariantCulture, $" Cyan box=focus {Fmt(fl.X, fl.Y, fl.Z)}.");
        return Finish(c, key, legend.ToString(), labels);
    }

    // ------------------------------------------------------------------ layers view (feet layer + the layer below)

    /// <summary>
    /// Two top-down grids side by side: the feet layer (y = Cy) and the layer below (Cy−1), each block at exactly that height,
    /// roles tinted. Air cells next to a solid face get a green corner dot: a block can be placed there (b1.8.1 needs a face to
    /// click). Built for holes ("which face can I place against standing in 7,62,9?") and floors with gaps.
    /// </summary>
    private VisionImage RenderLayers(VisionScene s, Slab slab, ulong key)
    {
        int r = Math.Min(s.Radius, 6), n = 2 * r + 1;
        int cs = s.CellPx ?? Math.Clamp(170 / n, 6, 16);
        int x0 = s.Cx - r, z0 = s.Cz - r;
        int zlw = 0;
        for (int i = 0; i < n; i++) if (Mod5(z0 + i) == 0 || z0 + i == s.Cz) zlw = Math.Max(zlw, PixelFont.Width((z0 + i).ToString(CultureInfo.InvariantCulture)));
        int ML = zlw + 5, MT = 22, gap = 8, MR = 4, MB = 4;
        int gw = n * cs;
        int W = ML + gw + gap + gw + MR, H = MT + n * cs + MB;
        var c = new Canvas(W, H, Margin);
        var labels = new List<string>();
        bool Solid(int x, int y, int z) { int id = slab.At(x, y, z); return id != 0 && Blocks.Get(id).Solid; }
        bool HasFace(int x, int y, int z) =>
            Solid(x + 1, y, z) || Solid(x - 1, y, z) || Solid(x, y + 1, z) || Solid(x, y, z + 1) || Solid(x, y, z - 1); // the floor below always is: not news
        int placeable = 0;
        for (int layer = 0; layer < 2; layer++)
        {
            int wy = s.Cy - layer, gx = ML + layer * (gw + gap);
            c.Chip(gx, 10, layer == 0 ? $"FEET y{wy}" : $"BELOW y{wy}", layer == 0 ? SelfC : Light, ChipBg, 1f);
            for (int iz = 0; iz < n; iz++)
                for (int ix = 0; ix < n; ix++)
                {
                    int wx = x0 + ix, wz = z0 + iz, px = gx + ix * cs, py = MT + iz * cs;
                    if (!slab.IsLoaded(wx, wz)) { Unknown(c, px, py, cs); continue; }
                    int id = slab.At(wx, wy, wz);
                    var role = slab.RoleAt(wx, wy, wz);
                    if (id == 0 || IsoPalette.Mat(id) is not { } m)
                    {
                        // Air. FEET grid: open = the floor right below shows through, dimmed (what you'd walk on); BELOW grid and
                        // deeper drops: dark; nothing below at all: void hatch.
                        bool below = false;
                        for (int yy = wy - 1; yy >= slab.Y0 && !below; yy--) below = slab.At(wx, yy, wz) != 0;
                        int under = slab.At(wx, wy - 1, wz);
                        if (layer == 0 && under != 0 && IsoPalette.Mat(under) is { } um) c.Fill(px, py, cs, cs, IsoPalette.Scale(um.Top, 0.5f));
                        else if (below || slab.HasBelow(wx, wz)) c.Fill(px, py, cs, cs, DeepC);
                        else VoidCell(c, px, py, cs);
                        if (role == BlockRole.GenCell) RoleCell(c, px, py, cs, role);
                        else if (HasFace(wx, wy, wz)) { c.Fill(px + cs - 4, py + 1, 3, 3, 0x2cff6a); placeable++; }
                        continue;
                    }
                    uint col = m.Top;
                    c.Fill(px, py, cs, cs, col);
                    if (s.Atlas != null)
                    {
                        int meta = slab.MetaAt(wx, wy, wz);
                        for (int ty = 0; ty < cs; ty++)
                            for (int tx = 0; tx < cs; tx++)
                                if (Tex(s, id, meta, Face.Up, (tx + 0.5f) / cs, (ty + 0.5f) / cs) is { } t) c.Set(px + tx, py + ty, t);
                    }
                    if (layer == 0 && Blocks.Get(id).Solid)
                    {
                        // A block at body height is a WALL beside you, never floor: raised bevel + a diagonal hatch.
                        uint hi = IsoPalette.Mix(col, 0xffffff, 0.5f), lo = IsoPalette.Scale(col, 0.35f);
                        c.Fill(px, py, cs, 2, hi); c.Fill(px, py, 2, cs, hi);
                        c.Fill(px, py + cs - 2, cs, 2, lo); c.Fill(px + cs - 2, py, 2, cs, lo);
                        for (int d = 2; d < cs - 2; d += 3) c.Set(px + d, py + d, lo);
                    }
                    if (Letter(id) is { } L && cs >= 8 && BlockRoles.Glyph(role) == null) c.Text(px + (cs - 5) / 2, py + (cs - 7) / 2, L.ToString(), InkFor(col));
                    RoleCell(c, px, py, cs, role);
                }
            for (int i = 0; i <= n; i++)
            {
                int lx = gx + Math.Min(i * cs, gw - 1), ly = MT + Math.Min(i * cs, n * cs - 1);
                for (int py = MT; py < MT + n * cs; py++) GridPx(c, lx, py, Mod5(x0 + i) == 0);
                for (int px = gx; px < gx + gw; px++) GridPx(c, px, ly, Mod5(z0 + i) == 0);
            }
            // x ticks: multiples of 5 and the focus column (yellow).
            for (int i = 0; i < n; i++)
            {
                int wx = x0 + i;
                bool f = s.Focus is { } fx && fx.X == wx;
                if (Mod5(wx) != 0 && !f) continue;
                if (!f && s.Focus is { } fx2 && Math.Abs(fx2.X - wx) * cs < 14) continue;
                var t = wx.ToString(CultureInfo.InvariantCulture);
                c.Text(gx + i * cs + cs / 2 - PixelFont.Width(t) / 2, 1, t, f ? SelfC : 0xb8c0d0);
            }
            if (s.Focus is { } fo && fo.Y == wy && Math.Abs(fo.X - s.Cx) <= r && Math.Abs(fo.Z - s.Cz) <= r)
                FocusBox(c, gx + (fo.X - x0) * cs, MT + (fo.Z - z0) * cs, cs);
            if (s.Self is { } me && me.Y == wy && Math.Abs(me.X - s.Cx) <= r && Math.Abs(me.Z - s.Cz) <= r)
                SelfMarker(c, gx + (me.X - x0) * cs + cs / 2, MT + (me.Z - z0) * cs + cs / 2, cs);
        }
        for (int i = 0; i < n; i++)
        {
            int wz = z0 + i;
            bool f = wz == s.Cz;
            if (Mod5(wz) != 0 && !f) continue;
            if (!f && Math.Abs(s.Cz - wz) * cs < 9) continue;
            var t = wz.ToString(CultureInfo.InvariantCulture);
            c.Text(ML - 2 - PixelFont.Width(t), MT + i * cs + cs / 2 - 3, t, f ? SelfC : 0xb8c0d0);
        }
        // Compass: north is up on both grids.
        c.Fill(W - 12, 9, 11, 11, NorthBg);
        c.Text(W - 9, 11, "N", Light);
        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"layers at {Fmt(s.Cx, s.Cy, s.Cz)}: left FEET y{s.Cy}, right BELOW y{s.Cy - 1}; N up, {W}x{H}px ~{(W * H + 749) / 750}tok. ");
        legend.Append("Blocks at exactly that height; FEET grid: raised/hatched=wall beside you, dim=open (floor below shows); dark=deeper drop, navy hatch=void; green dot=air with a side/top face to place against (holes, gaps, edges)");
        legend.Append(CultureInfo.InvariantCulture, $" ({placeable}).");
        if (BlockRoles.Legend(RolesIn(slab)) is { Length: > 0 } rl) legend.Append(" Roles: ").Append(rl).Append('.');
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
        int rot = s.Rotation & 3;
        const int pad = 14;
        int maxPx = s.MaxPx ?? MaxSide + 16;
        int A = IsoA(r), B, C;
        IsoSprite sprite;
        int minX, maxX, minY, maxY;
        // Tight bounds of the occupied cells (plus the lattice corners) in projected pixels; a panel shrinks the sprite to fit.
        while (true)
        {
            B = A / 2; C = A;
            sprite = IsoProjection.Custom(A, B, C);
            int lx = int.MaxValue, hx = int.MinValue, ly = int.MaxValue, hy = int.MinValue;
            var sp = sprite;
            void Grow(int dx, int dy, int dz)
            {
                var (u, v) = IsoProjection.Rotate(dx, dz, rot);
                var (px, py) = IsoProjection.Project(u, v, dy, sp);
                lx = Math.Min(lx, px); hx = Math.Max(hx, px + sp.W);
                ly = Math.Min(ly, py); hy = Math.Max(hy, py + sp.H);
            }
            foreach (var (dx, dz) in new[] { (-r, -r), (-r, r), (r, -r), (r, r) }) Grow(dx, y0 - 1 - s.Cy, dz);
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == 0) continue;
                int x = i % n, z = i / n % n, y = i / (n * n);
                Grow(x + slab.X0 - s.Cx, y + y0 - s.Cy, z + slab.Z0 - s.Cz);
            }
            Grow(0, 2, 0);
            (minX, maxX, minY, maxY) = (lx, hx, ly, hy);
            if (A <= 2 || (maxX - minX + 2 * pad <= maxPx && maxY - minY + 2 * pad <= maxPx)) break;
            A -= 2;
        }
        int W = Math.Min(maxPx, maxX - minX + 2 * pad), H = Math.Min(maxPx, maxY - minY + 2 * pad);
        int ox = pad - minX, oy = pad - minY;
        var view = new IsoView { Rotation = rot, Zoom = 2 };
        var input = new IsoInput(vol, [], view, IsoColours.TrueColor, true, s.Nether);
        IsoRenderer.Texturer? tex = s.Atlas == null && s.Light == null && s.Roles == null ? null : (id, wx, wy, wz, p, sp) =>
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
            uint? col = Tex(s, id, meta, f, u, v) is { } t ? IsoPalette.Scale(t, k)
                : IsoPalette.Mat(id) is { } m ? IsoPalette.Scale(p.Face == IsoFace.Top ? m.Top : m.Side, k) : null;
            // Role layer: highlighted blocks are tinted on every face (gen cells glow), so the 3D view agrees with the map.
            var role = slab.RoleAt(wx, wy, wz);
            if (col is { } cc && BlockRoles.Highlight(role) && role is not (BlockRole.Door or BlockRole.Station))
                return IsoPalette.Mix(cc, BlockRoles.Colour(role), role == BlockRole.GenCell ? 0.55f : 0.4f);
            if (s.Atlas == null && s.Light == null) return null;
            return col;
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
        // The focus ring goes on top of the markers (the bot often stands at its own focus).
        if (s.Focus is { } fo && InView(fo.X, fo.Z) && At(fo.X, fo.Y, fo.Z) is { } fa)
        {
            int fy = fa.Y - sprite.C / 2;
            c.Ring(fa.X, fy, Math.Max(6, A + 3), Ink);
            c.Ring(fa.X, fy, Math.Max(5, A + 2), FocusC);
            c.Ring(fa.X, fy, Math.Max(4, A + 1), FocusC);
            placer.Reserve(fa.X - A - 1, fy - A - 1, 2 * A + 2, 2 * A + 2);
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
        if (s.Focus is { } fl) legend.Append(CultureInfo.InvariantCulture, $" Cyan ring=focus {Fmt(fl.X, fl.Y, fl.Z)}.");
        return Finish(c, key, legend.ToString(), labels);
    }
}
