using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Data;

namespace MineClaw.Replay;

/// <summary>
/// One hand-noted action: <c>mm:ss[.f]  player  kind  [block|item]  [xN]</c> (time since the recording started; <c>#</c>
/// comments). Kinds are the activity kinds (broke, placed, opened, took, stored, lit_furnace, wrote_sign, chat, died, ...)
/// or <c>self_&lt;kind&gt;</c> for the bot's own actions (self_dug, self_placed, self_said, self_died).
/// </summary>
public sealed record TruthEntry(long OffsetMs, string Player, string Kind, string? What, int Count, int Line);

public sealed record KindScore(string Kind, int Truth, int Detected, int TruePositives, IReadOnlyList<long> OffsetsMs)
{
    public double Precision => Detected == 0 ? (Truth == 0 ? 1 : 0) : (double)TruePositives / Detected;
    public double Recall => Truth == 0 ? 1 : (double)TruePositives / Truth;
    public double? MeanOffsetMs => OffsetsMs.Count == 0 ? null : OffsetsMs.Average();
    public long? MedianOffsetMs => OffsetsMs.Count == 0 ? null : OffsetsMs.Order().ElementAt(OffsetsMs.Count / 2);
    public long? MaxAbsOffsetMs => OffsetsMs.Count == 0 ? null : OffsetsMs.Max(Math.Abs);
}

public sealed record CalibrationScore(IReadOnlyList<KindScore> Kinds, IReadOnlyList<TruthEntry> Missed, IReadOnlyList<TimelineEvent> Unexpected,
    int UnlabelledDetections)
{
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{"kind",-14} {"truth",5} {"found",5} {"tp",4} {"prec",6} {"recall",6} {"mean Δt",9} {"median",8} {"max |Δt|",9}");
        foreach (var k in Kinds)
            sb.AppendLine($"{k.Kind,-14} {k.Truth,5} {k.Detected,5} {k.TruePositives,4} {k.Precision,6:0.00} {k.Recall,6:0.00} " +
                          $"{Ms(k.MeanOffsetMs),9} {Ms(k.MedianOffsetMs),8} {Ms(k.MaxAbsOffsetMs),9}");
        int truth = Kinds.Sum(k => k.Truth), found = Kinds.Sum(k => k.Detected), tp = Kinds.Sum(k => k.TruePositives);
        sb.AppendLine($"{"ALL",-14} {truth,5} {found,5} {tp,4} {(found == 0 ? 0 : (double)tp / found),6:0.00} {(truth == 0 ? 1 : (double)tp / truth),6:0.00}");
        foreach (var m in Missed) sb.AppendLine($"missed   line {m.Line}: {ReplayResult.Clock(m.OffsetMs)} {m.Player} {m.Kind} {m.What} x{m.Count}");
        foreach (var u in Unexpected) sb.AppendLine($"extra    {u.Format()}");
        if (UnlabelledDetections > 0) sb.AppendLine($"({UnlabelledDetections} detection(s) of kinds the truth file doesn't label: not scored)");
        return sb.ToString();
    }

    private static string Ms(double? ms) => ms is { } v ? $"{v / 1000:+0.0;-0.0;0.0}s" : "-";
}

public static partial class Calibration
{
    [GeneratedRegex(@"^\s*(\d+):(\d+(?:\.\d+)?)\s+(\S+)\s+(\S+)(?:\s+(?!x\d)(\S+))?(?:\s+x(\d+))?\s*$")]
    private static partial Regex LineRx();

    public static List<TruthEntry> ParseTruth(string text)
    {
        var list = new List<TruthEntry>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Split('#')[0].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var m = LineRx().Match(line);
            if (!m.Success) throw new FormatException($"truth line {n}: '{line.Trim()}' (want: mm:ss.f player kind [block] [xN])");
            long ms = (long)(int.Parse(m.Groups[1].Value) * 60000 + double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 1000);
            list.Add(new TruthEntry(ms, m.Groups[3].Value, m.Groups[4].Value.ToLowerInvariant(), m.Groups[5].Success ? m.Groups[5].Value.ToLowerInvariant() : null,
                m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 1, n));
        }
        return list.OrderBy(t => t.OffsetMs).ToList();
    }

    /// <summary>The scoring key of a detected event: activity kinds as-is, the bot's own as self_&lt;kind&gt;.</summary>
    public static string KindOf(TimelineEvent e) => e.Source == "self" ? "self_" + e.Kind : e.Kind;

    private static bool WhatMatches(TruthEntry t, TimelineEvent e)
    {
        if (t.What is null) return true;
        string? block = e.BlockId is { } b ? Blocks.Get(b).Name : null;
        string? item = e.ItemId is { } i ? Items.Get(i).Name : null;
        return string.Equals(block, t.What, StringComparison.OrdinalIgnoreCase) || string.Equals(item, t.What, StringComparison.OrdinalIgnoreCase)
               || e.Text.Contains(t.What, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Greedy match within ±<paramref name="tolerance"/>: each truth entry consumes the earliest in-window detections of the same
    /// player + kind (+ block/item when given), counting merged events by their count. Precision/recall per kind; Δt =
    /// detected − noted (positive = the bot noticed later).
    /// </summary>
    public static CalibrationScore Score(ReplayResult replay, IReadOnlyList<TruthEntry> truth, TimeSpan? tolerance = null)
    {
        long tol = (long)(tolerance ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        var kinds = truth.Select(t => t.Kind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var detections = replay.Timeline.Where(e => e.Source is "activity" or "self").ToList();
        var scored = detections.Where(e => kinds.Contains(KindOf(e))).ToList();
        var left = scored.ToDictionary(e => e, e => e.Count);
        var offsets = kinds.ToDictionary(k => k, _ => new List<long>(), StringComparer.OrdinalIgnoreCase);
        var tp = kinds.ToDictionary(k => k, _ => 0, StringComparer.OrdinalIgnoreCase);
        var missed = new List<TruthEntry>();
        foreach (var t in truth)
        {
            int need = t.Count;
            var cands = scored.Where(e => left[e] > 0 && KindOf(e).Equals(t.Kind, StringComparison.OrdinalIgnoreCase)
                                          && string.Equals(e.Player, t.Player, StringComparison.OrdinalIgnoreCase)
                                          && Math.Abs(e.OffsetMs - t.OffsetMs) <= tol && WhatMatches(t, e))
                .OrderBy(e => Math.Abs(e.OffsetMs - t.OffsetMs)).ToList();
            bool first = true;
            foreach (var e in cands.OrderBy(e => e.OffsetMs < t.OffsetMs - tol / 2 ? 1 : 0).ThenBy(e => e.OffsetMs))
            {
                if (need == 0) break;
                int take = Math.Min(need, left[e]);
                left[e] -= take;
                need -= take;
                tp[t.Kind] += take;
                // Δt of the first detection of each noted action (a "x5" line is noted when it starts).
                if (first) offsets[t.Kind].Add(e.OffsetMs - t.OffsetMs);
                first = false;
            }
            if (need > 0) missed.Add(t with { Count = need });
        }
        var result = kinds.OrderBy(k => k, StringComparer.Ordinal).Select(k => new KindScore(k,
            truth.Where(t => t.Kind.Equals(k, StringComparison.OrdinalIgnoreCase)).Sum(t => t.Count),
            scored.Where(e => KindOf(e).Equals(k, StringComparison.OrdinalIgnoreCase)).Sum(e => e.Count),
            tp[k], offsets[k])).ToList();
        var unexpected = scored.Where(e => left[e] > 0).ToList();
        return new CalibrationScore(result, missed, unexpected, detections.Count - scored.Count);
    }
}
