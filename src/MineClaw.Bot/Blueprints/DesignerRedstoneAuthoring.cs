using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// A redstone design as it is saved (docs/web/api.md "Design Studio"): a circuit drawn on a build plot. Its DSL is a build's; only
/// the comment line <c># kind redstone</c> says what it is, because the DSL's <c>kind</c> statement knows build and mine and
/// older code must still parse the design (a comment is nothing to the parser). The bridge reads the same line
/// (tools/ViewerBridge/Data/DesignViews.cs): keep the two in step.
/// </summary>
public static partial class RedstoneDesign
{
    /// <summary>The tag, written as the DSL's first line.</summary>
    public const string KindLine = "# kind redstone\n";

    // The whole line: the two words in a design's name or inside another comment are not the tag.
    [GeneratedRegex(@"^[ \t]*#[ \t]*kind[ \t]+redstone[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex KindRx();

    /// <summary>A redstone design's DSL (its <c># kind redstone</c> line).</summary>
    public static bool IsRedstone(string? dsl) => dsl != null && KindRx().IsMatch(dsl);

    /// <summary>A circuit's drawn DSL under its tag and, when it has editor data, that data's marker.</summary>
    public static string Tagged(string dsl, string? authoring) =>
        KindLine + (authoring == null ? "" : DesignerRedstoneAuthoring.Marker(authoring)) + dsl;

    /// <summary>
    /// <paramref name="next"/> as the new DSL of a design whose DSL was <paramref name="previous"/>: tagged again when that was a
    /// circuit and the new text lost the line (a drawing of a revision never writes it; a revised design may drop it). Without
    /// this a commit, a revert or a revision would silently turn a circuit back into a build, which the builder would then build.
    /// The editor data's marker comes along with the tag, so the new revision still names the data it was drawn with
    /// (<see cref="DesignerStore.RedstoneAuthoringOf"/> finds that data by it).
    /// </summary>
    public static string Carry(string? previous, string next) =>
        IsRedstone(next) || !IsRedstone(previous) ? next : KindLine + (LayoutLineOf(previous) ?? "") + next;

    // The marker line DesignerRedstoneAuthoring.Marker writes.
    [GeneratedRegex(@"^# redstone layout [0-9a-f]{64}$", RegexOptions.Multiline)]
    private static partial Regex LayoutRx();

    /// <summary>The DSL's editor-data marker line (with its newline), or null when it has none.</summary>
    public static string? LayoutLineOf(string? dsl) => dsl != null && LayoutRx().Match(dsl) is { Success: true } m ? m.Value + "\n" : null;
}

/// <summary>
/// A redstone design's editor data (placed components, probes, breakpoints, simulator settings): one opaque JSON text per saved
/// revision (<see cref="DesignerStore.SaveRedstoneAuthoring"/>). The circuit itself is the design's cells; the browser checks this
/// data against them before it restores anything.
/// </summary>
public static class DesignerRedstoneAuthoring
{
    public const int MaxLength = 2_000_000;

    /// <summary>A short DSL marker: an edit of the data alone (a probe moved, a breakpoint added) is then a revision of its own,
    /// without copying editor data into the plan.</summary>
    public static string Marker(string data) => "# redstone layout " + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data))) + "\n";

    /// <summary>None (null), or a JSON object marked <c>"redstone": 1</c> of at most <see cref="MaxLength"/> characters. What else it
    /// holds is the studio's own.</summary>
    public static bool Valid(string? data)
    {
        if (data == null) return true;
        if (data.Length > MaxLength) return false;
        try
        {
            using var doc = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("redstone", out var mark)
                && mark.ValueKind == JsonValueKind.Number && mark.TryGetInt32(out var n) && n == 1;
        }
        catch (JsonException) { return false; }
    }
}
