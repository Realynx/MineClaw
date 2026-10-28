using Avalonia.Media;

namespace MineClaw.MemoryExplorer.Controls;

/// <summary>
/// Graph colours per theme. Categorical hues in a fixed, CVD-validated order (never cycled: the cluster view colours the 7 largest
/// roots and folds the rest into a neutral "other"); time is one blue ramp, old → new.
/// </summary>
public sealed class GraphPalette
{
    private static readonly Color[] CatLight = [C("#2a78d6"), C("#eb6834"), C("#1baf7a"), C("#eda100"), C("#e87ba4"), C("#008300"), C("#6250d6"), C("#e34948")];
    private static readonly Color[] CatDark = [C("#3987e5"), C("#d95926"), C("#199e70"), C("#c98500"), C("#d55181"), C("#008300"), C("#9085e9"), C("#e66767")];

    public GraphPalette(bool dark)
    {
        Dark = dark;
        Categorical = dark ? CatDark : CatLight;
        Other = dark ? C("#5b6170") : C("#b4b9c6");
        TimeOld = dark ? C("#184f95") : C("#b7d3f6");
        TimeNew = dark ? C("#9ec5f4") : C("#184f95");
    }

    public bool Dark { get; }
    public Color[] Categorical { get; }
    public Color Other { get; }
    public Color TimeOld { get; }
    public Color TimeNew { get; }

    public const int ClusterSlots = 7;

    public static readonly string[] CategoryNames = ["episode", "activity", "fact", "recipe", "chat", "world", "self"];

    public Color Category(MemoryCategory c) => Categorical[(int)c];

    public Color Time(double t) => Mix(TimeOld, TimeNew, Math.Clamp(t, 0, 1));

    /// <summary>Hue of a root cluster by its rank among the roots (largest first); past the slots, neutral.</summary>
    public Color Cluster(int rank) => rank >= 0 && rank < ClusterSlots ? Categorical[rank] : Other;

    /// <summary>A row-dependent tint so nested bands of one hue stay distinguishable: deeper rows lean toward the surface.</summary>
    public Color Depth(Color c, int row, int rows)
    {
        double t = rows <= 1 ? 0 : (double)row / (rows - 1) * 0.28;
        return Mix(c, Dark ? Color.FromRgb(28, 31, 39) : Colors.White, t);
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    private static Color C(string hex) => Color.Parse(hex);
}
