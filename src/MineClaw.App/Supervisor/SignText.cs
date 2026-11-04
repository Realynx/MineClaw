namespace MineClaw.App.Supervisor;

/// <summary>One styled piece of a sign line (<c>/api/world/blockentities</c> <c>rich</c>): its text and the styles that differ from
/// the sign's default (black, plain); a style that is off is left out.</summary>
public sealed record SignRun(string Text, string? Color = null, bool? Bold = null, bool? Italic = null, bool? Underlined = null,
    bool? Strikethrough = null, bool? Obfuscated = null);

/// <summary>
/// A 1.12.2 sign's styled lines as runs for the viewer, from the legacy § text the protocol made of the JSON components
/// (<see cref="MineClaw.Protocol.Release1122.ChatComponent.ToLegacy"/>: component styles, <c>extra</c> and § codes typed into the
/// text alike). The codes are read like <c>FontRenderer.renderStringAtPos</c>: a colour (and an unknown code, as white) resets
/// the styles, k obfuscated, l bold, m strikethrough, n underline, o italic, r back to the default. Pure.
/// </summary>
public static class SignText
{
    private static readonly string[] Colors =
        ["black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray", "dark_gray", "blue", "green", "aqua", "red",
         "light_purple", "yellow", "white"];

    /// <summary>Four lines of runs, or null when no line has a style (the plain <c>lines</c> say it all).</summary>
    public static IReadOnlyList<IReadOnlyList<SignRun>>? Rich(IReadOnlyList<string>? formatted)
    {
        if (formatted is null) return null;
        var lines = new List<IReadOnlyList<SignRun>>(4);
        bool styled = false;
        for (int i = 0; i < 4; i++)
        {
            var runs = Runs(i < formatted.Count ? formatted[i] : "");
            styled |= runs.Any(r => r.Color != null || r.Bold != null || r.Italic != null || r.Underlined != null || r.Strikethrough != null || r.Obfuscated != null);
            lines.Add(runs);
        }
        return styled ? lines : null;
    }

    /// <summary>One line's runs (adjacent pieces with the same style merged; an empty line has none).</summary>
    public static List<SignRun> Runs(string s)
    {
        var runs = new List<SignRun>();
        var style = default(SignStyle);
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '§' && i + 1 < s.Length)
            {
                Flush(runs, text, style);
                style = style.After(s[++i]);
                continue;
            }
            text.Append(c);
        }
        Flush(runs, text, style);
        return runs;
    }

    /// <summary>The pending text as a run in <paramref name="style"/>, merged into the last run when that has the same style.</summary>
    private static void Flush(List<SignRun> runs, System.Text.StringBuilder text, SignStyle style)
    {
        if (text.Length == 0) return;
        var run = style.Run(text.ToString());
        text.Clear();
        if (runs.Count > 0 && runs[^1] with { Text = "" } == run with { Text = "" }) runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
        else runs.Add(run);
    }

    /// <summary>The style in force while reading a line (default = black, plain).</summary>
    private readonly record struct SignStyle(string? Color, bool Bold, bool Italic, bool Under, bool Strike, bool Obf)
    {
        /// <summary>The style after a § code: a colour (an unknown code as white) resets the styles, k/l/m/n/o add one, r resets all.</summary>
        public SignStyle After(char codeChar)
        {
            int code = "0123456789abcdefklmnor".IndexOf(char.ToLowerInvariant(codeChar));
            return code switch
            {
                < 16 => new SignStyle(Colors[code < 0 ? 15 : code], false, false, false, false, false),
                16 => this with { Obf = true },
                17 => this with { Bold = true },
                18 => this with { Strike = true },
                19 => this with { Under = true },
                20 => this with { Italic = true },
                _ => default,
            };
        }

        public SignRun Run(string text) =>
            new(text, Color, Bold ? true : null, Italic ? true : null, Under ? true : null, Strike ? true : null, Obf ? true : null);
    }
}
