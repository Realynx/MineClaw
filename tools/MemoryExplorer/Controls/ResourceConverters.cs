using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MineClaw.MemoryExplorer.Controls;

public static class ResourceConverters
{
    /// <summary>Resource key → the StreamGeometry registered under it (nav icons).</summary>
    public static readonly IValueConverter Geometry = new FuncValueConverter<string?, Geometry?>(key =>
        key != null && Application.Current?.TryFindResource(key, out var v) == true ? v as Geometry : null);

    /// <summary>Claim/observation state → a semantic brush key's brush.</summary>
    public static readonly IValueConverter StateBrush = new FuncValueConverter<string?, IBrush?>(state =>
    {
        string key = state?.ToLowerInvariant() switch
        {
            "verified" or "live" => "GoodBrush",
            "refuted" or "removed" => "BadBrush",
            "disputed" or "provisional" or "unverified" => "WarnBrush",
            _ => "TextMutedBrush",
        };
        return Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var v) == true ? v as IBrush : Brushes.Gray;
    });

    public static readonly IValueConverter Positive = new FuncValueConverter<int, bool>(v => v > 0);

    public static readonly IValueConverter Percent = new FuncValueConverter<double, double>(v => Math.Clamp(v * 100, 0, 100));

    public static readonly IValueConverter LayerWidth = new FuncValueConverter<double, double>(v => Math.Max(4, v * 240));
}

public static class SearchConverters
{
    public static readonly IValueConverter ModeLabel = new FuncValueConverter<bool, string>(lexical => lexical ? "LEXICAL FALLBACK" : "HYBRID · BOT PIPELINE");
}

public static class AuditConverters
{
    public static readonly IValueConverter OkBrush = new FuncValueConverter<bool, IBrush?>(ok =>
        Application.Current?.TryFindResource(ok ? "TextBrush" : "BadBrush", Application.Current.ActualThemeVariant, out var v) == true ? v as IBrush : Brushes.Gray);
}
