using System.Globalization;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>A first-person view to render: an eye position, yaw/pitch (Minecraft convention: yaw 0 = south, 90 = west;
/// pitch +90 = down), the world, textures (null = palette colours) and the overlays.</summary>
public sealed class EyeScene
{
    public required IVoxelWorld World { get; init; }
    public TextureAtlas? Atlas { get; init; }
    public double EyeX { get; init; }
    public double EyeY { get; init; }
    public double EyeZ { get; init; }
    public float Yaw { get; init; }
    public float Pitch { get; init; }
    /// <summary>World time (ticks) for sky light and sky colour.</summary>
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
}

/// <summary>
/// Software voxel raycaster for <c>see {view:"eye"}</c>: per-pixel DDA from the eye through the world (live chunks, the
/// remembered layer dimmer), atlas texels at the hit face's UV (alpha-tested for plants/torches/glass, partial heights for
/// slabs, snow, plates, rails), vanilla flat per-face light (or smooth lighting), a sky / void gradient, players as textured
/// boxes with name tags, a crosshair, structure labels and outlines of the target / pointed block. 320×240 ≈ 103 tokens.
/// Buffers are reused between frames; rows render in parallel bands, each with its own light cache. One instance per bot.
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
    private LightCache[] _caches = [];
    public double LastMs { get; private set; }

    private sealed class LightCache
    {
        public readonly long[] Keys = new long[4096];
        public readonly float[] Vals = new float[4096 * 4];
        public void Clear() => Array.Fill(Keys, long.MinValue);
    }

    public readonly record struct Frame(uint[] Px, float[] Depth, int Width, int Height);

    private readonly record struct Cam(double Ox, double Oy, double Oz, double Fx, double Fy, double Fz, double Rx, double Ry, double Rz,
        double Ux, double Uy, double Uz, double TanX, double TanY);

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
        double tanY = Math.Tan(FovDegrees * Math.PI / 360), tanX = tanY * Width / Height;
        return new Cam(s.EyeX, s.EyeY, s.EyeZ, fx, fy, fz, rx, ry, rz, ux, uy, uz, tanX, tanY);
    }

    /// <summary>Screen pixel of a world point, with its distance along the view; null when behind the eye or off-screen.</summary>
    public (int X, int Y, double Dist)? Project(EyeScene s, double x, double y, double z)
    {
        var c = Camera(s);
        double dx = x - c.Ox, dy = y - c.Oy, dz = z - c.Oz;
        double f = dx * c.Fx + dy * c.Fy + dz * c.Fz;
        if (f < 0.05) return null;
        double r = dx * c.Rx + dy * c.Ry + dz * c.Rz, u = dx * c.Ux + dy * c.Uy + dz * c.Uz;
        double sx = r / f / c.TanX, sy = -u / f / c.TanY;
        int px = (int)Math.Floor((sx + 1) / 2 * Width), py = (int)Math.Floor((sy + 1) / 2 * Height);
        if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) return null;
        return (px, py, Math.Sqrt(dx * dx + dy * dy + dz * dz));
    }

    /// <summary>Renders the frame (pixels 0xRRGGBB, depth = distance or +∞ for sky). The arrays are reused by the next call.</summary>
    public Frame Raster(EyeScene s)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int W = Width, H = Height;
        if (_px.Length != W * H) { _px = new uint[W * H]; _depth = new float[W * H]; }
        int bands = Math.Clamp(Environment.ProcessorCount, 1, Math.Clamp(MaxThreads, 1, 64));
        if (_caches.Length != bands) _caches = Enumerable.Range(0, bands).Select(_ => new LightCache()).ToArray();
        foreach (var lc in _caches) lc.Clear();
        if (s.World is WorldVoxels wv) wv.Prepare((int)Math.Floor(s.EyeX), (int)Math.Floor(s.EyeZ), MaxDistance / 16 + 1);
        var cam = Camera(s);
        int sub = s.Nether ? 0 : Lighting.SkylightSubtracted(s.Time);
        var boxes = s.Entities.Where(e => e.Kind != "self").Select(e => Box(e)).ToArray();
        Parallel.For(0, bands, b =>
        {
            var cache = _caches[b];
            for (int y = b; y < H; y += bands)
                for (int x = 0; x < W; x++)
                {
                    double sx = (2 * (x + 0.5) / W - 1) * cam.TanX, sy = (1 - 2 * (y + 0.5) / H) * cam.TanY;
                    double dx = cam.Fx + cam.Rx * sx + cam.Ux * sy, dy = cam.Fy + cam.Ry * sx + cam.Uy * sy, dz = cam.Fz + cam.Rz * sx + cam.Uz * sy;
                    double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    dx /= l; dy /= l; dz /= l;
                    var (c, d) = Trace(s, cam.Ox, cam.Oy, cam.Oz, dx, dy, dz, sub, cache, boxes);
                    _px[y * W + x] = c;
                    _depth[y * W + x] = d;
                }
        });
        LastMs = sw.Elapsed.TotalMilliseconds;
        return new Frame(_px, _depth, W, H);
    }

    private readonly record struct EBox(double X0, double Y0, double Z0, double X1, double Y1, double Z1, VisionEntity E);

    private static EBox Box(VisionEntity e)
    {
        (double w, double h) = e.Kind switch { "item" => (0.25, 0.25), "mob" => (0.6, 1.6), _ => (0.6, 1.8) };
        return new EBox(e.X - w / 2, e.Y, e.Z - w / 2, e.X + w / 2, e.Y + h, e.Z + w / 2, e);
    }

    /// <summary>Partial block heights (bottom-aligned): slabs, snow layers, plates, rails, farmland, closed trapdoors.</summary>
    private static double HeightOf(int id, int meta) => id switch
    {
        Blocks.Slab => (meta & 8) != 0 ? 1 : 0.5,
        Blocks.SnowLayer => ((meta & 7) + 1) / 8.0,
        Blocks.StonePressurePlate or Blocks.WoodenPressurePlate => 1 / 16.0,
        Blocks.Rail or Blocks.PoweredRail or Blocks.DetectorRail or Blocks.RedstoneWire => 1 / 16.0,
        Blocks.Farmland => 15 / 16.0,
        Blocks.Trapdoor => (meta & 4) != 0 ? 1 : 3 / 16.0,
        _ => 1,
    };

    private static bool Translucent(int id) => id is Blocks.Water or Blocks.FlowingWater or Blocks.Ice or Blocks.Portal;

    private (uint Colour, float Depth) Trace(EyeScene s, double ox, double oy, double oz, double dx, double dy, double dz, int sub, LightCache cache, EBox[] boxes)
    {
        int vx = (int)Math.Floor(ox), vy = (int)Math.Floor(oy), vz = (int)Math.Floor(oz);
        int stepX = dx > 0 ? 1 : -1, stepY = dy > 0 ? 1 : -1, stepZ = dz > 0 ? 1 : -1;
        double tdx = dx == 0 ? double.MaxValue : Math.Abs(1 / dx), tdy = dy == 0 ? double.MaxValue : Math.Abs(1 / dy), tdz = dz == 0 ? double.MaxValue : Math.Abs(1 / dz);
        double tmx = dx == 0 ? double.MaxValue : (dx > 0 ? vx + 1 - ox : ox - vx) * tdx;
        double tmy = dy == 0 ? double.MaxValue : (dy > 0 ? vy + 1 - oy : oy - vy) * tdy;
        double tmz = dz == 0 ? double.MaxValue : (dz > 0 ? vz + 1 - oz : oz - vz) * tdz;
        // Nearest entity box along the ray (players drawn as boxes).
        double eT = double.MaxValue;
        int eIdx = -1;
        for (int i = 0; i < boxes.Length; i++)
            if (RayBox(ox, oy, oz, dx, dy, dz, boxes[i].X0, boxes[i].Y0, boxes[i].Z0, boxes[i].X1, boxes[i].Y1, boxes[i].Z1, out double t, out _) && t < eT) { eT = t; eIdx = i; }

        double t0 = 0; // distance at which the current voxel was entered
        int lastAxis = -1;
        uint tint = 0;
        float tintA = 0;
        var w = s.World;
        int maxSteps = MaxDistance * 3 + 3;
        for (int step = 0; step < maxSteps && t0 <= MaxDistance; step++)
        {
            if (eIdx >= 0 && eT <= t0) return Finish(ShadeEntity(s, boxes[eIdx], ox + dx * eT, oy + dy * eT, oz + dz * eT, dx, dy, dz), eT, tint, tintA);
            if (vy < 0 && dy < 0 || vy >= WorldState.Height && dy > 0) break;
            int id = (uint)vy < WorldState.Height ? w.Block(vx, vy, vz) : 0;
            if (id != 0 && step > 0)
            {
                int meta = w.Meta(vx, vy, vz);
                double h = HeightOf(id, meta);
                double ht = t0;
                Face face;
                double hx, hy, hz;
                bool hit;
                if (h < 1)
                {
                    hit = RayBox(ox, oy, oz, dx, dy, dz, vx, vy, vz, vx + 1, vy + h, vz + 1, out ht, out int ax);
                    face = ax switch { 0 => stepX > 0 ? Face.West : Face.East, 1 => stepY > 0 ? Face.Down : Face.Up, _ => stepZ > 0 ? Face.North : Face.South };
                }
                else
                {
                    hit = true;
                    face = lastAxis switch { 0 => stepX > 0 ? Face.West : Face.East, 1 => stepY > 0 ? Face.Down : Face.Up, _ => stepZ > 0 ? Face.North : Face.South };
                }
                if (hit)
                {
                    hx = ox + dx * ht; hy = oy + dy * ht; hz = oz + dz * ht;
                    var (u, v) = Uv(face, hx - vx, hy - vy, hz - vz);
                    uint? tex = s.Atlas is { } atlas ? BlockTextures.Texel(atlas, id, meta, face, (float)u, (float)v) : null;
                    bool skip = tex is { } tx && tx >> 24 < 128; // alpha-tested texel: look through
                    if (!skip)
                    {
                        uint baseC = tex ?? Palette(id, face);
                        float k = Light(s, w, id, vx, vy, vz, face, u, v, sub, cache);
                        if (w.State(vx, vz) == ColumnState.Remembered) k = Math.Max(k, 0.45f) * 0.8f; // no block light stored: floor, then dimmer
                        uint c = Lighting.Scale(baseC, k);
                        if (Translucent(id) && tintA < 0.5f)
                        {
                            // Water / ice / portal: tint what's behind, once.
                            tint = c; tintA = id == Blocks.Ice ? 0.45f : 0.6f;
                        }
                        else if (eIdx < 0 || ht < eT) return Finish(Fog(s, c, ht, dy, sub), ht, tint, tintA);
                    }
                }
            }
            // Advance.
            if (tmx < tmy && tmx < tmz) { vx += stepX; t0 = tmx; tmx += tdx; lastAxis = 0; }
            else if (tmy < tmz) { vy += stepY; t0 = tmy; tmy += tdy; lastAxis = 1; }
            else { vz += stepZ; t0 = tmz; tmz += tdz; lastAxis = 2; }
        }
        if (eIdx >= 0) return Finish(ShadeEntity(s, boxes[eIdx], ox + dx * eT, oy + dy * eT, oz + dz * eT, dx, dy, dz), eT, tint, tintA);
        return Finish(Sky(s, dy, sub), float.PositiveInfinity, tint, tintA);
    }

    private static (uint, float) Finish(uint c, double depth, uint tint, float a) => (a > 0 ? IsoPalette.Mix(c, tint, a) : c, (float)depth);

    private static (double U, double V) Uv(Face f, double fx, double fy, double fz) => f switch
    {
        Face.Up or Face.Down => (fx, fz),
        Face.North => (1 - fx, 1 - fy),
        Face.South => (fx, 1 - fy),
        Face.West => (fz, 1 - fy),
        _ => (1 - fz, 1 - fy),
    };

    private static uint Palette(int id, Face f)
    {
        var m = IsoPalette.Mat(id);
        if (m == null) return 0x808080;
        return f == Face.Up || f == Face.Down ? m.Top : m.Side;
    }

    private float Light(EyeScene s, IVoxelWorld w, int id, int x, int y, int z, Face f, double u, double v, int sub, LightCache cache)
    {
        if (BlockTextures.Emissive(id)) return 1f;
        long key = ((long)(x & 0xfffff) << 40) | ((long)(z & 0xfffff) << 20) | ((long)(y & 0xff) << 3) | (long)f;
        int slot = (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15)) >> 52);
        if (cache.Keys[slot] != key)
        {
            cache.Keys[slot] = key;
            if (!s.Smooth)
            {
                float k = FaceLight.Of(w, id, x, y, z, f, sub, s.Nether);
                cache.Vals[4 * slot] = cache.Vals[4 * slot + 1] = cache.Vals[4 * slot + 2] = cache.Vals[4 * slot + 3] = Math.Max(MinBrightness, k);
            }
            else SmoothCorners(s, w, x, y, z, f, sub, cache.Vals.AsSpan(4 * slot, 4));
        }
        var c = cache.Vals.AsSpan(4 * slot, 4);
        if (!s.Smooth) return c[0];
        float fu = (float)u, fv = (float)v;
        return (c[0] * (1 - fu) + c[1] * fu) * (1 - fv) + (c[2] * (1 - fu) + c[3] * fu) * fv;
    }

    /// <summary>Smooth lighting: each face corner averages the light of the 4 cells around it in the layer in front of the
    /// face (opaque cells count as the face cell's own light), times the face shade. Corners in (u,v) order 00, 10, 01, 11.</summary>
    private void SmoothCorners(EyeScene s, IVoxelWorld w, int x, int y, int z, Face f, int sub, Span<float> outC)
    {
        var (nx, ny, nz) = Lighting.Normal(f);
        int ax = x + nx, ay = y + ny, az = z + nz;
        int centre = FaceLight.CellLevel(w, ax, ay, az, sub);
        float shade = Lighting.FaceShade(f);
        // Tangent axes matching Uv(): (uAxis, vAxis) as unit steps with their sign.
        (int, int, int) uA, vA;
        switch (f)
        {
            case Face.Up or Face.Down: uA = (1, 0, 0); vA = (0, 0, 1); break;
            case Face.North: uA = (-1, 0, 0); vA = (0, -1, 0); break;
            case Face.South: uA = (1, 0, 0); vA = (0, -1, 0); break;
            case Face.West: uA = (0, 0, 1); vA = (0, -1, 0); break;
            default: uA = (0, 0, -1); vA = (0, -1, 0); break;
        }
        for (int corner = 0; corner < 4; corner++)
        {
            int su = (corner & 1) == 0 ? -1 : 1, sv = (corner & 2) == 0 ? -1 : 1;
            int sum = 0, n = 0;
            for (int a = 0; a <= 1; a++)
                for (int b = 0; b <= 1; b++)
                {
                    int cx = ax + uA.Item1 * su * a + vA.Item1 * sv * b, cy = ay + uA.Item2 * su * a + vA.Item2 * sv * b, cz = az + uA.Item3 * su * a + vA.Item3 * sv * b;
                    int lvl = a == 0 && b == 0 ? centre : FaceLight.Opaque(w.Block(cx, cy, cz)) ? centre : FaceLight.CellLevel(w, cx, cy, cz, sub);
                    if (lvl < 0) continue;
                    sum += lvl; n++;
                }
            float k = n == 0 ? 0.75f : Lighting.Brightness((int)Math.Round(sum / (double)n), s.Nether);
            outC[corner] = Math.Max(MinBrightness, k * shade);
        }
    }

    private static uint SkyTop(EyeScene s, int sub) => s.Nether ? 0x3a0e0a : IsoPalette.Scale(0x78a7ff, 1f - sub / 13f);
    private static uint SkyHorizon(EyeScene s, int sub) => s.Nether ? 0x5a1a10 : IsoPalette.Scale(0xc3d9ff, 1f - sub / 12.5f);

    private static uint Sky(EyeScene s, double dy, int sub)
    {
        if (dy >= 0) return IsoPalette.Mix(SkyHorizon(s, sub), SkyTop(s, sub), (float)Math.Min(1, dy * 1.6));
        // Below the horizon: the void, darkening downward.
        return IsoPalette.Mix(IsoPalette.Scale(SkyHorizon(s, sub), 0.55f), s.Nether ? 0x120404u : 0x05070cu, (float)Math.Min(1, -dy * 2.2));
    }

    private uint Fog(EyeScene s, uint c, double dist, double dy, int sub)
    {
        double start = MaxDistance * 0.6;
        if (dist <= start) return c;
        float a = (float)Math.Min(0.85, (dist - start) / (MaxDistance - start));
        return IsoPalette.Mix(c, SkyHorizon(s, sub), a);
    }

    private uint ShadeEntity(EyeScene s, EBox b, double hx, double hy, double hz, double dx, double dy, double dz)
    {
        // Which face was hit (closest box plane to the hit point).
        double ex = Math.Min(Math.Abs(hx - b.X0), Math.Abs(hx - b.X1)), ey = Math.Min(Math.Abs(hy - b.Y0), Math.Abs(hy - b.Y1)), ez = Math.Min(Math.Abs(hz - b.Z0), Math.Abs(hz - b.Z1));
        Face f = ey <= ex && ey <= ez ? (dy < 0 ? Face.Up : Face.Down) : ex <= ez ? (dx > 0 ? Face.West : Face.East) : (dz > 0 ? Face.North : Face.South);
        double fy = (hy - b.Y0) / Math.Max(0.01, b.Y1 - b.Y0);
        double fu = f is Face.West or Face.East ? (hz - b.Z0) / (b.Z1 - b.Z0) : (hx - b.X0) / (b.X1 - b.X0);
        uint c;
        var e = b.E;
        if (e.Kind == "item") c = 0xf2e46a;
        else if (e.Kind == "mob") c = 0x52b08e;
        else if (s.Atlas?.Skin is { } skin && skin.W >= 64 && skin.H >= 32)
        {
            // Steve skin: head (top 25%), body, legs — front textures on every side.
            int tx, ty;
            double up = 1 - fy;
            if (up < 0.25) { tx = 8 + (int)(fu * 8); ty = 8 + (int)(up / 0.25 * 8); }
            else if (up < 0.625) { tx = 20 + (int)(fu * 8); ty = 20 + (int)((up - 0.25) / 0.375 * 12); }
            else { tx = 4 + (int)(fu * 4) % 4; ty = 20 + (int)((up - 0.625) / 0.375 * 12); }
            c = skin.Px[Math.Clamp(ty, 0, skin.H - 1) * skin.W + Math.Clamp(tx, 0, skin.W - 1)] & 0xffffff;
            if (e.Kind == "hive") c = IsoPalette.Mix(c, 0xff9f43, 0.35f);
        }
        else c = e.Kind == "hive" ? 0xff9f43 : VisionRenderer.PlayerColour(e.Name);
        return Lighting.Scale(c, Lighting.FaceShade(f));
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

    // ------------------------------------------------------------------ the picture

    /// <summary>Raster + overlays (crosshair, outlines, name tags, labels) → PNG.</summary>
    public VisionImage Render(EyeScene s)
    {
        var fr = Raster(s);
        var c = new Canvas(fr.Width, fr.Height, (uint[])fr.Px.Clone());
        var labels = new List<string>();

        void Outline((int X, int Y, int Z)? b, uint col)
        {
            if (b is not { } p) return;
            var corners = new (int X, int Y, double D)?[8];
            for (int i = 0; i < 8; i++) corners[i] = Project(s, p.X + (i & 1), p.Y + ((i >> 1) & 1), p.Z + ((i >> 2) & 1));
            int[][] edges = [[0, 1], [2, 3], [4, 5], [6, 7], [0, 2], [1, 3], [4, 6], [5, 7], [0, 4], [1, 5], [2, 6], [3, 7]];
            foreach (var e in edges)
                if (corners[e[0]] is { } a && corners[e[1]] is { } bb) c.Line(a.X, a.Y, bb.X, bb.Y, col);
        }
        Outline(s.Target, 0xffd84a);
        Outline(s.Pointed, 0xff4fd8);

        bool Visible(int x, int y, double dist) => fr.Depth[y * fr.Width + x] >= dist - 1.0;
        var placer = new VisionRenderer.Placer(0, 0, fr.Width, fr.Height);
        void Tag(int x, int y, string t, uint fg)
        {
            int w = PixelFont.Width(t) + 4;
            if (placer.FindNear(x - w / 2, y - 11, w, 11) is { } at) c.Chip(at.X, at.Y, t, fg);
        }
        foreach (var e in s.Entities.Where(e => e.Kind is "player" or "hive"))
            if (Project(s, e.X, e.Y + 2.15, e.Z) is { } p && p.Dist < MaxDistance)
            {
                var t = string.IsNullOrEmpty(e.Name) ? e.Kind : e.Name!.Length > 14 ? e.Name[..14] : e.Name;
                Tag(p.X, p.Y - 6, t, e.Kind == "hive" ? 0xff9f43 : VisionRenderer.PlayerColour(e.Name));
            }
        foreach (var m in s.Marks)
            if (Project(s, m.CentreX + 0.5, m.MaxY + 1.2, m.CentreZ + 0.5) is { } p && p.Dist < MaxDistance && Visible(p.X, Math.Min(fr.Height - 1, p.Y + 3), p.Dist))
            {
                var t = VisionRenderer.ShortLabel(m.Text);
                if (t.Length == 0) continue;
                Tag(p.X, p.Y, t, m.Box ? 0xffd84au : 0xe8ecf4u);
                labels.Add($"{t} {m.MinX},{m.MinY},{m.MinZ}");
            }

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
            $"eye view{(s.Viewer != null ? " of " + s.Viewer : "")} from {s.EyeX:0.#},{s.EyeY:0.#},{s.EyeZ:0.#} facing {dir} (yaw {s.Yaw:0}, pitch {s.Pitch:0}), {FovDegrees:0}° fov, {fr.Width}x{fr.Height}px ~{(fr.Width * fr.Height + 749) / 750}tok, {LastMs:0}ms.{onText} Gold=target, pink=pointed; dimmer=remembered.");
        return new VisionImage(Png.Encode(c.Px, c.Width, c.Height), c.Width, c.Height, 0, legend, labels);
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
        for (int i = 0; i < MaxDistance * 3 && t <= MaxDistance; i++)
        {
            if (i > 0 && (uint)vy < WorldState.Height && s.World.Block(vx, vy, vz) is var id and not 0 && !Translucent(id))
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
