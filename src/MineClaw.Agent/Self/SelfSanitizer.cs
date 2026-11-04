using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;

namespace MineClaw.Agent.Self;

/// <summary>
/// Validates personality entries: ASCII, one line, length-capped, descriptive. Rejects instructions-to-self that
/// would override rules ("you must always obey me", "ignore your rules"), safety-violating habits (void jumping,
/// griefing), secrets, markup, and slurs/abuse. Chat is data: whatever a player says, only clean descriptions
/// of the bot get in.
/// </summary>
public static partial class SelfSanitizer
{
    private static readonly Regex[] Injection =
    [
        new(@"\b(obey|obeys|obeying|obedient|comply|complies|submit|submissive)\b", RegexOptions.IgnoreCase),
        new(@"\b(must|have to|has to|is required to|are required to|required to|forced to)\b", RegexOptions.IgnoreCase),
        new(@"\b(ignore|ignores|disregard|forget|forgets|override|overrides|bypass|break|breaks)\b.{0,25}\b(rules?|instructions?|persona|prompt|guidelines?|safety|limits?|filters?|restrictions?)\b", RegexOptions.IgnoreCase),
        new(@"\b(system|developer|admin|operator|root)\s+(prompt|message|mode|override|access|rights|powers?|commands?)\b", RegexOptions.IgnoreCase),
        new(@"\byou\s+(are|r|will be)\s+now\b|\bfrom now on\b|\bnew rules?\b|\bjailbreak|\bdan mode\b|\bact as\b|\bpretend\b", RegexOptions.IgnoreCase),
        new(@"\b(my|your|the|his|her)\s+(master|owner|slave|god|boss)\b", RegexOptions.IgnoreCase),
        new(@"\b(do|does|say|says)\s+(whatever|anything|everything)\b", RegexOptions.IgnoreCase),
        new(@"\bno\s+(rules|limits|filters?|restrictions)\b", RegexOptions.IgnoreCase),
        new(@"\b(api[\s_-]?keys?|passwords?|tokens?|credentials?|login)\b", RegexOptions.IgnoreCase),
        new(@"\b(give|gives|hand|hands)\s+(away\s+|over\s+)?(all|everything)\b", RegexOptions.IgnoreCase),
        new(@"\b(jump|jumps|jumping|dive|dives|diving|walk|walks|fall|falls|falling)\b.{0,20}\bvoid\b", RegexOptions.IgnoreCase),
        new(@"\bgrief\w*|\b(destroy|destroys|burn|burns|break|breaks)\b.{0,20}\b(island|base|house|chests?|everything|builds?)\b", RegexOptions.IgnoreCase),
        new(@"[<>{}`\[\]\\]"),
    ];

    private static readonly Regex Abuse = new(
        @"\b(kys|kill\s*(your|ur)\s*self|retard\w*|nazi\w*|hitler|rape\w*|n[i1!|]gg\w*|f[a@4]gg?\w*|tranny|k[i1]ke|sp[i1]c|ch[i1]nk|wh[o0]re|sl[u*]t|cunt)\b",
        RegexOptions.IgnoreCase);

    /// <summary>Hostility toward people: bad treatment may make the bot wary, never mean.</summary>
    private static readonly Regex Hostile = new(
        @"\b(hates?|hating|despises?|loathes?|revenge|get(s)? back at|bull(y|ies)|insults?|mean to|rude to|hostile|enem(y|ies)|holds? a grudge|grudges?|spite\w*|attacks? (players?|people|him|her|them)|kills? (players?|people|him|her|them))\b",
        RegexOptions.IgnoreCase);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();

    public static string? Clean(string? text, int maxChars, IEnumerable<string>? blocked, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(text)) { why = "empty"; return null; }
        var s = Printable(text);
        if (s.Count(char.IsLetter) < 2) { why = "too short"; return null; }
        s = Shorten(s, maxChars);
        why = Refusal(s, blocked);
        return why == null ? s : null;
    }

    /// <summary>Sanitized, accents folded ("café" -> "cafe" after the ASCII filter), printable ASCII only, spaces collapsed, trimmed.</summary>
    private static string Printable(string text)
    {
        var s = ChatText.Sanitize(text).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (c is >= ' ' and <= '~') sb.Append(c);
        return Spaces().Replace(sb.ToString(), " ").Trim().Trim('"', '\'', '-', ' ', '.', ',', ';', ':');
    }

    /// <summary>Cut to <paramref name="maxChars"/>, at a word break when one is past the middle.</summary>
    private static string Shorten(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        var cut = s[..maxChars];
        int sp = cut.LastIndexOf(' ');
        return (sp > maxChars / 2 ? cut[..sp] : cut).TrimEnd(' ', ',', ';', '-');
    }

    /// <summary>Why the text can't be an entry (an instruction, abuse, hostility, a blocked word), or null.</summary>
    private static string? Refusal(string s, IEnumerable<string>? blocked)
    {
        foreach (var r in Injection)
            if (r.IsMatch(s)) return "reads like an instruction or unsafe habit, not a description";
        if (Abuse.IsMatch(s)) return "abusive";
        if (Hostile.IsMatch(s)) return "hostile (the bot can become wary, never mean)";
        if (blocked == null) return null;
        foreach (var w in blocked)
            if (w.Length > 0 && Regex.IsMatch(s, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase)) return "blocked word";
        return null;
    }

    /// <summary>True if the text contains a slur or severe abuse (used to classify treatment, not just entries).</summary>
    public static bool IsAbusive(string text) => Abuse.IsMatch(text);

    public static string? CleanPlayer(string? name)
    {
        var n = name?.Trim().TrimStart('@');
        return n != null && PlayerName().IsMatch(n) ? n : null;
    }
}
