using System.Text;

namespace MineClaw.MemoryExplorer.Services;

public enum DiffKind { Same, Added, Removed, Gap }

/// <summary>One line of a version diff: kind, its number in the old and the new version, the text.</summary>
public sealed record DiffLine(DiffKind Kind, int? OldNo, int? NewNo, string Text)
{
    public string Prefix => Kind switch { DiffKind.Added => "+", DiffKind.Removed => "-", DiffKind.Gap => "…", _ => " " };
    public string OldText => OldNo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string NewText => NewNo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public bool IsAdded => Kind == DiffKind.Added;
    public bool IsRemoved => Kind == DiffKind.Removed;
    public bool IsGap => Kind == DiffKind.Gap;
}

/// <summary>
/// Line diff between two script versions (LCS; scripts are ≤ 4,000 chars, so the O(n·m) table is tiny), folded to hunks with
/// context, and as unified-diff text for copying. Whitespace at line ends doesn't count as a change.
/// </summary>
public static class ScriptDiff
{
    /// <summary>Every line of both versions in order: unchanged, removed (old only), added (new only).</summary>
    public static IReadOnlyList<DiffLine> Lines(string? oldText, string? newText)
    {
        var a = Split(oldText);
        var b = Split(newText);
        int n = a.Length, m = b.Length;
        if ((long)n * m > 4_000_000) return [.. a.Select((t, i) => new DiffLine(DiffKind.Removed, i + 1, null, t)), .. b.Select((t, i) => new DiffLine(DiffKind.Added, null, i + 1, t))];
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                lcs[i, j] = Eq(a[i], b[j]) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var list = new List<DiffLine>(n + m);
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (Eq(a[x], b[y])) { list.Add(new DiffLine(DiffKind.Same, x + 1, y + 1, b[y])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { list.Add(new DiffLine(DiffKind.Removed, x + 1, null, a[x])); x++; }
            else { list.Add(new DiffLine(DiffKind.Added, null, y + 1, b[y])); y++; }
        }
        for (; x < n; x++) list.Add(new DiffLine(DiffKind.Removed, x + 1, null, a[x]));
        for (; y < m; y++) list.Add(new DiffLine(DiffKind.Added, null, y + 1, b[y]));
        return list;
    }

    /// <summary>Only the changes with <paramref name="context"/> unchanged lines around them; skipped runs become one Gap line.</summary>
    public static IReadOnlyList<DiffLine> Hunks(IReadOnlyList<DiffLine> lines, int context = 3)
    {
        var keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Kind != DiffKind.Same)
                for (int k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++) keep[k] = true;
        var list = new List<DiffLine>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i]) { list.Add(lines[i]); continue; }
            int start = i;
            while (i + 1 < lines.Count && !keep[i + 1]) i++;
            list.Add(new DiffLine(DiffKind.Gap, null, null, $"{i - start + 1} unchanged line{(i == start ? "" : "s")}"));
        }
        return list;
    }

    public static (int Added, int Removed) Stats(IReadOnlyList<DiffLine> lines) =>
        (lines.Count(l => l.Kind == DiffKind.Added), lines.Count(l => l.Kind == DiffKind.Removed));

    /// <summary>Unified diff text ("--- v1", "+++ v2", "@@ -a,b +c,d @@" hunks).</summary>
    public static string Unified(IReadOnlyList<DiffLine> lines, string oldLabel, string newLabel, int context = 3)
    {
        var sb = new StringBuilder().Append("--- ").AppendLine(oldLabel).Append("+++ ").AppendLine(newLabel);
        var changed = Enumerable.Range(0, lines.Count).Where(i => lines[i].Kind != DiffKind.Same).ToList();
        int idx = 0;
        while (idx < changed.Count)
        {
            int from = Math.Max(0, changed[idx] - context), to = Math.Min(lines.Count - 1, changed[idx] + context);
            while (idx + 1 < changed.Count && changed[idx + 1] - context <= to + 1) { idx++; to = Math.Min(lines.Count - 1, changed[idx] + context); }
            idx++;
            var hunk = lines.Skip(from).Take(to - from + 1).ToList();
            int oldStart = hunk.FirstOrDefault(l => l.OldNo != null)?.OldNo ?? 0, newStart = hunk.FirstOrDefault(l => l.NewNo != null)?.NewNo ?? 0;
            sb.Append("@@ -").Append(oldStart).Append(',').Append(hunk.Count(l => l.OldNo != null))
              .Append(" +").Append(newStart).Append(',').Append(hunk.Count(l => l.NewNo != null)).AppendLine(" @@");
            foreach (var l in hunk) sb.Append(l.Prefix).AppendLine(l.Text);
        }
        return sb.ToString();
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    private static bool Eq(string a, string b) => string.Equals(a.TrimEnd(), b.TrimEnd(), StringComparison.Ordinal);
}
