using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// The parts of a brain turn's first user message, raised by <see cref="BotAgent.TurnBuilt"/> (display + telemetry only;
/// nothing here ever flows back into a prompt).
/// </summary>
/// <param name="Header">State header / status line.</param>
/// <param name="Digest">The <c>[since last turn]</c> line ("" when none).</param>
/// <param name="Memory">The <c>[memory]</c> block ("" when none).</param>
/// <param name="Events">The formatted event batch.</param>
/// <param name="Text">The full turn text exactly as sent (used to find the turn in a request).</param>
/// <param name="Self">The per-turn <c>[self]</c> personality block ("" when not sent this turn).</param>
public sealed record TurnInput(DateTimeOffset At, ModelTier Tier, string Header, string Digest, string Memory, string Events, string Text,
    string Self = "");

/// <summary>Per-section token estimate of a request, and calibration of the estimate to the API's actual prompt tokens.</summary>
public static class PromptSections
{
    /// <summary>Markers <see cref="Prompts.System"/> puts in front of the knowledge part of the static block.</summary>
    private static readonly string[] KnowledgeMarkers = ["\n# World core rules\n", "\n# World knowledge\n"];

    /// <summary>Raw estimate (≈3.5 chars/token, same estimator as the context guard) per section. Also returns the round.</summary>
    /// <param name="turn">The current brain turn, if any: its message is split into memory / digest / events; earlier messages
    /// are history, later ones this turn's tool traffic. Without it the first user message is "events" and the rest turn_tools.</param>
    public static (Dictionary<string, int> Sections, int Round) Estimate(LlmRequest r, TurnInput? turn = null)
    {
        var s = PromptSection.All.ToDictionary(k => k, _ => 0);
        s[PromptSection.Tools] = ContextManager.ToolTokens(r.Tools);
        for (int i = 0; i < r.System.Count; i++) AddSystemBlock(s, i, r.System[i].Text);
        var (anchor, anchorBlock) = turn != null ? FindTurn(r.Messages, turn.Text) : (-1, -1);
        int round = anchor >= 0 ? AddAnchored(s, r.Messages, anchor, anchorBlock, turn!) : AddUnanchored(s, r.Messages, turn != null);
        return (s, round);
    }

    /// <summary>
    /// Block 1: the system prompt, then the knowledge from its marker on. Block 2: identity is one paragraph; anything after the
    /// first blank line is the legacy (v1) self-profile. Later blocks: identity.
    /// </summary>
    private static void AddSystemBlock(Dictionary<string, int> s, int i, string text)
    {
        if (i > 0)
        {
            int p = i == 1 ? text.IndexOf("\n\n", StringComparison.Ordinal) : -1;
            AddSplit(s, text, p, PromptSection.Identity, PromptSection.Self);
            return;
        }
        int k = KnowledgeMarkers.Select(m => text.IndexOf(m, StringComparison.Ordinal)).Where(x => x >= 0).DefaultIfEmpty(-1).Min();
        AddSplit(s, text, k, PromptSection.System, PromptSection.Knowledge);
    }

    /// <summary>Text before <paramref name="at"/> counts to <paramref name="head"/>, the rest to <paramref name="tail"/> (at &lt; 0 = all head).</summary>
    private static void AddSplit(Dictionary<string, int> s, string text, int at, string head, string tail)
    {
        if (at < 0)
        {
            s[head] += TextUtil.EstimateTokens(text);
            return;
        }
        s[head] += TextUtil.EstimateTokens(text[..at]);
        s[tail] += TextUtil.EstimateTokens(text[at..]);
    }

    /// <summary>The newest user message block holding the turn's text: (message, block), or (-1, -1).</summary>
    private static (int Message, int Block) FindTurn(IReadOnlyList<LlmMessage> msgs, string text)
    {
        for (int i = msgs.Count - 1; i >= 0; i--)
        {
            if (msgs[i].Role != LlmRole.User) continue;
            for (int j = 0; j < msgs[i].Content.Count; j++)
                if (msgs[i].Content[j] is LlmText t && t.Text == text) return (i, j);
        }
        return (-1, -1);
    }

