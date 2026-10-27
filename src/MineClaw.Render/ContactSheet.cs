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
    /// <summary>Panels in order: top, side, iso, layers, eye. Unknown names are ignored.</summary>
    public IReadOnlyList<string> Views { get; init; } = ContactSheet.DefaultViews;
    /// <summary>Feet level the panels are built around (default: the focus y, or the bot's feet when it stands in the area).</summary>
    public int? FeetY { get; init; }
    public (int X, int Y, int Z)? Self { get; init; }
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
    /// <summary>Pixel budget of the whole sheet (≈ tokens × 750). 300k px ≈ 400 tokens.</summary>
    public int PixelBudget { get; init; } = 300_000;
}

/// <summary>
/// Renders a contact sheet: every panel shows the same focus (cyan box/ring), the same compass and coordinate ticks with the
/// focus coordinates in yellow, and the role layer (G gen cell = MINE HERE, P portal, X never-break...). One image of
/// ≈300-400 tokens replaces 3-4 separate pictures (≈200 tokens each plus per-image overhead), so the model gets several
/// angles of one spot in one look. Thread-safe; panels are cached by <see cref="VisionRenderer"/>.
/// </summary>
public sealed class ContactSheet
{
    public static readonly IReadOnlyList<string> DefaultViews = ["top", "side", "iso", "layers"];
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "top", "side", "iso", "layers", "eye" };

    private const int Gap = 4;
    private const uint Bg = 0x07080b, ChipBg = 0x2c313c, Light = 0xe8ecf4;
    private readonly VisionRenderer _renderer;
    private readonly Dictionary<(int, int), EyeRenderer> _eyes = new();
    private readonly object _lock = new();

    public ContactSheet(VisionRenderer? renderer = null) => _renderer = renderer ?? new VisionRenderer { CacheCapacity = 16 };

    public VisionRenderer Panels => _renderer;

    /// <summary>Panel side in pixels for <paramref name="count"/> panels within the pixel budget (≤290).</summary>
    public static (int Cols, int Rows, int Side) Layout(int count, int pixelBudget = 300_000)
    {
        count = Math.Max(1, count);
        int cols = count switch { 1 => 1, 2 => 2, 3 => 3, 4 => 2, _ => 3 };
        int rows = (count + cols - 1) / cols;
        int side = Math.Min(290, (int)Math.Sqrt(pixelBudget / (double)(cols * rows)));
        return (cols, rows, Math.Max(120, side));
    }

    public VisionImage Render(ContactSheetRequest q)
    {
        var views = q.Views.Select(v => v.Trim().ToLowerInvariant()).Where(Known.Contains).Distinct().ToList();
        if (!views.Contains("eye") || q.Eye == null) views.Remove("eye");
        if (views.Count == 0) views = DefaultViews.ToList();
        int r = Math.Clamp(q.Radius, 3, 12), n = 2 * r + 1;
        var (cols, rows, side) = Layout(views.Count, q.PixelBudget);
        int feet = q.FeetY ?? q.Focus.Y;
        int cs = Math.Clamp((side - 44) / n, 6, 20);
        var panels = new List<(string View, VisionImage Img, uint[] Px)>();
        ulong hash = 14695981039346656037UL;
        foreach (var v in views)
        {
            VisionImage img;
            if (v == "eye")
            {
                EyeRenderer eye;
                int ew = side, eh = side * 3 / 4;
                lock (_lock)
                    if (!_eyes.TryGetValue((ew, eh), out eye!)) _eyes[(ew, eh)] = eye = new EyeRenderer { Width = ew, Height = eh };
                img = eye.Render(q.Eye! is var e && e.Target == null ? new EyeScene
                {
                    World = e.World, Atlas = e.Atlas, EyeX = e.EyeX, EyeY = e.EyeY, EyeZ = e.EyeZ, Yaw = e.Yaw, Pitch = e.Pitch, Time = e.Time,
                    Nether = e.Nether, Smooth = e.Smooth, Entities = e.Entities, Marks = e.Marks, Target = q.Focus, Pointed = e.Pointed, Viewer = e.Viewer,
                } : q.Eye!);
            }
            else
            {
                var scene = new VisionScene
                {
                    View = v switch { "side" => VisionView.Side, "iso" => VisionView.Iso, "layers" => VisionView.Layers, _ => VisionView.Top },
                    Radius = r, Cx = q.Focus.X, Cy = feet, Cz = q.Focus.Z, Blocks = q.Blocks, Self = q.Self, Entities = q.Entities, Marks = q.Marks,
                    Target = q.Target, Pointed = q.Pointed, PointedBy = q.PointedBy, Nether = q.Nether, Atlas = q.Atlas, Light = q.Light, Time = q.Time,
                    Roles = q.Roles, Focus = q.Focus, CellPx = v == "layers" ? Math.Clamp((side - 34) / (2 * Math.Min(r, 6) * 2 + 2), 6, 12) : cs,
                    Axis = q.Axis, MaxPx = side, Up = 2, Down = 8, SideUp = 4, SideDown = 5, SideDepth = 3, IsoUp = 4, IsoDown = 5,
                };
                img = _renderer.Render(scene);
                unchecked { hash = (hash ^ img.BlocksHash) * 1099511628211UL; }
            }
            panels.Add((v, img, img.Pixels ?? Png.Decode(img.Png).Argb));
        }

        // Grid: columns as wide as their widest panel, rows as tall as their tallest.
        var colW = new int[cols];
        var rowH = new int[rows];
        for (int i = 0; i < panels.Count; i++)
        {
            colW[i % cols] = Math.Max(colW[i % cols], panels[i].Img.Width);
            rowH[i / cols] = Math.Max(rowH[i / cols], panels[i].Img.Height);
        }
        int W = colW.Sum() + Gap * (cols - 1), H = rowH.Sum() + Gap * (rows - 1);
        var c = new Canvas(W, H, Bg);
        for (int i = 0; i < panels.Count; i++)
        {
            int ox = colW.Take(i % cols).Sum() + Gap * (i % cols), oy = rowH.Take(i / cols).Sum() + Gap * (i / cols);
            var (view, img, px) = panels[i];
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++) c.Set(ox + x, oy + y, px[y * img.Width + x]);
            // Panel name chip (top-left corner; the top/layers/side/iso panels keep that corner free).
            var title = view switch { "top" => "TOP", "iso" => "ISO", "eye" => "EYE" + (q.Eye?.Viewer is { } who ? " " + Short(who) : ""), _ => null };
            if (title != null) c.Chip(ox, oy, title, Light, ChipBg, 1f);
        }

        var legend = new StringBuilder();
        legend.Append(CultureInfo.InvariantCulture, $"see sheet focus {q.Focus.X},{q.Focus.Y},{q.Focus.Z} r{r}, {W}x{H}px ~{(W * H + 749) / 750}tok; panels L>R, top>bottom: ");
        legend.Append(string.Join(", ", panels.Select(p => p.View switch
        {
            "top" => "TOP (N up, 1 cell=1 block, bevel=wall at feet y" + feet + ")",
            "side" => q.Axis == SideAxis.LookEast ? $"SIDE looking E through x{q.Focus.X} (z bottom, y left)" : $"SIDE looking N through z{q.Focus.Z} (x bottom, y left)",
            "iso" => "ISO (corner letters = compass)",
            "layers" => $"LAYERS (left: blocks AT feet height y{feet}, a solid cell there is a wall beside you, not floor; right: y{feet - 1}, what you stand on; green dot = air with a side/top face to place against)",
            _ => "EYE (first person" + (q.Eye?.Viewer is { } v2 ? " of " + v2 : "") + ")",
        })));
        legend.Append(". Cyan box/ring = focus in every panel, yellow ticks = its coords.");
        if (q.Self is { } me) legend.Append(CultureInfo.InvariantCulture, $" @=you {me.X},{me.Y},{me.Z}.");
        if (q.Pointed is { } pt) legend.Append(CultureInfo.InvariantCulture, $" Pink=pointed {pt.X},{pt.Y},{pt.Z}{(q.PointedBy != null ? " by " + q.PointedBy : "")}.");
        var roles = panels.SelectMany(p => RoleLetters(p.Img.Legend)).Distinct().ToList();
        if (roles.Count > 0) legend.Append(" Roles: ").Append(string.Join(", ", roles)).Append('.');
        legend.Append(" Navy hatch = void.");
        var labels = panels.SelectMany(p => p.Img.Labels).Distinct().Take(8).ToList();
        ulong key = hash;
        unchecked
        {
            foreach (char ch in string.Join("|", views) + q.Self + q.Pointed + q.Focus + q.Axis) key = (key ^ ch) * 1099511628211UL;
        }
        return new VisionImage(Png.Encode(c.Px, W, H), W, H, key, legend.ToString(), labels) { Pixels = c.Px, BlocksHash = hash };
    }

    /// <summary>Tokens of the same panels sent as separate default-size pictures (the old way), for the token report.</summary>
    public static int SeparateTokens(IEnumerable<VisionImage> images) => images.Sum(i => i.Tokens);

    private static string Short(string name) => name.Length <= 12 ? name : name[..12];

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
