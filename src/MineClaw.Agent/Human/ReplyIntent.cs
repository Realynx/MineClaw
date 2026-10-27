using System.Text.RegularExpressions;

namespace MineClaw.Agent.Human;

/// <summary>
/// Cheap keyword reading of loose player replies while the bot waits on them ("did it", "there", "like that", "here is the
/// cell", "yep"). Players teach conversationally; no exact keyword is required.
/// </summary>
public static partial class ReplyIntent
{
    // Whole-reply forms ("ok", "there", "see?", "yep") and phrases anywhere in a short reply ("ok I did it", "that's how").
    [GeneratedRegex(@"^\W*(?:ok(?:ay)?|k|kk|there|here|see|yep|yup|yes|ye|ya|yea|yeah|done|finished|did it|that'?s it|like (?:that|this)|ta-?da|voila|got it|now)\W*(?:\w+\W*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShortDoneRx();

    [GeneratedRegex(@"\b(?:done|finished|all done|did it|i did|that'?s (?:it|all|how)|like (?:that|this)|see (?:that|this|it|\?)|there you go|here (?:it is|is the|you go)|this (?:one|is it)|that one|there it is|ok that'?s all)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DonePhraseRx();

    [GeneratedRegex(@"\b(?:here|this (?:spot|one|cell|block)|right here|over here|i'?m (?:here|there|on it)|standing (?:here|on it)|there|this is (?:it|the)|ok|okay|yep|yup|now)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HereRx();

    [GeneratedRegex(@"\b(?:wait|hold on|not (?:yet|done)|one sec|1 sec|sec\b|hang on|almost|brb)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotYetRx();

    /// <summary>"done", "did it", "there", "ok", "finished", "that's it", "like that", "see", "here is the cell", "yep"...</summary>
    public static bool IsCompletion(string? reply)
    {
        var t = Strip(reply);
        if (t.Length == 0 || NotYetRx().IsMatch(t)) return false;
        return ShortDoneRx().IsMatch(t) || (t.Length <= 60 && DonePhraseRx().IsMatch(t));
    }

    /// <summary>ask_stand: "here", "here is the cell", "this spot", "ok", "i'm here"... (not "wait" / "not yet").</summary>
    public static bool IsHere(string? reply)
    {
        var t = Strip(reply);
        if (t.Length == 0 || NotYetRx().IsMatch(t)) return false;
        return HereRx().IsMatch(t) || IsCompletion(t);
    }

    /// <summary>Waiter replies arrive as "Player: text"; strips the name.</summary>
    public static string Strip(string? reply)
    {
        var t = (reply ?? "").Trim();
        int c = t.IndexOf(": ", StringComparison.Ordinal);
        if (c > 0 && c <= 16 && !t[..c].Contains(' ')) t = t[(c + 2)..];
        return t.Trim();
    }
}
