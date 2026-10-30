using System.Globalization;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>
/// The 3D panels of a <c>see</c> sheet, all rendered by the one <see cref="EyeRenderer"/> with different cameras around the
/// focus: <c>eye</c> (the body's first person), <c>shoulder</c> (third person behind the body), <c>orbit</c>/<c>orbit2</c>/
/// <c>orbit3</c>/<c>orbit4</c> (40° down from the NW/SE/NE/SW corners), <c>high</c> (65° down from the south, north up) and
/// <c>iso</c> (35° down from the SW corner, the old isometric framing as a perspective render). Overlays: the role layer as
/// subtle tints + outlines on the cells each pixel shows (gen cells, own-placed, player builds, never-break, portal, crops,
/// planned), the focus as a depth-tested cyan box, name tags, drop labels, a compass and the panel's title chip.
/// </summary>
public static partial class PerspectivePanel
{
    public static readonly IReadOnlySet<string> Views = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "eye", "shoulder", "orbit", "orbit2", "orbit3", "orbit4", "high", "iso" };

    private const uint FocusC = 0x5fe8ff, ChipBg = 0x2c313c, Light = 0xe8ecf4;

    /// <summary>A camera for a panel.</summary>
    public readonly record struct Rig(double X, double Y, double Z, float Yaw, float Pitch, float Fov, string Title, string Legend, bool Cut, int MaxDistance,
        double Ortho = 0, ClipBox? Clip = null);

    /// <summary>TOP: straight down, orthographic (one block = one grid cell everywhere), north up, roofs cut away.</summary>
    public static Rig Top((int X, int Y, int Z) focus, int feetY, int radius) =>
        new(focus.X + 0.5, feetY + 8, focus.Z + 0.5, 180, 90, 50, "TOP N^", $"TOP (straight down, orthographic, N up, grid 1 block, feet y{feetY})", true, 72,
            Ortho: radius + 1.2, Clip: new ClipBox(focus.X - 64, 0, focus.Z - 64, focus.X + 64, feetY + 6, focus.Z + 64));

    /// <summary>SIDE: an orthographic cut along the work axis: looking north through z (or east through x), the slice and 3 behind.</summary>
    public static Rig Side((int X, int Y, int Z) focus, int feetY, int radius, SideAxis axis)
    {
        double ortho = Math.Max((radius + 1.2) * 0.75, 5.5);
        var clip = axis == SideAxis.LookEast
            ? new ClipBox(focus.X, feetY - 6, focus.Z - radius - 1, focus.X + 3, feetY + 5, focus.Z + radius + 1)
            : new ClipBox(focus.X - radius - 1, feetY - 6, focus.Z - 3, focus.X + radius + 1, feetY + 5, focus.Z);
        return axis == SideAxis.LookEast
            ? new Rig(focus.X - 2, feetY + 0.5, focus.Z + 0.5, 270, 0, 50, "SIDE >E", $"SIDE (orthographic cut looking E through x{focus.X}, N left)", false, 40, ortho, clip)
            : new Rig(focus.X + 0.5, feetY + 0.5, focus.Z + 3, 180, 0, 50, "SIDE >N", $"SIDE (orthographic cut looking N through z{focus.Z}, W left)", false, 40, ortho, clip);
    }

    /// <summary>One layer seen from straight above (everything else clipped away): the feet layer or the one below.</summary>
    public static Rig Layer((int X, int Y, int Z) focus, int y, int radius, string title) =>
        new(focus.X + 0.5, y + 3, focus.Z + 0.5, 180, 90, 50, title, $"{title} (only y{y}, from above)", false, 40, Ortho: Math.Min(radius, 6) + 1.2,
            Clip: new ClipBox(focus.X - 40, y, focus.Z - 40, focus.X + 40, y, focus.Z + 40));

    /// <summary>Where the body stands and looks (third-person panels and the self model).</summary>
    public readonly record struct Body(double X, double Y, double Z, float Yaw, float Pitch, string? Name);

    private static string Dir8(double dx, double dz)
    {
        // Compass name of a horizontal direction (dx east, dz south).
        double ang = Math.Atan2(dx, -dz) * 180 / Math.PI; // 0 = north, 90 = east
        string[] n = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        return n[(int)Math.Round(((ang % 360) + 360) % 360 / 45.0) % 8];
    }

    /// <summary>The camera of a named view around the focus, or null when it needs a body that isn't there.</summary>
    /// <summary>A free camera on a spot: from a compass side ("N".."NW", or "above" straight-ish down) at a distance, looking at it.</summary>
    public static Rig Cam((int X, int Y, int Z) focus, int feetY, string? from, double? dist, string caption, int radius = 6)
    {
        var f = (from ?? "S").Trim().ToUpperInvariant();
        bool above = f is "ABOVE" or "UP" or "TOP";
        var (sx, sz) = f switch
        {
            "N" => (0, -1), "NE" => (1, -1), "E" => (1, 0), "SE" => (1, 1), "S" => (0, 1), "SW" => (-1, 1), "W" => (-1, 0), "NW" => (-1, -1), _ => (0, 1),
        };
        double elev = above ? 80 : 35, d = Math.Clamp(dist ?? radius * 1.8 + 4, 3, 48);
        double fx = focus.X + 0.5, fy = feetY + 0.5, fz = focus.Z + 0.5;
        double h = d * Math.Cos(elev * Math.PI / 180), v = d * Math.Sin(elev * Math.PI / 180), len = Math.Sqrt(sx * sx + sz * sz);
        double cx = fx + sx / len * h, cy = fy + v, cz = fz + sz / len * h;
        var (yaw, pitch) = EyeScene.LookAt(cx, cy, cz, fx, fy, fz);
        string side = above ? "ABOVE" : f.Length <= 2 ? f : "S";
        return new Rig(cx, cy, cz, yaw, pitch, 55, $"{caption} {side}", $"CAM {caption} from {(above ? "above" : side)}, {d:0} m", true, (int)(d + 2 * radius + 10));
    }

    /// <summary>n orbit cameras evenly spaced around a spot, 40° down, the first from the NW.</summary>
    public static IEnumerable<Rig> Orbits((int X, int Y, int Z) focus, int feetY, int radius, int n, string caption)
    {
        n = Math.Clamp(n, 1, 6);
        double fx = focus.X + 0.5, fy = feetY + 0.5, fz = focus.Z + 0.5;
        const float fov = 50;
        double d = (radius + 1.5) / Math.Tan(fov * Math.PI / 360) * 1.05;
        double h = d * Math.Cos(40 * Math.PI / 180), v = d * Math.Sin(40 * Math.PI / 180);
        for (int i = 0; i < n; i++)
        {
            double ang = Math.PI * 1.25 + i * 2 * Math.PI / n; // start NW (dx −, dz −)
            double sx = Math.Cos(ang), sz = Math.Sin(ang);
            double cx = fx + sx * h, cy = fy + v, cz = fz + sz * h;
            var (yaw, pitch) = EyeScene.LookAt(cx, cy, cz, fx, fy, fz);
            string from = Dir8(sx, sz);
            yield return new Rig(cx, cy, cz, yaw, pitch, fov, $"{caption} {i + 1}/{n} {from}", $"ORBIT {i + 1}/{n} of {caption} from {from}", true, (int)(d + 2 * radius + 8));
        }
    }

    /// <summary>Over the shoulder of a body (a player, a bot): behind and above it, looking where it looks.</summary>
    public static Rig Follow(Body b, string caption)
    {
        double yawR = b.Yaw * Math.PI / 180, ex = b.X, ey = b.Y + 1.62, ez = b.Z;
        double fwx = -Math.Sin(yawR), fwz = Math.Cos(yawR), rx = -fwz, rz = fwx;
        double cx = ex - fwx * 3.4 - rx * 0.9, cy = ey + 1.1, cz = ez - fwz * 3.4 - rz * 0.9;
        var (yaw, pitch) = EyeScene.LookAt(cx, cy, cz, ex + fwx * 5, ey - 1.6 + Math.Clamp(-b.Pitch / 30.0, -2, 2), ez + fwz * 5);
        string dir = EyeRenderer.Compass(yaw);
        return new Rig(cx, cy, cz, yaw, pitch, 62f, $"FOLLOW {Short(b.Name ?? "?")} >{dir}", $"FOLLOW {b.Name} (behind, facing {dir})", false, 48);
    }

    /// <summary>A body's own eyes.</summary>
    public static Rig EyeOf(Body b, string caption) =>
        new(b.X, b.Y + 1.62, b.Z, b.Yaw, b.Pitch, 70f, $"{caption} >{EyeRenderer.Compass(b.Yaw)}", $"{caption} (first person of {b.Name}, facing {EyeRenderer.Compass(b.Yaw)})", false, 48);

    public static Rig? For(string view, (int X, int Y, int Z) focus, int feetY, int radius, Body? body, EyeScene? eye, int rotation = 0)
    {
        double fx = focus.X + 0.5, fy = feetY + 0.5, fz = focus.Z + 0.5;
        Rig Orbit(string name, int sx, int sz, double elev, float fov, string title)
        {
            double half = (radius + 1.5) / Math.Tan(fov * Math.PI / 360);
            double d = half * 1.05;
            double h = d * Math.Cos(elev * Math.PI / 180), v = d * Math.Sin(elev * Math.PI / 180);
            double len = Math.Sqrt(sx * sx + sz * sz);
            double cx = fx + sx / len * h, cy = fy + v, cz = fz + sz / len * h;
            var (yaw, pitch) = EyeScene.LookAt(cx, cy, cz, fx, fy, fz);
            string from = Dir8(sx, sz), to = Dir8(-sx, -sz);
            return new Rig(cx, cy, cz, yaw, pitch, fov, title, $"{name} from {from} looking {to}, {elev:0} deg down", true, (int)(d + 2 * radius + 8));
        }
        switch (view)
        {
            case "eye":
                if (eye == null) return null;
                return new Rig(eye.EyeX, eye.EyeY, eye.EyeZ, eye.Yaw, eye.Pitch, 70f, "EYE" + (eye.Viewer is { } who ? " " + Short(who) : "") + " >" + EyeRenderer.Compass(eye.Yaw),
                    $"EYE (first person{(eye.Viewer != null ? " of " + eye.Viewer : "")}) facing {EyeRenderer.Compass(eye.Yaw)}", false, 48);
            case "shoulder":
            {
                if (body is not { } b) return null;
                double ex = b.X, ey = b.Y + 1.62, ez = b.Z;
                double tx = fx - ex, tz = fz - ez;
                bool self = tx * tx + tz * tz < 2.5;
                double yawR = self ? b.Yaw * Math.PI / 180 : Math.Atan2(-tx, tz);
                double fwx = -Math.Sin(yawR), fwz = Math.Cos(yawR), rx = -fwz, rz = fwx;
                double cx = ex - fwx * 3.4 - rx * 0.9, cy = ey + 1.1, cz = ez - fwz * 3.4 - rz * 0.9;
                var (yaw, pitch) = self ? EyeScene.LookAt(cx, cy, cz, ex + fwx * 5, ey - 1.6, ez + fwz * 5) : EyeScene.LookAt(cx, cy, cz, fx, fy, fz);
                string dir = EyeRenderer.Compass(yaw);
                return new Rig(cx, cy, cz, yaw, pitch, 62f, "SHOULDER >" + dir, $"SHOULDER (behind {(b.Name ?? "you")}, facing {dir})", false, 48);
            }
            case "orbit": return Orbit("ORBIT", -1, -1, 40, 50, "ORBIT NW>SE");
            case "orbit2": return Orbit("ORBIT", 1, 1, 40, 50, "ORBIT SE>NW");
            case "orbit3": return Orbit("ORBIT", 1, -1, 40, 50, "ORBIT NE>SW");
            case "orbit4": return Orbit("ORBIT", -1, 1, 40, 50, "ORBIT SW>NE");
            case "iso":
            {
                // Rotation in quarter turns, like the old isometric view: 0 from the SW (north up-right), 1 SE, 2 NE, 3 NW.
                var (sx, sz, name) = (rotation & 3) switch { 1 => (1, 1, "SE>NW"), 2 => (1, -1, "NE>SW"), 3 => (-1, -1, "NW>SE"), _ => (-1, 1, "SW>NE") };
                return Orbit("ISO", sx, sz, 35, 45, $"ISO {name}");
            }
            case "high": return Orbit("HIGH", 0, 1, 65, 50, "HIGH S>N");
            default: return null;
        }
    }

    private static string Short(string name) => name.Length <= 10 ? name : name[..10];

    /// <summary>Cells above the feet layer hanging over air/fluid down to it (roofs, canopies) within the focus window.</summary>
    internal static CutAway? Cut(VisionRenderer.Slab slab, int feetY)
    {
        var cut = new CutAway(slab.X0, slab.Y0, slab.Z0, slab.N, slab.Sy, slab.N);
        for (int iy = 0; iy < slab.Sy; iy++)
        {
            int wy = slab.Y0 + iy;
            if (wy <= feetY + 1) continue; // head height stays: walls at the feet and head layers are the work area
            for (int iz = 0; iz < slab.N; iz++)
                for (int ix = 0; ix < slab.N; ix++)
                {
                    int x = slab.X0 + ix, z = slab.Z0 + iz;
                    if (slab.At(x, wy, z) != 0 && slab.Overhangs(x, wy, z, feetY)) cut.Hide(x, wy, z);
                }
        }
        return cut.Count == 0 ? null : cut;
    }

    /// <summary>Inputs shared by every panel of one sheet.</summary>
    public sealed class Input
    {
        public required IVoxelWorld World { get; init; }
        public TextureAtlas? Atlas { get; init; }
        public long Time { get; init; } = 6000;
        public bool Nether { get; init; }
        public IReadOnlyList<VisionEntity> Entities { get; init; } = [];
        public IReadOnlyList<VisionMark> Marks { get; init; } = [];
        public (int X, int Y, int Z) Focus { get; init; }
        public (int X, int Y, int Z)? Pointed { get; init; }
        public (int X, int Y, int Z)? Target { get; init; }
        internal VisionRenderer.Slab? Slab { get; init; }
        public CutAway? Cut { get; init; }
        public bool Smooth { get; init; }
        /// <summary>The reading aids (grid, rulers, labels, gizmo, rings); null = plain panel (names + compass only).</summary>
        public ReadingAids.Input? Aids { get; init; }
        /// <summary>Second caption line (bottom-left): time of day and staleness ("day, seen 3 min ago").</summary>
        public string? Stamp { get; init; }
        /// <summary>The x-ray cut of the outside cameras (orbit, iso, high, cam): automatic when the subject is hidden.</summary>
        public XRay XRay { get; init; } = XRay.Auto;
        /// <summary>The body the x-ray keeps in sight (feet position); null = only the focus.</summary>
        public (double X, double Y, double Z)? Subject { get; init; }
        /// <summary>Feet layer of the spot: the x-ray never cuts below it.</summary>
        public int? FeetY { get; init; }
        public Weather Weather { get; init; }
    }

    /// <summary>The x-ray cut a rig needs (<see cref="ViewCut"/>): only outside perspective cameras that cut (orbit, iso,
    /// high, cam; never the eye, the shoulder or the orthographic maps), only when the body or the focus is hidden from it
    /// (<see cref="XRay.Auto"/>) or always (<see cref="XRay.On"/>).</summary>
    public static ViewCut? XRayFor(Rig rig, Input inp)
    {
        if (!rig.Cut || rig.Ortho > 0 || inp.XRay == XRay.Off) return null;
        int feet = inp.FeetY ?? inp.Focus.Y;
        return ViewCut.For(inp.World, rig.X, rig.Y, rig.Z, inp.Subject, inp.Focus, XRayRadius, feet, inp.Cut, inp.XRay == XRay.On);
    }

    /// <summary>Window radius of the x-ray at the subject: 3 blocks shows the tunnel or room around the body, not the hillside.</summary>
    public const double XRayRadius = 3.0;

    /// <summary>Renders one panel (raster + role overlay + focus box + tags + compass + title).</summary>
    public static VisionImage Render(EyeRenderer r, Rig rig, Input inp, out List<string> labels) => Render(r, rig, inp, out labels, out _);

    /// <summary>Renders one panel; <paramref name="facts"/> = the reading aids' labels as text ("chest 14,64,-5").</summary>
    public static VisionImage Render(EyeRenderer r, Rig rig, Input inp, out List<string> labels, out List<string> facts)
    {
        var xray = XRayFor(rig, inp);
        var scene = new EyeScene
        {
            World = inp.World, Atlas = inp.Atlas, EyeX = rig.X, EyeY = rig.Y, EyeZ = rig.Z, Yaw = rig.Yaw, Pitch = rig.Pitch, Time = inp.Time,
            Nether = inp.Nether, Entities = inp.Entities, Marks = inp.Marks, Fov = rig.Fov, MaxDistance = rig.MaxDistance,
            Cut = rig.Cut ? inp.Cut : null, MinBrightness = rig.Ortho > 0 ? 0.6f : 0.3f, Smooth = inp.Smooth, Ortho = rig.Ortho, Clip = rig.Clip,
            XRay = xray, Weather = inp.Weather,
            // Iso and the maps get the weather's wash; the other panels sparse streaks (never enough to hide a block).
            Precip = rig.Ortho > 0 || rig.Title.StartsWith("ISO", StringComparison.Ordinal) ? PrecipStyle.Tint : PrecipStyle.Streaks, PrecipDensity = 0.3f,
        };
        var fr = r.Raster(scene);
        var c = new Canvas(fr.Width, fr.Height, (uint[])fr.Px.Clone());
        if (inp.Slab is { Roles: not null } slab) RoleOverlay(r, c, fr, scene, slab);
        if (inp.Pointed is { } pt) r.BlockBox(c, fr, scene, pt, 0xff4fd8);
        r.BlockBox(c, fr, scene, inp.Focus, FocusC, -0.03f);
        var placer = new VisionRenderer.Placer(0, 0, fr.Width, fr.Height);
        string title = xray != null ? rig.Title + " X-RAY" : rig.Title;
        int tw = PixelFont.Width(title) + 4;
        placer.Reserve(0, 0, tw, 11);
        placer.Reserve(fr.Width - 30, fr.Height - 30, 30, 30);
        if (inp.Stamp != null) placer.Reserve(0, fr.Height - 11, PixelFont.Width(inp.Stamp) + 4, 11);
        if (inp.Aids is { } aids)
        {
            facts = ReadingAids.Draw(r, c, fr, scene, aids, placer);
            labels = facts;
        }
        else
        {
            facts = [];
            labels = r.Tags(c, fr, scene, placer, marks: true, drops: true);
            r.Compass(c, scene, fr.Width - 12, fr.Height - 12);
        }
        c.Chip(0, 0, title, Light, ChipBg, 1f);
        if (inp.Stamp != null) c.Chip(0, fr.Height - 11, inp.Stamp, 0xb8c0d0, ChipBg, 0.9f);
        string legend = xray == null ? rig.Legend : rig.Legend + ", x-ray: blocks between the camera and you/the focus cut away, cut faces hatched";
        return new VisionImage([], fr.Width, fr.Height, 0, legend, labels) { Pixels = c.Px, XRay = xray != null };
    }

    /// <summary>Roles drawn on what each pixel shows: a light tint over the cell's pixels and its silhouette in the role colour;
    /// air cells with a role (a refilling gen cell) get a depth-tested wire box; G/X/P/H letters at visible cell centres.</summary>
    private static void RoleOverlay(EyeRenderer r, Canvas c, EyeRenderer.Frame fr, EyeScene s, VisionRenderer.Slab slab)
    {
        if (fr.Cells is not { } cells) return;
        int W = fr.Width, H = fr.Height;
        var roleOf = new Dictionary<long, BlockRole>();
        BlockRole RoleAt(long k)
        {
            if (k == EyeRenderer.NoCell) return BlockRole.None;
            if (roleOf.TryGetValue(k, out var role)) return role;
            var (x, y, z) = EyeRenderer.FromCellKey(k);
            role = slab.RoleAt(x, y, z);
            // A refilling gen cell is air: tint the floor it sits on, so it shows from above like a cobble one.
            if (!Shown(role) && slab.At(x, y + 1, z) == 0 && slab.RoleAt(x, y + 1, z) == BlockRole.GenCell) role = BlockRole.GenCell;
            // Lava (opaque) flowing over a gen cell hides it from above: tint the fluid in the cell's colour.
            if (!Shown(role) && BlockGeometry.IsFluid(slab.At(x, y, z)) && slab.RoleAt(x, y - 1, z) == BlockRole.GenCell) role = BlockRole.GenCell;
            if (!Shown(role)) role = BlockRole.None;
            roleOf[k] = role;
            return role;
        }
        var src = (uint[])c.Px.Clone();
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                long k = cells[y * W + x];
                var role = RoleAt(k);
                if (role == BlockRole.None) continue;
                uint col = BlockRoles.Colour(role);
                bool edge = x == 0 || y == 0 || x == W - 1 || y == H - 1 || cells[y * W + x - 1] != k || cells[y * W + x + 1] != k || cells[(y - 1) * W + x] != k || cells[(y + 1) * W + x] != k;
                c.Px[y * W + x] = edge ? IsoPalette.Mix(src[y * W + x], col, 0.85f) : IsoPalette.Mix(src[y * W + x], col, role == BlockRole.GenCell ? 0.38f : 0.22f);
            }
        // Air cells with a role (a gen cell refilling, a planned block not yet placed) and the letters.
        int letters = 0;
        for (int iy = 0; iy < slab.Sy; iy++)
            for (int iz = 0; iz < slab.N; iz++)
                for (int ix = 0; ix < slab.N; ix++)
                {
                    int x = slab.X0 + ix, y = slab.Y0 + iy, z = slab.Z0 + iz;
                    var role = slab.RoleAt(x, y, z);
                    if (!Shown(role)) continue;
                    if (slab.At(x, y, z) == 0) r.BlockBox(c, fr, s, (x, y, z), BlockRoles.Colour(role), 0.06f);
                    if (letters < 10 && role is BlockRole.GenCell or BlockRole.NeverBreak or BlockRole.Portal
                        && BlockRoles.Glyph(role) is { } g && r.Project(s, x + 0.5, y + 1.02, z + 0.5) is { } p
                        && fr.Depth[p.Y * W + p.X] >= p.Dist - 0.6)
                    {
                        c.Fill(p.X - 3, p.Y - 4, 7, 9, 0x101318);
                        c.Text(p.X - 2, p.Y - 3, g.ToString(), BlockRoles.Colour(role));
                        letters++;
                    }
                }
    }

    /// <summary>Roles worth drawing on a 3D panel (stations, doors and soft protection read fine from the blocks themselves).</summary>
    public static bool Shown(BlockRole r) => r is BlockRole.GenCell or BlockRole.Portal or BlockRole.NeverBreak or BlockRole.PlayerBuild
        or BlockRole.OwnPlaced or BlockRole.Planned or BlockRole.CropRipe or BlockRole.CropGrowing or BlockRole.DarkSpawn;

    /// <summary>Drops near a spot as text: "2 drops: 2 sapling at 4,64,9; cobblestone at 3,64,8".</summary>
    public static string? DropsText(IEnumerable<VisionEntity> ents, (int X, int Y, int Z)? near = null, int max = 6)
    {
        var drops = ents.Where(e => e.Kind == "item").ToList();
        if (drops.Count == 0) return null;
        if (near is { } n) drops = drops.OrderBy(e => Math.Abs(e.X - n.X - 0.5) + Math.Abs(e.Z - n.Z - 0.5) + Math.Abs(e.Y - n.Y)).ToList();
        var parts = drops.Take(max).Select(e => string.Create(CultureInfo.InvariantCulture,
            $"{EyeRenderer.DropLabel(e)} at {(int)Math.Floor(e.X)},{(int)Math.Floor(e.Y)},{(int)Math.Floor(e.Z)}"));
        return $"{drops.Count} drop{(drops.Count == 1 ? "" : "s")}: " + string.Join("; ", parts) + (drops.Count > max ? "; ..." : "");
    }
}

