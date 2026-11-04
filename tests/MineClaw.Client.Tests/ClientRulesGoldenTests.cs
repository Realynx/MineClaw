using System.Text;

namespace MineClaw.Client.Tests;

/// <summary>
/// Exhaustive equivalence goldens for the client's pure rules (complexity refactor WP05), taken from the implementations before
/// they were reshaped: <see cref="ChatText.Sanitize"/> over every BMP character on its own and a set of mixed lines.
/// </summary>
public class ClientRulesGoldenTests
{
    [Fact]
    public void Chat_sanitize_matches_the_golden() => SimRulesGoldenTests.Check("chat-sanitize", "4975A5D008932063BF7DD8A65741E12F015D1BA781BEBB6FE045CAAA560CA860", sb =>
    {
        for (int c = 0; c <= 0xFFFF; c++)
        {
            if (c is >= 0xD800 and <= 0xDFFF) continue;
            string s;
            try { s = ChatText.Sanitize("a" + (char)c + "b"); }
            catch (ArgumentException) { s = "(throws)"; } // String.Normalize refuses noncharacters
            if (s != "ab") sb.Append(c).Append('=').Append(s).Append('\n');
        }
        string[] lines =
        [
            "", " ", "  hi   there  ", "§cred§r text §", "§", "café crème brûlée", "smart ‘quotes’ and “doubles” — dash… ok",
            "tab\there\nnew\r\nline", "→ ← × ß æ Æ ø Ø œ • ·", "`backtick´", "Ǆ ﬁ ½ ①", "ä̈", " nbsp thin narrow",
        ];
        foreach (var l in lines) sb.Append('[').Append(ChatText.Sanitize(l)).Append("]\n");
    });
}
