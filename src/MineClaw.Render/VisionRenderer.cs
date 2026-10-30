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

        /// <summary>
        /// A block above <paramref name="feetY"/> hanging over air/fluid/non-solid down to the feet layer (a roof, a canopy):
        /// cut away in top/iso views so it never hides what is at feet height (a gen cell under a roof).
        /// </summary>
        public bool Overhangs(int x, int y, int z, int feetY)
        {
            if (y <= feetY) return false;
            for (int yy = feetY; yy < y; yy++)
            {
                int id = At(x, yy, z);
                if (id == 0 || !Blocks.Get(id).Solid) return true;
            }
            return false;
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
        int height = s.Blocks.WorldHeight;
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
                    if ((uint)wy >= height) continue;
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
          .Append(s.Up).Append(s.Down).Append(s.SideUp).Append(s.SideDown).Append(s.SideDepth).Append(s.IsoUp).Append(s.IsoDown).Append(s.CutAway).Append(s.Weather.R).Append(s.Weather.T).Append('|');
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
            _ => RenderMap(s, slab, key),
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


    private static VisionImage Finish(Canvas c, ulong key, string legend, List<string> labels) =>
        new(Png.Encode(c.Px, c.Width, c.Height), c.Width, c.Height, key, legend, labels) { Pixels = c.Px };


    private static IEnumerable<BlockRole> RolesIn(Slab slab) =>
        slab.Roles == null ? [] : slab.Roles.Distinct().Select(b => (BlockRole)b).Where(r => BlockRoles.Glyph(r) != null);


    // ------------------------------------------------------------------ every view: the 3D renderer

    /// <summary>Cell size the old flat maps used for a radius (kept for callers sizing layouts).</summary>
    public static int CellSize(int radius) => Math.Clamp(352 / (2 * Math.Clamp(radius, 2, 32) + 1), 6, 16);

    /// <summary>Iso sprite half-width for a radius (the TUI's orthographic sprite size; kept for callers sizing old layouts).</summary>
    public static int IsoA(int radius)
    {
        int n = 2 * Math.Clamp(radius, 2, 32) + 1;
        int a = Math.Clamp(380 / (2 * n), 2, 8);
        return a & ~1;
    }

    private readonly Dictionary<(int, int), EyeRenderer> _isoEyes = new();

    /// <summary>Raycaster thread cap of these views (the perspective renderer; background viewers use 1).</summary>
    public int IsoThreads { get; init; } = 8;

    private EyeRenderer EyeFor(int w, int h)
    {
        lock (_lock)
        {
            if (!_isoEyes.TryGetValue((w, h), out var eye))
            {
                if (_isoEyes.Count > 6) _isoEyes.Clear();
                _isoEyes[(w, h)] = eye = new EyeRenderer { Width = w, Height = h, MaxThreads = IsoThreads };
            }
            return eye;
        }
    }

    /// <summary>The camera of a single view: ISO = a 35° orbit from the SW (rotation = quarter turns); TOP = straight down,
    /// orthographic; SIDE = an orthographic cut along the axis; LAYERS = the feet layer from above (the sheet shows both layers).</summary>
    public static PerspectivePanel.Rig RigFor(VisionScene s)
    {
        int r = Math.Clamp(s.Radius, 2, 32);
        var focus = s.Focus ?? (s.Cx, s.Cy, s.Cz);
        return s.View switch
        {
            VisionView.Iso => PerspectivePanel.For("iso", focus, s.Cy, r, null, null, s.Rotation)!.Value,
            VisionView.Side => PerspectivePanel.Side((s.Cx, s.Cy, s.Cz), s.Cy, r, s.Axis),
            VisionView.Layers => PerspectivePanel.Layer((s.Cx, s.Cy, s.Cz), s.Cy, r, $"FEET Y{s.Cy}"),
            _ => PerspectivePanel.Top((s.Cx, s.Cy, s.Cz), s.Cy, r),
        };
    }

    /// <summary>Picture size of a single view (4:3 within <see cref="VisionScene.MaxPx"/>).</summary>
    public static (int W, int H) SizeFor(VisionScene s)
    {
        int side = s.MaxPx ?? MaxSide;
        return (side, side * 3 / 4);
    }

    /// <summary>Where a world point lands in a single view's picture (for tests and overlays), or null off-picture.</summary>
    public (int X, int Y)? Pixel(VisionScene s, double x, double y, double z)
    {
        var (w, h) = SizeFor(s);
        var rig = RigFor(s);
        var scene = new EyeScene { World = new BlockAccessVoxels(s.Blocks), EyeX = rig.X, EyeY = rig.Y, EyeZ = rig.Z, Yaw = rig.Yaw, Pitch = rig.Pitch, Fov = rig.Fov, Ortho = rig.Ortho };
        return EyeFor(w, h).Project(scene, x, y, z) is { } p ? (p.X, p.Y) : null;
    }

    /// <summary>
    /// Every single-picture view is the freecam's renderer (textures, render types, fluids, models, shadows, fog, the role
    /// layer as tints/outlines, the reading aids): "iso" a high 3/4 orbit, "top"/"side"/"layers" orthographic cameras with
    /// clipping. (The orthographic character-cell rasterizer, <see cref="IsoRenderer"/>, only draws the TUI's text viewport.)
    /// </summary>
    private VisionImage RenderMap(VisionScene s, Slab slab, ulong key)
    {
        int r = (slab.N - 1) / 2;
        var (w, h) = SizeFor(s);
        var eye = EyeFor(w, h);
        var focus = s.Focus ?? (s.Cx, s.Cy, s.Cz);
        var rig = RigFor(s);
        var ents = new List<VisionEntity>(s.Entities);
        if (s.Self is { } me && !ents.Any(e => (int)Math.Floor(e.X) == me.X && (int)Math.Floor(e.Z) == me.Z))
        {
            var (yaw, _) = EyeScene.LookAt(me.X + 0.5, me.Y, me.Z + 0.5, focus.X + 0.5, focus.Y, focus.Z + 0.5);
            ents.Add(new VisionEntity("self", "you", me.X + 0.5, me.Y, me.Z + 0.5, '@') { Yaw = Math.Abs(me.X - focus.X) + Math.Abs(me.Z - focus.Z) == 0 ? 0 : yaw });
        }
        var input = new PerspectivePanel.Input
        {
            World = s.Light ?? new BlockAccessVoxels(s.Blocks), Atlas = s.Atlas, Time = s.Time, Nether = s.Nether, Entities = ents, Marks = s.Marks,
            Focus = focus, Pointed = s.Pointed, Target = s.Target, Slab = slab, Cut = s.CutAway ? PerspectivePanel.Cut(slab, s.Cy) : null,
            Aids = new ReadingAids.Input { Focus = focus, FeetY = s.Cy, Radius = Math.Min(r, 10), Slab = slab, Rings = false },
            FeetY = s.Cy, Subject = s.Self is { } sb ? (sb.X + 0.5, sb.Y, sb.Z + 0.5) : null, Weather = s.Nether ? default : s.Weather,
        };
        var img = PerspectivePanel.Render(eye, rig, input, out var labels);
        var c = new Canvas(w, h, img.Pixels!);
        string view = s.View.ToString().ToLowerInvariant();
        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"{view} r{r} centre {Fmt(s.Cx, s.Cy, s.Cz)}: {img.Legend}{(s.View == VisionView.Top && s.CutAway ? ", roofs above it cut away" : "")}, rendered like the game; N arrow bottom-right, {w}x{h}px ~{(w * h + 749) / 750}tok.");
        legend.Append(" Grid = 1 block (every 5th labelled with its X/Z), gold Y ruler at the centre.");
        if (s.Self is { } self) legend.Append(CultureInfo.InvariantCulture, $" @=you {Fmt(self.X, self.Y, self.Z)} (orange tag).");
        if (s.Focus is { } fl) legend.Append(CultureInfo.InvariantCulture, $" Cyan box=focus {Fmt(fl.X, fl.Y, fl.Z)}.");
        if (s.Pointed is { } pt) legend.Append(CultureInfo.InvariantCulture, $" Pink box=pointed {Fmt(pt.X, pt.Y, pt.Z)}{(s.PointedBy != null ? " by " + s.PointedBy : "")}.");
        if (BlockRoles.Legend(RolesIn(slab).Where(PerspectivePanel.Shown)) is { Length: > 0 } rl) legend.Append(" Roles: ").Append(rl).Append('.');
        if (PerspectivePanel.DropsText(s.Entities, focus) is { } drops) legend.Append(' ').Append(drops).Append('.');
        return Finish(c, key, legend.ToString(), labels);
    }
}
