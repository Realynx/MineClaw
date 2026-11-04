using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>Chat text helpers for b1.8.1: colour-code stripping, sanitising to the font's allowed chars, line splitting.</summary>
public static partial class ChatText
{
    /// <summary>b1.8.1 server limit for an incoming chat packet; longer messages get the client kicked.</summary>
    public const int MaxLineLength = 100;

    /// <summary>
    /// ChatAllowedCharacters from b1.8.1's font.txt. The server kicks ("Illegal characters in chat") for anything
    /// else — notably '§', backtick, tabs, newlines, emoji and most non-Latin-1 text.
    /// </summary>
    public const string AllowedCharacters =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_'abcdefghijklmnopqrstuvwxyz{|}~" +
        "⌂ÇüéâäàåçêëèïîìÄÅÉæÆ" +
        "ôöòûùÿÖÜø£Ø×ƒáíóúñÑª" +
        "º¿®¬½¼¡«»";

    private static readonly HashSet<char> Allowed = new(AllowedCharacters);

    private static readonly (string From, string To)[] Replacements =
    [
        ("‘", "'"), ("’", "'"), ("“", "\""), ("”", "\""), ("`", "'"),
        ("–", "-"), ("—", "-"), ("…", "..."), ("•", "*"), ("♥", "<3"), ("❤", "<3"),
        (" ", " "), ("\t", " "), ("\r", " "), ("\n", " "), ("→", "->"), ("←", "<-"),
    ];

    [GeneratedRegex("§.?", RegexOptions.Singleline)]
    private static partial Regex ColorCodes();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    /// <summary>Removes §x formatting codes.</summary>
    public static string StripColors(string s) => ColorCodes().Replace(s, "");

    public static bool IsAllowed(char c) => Allowed.Contains(c);

    /// <summary>Maps/strips everything the b1.8.1 server would reject, collapses whitespace and refuses leading '/'.</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = StripColors(text);
        foreach (var (from, to) in Replacements) text = text.Replace(from, to);
        // Strip markdown emphasis the model might emit.
        text = text.Replace("**", "").Replace("__", "");

        var sb = new StringBuilder(text.Length);
        foreach (var c in text) AppendAllowed(sb, c);
        var result = MultiSpace().Replace(sb.ToString(), " ").Trim();
        // Never let model text become a server command.
        while (result.StartsWith('/')) result = result[1..].TrimStart();
        return result;
    }

    /// <summary>The character if the server takes it, else its base letter (e.g. 'ő' -> 'o'); emoji and other surrogates dropped.</summary>
    private static void AppendAllowed(StringBuilder sb, char c)
    {
        if (Allowed.Contains(c)) { sb.Append(c); return; }
        if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c)) return; // emoji etc.
        var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
        foreach (var d in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(d) == UnicodeCategory.NonSpacingMark) continue;
            if (Allowed.Contains(d)) sb.Append(d);
        }
    }

    /// <summary>Splits into lines of at most <paramref name="max"/> chars on word boundaries (hard-splitting huge words).</summary>
    public static List<string> Split(string text, int max = MaxLineLength)
    {
        var lines = new List<string>();
        if (max <= 0 || string.IsNullOrWhiteSpace(text)) return lines;
        var current = new StringBuilder();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw;
            while (word.Length > max)
            {
                if (current.Length > 0) { lines.Add(current.ToString()); current.Clear(); }
                lines.Add(word[..max]);
                word = word[max..];
            }
            if (word.Length == 0) continue;
            if (current.Length == 0) current.Append(word);
            else if (current.Length + 1 + word.Length <= max) current.Append(' ').Append(word);
            else { lines.Add(current.ToString()); current.Clear().Append(word); }
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }
}
