using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Context;

public sealed class ContextOptions
{
    /// <summary>Approximate token budget for the rolling history (journal + episodes) sent to the main tier.</summary>
    public int HistoryTokenBudget { get; set; } = 4000;
    /// <summary>After compaction, history should be at most this fraction of the budget.</summary>
    public double CompactTargetFraction { get; set; } = 0.5;
    /// <summary>The newest N episodes keep their tool results in full; older ones are truncated.</summary>
    public int KeepFullEpisodes { get; set; } = 2;
    public int OldToolResultChars { get; set; } = 80;
    public int JournalMaxChars { get; set; } = 1200;
    /// <summary>Cheap tier sees a flattened text digest of recent episodes, capped at this many chars.</summary>
    public int CheapDigestChars { get; set; } = 1200;
}

/// <summary>One model turn: the first user message (state + events) through the final assistant message.</summary>
public sealed class Episode
{
    public Episode(ModelTier tier, DateTimeOffset started)
    {
        Tier = tier;
        Started = started;
    }

    public ModelTier Tier { get; }
    public DateTimeOffset Started { get; }
    public List<LlmMessage> Messages { get; } = new();
}

/// <summary>
/// Rolling, bounded conversation context. Completed episodes are sealed (every tool_use has its tool_result),
/// stripped of thinking blocks (so later history edits never invalidate a replayed signature), old tool results
/// are truncated, and when over budget the oldest whole episodes are folded into a short journal — whole
/// episodes only, so tool_use/tool_result pairs are never split.
/// </summary>
public sealed class ContextManager
{
    private readonly ContextOptions _options;
    private readonly List<Episode> _episodes = new();
    private readonly object _lock = new();

    public ContextManager(ContextOptions? options = null) => _options = options ?? new ContextOptions();

    public string Journal { get; private set; } = "";

    public IReadOnlyList<Episode> Episodes { get { lock (_lock) return _episodes.ToArray(); } }

    public void SetJournal(string journal) { lock (_lock) Journal = journal; }

    /// <summary>Adds a finished episode (sealed + stripped) and truncates older tool results.</summary>
    public void Commit(Episode episode)
    {
        Seal(episode.Messages);
        StripThinking(episode.Messages);
        lock (_lock)
        {
            _episodes.Add(episode);
            for (int i = 0; i < _episodes.Count - _options.KeepFullEpisodes; i++)
                TruncateToolResults(_episodes[i].Messages, _options.OldToolResultChars);
        }
    }

