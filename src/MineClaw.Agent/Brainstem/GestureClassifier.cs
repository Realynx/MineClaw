using System.Text.RegularExpressions;

namespace MineClaw.Agent.Brainstem;

/// <summary>What a line says with the body, and which rule said so.</summary>
public readonly record struct GestureHit(string Gesture, string Rule);

/// <summary>
/// Static, data-driven text → gesture matching (zero tokens). The bot's own line is read like a person would nod along:
/// the <b>opening</b> carries agreement/refusal ("Got it foxy, ..." nods even if a "sorry" follows later), greetings and
/// thanks count in the first sentence, excitement anywhere. A leading "Name," vocative is skipped ("Foxy, sorry ..." shakes).
/// "/me bows" and "*waves*" map their verb directly and win over the text. Rules are checked in order; the first match wins;
/// config rows (<see cref="GestureOptions.Table"/>) come first.
/// </summary>
public sealed class GestureClassifier
{
    private readonly List<(string Scope, Regex Rx, string Gesture, string Id)> _rules = new();

    public static readonly GestureRule[] Defaults =
    [
        // /me actions (the text after "/me", or "*...*")
        new("emote", @"^(\w+ly\s+)?(bows?|curtsies|curtsy|curtseys?|curtsied|bowing)\b", "bow"),
        new("emote", @"^(\w+ly\s+)?(waves?|waving)\b", "wave"),
        new("emote", @"^(\w+ly\s+)?(nods?|nodding)\b", "nod"),
        new("emote", @"^(\w+ly\s+)?shakes\s+(her|his|its|their|my)\s+head\b", "shake"),
        new("emote", @"^(\w+ly\s+)?(hops?|jumps?|bounces?|bouncing|hopping)\b", "hop"),
        new("emote", @"^(\w+ly\s+)?(giggles?|squeals?|cheers?|wiggles?|dances?|claps?|squees?|crouch(es)?|bobs?)\b", "bob"),
        new("emote", @"^(\w+ly\s+)?(looks?|turns?|glances?|peeks?)\s+(over\s+)?(at|to|toward|towards)\b", "look"),
        // the opening of a chat line
        new("start", @"^(hi+|hello+|hey+|heya|hiya|howdy|hai+|welcome( back)?|good (morning|afternoon|evening))\b", "greet"),
        new("start", @"^\S+\s+(hi+|hello+|hey+|hai+)\b", "greet"), // "ROBIN hiii uwu"
        new("start", @"^(yes+|yep+|yeah+|yup+|ya|ok(ay|ie|ies)?|kk?|sure( thing)?|of course|(i'?m\s+)?on it|got it|gotcha|will do|roger|aye|alright|right away|absolutely|you got it|can do|coming right up|on my way|deal)\b", "nod"),
        new("start", @"^(no+|nope|nah|nuh uh|sorry|sowwy|oops|i\s+can'?t|i\s+cannot|i\s+won'?t|can'?t|won'?t|not\s+(yet|now|right now))\b", "shake"),
        // the first sentence
        new("head", @"\b(thank(s+| you| u)|thx|ty|tysm|thankies|thank)\b", "bow"),
        // anywhere
        new("any", @"\b(yay+|woo+(hoo+)?|yippee+|hooray|wahoo|let'?s\s+go+)\b|\buwu\s*!|\bowo\s*!", "excite"),
        new("head", @"\b(sorry|sowwy)\b", "shake"),
    ];

    /// <summary>A player's short yes/no (answers to the bot's own question).</summary>
    private static readonly Regex YesNo = new(@"^(yes+|yeah+|yep+|yup+|ya|sure|ok(ay|ie)?|no+|nope|nah)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Foxy, " / "PoofimaFox, " / "foxy sama, ": one word (+ an honorific), not "lmaooo fair, no cap ...".</summary>
    private static readonly Regex Vocative = new(@"^[\w~\-']+(\s+(sama|chan|san|kun|senpai|sensei)~?)?\s*,\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InlineEmote = new(@"(?<![\w*])\*(?<a>[a-z][^*\n]{0,60})\*(?![\w*])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public GestureClassifier(IEnumerable<GestureRule>? overrides = null, bool replaceDefaults = false)
    {
        int i = 0;
        foreach (var r in (overrides ?? []).Concat(replaceDefaults ? [] : Defaults))
        {
            i++;
            if (string.IsNullOrWhiteSpace(r.Pattern) || string.IsNullOrWhiteSpace(r.Gesture)) continue;
            Regex rx;
            try { rx = new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { continue; } // a bad config row never breaks chat
            _rules.Add(((r.Scope ?? "start").Trim().ToLowerInvariant(), rx, r.Gesture.Trim().ToLowerInvariant(), $"{r.Scope}#{i}"));
        }
    }

    /// <summary>
    /// The gesture for a whole outgoing text (several lines, "/me" lines, inline "*bows*"): the first emote that maps wins,
    /// else the first chat line's classification. Null = no gesture ("none" rows also return null).
    /// </summary>
    public GestureHit? ClassifyOutgoing(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var chat = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
            {
                if (Emote(line[4..]) is { } e) return Real(e);
                continue;
            }
            if (line.StartsWith('/')) continue; // /tell etc.: a command, not body language
            // "Hai foxy /me curtsies" (inline /me) and "*bows*"
            int me = line.IndexOf(" /me ", StringComparison.OrdinalIgnoreCase);
            if (me >= 0 && Emote(line[(me + 5)..]) is { } ie) return Real(ie);
            foreach (Match m in InlineEmote.Matches(line))
                if (Emote(m.Groups["a"].Value) is { } se) return Real(se);
            chat.Add(me >= 0 ? line[..me] : line);
        }
        return chat.Count == 0 ? null : Real(Chat(chat[0]));
    }

    /// <summary>A /me action ("bows deeply and holds the bread close") → its gesture, or null.</summary>
    public GestureHit? Emote(string action)
    {
        var a = Clean(action);
        if (a.Length == 0) return null;
        foreach (var r in _rules)
            if (r.Scope == "emote" && Safe(r.Rx, a)) return new(r.Gesture, r.Id);
        return null;
    }

    /// <summary>One chat line → its gesture (a "none" row returns that, so callers can tell "silenced" from "no rule").</summary>
    public GestureHit? Chat(string line)
    {
        var t = Clean(line);
        if (t.Length == 0) return null;
        var lead = t;
        var stripped = Vocative.Match(t) is { Success: true } v && v.Length < t.Length ? t[v.Length..] : null;
        int end = t.IndexOfAny(['.', '!', '?', ';']);
        var head = end > 0 ? t[..(end + 1)] : t;
        foreach (var r in _rules)
        {
            bool hit = r.Scope switch
            {
                "start" => Safe(r.Rx, lead) || (stripped != null && Safe(r.Rx, stripped)),
                "head" => Safe(r.Rx, head),
                "any" => Safe(r.Rx, t),
                _ => false,
            };
            if (hit) return new(r.Gesture, r.Id);
        }
        return null;
    }

    /// <summary>"yes", "nope", "sure, go ahead": a short yes/no reply.</summary>
    public static bool IsYesNo(string text)
    {
        var t = Clean(text);
        return YesNo.IsMatch(t) && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6;
    }

    private static GestureHit? Real(GestureHit? h) => h is { Gesture: not ("none" or "") } ? h : null;

    private static string Clean(string s) => (s ?? "").Trim().TrimStart('*', '~', '-', ' ').Trim();

    private static bool Safe(Regex rx, string s)
    {
        try { return rx.IsMatch(s); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
