using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MineClaw.Client;

/// <summary>
/// Chat helpers. The b1.8.1 server kicks ("Illegal characters in chat") for any character outside the font's allowed
/// set, and for lines over 100 characters, so everything sent goes through <see cref="Sanitize"/> and <see cref="Split"/>.
/// We restrict to printable ASCII minus the backtick (the beta font maps that cell to an apostrophe and the server rejects
/// it), transliterating common Unicode punctuation and stripping diacritics.
/// </summary>
public static partial class ChatText
{
    public const int MaxLength = 100;

    public static bool IsAllowed(char c) => c >= 0x20 && c <= 0x7E && c != '`';

    /// <summary>The characters <see cref="Sanitize"/> transliterates (smart quotes, dashes, odd spaces, ligatures…).</summary>
    private static readonly Dictionary<char, string> Transliterated = new()
    {
        ['‘'] = "'",
        ['’'] = "'",
        ['‚'] = "'",
        ['′'] = "'",
        ['`'] = "'",
        ['´'] = "'",
        ['“'] = "\"",
        ['”'] = "\"",
        ['„'] = "\"",
        ['″'] = "\"",
        ['«'] = "\"",
        ['»'] = "\"",
        ['–'] = "-",
        ['—'] = "-",
        ['−'] = "-",
        ['‐'] = "-",
        ['‑'] = "-",
        ['…'] = "...",
        ['\u00A0'] = " ",
        ['\t'] = " ",
        ['\r'] = " ",
        ['\n'] = " ",
        ['\u2009'] = " ",
        ['\u202F'] = " ",
        ['•'] = "*",
        ['·'] = "*",
        ['→'] = "->",
        ['←'] = "<-",
        ['×'] = "x",
        ['ß'] = "ss",
        ['æ'] = "ae",
        ['Æ'] = "AE",
        ['ø'] = "o",
        ['Ø'] = "O",
        ['œ'] = "oe",
    };

    /// <summary>Maps text to the allowed character set: smart quotes → ASCII, accents stripped, newlines → spaces,
    /// colour codes and unknown characters dropped, whitespace collapsed.</summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        foreach (char raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            if (raw == '§') { sb.Append(raw); continue; } // colour code marker: removed with its code below
            if (Transliterated.TryGetValue(raw, out var mapped)) sb.Append(mapped);
            else if (IsAllowed(raw)) sb.Append(raw);
        }
        // Drop "§x" colour codes, then any remaining § and collapse whitespace.
        var s = ColourCode().Replace(sb.ToString(), "").Replace("§", "");
        s = Spaces().Replace(s, " ").Trim();
        return s;
    }

    /// <summary>Splits sanitized text into lines of at most <paramref name="max"/> characters, breaking at spaces
    /// when possible. A leading '/' (command) is never split off into a continuation line.</summary>
    public static List<string> Split(string text, int max = MaxLength)
    {
        var lines = new List<string>();
        var s = text.Trim();
        while (s.Length > max)
        {
            int cut = s.LastIndexOf(' ', max);
            if (cut < max / 2) cut = max; // no good space: hard cut
            lines.Add(s[..cut].TrimEnd());
            s = s[cut..].TrimStart();
        }
        if (s.Length > 0) lines.Add(s);
        return lines;
    }

    /// <summary>Sanitize + split in one go.</summary>
    public static List<string> Prepare(string text, int max = MaxLength) => Split(Sanitize(text), max);

    /// <summary>Removes § colour codes from server chat.</summary>
    public static string StripColours(string raw) => ColourCode().Replace(raw, "").Replace("§", "");

    /// <summary>Parses vanilla "&lt;name&gt; message" chat. Returns false for server/system lines.</summary>
    public static bool TryParsePlayerChat(string raw, out string sender, out string message)
    {
        var m = PlayerLine().Match(StripColours(raw));
        if (m.Success)
        {
            sender = m.Groups[1].Value;
            message = m.Groups[2].Value;
            return true;
        }
        sender = message = string.Empty;
        return false;
    }

    [GeneratedRegex("§[0-9a-fk-orA-FK-OR]")]
    private static partial Regex ColourCode();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^<([A-Za-z0-9_]{1,16})> ?(.*)$")]
    private static partial Regex PlayerLine();
}
