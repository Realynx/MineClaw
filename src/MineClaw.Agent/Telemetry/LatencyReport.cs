using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Telemetry;

/// <summary>p50/p95 of one category: n, first visible reaction, full answer (ms).</summary>
public sealed record LatencyStats(string Category, int N, long? ReactP50, long? ReactP95, long? FullP50, long? FullP95)
{
    public string Line() => $"{Category} n{N} react {S(ReactP50)}/{S(ReactP95)} full {S(FullP50)}/{S(FullP95)}";
    private static string S(long? ms) => ms is { } v ? (v / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + "s" : "-";
}

/// <summary>
/// Chat → reaction metrics by kind of message (command, question, banter, greeting, help call): p50/p95 of the first visible
/// reaction (head turn, gesture, line, movement, action) and of the full answer (final line or first action, the later).
/// Built from live <see cref="LatencyRecord"/>s, from <c>latency-*.jsonl</c>, or reconstructed from a <c>mineclaw-*.log</c>
/// (<see cref="FromLog"/>: older snapshots that predate the latency file).
/// </summary>
public static class LatencyReport
{
    /// <summary>Per category (report order) plus "all".</summary>
    public static IReadOnlyList<LatencyStats> Build(IEnumerable<LatencyRecord> records)
    {
        var list = records.ToList();
        var rows = new List<LatencyStats>();
        foreach (var cat in ChatIntent.Categories.Append("all"))
        {
            var r = cat == "all" ? list : list.Where(x => (x.Category ?? ChatIntent.Banter) == cat).ToList();
            if (r.Count == 0) continue;
            rows.Add(new LatencyStats(cat, r.Count, P(r.Select(x => x.ReactionMs), 50), P(r.Select(x => x.ReactionMs), 95),
                P(r.Select(x => x.FullMs ?? LatencyTracker.Full(x.ConclusiveMs, x.FirstActionMs, x.FirstChatMs)), 50),
                P(r.Select(x => x.FullMs ?? LatencyTracker.Full(x.ConclusiveMs, x.FirstActionMs, x.FirstChatMs)), 95)));
        }
        return rows;
    }

    /// <summary>"command n12 react 0.1s/0.3s full 4.2s/9.1s · question n4 ..." (p50/p95).</summary>
    public static string Line(IEnumerable<LatencyRecord> records) => string.Join(" · ", Build(records).Select(s => s.Line()));

    /// <summary>Nearest-rank percentile of the non-null values (null = none).</summary>
    public static long? P(IEnumerable<long?> values, int pct)
    {
        var v = values.Where(x => x != null).Select(x => x!.Value).OrderBy(x => x).ToList();
        if (v.Count == 0) return null;
        int rank = (int)Math.Ceiling(pct / 100.0 * v.Count);
        return v[Math.Clamp(rank - 1, 0, v.Count - 1)];
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Reads a latency-*.jsonl file (bad lines skipped).</summary>
    public static IEnumerable<LatencyRecord> ReadJsonl(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            LatencyRecord? r = null;
            try { r = JsonSerializer.Deserialize<LatencyRecord>(line, Json); } catch (JsonException) { }
            if (r != null) yield return r;
        }
    }

    // ------------------------------------------------------------------ reconstruction from the text log

    private static readonly Regex TimeRx = new(@"^(\d\d):(\d\d):(\d\d)\.(\d{3}) ", RegexOptions.Compiled);
    private static readonly Regex ChatRx = new(@" App: chat: <([^>]+)> (.*)$", RegexOptions.Compiled);
    private static readonly Regex TriageRx = new(@"Agent\.BotAgent: triage → ", RegexOptions.Compiled);
    private static readonly Regex CallRx = new(@"Agent\.BotAgent: call \S+ \(.*\) round \d+:|Worker: worker call \d+:", RegexOptions.Compiled);
    private static readonly Regex ToolRx = new(@"Agent\.BotAgent: tool (\w+) → (ok|error)", RegexOptions.Compiled);
    private static readonly Regex TurnEndRx = new(@"Agent\.BotAgent: turn: tier ", RegexOptions.Compiled);

