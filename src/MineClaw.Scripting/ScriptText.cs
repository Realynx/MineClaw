using System.Text;

namespace MineClaw.Scripting;

/// <summary>The terse lines the models see: script state (≤ 400 chars) and run outcomes (≤ 300 chars).</summary>
public static class ScriptText
{
    /// <summary>"4m12s", "42s", "1h03m".</summary>
    public static string Dur(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s"
        : $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))}s";

    /// <summary>
    /// Compact state: <c>script cobble_loop paused (danger: hp 5/20) L12 `mine('cobblestone',16)` | cp 'loop' 1/2 | 45% 30 cobble |
    /// n=30 | 31 acts | 3m10s</c>.
    /// </summary>
    public static string State(ScriptSession s, int max = 400)
    {
        var sb = new StringBuilder("script ").Append(s.Name).Append(' ').Append(s.Status.ToString().ToLowerInvariant());
        if (s.Status == ScriptStatus.Paused && s.PauseReason != null) sb.Append(" (").Append(s.PauseReason).Append(')');
        if (s.Reason != null && !s.IsLive) sb.Append(" (").Append(s.Reason).Append(')');
        if (s.Line > 0) sb.Append(" L").Append(s.Line);
        if (s.Snippet != null) sb.Append(" `").Append(s.Snippet).Append('`');
        if (s.Checkpoint != null) sb.Append(" | cp '").Append(s.Checkpoint).Append('\'').Append(s.CheckpointTotal > 0 ? $" {s.CheckpointsPassed}/{s.CheckpointTotal}" : "");
        if (s.ProgressPct != null || s.ProgressNote != null)
        {
            sb.Append(" | ");
            if (s.ProgressPct is { } p) sb.Append($"{p:0}%");
            if (s.ProgressNote != null) sb.Append(s.ProgressPct != null ? " " : "").Append(s.ProgressNote);
        }
        if (s.Vars != null) sb.Append(" | ").Append(s.Vars);
        if (s.LastFailure != null && s.IsLive) sb.Append(" | last fail ").Append(s.LastFailure);
        sb.Append(" | ").Append(s.Actions).Append(" acts | ").Append(Dur(s.Elapsed));
        return Cap(sb.ToString(), max);
    }

    /// <summary>
    /// <c>ok: script cobble_loop done (+64 cobblestone, 4m12s)</c> / <c>failed: script x at line 12 (goto: no route): cp 1/3, 7 acts, 1m02s</c>
    /// / <c>cancelled: script x at line 4 (stop): 3 acts, 20s</c>.
    /// </summary>
    public static string Outcome(ScriptSession s, string? completion, IReadOnlyList<string> gained, int max = 300)
    {
        var dur = Dur(s.Elapsed);
        if (s.Status == ScriptStatus.Done)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(completion)) parts.Add(ScriptSession.Short(ScriptSession.OneLine(completion), 80));
            parts.AddRange(gained);
            if (parts.Count == 0) parts.Add($"{s.Actions} acts");
            parts.Add(dur);
            return Cap($"ok: script {s.Name} done ({string.Join(", ", parts)})", max);
        }
        var head = s.Status == ScriptStatus.Cancelled ? "cancelled" : "failed";
        var where = s.Line > 0 ? $" at line {s.Line}" : "";
        var reason = s.Reason ?? "?";
        // An error right after a failed action is usually about it: "failed at line 12 (goto: no route)".
        if (reason == "fail()" && s.LastFailure != null) reason = s.LastFailure;
        var progress = s.CheckpointTotal > 0 ? $"cp {s.CheckpointsPassed}/{s.CheckpointTotal}, {s.Actions} acts" : $"{s.Actions} acts";
        var tail = gained.Count > 0 ? ", " + string.Join(", ", gained) : "";
        return Cap($"{head}: script {s.Name}{where} ({reason}): {progress}{tail}, {dur}", max);
    }

    public static string Cap(string s, int max) => s.Length <= max ? s : s[..(max - 3)].TrimEnd() + "...";
}
