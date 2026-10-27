using MineClaw.World;

namespace MineClaw.Render;

/// <summary>Projection of a <c>see</c> picture.</summary>
public enum VisionView { Top, Side, Iso }

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
}

/// <summary>A rendered picture: PNG bytes, size, the API's token estimate (w·h/750) and a short text legend.</summary>
public sealed record VisionImage(byte[] Png, int Width, int Height, ulong Key, string Legend, IReadOnlyList<string> Labels)
{
    public int Tokens => (Width * Height + 749) / 750;
}
