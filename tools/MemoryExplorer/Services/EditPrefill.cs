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
        string Dim() => row.Dimension == -1 ? "nether" : "overworld";
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (MemoryKinds.Normalize(kind))
        {
            case MemoryKinds.Fact:
                d["subject"] = row.Name;
                d["text"] = row.Text;
                break;
            case MemoryKinds.Place:
                d["name"] = row.Name;
                d["x"] = P("x"); d["y"] = P("y"); d["z"] = P("z");
                d["dim"] = Dim();
                d["note"] = P("note") ?? "";
                break;
            case MemoryKinds.Structure:
            {
                var lines = row.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                d["name"] = row.Name;
                d["kind"] = row.Type;
                d["note"] = string.Join("\n", lines.Where(l => !l.StartsWith("• ", StringComparison.Ordinal)));
                d["confidence"] = row.Confidence?.ToString("0.00", CultureInfo.InvariantCulture);
                d["steps"] = string.Join("\n", lines.Where(l => l.StartsWith("• ", StringComparison.Ordinal)).Select(l => l[2..]));
                d["min"] = P("min") ?? "";
                d["max"] = P("max") ?? "";
                d["dim"] = Dim();
                break;
            }
            case MemoryKinds.Claim:
                d["text"] = row.Text;
                d["claimant"] = P("claimant");
                d["status"] = row.State;
                d["confidence"] = row.Confidence?.ToString("0.00", CultureInfo.InvariantCulture);
                d["reason"] = P("reason") ?? "";
                break;
            case MemoryKinds.Trait:
                d["section"] = row.Type;
                d["text"] = row.Text;
                d["player"] = P("player") ?? "";
                d["reason"] = "";
                break;
            case MemoryKinds.Lesson:
                d["title"] = row.Name;
                d["correction"] = row.Text;
                d["patterns"] = "";
                break;
            case MemoryKinds.Observation:
            {
                var text = row.Text;
                string annotation = "";
                int i = text.IndexOf("\nannotation: ", StringComparison.Ordinal);
                if (i >= 0) { annotation = text[(i + "\nannotation: ".Length)..]; text = text[..i]; }
                d["text"] = text;
                d["annotation"] = annotation;
                break;
            }
        }
        return d;
    }

    /// <summary>Only the fields the user changed (the backend keeps absent fields; "" clears an optional one).</summary>
    public static Dictionary<string, string?> Changed(IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var b) || (b ?? "") != (kv.Value ?? ""))
             .ToDictionary(kv => kv.Key, kv => kv.Value);
}
