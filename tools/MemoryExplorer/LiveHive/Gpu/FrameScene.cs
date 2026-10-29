using System.Numerics;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.MemoryExplorer.LiveHive.Gpu;

/// <summary>A section's GPU buffers (owned by the GL renderer; the scene only reads ids and counts) and its per-frame uniforms.</summary>
public sealed class GpuSection
{
    public SectionKey Key;
    public int SolidVbo, SolidCount, TransVbo, TransCount;
    public float Dist, Rem, Stale;
}

/// <summary>A screen label for the overlay control (render-pixel coordinates).</summary>
public struct LabelItem
{
    public string Text;
    public float X, Y, Dist;
    public uint Colour;
    public bool Occluded;
}

/// <summary>
/// Everything one GPU frame needs, prepared on the CPU without allocating (once warmed up): the camera (view-projection,
/// camera-relative so floats stay precise far from 0,0), the visible sections (frustum-culled, solid front-to-back,
/// translucent back-to-front, with their staleness / remembered uniforms), the dynamic geometry (players, mobs, bots, dropped
/// items with shadows), the overlay boxes as lines (rebuilt only when the overlays change) and the screen labels.
/// </summary>
public sealed class FrameScene
{
    /// <summary>The shared entity description of a wire entity (CPU and GPU paths draw the same models).</summary>
    public static VisionEntity Vision(EntityDto e) => ToVision(new EntityView { Id = e.Id ?? 0, Kind = e.Kind, Name = e.Name, X = e.X, Y = e.Y, Z = e.Z, Item = e.Item ?? 0, Damage = e.Damage ?? 0, Count = e.Count ?? 1, Yaw = e.Yaw ?? 0, Vx = e.Vx ?? 0, Vz = e.Vz ?? 0, AgeTicks = (e.Age ?? 0) * 20, Flags = e.Flags ?? 0, Variant = e.Variant ?? 0 });

    private readonly HiveWorldCache _cache;
    private readonly HiveVoxels _vox;
    private readonly List<EntityView> _ents = new(256);
    private readonly Dictionary<(int Id, int Count), string> _dropText = new();
    private readonly List<(string Text, Vector3 At, uint Colour, bool Always)> _staticLabels = [];
    private readonly Vector4[] _planes = new Vector4[6];
    private static readonly Comparison<GpuSection> NearFirst = (a, b) => a.Dist.CompareTo(b.Dist);
    private static readonly Comparison<GpuSection> FarFirst = (a, b) => b.Dist.CompareTo(a.Dist);
    private object? _linesFor;
    private Overlay _linesFlags;
    private int _linesDim = int.MinValue;

    public FrameScene(HiveWorldCache cache)
    {
        _cache = cache;
        _vox = new HiveVoxels(cache);
    }

    public readonly Dictionary<SectionKey, GpuSection> Sections = new();
    public readonly List<GpuSection> SolidDraw = new(1024), TransDraw = new(256);
    public readonly VertexList DynSolid = new(), DynTrans = new(), Lines = new();
    /// <summary>World point the line vertices are relative to (subtract the camera at draw time).</summary>
    public Vector3d LinesOrigin;
    public bool LinesChanged;
    public readonly List<LabelItem> Labels = new(128);

    public EntityStore? Entities { get; set; }
    public Func<IReadOnlyList<BotDto>> Bots { get; set; } = () => [];
    public Func<OverlaysDto?> Overlays { get; set; } = () => null;
    public Overlay Flags { get; set; } = Overlay.Default;
    public bool Textured { get; set; }
    /// <summary>World.k at the world time (0 noon, 11 midnight): the sky colours and the entities' light follow it.</summary>
    public int SkySubtracted { get; set; }
    /// <summary>The sky's zenith, horizon (= fog) and void colours this frame (the CPU eye's, <see cref="EyeRenderer.SkyColours"/>).</summary>
    public (uint Top, uint Horizon, uint Void) Sky => EyeRenderer.SkyColours(SkySubtracted, Pose.Dim == -1);
    public float FovDegrees { get; set; } = 70;
    public int MaxDistance { get; set; } = 96;
    /// <summary>Columns seen within this long draw as live; older ones dimmer (the CPU eye's rule).</summary>
    public TimeSpan Fresh { get; set; } = TimeSpan.FromMinutes(2);

