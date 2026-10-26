namespace MineClaw.Agent.Tools;

/// <summary>
/// Terse result of a tool call. <see cref="Text"/> is what the model sees, so keep it to one short line
/// (e.g. "mined 12 cobblestone (inv 44)"). Long text is truncated by <see cref="ToolRegistry"/>.
/// </summary>
public sealed record ToolResult(bool Ok, string Text)
{
    public static ToolResult Success(string text) => new(true, text);
    public static ToolResult Error(string text) => new(false, text);

    /// <summary>Returns a copy whose text is at most <paramref name="maxLength"/> chars (ellipsis marks the cut).</summary>
    public ToolResult Truncate(int maxLength) =>
        Text.Length <= maxLength ? this : this with { Text = TextUtil.Truncate(Text, maxLength) };

    public override string ToString() => (Ok ? "ok: " : "error: ") + Text;
}

internal static class TextUtil
{
    public static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 0) return "";
        if (text.Length <= maxLength) return text;
        if (maxLength <= 3) return text[..maxLength];
        return text[..(maxLength - 3)].TrimEnd() + "...";
    }

    /// <summary>Very rough token estimate (≈3.5 chars/token for English + JSON).</summary>
    public static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 3.5);

    public static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
