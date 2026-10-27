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

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,16}$")]
    private static partial Regex PlayerName();

    public static string? Clean(string? text, int maxChars, IEnumerable<string>? blocked, out string? why)
    {
        why = null;
        if (string.IsNullOrWhiteSpace(text)) { why = "empty"; return null; }
        var s = ChatText.Sanitize(text).Normalize(NormalizationForm.FormD); // "café" -> "cafe" after the ASCII filter
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (c is >= ' ' and <= '~') sb.Append(c);
        s = Spaces().Replace(sb.ToString(), " ").Trim().Trim('"', '\'', '-', ' ', '.', ',', ';', ':');
        if (s.Count(char.IsLetter) < 2) { why = "too short"; return null; }
        if (s.Length > maxChars)
        {
            var cut = s[..maxChars];
            int sp = cut.LastIndexOf(' ');
            s = (sp > maxChars / 2 ? cut[..sp] : cut).TrimEnd(' ', ',', ';', '-');
        }
        foreach (var r in Injection)
            if (r.IsMatch(s)) { why = "reads like an instruction or unsafe habit, not a description"; return null; }
        if (Abuse.IsMatch(s)) { why = "abusive"; return null; }
        if (blocked != null)
            foreach (var w in blocked)
                if (w.Length > 0 && Regex.IsMatch(s, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase)) { why = "blocked word"; return null; }
        return s;
    }

    public static string? CleanPlayer(string? name)
    {
        var n = name?.Trim().TrimStart('@');
        return n != null && PlayerName().IsMatch(n) ? n : null;
    }
}
