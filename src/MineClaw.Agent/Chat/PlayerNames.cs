using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>
/// Player identity for alias matching: exact account names win over nicknames, and a regular's nickname is never read inside
/// another online account's name (live 10-28 02:23: robinchan, Robin's own bot, was mourned as "robin got clapped by the
/// spider" and its pickaxe hand-over announced "to Robin"). "robin-chan" / "Robin Chan" are the account robinchan; "robin" is
/// kiwi only when no online account is called that.
/// </summary>
public static class PlayerNames
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>A name with case and separators dropped ("Robin-Chan" → "robinchan").</summary>
    public static string Key(string? name) =>
        new((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The login as a whole word, written loosely: any case, a space/_/- between its letters ("robin-chan").</summary>
    private static Regex Loose(string login)
    {
        var k = Key(login);
        // Short names only as written (a 3-4 letter login spelled with spaces would match ordinary words).
        string sep = k.Length >= 5 ? @"[\s_-]?" : "";
        return new Regex(@"(?<![A-Za-z0-9_])@?" + string.Join(sep, k.Select(c => Regex.Escape(c.ToString()))) + @"(?![A-Za-z0-9_])", O);
    }

    /// <summary>The text mentions the account, loosely written.</summary>
    public static bool MentionsLogin(string? text, string login) => Key(login).Length > 0 && Loose(login).IsMatch(text ?? "");

    /// <summary>Loose mentions of these logins rewritten to the exact login ("robin-chan look" → "robinchan look").</summary>
    public static string Canonical(string? text, IEnumerable<string> logins)
    {
        var t = text ?? "";
        foreach (var l in logins.Where(l => Key(l).Length > 0).OrderByDescending(l => Key(l).Length))
            t = Loose(l).Replace(t, l);
        return t;
    }

    /// <summary>The text with every mention of these logins blanked: what is left can be checked for nicknames.</summary>
    public static string WithoutLogins(string? text, IEnumerable<string> logins)
    {
        var t = text ?? "";
        foreach (var l in logins.Where(l => Key(l).Length > 0).OrderByDescending(l => Key(l).Length))
            t = Loose(l).Replace(t, " ");
        return t;
    }

    /// <summary>
    /// The online account <paramref name="name"/> means: an account called that (loosely) first, then a nickname. Null = nobody.
    /// </summary>
    public static string? Resolve(string? name, IEnumerable<string> online, Func<string, IEnumerable<string>> nicknamesOf)
    {
        var k = Key(name);
        if (k.Length == 0) return null;
        var list = online.ToList();
        if (list.FirstOrDefault(p => Key(p) == k) is { } login) return login;
        return list.FirstOrDefault(p => nicknamesOf(p).Any(n => Key(n) == k));
    }

    /// <summary>
    /// A bot line that calls another online account by a regular's nickname hidden in its name ("robin" for robinchan) while the
    /// turn is about that account and not the regular: the nickname becomes the account's name. <paramref name="turn"/> = the
    /// turn's waking events (player, text).
    /// </summary>
    public static string Unfold(string text, IReadOnlyCollection<(string? Player, string Text)> turn, IReadOnlyCollection<string> online,
        Func<string, IEnumerable<string>> nicknamesOf)
    {
        if (string.IsNullOrWhiteSpace(text) || online.Count < 2 || turn.Count == 0) return text;
        foreach (var account in online)
        {
            var ak = Key(account);
            bool aboutAccount = turn.Any(e => string.Equals(e.Player, account, StringComparison.OrdinalIgnoreCase) || MentionsLogin(e.Text, account));
            if (!aboutAccount) continue;
            foreach (var regular in online.Where(r => !string.Equals(r, account, StringComparison.OrdinalIgnoreCase)))
            {
                var nicks = nicknamesOf(regular).Where(n => Key(n).Length >= 3 && ak.Contains(Key(n)) && ak != Key(n)).ToList();
                if (nicks.Count == 0) continue;
                bool aboutRegular = turn.Any(e => string.Equals(e.Player, regular, StringComparison.OrdinalIgnoreCase)
                    || MentionsLogin(e.Text, regular)
                    || nicknamesOf(regular).Any(n => AddressingText.Mentions(WithoutLogins(e.Text, online), n)));
                if (aboutRegular) continue;
                foreach (var n in nicks)
                    text = Regex.Replace(text, @"(?<![A-Za-z0-9_])" + Regex.Escape(n) + @"(?![A-Za-z0-9_\-])", account, O);
            }
        }
        return text;
    }
}
