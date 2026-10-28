namespace MineClaw.Agent.Bodies;

/// <summary>
/// The "[bodies]" line of a turn (one brain, many bodies; docs/design/one-brain-many-bodies.md section 2): every non-focus
/// body in one short part (≤ <see cref="MaxPartTokens"/>), offline bodies collapsed into one "off" part, the whole line at
/// most <see cref="MaxTokens"/> (≈3.5 chars/token). Uncached tail only, and only while two or more bodies are online; the
/// focus body keeps the state header and [here].
/// <code>[bodies] 67 gen@cobble_gen 42/64 hp18 f12 stone_pick free8 | 69 idle @main_house hp20 f20 | 420 DANGER hp6 | 1337 off</code>
/// </summary>
public static class BodyStatusBlock
{
    public const string Tag = "[bodies]";
    public const int MaxTokens = 120;
    public const int MaxPartTokens = 25;

    private const double CharsPerToken = 3.5;
    public static int MaxChars => (int)(MaxTokens * CharsPerToken);
    public static int MaxPartChars => (int)(MaxPartTokens * CharsPerToken);

    /// <summary>One body: its short name, whether it is online, and its status text ("gen@cobble_gen 42/64 hp18 f12").</summary>
    public sealed record Part(string Body, bool Online, string Text);

    /// <summary>The line, or "" when fewer than two bodies are online (or nothing to show besides the focus).</summary>
    public static string Render(IReadOnlyList<Part> bodies, string? focus)
    {
        if (bodies.Count(b => b.Online) < 2) return "";
        var parts = new List<string>();
        foreach (var b in bodies)
        {
            if (!b.Online || Same(b.Body, focus ?? "main")) continue;
            var text = b.Text.Trim();
            var part = text.Length > 0 ? $"{b.Body} {text}" : b.Body;
            parts.Add(Clip(part, MaxPartChars));
        }
        var off = bodies.Where(b => !b.Online && !Same(b.Body, focus ?? "main")).Select(b => b.Body).ToList();
        if (off.Count > 0) parts.Add(string.Join(",", off) + " off");
        if (parts.Count == 0) return "";
        var line = Tag + " " + string.Join(" | ", parts);
        if (line.Length <= MaxChars) return line;
        // Over the cap: shorten every part evenly (each body keeps its name and the start of its status), then hard-clip.
        int each = Math.Max(8, (MaxChars - Tag.Length - 1) / parts.Count - 3);
        line = Tag + " " + string.Join(" | ", parts.Select(p => Clip(p, each)));
        return Clip(line, MaxChars);
    }

    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)].TrimEnd() + "…";

    /// <summary>Token estimate the budget test pins (chars / 3.5, rounded up).</summary>
    public static int Tokens(string line) => (int)Math.Ceiling(line.Length / CharsPerToken);
}