    public CamPose Pose { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public Matrix4x4 ViewProj { get; private set; }
    public ViewProjector Projector { get; private set; }
    public (float X, float Y, float Z) Forward, Right, Up;
    public float TanX, TanY;
    public int EntitiesDrawn { get; private set; }

    public void Prepare(CamPose pose, int w, int h, DateTimeOffset now)
    {
        Pose = pose; Width = w; Height = h;
        var proj = new ViewProjector(pose, w, h, FovDegrees);
        Projector = proj;
        Forward = ((float)proj.Fx, (float)proj.Fy, (float)proj.Fz);
        Right = ((float)proj.Rx, (float)proj.Ry, (float)proj.Rz);
        Up = ((float)proj.Ux, (float)proj.Uy, (float)proj.Uz);
        TanX = (float)proj.TanX; TanY = (float)proj.TanY;
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(Forward.X, Forward.Y, Forward.Z), new Vector3(Up.X, Up.Y, Up.Z));
        ViewProj = view * Perspective(FovDegrees * MathF.PI / 180, w / (float)Math.Max(1, h), 0.05f, MaxDistance * 2.5f);
        Planes(ViewProj);
        _vox.Prepare(pose.Dim, (int)Math.Floor(pose.X), (int)Math.Floor(pose.Z), MaxDistance / 16 + 1, now);
        CullSections(now);
        BuildDynamic(now);
        BuildLines();
        BuildLabels();
    }

    /// <summary>OpenGL-style perspective (clip z in −w..w) for row vectors (v · M).</summary>
    public static Matrix4x4 Perspective(float fovY, float aspect, float near, float far)
    {
        float f = 1f / MathF.Tan(fovY / 2);
        return new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
    }

