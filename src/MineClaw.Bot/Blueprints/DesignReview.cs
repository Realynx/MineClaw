using System.Text.RegularExpressions;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// Player review of a pending design from chat (zero tokens): an explicit acceptance by a trusted player ("build it", "looks
/// good, build") starts construction; a change request from the requester or a trusted player ("make it taller", "door facing
/// the gen") starts another revision. Anything else is left to the brain.
/// </summary>
public static partial class DesignReview
{
    [GeneratedRegex(@"^\W*(?:(?:ok(?:ay)?|yes|yeah|yep|yup|sure|great|nice|perfect|cool|awesome|love it|looks? (?:good|great|fine|nice|perfect))[\s,!.]*)*(?:(?:please|pls|go ahead(?: and)?|you can|then|now)\s+)*(?:build|make|construct)\s+(?:it|that|this|that one|this one|the \w+)(?:\s+(?:now|please|pls|then|for me))?\W*$|^\W*(?:looks? (?:good|great|fine|nice)|perfect|great|nice),?\s*(?:go\s+)?build\W*$|^\W*build it\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex AcceptRx();

    [GeneratedRegex(@"\b(?:make it|change|move|add|remove|without|more|less|fewer|bigger|smaller|taller|shorter|wider|narrower|higher|lower|instead|facing|face|turn|rotate|swap|replace|use|door|doors|window|windows|roof|floor|wall|walls|torch|torches|chimney|porch|stairs|glass|darker|lighter|longer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeRx();

    [GeneratedRegex(@"^\W*(?:can|could|would|will) (?:you|u)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PoliteRx();

    [GeneratedRegex(@"\b(?:another|again|copy|one more|second one)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReuseRx();

    public static bool IsAcceptance(string text) => AcceptRx().IsMatch(text.Trim());

    /// <summary>A change request about the pending design (not a question about something else, not a new build).</summary>
    public static bool IsChange(string text)
    {
        var t = text.Trim();
        if (t.Length < 3 || IsAcceptance(t) || ReuseRx().IsMatch(t)) return false;
        if (t.EndsWith('?') && !PoliteRx().IsMatch(t)) return false;
        if (t.StartsWith("stop", StringComparison.OrdinalIgnoreCase) || t.StartsWith("cancel", StringComparison.OrdinalIgnoreCase)) return false;
        return ChangeRx().IsMatch(t);
    }

    /// <summary>Handles a line from a player; returns what was done (the line is then consumed), or null.</summary>
    public static string? OnChat(BlueprintService s, string? player, string text)
    {
        if (string.IsNullOrWhiteSpace(player) || string.IsNullOrWhiteSpace(text)) return null;
        if (s.Pending(player) is not { } rec) return null;
        if (IsAcceptance(text))
        {
            if (rec.Status == DesignStatus.Designing) return null; // nothing to accept yet: the brain answers
            var r = s.Accept(rec, player);
            _ = s.Say(r);
            return $"design #{rec.Id}: {player} said \"{text.Trim()}\" → {r}";
        }
        if (IsChange(text))
        {
            bool allowed = s.IsTrusted(player) || rec.Requester?.Equals(player, StringComparison.OrdinalIgnoreCase) == true;
            if (!allowed || rec.Status == DesignStatus.Designing) return null;
            var r = s.Revise(rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), text.Trim(), player);
            if (r.StartsWith("error", StringComparison.Ordinal)) return null;
            _ = s.Say($"ok, changing the {rec.Name}: I'll post the new version");
            return $"design #{rec.Id}: {player}'s change \"{text.Trim()}\" → {r}";
        }
        return null;
    }
}
