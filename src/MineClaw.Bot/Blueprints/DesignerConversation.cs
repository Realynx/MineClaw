using System.Globalization;
using System.Text;
using MineClaw.Agent.Llm;

namespace MineClaw.Bot.Blueprints;

/// <summary>
/// The messages one Design Studio turn starts from: the last few exchanges of the transcript as plain text, then the user's
/// brief. Every provider gets a well-formed list: it starts with the user and alternates (same-role neighbours, such as a
/// session note after a reply or a message whose reply never came, are merged), and every text is non-empty and bounded.
/// An AI line carries the edits it made, so the model remembers what it did and not only what it said.
/// </summary>
public static class DesignerConversation
{
    /// <summary>Longest earlier line sent back (the canvas summary carries the state; a long line is mostly noise).</summary>
    public const int MaxLineChars = 1200;

    /// <summary>Edit labels named per AI line.</summary>
    private const int MaxEditLabels = 6;

    /// <summary>The last <paramref name="turns"/> exchanges (each from a user line on) of <paramref name="history"/>, then the brief.</summary>
    public static List<LlmMessage> Build(IReadOnlyList<DesignerMessageRecord> history, int turns, string brief)
    {
        var messages = new List<LlmMessage>();
        foreach (var h in Recent(history, turns))
            Append(messages, h.Role == "assistant" ? LlmRole.Assistant : LlmRole.User, Line(h));
        Append(messages, LlmRole.User, brief);
        return messages;
    }

    /// <summary>The records from the <paramref name="turns"/>-th last user line on (an AI line before it would lead the list).</summary>
    private static IEnumerable<DesignerMessageRecord> Recent(IReadOnlyList<DesignerMessageRecord> history, int turns)
    {
        if (turns <= 0) return [];
        int start = history.Count, users = 0;
        for (int i = history.Count - 1; i >= 0 && users < turns; i--)
            if (history[i].Role != "assistant")
            {
                start = i;
                users++;
            }
        return history.Skip(start);
    }

    /// <summary>One record as the model reads it: an AI line with the edits it made first, any line clipped.</summary>
    private static string Line(DesignerMessageRecord h)
    {
        var text = DesignerText.Clip(h.Text.Trim(), MaxLineChars);
        if (h.Role != "assistant" || h.Ops.Count == 0) return text.Length > 0 ? text : "(done)";
        var edits = string.Join("; ", h.Ops.Take(MaxEditLabels).Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Label} ({o.Cells})")))
            + (h.Ops.Count > MaxEditLabels ? $"; {h.Ops.Count - MaxEditLabels} more" : "");
        return $"[your edits: {edits}]" + (text.Length > 0 ? " " + text : "");
    }

    /// <summary>Adds a text turn, merged into the last message when it has the same role (the API wants alternating turns).</summary>
    private static void Append(List<LlmMessage> messages, LlmRole role, string text)
    {
        if (messages.Count == 0 && role != LlmRole.User) return;
        if (messages.Count > 0 && messages[^1].Role == role)
        {
            messages[^1] = new LlmMessage(role, [new LlmText(new StringBuilder(messages[^1].Text).Append("\n\n").Append(text).ToString())]);
            return;
        }
        messages.Add(new LlmMessage(role, [new LlmText(text)]));
    }
}

/// <summary>The Design Studio's text clipping (the transcript, the model's lines, notes).</summary>
public static class DesignerText
{
    /// <summary>At most <paramref name="n"/> chars, an ellipsis marking the cut.</summary>
    public static string Clip(string s, int n) => s.Length <= n ? s : s[..Math.Max(0, n - 1)] + "…";
}
