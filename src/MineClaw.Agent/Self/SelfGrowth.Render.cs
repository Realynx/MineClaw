using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Self;

public sealed partial class SelfGrowth
{
    /// <summary>Header line of the uncached "[self]" block: it ranks itself below persona and rules.</summary>
    public static string BlockHeader => ContextManager.SelfBlockTag + " " + Prompts.SelfBlockNote;

    /// <summary>
    /// The compact personality block for this turn (≤ <see cref="SelfOptions.MaxBlockChars"/>, ≈180 tokens), "" when nothing
    /// has grown yet. Main: mood → you → jokes → people. Clone: hive profile → its shard (seeds + grown traits, mood,
    /// people; ≤ <see cref="SelfOptions.MaxShardChars"/>). Deterministic for a given state, so it can be sent only on change.
    /// </summary>
    public string RenderBlock(IEnumerable<string>? turnPlayers = null)
    {
        if (!_o.Enabled) return "";
        var lines = new List<(int Priority, string Text)>(); // lower priority number = dropped last
        var mood = Mood().Words();
        var you = new List<string>();
        var seeds = IsClone ? Seeds() : [];
        if (seeds.Count > 0) you.Add(string.Join(", ", seeds));
        you.AddRange(OwnTraits());
        var jokes = Profile.Entries.Where(e => e.Section == SelfSection.Joke).OrderByDescending(e => e.Strength).Take(2)
            .Select(e => (Profile.IsStrong(e) ? "" : "~") + "\"" + e.Text + "\"").ToList();
        var people = new List<string>();
        foreach (var p in PresentPlayers(turnPlayers))
        {
            if (people.Count >= Math.Max(0, _o.MaxPeopleLines)) break;
            var line = PersonLine(p);
            if (line.Length > 0) people.Add(line);
        }

        if (IsClone)
        {
            HiveProfile?.Reload(); // shared: another clone may have promoted something since
            var hive = HiveProfile?.Entries.Where(e => e.Section is not (SelfSection.Relationship or SelfSection.Nickname or SelfSection.Milestone))
                .OrderByDescending(e => e.Strength).Take(4).Select(e => e.Text).ToList() ?? [];
            if (hive.Count > 0) lines.Add((1, Cap("hive (all maid clones): " + string.Join("; ", hive), 160)));
            you.RemoveAll(y => hive.Any(h => SelfProfile.Similar(y.TrimStart('~'), h))); // shared already: not repeated in the shard
            var shard = new List<(int, string)>();
            if (you.Count > 0) shard.Add((0, Cap($"you ({Clone}): " + string.Join("; ", you), 150)));
            if (mood.Length > 0) shard.Add((2, "mood: " + mood));
            if (jokes.Count > 0) shard.Add((5, Cap("jokes: " + string.Join(", ", jokes), 70)));
            for (int i = 0; i < people.Count; i++) shard.Add((3 + i * 2, Cap(people[i], 100)));
            lines.AddRange(Fit(shard, _o.MaxShardChars));
        }
        else
        {
            if (mood.Length > 0) lines.Add((1, "mood: " + mood));
            if (you.Count > 0) lines.Add((0, Cap("you: " + string.Join("; ", you), 220)));
            if (jokes.Count > 0) lines.Add((5, Cap("jokes: " + string.Join(", ", jokes), 90)));
            for (int i = 0; i < people.Count; i++) lines.Add((2 + i * 2, Cap(people[i], 120)));
            if (Profile.Entries.Where(e => e.Section == SelfSection.Milestone).OrderByDescending(e => e.Strength).FirstOrDefault() is { } m)
                lines.Add((8, Cap("memory: " + m.Text, 80)));
        }
        if (lines.Count == 0) return "";
        var fitted = Fit(lines, _o.MaxBlockChars - BlockHeader.Length - 1);
        return fitted.Count == 0 ? "" : BlockHeader + "\n" + string.Join("\n", fitted.Select(l => l.Text));
    }

    /// <summary>Drops the least important lines until the joined text fits (original order kept).</summary>
    private static List<(int Priority, string Text)> Fit(List<(int Priority, string Text)> lines, int max)
    {
        var kept = lines.ToList();
        while (kept.Count > 0 && kept.Sum(l => l.Text.Length + 1) - 1 > max)
            kept.Remove(kept.OrderByDescending(l => l.Priority).First());
        return kept;
    }