    /// <summary>
    /// Reconstructs chat → reaction records from a bot's text log (no head turn or latency file in older builds): a player line
    /// followed by a brain turn within <paramref name="maxQueueSeconds"/> is a measured chat; its first visible reaction is the
    /// bot's first chat line or first game action, whichever came first, and the full answer the later of its last line and
    /// first action. A body tool's start is the end of the model call that issued it (the log stamps tools when they finish);
    /// a delegate's is the worker's first call. Lines from <paramref name="bot"/> and <paramref name="otherBots"/> never count.
    /// </summary>
    public static List<LatencyRecord> FromLog(IEnumerable<string> lines, string bot, IEnumerable<string>? otherBots = null,
        IEnumerable<string>? names = null, double maxQueueSeconds = 15, DateOnly? day = null)
    {
        var bots = new HashSet<string>((otherBots ?? []).Append(bot), StringComparer.OrdinalIgnoreCase);
        var nameList = (names ?? []).Append(bot).ToList();
        var date = day ?? new DateOnly(2026, 1, 1);
        var records = new List<LatencyRecord>();
        (DateTimeOffset At, string Player, string Text)? pending = null;
        (DateTimeOffset At, string Player, string Text)? turn = null;
        DateTimeOffset? lastCall = null, firstLine = null, lastLine = null, action = null;
        bool workerAction = false;
        DateTimeOffset prev = DateTimeOffset.MinValue;
        int dayOffset = 0;
        var tools = new List<string>();

        foreach (var raw in lines)
        {
            var tm = TimeRx.Match(raw);
            if (!tm.Success) continue;
            var t = new DateTimeOffset(date.ToDateTime(new TimeOnly(int.Parse(tm.Groups[1].Value), int.Parse(tm.Groups[2].Value),
                int.Parse(tm.Groups[3].Value), int.Parse(tm.Groups[4].Value))), TimeSpan.Zero).AddDays(dayOffset);
            if (prev != DateTimeOffset.MinValue && t < prev - TimeSpan.FromHours(6)) { dayOffset++; t = t.AddDays(1); }
            prev = t;

            if (ChatRx.Match(raw) is { Success: true } cm)
            {
                var who = cm.Groups[1].Value;
                if (who.Equals(bot, StringComparison.OrdinalIgnoreCase))
                {
                    if (turn != null) { firstLine ??= t; lastLine = t; }
                    continue;
                }
                if (bots.Contains(who)) continue;
                var text = cm.Groups[2].Value;
                if (turn == null && (pending == null || t - pending.Value.At > TimeSpan.FromSeconds(maxQueueSeconds))) pending = (t, who, text);
                else if (turn == null && pending is { } pd && pd.Player.Equals(who, StringComparison.OrdinalIgnoreCase)) pending = (pd.At, pd.Player, pd.Text + " / " + text);
                continue;
            }
            if (TriageRx.IsMatch(raw))
            {
                if (turn == null && pending is { } p && t - p.At <= TimeSpan.FromSeconds(maxQueueSeconds)) turn = p;
                pending = null;
                if (turn != null) { lastCall = null; firstLine = lastLine = action = null; workerAction = false; tools.Clear(); }
                continue;
            }
            if (turn == null) continue;
            if (CallRx.IsMatch(raw))
            {
                if (raw.Contains("worker call", StringComparison.Ordinal) && workerAction == false && tools.Count == 0) { action ??= t; workerAction = true; }
                lastCall = t;
                continue;
            }
            if (ToolRx.Match(raw) is { Success: true } tool)
            {
                var name = tool.Groups[1].Value;
                tools.Add(name);
                if (action == null && (LatencyTracker.BodyTools.Contains(name) || name == "stop")) action = lastCall ?? t;
                continue;
            }
            if (TurnEndRx.IsMatch(raw))
            {
                var o = turn.Value;
                long? Ms(DateTimeOffset? x) => x is { } v ? Math.Max(0, (long)(v - o.At).TotalMilliseconds) : null;
                var react = new[] { firstLine, action }.Where(x => x != null).Min();
                var cls = ChatIntent.CategoryOf(o.Text.Split(" / "), nameList);
                records.Add(new LatencyRecord
                {
                    Ts = o.At, Bot = bot, Player = o.Player, Text = TextUtil.Truncate(o.Text, 80), Kind = "log",
                    ReactionMs = Ms(react), FirstChatMs = Ms(firstLine), ConclusiveMs = Ms(lastLine), FirstActionMs = Ms(action),
                    ReactionBy = react == null ? null : react == firstLine ? "line" : "action", Category = cls,
                    FullMs = LatencyTracker.Full(Ms(lastLine), Ms(action), Ms(firstLine)), Tools = tools.ToArray(),
                });
                turn = null;
            }
        }
        return records;
    }

    /// <summary>A markdown table of <see cref="Build"/> (design docs, reports).</summary>
    public static string Markdown(IEnumerable<LatencyRecord> records)
    {
        var sb = new StringBuilder("| kind | n | first reaction p50 | p95 | full answer p50 | p95 |\n|---|---|---|---|---|---|\n");
        foreach (var s in Build(records))
            sb.Append($"| {s.Category} | {s.N} | {F(s.ReactP50)} | {F(s.ReactP95)} | {F(s.FullP50)} | {F(s.FullP95)} |\n");
        return sb.ToString();
        static string F(long? ms) => ms is { } v ? (v / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s" : "-";
    }
}
