using System.Text.RegularExpressions;

namespace MineClaw.Agent.Worker;

/// <summary>
/// Zero-token detector for a player's mid-task amendment (live 10-27 21:26: Fox said "I need torches", then "just like 5"
/// six seconds later; the amendment reached the worker through an Opus round only after it had crafted 24 and it ended with
/// 32). Matching lines go straight from chat to the running worker's next round; the brain still gets the chat as usual.
/// Only short, self-contained corrections count: a quantity ("just 5", "only like 3", "stop at 10", "make it 2", "5 is
/// enough") or a cancel ("never mind", "nvm", "forget it", "no more", "that's enough").
/// </summary>
public static partial class TaskAmendment
{
    public sealed record Amendment(string Kind, int? Count, string Text)
    {
        /// <summary>The note the worker sees: "[player says] just like 5 → only 5 in total now; stop at 5 (don't make/take more)".</summary>
        public string Note(string player) => Kind switch
        {
            "count" => $"{player} says \"{Text}\": the amount is now {Count} in total; aim for {Count} from your next action on (no more than needed)",
            _ => $"{player} says \"{Text}\": they don't want this any more; stop at your next step, put back what you took, report",
        };
    }

    [GeneratedRegex(@"^(?:(?:oh|ah|um+|uh+|ok(?:ay)?|actually|wait|nah|no|hmm+)[,!. ]+)*(?:(?:just|only)\s+(?:like\s+|about\s+|around\s+|maybe\s+)?(?<n>\d{1,3})|(?:i\s+)?(?:only|just)\s+(?:need|want)\s+(?:like\s+|about\s+)?(?<n>\d{1,3})|(?:stop\s+at|make\s+it|max(?:imum)?|no\s+more\s+than|up\s+to)\s+(?<n>\d{1,3})|(?<n>\d{1,3})\s+(?:is|are|'s|would\s+be)\s+(?:enough|plenty|fine|good))\b(?:\s+\w+){0,4}\s*[.!~:3)( ]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountRx();

    [GeneratedRegex(@"^(?:(?:oh|ah|um+|uh+|ok(?:ay)?|actually|wait|nah|no|hmm+)[,!. ]+)*(?:never\s*mind|nvm|forget\s+(?:it|that|about\s+it)|cancel\s+(?:it|that)|don'?t\s+bother|no\s+more|that'?s\s+enough|stop\s+making\s+(?:them|those|more))\b[\w\s,.!~:3)(]{0,30}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CancelRx();

    /// <summary>The amendment in a chat line, or null (anything longer or unclear goes to the brain only).</summary>
    public static Amendment? Detect(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.Length > 60) return null;
        if (CountRx().Match(t) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) && n > 0) return new("count", n, t);
        if (CancelRx().IsMatch(t)) return new("cancel", null, t);
        return null;
    }
}
