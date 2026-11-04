using System.Text.RegularExpressions;
using MineClaw.Data;

namespace MineClaw.Bot.Perception;

/// <summary>A chat line that sets up a hand-off: pickups are for <see cref="Target"/> (only <see cref="ItemId"/>, when named).</summary>
public sealed record HandoffIntentMatch(string Target, int? ItemId);

/// <summary>
/// Zero-token detector for "this item is for someone else" in chat, either side: the bot's own promise ("give the bread to me
/// and I'll pass it to 67", live 10-27 15:46) or a player's line ("pass this to 69", "bread for 67", "give it to kiwi"). Only
/// known names count (hive clones by number/login, players); "me"/"you"/the bot itself never are targets.
/// </summary>
public static class HandoffIntent
{
    private static readonly Regex PassTo = new(
        @"\b(?:pass|give|hand|bring|take|toss|throw|send|deliver)(?:s|ing)?\b(?<what>(?:\s+(?:it|this|that|them|these|those|the|some|a|an|one|\d+|\w+)){0,3}?)\s+(?:on\s+|over\s+|along\s+)?to\s+(?:clone\s+|maid\s+)?(?<who>[\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ForWho = new(
        @"\b(?<what>\w+)\s+(?:is\s+|are\s+)?for\s+(?:clone\s+|maid\s+)?(?<who>[\w]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> NotTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "me", "you", "us", "them", "him", "her", "it", "the", "a", "an", "my", "your", "now", "later", "night", "day", "a while", "real",
    };

    /// <param name="names">Known targets: player logins and hive clone numbers/logins ("67" or "maid_chan_67").</param>
    /// <param name="self">This bot's login and aliases (never a target).</param>
    public static HandoffIntentMatch? Detect(string text, IEnumerable<string> names, IEnumerable<string>? self = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var known = names.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var selfSet = new HashSet<string>(self ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (Match m in PassTo.Matches(text))
            if (Resolve(m.Groups["who"].Value, known, selfSet) is { } who) return new(who, ItemIn(m.Groups["what"].Value) ?? ItemIn(text));
        foreach (Match m in ForWho.Matches(text))
            if (Resolve(m.Groups["who"].Value, known, selfSet) is { } who && ForWhoHandoff(m.Groups["what"].Value, who, text) is { } match)
                return match;
        return null;
    }

    /// <summary>"this is for 67" / "bread for 67": an item or a deictic word before "for"; "wait for 67" is not a hand-off.</summary>
    private static HandoffIntentMatch? ForWhoHandoff(string what, string who, string text)
    {
        int? item = ItemIn(what);
        if (item == null && !Deictic.Contains(what.ToLowerInvariant())) return null;
        return new(who, item ?? ItemIn(text));
    }

    private static readonly HashSet<string> Deictic = new(StringComparer.Ordinal) { "this", "that", "these", "it", "them", "those" };

    /// <summary>Words never read as an item name.</summary>
    private static readonly HashSet<string> NotItems = new(StringComparer.Ordinal)
        { "this", "that", "these", "those", "them", "the", "some", "and", "for", "pass", "give", "hand" };

    private static string? Resolve(string word, IReadOnlyList<string> known, HashSet<string> self)
    {
        if (word.Length == 0 || NotTargets.Contains(word) || self.Contains(word)) return null;
        foreach (var n in known)
        {
            if (n.Equals(word, StringComparison.OrdinalIgnoreCase)) return n;
            // A clone's number names it ("67" → maid_chan_67).
            if (word.All(char.IsDigit) && n.EndsWith("_" + word, StringComparison.OrdinalIgnoreCase)) return n;
        }
        return null;
    }

    /// <summary>The first item a phrase names ("the bread", "2 breads", "porkchops"), null = none.</summary>
    public static int? ItemIn(string phrase)
    {
        foreach (Match w in Regex.Matches(phrase.ToLowerInvariant(), "[a-z_]{3,}"))
        {
            var word = w.Value;
            if (NotItems.Contains(word)) continue;
            foreach (var cand in new[] { word, word.TrimEnd('s'), word.EndsWith("es") ? word[..^2] : word })
                if (cand.Length >= 3 && Items.ByName(cand) is { } info) return info.Id;
        }
        return null;
    }
}
