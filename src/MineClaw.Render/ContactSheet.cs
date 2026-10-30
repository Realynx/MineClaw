using System.Globalization;
using System.Text;
using MineClaw.World;

namespace MineClaw.Render;

/// <summary>One <c>see</c> call of vision v2: several views of the SAME focus area, composed into one contact-sheet image.</summary>
public sealed class ContactSheetRequest
{
    public required IBlockAccess Blocks { get; init; }
    public (int X, int Y, int Z) Focus { get; init; }
    /// <summary>Horizontal radius around the focus (3..12; a focused spot, not the island).</summary>
    public int Radius { get; init; } = 6;
    /// <summary>Panels in order (see <see cref="ContactSheet.Known"/>). Unknown names are ignored.</summary>
    public IReadOnlyList<string> Views { get; init; } = ContactSheet.DefaultViews;
    /// <summary>Feet level the panels are built around (default: the focus y, or the bot's feet when it stands in the area).</summary>
    public int? FeetY { get; init; }
    public (int X, int Y, int Z)? Self { get; init; }
    /// <summary>The body's exact pose (third-person panels, its model); null = from <see cref="Eye"/> or <see cref="Self"/>.</summary>
    public PerspectivePanel.Body? SelfPose { get; init; }
    public IReadOnlyList<VisionEntity> Entities { get; init; } = [];
    public IReadOnlyList<VisionMark> Marks { get; init; } = [];
    public (int X, int Y, int Z)? Target { get; init; }
    public (int X, int Y, int Z)? Pointed { get; init; }
    public string? PointedBy { get; init; }
    public bool Nether { get; init; }
    public TextureAtlas? Atlas { get; init; }
    public IVoxelWorld? Light { get; init; }
    public long Time { get; init; } = 6000;
    public RoleMap? Roles { get; init; }
    /// <summary>Side panel direction (the work axis): looking north = slice along x, looking east = slice along z.</summary>
    public SideAxis Axis { get; init; } = SideAxis.LookNorth;
    /// <summary>First-person panel, prepared by the caller (viewer, yaw, pitch); used when <see cref="Views"/> has "eye".</summary>
    public EyeScene? Eye { get; init; }
    /// <summary>Pixel budget of the whole sheet (≈ tokens × 750). 228k px ≈ 304 tokens, whatever the panel count.</summary>
    public int PixelBudget { get; init; } = ContactSheet.DefaultPixelBudget;
    /// <summary>Orbit/high/iso panels and TOP cut away roofs above the feet layer (default; off for design previews).</summary>
    public bool CutAway { get; init; } = true;
    /// <summary>Reading aids on the 3D panels (grid, rulers, labels, gizmo, rings); on by default (<c>see {aids:false}</c>).</summary>
    public bool Aids { get; init; } = true;
    /// <summary>Extra spots to mark with a crosshair and their coordinates (what the question mentions).</summary>
    public IReadOnlyList<(int X, int Y, int Z)> Spots { get; init; } = [];
    /// <summary>Caption line 2 of every 3D panel (staleness of a remote spot); null = time of day only.</summary>
    public string? Stamp { get; init; }
    /// <summary>Camera adjustments per view name ("iso" → yaw/pitch/zoom, "top" → zoom/pan…); views not listed keep their defaults.</summary>
    public IReadOnlyDictionary<string, CamParams>? ViewParams { get; init; }
    /// <summary>The x-ray cut of the outside cameras (<see cref="ViewCut"/>): Auto = only when the body/focus is hidden.</summary>
    public XRay XRay { get; init; } = XRay.Auto;
    /// <summary>Rain/thunder at the spot (darker sky and light, streaks, the caption says it).</summary>
    public Weather Weather { get; init; }
}

/// <summary>
/// Everything the panels of one spot share (a composite can mix spots: each panel carries its own context).
/// </summary>
public sealed class PanelContext
{
    public required IBlockAccess Blocks { get; init; }
    public IVoxelWorld? Light { get; init; }
    public (int X, int Y, int Z) Focus { get; init; }
    public int FeetY { get; init; }
    public int Radius { get; init; } = 6;
    public RoleMap? Roles { get; init; }
    public IReadOnlyList<VisionEntity> Entities { get; init; } = [];
    public IReadOnlyList<VisionMark> Marks { get; init; } = [];
    public TextureAtlas? Atlas { get; init; }
    public long Time { get; init; } = 6000;
    public bool Nether { get; init; }
    public PerspectivePanel.Body? Body { get; init; }
    public (int X, int Y, int Z)? Pointed { get; init; }
    public (int X, int Y, int Z)? Target { get; init; }
    public IReadOnlyList<(int X, int Y, int Z)> Spots { get; init; } = [];
    public bool CutAway { get; init; } = true;
    public bool Aids { get; init; } = true;
    /// <summary>"seen 3 min ago" (remote memory) or null (live).</summary>
    public string? Staleness { get; init; }
    public bool Smooth { get; init; }
    public XRay XRay { get; init; } = XRay.Auto;
    public Weather Weather { get; init; }

