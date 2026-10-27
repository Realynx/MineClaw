using MineClaw.World;

namespace MineClaw.Render;

/// <summary>Projection of a <c>see</c> picture. <see cref="Layers"/>: the feet layer and the layer below, side by side.</summary>
public enum VisionView { Top, Side, Iso, Layers }

/// <summary>Side view direction: looking north (slice along x, the default) or looking east (slice along z).</summary>
public enum SideAxis { LookNorth, LookEast }

/// <summary>A labelled thing from memory or the world index: a box (structure) or a single block (waypoint, chest...).</summary>
/// <param name="Box">True = draw the box outline (structures with an extent).</param>
public sealed record VisionMark(string Text, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ, bool Box = false)
{
    public static VisionMark At(string text, int x, int y, int z) => new(text, x, y, z, x, y, z);
    public double CentreX => (MinX + MaxX) / 2.0;
    public double CentreZ => (MinZ + MaxZ) / 2.0;
}

/// <summary>An entity to draw: <c>player</c>, <c>hive</c> (a fellow bot), <c>mob</c> or <c>item</c>.</summary>
public sealed record VisionEntity(string Kind, string? Name, double X, double Y, double Z, char? Glyph = null);

/// <summary>Everything one <c>see</c> picture shows. Blocks come from an <see cref="IBlockAccess"/> (live + remembered).</summary>
public sealed class VisionScene
{
    public VisionView View { get; init; } = VisionView.Top;
    /// <summary>Horizontal radius in blocks (the picture covers 2R+1 columns).</summary>
    public int Radius { get; init; } = 12;
    /// <summary>Centre block of the picture; <see cref="Cy"/> is the "feet" level (floor = Cy−1).</summary>
    public int Cx { get; init; }
    public int Cy { get; init; }
    public int Cz { get; init; }
    public required IBlockAccess Blocks { get; init; }
    /// <summary>The bot's feet block (drawn as @), if in view.</summary>
    public (int X, int Y, int Z)? Self { get; init; }
    public IReadOnlyList<VisionEntity> Entities { get; init; } = [];
    public IReadOnlyList<VisionMark> Marks { get; init; } = [];
    /// <summary>Highlighted block (current target / asked focus).</summary>
    public (int X, int Y, int Z)? Target { get; init; }
    public IReadOnlyList<(int X, int Y, int Z)> Path { get; init; } = [];
    /// <summary>The latest block a player pointed at (punched), outlined.</summary>
    public (int X, int Y, int Z)? Pointed { get; init; }
    public string? PointedBy { get; init; }
    /// <summary>Iso rotation in quarter turns (0: north up-right).</summary>
    public int Rotation { get; init; }
    public bool Nether { get; init; }
    /// <summary>Top view: columns are scanned from Cy+Up (head level) down to Cy−Down: roofs and door lintels above are cut away.</summary>
    public int Up { get; init; } = 1;
    public int Down { get; init; } = 10;
    /// <summary>Side view: rows Cy+SideUp .. Cy−SideDown, and how many blocks behind the slice show (faded).</summary>
    public int SideUp { get; init; } = 6;
    public int SideDown { get; init; } = 8;
    public int SideDepth { get; init; } = 10;
    /// <summary>b1.8.1 textures; null = flat palette colours.</summary>
    public TextureAtlas? Atlas { get; init; }
    /// <summary>Light source for the textured views (vanilla flat per-face light); null = unlit.</summary>
    public IVoxelWorld? Light { get; init; }
    /// <summary>World time (ticks) for sky-light subtraction.</summary>
    public long Time { get; init; } = 6000;
    /// <summary>Darkest a face gets in the top/iso/side pictures (they are maps: keep them readable at night).</summary>
    public float MinBrightness { get; init; } = 0.35f;
    /// <summary>Iso volume: Cy−IsoDown .. Cy+IsoUp.</summary>
    public int IsoUp { get; init; } = 6;
    public int IsoDown { get; init; } = 8;

    // ---- vision v2 (contact sheet panels)

    /// <summary>Role layer: tinted cells + role letters (G gen cell, P portal, X never-break...). Null = none.</summary>
    public RoleMap? Roles { get; init; }
    /// <summary>The shared focus marker (cyan box with corner ticks) and its coordinates highlighted on the rulers.</summary>
    public (int X, int Y, int Z)? Focus { get; init; }
    /// <summary>Cell size override in pixels (contact sheet panels); null = <see cref="VisionRenderer.CellSize"/>.</summary>
    public int? CellPx { get; init; }
    /// <summary>Side view: looking north (slice along x) or east (slice along z: the work axis runs north-south).</summary>
    public SideAxis Axis { get; init; } = SideAxis.LookNorth;
    /// <summary>Iso: the largest width/height in pixels (the sprite shrinks to fit); null = <see cref="VisionRenderer.MaxSide"/>.</summary>
    public int? MaxPx { get; init; }
    /// <summary>
    /// Top and iso views cut away blocks above the feet layer that hang over air or fluid down to it (a roof over a cobble
    /// generator, a canopy): what sits at feet height is what the bot works on, and a roof must never hide a gen cell.
    /// </summary>
    public bool CutAway { get; init; }
}

/// <summary>A rendered picture: PNG bytes, size, the API's token estimate (w·h/750) and a short text legend.</summary>
public sealed record VisionImage(byte[] Png, int Width, int Height, ulong Key, string Legend, IReadOnlyList<string> Labels)
{
    public int Tokens => (Width * Height + 749) / 750;
    /// <summary>The raw 0xRRGGBB pixels (top/side/iso/layers renders), so the contact sheet pastes panels without decoding.</summary>
    internal uint[]? Pixels { get; init; }
    /// <summary>Hash of the blocks in view (no overlays): "nothing changed here" for auto-vision and the perception cache.</summary>
    public ulong BlocksHash { get; init; }
}