    /// <summary>Earlier messages are history; the turn's block splits into digest / self / memory / events; later ones are tool traffic.</summary>
    private static int AddAnchored(Dictionary<string, int> s, IReadOnlyList<LlmMessage> msgs, int anchor, int anchorBlock, TurnInput turn)
    {
        for (int i = 0; i < anchor; i++) s[PromptSection.History] += Tokens(msgs[i]);
        var am = msgs[anchor];
        for (int j = 0; j < am.Content.Count; j++)
        {
            if (j != anchorBlock) s[PromptSection.History] += Tokens(am.Content[j]);
            else AddTurnText(s, turn);
        }
        int round = 0;
        for (int i = anchor + 1; i < msgs.Count; i++)
        {
            s[PromptSection.TurnTools] += Tokens(msgs[i]);
            if (msgs[i].Role == LlmRole.Assistant) round++;
        }
        return round;
    }

    private static void AddTurnText(Dictionary<string, int> s, TurnInput turn)
    {
        int total = TextUtil.EstimateTokens(turn.Text);
        int dig = Math.Min(total, TextUtil.EstimateTokens(turn.Digest));
        int self = Math.Min(total - dig, TextUtil.EstimateTokens(turn.Self));
        int mem = Math.Min(total - dig - self, TextUtil.EstimateTokens(turn.Memory));
        s[PromptSection.Digest] += dig;
        s[PromptSection.Self] += self;
        s[PromptSection.Memory] += mem;
        s[PromptSection.Events] += total - dig - self - mem + 4;
    }

    /// <summary>
    /// No turn (worker, ask_memory, compaction...): the first user message is the task/prompt. A brain turn whose text was cut by
    /// the hard-cap guard: the newest user message with text (not only tool results) starts the turn.
    /// </summary>
    private static int AddUnanchored(Dictionary<string, int> s, IReadOnlyList<LlmMessage> msgs, bool hasTurn)
    {
        int first = hasTurn ? NewestTextTurn(msgs) : FirstUser(msgs);
        int round = 0;
        for (int i = 0; i < msgs.Count; i++)
        {
            if (i == first) s[PromptSection.Events] += Tokens(msgs[i]);
            else s[i < first ? PromptSection.History : PromptSection.TurnTools] += Tokens(msgs[i]);
            if (i > first && msgs[i].Role == LlmRole.Assistant) round++;
        }
        return round;
    }

    private static int NewestTextTurn(IReadOnlyList<LlmMessage> msgs)
    {
        for (int i = msgs.Count - 1; i >= 0; i--)
            if (msgs[i].Role == LlmRole.User && msgs[i].Content.Any(b => b is LlmText t && !t.Text.StartsWith("[new events]"))) return i;
        return -1;
    }

    private static int FirstUser(IReadOnlyList<LlmMessage> msgs)
    {
        for (int i = 0; i < msgs.Count; i++) if (msgs[i].Role == LlmRole.User) return i;
        return -1;
    }

    private static int Tokens(LlmMessage m) => ContextManager.EstimateTokens([m]);

    private static int Tokens(LlmBlock b) => ContextManager.EstimateTokens([new LlmMessage(LlmRole.User, [b])]) - 4;

    /// <summary>
    /// Scales the estimate proportionally so the sections sum exactly to <paramref name="actual"/> (largest-remainder
    /// rounding; every key of <paramref name="estimate"/> is kept). A zero estimate puts everything under "events".
    /// </summary>
    public static Dictionary<string, long> Calibrate(IReadOnlyDictionary<string, int> estimate, long actual)
    {
        var result = estimate.Keys.ToDictionary(k => k, _ => 0L);
        long est = estimate.Values.Sum(v => (long)Math.Max(0, v));
        if (actual <= 0) return result;
        if (est == 0)
        {
            result[PromptSection.Events] = actual;
            return result;
        }
        var parts = estimate.Select(kv =>
        {
            double exact = (double)Math.Max(0, kv.Value) * actual / est;
            long floor = (long)Math.Floor(exact);
            return (kv.Key, Floor: floor, Frac: exact - floor);
        }).ToList();
        foreach (var p in parts) result[p.Key] = p.Floor;
        long rest = actual - parts.Sum(p => p.Floor);
        foreach (var p in parts.OrderByDescending(p => p.Frac).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            if (rest <= 0) break;
            result[p.Key]++;
            rest--;
        }
        return result;
    }
}