    private VisionRenderer.Slab? _slab;
    private IVoxelWorld? _world;

    internal VisionRenderer.Slab Slab => _slab ??= VisionRenderer.Extract(new VisionScene
    {
        Radius = Math.Clamp(Radius, 2, 32), Cx = Focus.X, Cy = FeetY, Cz = Focus.Z, Blocks = Blocks, Roles = Roles, Up = 6, Down = 8, SideUp = 4, SideDown = 5, IsoUp = 6, IsoDown = 8,
    });

    public IVoxelWorld World => _world ??= Light ?? new BlockAccessVoxels(Blocks);

    /// <summary>Entities with the body added (third-person panels draw it).</summary>
    public IReadOnlyList<VisionEntity> WithBody()
    {
        if (Body is not { } b || Entities.Any(e => Math.Abs(e.X - b.X) < 0.4 && Math.Abs(e.Z - b.Z) < 0.4 && Math.Abs(e.Y - b.Y) < 1)) return Entities;
        return [.. Entities, new VisionEntity("self", b.Name ?? "you", b.X, b.Y, b.Z, '@') { Yaw = b.Yaw, Pitch = b.Pitch }];
    }

    public string TimeOfDay => Nether ? "nether" : Lighting.SkylightSubtracted(Time) >= 8 ? "night" : Lighting.SkylightSubtracted(Time) >= 3 ? "dusk" : "day";

    /// <summary>"rain" / "thunderstorm" / "snow" at the focus (its biome and height), null = clear, the Nether, a dry biome.</summary>
    public string? WeatherText => Nether ? null : Weather.Text(MineClaw.Render.Weather.At(World.Biome(Focus.X, Focus.Z), FeetY));

    /// <summary>The body the x-ray keeps in sight: only when it stands near the spot (a remote cam doesn't cut toward it).</summary>
    public (double X, double Y, double Z)? Subject => Body is { } b && Math.Abs(b.X - Focus.X - 0.5) <= Radius + 1 && Math.Abs(b.Z - Focus.Z - 0.5) <= Radius + 1
        && Math.Abs(b.Y - FeetY) <= 6 ? (b.X, b.Y, b.Z) : null;
}

/// <summary>One panel of a composite: a 3D camera on a context, or a flat map of it.</summary>
public sealed class PanelJob
{
    public required PanelContext Ctx { get; init; }
    /// <summary>The 3D camera; null = a flat map (<see cref="Flat"/>).</summary>
    public PerspectivePanel.Rig? Rig { get; init; }
    /// <summary>"top", "side" or "layers" for a flat map panel.</summary>
    public string? Flat { get; init; }
    public SideAxis Axis { get; init; } = SideAxis.LookNorth;
    /// <summary>Camera adjustments of a flat panel (TOP/SIDE zoom, pan, axis); empty = the default framing.</summary>
    public CamParams Params { get; init; }
    /// <summary>The world the camera sees when it differs from the context's (a clone's eye in its own region).</summary>
    public IVoxelWorld? WorldOverride { get; init; }
}

