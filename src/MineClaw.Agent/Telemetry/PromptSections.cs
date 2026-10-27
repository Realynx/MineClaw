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
public sealed record TurnInput(DateTimeOffset At, ModelTier Tier, string Header, string Digest, string Memory, string Events, string Text);

/// <summary>Per-section token estimate of a request, and calibration of the estimate to the API's actual prompt tokens.</summary>
public static class PromptSections
{
    /// <summary>Markers <see cref="Prompts.System"/> puts in front of the knowledge part of the static block.</summary>
    private static readonly string[] KnowledgeMarkers = ["\n# Skyblock core rules\n", "\n# Skyblock knowledge\n"];

    /// <summary>Raw estimate (≈3.5 chars/token, same estimator as the context guard) per section. Also returns the round.</summary>
    /// <param name="turn">The current brain turn, if any: its message is split into memory / digest / events; earlier messages
    /// are history, later ones this turn's tool traffic. Without it the first user message is "events" and the rest turn_tools.</param>
    public static (Dictionary<string, int> Sections, int Round) Estimate(LlmRequest r, TurnInput? turn = null)
    {
        var s = PromptSection.All.ToDictionary(k => k, _ => 0);
        s[PromptSection.Tools] = Worker.WorkerAgent.ToolTokens(r.Tools);

        for (int i = 0; i < r.System.Count; i++)
        {
            var text = r.System[i].Text;
            if (i > 0) { s[PromptSection.Identity] += TextUtil.EstimateTokens(text); continue; }
            int k = KnowledgeMarkers.Select(m => text.IndexOf(m, StringComparison.Ordinal)).Where(x => x >= 0).DefaultIfEmpty(-1).Min();
            if (k < 0) s[PromptSection.System] += TextUtil.EstimateTokens(text);
            else
            {
                s[PromptSection.System] += TextUtil.EstimateTokens(text[..k]);
                s[PromptSection.Knowledge] += TextUtil.EstimateTokens(text[k..]);
            }
        }

        var msgs = r.Messages;
        int anchor = -1, anchorBlock = -1;
        if (turn != null)
        {
            for (int i = msgs.Count - 1; i >= 0 && anchor < 0; i--)
            {
                if (msgs[i].Role != LlmRole.User) continue;
                for (int j = 0; j < msgs[i].Content.Count; j++)
                    if (msgs[i].Content[j] is LlmText t && t.Text == turn.Text) { anchor = i; anchorBlock = j; break; }
            }
        }

        int round = 0;
        if (anchor >= 0)
        {
            for (int i = 0; i < anchor; i++) s[PromptSection.History] += Tokens(msgs[i]);
            var am = msgs[anchor];
            for (int j = 0; j < am.Content.Count; j++)
            {
                if (j != anchorBlock) { s[PromptSection.History] += Tokens(am.Content[j]); continue; }
                int total = TextUtil.EstimateTokens(turn!.Text);
                int dig = Math.Min(total, TextUtil.EstimateTokens(turn.Digest));
                int mem = Math.Min(total - dig, TextUtil.EstimateTokens(turn.Memory));
                s[PromptSection.Digest] += dig;
                s[PromptSection.Memory] += mem;
                s[PromptSection.Events] += total - dig - mem + 4;
            }
            for (int i = anchor + 1; i < msgs.Count; i++)
            {
                s[PromptSection.TurnTools] += Tokens(msgs[i]);
                if (msgs[i].Role == LlmRole.Assistant) round++;
            }
        }
        else
        {
            // No turn (worker, ask_memory, compaction...): the first user message is the task/prompt. A brain turn whose text
            // was cut by the hard-cap guard: the newest user message with text (not only tool results) starts the turn.
            int first = -1;
            if (turn != null)
            {
                for (int i = msgs.Count - 1; i >= 0 && first < 0; i--)
                    if (msgs[i].Role == LlmRole.User && msgs[i].Content.Any(b => b is LlmText t && !t.Text.StartsWith("[new events]"))) first = i;
            }
            else
                for (int i = 0; i < msgs.Count; i++) if (msgs[i].Role == LlmRole.User) { first = i; break; }
            for (int i = 0; i < msgs.Count; i++)
            {
                if (i == first) s[PromptSection.Events] += Tokens(msgs[i]);
                else s[i < first ? PromptSection.History : PromptSection.TurnTools] += Tokens(msgs[i]);
                if (i > first && msgs[i].Role == LlmRole.Assistant) round++;
            }
        }
        return (s, round);
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