    public int EstimateTokens()
    {
        lock (_lock) return TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => EstimateTokens(e.Messages));
    }

    public bool NeedsCompaction => EstimateTokens() > _options.HistoryTokenBudget;

    /// <summary>
    /// Folds the oldest episodes into the journal until history fits the target. <paramref name="summarize"/>
    /// gets a prompt and returns the new journal (cheap tier); on failure a mechanical digest is used instead.
    /// </summary>
    public async Task<int> CompactAsync(Func<string, CancellationToken, Task<string>>? summarize, CancellationToken ct)
    {
        List<Episode> old;
        string journal;
        lock (_lock)
        {
            int target = (int)(_options.HistoryTokenBudget * _options.CompactTargetFraction);
            int total = TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => EstimateTokens(e.Messages));
            int take = 0;
            while (take < _episodes.Count - 1 && total > target)
            {
                total -= EstimateTokens(_episodes[take].Messages);
                take++;
            }
            if (take == 0) return 0;
            old = _episodes.Take(take).ToList();
            journal = Journal;
        }

        var digest = string.Join("\n", old.Select(e => Flatten(e, 160)));
        string next;
        try
        {
            if (summarize == null) throw new InvalidOperationException("no summarizer");
            var prompt = $"Journal so far:\n{(journal.Length > 0 ? journal : "(empty)")}\n\nNewer log to fold in:\n{digest}";
            next = (await summarize(prompt, ct).ConfigureAwait(false)).Trim();
            if (next.Length == 0) throw new InvalidOperationException("empty summary");
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            next = (journal + "\n" + digest).Trim();
            // Keep the most recent part if the mechanical digest is too long.
            if (next.Length > _options.JournalMaxChars) next = "..." + next[^(_options.JournalMaxChars - 3)..];
        }
        next = TextUtil.Truncate(next, _options.JournalMaxChars);

        lock (_lock)
        {
            foreach (var e in old) _episodes.Remove(e);
            Journal = next;
        }
        return old.Count;
    }

    /// <summary>System prompt for the compaction call.</summary>
    public const string CompactionInstructions =
        "You maintain a Minecraft bot's memory journal. Merge the journal and the newer log into an updated journal: " +
        "at most 10 terse lines, oldest first. Keep open requests and promises, who asked for what, what was built or found and where " +
        "(coordinates), things learned about players, and current goals. Drop small talk and routine tool chatter. Output only the journal.";

    /// <summary>
    /// Messages for a request. Main tier: journal + structured past episodes + the current episode.
    /// Cheap tier: a flattened digest of recent episodes in the first user message + the current episode
    /// (cheap requests never carry main-tier tool blocks).
    /// </summary>
    public IReadOnlyList<LlmMessage> BuildMessages(ModelTier tier, IReadOnlyList<LlmMessage> current)
    {
        lock (_lock)
        {
            var result = new List<LlmMessage>();
            string prefix;
            if (tier == ModelTier.Main)
            {
                prefix = Journal.Length > 0 ? "[journal]\n" + Journal : "";
                foreach (var e in _episodes) result.AddRange(e.Messages);
            }
            else
            {
                var sb = new StringBuilder();
                if (Journal.Length > 0) sb.Append("[journal]\n").Append(Journal).Append('\n');
                var recent = new List<string>();
                int budget = _options.CheapDigestChars;
                for (int i = _episodes.Count - 1; i >= 0 && budget > 0; i--)
                {
                    var f = Flatten(_episodes[i], 100);
                    if (f.Length > budget) break;
                    budget -= f.Length;
                    recent.Insert(0, f);
                }
                if (recent.Count > 0) sb.Append("[recent]\n").Append(string.Join("\n", recent));
                prefix = sb.ToString().TrimEnd();
            }

            result.AddRange(current);
            if (prefix.Length > 0 && result.Count > 0 && result[0].Role == LlmRole.User)
            {
                var first = result[0];
                result[0] = first with { Content = [new LlmText(prefix), .. first.Content] };
            }
            return result;
        }
    }

    // ---------- helpers ----------

    /// <summary>Makes a message list well-formed: every tool_use answered, ends with an assistant message, no empty text.</summary>
    public static void Seal(List<LlmMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            var cleaned = m.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
            if (cleaned.Count == 0) cleaned.Add(new LlmText(m.Role == LlmRole.Assistant ? "(silent)" : "(nothing)"));
            if (cleaned.Count != m.Content.Count) messages[i] = m = m with { Content = cleaned };

            if (m.Role != LlmRole.Assistant) continue;
            var ids = m.ToolUses.Select(u => u.Id).ToList();
            if (ids.Count == 0) continue;
            var next = i + 1 < messages.Count ? messages[i + 1] : null;
            if (next == null || next.Role != LlmRole.User)
            {
                messages.Insert(i + 1, new LlmMessage(LlmRole.User, ids.Select(id => (LlmBlock)new LlmToolResult(id, "cancelled", true)).ToList()));
                continue;
            }
            var answered = next.ToolResults.Select(r => r.ToolUseId).ToHashSet();
            var missing = ids.Where(id => !answered.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                // tool_result blocks must come first in the user message.
                var blocks = missing.Select(id => (LlmBlock)new LlmToolResult(id, "cancelled", true)).ToList();
                messages[i + 1] = next with { Content = [.. next.Content.OfType<LlmToolResult>(), .. blocks, .. next.Content.Where(b => b is not LlmToolResult)] };
            }
        }
        if (messages.Count > 0 && messages[^1].Role == LlmRole.User)
            messages.Add(LlmMessage.Assistant("(stopped)"));
    }

    public static void StripThinking(List<LlmMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!m.Content.Any(b => b is LlmThinking or LlmRedactedThinking)) continue;
            var kept = m.Content.Where(b => b is not (LlmThinking or LlmRedactedThinking)).ToList();
            if (kept.Count == 0) kept.Add(new LlmText("(silent)"));
            messages[i] = m with { Content = kept };
        }
    }

    public static void TruncateToolResults(List<LlmMessage> messages, int maxChars)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!m.Content.Any(b => b is LlmToolResult r && r.Content.Length > maxChars)) continue;
            messages[i] = m with
            {
                Content = m.Content.Select(b => b is LlmToolResult r && r.Content.Length > maxChars
                    ? r with { Content = TextUtil.Truncate(r.Content, maxChars) } : b).ToList(),
            };
        }
    }

    public static int EstimateTokens(IEnumerable<LlmMessage> messages) =>
        messages.Sum(m => 4 + m.Content.Sum(b => b switch
        {
            LlmText t => TextUtil.EstimateTokens(t.Text),
            LlmToolUse u => 10 + TextUtil.EstimateTokens(u.Input.GetRawText()),
            LlmToolResult r => 8 + TextUtil.EstimateTokens(r.Content),
            LlmThinking th => TextUtil.EstimateTokens(th.Thinking) + 50,
            _ => 20,
        }));

    /// <summary>One compact text rendering of an episode (for the cheap tier and for compaction input).</summary>
    public static string Flatten(Episode e, int maxPart)
    {
        var parts = new List<string>();
        foreach (var m in e.Messages)
        {
            foreach (var b in m.Content)
            {
                switch (b)
                {
                    case LlmText t when m.Role == LlmRole.User:
                        // Drop the state header line; keep the events.
                        var lines = t.Text.Split('\n').Where(l => l.StartsWith('[') && !l.StartsWith("[journal]") && !l.StartsWith("[recent]"));
                        parts.AddRange(lines.Select(l => TextUtil.Truncate(l, maxPart)));
                        break;
                    case LlmText t when t.Text != "(silent)":
                        parts.Add("you: " + TextUtil.Truncate(TextUtil.OneLine(t.Text), maxPart));
                        break;
                    case LlmToolUse u:
                        parts.Add($"did {u.Name}({CompactArgs(u.Input)})");
                        break;
                    case LlmToolResult r:
                        parts.Add((r.IsError ? "  ! " : "  = ") + TextUtil.Truncate(TextUtil.OneLine(r.Content), Math.Min(maxPart, 80)));
                        break;
                }
            }
        }
        return string.Join("\n", parts);
    }

    private static string CompactArgs(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        var s = string.Join(",", input.EnumerateObject().Select(p =>
            p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()));
        return TextUtil.Truncate(s, 60);
    }
}
