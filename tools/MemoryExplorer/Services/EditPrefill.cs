using System.Globalization;
using MineClaw.Agent.Admin;

namespace MineClaw.MemoryExplorer.Services;

/// <summary>Maps a browser row to the edit form's starting values and the record id the admin backend expects.</summary>
public static class EditPrefill
{
    public static string? IdOf(string kind, MemoryRow row) => MemoryKinds.Normalize(kind) switch
    {
        MemoryKinds.Lesson or MemoryKinds.Place or MemoryKinds.Structure => row.Id,
        _ => row.Id,
    };

    public static Dictionary<string, string?> For(string kind, MemoryRow row)
    {
        var p = row.Props.GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        string? P(string k) => p.TryGetValue(k, out var v) ? v : null;
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (MemoryKinds.Normalize(kind) is { } k && Fills.TryGetValue(k, out var fill)) fill(d, row, P);
        return d;
    }

    /// <summary>Fills the form's fields for one kind from the row (<c>p</c> reads a detail property; null = absent).</summary>
    private delegate void Fill(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p);

    private static readonly Dictionary<string, Fill> Fills = new(StringComparer.Ordinal)
    {
        [MemoryKinds.Fact] = FillFact,
        [MemoryKinds.Place] = FillPlace,
        [MemoryKinds.Structure] = FillStructure,
        [MemoryKinds.Claim] = FillClaim,
        [MemoryKinds.Trait] = FillTrait,
        [MemoryKinds.Lesson] = FillLesson,
        [MemoryKinds.Observation] = FillObservation,
    };

    private static string DimOf(MemoryRow row) => row.Dimension == -1 ? "nether" : "overworld";

    private static string? ConfidenceOf(MemoryRow row) => row.Confidence?.ToString("0.00", CultureInfo.InvariantCulture);

    private static void FillFact(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        d["subject"] = row.Name;
        d["text"] = row.Text;
    }

    private static void FillPlace(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        d["name"] = row.Name;
        d["x"] = p("x"); d["y"] = p("y"); d["z"] = p("z");
        d["dim"] = DimOf(row);
        d["note"] = p("note") ?? "";
    }

    /// <summary>A structure's text is its note plus "• " step lines.</summary>
    private static void FillStructure(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        var lines = row.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        d["name"] = row.Name;
        d["kind"] = row.Type;
        d["note"] = string.Join("\n", lines.Where(l => !l.StartsWith("• ", StringComparison.Ordinal)));
        d["confidence"] = ConfidenceOf(row);
        d["steps"] = string.Join("\n", lines.Where(l => l.StartsWith("• ", StringComparison.Ordinal)).Select(l => l[2..]));
        d["min"] = p("min") ?? "";
        d["max"] = p("max") ?? "";
        d["dim"] = DimOf(row);
    }

    private static void FillClaim(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        d["text"] = row.Text;
        d["claimant"] = p("claimant");
        d["status"] = row.State;
        d["confidence"] = ConfidenceOf(row);
        d["reason"] = p("reason") ?? "";
    }

    private static void FillTrait(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        d["section"] = row.Type;
        d["text"] = row.Text;
        d["player"] = p("player") ?? "";
        d["reason"] = "";
    }

    private static void FillLesson(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        d["title"] = row.Name;
        d["correction"] = row.Text;
        d["patterns"] = "";
    }

    /// <summary>An observation's text may end in "\nannotation: …" (the annotation field).</summary>
    private static void FillObservation(Dictionary<string, string?> d, MemoryRow row, Func<string, string?> p)
    {
        var text = row.Text;
        string annotation = "";
        int i = text.IndexOf("\nannotation: ", StringComparison.Ordinal);
        if (i >= 0) { annotation = text[(i + "\nannotation: ".Length)..]; text = text[..i]; }
        d["text"] = text;
        d["annotation"] = annotation;
    }

    /// <summary>Only the fields the user changed (the backend keeps absent fields; "" clears an optional one).</summary>
    public static Dictionary<string, string?> Changed(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var b) || (b ?? "") != (kv.Value ?? ""))
             .ToDictionary(kv => kv.Key, kv => kv.Value);
}
