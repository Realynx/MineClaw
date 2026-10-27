using System.Globalization;

namespace MineClaw.Agent.Rag;

/// <summary>
/// b1.8.1 dimensions (0 overworld, -1 nether; there is no End) and the helpers every memory reader/writer shares:
/// parsing the model's <c>dim</c> argument, the tiny "(overworld)" render tag, and the 1:8 portal coordinate scale.
/// </summary>
public static class WorldDim
{
    public const int Overworld = 0, Nether = -1;
    /// <summary>Reader filter meaning "every dimension" (<c>dim=any</c>).</summary>
    public const int Any = int.MinValue;

    public static string Name(int dim) => dim == Nether ? "nether" : dim == Overworld ? "overworld" : "dim" + dim.ToString(CultureInfo.InvariantCulture);

    /// <summary>The other dimension of b1.8 (portals only connect these two).</summary>
    public static int Other(int dim) => dim == Nether ? Overworld : Nether;

    /// <summary>
    /// The model's <c>dim</c> argument: null/""/here/current → <paramref name="current"/>; overworld|ow|0; nether|-1;
    /// any|all|both → <see cref="Any"/>. False for anything else.
    /// </summary>
    public static bool TryParse(string? s, int current, out int dim)
    {
        dim = current;
        var t = s?.Trim().ToLowerInvariant();
        switch (t)
        {
            case null or "" or "here" or "current" or "this": return true;
            case "overworld" or "ow" or "world" or "surface" or "0": dim = Overworld; return true;
            case "nether" or "hell" or "-1": dim = Nether; return true;
            case "any" or "all" or "both" or "*": dim = Any; return true;
            default: return false;
        }
    }

    /// <summary>" (overworld)" when a thing lives in another dimension than <paramref name="current"/>, else "".</summary>
    public static string Tag(int dim, int current) => dim == current ? "" : " (" + Name(dim) + ")";

    /// <summary>True when a row of <paramref name="dim"/> passes the reader filter (<see cref="Any"/> passes everything).</summary>
    public static bool Matches(int dim, int filter) => filter == Any || dim == filter;

    // ---------------- 1:8 portal scale (y unchanged) ----------------

    public static int ToNether(int v) => (int)Math.Floor(v / 8.0);
    public static int ToOverworld(int v) => v * 8;

    public static (int X, int Y, int Z) ToNether(int x, int y, int z) => (ToNether(x), y, ToNether(z));
    public static (int X, int Y, int Z) ToOverworld(int x, int y, int z) => (ToOverworld(x), y, ToOverworld(z));

    /// <summary>Converts block coordinates between dimensions (identity when <paramref name="from"/> == <paramref name="to"/>).</summary>
    public static (int X, int Y, int Z) Convert(int x, int y, int z, int from, int to) =>
        from == to ? (x, y, z) : to == Nether ? ToNether(x, y, z) : ToOverworld(x, y, z);

    /// <summary>Horizontal distance scale of one block of <paramref name="dim"/> in overworld blocks (nether: 8).</summary>
    public static double Scale(int dim) => dim == Nether ? 8 : 1;
}