    private static string Cap(string s, int n)
    {
        if (s.Length <= n) return s;
        var cut = s[..n];
        int sp = cut.LastIndexOfAny([';', ',']);
        return (sp > n / 2 ? cut[..sp] : cut.TrimEnd()).TrimEnd(' ', ',', ';');
    }

    /// <summary>Own grown traits/likes (strong first; "~" = budding, not yet reinforced enough) plus emergent preferences.</summary>
    private List<string> OwnTraits()
    {
        var items = new List<string>();
        var entries = Profile.Entries.Where(e => e.Section is SelfSection.Trait or SelfSection.Like or SelfSection.Dislike or SelfSection.Opinion)
            .OrderByDescending(e => Profile.IsStrong(e)).ThenByDescending(e => e.Strength).ThenBy(e => e.Id).Take(5).ToList();
        foreach (var e in entries)
            items.Add((Profile.IsStrong(e) ? "" : "~") + e.Section switch
            {
                SelfSection.Like => "likes " + e.Text,
                SelfSection.Dislike => "meh about " + e.Text,
                _ => e.Text,
            });
        foreach (var p in Preferences().Take(8))
        {
            string? s = p.IsPro ? $"pro at {TaskTypes.Label(p.Subject)}" : null;
            if (p.IsLike) s = s == null ? $"likes {TaskTypes.Label(p.Subject)}" + (p.Praise >= 0.8 ? " (gets praised for it)" : "") : s + " and loves it";
            else if (p.IsDislike) s = $"meh about {TaskTypes.Label(p.Subject)}";
            if (s != null && !items.Any(i => SelfProfile.Similar(i, s))) items.Add(s);
            if (items.Count >= 7) break;
        }
        return items;
    }

    /// <summary>"kiwi: fave tease target, warm, trusts you w/ the farm" (grown relationship entry first, then the aggregates).</summary>
    public string PersonLine(string player)
    {
        var entry = Profile.Entries.Where(e => e.Section == SelfSection.Relationship && string.Equals(e.Player, player, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Strength).FirstOrDefault();
        var nick = Profile.Entries.FirstOrDefault(e => e.Section == SelfSection.Nickname && string.Equals(e.Player, player, StringComparison.OrdinalIgnoreCase));
        var rel = Relationship(player);
        var parts = new List<string>();
        if (entry != null) parts.Add((Profile.IsStrong(entry) ? "" : "~") + entry.Text);
        var agg = rel.Describe();
        if (agg.Length > 0) parts.Add(agg);
        if (nick != null && rel.Nickname == null) parts.Add($"calls you \"{nick.Text}\"");
        return parts.Count == 0 ? "" : $"{player}: {string.Join(", ", parts)}";
    }
}

/// <summary>
/// Sends the "[self]" block only when it changed or is no longer visible in the history window: an unchanged block costs
/// zero tokens per turn (the copy in history is read from cache). The cheap tier's transcript drops the block, so it
/// always gets it.
/// </summary>
public sealed class SelfBlockGate
{
    private readonly object _lock = new();
    private string? _last;
    private long _seq = -1;

    public string Next(string block, ModelTier tier, ContextManager context)
    {
        if (tier != ModelTier.Main) return block;
        lock (_lock)
        {
            if (block.Length == 0)
            {
                // A reset/rollback emptied it: say so once, so an older block in history isn't taken as current.
                if (string.IsNullOrEmpty(_last) || !Visible(context)) { _last = ""; return ""; }
                block = ContextManager.SelfBlockTag + " (nothing grown right now)";
            }
            if (block == _last && Visible(context)) return "";
            _last = block;
            _seq = context.NextSeq;
            return block;
        }
    }

    private bool Visible(ContextManager context) =>
        context.Episodes.Any(e => e.Seq == _seq && e.Messages.Count > 0 && e.Messages[0].Text.Contains(ContextManager.SelfBlockTag, StringComparison.Ordinal));

    public void Reset() { lock (_lock) { _last = null; _seq = -1; } }
}
