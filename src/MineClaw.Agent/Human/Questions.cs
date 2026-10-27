using System.Text.RegularExpressions;

namespace MineClaw.Agent.Human;

/// <summary>
/// Players talk relationally ("the one right next to the lava?"), not in coordinates. Questions the bot sends get their
/// "x,y,z" triples replaced by a landmark description, unless the player used coordinates first or asked for them.
/// </summary>
public static partial class Questions
{
    [GeneratedRegex(@"(?<![\w.-])\(?(-?\d{1,6})\s*,\s*(-?\d{1,3})\s*,\s*(-?\d{1,6})\)?(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex CoordsRx();

    [GeneratedRegex(@"\b(?:coords?|coordinates|xyz|x\s*,?\s*y\s*,?\s*z|exact (?:spot|position|block|location)|what (?:pos|position))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AskedForCoordsRx();

    /// <summary>True when any of the player's recent lines has a coordinate triple or asks for coordinates.</summary>
    public static bool PlayerUsesCoords(IEnumerable<string>? recent) =>
        recent != null && recent.Any(l => CoordsRx().IsMatch(l) || AskedForCoordsRx().IsMatch(l));

    public static bool HasCoords(string? text) => text != null && CoordsRx().IsMatch(text);

    /// <summary>
    /// Replaces coordinate triples with <paramref name="landmark"/> descriptions (kept when it has none for a triple, or when
    /// the player used/asked for coordinates).
    /// </summary>
    public static string Humanize(string text, IEnumerable<string>? recentFromPlayer, Func<int, int, int, string?>? landmark)
    {
        if (landmark == null || !HasCoords(text) || PlayerUsesCoords(recentFromPlayer)) return text;
        return CoordsRx().Replace(text, m =>
        {
            string? d = null;
            try { d = landmark(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
            catch (Exception) { }
            return string.IsNullOrWhiteSpace(d) ? m.Value : d.Trim();
        });
    }

    /// <summary>needs_input / results for the brain: keep the coordinates (precise for reasoning) and add the landmark.</summary>
    public static string Annotate(string text, Func<int, int, int, string?>? landmark, int max = 3)
    {
        if (landmark == null || !HasCoords(text)) return text;
        int n = 0;
        return CoordsRx().Replace(text, m =>
        {
            if (n++ >= max) return m.Value;
            string? d = null;
            try { d = landmark(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
            catch (Exception) { }
            return string.IsNullOrWhiteSpace(d) ? m.Value : $"{m.Value} ({d.Trim()})";
        });
    }
}
