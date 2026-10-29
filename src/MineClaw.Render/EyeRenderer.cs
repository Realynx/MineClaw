using System.Globalization;
using System.Numerics;
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
    /// <summary>Darkest a lit face gets (null = the renderer's <see cref="EyeRenderer.MinBrightness"/>; maps use a higher floor).</summary>
    public float? MinBrightness { get; init; }
    /// <summary>Orthographic projection: half the view height in blocks (0 = perspective). TOP/SIDE/LAYERS maps use it.</summary>
    public double Ortho { get; init; }
    /// <summary>Only blocks inside this box are drawn (a side slice, one layer); null = everything.</summary>
    public ClipBox? Clip { get; init; }

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
        int sub = s.Nether ? 0 : Lighting.SkylightSubtracted(s.Time);
        var ents = PrepareEntities(s, sub);
        var shadows = Shadows(s, ents);
        var ctx = new Ctx(s, sub, ents, shadows, maxDist, MinB(s));
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
        LastMs = sw.Elapsed.TotalMilliseconds;
        return new Frame(_px, _depth, W, H, _cells);
    }

    private sealed record Ctx(EyeScene S, int Sub, EMesh[] Ents, Shadow[] Shadows, int MaxDist, float MinB);

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

    private (uint Colour, float Depth, long Cell) Trace(Ctx ctx, double ox, double oy, double oz, double dx, double dy, double dz, BandCache cache)
    {
        var s = ctx.S;
        int vx = (int)Math.Floor(ox), vy = (int)Math.Floor(oy), vz = (int)Math.Floor(oz);
        int stepX = dx > 0 ? 1 : -1, stepY = dy > 0 ? 1 : -1, stepZ = dz > 0 ? 1 : -1;
        double tdx = dx == 0 ? double.MaxValue : Math.Abs(1 / dx), tdy = dy == 0 ? double.MaxValue : Math.Abs(1 / dy), tdz = dz == 0 ? double.MaxValue : Math.Abs(1 / dz);
        double tmx = dx == 0 ? double.MaxValue : (dx > 0 ? vx + 1 - ox : ox - vx) * tdx;
        double tmy = dy == 0 ? double.MaxValue : (dy > 0 ? vy + 1 - oy : oy - vy) * tdy;
        double tmz = dz == 0 ? double.MaxValue : (dz > 0 ? vz + 1 - oz : oz - vz) * tdz;

        // Nearest opaque entity texel along the ray.
        double eT = double.MaxValue;
        uint eC = 0;
        var dirF = new Vector3((float)dx, (float)dy, (float)dz);
        foreach (var m in ctx.Ents)
        {
            if (!RayBox(ox, oy, oz, dx, dy, dz, m.X0, m.Y0, m.Z0, m.X1, m.Y1, m.Z1, out double tb, out _) || tb > eT) continue;
            var o = new Vector3((float)(ox - m.Ox), (float)(oy - m.Oy), (float)(oz - m.Oz));
            for (int i = 0; i < m.Quads.Length; i++)
            {
                ref var q = ref m.Quads[i];
                if (!QuadHit.Intersect(q, o, dirF, 0.01f, out float t, out var uv) || t >= eT) continue;
                uint tex = Sample(s, q, uv);
                float a = q.Alpha > 0 ? q.Alpha : (tex >> 24) / 255f;
                if (a < 0.5f) continue;
                eT = t;
                eC = Lighting.Scale(tex, Math.Min(1f, m.Light * q.Shade));
            }
        }

        var blend = Blend.New();
        double t0 = 0;
        int lastAxis = -1, prevId = s.World.Block(vx, vy, vz);
        var w = s.World;
        int maxDist = ctx.MaxDist;
        int maxSteps = maxDist * 3 + 3, height = w.WorldHeight;
        Span<(float T, int Q)> hits = stackalloc (float, int)[24];
        for (int step = 0; step < maxSteps && t0 <= maxDist; step++)
        {
            if (eT <= t0) return (Finish(ctx, blend, eC, eT, dy), (float)eT, NoCell);
            if (vy < 0 && dy < 0 || vy >= height && dy > 0) break;
            int id = (uint)vy < height ? w.Block(vx, vy, vz) : 0;
            double tExit = Math.Min(tmx, Math.Min(tmy, tmz));
            if (id != 0 && step > 0 && !(s.Cut is { } cut && cut.Hidden(vx, vy, vz)) && !(s.Clip is { } clip && !clip.Contains(vx, vy, vz)))
            {
                var models = s.Atlas?.Models;
                var shape = BlockMesh.Shape(models, id, w.Meta(vx, vy, vz));
                if (BlockMesh.IsCube(shape))
                {
                    // Glass beside glass and ice beside ice draw no face between them (fancy leaves do).
                    bool culled = shape != BlockShape.Cube && id != Blocks.Leaves && prevId == id && !(models != null && id == 161);
                    if (!culled)
                    {
                        int meta = w.Meta(vx, vy, vz);
                        Face face = lastAxis switch { 0 => stepX > 0 ? Face.West : Face.East, 1 => stepY > 0 ? Face.Down : Face.Up, _ => stepZ > 0 ? Face.North : Face.South };
                        double hx = ox + dx * t0, hy = oy + dy * t0, hz = oz + dz * t0;
                        var (u, v) = Uv(face, hx - vx, hy - vy, hz - vz);
                        uint texel;
                        if (models != null)
                            texel = models.CubeTexel(w, vx, vy, vz, id, meta, face, new Vector3((float)(hx - vx), (float)(hy - vy), (float)(hz - vz)));
                        else if (s.Atlas is { } atlas)
                        {
                            var tr = BlockGeometry.CubeTile(w, vx, vy, vz, id, meta, face);
                            texel = BlockTextures.Texel(atlas, tr, id, (float)u, (float)v) ?? (0xff000000 | BlockGeometry.FlatColour(id, face));
                        }
                        else texel = 0xff000000 | BlockGeometry.FlatColour(id, face);
                        float a = shape == BlockShape.TranslucentCube ? 0.62f : (texel >> 24) / 255f;
                        if (shape == BlockShape.Cube || a >= 0.5f)
                        {
                            float k = models != null && models.Emissive(id) ? 1f : CubeLight(s, w, id, vx, vy, vz, face, u, v, ctx.Sub, cache, ctx.MinB);
                            uint c = Lighting.Scale(texel, k);
                            if (face == Face.Up) c = ShadowAt(ctx, c, hx, hy, hz);
                            if (shape == BlockShape.TranslucentCube) blend.Add(c, a);
                            else if (eT > t0) return (Finish(ctx, blend, Fog(ctx, c, t0), t0, dy), (float)t0, CellKey(vx, vy, vz));
                        }
                    }
                }
                else
                {
                    var quads = Shape(models, w, vx, vy, vz, cache);
                    if (quads.Length > 0)
                    {
                        var o = new Vector3((float)(ox - vx), (float)(oy - vy), (float)(oz - vz));
                        int n = 0;
                        for (int i = 0; i < quads.Length && n < hits.Length; i++)
                            if (QuadHit.Intersect(quads[i], o, dirF, 0.0005f, out float t, out _) && t >= t0 - 1e-3 && t <= tExit + 1e-3)
                            {
                                int j = n++;
                                while (j > 0 && hits[j - 1].T > t) { hits[j] = hits[j - 1]; j--; }
                                hits[j] = (t, i);
                            }
                        for (int h = 0; h < n; h++)
                        {
                            var (t, qi) = hits[h];
                            if (t >= eT) break;
                            ref var q = ref quads[qi];
                            QuadHit.Intersect(q, o, dirF, t - 1e-4f, out _, out var uv);
                            uint tex = Sample(s, q, uv);
                            float a = q.Alpha > 0 ? q.Alpha : (tex >> 24) / 255f;
                            if (q.Pass != RenderPass.Translucent && a < 0.5f) continue;
                            float k = q.Emissive ? 1f : QuadLight(s, w, q, vx, vy, vz, ctx.Sub, cache, ctx.MinB);
                            uint c = Lighting.Scale(tex, k);
                            double hx = ox + dx * t, hy = oy + dy * t, hz = oz + dz * t;
                            if (q.Normal.Y > 0.7f) c = ShadowAt(ctx, c, hx, hy, hz);
                            if (q.Pass == RenderPass.Translucent) { blend.Add(Fog(ctx, c, t), Math.Clamp(a, 0.3f, 0.95f)); if (blend.T < 0.04f) return (blend.Over(0), t, CellKey(vx, vy, vz)); continue; }
                            return (Finish(ctx, blend, Fog(ctx, c, t), t, dy), t, CellKey(vx, vy, vz));
                        }
                    }
                }
            }
            prevId = id;
            // Advance.
            t0 = tExit;
            if (tmx < tmy && tmx < tmz) { vx += stepX; tmx += tdx; lastAxis = 0; }
            else if (tmy < tmz) { vy += stepY; tmy += tdy; lastAxis = 1; }
            else { vz += stepZ; tmz += tdz; lastAxis = 2; }
        }
        if (eT < double.MaxValue) return (Finish(ctx, blend, eC, eT, dy), (float)eT, NoCell);
        return (blend.Over(Sky(s, dy, ctx.Sub)), float.PositiveInfinity, NoCell);
    }

    private static uint Finish(Ctx ctx, Blend b, uint c, double depth, double dy) => b.Over(c);

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
    public static uint SampleQuad(TextureAtlas? atlas0, in GeoQuad q, Vector2 uv)
    {
        float u = uv.X, v = uv.Y;
        switch (q.Tex)
        {
            case QuadTex.WaterStill: return FluidTextures.Sample(FluidTextures.WaterStill, u, v);
            case QuadTex.WaterFlow: return FluidTextures.Sample(FluidTextures.WaterFlow, u, v);
            case QuadTex.LavaStill: return FluidTextures.Sample(FluidTextures.LavaStill, u, v);
            case QuadTex.LavaFlow: return FluidTextures.Sample(FluidTextures.LavaFlow, u, v);
            case QuadTex.Fire: return FluidTextures.Sample(q.Tile == 1 ? FluidTextures.FireB : FluidTextures.FireA, Math.Clamp(u, 0, 0.999f), Math.Clamp(v, 0, 0.999f));
            case QuadTex.Entity:
            {
                uint t = EntityTextures.Sample(atlas0, q.Tile, u, v);
                return q.Tint != 0 ? BlockTextures.Tinted(t, q.Tint) : t;
            }
            case QuadTex.Item:
                if (atlas0?.SampleItem(q.Tile, Math.Clamp(u, 0, 0.999f), Math.Clamp(v, 0, 0.999f)) is { } it) return it;
                return 0xff000000 | q.Flat;
            case QuadTex.Flat:
                return 0xff000000 | q.Flat;
            case QuadTex.Block:
                return atlas0?.Models is { } models ? models.SampleQuad(q, uv) : 0xff000000 | q.Flat;
            default:
                if (q.Tile < 0 || atlas0 is not { } atlas) return (q.Pass == RenderPass.Cutout && atlas0 == null ? 0xff000000u : 0xff000000u) | q.Flat;
                u = Math.Clamp(u, 0, 0.999f); v = Math.Clamp(v, 0, 0.999f);
                uint p = BlockTextures.Tinted(atlas.Sample(q.Tile, u, v), q.Tint);
                if (q.Overlay >= 0)
                {
                    uint ov = atlas.Sample(q.Overlay, u, v);
                    if (ov >> 24 >= 128) p = BlockTextures.Tinted(ov, q.OverlayTint) | 0xff000000;
                }
                return p;
        }
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

    private static uint SkyTop(EyeScene s, int sub) => SkyColours(sub, s.Nether).Top;
    private static uint SkyHorizon(EyeScene s, int sub) => SkyColours(sub, s.Nether).Horizon;

    /// <summary>The sky's zenith, horizon (also the fog) and void colours at a sky-light subtraction (World.k; 0 noon, 11
    /// midnight): every renderer's day/night sky (the GPU freecam's too).</summary>
    public static (uint Top, uint Horizon, uint Void) SkyColours(int sub, bool nether) => nether
        ? (0x3a0e0au, 0x5a1a10u, 0x120404u)
        : (IsoPalette.Scale(0x78a7ff, 1f - sub / 13f), IsoPalette.Scale(0xc3d9ff, 1f - sub / 12.5f), 0x05070cu);

    private static uint Sky(EyeScene s, double dy, int sub)
    {
        if (dy >= 0) return IsoPalette.Mix(SkyHorizon(s, sub), SkyTop(s, sub), (float)Math.Min(1, dy * 1.6));
        // Below the horizon: the void, darkening downward.
        return IsoPalette.Mix(IsoPalette.Scale(SkyHorizon(s, sub), 0.55f), s.Nether ? 0x120404u : 0x05070cu, (float)Math.Min(1, -dy * 2.2));
    }

    /// <summary>Linear distance fog toward the horizon colour (from 60 % of the view distance to 85 % opacity at the end).</summary>
    private static uint Fog(Ctx ctx, uint c, double dist)
    {
        double start = ctx.MaxDist * 0.6;
        if (dist <= start) return c;
        float a = (float)Math.Min(0.85, (dist - start) / (ctx.MaxDist - start));
        return IsoPalette.Mix(c, SkyHorizon(ctx.S, ctx.Sub), a);
    }

    /// <summary>Slab-method ray/AABB intersection; t = entry distance (0 when inside), axis = entry axis (0 x, 1 y, 2 z).</summary>
    private static bool RayBox(double ox, double oy, double oz, double dx, double dy, double dz, double x0, double y0, double z0,
        double x1, double y1, double z1, out double t, out int axis)
    {
        double tmin = 0, tmax = double.MaxValue;
        axis = -1;
        t = 0;
        for (int a = 0; a < 3; a++)
        {
            double o = a == 0 ? ox : a == 1 ? oy : oz, d = a == 0 ? dx : a == 1 ? dy : dz, lo = a == 0 ? x0 : a == 1 ? y0 : z0, hi = a == 0 ? x1 : a == 1 ? y1 : z1;
            if (Math.Abs(d) < 1e-12) { if (o < lo || o > hi) return false; continue; }
            double t1 = (lo - o) / d, t2 = (hi - o) / d;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tmin) { tmin = t1; axis = a; }
            if (t2 < tmax) tmax = t2;
            if (tmin > tmax) return false;
        }
        t = tmin;
        if (axis < 0) axis = 1;
        return true;
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
        int maxDist = MaxDist(s);
        void Tag(int x, int y, string t, uint fg)
        {
            int w = PixelFont.Width(t) + 4;
            if (placer.FindNear(x - w / 2, y - 11, w, 11) is { } at) c.Chip(at.X, at.Y, t, fg);
        }
        bool Visible(int x, int y, double dist) => fr.Depth[Math.Clamp(y, 0, fr.Height - 1) * fr.Width + Math.Clamp(x, 0, fr.Width - 1)] >= dist - 1.0;
        foreach (var e in s.Entities.Where(e => e.Kind is "player" or "hive" or "self"))
        {
            double hx = s.EyeX - e.X, hz = s.EyeZ - e.Z;
            if (hx * hx + hz * hz < 0.36 && s.EyeY - e.Y is > -0.2 and < 2.0) continue;
            if (Project(s, e.X, e.Y + (e.Sneaking ? 2.0 : 2.15), e.Z) is { } p && p.Dist < maxDist)
            {
                var t = string.IsNullOrEmpty(e.Name) ? e.Kind : e.Name!.Length > 14 ? e.Name[..14] : e.Name;
                Tag(p.X, p.Y - 2, t, e.Kind is "hive" or "self" ? 0xff9f43 : VisionRenderer.PlayerColour(e.Name));
            }
        }
        if (drops)
            foreach (var e in s.Entities.Where(e => e.Kind == "item"))
                if (Project(s, e.X, e.Y + 0.45, e.Z) is { } p && p.Dist < maxDist * 0.6 && Visible(p.X, Math.Min(fr.Height - 1, p.Y + 3), p.Dist))
                    Tag(p.X, p.Y, DropLabel(e), 0xf2e46a);
        if (marks)
            foreach (var m in s.Marks)
                if (Project(s, m.CentreX + 0.5, m.MaxY + 1.2, m.CentreZ + 0.5) is { } p && p.Dist < maxDist && Visible(p.X, Math.Min(fr.Height - 1, p.Y + 3), p.Dist))
                {
                    var t = VisionRenderer.ShortLabel(m.Text);
                    if (t.Length == 0) continue;
                    Tag(p.X, p.Y, t, m.Box ? 0xffd84au : 0xe8ecf4u);
                    labels.Add($"{t} {m.MinX},{m.MinY},{m.MinZ}");
                }
        return labels;
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
        double ox = cam.Ox, oy = cam.Oy, oz = cam.Oz, dx = cam.Fx, dy = cam.Fy, dz = cam.Fz;
        int vx = (int)Math.Floor(ox), vy = (int)Math.Floor(oy), vz = (int)Math.Floor(oz);
        int stepX = dx > 0 ? 1 : -1, stepY = dy > 0 ? 1 : -1, stepZ = dz > 0 ? 1 : -1;
        double tdx = dx == 0 ? double.MaxValue : Math.Abs(1 / dx), tdy = dy == 0 ? double.MaxValue : Math.Abs(1 / dy), tdz = dz == 0 ? double.MaxValue : Math.Abs(1 / dz);
        double tmx = dx == 0 ? double.MaxValue : (dx > 0 ? vx + 1 - ox : ox - vx) * tdx;
        double tmy = dy == 0 ? double.MaxValue : (dy > 0 ? vy + 1 - oy : oy - vy) * tdy;
        double tmz = dz == 0 ? double.MaxValue : (dz > 0 ? vz + 1 - oz : oz - vz) * tdz;
        double t = 0;
        int maxDist = MaxDist(s), height = s.World.WorldHeight;
        for (int i = 0; i < maxDist * 3 && t <= maxDist; i++)
        {
            if (i > 0 && (uint)vy < height && s.World.Block(vx, vy, vz) is var id and not 0 && !Translucent(id))
                return (vx, vy, vz, id, t);
            if (tmx < tmy && tmx < tmz) { vx += stepX; t = tmx; tmx += tdx; }
            else if (tmy < tmz) { vy += stepY; t = tmy; tmy += tdy; }
            else { vz += stepZ; t = tmz; tmz += tdz; }
        }
        return null;
    }

    private static readonly string[] Compass8 = ["S", "SW", "W", "NW", "N", "NE", "E", "SE"];
    public static string Compass(float yaw) => Compass8[(int)Math.Round((((yaw % 360) + 360) % 360) / 45.0) % 8];
}