/// <summary>
/// Renders a contact sheet / composite: textured perspective views from several viewpoints (the body's EYE, over the
/// SHOULDER, ORBITs, a HIGH oblique, free CAMs anywhere the hive remembers, other bodies' eyes, FOLLOW cams) plus small flat
/// maps when asked, side by side in ONE image within a fixed pixel budget (more panels = smaller panels). Every 3D panel is
/// the one <see cref="EyeRenderer"/> (the freecam's renderer): b1.8.1 textures and render types, fluids at their real
/// heights, players and mobs as their models, drops, fire; the reading aids; a caption with the source, compass, time of day
/// and staleness; one shared legend strip. The deterministic facts come grouped per panel. Thread-safe.
/// </summary>
public sealed class ContactSheet
{
    /// <summary>Default: five 3D panels and the TOP map (eye → orbit3 and shoulder → orbit4 when there is no body).</summary>
    public static readonly IReadOnlyList<string> DefaultViews = ["eye", "shoulder", "orbit", "orbit2", "high", "top"];
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "top", "side", "iso", "layers", "eye", "shoulder", "orbit", "orbit2", "orbit3", "orbit4", "high" };
    public const int DefaultPixelBudget = 228_000;
    /// <summary>Most panels in one composite (more would shrink below a readable size: split the request).</summary>
    public const int MaxPanels = 6;
    public const int MinPanelWidth = 120;
    private const int StripH = 11;

    private const int Gap = 4;
    private const uint Bg = 0x07080b, ChipBg = 0x2c313c, Light = 0xe8ecf4;
    private readonly VisionRenderer _renderer;
    private readonly Dictionary<(int, int), EyeRenderer> _eyes = new();
    private readonly object _lock = new();

    public ContactSheet(VisionRenderer? renderer = null) => _renderer = renderer ?? new VisionRenderer { CacheCapacity = 16 };

    public VisionRenderer Panels => _renderer;

    /// <summary>Thread cap of the 3D panels' raycaster (<see cref="EyeRenderer.MaxThreads"/>); background viewers use 1.</summary>
    public int EyeThreads { get; init; } = 8;

    /// <summary>Milliseconds the last sheet took to render (all panels).</summary>
    public double LastMs { get; private set; }

    /// <summary>Grid and panel size (4:3 cells) for <paramref name="count"/> panels within the pixel budget (the legend strip included).</summary>
    public static (int Cols, int Rows, int W, int H) Layout(int count, int pixelBudget = DefaultPixelBudget)
    {
        count = Math.Clamp(count, 1, MaxPanels);
        int cols = count switch { 1 => 1, 2 => 2, 3 => 3, 4 => 2, _ => 3 };
        int rows = (count + cols - 1) / cols;
        double area = pixelBudget / (double)(cols * rows);
        int w = Math.Clamp((int)Math.Sqrt(area * 4 / 3), MinPanelWidth, 560);
        int h = w * 3 / 4 - (StripH + rows - 1) / rows; // the shared legend strip comes out of the panels' height
        return (cols, rows, w, h);
    }

    private EyeRenderer Eye(int w, int h)
    {
        lock (_lock)
        {
            if (!_eyes.TryGetValue((w, h), out var eye))
            {
                if (_eyes.Count > 8) _eyes.Clear();
                _eyes[(w, h)] = eye = new EyeRenderer { Width = w, Height = h, MaxThreads = EyeThreads };
            }
            return eye;
        }
    }

    /// <summary>The classic single-spot sheet: the named views around one focus (the default <c>see</c>).</summary>
    public VisionImage Render(ContactSheetRequest q)
    {
        int r = Math.Clamp(q.Radius, 3, 12);
        int feet = q.FeetY ?? q.Focus.Y;
        var body = BodyOf(q);
        var ctx = new PanelContext
        {
            Blocks = q.Blocks, Light = q.Light, Focus = q.Focus, FeetY = feet, Radius = r, Roles = q.Roles, Entities = q.Entities, Marks = q.Marks,
            Atlas = q.Atlas, Time = q.Time, Nether = q.Nether, Body = body, Pointed = q.Pointed, Target = q.Target, Spots = q.Spots, CutAway = q.CutAway,
            Aids = q.Aids, Staleness = q.Stamp, Smooth = q.Eye?.Smooth ?? false, XRay = q.XRay, Weather = q.Weather,
        };
        var views = new List<string>();
        foreach (var v0 in q.Views.Select(v => v.Trim().ToLowerInvariant()).Where(Known.Contains))
        {
            var v = v0;
            if (v == "eye" && q.Eye == null) v = "orbit3";
            if (v == "shoulder" && body == null) v = "orbit4";
            if (!views.Contains(v)) views.Add(v);
        }
        if (views.Count == 0) views = DefaultViews.Where(v => v != "eye" || q.Eye != null).ToList();
        var jobs = new List<PanelJob>();
        foreach (var v in views.Take(MaxPanels))
        {
            var cam = q.ViewParams != null && q.ViewParams.TryGetValue(v, out var vp) ? vp : default;
            if (PerspectivePanel.Views.Contains(v))
            {
                if (PerspectivePanel.For(v, q.Focus, feet, r, body, q.Eye, cam) is not { } rig) continue;
                jobs.Add(new PanelJob { Ctx = ctx, Rig = rig, WorldOverride = v == "eye" && q.Eye is { } e && !ReferenceEquals(e.World, ctx.World) ? e.World : null });
            }
            else jobs.Add(new PanelJob { Ctx = ctx, Flat = v, Axis = q.Axis, Params = cam });
        }
        var head = string.Create(CultureInfo.InvariantCulture, $"see sheet focus {q.Focus.X},{q.Focus.Y},{q.Focus.Z} r{r}");
        var extra = new List<string>();
        if (q.Self is { } me) extra.Add(string.Create(CultureInfo.InvariantCulture, $"@=you {me.X},{me.Y},{me.Z} (orange tag)."));
        if (q.Pointed is { } pt) extra.Add(string.Create(CultureInfo.InvariantCulture, $"Pink=pointed {pt.X},{pt.Y},{pt.Z}{(q.PointedBy != null ? " by " + q.PointedBy : "")}."));
        if (PerspectivePanel.DropsText(q.Entities, q.Focus) is { } drops) extra.Add(drops + ".");
        return Compose(jobs, q.PixelBudget, head, extra, q.Self);
    }

    /// <summary>
    /// Renders panels (any mix of spots and cameras) into one image within <paramref name="pixelBudget"/>: a grid of equal
    /// cells, captions, the shared legend strip, and the legend text with the facts grouped per panel.
    /// </summary>
    public VisionImage Compose(IReadOnlyList<PanelJob> jobs, int pixelBudget, string head, IReadOnlyList<string>? extra = null, (int X, int Y, int Z)? self = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        jobs = jobs.Take(MaxPanels).ToList();
        var (cols, rows, pw, ph) = Layout(Math.Max(1, jobs.Count), pixelBudget);
        var panels = new List<(PanelJob Job, int W, int H, uint[] Px, string Legend, List<string> Facts, string Caption)>();
        bool xray = false;
        ulong hash = 14695981039346656037UL;
        PerspectivePanel.Input InputFor(PanelJob job, PerspectivePanel.Rig rig)
        {
            var ctx = job.Ctx;
            return new PerspectivePanel.Input
            {
                World = job.WorldOverride ?? ctx.World, Atlas = ctx.Atlas, Time = ctx.Time, Nether = ctx.Nether, Entities = ctx.WithBody(), Marks = ctx.Marks,
                Focus = ctx.Focus, Pointed = ctx.Pointed, Target = ctx.Target, Slab = ctx.Slab, Smooth = ctx.Smooth,
                Cut = ctx.CutAway && rig.Cut && job.WorldOverride == null ? PerspectivePanel.Cut(ctx.Slab, ctx.FeetY) : null,
                Aids = ctx.Aids ? new ReadingAids.Input { Focus = ctx.Focus, FeetY = ctx.FeetY, Radius = ctx.Radius, Spots = ctx.Spots, Body = ctx.Body, Slab = ctx.Slab, Rings = rig.Ortho == 0 } : null,
                Stamp = ctx.TimeOfDay + (ctx.WeatherText is { } wt ? ", " + wt : "") + (ctx.Staleness != null ? ", " + ctx.Staleness : ""),
                XRay = job.WorldOverride == null && ctx.CutAway ? ctx.XRay : XRay.Off, Subject = ctx.Subject, FeetY = ctx.FeetY, Weather = ctx.Nether ? default : ctx.Weather, // design previews (no cut-away) stay exact
            };
        }
        foreach (var job in jobs)
        {
            var ctx = job.Ctx;
            unchecked { hash = (hash ^ ctx.Slab.Hash) * 1099511628211UL; }
            if (job.Flat == "layers")
            {
                // Two orthographic slices side by side: the feet layer and the one below, each seen from straight above.
                int hw = (pw - Gap) / 2;
                var left = PerspectivePanel.Layer(ctx.Focus, ctx.FeetY, ctx.Radius, $"FEET Y{ctx.FeetY}");
                var right = PerspectivePanel.Layer(ctx.Focus, ctx.FeetY - 1, ctx.Radius, $"BELOW Y{ctx.FeetY - 1}");
                var a = PerspectivePanel.Render(Eye(hw, ph), left, InputFor(job, left), out _, out var fa);
                var b = PerspectivePanel.Render(Eye(hw, ph), right, InputFor(job, right), out _, out var fb);
                var px = new uint[pw * ph];
                Array.Fill(px, Bg);
                for (int y = 0; y < ph; y++)
                {
                    Array.Copy(a.Pixels!, y * hw, px, y * pw, hw);
                    Array.Copy(b.Pixels!, y * hw, px, y * pw + hw + Gap, hw);
                }
                panels.Add((job, pw, ph, px, $"LAYERS (left: only y{ctx.FeetY} = blocks AT feet height from above, a solid cell there is a wall beside you, not floor; right: only y{ctx.FeetY - 1}, what you stand on)",
                    fa.Concat(fb).Distinct().ToList(), "LAYERS"));
                continue;
            }
            var rig = job.Rig ?? (job.Flat == "side" ? PerspectivePanel.Side(ctx.Focus, ctx.FeetY, ctx.Radius, job.Axis, job.Params) : PerspectivePanel.Top(ctx.Focus, ctx.FeetY, ctx.Radius, job.Params));
            var img = PerspectivePanel.Render(Eye(pw, ph), rig, InputFor(job, rig), out _, out var facts);
            string legendText = job.Flat == "top" && ctx.CutAway ? rig.Legend[..^1] + ", roofs above it cut away)" : img.Legend;
            xray |= img.XRay;
            panels.Add((job, img.Width, img.Height, img.Pixels!, legendText, facts, rig.Title));
            unchecked { hash = (hash ^ (ulong)HashCode.Combine(Math.Round(rig.X, 1), Math.Round(rig.Y, 1), Math.Round(rig.Z, 1), rig.Yaw, rig.Pitch)) * 1099511628211UL; }
        }

        int W = cols * pw + Gap * (cols - 1);
        int usedRows = (panels.Count + cols - 1) / cols;
        int H = Math.Max(1, usedRows) * (ph + Gap) + StripH;
        var c = new Canvas(W, H, Bg);
        for (int i = 0; i < panels.Count; i++)
        {
            var (job, iw, ih, px, _, _, _) = panels[i];
            int ox = (i % cols) * (pw + Gap) + Math.Max(0, (pw - iw) / 2), oy = (i / cols) * (ph + Gap) + Math.Max(0, (ph - ih) / 2);
            for (int y = 0; y < Math.Min(ih, ph); y++)
                for (int x = 0; x < Math.Min(iw, pw); x++) c.Set(ox + x, oy + y, px[y * iw + x]);
        }
        LegendStrip(c, 0, H - StripH, W);

        // Legend text: header, panels (with their facts), shared notes.
        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"{head}, {W}x{H}px ~{(W * H + 749) / 750}tok; panels L>R, top>bottom:");
        var told = new HashSet<string>();
        for (int i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            legend.Append(CultureInfo.InvariantCulture, $" [{i + 1}] {p.Legend}");
            if (p.Job.Ctx.Staleness != null && p.Job.Rig != null) legend.Append(" (").Append(p.Job.Ctx.Staleness).Append(')');
            // Each label once: a later panel lists only what the earlier ones didn't.
            var fresh = p.Facts.Distinct().Where(told.Add).Take(8).ToList();
            if (fresh.Count > 0) legend.Append(": ").Append(string.Join("; ", fresh));
            legend.Append('.');
        }
        legend.Append(" Cyan box = focus in every panel.");
        legend.Append(" 3D = the game's view; caption = viewpoint >facing, time/staleness. Grid 1 block, every 5th labelled X/Z; gold Y ruler; crosshair + x,y,z on key blocks; rings 2/4/8 blocks from you; N/E/S/W gizmo.");
        var roles = new HashSet<string>();
        foreach (var p in panels)
        {
            var slab = p.Job.Ctx.Slab;
            if (slab.Roles == null) continue;
            for (int i = 0; i < slab.Roles.Length; i++) if (PerspectivePanel.Shown((BlockRole)slab.Roles[i])) roles.Add(BlockRoles.Name((BlockRole)slab.Roles[i]));
        }
        if (roles.Count > 0) legend.Append(" Roles (3D tint/outline, TOP letters): ").Append(string.Join(", ", roles)).Append('.');
        if (panels.Any(p => p.Job.Ctx.CutAway && p.Job.Rig is { Cut: true })) legend.Append(" Cut-away: roofs over the focus layer cut away in ORBIT/HIGH/ISO and TOP.");
        if (xray) legend.Append(" X-ray: rock/walls between the camera and you or the focus are removed in the panels that say x-ray; hatched slate faces are cut edges, not real walls.");
        var weather = panels.Select(p => p.Job.Ctx.WeatherText).FirstOrDefault(t => t != null);
        if (weather != null) legend.Append(" Weather: ").Append(weather).Append(" (darker sky and light; streaks are rain/snow, not blocks).");
        if (extra != null) foreach (var e in extra) legend.Append(' ').Append(e);
        legend.Append(" Dark below the island = void.");
        var labels = panels.SelectMany(p => p.Facts).Distinct().Take(12).ToList();
        LastMs = sw.Elapsed.TotalMilliseconds;
        ulong key = hash;
        unchecked
        {
            foreach (var p in panels)
                foreach (var e in p.Job.Ctx.Entities) key = (key ^ (ulong)HashCode.Combine(e.Kind, e.Name, Math.Round(e.X, 1), Math.Round(e.Y, 1), Math.Round(e.Z, 1), e.Yaw, e.Burning)) * 1099511628211UL;
            foreach (char ch in head) key = (key ^ ch) * 1099511628211UL;
        }
        return new VisionImage(Png.Encode(c.Px, W, H), W, H, key, legend.ToString(), labels) { Pixels = c.Px, BlocksHash = hash, XRay = xray, Weather = weather };
    }

    /// <summary>The one legend strip of a sheet: what the colours and outlines mean.</summary>
    private static void LegendStrip(Canvas c, int x0, int y0, int w)
    {
        c.Fill(x0, y0, w, StripH, 0x101318);
        int x = x0 + 3;
        void Item(uint col, string text)
        {
            if (x + 8 + PixelFont.Width(text) > x0 + w) return;
            c.Fill(x, y0 + 2, 6, 7, col);
            c.Text(x + 8, y0 + 2, text, 0xc8cfdc);
            x += 8 + PixelFont.Width(text) + 6;
        }
        Item(0x5fe8ff, "FOCUS");
        Item(BlockRoles.Colour(BlockRole.GenCell), "GEN CELL");
        Item(BlockRoles.Colour(BlockRole.OwnPlaced), "YOURS");
        Item(BlockRoles.Colour(BlockRole.PlayerBuild), "PLAYER BUILD");
        Item(BlockRoles.Colour(BlockRole.NeverBreak), "NEVER BREAK");
        Item(BlockRoles.Colour(BlockRole.Planned), "PLANNED");
        Item(0xffd84a, "Y RULER");
        Item(0xd8dee9, "GRID 1/5");
        Item(0xff9f43, "RINGS 2/4/8");
    }

    /// <summary>Where the body is: the explicit pose, else the eye pose (when it is the bot's own), else the feet block.</summary>
    private static PerspectivePanel.Body? BodyOf(ContactSheetRequest q)
    {
        if (q.SelfPose is { } p) return p;
        if (q.Eye is { } e && (q.Self is null || Math.Abs(e.EyeX - (q.Self.Value.X + 0.5)) < 1.5 && Math.Abs(e.EyeZ - (q.Self.Value.Z + 0.5)) < 1.5))
            return new PerspectivePanel.Body(e.EyeX, e.EyeY - 1.62, e.EyeZ, e.Yaw, e.Pitch, e.Viewer);
        if (q.Self is { } s)
        {
            var (yaw, _) = EyeScene.LookAt(s.X + 0.5, s.Y + 1.62, s.Z + 0.5, q.Focus.X + 0.5, q.Focus.Y + 0.5, q.Focus.Z + 0.5);
            return new PerspectivePanel.Body(s.X + 0.5, s.Y, s.Z + 0.5, yaw, 20, null);
        }
        return null;
    }

    /// <summary>Tokens of the same panels sent as separate default-size pictures (the old way), for the token report.</summary>
    public static int SeparateTokens(IEnumerable<VisionImage> images) => images.Sum(i => i.Tokens);

    private static IEnumerable<string> RoleLetters(string legend)
    {
        int i = legend.IndexOf("Roles: ", StringComparison.Ordinal);
        if (i < 0) yield break;
        var rest = legend[(i + 7)..];
        int end = rest.IndexOf('.');
        if (end >= 0) rest = rest[..end];
        foreach (var part in rest.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return part;
    }
}
