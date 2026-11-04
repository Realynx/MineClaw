using System.Text;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// "What happened since the brain's last turn", for the turn builder: compact lines from closed rollups, in-progress
/// sessions and notable single events, ranked by salience (the addressing player, near the bot, unusual: first time /
/// unknown build / death / big build, recency). High-confidence facts are stated, medium ones say "probably", low ones
/// are left out; routine noise (travel, equips, chat) is dropped.
/// </summary>
public sealed class ActivityDigest
{
    private readonly PlayerActivityTracker _t;
    private readonly ActivityOptions _o;
    private readonly TimeProvider _time;

    internal ActivityDigest(PlayerActivityTracker tracker, ActivityOptions options, TimeProvider time)
    {
        _t = tracker;
        _o = options;
        _time = time;
    }

    private sealed record Line(string Player, string Text, double Score);

    /// <summary>Compact digest ("since last: kiwi mined ~20 cobblestone at the cobble gen; ...") or "" when nothing notable.</summary>
    public string Since(DateTimeOffset since, int maxChars = 500, IReadOnlyCollection<string>? focusPlayers = null, string prefix = "since last: ")
    {
        var (closed, open, events, bot) = _t.DigestInputs(since);
        var lines = new List<Line>();
        var scorer = new Scorer(_time.GetUtcNow(), bot, new HashSet<string>(focusPlayers ?? [], StringComparer.OrdinalIgnoreCase));

        foreach (var r in closed.Concat(open))
        {
            if (r.Confidence < _o.MediumConfidence) continue;
            var text = r.Short.Length > 0 ? r.Short : r.Text;
            lines.Add(new Line(r.Player, text, scorer.Score(r.Player, r.Salience, r.End, r)));
        }
        // Singular notable facts that sessions don't carry (they never open sessions): deaths, signs.
        foreach (var e in events)
            if (EventLine(e, lines, scorer) is { } line) lines.Add(line);
        // Routine noise floor: a lone pickup or a single broken block far away doesn't make the cut.
        var kept = lines.Where(l => l.Score >= 1.2).OrderByDescending(l => l.Score).ToList();
        return kept.Count == 0 ? "" : Join(kept, maxChars, prefix);
    }

    /// <summary>A death or sign no session line already mentions, or a focus player's join/leave; else null.</summary>
    private Line? EventLine(ActivityEvent e, List<Line> lines, Scorer scorer)
    {
        if (e.Confidence < _o.MediumConfidence) return null;
        if (e.Kind is ActivityKinds.Died or ActivityKinds.WroteSign && !lines.Any(l => l.Player == e.Player && l.Text.Contains(e.Detail ?? "\u0001")))
        {
            double sal = e.Kind == ActivityKinds.Died ? 5 : 2.5;
            return new Line(e.Player, (e.Confidence < _o.HighConfidence ? "probably " : "") + e.Text, scorer.Score(e.Player, sal, e.At, null));
        }
        if (e.Kind is ActivityKinds.Joined or ActivityKinds.Left && scorer.Focus.Contains(e.Player))
            return new Line(e.Player, e.Text, scorer.Score(e.Player, 0.3, e.At, null));
        return null;
    }

    /// <summary>Salience + focus player + near the bot + recency.</summary>
    private sealed class Scorer(DateTimeOffset now, MineClaw.World.Vec3d bot, HashSet<string> focus)
    {
        public HashSet<string> Focus => focus;

        public double Score(string player, double salience, DateTimeOffset at, ActivityRollup? r)
        {
            double s = salience;
            if (focus.Contains(player)) s += 3;
            if (Near(r)) s += 1;
            s += Math.Exp(-(now - at).TotalMinutes / 10);
            return s;
        }

        private bool Near(ActivityRollup? r)
        {
            if (r == null || r.Max == default) return false;
            double dx = (r.Min.X + r.Max.X) / 2.0 - bot.X, dz = (r.Min.Z + r.Max.Z) / 2.0 - bot.Z;
            return dx * dx + dz * dz <= 24 * 24;
        }
    }

    private static string Join(List<Line> kept, int maxChars, string prefix)
    {
        var sb = new StringBuilder(prefix);
        int start = sb.Length;
        foreach (var l in kept)
        {
            var piece = (sb.Length > start ? "; " : "") + l.Text;
            if (sb.Length + piece.Length > maxChars) continue;
            sb.Append(piece);
        }
        return sb.Length == start ? "" : sb.ToString();
    }
}
