using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="SignText.Runs"/> (WP07) against the pre-refactor implementation, kept here verbatim as the reference: every §
/// code (upper and lower case, unknown ones, a trailing §) in random mixes with text.
/// </summary>
public class SignTextEquivalenceTests
{
    private static readonly string[] Colors =
        ["black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray", "dark_gray", "blue", "green", "aqua", "red",
         "light_purple", "yellow", "white"];

    private static List<SignRun> Reference(string s)
    {
        var runs = new List<SignRun>();
        string? color = null;
        bool bold = false, italic = false, under = false, strike = false, obf = false;
        var text = new System.Text.StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            var run = new SignRun(text.ToString(), color, bold ? true : null, italic ? true : null, under ? true : null, strike ? true : null, obf ? true : null);
            text.Clear();
            if (runs.Count > 0 && runs[^1] with { Text = "" } == run with { Text = "" }) runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
            else runs.Add(run);
        }
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '§' && i + 1 < s.Length)
            {
                int code = "0123456789abcdefklmnor".IndexOf(char.ToLowerInvariant(s[++i]));
                Flush();
                if (code < 16)
                {
                    obf = bold = strike = under = italic = false;
                    color = Colors[code < 0 ? 15 : code];
                }
                else if (code == 16) obf = true;
                else if (code == 17) bold = true;
                else if (code == 18) strike = true;
                else if (code == 19) under = true;
                else if (code == 20) italic = true;
                else
                {
                    obf = bold = strike = under = italic = false;
                    color = null;
                }
                continue;
            }
            text.Append(c);
        }
        Flush();
        return runs;
    }

    [Fact]
    public void Runs_match_the_reference()
    {
        const string Codes = "0123456789abcdefklmnorABCDEFKLMNORzZ§ ";
        var rng = new Random(1122);
        var fixedCases = new[] { "", "§", "a§", "§§", "§§a", "§l", "§lA§lB", "§cA§cB", "§k§l§m§n§oX", "§rX§r", "plain", "§zX§fY", "A§LB§Rc" };
        foreach (var s in fixedCases) Assert.Equal(Reference(s), SignText.Runs(s));
        for (int n = 0; n < 5000; n++)
        {
            var chars = new char[rng.Next(0, 24)];
            for (int i = 0; i < chars.Length; i++)
                chars[i] = rng.Next(3) switch { 0 => '§', 1 => Codes[rng.Next(Codes.Length)], _ => (char)('a' + rng.Next(3)) };
            var s = new string(chars);
            Assert.Equal(Reference(s), SignText.Runs(s));
        }
    }
}