    private void Planes(Matrix4x4 m)
    {
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41); var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43); var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _planes[0] = c4 + c1; _planes[1] = c4 - c1; _planes[2] = c4 + c2; _planes[3] = c4 - c2; _planes[4] = c4 + c3; _planes[5] = c4 - c3;
    }

    /// <summary>AABB (camera-relative) against the frustum: false when entirely outside one plane.</summary>
    public bool Visible(Vector3 lo, Vector3 hi)
    {
        foreach (var p in _planes)
        {
            float x = p.X >= 0 ? hi.X : lo.X, y = p.Y >= 0 ? hi.Y : lo.Y, z = p.Z >= 0 ? hi.Z : lo.Z;
            if (p.X * x + p.Y * y + p.Z * z + p.W < 0) return false;
        }
        return true;
    }

    private void CullSections(DateTimeOffset now)
    {
        SolidDraw.Clear();
        TransDraw.Clear();
        long nowMs = now.ToUnixTimeMilliseconds(), freshAfter = nowMs - (long)Fresh.TotalMilliseconds;
        bool stale = Flags.HasFlag(Overlay.Staleness);
        float maxD = MaxDistance + 14;
        foreach (var s in Sections.Values)
        {
            var k = s.Key;
            var lo = new Vector3((float)(k.Cx * 16 - Pose.X), (float)(k.Sy * 16 - Pose.Y), (float)(k.Cz * 16 - Pose.Z));
            var c = lo + new Vector3(8, 8, 8);
            float d = c.Length();
            if (d > maxD + 14 || !Visible(lo, lo + new Vector3(16, 16, 16))) continue;
            s.Dist = d;
            var col = _cache.Get(Pose.Dim, k.Cx, k.Cz);
            s.Rem = col != null && col.SeenMs < freshAfter ? 1 : 0;
            s.Stale = stale && col != null ? OverlayPainter.AgeAlpha(nowMs - col.SeenMs) : 0;
            if (s.SolidCount > 0) SolidDraw.Add(s);
            if (s.TransCount > 0) TransDraw.Add(s);
        }
        SolidDraw.Sort(NearFirst);
        TransDraw.Sort(FarFirst);
    }

    private float LightAt(double x, double y, double z)
    {
        int lvl = FaceLight.CellLevel(_vox, (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), Pose.Dim == -1 ? 0 : SkySubtracted);
        return lvl < 0 ? 0.75f : Math.Max(0.2f, Lighting.Brightness(lvl, Pose.Dim == -1));
    }

    /// <summary>Top of the first non-air block at or below (x, y, z) within 4 blocks (for shadows); NaN = none.</summary>
    private float Ground(double x, double y, double z)
    {
        int bx = (int)Math.Floor(x), bz = (int)Math.Floor(z), by = (int)Math.Floor(y + 0.01);
        for (int yy = by; yy >= by - 4 && yy >= 0; yy--)
        {
            int id = _vox.Block(bx, yy, bz);
            if (id != 0 && BlockGeometry.OpaqueCube(id)) return yy + 1;
        }
        return float.NaN;
    }

    /// <summary>One entity's posed model from MineClaw.Render (<see cref="EntityGeometry.Build"/>, feet-relative), re-posed at
    /// most every <see cref="ModelRefreshMs"/> (walk swing, item bob/spin), translated to the interpolated position every frame.</summary>
    private sealed class ModelCache
    {
        public readonly List<GeoQuad> Quads = new(96);
        public double At = double.MinValue;
        public float Shadow;
        public long Seen;
    }

    private readonly Dictionary<long, ModelCache> _models = new();
    private readonly List<long> _modelDrop = [];
    private long _frame;
    /// <summary>How often a model is re-posed (the shared geometry allocates per build, so not every frame).</summary>
    public double ModelRefreshMs { get; set; } = 33;
    /// <summary>The texture atlas the shared entity geometry samples (skin, mob textures); null = stand-ins.</summary>
    public Func<TextureAtlas?> Atlas { get; set; } = () => null;

    private static VisionEntity ToVision(in EntityView e) => e.Kind switch
    {
        "item" => new VisionEntity("item", e.Name, e.X, e.Y, e.Z) { ItemId = e.Item, ItemCount = Math.Max(1, e.Count), ItemDamage = e.Damage, Seed = e.Id, Age = e.AgeTicks },
        "mob" => new VisionEntity("mob", e.Name, e.X, e.Y, e.Z) { Mob = e.Name, Yaw = e.Yaw, Vx = e.Vx, Vz = e.Vz, Seed = e.Id, Age = e.AgeTicks, Variant = e.Variant,
            Sneaking = (e.Flags & EntityDto.Sneaking) != 0, Burning = (e.Flags & EntityDto.Burning) != 0 },
        _ => new VisionEntity(e.Kind, e.Name, e.X, e.Y, e.Z) { Yaw = e.Yaw, Vx = e.Vx, Vz = e.Vz, HeldItem = e.Kind == "player" ? e.Item : 0, Seed = e.Id, Age = e.AgeTicks,
            Sneaking = (e.Flags & EntityDto.Sneaking) != 0, Burning = (e.Flags & EntityDto.Burning) != 0 },
    };

    private void Model(long key, in EntityView e, double nowMs, float light)
    {
        if (!_models.TryGetValue(key, out var m)) _models[key] = m = new ModelCache();
        m.Seen = _frame;
        if (nowMs - m.At >= ModelRefreshMs)
        {
            m.At = nowMs;
            m.Quads.Clear();
            var ve = ToVision(e);
            try { EntityGeometry.Build(ve, m.Quads, Pose.X, Pose.Z, Atlas()); } catch (Exception) { }
            m.Shadow = EntityGeometry.ShadowSize(ve);
        }
        var feet = new Vector3((float)(e.X - Pose.X), (float)(e.Y - Pose.Y), (float)(e.Z - Pose.Z));
        foreach (var q in m.Quads)
            QuadEmit.Emit(q.Pass == RenderPass.Translucent ? DynTrans : DynSolid, q, feet, Math.Max(0.12f, light * q.Shade), Textured);
        float g = Ground(e.X, e.Y, e.Z);
        if (!float.IsNaN(g) && e.Y - g < 3) Shadow(feet, g - (float)Pose.Y, m.Shadow, (float)(0.75 * Math.Clamp(1 - (e.Y - g) / 3, 0, 1)));
        EntitiesDrawn++;
    }

    /// <summary>The round contact shadow (Render.shadowSize radius) on the ground below the feet.</summary>
    private void Shadow(Vector3 feet, float groundRel, float radius, float alpha)
    {
        float y = groundRel + 0.015f;
        var q = GeoQuad.Make(new(feet.X - radius, y, feet.Z - radius), new(feet.X - radius, y, feet.Z + radius),
            new(feet.X + radius, y, feet.Z + radius), new(feet.X + radius, y, feet.Z - radius), new(0, 0), new(0, 1), new(1, 1), new(1, 0));
        q.Alpha = alpha;
        QuadEmit.EmitExtra(DynTrans, q, Vector3.Zero, 1f, GpuAtlas.Shadow, 0x000000);
    }

    private void BuildDynamic(DateTimeOffset now)
    {
        DynSolid.Clear();
        DynTrans.Clear();
        EntitiesDrawn = 0;
        _frame++;
        double nowMs = FreecamStats.NowMs;
        double maxD2 = (double)MaxDistance * MaxDistance;
        bool showEnts = Flags.HasFlag(Overlay.Bots), showDrops = Flags.HasFlag(Overlay.Drops) || showEnts;
        _ents.Clear();
        if (Entities != null && (showEnts || showDrops))
        {
            Entities.Interpolated(Pose.Dim, _ents);
            foreach (var e in _ents)
            {
                double dx = e.X - Pose.X, dy = e.Y - Pose.Y, dz = e.Z - Pose.Z;
                if (dx * dx + dy * dy + dz * dz > maxD2) continue;
                bool item = e.Kind == "item";
                if (item ? !showDrops : !showEnts) continue;
                var feet = new Vector3((float)dx, (float)dy, (float)dz);
                if (!Visible(feet - new Vector3(1.5f, 0.5f, 1.5f), feet + new Vector3(1.5f, 3f, 1.5f))) continue;
                Model(e.Id, e, nowMs, LightAt(e.X, e.Y + 0.2, e.Z));
            }
        }
        var bots = Bots();
        if (showEnts)
            for (int bi = 0; bi < bots.Count; bi++)
            {
                var b = bots[bi];
                if (b.Dim != Pose.Dim) continue;
                double dx = b.X - Pose.X, dy = b.Y - Pose.Y, dz = b.Z - Pose.Z;
                if (dx * dx + dy * dy + dz * dz > maxD2 || dx * dx + dy * dy + dz * dz < 0.3) continue;
                var feet = new Vector3((float)dx, (float)dy, (float)dz);
                if (!Visible(feet - new Vector3(1, 0.5f, 1), feet + new Vector3(1, 2.5f, 1))) continue;
                var v = new EntityView { Id = 0, Kind = "hive", Name = b.Name, X = b.X, Y = b.Y, Z = b.Z, Yaw = b.Yaw ?? 0 };
                Model(-1 - bi, v, nowMs, LightAt(b.X, b.Y + 1, b.Z));
            }
        if (_models.Count > 512 || _frame % 120 == 0)
        {
            _modelDrop.Clear();
            foreach (var kv in _models) if (_frame - kv.Value.Seen > 120) _modelDrop.Add(kv.Key);
            foreach (var k in _modelDrop) _models.Remove(k);
        }
    }

    // ------------------------------------------------------------------ overlay boxes (rebuilt on change)

    private void BuildLines()
    {
        var ov = Overlays();
        if (ReferenceEquals(ov, _linesFor) && Flags == _linesFlags && Pose.Dim == _linesDim) return;
        _linesFor = ov; _linesFlags = Flags; _linesDim = Pose.Dim;
        Lines.Clear();
        _staticLabels.Clear();
        LinesChanged = true;
        LinesOrigin = new Vector3d(Math.Floor(Pose.X), Math.Floor(Pose.Y), Math.Floor(Pose.Z));
        if (ov == null || ov.Dim != Pose.Dim) return;
        var o = LinesOrigin;
        void Box(double x0, double y0, double z0, double x1, double y1, double z1, uint c, float hidden)
        {
            var lo = new Vector3((float)(x0 - o.X), (float)(y0 - o.Y), (float)(z0 - o.Z));
            var hi = new Vector3((float)(x1 - o.X), (float)(y1 - o.Y), (float)(z1 - o.Z));
            uint rgba = QuadEmit.Pack(c, hidden);
            for (int e = 0; e < 12; e++)
            {
                var (a, b) = Edge(e, lo, hi);
                ref var v = ref Lines.Add4(); // two lines' worth; the second pair is unused padding
                Lines.Count -= 2;
                var span = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref v, 2);
                span[0] = new GpuVertex { X = a.X, Y = a.Y, Z = a.Z, Light = 255, Rgba = rgba };
                span[1] = new GpuVertex { X = b.X, Y = b.Y, Z = b.Z, Light = 255, Rgba = rgba };
            }
        }
        void Mark(MarkDto m, uint colour, string text, float inset, bool label, float hidden = 0)
        {
            if (m.Min.Length < 3 || m.Max.Length < 3) return;
            double x0 = Math.Min(m.Min[0], m.Max[0]) + inset, y0 = Math.Min(m.Min[1], m.Max[1]) + inset, z0 = Math.Min(m.Min[2], m.Max[2]) + inset;
            double x1 = Math.Max(m.Min[0], m.Max[0]) + 1 - inset, y1 = Math.Max(m.Min[1], m.Max[1]) + 1 - inset, z1 = Math.Max(m.Min[2], m.Max[2]) + 1 - inset;
            Box(x0, y0, z0, x1, y1, z1, colour, hidden);
            if (label && text.Length > 0) _staticLabels.Add((text.Length > 40 ? text[..39] + "…" : text, new((float)((x0 + x1) / 2), (float)(y1 + 0.6), (float)((z0 + z1) / 2)), colour, hidden > 0));
        }
        if (Flags.HasFlag(Overlay.Plans))
        {
            int drawn = 0;
            foreach (var plan in ov.Plans)
            {
                foreach (var b in plan.Blocks)
                {
                    if (b.Length < 6 || drawn > 4000) continue;
                    bool done = b[5] != 0;
                    if (done && _vox.Block(b[0], b[1], b[2]) == b[3]) continue;
                    Box(b[0] + 0.03, b[1] + 0.03, b[2] + 0.03, b[0] + 0.97, b[1] + 0.97, b[2] + 0.97, done ? OverlayPainter.PlanDoneColour : OverlayPainter.PlanColour, 0.25f);
                    drawn++;
                }
                if (plan.Blocks.Count > 0 && plan.Blocks[0] is { Length: >= 3 } f)
                    _staticLabels.Add(($"plan #{plan.Id} {plan.Name} ({plan.Status})", new(f[0] + 0.5f, f[1] + 1.6f, f[2] + 0.5f), OverlayPainter.PlanColour, false));
            }
        }
        if (Flags.HasFlag(Overlay.Claims)) foreach (var c in ov.Claims) Mark(c, OverlayPainter.ClaimColour, $"{c.Owner}: {c.Text}", 0, true);
        if (Flags.HasFlag(Overlay.Places)) foreach (var m in ov.Places) Mark(m, m.Kind == "waypoint" ? OverlayPainter.WaypointColour : OverlayPainter.PlaceColour, m.Text, 0, true);
        if (Flags.HasFlag(Overlay.Chests))
            foreach (var m in ov.Points)
            {
                if (m.Kind == "island")
                {
                    if (m.Min.Length >= 3 && m.Max.Length >= 3)
                        _staticLabels.Add((m.Text, new((m.Min[0] + m.Max[0]) / 2f + 0.5f, m.Max[1] + 2.5f, (m.Min[2] + m.Max[2]) / 2f + 0.5f), OverlayPainter.PlaceColour, false));
                    continue;
                }
                uint col = m.Kind switch
                {
                    "chest" => OverlayPainter.ChestColour, "cobble_generator" or "key" => OverlayPainter.GenColour, "farm" => OverlayPainter.FarmColour,
                    "portal" => OverlayPainter.PortalColour, _ => OverlayPainter.PlaceColour,
                };
                Mark(m, col, m.Kind == "key" ? m.Text : m.Kind.Replace('_', ' '), 0.02f, m.Kind is not ("chest" or "furnace"));
            }
        if (Flags.HasFlag(Overlay.Dangers)) foreach (var d in ov.Dangers) Mark(d, OverlayPainter.DangerColour, "! " + d.Text, -0.05f, true, 0.45f);
    }

    private static (Vector3, Vector3) Edge(int e, Vector3 lo, Vector3 hi)
    {
        Vector3 P(int i) => new((i & 1) != 0 ? hi.X : lo.X, (i & 2) != 0 ? hi.Y : lo.Y, (i & 4) != 0 ? hi.Z : lo.Z);
        return e switch
        {
            0 => (P(0), P(1)), 1 => (P(2), P(3)), 2 => (P(4), P(5)), 3 => (P(6), P(7)), 4 => (P(0), P(2)), 5 => (P(1), P(3)),
            6 => (P(4), P(6)), 7 => (P(5), P(7)), 8 => (P(0), P(4)), 9 => (P(1), P(5)), 10 => (P(2), P(6)), _ => (P(3), P(7)),
        };
    }

    // ------------------------------------------------------------------ labels

    private void BuildLabels()
    {
        Labels.Clear();
        foreach (var (text, at, colour, always) in _staticLabels) Label(text, at.X, at.Y, at.Z, colour, always);
        if (Flags.HasFlag(Overlay.Bots))
        {
            var bots = Bots();
            for (int bi = 0; bi < bots.Count; bi++)
                if (bots[bi] is var b && b.Dim == Pose.Dim) Label(b.Main ? "main" : b.Short, b.X, b.Y + 2.2, b.Z, OverlayPainter.BotColour, true);
            foreach (var e in _ents)
                if (e.Kind == "player" && e.Name != null) Label(e.Name, e.X, e.Y + 2.2, e.Z, OverlayPainter.PlayerColour, true);
        }
        if (Flags.HasFlag(Overlay.Drops))
        {
            if (_dropText.Count > 4096) _dropText.Clear();
            foreach (var e in _ents)
            {
                if (e.Kind != "item") continue;
                if (!_dropText.TryGetValue((e.Id, e.Count), out var t))
                    _dropText[(e.Id, e.Count)] = t = e.Count > 1 ? $"{e.Name ?? "item"} x{e.Count}" : e.Name ?? "item";
                Label(t, e.X, e.Y + 0.65, e.Z, OverlayPainter.DropColour, true);
            }
        }
    }

    private void Label(string text, double x, double y, double z, uint colour, bool always)
    {
        var cam = Projector;
        if (!cam.Project(x, y, z, out var sx, out var sy, out var dist) || dist > MaxDistance * 1.25) return;
        if (sx < -40 || sy < -20 || sx > cam.W + 40 || sy > cam.H + 20) return;
        bool occluded = Occluded(x, y, z, dist);
        if (occluded && !always) return;
        Labels.Add(new LabelItem { Text = text, X = (float)sx, Y = (float)sy, Dist = (float)dist, Colour = colour, Occluded = occluded });
    }

    /// <summary>Whether an opaque cube lies between the eye and the point (voxel DDA).</summary>
    private bool Occluded(double x, double y, double z, double dist)
    {
        double ox = Pose.X, oy = Pose.Y, oz = Pose.Z, dx = (x - ox) / dist, dy = (y - oy) / dist, dz = (z - oz) / dist;
        int vx = (int)Math.Floor(ox), vy = (int)Math.Floor(oy), vz = (int)Math.Floor(oz);
        int sx = dx > 0 ? 1 : -1, sy = dy > 0 ? 1 : -1, sz = dz > 0 ? 1 : -1;
        double tdx = dx == 0 ? double.MaxValue : Math.Abs(1 / dx), tdy = dy == 0 ? double.MaxValue : Math.Abs(1 / dy), tdz = dz == 0 ? double.MaxValue : Math.Abs(1 / dz);
        double tx = dx == 0 ? double.MaxValue : (dx > 0 ? vx + 1 - ox : ox - vx) * tdx;
        double ty = dy == 0 ? double.MaxValue : (dy > 0 ? vy + 1 - oy : oy - vy) * tdy;
        double tz = dz == 0 ? double.MaxValue : (dz > 0 ? vz + 1 - oz : oz - vz) * tdz;
        double t = 0;
        int h = _vox.WorldHeight;
        for (int i = 0; i < 512 && t < dist - 0.75; i++)
        {
            if (i > 0 && (uint)vy < h && BlockGeometry.OpaqueCube(_vox.Block(vx, vy, vz))) return true;
            if (tx < ty && tx < tz) { vx += sx; t = tx; tx += tdx; }
            else if (ty < tz) { vy += sy; t = ty; ty += tdy; }
            else { vz += sz; t = tz; tz += tdz; }
        }
        return false;
    }
}

/// <summary>A double-precision world point.</summary>
public readonly record struct Vector3d(double X, double Y, double Z);
