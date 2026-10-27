using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Perception;

/// <summary>
/// Corrections REPLACE stale generator memory instead of appending to it. A player says the bot mines the wrong blocks
/// ("wrong blocks", "not that one", "wrong cell") → a correction window opens for that player; every cell they then teach
/// (punch, "here", demo) becomes the gen's dig-cell set, and the structure's older dig keys (<c>mine_here@...</c>, earlier
/// taught cells) not in the new set are retracted: moved to <see cref="Structure.RetiredKeys"/> with who/when/why, steps
/// and notes that name them move to <see cref="Structure.History"/>, and a version line records the change. A cell the yield
/// guard proved wrong is retracted the same way (evidence, no player needed). Zero tokens.
/// </summary>
public sealed partial class DigCellCorrections
{
    private readonly DigCells _cells;
    private readonly MemoryStore? _memory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Window> _open = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Window
    {
        public DateTimeOffset Until;
        public readonly List<WPos> Cells = new();
    }

    public DigCellCorrections(DigCells cells, MemoryStore? memory, TimeProvider? time = null)
    {
        _cells = cells;
        _memory = memory;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long after a correction the player's taught cells replace the old ones.</summary>
    public TimeSpan WindowLength { get; set; } = TimeSpan.FromMinutes(5);

    public Func<int>? Dimension { get; set; }
    private int Dim => Dimension?.Invoke() ?? 0;

    /// <summary>Logged changes ("retracted 6,64,8 (wrong cell, by PoofimaFox)").</summary>
    public event Action<string>? Changed;

    [GeneratedRegex(@"\b(?:wrong\s+(?:block|blocks|cell|cells|one|ones|spot|place|side)|(?:not|isn'?t|aren'?t)\s+(?:that|this|those|these|the\s+right)\s+(?:one|ones|block|blocks|cell|cells|spot)|that'?s\s+not\s+(?:it|the\s+(?:right\s+)?(?:one|block|cell))|mining\s+the\s+wrong|the\s+other\s+(?:cell|block)\s+not|not\s+there)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CorrectionWords();

    public static bool IsCorrection(string? text) => !string.IsNullOrWhiteSpace(text) && CorrectionWords().IsMatch(text);

    public bool IsOpen(string player) => _open.TryGetValue(player, out var w) && w.Until > _time.GetUtcNow();

    /// <summary>A player's chat line: a correction opens (or extends) their window. True when it did.</summary>
    public bool OnChat(string? player, string text)
    {
        if (player == null || !IsCorrection(text)) return false;
        var w = _open.AddOrUpdate(player, _ => new Window(), (_, old) => old.Until > _time.GetUtcNow() ? old : new Window());
        w.Until = _time.GetUtcNow() + WindowLength;
        return true;
    }

    /// <summary>
    /// A cell this player just taught (punch / "here" / demo). Inside a correction window it joins the new set and the
    /// gen's other dig keys are retracted. Returns what changed (null = nothing).
    /// </summary>
    public string? OnTaught(WPos cell, string? player)
    {
        if (player == null || !_open.TryGetValue(player, out var w) || w.Until <= _time.GetUtcNow()) return null;
        List<WPos> set;
        lock (w.Cells)
        {
            if (!w.Cells.Contains(cell)) w.Cells.Add(cell);
            set = w.Cells.ToList();
        }
        return Replace(cell, set, player, "corrected");
    }

    [GeneratedRegex(@"\b(?:never|don'?t|dont|do\s+not|avoid|leave|skip|not)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeverWords();

    [GeneratedRegex(@"\b(?:dig|mine)\s+only\b|\bonly\s+(?:dig|mine)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlyWords();

    [GeneratedRegex(@"(?<![\d-])(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)(?!\d)")]
    private static partial Regex CoordWords();

    /// <summary>Never-break marks on generator cells (the migration drops vague ones on confirmed regen cells).</summary>
    public MineClaw.Bot.ProtectionMarks? Marks { get; set; }

    /// <summary>
    /// A generator cell is minable after all (a trusted "you can mine the middle one", or the one-time migration for a
    /// confirmed regenerating cell): memory steps that forbid it ("never mine the middle cell 5,64,7 (kiwi)", "dig only 4,64,6
    /// and 6,64,8") move to the structure's history, and the cell is a dig cell again. Live 10-27 21:17: a vague "don't mine the
    /// middle one" left the bot one cell of three to hammer. Returns the logged line ("unmarked gen cell 5,64,7 per PoofimaFox:
    /// ..."), or null when nothing forbade it.
    /// </summary>
    public string? UnmarkGenCell(WPos cell, string by, bool middle = false)
    {
        if (_memory == null) return null;
        int dim = Dim;
        bool NamesCell(string t) => Regex.IsMatch(t, $@"(?<![\d-]){cell.X}\s*,\s*{cell.Y}\s*,\s*{cell.Z}(?!\d)");
        bool Forbids(string t)
        {
            if (NamesCell(t) || (middle && t.Contains("middle", StringComparison.OrdinalIgnoreCase)))
                return NeverWords().IsMatch(t);
            // "dig only 4,64,6 and 6,64,8": an exclusive list of other cells near this one leaves it out.
            if (!OnlyWords().IsMatch(t)) return false;
            var named = CoordWords().Matches(t).Select(m => new WPos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))).ToList();
            return named.Count > 0 && named.All(n => n != cell) && named.Any(n => Math.Abs(n.X - cell.X) <= 4 && Math.Abs(n.Y - cell.Y) <= 2 && Math.Abs(n.Z - cell.Z) <= 4);
        }
        var hits = _memory.Read(d => d.Structures.Where(x => x.Dimension == dim && x.Steps.Any(Forbids)).Select(x => x.Name).ToList());
        var stamp = _time.GetUtcNow().ToLocalTime().ToString("MM-dd HH:mm");
        var removed = new List<string>();
        foreach (var name in hits)
            _memory.UpsertStructure(name, st =>
            {
                foreach (var step in st.Steps.Where(Forbids).ToList())
                {
                    st.Steps.Remove(step);
                    st.History.Add($"step (unmarked {stamp} per {by}: gen cell {cell.X},{cell.Y},{cell.Z} is minable): {step}");
                    removed.Add($"{name}: {step}");
                }
                if (st.History.Count > 40) st.History.RemoveRange(0, st.History.Count - 40);
            });
        int marks = 0;
        if (Marks is { } mk && mk.NeverAt(cell, dim) is { } never && (middle || !never.Exact || never.Label.Contains("middle", StringComparison.OrdinalIgnoreCase)))
            marks = mk.Unmark(dim, [(cell, cell)]).Count;
        if (removed.Count == 0 && marks == 0) return null;
        _cells.Register(cell, by);
        _cells.Invalidate();
        var line = $"unmarked gen cell {cell.X},{cell.Y},{cell.Z} per {by}"
                   + (removed.Count > 0 ? ": " + string.Join("; ", removed) : "") + (marks > 0 ? $" (+{marks} never-break mark(s))" : "");
        try { Changed?.Invoke(line); } catch (Exception) { }
        return line;
    }

    /// <summary>
    /// One-time migration (idempotent: a second run finds nothing): every confirmed regenerating cell of an indexed generator
    /// (observation <c>cobble_generator</c> with <c>confirmed</c> and its <c>dig</c> list) loses vague never-break notes/marks.
    /// </summary>
    public int UnmarkConfirmedGenCells(MineClaw.Agent.Rag.ObservationStore? observations, string by)
    {
        if (observations == null) return 0;
        int n = 0;
        try
        {
            foreach (var cell in ConfirmedGenCells(observations, Dim))
                if (UnmarkGenCell(cell, by) != null) n++;
        }
        catch (Exception) { /* db busy: next start */ }
        return n;
    }

    /// <summary>Dig cells of confirmed (regen-observed) generators in the shared db.</summary>
    public static List<WPos> ConfirmedGenCells(MineClaw.Agent.Rag.ObservationStore observations, int dim)
    {
        var list = new List<WPos>();
        foreach (var row in observations.OfKind("cobble_generator", dim))
        {
            if (row.FieldsJson is not { Length: > 0 } f) continue;
            try
            {
                using var j = System.Text.Json.JsonDocument.Parse(f);
                var root = j.RootElement;
                if (!root.TryGetProperty("confirmed", out var conf) || conf.ValueKind != System.Text.Json.JsonValueKind.True) continue;
                if (!root.TryGetProperty("dig", out var dig) || dig.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var e in dig.EnumerateArray())
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Array && e.GetArrayLength() == 3)
                        list.Add(new WPos(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32()));
            }
            catch (System.Text.Json.JsonException) { }
        }
        return list.Distinct().ToList();
    }

    /// <summary>The yield guard proved a cell wrong: retract its keys (no replacement).</summary>
    public string? RetractBad(WPos cell, string reason) => Retract(cell, [cell], "yield guard", reason);

    /// <summary>The gen near <paramref name="near"/> gets exactly <paramref name="cells"/> as its dig cells (plus none other).</summary>
    public string? Replace(WPos near, IReadOnlyList<WPos> cells, string by, string why)
    {
        if (_memory == null) return null;
        int dim = Dim;
        var gen = DigCells.GeneratorNear(_memory, dim, near);
        if (gen == null)
        {
            foreach (var c in cells) _cells.Register(c, by);
            return null;
        }
        var keep = cells.ToHashSet();
        var stale = gen.KeyBlocks.Where(k => DigCells.IsDigLabel(k.Label) && !keep.Contains(new WPos(k.X, k.Y, k.Z))).Select(k => new WPos(k.X, k.Y, k.Z)).ToList();
        var added = cells.Where(c => !gen.KeyBlocks.Any(k => k.X == c.X && k.Y == c.Y && k.Z == c.Z)).ToList();
        if (stale.Count == 0 && added.Count == 0) return null;
        return Apply(gen.Name, stale, added, by, why);
    }

    private string? Retract(WPos near, IReadOnlyList<WPos> bad, string by, string why)
    {
        if (_memory == null) return null;
        var gen = DigCells.GeneratorNear(_memory, Dim, near);
        foreach (var b in bad) _cells.Retract(b);
        if (gen == null) return null;
        var stale = bad.Where(b => gen.KeyBlocks.Any(k => k.X == b.X && k.Y == b.Y && k.Z == b.Z && DigCells.IsDigLabel(k.Label))).ToList();
        return stale.Count == 0 ? null : Apply(gen.Name, stale, [], by, why);
    }

    private string Apply(string structure, List<WPos> stale, List<WPos> added, string by, string why)
    {
        var at = _time.GetUtcNow();
        string stamp = at.ToLocalTime().ToString("MM-dd HH:mm");
        static string F(WPos p) => $"{p.X},{p.Y},{p.Z}";
        static bool Names(string text, WPos p) => Regex.IsMatch(text, $@"(?<![\d-]){p.X}\s*,\s*{p.Y}\s*,\s*{p.Z}(?!\d)");
        string line = "";
        _memory!.UpsertStructure(structure, s =>
        {
            int version = s.History.Count + 2; // v1 = as first taught
            foreach (var p in stale)
            {
                foreach (var k in s.KeyBlocks.Where(k => k.X == p.X && k.Y == p.Y && k.Z == p.Z && DigCells.IsDigLabel(k.Label)).ToList())
                {
                    s.KeyBlocks.Remove(k);
                    s.RetiredKeys.Add(new KeyBlock { Label = $"{k.Label} [retracted v{version} {stamp} by {by}: {why}]", X = k.X, Y = k.Y, Z = k.Z });
                }
                // Steps/notes that point at a retracted cell are history now, not instructions.
                foreach (var st in s.Steps.Where(x => Names(x, p)).ToList())
                {
                    s.Steps.Remove(st);
                    s.History.Add($"v{version - 1} step (retracted {stamp}): {st}");
                }
                if (s.Note is { } note && Names(note, p))
                {
                    s.History.Add($"v{version - 1} note (retracted {stamp}): {note}");
                    s.Note = Regex.Replace(note, $@"(?<![\d-]){p.X}\s*,\s*{p.Y}\s*,\s*{p.Z}(?!\d)", $"{F(p)} (retracted: wrong cell)");
                }
            }
            foreach (var p in added)
                s.KeyBlocks.Add(new KeyBlock { Label = $"mine_here (taught by {by}, v{version})", X = p.X, Y = p.Y, Z = p.Z });
            var digs = s.KeyBlocks.Where(k => DigCells.IsDigLabel(k.Label)).Select(k => $"{k.X},{k.Y},{k.Z}").ToList();
            line = $"v{version} {stamp} {by} ({why}): retracted {(stale.Count == 0 ? "none" : string.Join(" ", stale.Select(F)))}; dig cells now {(digs.Count == 0 ? "unknown" : string.Join(" ", digs))}";
            s.History.Add(line);
            if (s.History.Count > 40) s.History.RemoveRange(0, s.History.Count - 40);
            if (s.RetiredKeys.Count > 20) s.RetiredKeys.RemoveRange(0, s.RetiredKeys.Count - 20);
        });
        foreach (var p in stale) _cells.Retract(p);
        foreach (var p in added) _cells.Register(p, by);
        _cells.Invalidate();
        try { Changed?.Invoke($"{structure}: {line}"); } catch (Exception) { }
        return line;
    }
}
