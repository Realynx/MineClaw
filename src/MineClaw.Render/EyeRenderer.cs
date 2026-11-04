using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>A perspective view to render: an eye position, yaw/pitch (Minecraft convention: yaw 0 = south, 90 = west;
/// pitch +90 = down), the world, textures (null = palette colours) and the overlays. The first-person EYE, the over-the-
/// shoulder, orbit and high oblique panels, the "iso" view and the free cameras are all this scene with another camera.</summary>
public sealed class EyeScene
{
    public required IVoxelWorld World { get; init; }
    public TextureAtlas? Atlas { get; init; }
    public double EyeX { get; init; }
    public double EyeY { get; init; }
    public double EyeZ { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    /// <summary>World time (ticks) for sky light and sky colour (6000 = noon, 18000 = midnight): the day/night setting.</summary>
    public long Time { get; init; } = 6000;
    public bool Nether { get; init; }
    /// <summary>Smooth lighting: per-vertex average of the 4 cells around each face corner, bilinear across the face.</summary>
    public bool Smooth { get; init; }
    public IReadOnlyList<VisionEntity> Entities { get; init; } = [];
    public IReadOnlyList<VisionMark> Marks { get; init; } = [];
    public (int X, int Y, int Z)? Target { get; init; }
    public (int X, int Y, int Z)? Pointed { get; init; }
    /// <summary>Whose eyes (for the legend): null = the bot.</summary>
    public string? Viewer { get; init; }
    /// <summary>Vertical field of view override (null = the renderer's <see cref="EyeRenderer.FovDegrees"/>).</summary>
    public float? Fov { get; init; }
    /// <summary>View distance override (null = the renderer's <see cref="EyeRenderer.MaxDistance"/>).</summary>
    public int? MaxDistance { get; init; }
    /// <summary>Cells not drawn (roofs cut away over the work layer in the orbit panels).</summary>
    public CutAway? Cut { get; init; }
    /// <summary>The x-ray cut of a camera outside its subject (<see cref="ViewCut"/>: the cells between the camera and a body in
    /// a tunnel, cave or under a roof); faces it exposes are hatched. Null = exact (the freecam, the eye).</summary>
    public ViewCut? XRay { get; init; }
    /// <summary>Rain/thunder strengths: a darker, greyer sky and fog, less sky light, and precipitation (<see cref="MineClaw.Render.Weather"/>).</summary>
    public Weather Weather { get; init; }
    /// <summary>How precipitation shows: Auto = streaks/flakes in perspective views, a tint in orthographic ones.</summary>
    public PrecipStyle Precip { get; init; }
    /// <summary>Precipitation density scale (1 = the freecam's; sheets draw sparser streaks so they never hide a block).</summary>
    public float PrecipDensity { get; init; } = 1f;
    /// <summary>Animation phase of the falling streaks (ticks; the freecam advances it, still pictures leave it).</summary>
    public long PrecipTick { get; init; }
    /// <summary>Darkest a lit face gets (null = the renderer's <see cref="EyeRenderer.MinBrightness"/>; maps use a higher floor).</summary>
    public float? MinBrightness { get; init; }
    /// <summary>Orthographic projection: half the view height in blocks (0 = perspective). TOP/SIDE/LAYERS maps use it.</summary>
    public double Ortho { get; init; }
    /// <summary>Only blocks inside this box are drawn (a side slice, one layer); null = everything.</summary>
    public ClipBox? Clip { get; init; }

    /// <summary>The entity is the camera's own body (the eye inside it): no tag, no label.</summary>
    internal bool IsViewerBody(VisionEntity e)
    {
        double hx = EyeX - e.X, hz = EyeZ - e.Z;
        return hx * hx + hz * hz < 0.36 && EyeY - e.Y is > -0.2 and < 2.0;
    }

    /// <summary>Yaw/pitch (MC degrees) from (ex, ey, ez) toward (tx, ty, tz).</summary>
    public static (float Yaw, float Pitch) LookAt(double ex, double ey, double ez, double tx, double ty, double tz)
    {
        double dx = tx - ex, dy = ty - ey, dz = tz - ez;
        return ((float)(Math.Atan2(-dx, dz) * 180 / Math.PI), (float)(-Math.Atan2(dy, Math.Sqrt(dx * dx + dz * dz)) * 180 / Math.PI));
    }
}

/// <summary>An inclusive block box: the only cells a clipped view draws.</summary>
public readonly record struct ClipBox(int X0, int Y0, int Z0, int X1, int Y1, int Z1)
{
    public bool Contains(int x, int y, int z) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1 && z >= Z0 && z <= Z1;
}

/// <summary>A box of cells hidden from a view (roofs over the work layer); everything outside the box is drawn.</summary>
public sealed class CutAway(int x0, int y0, int z0, int sx, int sy, int sz)
{
    private readonly bool[] _mask = new bool[sx * sy * sz];
    public int Count { get; private set; }

    public void Hide(int x, int y, int z)
    {
        int ix = x - x0, iy = y - y0, iz = z - z0;
        if ((uint)ix >= (uint)sx || (uint)iy >= (uint)sy || (uint)iz >= (uint)sz) return;
        ref bool m = ref _mask[(iy * sz + iz) * sx + ix];
        if (!m) Count++;
        m = true;
    }

    public bool Hidden(int x, int y, int z)
    {
        int ix = x - x0, iy = y - y0, iz = z - z0;
        return (uint)ix < (uint)sx && (uint)iy < (uint)sy && (uint)iz < (uint)sz && _mask[(iy * sz + iz) * sx + ix];
    }
}

/// <summary>
/// The x-ray cut for a camera outside its subject (live 10-29, Fox: "when the bot's in the tunnel… there's no x-ray on the blocks
/// in front of the camera for things like orbit and ISO, so you just see blocks in your face"). One cone per subject, its apex at
/// the camera and its base a disk of <see cref="Radius"/> blocks around the subject point, perpendicular to the view line: every
/// cell whose centre lies inside a cone (with the cell's half-diagonal of slack) and in front of the subject's plane is not
/// drawn, so the picture keeps everything around the hole and the subject shows at the bottom of a clean window, tunnel floor
/// and far walls included. Never cut: anything below <see cref="FloorY"/> (the floor the subject stands on), anything behind
/// the subject. Why a camera cone and not a section cut (everything above head height within a radius) or a fixed cylinder:
/// a section cut never opens a line of sight through rock outside its radius (a bot 20 blocks underground stays hidden by the
/// hillside the 40° ray crosses first), and a cylinder blows a hole as wide as itself next to the camera, wiping the whole
/// foreground; the cone removes exactly the cells that hide the window and nothing else, at any depth. Analytic (no mask box),
/// so it reaches the camera however far away it is. Faces the cut exposes are hatched by the renderer (cool tint + diagonal
/// stripes), so a cut edge never reads as a real wall.
/// </summary>
public sealed class ViewCut
{
    private readonly (double Px, double Py, double Pz, double Ux, double Uy, double Uz, double D)[] _cones;
    private readonly double _cx, _cy, _cz;

    /// <summary>Window radius at the subject (blocks).</summary>
    public double Radius { get; }
    /// <summary>Lowest y the cut may remove (the subject's feet layer; the floor below always stays).</summary>
    public int FloorY { get; }
    public int Subjects => _cones.Length;

    public ViewCut(double camX, double camY, double camZ, IEnumerable<(double X, double Y, double Z)> subjects, double radius, int floorY)
    {
        (_cx, _cy, _cz) = (camX, camY, camZ);
        Radius = radius;
        FloorY = floorY;
        var list = new List<(double, double, double, double, double, double, double)>();
        foreach (var (x, y, z) in subjects)
        {
            double ax = x - camX, ay = y - camY, az = z - camZ, d = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (d < 1.5) continue;
            list.Add((x, y, z, ax / d, ay / d, az / d, d));
        }
        _cones = list.ToArray();
    }

    /// <summary>The cell (x, y, z) is cut away.</summary>
    public bool Hides(int x, int y, int z)
    {
        if (y < FloorY) return false;
        double qx = x + 0.5 - _cx, qy = y + 0.5 - _cy, qz = z + 0.5 - _cz;
        double q2 = qx * qx + qy * qy + qz * qz;
        foreach (var c in _cones)
        {
            double t = qx * c.Ux + qy * c.Uy + qz * c.Uz;
            if (t <= 0 || t >= c.D) continue;
            double r = Radius * t / c.D + 0.87, perp2 = q2 - t * t;
            if (perp2 <= r * r) return true;
        }
        return false;
    }

    /// <summary>
    /// Does the world hide the point (x, y, z) from the camera? Walks the segment (DDA) toward the camera and stops at the first
    /// opaque cube or leaf block that is not already cut (<paramref name="mask"/>, the roof cut). The start cell never counts.
    /// </summary>
    public static bool Blocked(IVoxelWorld w, double x, double y, double z, double camX, double camY, double camZ, CutAway? mask = null)
    {
        double dx = camX - x, dy = camY - y, dz = camZ - z, len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (len < 1e-6) return false;
        dx /= len; dy /= len; dz /= len;
        var d = new EyeRenderer.Dda(x, y, z, dx, dy, dz);
        int h = w.WorldHeight;
        for (int i = 0; i < 512; i++)
        {
            double t = d.Exit;
            d.Advance();
            if (t >= len - 0.3) return false;
            if ((uint)d.Vy >= (uint)h) { if (d.Vy >= h && dy > 0) return false; continue; }
            if (Occludes(w, d.Vx, d.Vy, d.Vz, mask)) return true;
        }
        return false;
    }

    /// <summary>An opaque cube or leaves that the roof cut (<paramref name="mask"/>) left in place.</summary>
    private static bool Occludes(IVoxelWorld w, int x, int y, int z, CutAway? mask)
    {
        int id = w.Block(x, y, z);
        if (id == 0 || !(BlockGeometry.OpaqueCube(id) || id is Blocks.Leaves or 161)) return false;
        return !(mask != null && mask.Hidden(x, y, z));
    }

    /// <summary>
    /// The cut a camera needs for its subjects, or null when nothing hides them (an open island: the picture stays exactly as
    /// it was). A body counts as hidden when two of its feet / waist / head points are; a focus block when its top centre is.
    /// <paramref name="force"/> = cut even when nothing is hidden yet (the Explorer's "x-ray on").
    /// </summary>
    public static ViewCut? For(IVoxelWorld w, double camX, double camY, double camZ, (double X, double Y, double Z)? body,
        (int X, int Y, int Z)? focus, double radius, int floorY, CutAway? mask = null, bool force = false)
    {
        var subjects = new List<(double, double, double)>();
        bool hidden = false;
        if (body is { } b)
        {
            int n = 0;
            foreach (double h in new[] { 0.3, 1.0, 1.55 })
                if (Blocked(w, b.X, b.Y + h, b.Z, camX, camY, camZ, mask)) n++;
            hidden |= n >= 2;
            subjects.Add((b.X, b.Y + 0.9, b.Z));
        }
        if (focus is { } f && (body is not { } bb || Math.Abs(bb.X - (f.X + 0.5)) + Math.Abs(bb.Z - (f.Z + 0.5)) + Math.Abs(bb.Y - f.Y) > 1.5))
        {
            hidden |= Blocked(w, f.X + 0.5, f.Y + 1.02, f.Z + 0.5, camX, camY, camZ, mask);
            subjects.Add((f.X + 0.5, f.Y + 1.0, f.Z + 0.5));
        }
        if (!hidden && !force) return null;
        var cut = new ViewCut(camX, camY, camZ, subjects, radius, floorY);
        return cut.Subjects == 0 ? null : cut;
    }
}

/// <summary>
/// The one perspective renderer behind every 3D picture (the first-person EYE, the AI's sheet panels, the "iso" view, the
/// supervisor camera and the Live Hive freecam/tiles): a CPU voxel raycaster over <see cref="BlockGeometry"/> and
/// <see cref="EntityGeometry"/>. Per pixel: DDA through the cells; full cubes hit at the entry face (fast path), every other
/// render type intersects its quads (slabs, stairs, torches leaning off walls, crossed plants, crops, doors, ladders, rails,
/// fences, panes, cactus, signs...); fluids use vanilla corner heights; cutout texels (fancy leaves, glass, plants) are
/// alpha-tested; water, ice and portals blend front to back; players and mobs are posed b1.8.1 models, dropped items spin
/// and bob; shadows under entities; vanilla per-face shade × light (block/sky light and the time of day), distance fog.
/// Buffers are reused between frames; rows render in parallel bands (each with its own light and shape caches). One instance
/// per caller; not thread-safe.
/// </summary>
public sealed class EyeRenderer
{
    public int Width { get; init; } = 320;
    public int Height { get; init; } = 240;
    /// <summary>Vertical field of view (vanilla "Normal" FOV is 70°).</summary>
    public float FovDegrees { get; init; } = 70f;
    public int MaxDistance { get; init; } = 64;
    /// <summary>Darkest a lit face gets (the client's brightness slider; keeps night pictures readable).</summary>
    public float MinBrightness { get; init; } = 0.12f;
    /// <summary>Most row bands rendered in parallel (CPU cap for background viewers; 1 = the calling thread only; ≤ 64).</summary>
    public int MaxThreads { get; init; } = 8;

    private uint[] _px = [];
    private float[] _depth = [];
    private long[] _cells = [];
    private BandCache[] _caches = [];
    public double LastMs { get; private set; }

    /// <summary>Cell id of a pixel that hit no block (sky, void, an entity).</summary>
    public const long NoCell = long.MinValue;

    /// <summary>A cell as one long: 21 bits each (x and z offset by 2^20, y as is).</summary>
    public static long CellKey(int x, int y, int z) => ((long)(x + (1 << 20)) << 42) | ((long)(z + (1 << 20)) << 21) | (long)(y & 0x1fffff);
    public static (int X, int Y, int Z) FromCellKey(long k) => ((int)(k >> 42) - (1 << 20), (int)(k & 0x1fffff), (int)((k >> 21) & 0x1fffff) - (1 << 20));

    private sealed class BandCache
    {
        public readonly long[] Keys = new long[4096];
        public readonly float[] Vals = new float[4096 * 4];
        public readonly long[] ShapeKeys = new long[2048];
        public readonly GeoQuad[]?[] Shapes = new GeoQuad[]?[2048];
        public readonly List<GeoQuad> Tmp = new(32);
        public void Clear() { Array.Fill(Keys, long.MinValue); Array.Fill(ShapeKeys, long.MinValue); }
    }

    /// <summary>A rendered frame: 0xRRGGBB pixels, the distance of each pixel (+∞ = sky), and the cell each pixel shows.</summary>
    public readonly record struct Frame(uint[] Px, float[] Depth, int Width, int Height, long[]? Cells = null);

    private readonly record struct Cam(double Ox, double Oy, double Oz, double Fx, double Fy, double Fz, double Rx, double Ry, double Rz,
        double Ux, double Uy, double Uz, double TanX, double TanY, bool Ortho = false);

    private float Fov(EyeScene s) => s.Fov ?? FovDegrees;
    private int MaxDist(EyeScene s) => s.MaxDistance ?? MaxDistance;

    private Cam Camera(EyeScene s)
    {
        double yaw = s.Yaw * Math.PI / 180, pitch = Math.Clamp(s.Pitch, -89.9f, 89.9f) * Math.PI / 180;
        double fx = -Math.Sin(yaw) * Math.Cos(pitch), fy = -Math.Sin(pitch), fz = Math.Cos(yaw) * Math.Cos(pitch);
        // right = forward × up; up' = right × forward
        double rx = -fz, ry = 0, rz = fx;
        double rl = Math.Sqrt(rx * rx + rz * rz);
        if (rl < 1e-9) { rx = -1; rz = 0; rl = 1; }
        rx /= rl; rz /= rl;
        double ux = ry * fz - rz * fy, uy = rz * fx - rx * fz, uz = rx * fy - ry * fx;
        if (s.Ortho > 0) return new Cam(s.EyeX, s.EyeY, s.EyeZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, s.Ortho * Width / Height, s.Ortho, true);
        double tanY = Math.Tan(Fov(s) * Math.PI / 360), tanX = tanY * Width / Height;
        return new Cam(s.EyeX, s.EyeY, s.EyeZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, tanX, tanY);
    }

    /// <summary>The world point a pixel shows at distance <paramref name="dist"/> along its ray (the inverse of <see cref="Project"/>).</summary>
    public (double X, double Y, double Z) Unproject(EyeScene s, double px, double py, double dist)
    {
        var cam = Camera(s);
        double sx = (2 * px / Width - 1) * cam.TanX, sy = (1 - 2 * py / Height) * cam.TanY;
        if (cam.Ortho)
            return (cam.Ox + cam.Rx * sx + cam.Ux * sy + cam.Fx * dist, cam.Oy + cam.Ry * sx + cam.Uy * sy + cam.Fy * dist, cam.Oz + cam.Rz * sx + cam.Uz * sy + cam.Fz * dist);
        double dx = cam.Fx + cam.Rx * sx + cam.Ux * sy, dy = cam.Fy + cam.Ry * sx + cam.Uy * sy, dz = cam.Fz + cam.Rz * sx + cam.Uz * sy;
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (cam.Ox + dx / l * dist, cam.Oy + dy / l * dist, cam.Oz + dz / l * dist);
    }

    /// <summary>Screen pixel of a world point, with its distance from the eye; null when behind the eye or off-screen.</summary>
    public (int X, int Y, double Dist)? Project(EyeScene s, double x, double y, double z)
    {
        if (ProjectF(s, x, y, z) is not { } p) return null;
        int px = (int)Math.Floor(p.X), py = (int)Math.Floor(p.Y);
        if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) return null;
        return (px, py, p.Dist);
    }

    /// <summary>Unclipped sub-pixel projection (null only behind the eye).</summary>
    public (double X, double Y, double Dist)? ProjectF(EyeScene s, double x, double y, double z)
    {
        var c = Camera(s);
        double dx = x - c.Ox, dy = y - c.Oy, dz = z - c.Oz;
        double f = dx * c.Fx + dy * c.Fy + dz * c.Fz;
        if (f < 0.05) return null;
        double r = dx * c.Rx + dy * c.Ry + dz * c.Rz, u = dx * c.Ux + dy * c.Uy + dz * c.Uz;
        if (c.Ortho) return ((r / c.TanX + 1) / 2 * Width, (-u / c.TanY + 1) / 2 * Height, f);
        double sx = r / f / c.TanX, sy = -u / f / c.TanY;
        return ((sx + 1) / 2 * Width, (sy + 1) / 2 * Height, Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    // ------------------------------------------------------------------ entities

    private sealed class EMesh
    {
        public required VisionEntity E;
        public double Ox, Oy, Oz;
        public double X0, Y0, Z0, X1, Y1, Z1;
        public required GeoQuad[] Quads;
        public float Light = 1f;
    }

    private readonly record struct Shadow(double X, double Y, double Z, double R, float Strength);

    private EMesh[] PrepareEntities(EyeScene s, int sub)
    {
        var list = new List<EMesh>();
        var tmp = new List<GeoQuad>(64);
        foreach (var e in s.Entities)
        {
            // The camera's own body (eye inside its box) is not drawn: a first-person view has no head in the way.
            double hx = s.EyeX - e.X, hz = s.EyeZ - e.Z, hy = s.EyeY - e.Y;
            if (e.Kind != "item" && hx * hx + hz * hz < 0.36 && hy > -0.2 && hy < 2.0) continue;
            tmp.Clear();
            try { EntityGeometry.Build(e, tmp, s.EyeX, s.EyeZ, s.Atlas, s.EyeY); } catch (Exception) { continue; }
            if (tmp.Count == 0) continue;
            float x0 = float.MaxValue, y0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue, z1 = float.MinValue;
            foreach (var q in tmp)
                foreach (var p in new[] { q.P0, q.P1, q.P2, q.P3 })
                {
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); z0 = Math.Min(z0, p.Z);
                    x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); z1 = Math.Max(z1, p.Z);
                }
            int bx = (int)Math.Floor(e.X), by = (int)Math.Floor(e.Y + 0.1), bz = (int)Math.Floor(e.Z);
            int lvl = Math.Max(FaceLight.CellLevel(s.World, bx, by, bz, sub), FaceLight.CellLevel(s.World, bx, by + 1, bz, sub));
            float k = lvl < 0 ? 0.8f : Lighting.Brightness(lvl, s.Nether);
            list.Add(new EMesh
            {
                E = e, Ox = e.X, Oy = e.Y, Oz = e.Z, Quads = tmp.ToArray(), Light = Math.Max(MinB(s), k),
                X0 = e.X + x0 - 0.01, Y0 = e.Y + y0 - 0.01, Z0 = e.Z + z0 - 0.01, X1 = e.X + x1 + 0.01, Y1 = e.Y + y1 + 0.01, Z1 = e.Z + z1 + 0.01,
            });
        }
        return list.ToArray();
    }

    private float MinB(EyeScene s) => s.MinBrightness ?? MinBrightness;

    private static Shadow[] Shadows(EyeScene s, EMesh[] ents)
    {
        var list = new List<Shadow>();
        foreach (var m in ents)
        {
            float r = EntityGeometry.ShadowSize(m.E);
            // Render.renderShadow: the ground under the feet (first solid below, ≤ 2 down).
            int bx = (int)Math.Floor(m.E.X), bz = (int)Math.Floor(m.E.Z);
            for (int dy = 0; dy <= 2; dy++)
            {
                int y = (int)Math.Floor(m.E.Y) - dy;
                int id = s.World.Block(bx, y, bz);
                if (id != 0 && !BlockGeometry.IsFluid(id))
                {
                    list.Add(new Shadow(m.E.X, m.E.Y, m.E.Z, r, m.E.Kind == "item" ? 0.75f : 1f));
                    break;
                }
            }
        }
        return list.ToArray();
    }

    // ------------------------------------------------------------------ raster

    /// <summary>Renders the frame (pixels 0xRRGGBB, depth = distance or +∞ for sky). The arrays are reused by the next call.</summary>
    public Frame Raster(EyeScene s)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int W = Width, H = Height;
        if (_px.Length != W * H) { _px = new uint[W * H]; _depth = new float[W * H]; _cells = new long[W * H]; }
        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Clamp(MaxThreads, 1, 64));
        if (_caches.Length != bands) _caches = Enumerable.Range(0, bands).Select(_ => new BandCache()).ToArray();
        foreach (var lc in _caches) lc.Clear();
        int maxDist = MaxDist(s);
        if (s.World is WorldVoxels wv) wv.Prepare((int)Math.Floor(s.EyeX), (int)Math.Floor(s.EyeZ), maxDist / 16 + 1);
        var cam = Camera(s);
        var wx = s.Nether ? Weather.Clear : s.Weather;
        int sub = s.Nether ? 0 : Lighting.SkylightSubtracted(s.Time, wx.Rain, wx.Thunder);
        var ents = PrepareEntities(s, sub);
        var shadows = Shadows(s, ents);
        var ctx = new Ctx(s, sub, ents, shadows, maxDist, MinB(s)) { Sky = SkyColours(sub, s.Nether, wx) };
        void Row(int b)
        {
            var cache = _caches[b];
            for (int y = b; y < H; y += bands)
                for (int x = 0; x < W; x++)
                {
                    double sx = (2 * (x + 0.5) / W - 1) * cam.TanX, sy = (1 - 2 * (y + 0.5) / H) * cam.TanY;
                    if (cam.Ortho)
                    {
                        // Parallel rays from the image plane through the eye point.
                        var (oc, od, ocell) = Trace(ctx, cam.Ox + cam.Rx * sx + cam.Ux * sy, cam.Oy + cam.Ry * sx + cam.Uy * sy, cam.Oz + cam.Rz * sx + cam.Uz * sy, cam.Fx, cam.Fy, cam.Fz, cache);
                        _px[y * W + x] = oc;
                        _depth[y * W + x] = od;
                        _cells[y * W + x] = ocell;
                        continue;
                    }
                    double dx = cam.Fx + cam.Rx * sx + cam.Ux * sy, dy = cam.Fy + cam.Ry * sx + cam.Uy * sy, dz = cam.Fz + cam.Rz * sx + cam.Uz * sy;
                    double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    dx /= l; dy /= l; dz /= l;
                    var (c, d, cell) = Trace(ctx, cam.Ox, cam.Oy, cam.Oz, dx, dy, dz, cache);
                    _px[y * W + x] = c;
                    _depth[y * W + x] = d;
                    _cells[y * W + x] = cell;
                }
        }
        if (bands == 1) Row(0);
        else Parallel.For(0, bands, Row);
        if (wx.Wet && s.Precip != PrecipStyle.None) Precipitation(s, ctx, cam);
        LastMs = sw.Elapsed.TotalMilliseconds;
        return new Frame(_px, _depth, W, H, _cells);
    }

    private sealed record Ctx(EyeScene S, int Sub, EMesh[] Ents, Shadow[] Shadows, int MaxDist, float MinB)
    {
        /// <summary>The frame's sky colours (time of day and weather).</summary>
        public (uint Top, uint Horizon, uint Void) Sky { get; init; }
    }

    // ------------------------------------------------------------------ weather

    /// <summary>
    /// Precipitation over the finished frame (EntityRenderer.renderRainSnow, simplified for still pictures): perspective views
    /// get streaks (rain: thin slanted lines, snow: soft flakes) at random points of the view volume that see the sky (sky
    /// light 15, or no block above in a column without light), depth-tested against the frame, so a roof or a tunnel keeps them
    /// out; orthographic views (TOP, layers) and <see cref="PrecipStyle.Tint"/> get a light wash of the weather's fog colour
    /// instead. Deterministic per (camera cell, <see cref="EyeScene.PrecipTick"/>): the same still picture twice is identical.
    /// </summary>
    private void Precipitation(EyeScene s, Ctx ctx, Cam cam)
    {
        int W = Width, H = Height;
        var wx = s.Weather;
        var style = s.Precip == PrecipStyle.Auto ? (cam.Ortho ? PrecipStyle.Tint : PrecipStyle.Streaks) : s.Precip;
        if (style == PrecipStyle.Tint)
        {
            uint fog = ctx.Sky.Horizon;
            float a = 0.06f + 0.10f * wx.R + 0.06f * wx.T;
            for (int i = 0; i < W * H; i++) _px[i] = IsoPalette.Mix(_px[i], fog, a);
            return;
        }
        var w = s.World;
        int h = w.WorldHeight;
        int n = (int)(W * H / 300.0 * wx.R * Math.Clamp(s.PrecipDensity, 0f, 4f));
        uint seed = (uint)HashCode.Combine((int)Math.Floor(s.EyeX), (int)Math.Floor(s.EyeY), (int)Math.Floor(s.EyeZ), s.PrecipTick / 2);
        uint Next() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed; }
        double R01() => (Next() & 0xffffff) / (double)0x1000000;
        bool Open(int bx, int by, int bz)
        {
            if (by >= h) return true;
            if (by < 0) return false;
            int sky = w.SkyLight(bx, by, bz);
            if (sky >= 15) return w.Block(bx, by, bz) == 0 || !BlockGeometry.OpaqueCube(w.Block(bx, by, bz));
            if (sky >= 0) return false;
            for (int y = by; y < Math.Min(h, by + 48); y++) if (w.Block(bx, y, bz) != 0) return false;
            return true;
        }
        double fall = s.PrecipTick % 20 / 20.0;
        for (int i = 0; i < n; i++)
        {
            int px = (int)(R01() * W), py = (int)(R01() * H);
            double far = Math.Min(_depth[py * W + px], 36.0);
            if (far < 1.5) continue;
            double d = 1.2 + R01() * (far - 1.2);
            var (x, y, z) = Unproject(s, px + 0.5, py + 0.5, d);
            if (cam.Ortho) continue;
            int bx = (int)Math.Floor(x), by = (int)Math.Floor(y), bz = (int)Math.Floor(z);
            if (!Open(bx, by, bz)) continue;
            int biome = w.Biome(bx, bz);
            var kind = Weather.At(biome, y);
            if (kind == MineClaw.Render.Precip.None) continue;
            if (kind == MineClaw.Render.Precip.Snow)
            {
                if (ProjectF(s, x, y - fall * 0.4, z) is not { } p) continue;
                int fx = (int)p.X, fy = (int)p.Y;
                float k = (float)Math.Clamp(1.0 - p.Dist / 40.0, 0.25, 0.9);
                for (int oy = 0; oy < 2; oy++)
                    for (int ox = 0; ox < 2; ox++)
                    {
                        int qx = fx + ox, qy = fy + oy;
                        if ((uint)qx >= (uint)W || (uint)qy >= (uint)H || _depth[qy * W + qx] < p.Dist) continue;
                        _px[qy * W + qx] = IsoPalette.Mix(_px[qy * W + qx], 0xf4f8ff, k * (ox + oy == 2 ? 0.5f : 1f));
                    }
                continue;
            }
            // Rain: a streak ~0.9 blocks long, slanting a little, fainter with distance.
            double y0 = y + (0.45 - fall) * 0.9;
            if (ProjectF(s, x, y0, z) is not { } a || ProjectF(s, x + 0.08, y0 - 0.9, z + 0.05) is not { } b) continue;
            float alpha = (float)Math.Clamp(0.55 - a.Dist / 70.0, 0.18, 0.5) * (0.7f + 0.3f * wx.R);
            int steps = (int)Math.Clamp(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)), 1, 40);
            for (int k = 0; k <= steps; k++)
            {
                double f = k / (double)steps;
                int qx = (int)(a.X + (b.X - a.X) * f), qy = (int)(a.Y + (b.Y - a.Y) * f);
                if ((uint)qx >= (uint)W || (uint)qy >= (uint)H) continue;
                double dist = a.Dist + (b.Dist - a.Dist) * f;
                if (_depth[qy * W + qx] < dist) continue;
                _px[qy * W + qx] = IsoPalette.Mix(_px[qy * W + qx], 0xb4c4e0, alpha);
            }
        }
    }

    /// <summary>Front-to-back compositing of blended layers (water, ice, portal, slime) over what's behind them.</summary>
    private struct Blend
    {
        public float R, G, B, T;
        public static Blend New() => new() { T = 1f };
        public void Add(uint c, float a)
        {
            float k = T * a;
            R += k * ((c >> 16) & 0xff); G += k * ((c >> 8) & 0xff); B += k * (c & 0xff);
            T *= 1 - a;
        }
        public readonly uint Over(uint c)
        {
            if (T >= 0.999f) return c & 0xffffff;
            uint r = (uint)Math.Min(255, R + T * ((c >> 16) & 0xff)), g = (uint)Math.Min(255, G + T * ((c >> 8) & 0xff)), b = (uint)Math.Min(255, B + T * (c & 0xff));
            return r << 16 | g << 8 | b;
        }
    }

    /// <summary>Partial block heights for the crosshair pick (the render shapes come from <see cref="BlockGeometry"/>).</summary>
    private static bool Translucent(int id) => id is Blocks.Water or Blocks.FlowingWater or Blocks.Ice or Blocks.Portal;

    /// <summary>A ray: origin and unit direction (the float direction for the quad intersections).</summary>
    private readonly struct Ray(double ox, double oy, double oz, double dx, double dy, double dz)
    {
        public readonly double Ox = ox, Oy = oy, Oz = oz, Dx = dx, Dy = dy, Dz = dz;
        public readonly Vector3 DirF = new((float)dx, (float)dy, (float)dz);
    }

    /// <summary>Amanatides–Woo voxel walk: the current cell, the axis it was entered through and the next boundary per axis.</summary>
    internal struct Dda
    {
        public int Vx, Vy, Vz, LastAxis;
        public readonly int StepX, StepY, StepZ;
        public readonly double Tdx, Tdy, Tdz;
        public double Tmx, Tmy, Tmz;

        public Dda(double ox, double oy, double oz, double dx, double dy, double dz)
        {
            Vx = (int)Math.Floor(ox); Vy = (int)Math.Floor(oy); Vz = (int)Math.Floor(oz);
            LastAxis = -1;
            StepX = Step(dx); StepY = Step(dy); StepZ = Step(dz);
            Tdx = Delta(dx); Tdy = Delta(dy); Tdz = Delta(dz);
            Tmx = First(ox, Vx, dx, Tdx); Tmy = First(oy, Vy, dy, Tdy); Tmz = First(oz, Vz, dz, Tdz);
        }

        private static int Step(double d) => d > 0 ? 1 : -1;
        private static double Delta(double d) => d == 0 ? double.MaxValue : Math.Abs(1 / d);
        private static double First(double o, int v, double d, double td) => d == 0 ? double.MaxValue : (d > 0 ? v + 1 - o : o - v) * td;

        /// <summary>Distance along the ray where it leaves the current cell.</summary>
        public readonly double Exit => Math.Min(Tmx, Math.Min(Tmy, Tmz));

        /// <summary>The face of the current cell the ray came in through.</summary>
        public readonly Face EntryFace => LastAxis switch
        {
            0 => StepX > 0 ? Face.West : Face.East,
            1 => StepY > 0 ? Face.Down : Face.Up,
            _ => StepZ > 0 ? Face.North : Face.South,
        };

        /// <summary>Steps into the next cell.</summary>
        public void Advance()
        {
            if (Tmx < Tmy && Tmx < Tmz) { Vx += StepX; Tmx += Tdx; LastAxis = 0; }
            else if (Tmy < Tmz) { Vy += StepY; Tmy += Tdy; LastAxis = 1; }
            else { Vz += StepZ; Tmz += Tdz; LastAxis = 2; }
        }
    }

    /// <summary>What one pixel's ray found: colour, depth and the cell it shows.</summary>
    private readonly record struct Hit(uint Colour, float Depth, long Cell);

    private (uint Colour, float Depth, long Cell) Trace(Ctx ctx, double ox, double oy, double oz, double dx, double dy, double dz, BandCache cache)
    {
        var ray = new Ray(ox, oy, oz, dx, dy, dz);
        var dda = new Dda(ox, oy, oz, dx, dy, dz);
        var (eT, eC) = NearestEntity(ctx, ray);
        var w = ctx.S.World;
        var blend = Blend.New();
        double t0 = 0;
        int prevId = w.Block(dda.Vx, dda.Vy, dda.Vz);
        int maxDist = ctx.MaxDist;
        int maxSteps = maxDist * 3 + 3, height = w.WorldHeight;
        Span<(float T, int Q)> hits = stackalloc (float, int)[24];
        bool cutBefore = false; // the previous cell was solid and x-rayed away: the face it exposes is a cut face
        for (int step = 0; step < maxSteps && t0 <= maxDist; step++)
        {
            if (eT <= t0) return (Finish(ctx, blend, eC, eT, dy), (float)eT, NoCell);
            if (LeftWorld(dda.Vy, dy, height)) break;
            int id = (uint)dda.Vy < height ? w.Block(dda.Vx, dda.Vy, dda.Vz) : 0;
            double tExit = dda.Exit;
            bool cutEdge = cutBefore;
            id = XRayed(ctx.S, dda, id, step, out cutBefore);
            if (Visible(ctx.S, dda, id, step)
                && HitCell(ctx, ray, dda, id, prevId, t0, tExit, cutEdge, eT, ref blend, cache, hits, out var hit))
                return (hit.Colour, hit.Depth, hit.Cell);
            prevId = id;
            t0 = tExit;
            dda.Advance();
        }
        if (eT < double.MaxValue) return (Finish(ctx, blend, eC, eT, dy), (float)eT, NoCell);
        return (blend.Over(Sky(ctx, dy)), float.PositiveInfinity, NoCell);
    }

    /// <summary>Nearest opaque entity texel along the ray: its distance (MaxValue = none) and lit colour.</summary>
    private static (double T, uint C) NearestEntity(Ctx ctx, in Ray r)
    {
        double eT = double.MaxValue;
        uint eC = 0;
        foreach (var m in ctx.Ents)
        {
            if (!RayBox(r.Ox, r.Oy, r.Oz, r.Dx, r.Dy, r.Dz, m.X0, m.Y0, m.Z0, m.X1, m.Y1, m.Z1, out double tb, out _) || tb > eT) continue;
            NearestEntityQuad(ctx.S, m, r, ref eT, ref eC);
        }
        return (eT, eC);
    }

    private static void NearestEntityQuad(EyeScene s, EMesh m, in Ray r, ref double eT, ref uint eC)
    {
        var o = new Vector3((float)(r.Ox - m.Ox), (float)(r.Oy - m.Oy), (float)(r.Oz - m.Oz));
        for (int i = 0; i < m.Quads.Length; i++)
        {
            ref var q = ref m.Quads[i];
            if (!QuadHit.Intersect(q, o, r.DirF, 0.01f, out float t, out var uv) || t >= eT) continue;
            uint tex = Sample(s, q, uv);
            float a = q.Alpha > 0 ? q.Alpha : (tex >> 24) / 255f;
            if (a < 0.5f) continue;
            eT = t;
            eC = Lighting.Scale(tex, Math.Min(1f, m.Light * q.Shade));
        }
    }

    /// <summary>The block id the ray sees in the current cell: 0 when the x-ray cut removes it (then <paramref name="cut"/> says
    /// whether it was solid, so the next face is a cut face).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int XRayed(EyeScene s, in Dda d, int id, int step, out bool cut)
    {
        cut = false;
        if (id == 0 || s.XRay is not { } xray || step == 0 || !xray.Hides(d.Vx, d.Vy, d.Vz)) return id;
        cut = BlockGeometry.OpaqueCube(id) || id is Blocks.Leaves or 161;
        return 0;
    }

    /// <summary>The ray left the world through its bottom or top.</summary>
    private static bool LeftWorld(int vy, double dy, int height) => vy < 0 && dy < 0 || vy >= height && dy > 0;

    /// <summary>A block in a cell past the eye's own that the view draws.</summary>
    private static bool Visible(EyeScene s, in Dda d, int id, int step) => id != 0 && step > 0 && Drawn(s, d.Vx, d.Vy, d.Vz);

    /// <summary>The cell is drawn: not under a cut-away roof, inside the clip box.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Drawn(EyeScene s, int x, int y, int z) =>
        !(s.Cut is { } cut && cut.Hidden(x, y, z)) && !(s.Clip is { } clip && !clip.Contains(x, y, z));

    /// <summary>The ray in a non-air cell: a full cube's entry face or the block's quads. True = the pixel is decided.</summary>
    private bool HitCell(Ctx ctx, in Ray r, in Dda d, int id, int prevId, double t0, double tExit, bool cutEdge, double eT, ref Blend blend,
        BandCache cache, Span<(float T, int Q)> hits, out Hit hit)
    {
        var models = ctx.S.Atlas?.Models;
        var shape = BlockMesh.Shape(models, id, ctx.S.World.Meta(d.Vx, d.Vy, d.Vz));
        if (BlockMesh.IsCube(shape)) return CubeHit(ctx, r, d, id, shape, models, prevId, t0, cutEdge, eT, ref blend, cache, out hit);
        return QuadsHit(ctx, r, d, models, t0, tExit, cutEdge, eT, ref blend, cache, hits, out hit);
    }

    private bool CubeHit(Ctx ctx, in Ray r, in Dda d, int id, BlockShape shape, IBlockModels? models, int prevId, double t0, bool cutEdge, double eT,
        ref Blend blend, BandCache cache, out Hit hit)
    {
        hit = default;
        var s = ctx.S;
        var w = s.World;
        // Glass beside glass and ice beside ice draw no face between them (fancy leaves do).
        if (CulledAgainstSame(shape, id, prevId, models)) return false;
        int meta = w.Meta(d.Vx, d.Vy, d.Vz);
        Face face = d.EntryFace;
        double hx = r.Ox + r.Dx * t0, hy = r.Oy + r.Dy * t0, hz = r.Oz + r.Dz * t0;
        var (u, v) = Uv(face, hx - d.Vx, hy - d.Vy, hz - d.Vz);
        uint texel = CubeTexel(s, models, d, id, meta, face, hx, hy, hz, u, v);
        float a = shape == BlockShape.TranslucentCube ? 0.62f : (texel >> 24) / 255f;
        if (!(shape == BlockShape.Cube || a >= 0.5f)) return false;
        float k = ModelEmissive(models, id) ? 1f : CubeLight(s, w, id, d.Vx, d.Vy, d.Vz, face, u, v, ctx.Sub, cache, ctx.MinB);
        uint c = Lighting.Scale(texel, k);
        if (face == Face.Up) c = ShadowAt(ctx, c, hx, hy, hz);
        if (cutEdge) c = Hatch(c, hx, hy, hz);
        if (shape == BlockShape.TranslucentCube) { blend.Add(c, a); return false; }
        if (!(eT > t0)) return false;
        hit = new Hit(Finish(ctx, blend, Fog(ctx, c, t0), t0, r.Dy), (float)t0, CellKey(d.Vx, d.Vy, d.Vz));
        return true;
    }

    /// <summary>Glass beside glass and ice beside ice draw no face between them (fancy leaves do; 1.12.2's dark oak leaves too).</summary>
    private static bool CulledAgainstSame(BlockShape shape, int id, int prevId, IBlockModels? models) =>
        shape != BlockShape.Cube && id != Blocks.Leaves && prevId == id && !(models != null && id == 161);

    private static bool ModelEmissive(IBlockModels? models, int id) => models != null && models.Emissive(id);

    /// <summary>Texel (0xAARRGGBB) of a full cube's face at the hit: the 1.12.2 model, the b1.8.1 tile, or the flat colour.</summary>
    private static uint CubeTexel(EyeScene s, IBlockModels? models, in Dda d, int id, int meta, Face face, double hx, double hy, double hz, double u, double v)
    {
        var w = s.World;
        if (models != null)
            return models.CubeTexel(w, d.Vx, d.Vy, d.Vz, id, meta, face, new Vector3((float)(hx - d.Vx), (float)(hy - d.Vy), (float)(hz - d.Vz)));
        if (s.Atlas is not { } atlas) return 0xff000000 | BlockGeometry.FlatColour(id, face);
        var tr = BlockGeometry.CubeTile(w, d.Vx, d.Vy, d.Vz, id, meta, face);
        return BlockTextures.Texel(atlas, tr, id, (float)u, (float)v) ?? (0xff000000 | BlockGeometry.FlatColour(id, face));
    }

    private bool QuadsHit(Ctx ctx, in Ray r, in Dda d, IBlockModels? models, double t0, double tExit, bool cutEdge, double eT, ref Blend blend,
        BandCache cache, Span<(float T, int Q)> hits, out Hit hit)
    {
        hit = default;
        var quads = Shape(models, ctx.S.World, d.Vx, d.Vy, d.Vz, cache);
        if (quads.Length == 0) return false;
        var o = new Vector3((float)(r.Ox - d.Vx), (float)(r.Oy - d.Vy), (float)(r.Oz - d.Vz));
        int n = SortedHits(quads, o, r.DirF, t0, tExit, hits);
        for (int h = 0; h < n; h++)
        {
            var (t, qi) = hits[h];
            if (t >= eT) break;
            ref var q = ref quads[qi];
            if (QuadColour(ctx, r, d, q, o, t, cutEdge, cache, out float a) is not { } c) continue;
            if (q.Pass != RenderPass.Translucent)
            {
                hit = new Hit(Finish(ctx, blend, Fog(ctx, c, t), t, r.Dy), t, CellKey(d.Vx, d.Vy, d.Vz));
                return true;
            }
            blend.Add(Fog(ctx, c, t), Math.Clamp(a, 0.3f, 0.95f));
            if (blend.T >= 0.04f) continue;
            hit = new Hit(blend.Over(0), t, CellKey(d.Vx, d.Vy, d.Vz));
            return true;
        }
        return false;
    }

    /// <summary>The quads the ray crosses inside the cell, nearest first (insertion sort into <paramref name="hits"/>).</summary>
    private static int SortedHits(GeoQuad[] quads, Vector3 o, Vector3 dir, double t0, double tExit, Span<(float T, int Q)> hits)
    {
        int n = 0;
        for (int i = 0; i < quads.Length && n < hits.Length; i++)
            if (QuadHit.Intersect(quads[i], o, dir, 0.0005f, out float t, out _) && t >= t0 - 1e-3 && t <= tExit + 1e-3)
            {
                int j = n++;
                while (j > 0 && hits[j - 1].T > t) { hits[j] = hits[j - 1]; j--; }
                hits[j] = (t, i);
            }
        return n;
    }

    /// <summary>The lit colour of a quad at the ray's hit, or null where an alpha-tested texel lets the ray through.</summary>
    private static uint? QuadColour(Ctx ctx, in Ray r, in Dda d, in GeoQuad q, Vector3 o, float t, bool cutEdge, BandCache cache, out float a)
    {
        var s = ctx.S;
        QuadHit.Intersect(q, o, r.DirF, t - 1e-4f, out _, out var uv);
        uint tex = Sample(s, q, uv);
        a = q.Alpha > 0 ? q.Alpha : (tex >> 24) / 255f;
        if (q.Pass != RenderPass.Translucent && a < 0.5f) return null;
        float k = q.Emissive ? 1f : QuadLight(s, s.World, q, d.Vx, d.Vy, d.Vz, ctx.Sub, cache, ctx.MinB);
        uint c = Lighting.Scale(tex, k);
        double hx = r.Ox + r.Dx * t, hy = r.Oy + r.Dy * t, hz = r.Oz + r.Dz * t;
        if (q.Normal.Y > 0.7f) c = ShadowAt(ctx, c, hx, hy, hz);
        if (cutEdge) c = Hatch(c, hx, hy, hz);
        return c;
    }

    private static uint Finish(Ctx ctx, Blend b, uint c, double depth, double dy) => b.Over(c);

    /// <summary>Colour of the x-ray cut's section faces (a cool slate tint; the stripes are darker).</summary>
    public const uint CutTint = 0x7f8fb8;

    /// <summary>A face exposed by the x-ray cut: tinted toward <see cref="CutTint"/> with diagonal world-space stripes (an
    /// architect's section hatch), so the cut edge reads as "removed here", never as a wall that is really there.</summary>
    private static uint Hatch(uint c, double x, double y, double z)
    {
        double f = (x + y + z) * 2.5;
        bool stripe = f - Math.Floor(f) < 0.18;
        return stripe ? IsoPalette.Mix(c, 0x262c3c, 0.4f) : IsoPalette.Mix(c, CutTint, 0.22f);
    }

    private static GeoQuad[] Shape(IBlockModels? models, IVoxelWorld w, int x, int y, int z, BandCache cache)
    {
        long key = CellKey(x, y, z);
        int slot = (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15)) >> 53);
        if (cache.ShapeKeys[slot] == key && cache.Shapes[slot] is { } hit) return hit;
        cache.Tmp.Clear();
        BlockMesh.Quads(models, w, x, y, z, cache.Tmp);
        var arr = cache.Tmp.ToArray();
        cache.ShapeKeys[slot] = key;
        cache.Shapes[slot] = arr;
        return arr;
    }

    /// <summary>Texel (0xAARRGGBB) of a quad at its UV: terrain (tint + overlay), procedural liquids, entity skins, items.</summary>
    internal static uint Sample(EyeScene s, in GeoQuad q, Vector2 uv) => SampleQuad(s.Atlas, q, uv);

    /// <summary>Texel (0xAARRGGBB) of a quad at its UV with the given textures (the raycaster's sampling; parity tests).</summary>
    public static uint SampleQuad(TextureAtlas? atlas0, in GeoQuad q, Vector2 uv) => q.Tex switch
    {
        QuadTex.WaterStill => FluidTextures.Sample(FluidTextures.WaterStill, uv.X, uv.Y),
        QuadTex.WaterFlow => FluidTextures.Sample(FluidTextures.WaterFlow, uv.X, uv.Y),
        QuadTex.LavaStill => FluidTextures.Sample(FluidTextures.LavaStill, uv.X, uv.Y),
        QuadTex.LavaFlow => FluidTextures.Sample(FluidTextures.LavaFlow, uv.X, uv.Y),
        QuadTex.Fire => FluidTextures.Sample(q.Tile == 1 ? FluidTextures.FireB : FluidTextures.FireA, Math.Clamp(uv.X, 0, 0.999f), Math.Clamp(uv.Y, 0, 0.999f)),
        QuadTex.Entity => EntityTexel(atlas0, q, uv),
        QuadTex.Item => atlas0?.SampleItem(q.Tile, Math.Clamp(uv.X, 0, 0.999f), Math.Clamp(uv.Y, 0, 0.999f)) ?? 0xff000000 | q.Flat,
        QuadTex.Flat => 0xff000000 | q.Flat,
        QuadTex.Block => atlas0?.Models is { } models ? models.SampleQuad(q, uv) : 0xff000000 | q.Flat,
        _ => TerrainTexel(atlas0, q, uv),
    };

    private static uint EntityTexel(TextureAtlas? atlas, in GeoQuad q, Vector2 uv)
    {
        uint t = EntityTextures.Sample(atlas, q.Tile, uv.X, uv.Y);
        return q.Tint != 0 ? BlockTextures.Tinted(t, q.Tint) : t;
    }

    /// <summary>A terrain.png tile texel (tint + overlay), or the flat colour without a tile or an atlas.</summary>
    private static uint TerrainTexel(TextureAtlas? atlas0, in GeoQuad q, Vector2 uv)
    {
        if (q.Tile < 0 || atlas0 is not { } atlas) return 0xff000000u | q.Flat;
        float u = Math.Clamp(uv.X, 0, 0.999f), v = Math.Clamp(uv.Y, 0, 0.999f);
        uint p = BlockTextures.Tinted(atlas.Sample(q.Tile, u, v), q.Tint);
        if (q.Overlay < 0) return p;
        uint ov = atlas.Sample(q.Overlay, u, v);
        return ov >> 24 >= 128 ? BlockTextures.Tinted(ov, q.OverlayTint) | 0xff000000 : p;
    }

    private static (double U, double V) Uv(Face f, double fx, double fy, double fz) => FaceLight.CubeFaceUv(f, fx, fy, fz);

    /// <summary>Darkens a ground point inside an entity's shadow (Render.renderShadow: a soft disc, fainter with height).</summary>
    private static uint ShadowAt(Ctx ctx, uint c, double hx, double hy, double hz)
    {
        foreach (var sh in ctx.Shadows)
        {
            double ddx = hx - sh.X, ddz = hz - sh.Z, d2 = ddx * ddx + ddz * ddz;
            if (d2 >= sh.R * sh.R) continue;
            double above = sh.Y - hy;
            if (above < -0.3 || above > 2) continue;
            float a = (float)((1 - Math.Clamp(above, 0, 2) / 2) * 0.5 * sh.Strength * (1 - d2 / (sh.R * sh.R)));
            c = Lighting.Scale(c, 1 - a);
        }
        return c;
    }

    private float CubeLight(EyeScene s, IVoxelWorld w, int id, int x, int y, int z, Face f, double u, double v, int sub, BandCache cache, float minB)
    {
        if (BlockTextures.Emissive(id)) return 1f;
        float remembered = w.State(x, z) == ColumnState.Remembered ? 0.8f : 1f;
        long key = ((long)(x & 0xfffff) << 40) | ((long)(z & 0xfffff) << 20) | ((long)(y & 0xff) << 4) | (long)f;
        int slot = (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15)) >> 52);
        if (cache.Keys[slot] != key)
        {
            cache.Keys[slot] = key;
            FaceLight.CubeCorners(w, id, x, y, z, f, sub, s.Nether, s.Smooth, minB, cache.Vals.AsSpan(4 * slot, 4));
        }
        var c = cache.Vals.AsSpan(4 * slot, 4);
        if (!s.Smooth) return remembered < 1 ? Math.Max(c[0], 0.45f) * remembered : c[0];
        float fu = (float)u, fv = (float)v;
        float r = (c[0] * (1 - fu) + c[1] * fu) * (1 - fv) + (c[2] * (1 - fu) + c[3] * fu) * fv;
        return remembered < 1 ? Math.Max(r, 0.45f) * remembered : r;
    }

    /// <summary>Light of a shape quad: the neighbouring cell its face looks into, or the block's own cell (max with the one above,
    /// so slabs and plants aren't dark from stale light), × the quad's shade.</summary>
    private static float QuadLight(EyeScene s, IVoxelWorld w, in GeoQuad q, int x, int y, int z, int sub, BandCache cache, float minB)
    {
        int f = q.LightFace;
        long key = ((long)(x & 0xfffff) << 40) | ((long)(z & 0xfffff) << 20) | ((long)(y & 0xff) << 4) | (long)(f < 0 ? 7 : f + 8);
        int slot = (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15)) >> 52);
        float lvlK;
        if (cache.Keys[slot] == key) lvlK = cache.Vals[4 * slot];
        else
        {
            lvlK = FaceLight.QuadLevel(w, f, x, y, z, sub, s.Nether);
            if (w.State(x, z) == ColumnState.Remembered) lvlK = Math.Max(lvlK, 0.45f) * 0.8f;
            cache.Keys[slot] = key;
            cache.Vals[4 * slot] = lvlK;
        }
        return Math.Max(minB, lvlK * q.Shade);
    }

    /// <summary>The sky's zenith, horizon (also the fog) and void colours at a sky-light subtraction (World.k; 0 noon, 11
    /// midnight): every renderer's day/night sky (the GPU freecam's too).</summary>
    public static (uint Top, uint Horizon, uint Void) SkyColours(int sub, bool nether) => nether
        ? (0x3a0e0au, 0x5a1a10u, 0x120404u)
        : (IsoPalette.Scale(0x78a7ff, 1f - sub / 13f), IsoPalette.Scale(0xc3d9ff, 1f - sub / 12.5f), 0x05070cu);

    /// <summary><see cref="SkyColours(int, bool)"/> under weather (pass the weather's <see cref="Lighting.SkylightSubtracted(long, float, float)"/>):
    /// the zenith greyed like getSkyColorBody, the horizon/fog like updateFogColor. Clear weather = the plain colours.</summary>
    public static (uint Top, uint Horizon, uint Void) SkyColours(int sub, bool nether, Weather weather)
    {
        var c = SkyColours(sub, nether);
        if (nether || !weather.Wet) return c;
        return (weather.Sky(c.Top), weather.Fog(weather.Sky(c.Horizon)), c.Void);
    }

    private static uint Sky(Ctx ctx, double dy)
    {
        var (top, horizon, _) = ctx.Sky;
        if (dy >= 0) return IsoPalette.Mix(horizon, top, (float)Math.Min(1, dy * 1.6));
        // Below the horizon: the void, darkening downward.
        return IsoPalette.Mix(IsoPalette.Scale(horizon, 0.55f), ctx.S.Nether ? 0x120404u : 0x05070cu, (float)Math.Min(1, -dy * 2.2));
    }

    /// <summary>Linear distance fog toward the horizon colour (from 60 % of the view distance to 85 % opacity at the end).</summary>
    private static uint Fog(Ctx ctx, uint c, double dist)
    {
        double start = ctx.MaxDist * 0.6;
        if (dist <= start) return c;
        float a = (float)Math.Min(0.85, (dist - start) / (ctx.MaxDist - start));
        return IsoPalette.Mix(c, ctx.Sky.Horizon, a);
    }

    /// <summary>Slab-method ray/AABB intersection; t = entry distance (0 when inside), axis = entry axis (0 x, 1 y, 2 z).</summary>
    private static bool RayBox(double ox, double oy, double oz, double dx, double dy, double dz, double x0, double y0, double z0,
        double x1, double y1, double z1, out double t, out int axis)
    {
        double tmin = 0, tmax = double.MaxValue;
        axis = -1;
        t = 0;
        if (!RaySlab(ox, dx, x0, x1, 0, ref tmin, ref tmax, ref axis)) return false;
        if (!RaySlab(oy, dy, y0, y1, 1, ref tmin, ref tmax, ref axis)) return false;
        if (!RaySlab(oz, dz, z0, z1, 2, ref tmin, ref tmax, ref axis)) return false;
        t = tmin;
        if (axis < 0) axis = 1;
        return true;
    }

    /// <summary>One axis of the slab test: narrows [tmin, tmax] (the entry axis when tmin grows); false = the ray misses.</summary>
    private static bool RaySlab(double o, double d, double lo, double hi, int a, ref double tmin, ref double tmax, ref int axis)
    {
        if (Math.Abs(d) < 1e-12) return !(o < lo || o > hi);
        double t1 = (lo - o) / d, t2 = (hi - o) / d;
        if (t1 > t2) (t1, t2) = (t2, t1);
        if (t1 > tmin) { tmin = t1; axis = a; }
        if (t2 < tmax) tmax = t2;
        return !(tmin > tmax);
    }

    // ------------------------------------------------------------------ overlays

    /// <summary>A 3D segment drawn with a depth test: solid where visible, dotted where something is in front.</summary>
    public void Line3D(Canvas c, Frame fr, EyeScene s, (double X, double Y, double Z) a, (double X, double Y, double Z) b, uint col, bool hiddenDots = true, float alpha = 1f)
    {
        double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) + (b.Z - a.Z) * (b.Z - a.Z));
        var pa = ProjectF(s, a.X, a.Y, a.Z);
        var pb = ProjectF(s, b.X, b.Y, b.Z);
        if (pa == null || pb == null) return;
        double pxLen = Math.Max(Math.Abs(pa.Value.X - pb.Value.X), Math.Abs(pa.Value.Y - pb.Value.Y));
        int n = Math.Max(2, (int)(pxLen * 1.5) + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = i / (double)n;
            double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t, z = a.Z + (b.Z - a.Z) * t;
            if (ProjectF(s, x, y, z) is not { } p) continue;
            int ix = (int)p.X, iy = (int)p.Y;
            if ((uint)ix >= (uint)fr.Width || (uint)iy >= (uint)fr.Height) continue;
            bool visible = fr.Depth[iy * fr.Width + ix] >= p.Dist - 0.08;
            if (visible) { if (alpha >= 0.999f) c.Set(ix, iy, col); else c.Blend(ix, iy, col, alpha); }
            else if (hiddenDots && i % 3 == 0) c.Blend(ix, iy, col, 0.6f);
        }
        _ = len;
    }

    /// <summary>The 12 edges of a block, depth-tested.</summary>
    public void BlockBox(Canvas c, Frame fr, EyeScene s, (int X, int Y, int Z) p, uint col, float inset = -0.02f)
    {
        double x0 = p.X + inset, y0 = p.Y + inset, z0 = p.Z + inset;
        double x1 = p.X + 1 - inset, y1 = p.Y + 1 - inset, z1 = p.Z + 1 - inset;
        var v = new (double, double, double)[8];
        for (int i = 0; i < 8; i++) v[i] = ((i & 1) == 0 ? x0 : x1, ((i >> 1) & 1) == 0 ? y0 : y1, ((i >> 2) & 1) == 0 ? z0 : z1);
        int[][] edges = [[0, 1], [2, 3], [4, 5], [6, 7], [0, 2], [1, 3], [4, 6], [5, 7], [0, 4], [1, 5], [2, 6], [3, 7]];
        foreach (var e in edges) Line3D(c, fr, s, v[e[0]], v[e[1]], col);
    }

    /// <summary>Name tags over players/bots and labels over marks (2D chips at their projected anchors, kept apart).</summary>
    internal List<string> Tags(Canvas c, Frame fr, EyeScene s, VisionRenderer.Placer placer, bool marks = true, bool drops = false)
    {
        var labels = new List<string>();
        var tags = new Tagger(this, c, fr, s, placer, MaxDist(s));
        foreach (var e in s.Entities.Where(e => e.Kind is "player" or "hive" or "self")) tags.Name(e);
        if (drops)
            foreach (var e in s.Entities.Where(e => e.Kind == "item")) tags.Drop(e);
        if (marks)
            foreach (var m in s.Marks)
                if (tags.Mark(m) is { } label) labels.Add(label);
        return labels;
    }

    /// <summary>Name tags and labels of one frame as 2D chips at their projected anchors, kept apart by the placer.</summary>
    private sealed class Tagger(EyeRenderer r, Canvas c, Frame fr, EyeScene s, VisionRenderer.Placer placer, int maxDist)
    {
        private void Tag(int x, int y, string t, uint fg)
        {
            int w = PixelFont.Width(t) + 4;
            if (placer.FindNear(x - w / 2, y - 11, w, 11) is { } at) c.Chip(at.X, at.Y, t, fg);
        }

        private bool Visible(int x, int y, double dist) => fr.Depth[Math.Clamp(y, 0, fr.Height - 1) * fr.Width + Math.Clamp(x, 0, fr.Width - 1)] >= dist - 1.0;

        /// <summary>A player's or bot's name over its head (not the camera's own body).</summary>
        public void Name(VisionEntity e)
        {
            if (s.IsViewerBody(e)) return;
            if (r.Project(s, e.X, e.Y + (e.Sneaking ? 2.0 : 2.15), e.Z) is not { } p || p.Dist >= maxDist) return;
            var t = string.IsNullOrEmpty(e.Name) ? e.Kind : e.Name!.Length > 14 ? e.Name[..14] : e.Name;
            Tag(p.X, p.Y - 2, t, e.Kind is "hive" or "self" ? 0xff9f43 : VisionRenderer.PlayerColour(e.Name));
        }

        /// <summary>A visible drop's label, within 60 % of the view distance.</summary>
        public void Drop(VisionEntity e)
        {
            if (r.Project(s, e.X, e.Y + 0.45, e.Z) is { } p && p.Dist < maxDist * 0.6 && Visible(p.X, Math.Min(fr.Height - 1, p.Y + 3), p.Dist))
                Tag(p.X, p.Y, DropLabel(e), 0xf2e46a);
        }

        /// <summary>A visible mark's short label; returns "label x,y,z" when drawn.</summary>
        public string? Mark(VisionMark m)
        {
            if (r.Project(s, m.CentreX + 0.5, m.MaxY + 1.2, m.CentreZ + 0.5) is not { } p || p.Dist >= maxDist || !Visible(p.X, Math.Min(fr.Height - 1, p.Y + 3), p.Dist))
                return null;
            var t = VisionRenderer.ShortLabel(m.Text);
            if (t.Length == 0) return null;
            Tag(p.X, p.Y, t, m.Box ? 0xffd84au : 0xe8ecf4u);
            return $"{t} {m.MinX},{m.MinY},{m.MinZ}";
        }
    }

    /// <summary>"2 sapling" style label of a dropped item.</summary>
    public static string DropLabel(VisionEntity e)
    {
        var name = e.Name is { Length: > 0 } n && !n.StartsWith("item", StringComparison.OrdinalIgnoreCase) ? n : ItemName(e.ItemId);
        if (name.Length > 12) name = name[..12];
        return Math.Max(1, e.ItemCount) > 1 ? $"{e.ItemCount} {name}" : name;
    }

    public static string ItemName(int id) => id is > 0 and < 256 ? Blocks.Get(id).Name.Replace('_', ' ') : Items.Get(id).Name.Replace('_', ' ');

    /// <summary>A small compass (N arrow) for a perspective panel: where north points on screen from this camera.</summary>
    public void Compass(Canvas c, EyeScene s, int cx, int cy, int r = 9)
    {
        c.Disc(cx, cy, r + 1, 0x101318);
        // North = −z: its screen direction is the camera-relative angle of −z in the horizontal plane.
        double yaw = s.Yaw * Math.PI / 180;
        double fx = -Math.Sin(yaw), fz = Math.Cos(yaw);   // forward
        double rx = -fz, rz = fx;                          // right
        double nx = 0, nz = -1;
        double sx = nx * rx + nz * rz, sy = nx * fx + nz * fz; // north in (right, forward)
        double l = Math.Sqrt(sx * sx + sy * sy);
        if (l < 1e-6) { sx = 0; sy = 1; l = 1; }
        // Foreshortening with the pitch: looking down, "forward" is up on screen.
        int tx = cx + (int)Math.Round(sx / l * (r - 2)), ty = cy - (int)Math.Round(sy / l * (r - 2));
        c.Line(cx, cy, tx, ty, 0xff4a4a);
        c.Line(cx, cy, cx - (tx - cx) / 2, cy - (ty - cy) / 2, 0xe8ecf4);
        c.Text(tx - 2 + Math.Sign(tx - cx) * 3, ty - 3 + Math.Sign(ty - cy) * 4, "N", 0xff6a6a);
    }

    /// <summary>Raster + overlays (crosshair, outlines, name tags, labels) → PNG.</summary>
    public VisionImage Render(EyeScene s)
    {
        var fr = Raster(s);
        var c = new Canvas(fr.Width, fr.Height, (uint[])fr.Px.Clone());
        if (s.Target is { } tg) BlockBox(c, fr, s, tg, 0xffd84a);
        if (s.Pointed is { } pt) BlockBox(c, fr, s, pt, 0xff4fd8);
        var placer = new VisionRenderer.Placer(0, 0, fr.Width, fr.Height);
        var labels = Tags(c, fr, s, placer, drops: true);

        // Crosshair.
        int cx = fr.Width / 2, cy = fr.Height / 2;
        for (int i = -4; i <= 4; i++)
        {
            if (Math.Abs(i) < 1) continue;
            c.Set(cx + i, cy, 0xffffff); c.Set(cx, cy + i, 0xffffff);
        }

        // What the crosshair is on (precise, for "which block am I looking at").
        string onText = "";
        if (CrosshairBlock(s) is { } hit) onText = $" crosshair on {Blocks.Get(hit.Id).Name} {hit.X},{hit.Y},{hit.Z} ({hit.Dist:0.#}m).";
        string dir = Compass(s.Yaw);
        var legend = string.Create(CultureInfo.InvariantCulture,
            $"eye view{(s.Viewer != null ? " of " + s.Viewer : "")} from {s.EyeX:0.#},{s.EyeY:0.#},{s.EyeZ:0.#} facing {dir} (yaw {s.Yaw:0}, pitch {s.Pitch:0}), {Fov(s):0}° fov, {fr.Width}x{fr.Height}px ~{(fr.Width * fr.Height + 749) / 750}tok, {LastMs:0}ms.{onText} Gold=target, pink=pointed; dimmer=remembered.");
        return new VisionImage(Png.Encode(c.Px, c.Width, c.Height), c.Width, c.Height, 0, legend, labels) { Pixels = c.Px };
    }

    /// <summary>The block the centre ray hits (vanilla-style pick), or null.</summary>
    public (int X, int Y, int Z, int Id, double Dist)? CrosshairBlock(EyeScene s)
    {
        var cam = Camera(s);
        var d = new Dda(cam.Ox, cam.Oy, cam.Oz, cam.Fx, cam.Fy, cam.Fz);
        double t = 0;
        int maxDist = MaxDist(s), height = s.World.WorldHeight;
        for (int i = 0; i < maxDist * 3 && t <= maxDist; i++)
        {
            if (i > 0 && (uint)d.Vy < height && s.World.Block(d.Vx, d.Vy, d.Vz) is var id and not 0 && !Translucent(id))
                return (d.Vx, d.Vy, d.Vz, id, t);
            t = d.Exit;
            d.Advance();
        }
        return null;
    }

    private static readonly string[] Compass8 = ["S", "SW", "W", "NW", "N", "NE", "E", "SE"];
    public static string Compass(float yaw) => Compass8[(int)Math.Round((((yaw % 360) + 360) % 360) / 45.0) % 8];
}