/// <summary><see cref="IVoxelWorld"/> over an <see cref="IBlockAccess"/> without stored light (a design preview's overlay, tests):
/// sky light 15 above the highest opaque block of a column, else 4; no block light.</summary>
public sealed class BlockAccessVoxels(IBlockAccess blocks) : IVoxelWorld
{
    private readonly Dictionary<(int, int), int> _top = new();
    private readonly object _lock = new();
    private readonly int _h = blocks.WorldHeight;

    public int WorldHeight => _h;

    public int Block(int x, int y, int z) => (uint)y < _h ? blocks.GetBlock(x, y, z) : 0;
    public int Meta(int x, int y, int z) => (uint)y < _h ? blocks.GetMeta(x, y, z) : 0;

    public int SkyLight(int x, int y, int z)
    {
        int top;
        lock (_lock)
            if (!_top.TryGetValue((x, z), out top))
            {
                top = -1;
                for (int yy = _h - 1; yy >= 0; yy--)
                    if (BlockGeometry.OpaqueCube(blocks.GetBlock(x, yy, z))) { top = yy; break; }
                _top[(x, z)] = top;
            }
        return y > top ? 15 : 4;
    }

    public int BlockLight(int x, int y, int z) => 0;
    public ColumnState State(int x, int z) => blocks.IsLoaded(x, z) ? ColumnState.Live : ColumnState.Unknown;
}
