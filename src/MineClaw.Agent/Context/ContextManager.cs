using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Context;

public sealed class ContextOptions
{
    /// <summary>
    /// Main-tier history (journal + episodes, estimated tokens) that triggers compaction. History is append-only
    /// between compactions so every request re-reads it from the prompt cache; compaction is rare and batched.
    /// </summary>
    public int CompactAtTokens { get; set; } = 30_000;
    /// <summary>Compaction folds the oldest episodes until history is at most this (hysteresis).</summary>
    public int CompactToTokens { get; set; } = 20_000;
    /// <summary>
    /// Hard ceiling for any request's context (system + tools + history + current turn + max output). Enforced before
    /// every brain/cheap call: history is folded mechanically, then tool results and long texts are cut; if it still
    /// doesn't fit the call is skipped. Never exceeded.
    /// </summary>
    public int MaxTokens { get; set; } = 100_000;
    /// <summary>Token estimates are rough (≈3.5 chars/token); the guard keeps the estimate under MaxTokens × this.</summary>
    public double HardCapSafety { get; set; } = 0.85;
    /// <summary>Cheap tier sees a text-only transcript of the same episodes; it compacts at/to these sizes.</summary>
    public int CheapCompactAtTokens { get; set; } = 16_000;
    public int CheapCompactToTokens { get; set; } = 10_000;
    /// <summary>At compaction, the newest N surviving episodes keep their tool results in full; older ones are truncated.</summary>
    public int KeepFullEpisodes { get; set; } = 2;
    public int OldToolResultChars { get; set; } = 80;
    /// <summary>Older history is retrievable (RAG episodes), so the journal can stay short.</summary>
    public int JournalMaxChars { get; set; } = 900;
    /// <summary>Per-episode cap for the cheap tier's text rendering.</summary>
    public int CheapEpisodeChars { get; set; } = 500;
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
    /// <summary>Driven by injected test chat (console /as): indexed as source=test and kept out of reflection.</summary>
    public bool IsTest { get; init; }
    public List<LlmMessage> Messages { get; } = new();
    /// <summary>Monotonic sequence number assigned at commit.</summary>
    public long Seq { get; internal set; } = -1;
    internal LlmMessage? CheapUser { get; set; }
    internal LlmMessage? CheapAssistant { get; set; }
    internal int MainTokens { get; set; }
    internal int CheapTokens { get; set; }
}

/// <summary>
/// Rolling, bounded, prompt-cache-friendly conversation context. Committed episodes are sealed (every tool_use
/// answered) and stripped of thinking blocks, then never touched again until the next compaction: each request is
/// the previous one plus new messages, so the history prefix is byte-identical turn to turn and comes from cache.
/// When history passes <see cref="ContextOptions.CompactAtTokens"/> the oldest whole episodes are folded into a
/// short journal in one go (down to <see cref="ContextOptions.CompactToTokens"/>), and only then are older tool
/// results truncated — one cache rebuild per compaction, not per turn. The cheap tier gets an equally append-only
/// text transcript (user: events + actions, assistant: what was said), never main-tier tool blocks.
/// </summary>
public sealed class ContextManager
{
    private readonly ContextOptions _options;
    private readonly List<Episode> _episodes = new();
    private readonly object _lock = new();
    private long _nextSeq;

    public ContextManager(ContextOptions? options = null) => _options = options ?? new ContextOptions();

    public ContextOptions Options => _options;

    public string Journal { get; private set; } = "";

    public IReadOnlyList<Episode> Episodes { get { lock (_lock) return _episodes.ToArray(); } }

    /// <summary>Sequence number the next committed episode will get.</summary>
    public long NextSeq { get { lock (_lock) return _nextSeq; } }

    /// <summary>Lowest sequence number still in the window (= <see cref="NextSeq"/> when empty).</summary>
    public long OldestSeq { get { lock (_lock) return _episodes.Count > 0 ? _episodes[0].Seq : _nextSeq; } }

    /// <summary>Raised after an episode is committed (outside the lock).</summary>
    public event Action<Episode>? Committed;

    /// <summary>Raised after compaction with the folded episodes and the new journal (outside the lock).</summary>
    public event Action<IReadOnlyList<Episode>, string>? Compacted;

    public void SetJournal(string journal) { lock (_lock) Journal = journal; }

    /// <summary>Adds a finished episode (sealed + stripped). Never rewrites earlier history.</summary>
    public void Commit(Episode episode)
    {
        Seal(episode.Messages);
        StripThinking(episode.Messages);
        var (cu, ca) = CheapRendering(episode, _options.CheapEpisodeChars);
        episode.CheapUser = cu;
        episode.CheapAssistant = ca;
        episode.MainTokens = EstimateTokens(episode.Messages);
        episode.CheapTokens = EstimateTokens([cu, ca]);
        lock (_lock)
        {
            episode.Seq = _nextSeq++;
            _episodes.Add(episode);
        }
        Committed?.Invoke(episode);
    }

    public int EstimateTokens()
    {
        lock (_lock) return TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => e.MainTokens);
    }

    public int EstimateCheapTokens()
    {
        lock (_lock) return TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => e.CheapTokens);
    }

    public bool NeedsCompaction
    {
        get
        {
            int main = EstimateTokens(), cheap = EstimateCheapTokens();
            return main > _options.CompactAtTokens || cheap > _options.CheapCompactAtTokens || main > _options.MaxTokens;
        }
    }

    /// <summary>
    /// Folds the oldest episodes into the journal until both renderings fit their targets. <paramref name="summarize"/>
    /// gets a prompt and returns the new journal (cheap tier); on failure a mechanical digest is used instead.
    /// </summary>
    public async Task<int> CompactAsync(Func<string, CancellationToken, Task<string>>? summarize, CancellationToken ct)
    {
        List<Episode> old;
        string journal;
        lock (_lock)
        {
            int jt = TextUtil.EstimateTokens(Journal);
            int main = jt + _episodes.Sum(e => e.MainTokens);
            int cheap = jt + _episodes.Sum(e => e.CheapTokens);
            int take = 0;
            while (take < _episodes.Count - 1 && (main > _options.CompactToTokens || cheap > _options.CheapCompactToTokens))
            {
                main -= _episodes[take].MainTokens;
                cheap -= _episodes[take].CheapTokens;
                take++;
            }
            old = _episodes.Take(take).ToList();
            journal = Journal;
        }

        string next = journal;
        if (old.Count > 0)
        {
            var digest = string.Join("\n", old.Select(e => Flatten(e, 160)));
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
        }

        lock (_lock)
        {
            foreach (var e in old) _episodes.Remove(e);
            Journal = next;
            // The cache is being rebuilt anyway: this is the only time older tool results get truncated.
            for (int i = 0; i < _episodes.Count - _options.KeepFullEpisodes; i++) Retruncate(_episodes[i], _options.OldToolResultChars);
            int total = TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => e.MainTokens);
            if (total > _options.MaxTokens)
                foreach (var e in _episodes) Retruncate(e, Math.Max(40, _options.OldToolResultChars / 2));
        }
        if (old.Count > 0) Compacted?.Invoke(old, next);
        return old.Count;
    }

    private static void Retruncate(Episode e, int chars)
    {
        TruncateToolResults(e.Messages, chars);
        e.MainTokens = EstimateTokens(e.Messages);
    }

    /// <summary>
    /// Emergency, synchronous (no model call) squeeze of committed history to at most <paramref name="maxTokens"/> for
    /// <paramref name="tier"/>'s rendering: cut every old tool result hard, then fold whole episodes (oldest first, the newest
    /// too if needed) into the journal mechanically, then shorten the journal. Busts the history cache, so it only runs when
    /// a request would otherwise break <see cref="ContextOptions.MaxTokens"/>. Returns the resulting estimate.
    /// </summary>
    public int EnforceHardCap(int maxTokens, ModelTier tier)
    {
        maxTokens = Math.Max(0, maxTokens);
        List<Episode> folded = new();
        string journal;
        int total;
        lock (_lock)
        {
            int Measure() => TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => tier == ModelTier.Main ? e.MainTokens : e.CheapTokens);
            if (Measure() <= maxTokens) return Measure();
            foreach (var e in _episodes) Retruncate(e, 40);
            while (_episodes.Count > 0 && Measure() > maxTokens)
            {
                var e = _episodes[0];
                _episodes.RemoveAt(0);
                folded.Add(e);
            }
            if (folded.Count > 0)
            {
                var digest = string.Join("\n", folded.Select(e => Flatten(e, 80)));
                var next = (Journal + "\n" + digest).Trim();
                if (next.Length > _options.JournalMaxChars) next = "..." + next[^(_options.JournalMaxChars - 3)..];
                Journal = next;
            }
            // Last resort: the journal itself (bounded by JournalMaxChars, so only an absurdly small cap gets here).
            int journalBudget = maxTokens - _episodes.Sum(e => tier == ModelTier.Main ? e.MainTokens : e.CheapTokens);
            if (TextUtil.EstimateTokens(Journal) > journalBudget)
            {
                int chars = (int)(journalBudget * 3.5) - 4;
                Journal = chars > 20 ? TextUtil.Truncate(Journal, chars) : "";
            }
            journal = Journal;
            total = Measure();
        }
        if (folded.Count > 0) Compacted?.Invoke(folded, journal);
        return total;
    }

    /// <summary>
    /// Shrinks an in-flight message list (the current episode) to at most <paramref name="maxTokens"/>: tool results are cut
    /// (older messages first, then all), then the longest text blocks. Tool-use/result pairing is preserved. Returns the estimate.
    /// </summary>
    public static int Shrink(List<LlmMessage> messages, int maxTokens, int toolResultChars = 80)
    {
        int est = EstimateTokens(messages);
        if (est <= maxTokens) return est;
        // 1. tool results of all but the newest message, then of every message.
        if (messages.Count > 1)
        {
            var older = messages.GetRange(0, messages.Count - 1);
            TruncateToolResults(older, toolResultChars);
            for (int i = 0; i < older.Count; i++) messages[i] = older[i];
            est = EstimateTokens(messages);
            if (est <= maxTokens) return est;
        }
        TruncateToolResults(messages, toolResultChars);
        est = EstimateTokens(messages);
        // 2. halve the longest text / tool-result block until it fits (bounded loop).
        for (int guard = 0; est > maxTokens && guard < 200; guard++)
        {
            int bestMsg = -1, bestBlock = -1, bestLen = 0;
            for (int i = 0; i < messages.Count; i++)
                for (int j = 0; j < messages[i].Content.Count; j++)
                {
                    int len = messages[i].Content[j] switch { LlmText t => t.Text.Length, LlmToolResult r => r.Content.Length, _ => 0 };
                    if (len > bestLen) { bestLen = len; bestMsg = i; bestBlock = j; }
                }
            if (bestMsg < 0 || bestLen <= 24) break;
            int over = (int)Math.Ceiling((est - maxTokens) * 3.5) + 8;
            int keep = Math.Max(20, Math.Min(bestLen / 2, bestLen - over));
            var m = messages[bestMsg];
            var blocks = m.Content.ToList();
            blocks[bestBlock] = blocks[bestBlock] switch
            {
                LlmText t => new LlmText(TextUtil.Truncate(t.Text, keep)),
                LlmToolResult r => r with { Content = TextUtil.Truncate(r.Content, keep) },
                var b => b,
            };
            messages[bestMsg] = m with { Content = blocks };
            est = EstimateTokens(messages);
        }
        return est;
    }

    /// <summary>System prompt for the compaction call.</summary>
    public const string CompactionInstructions =
        "You maintain a Minecraft bot's memory journal. Merge the journal and the newer log into an updated journal: " +
        "at most 8 terse lines, oldest first. Keep open requests and promises, who asked for what, current goals and plans. " +
        "Older details stay searchable elsewhere, so drop small talk, routine tool chatter and finished one-offs. Output only the journal.";

    /// <summary>
    /// Messages for a request: journal (on the first message) + past episodes + the current episode. Main tier
    /// replays the structured episodes; the cheap tier gets their text renderings. Both are append-only.
    /// </summary>
    public IReadOnlyList<LlmMessage> BuildMessages(ModelTier tier, IReadOnlyList<LlmMessage> current)
    {
        lock (_lock)
        {
            var result = new List<LlmMessage>();
            foreach (var e in _episodes)
            {
                if (tier == ModelTier.Main) result.AddRange(e.Messages);
                else { result.Add(e.CheapUser!); result.Add(e.CheapAssistant!); }
            }
            result.AddRange(current);
            if (Journal.Length > 0 && result.Count > 0 && result[0].Role == LlmRole.User)
            {
                var first = result[0];
                result[0] = first with { Content = [new LlmText("[journal]\n" + Journal), .. first.Content] };
            }
            return result;
        }
    }

    /// <summary>Cheap-tier text rendering of an episode: user = events + actions taken, assistant = what was said.</summary>
    internal static (LlmMessage User, LlmMessage Assistant) CheapRendering(Episode e, int maxChars)
    {
        var user = new List<string>();
        var said = new List<string>();
        var uses = new Dictionary<string, LlmToolUse>();
        foreach (var m in e.Messages)
        {
            foreach (var b in m.Content)
            {
                switch (b)
                {
                    case LlmText t when m.Role == LlmRole.User:
                        user.AddRange(EventLines(t.Text).Select(l => TextUtil.Truncate(l, 160)));
                        break;
                    case LlmText t when m.Role == LlmRole.Assistant && t.Text != "(silent)" && t.Text != "(stopped)":
                        said.Add(TextUtil.OneLine(t.Text));
                        break;
                    case LlmToolUse u:
                        uses[u.Id] = u;
                        break;
                    case LlmToolResult r:
                        var name = uses.TryGetValue(r.ToolUseId, out var tu) ? $"{tu.Name}({CompactArgs(tu.Input)})" : "tool";
                        user.Add($"[did] {name} {(r.IsError ? "!" : "=")} {TextUtil.Truncate(TextUtil.OneLine(r.Content), 80)}");
                        break;
                }
            }
        }
        var u1 = TextUtil.Truncate(user.Count > 0 ? string.Join("\n", user) : "(nothing)", maxChars);
        var a1 = TextUtil.Truncate(said.Count > 0 ? string.Join(" ", said) : "(silent)", maxChars);
        return (LlmMessage.User(u1), LlmMessage.Assistant(a1));
    }

    /// <summary>Event lines of a turn text (state header, journal and auto-recalled memory dropped).</summary>
    internal static IEnumerable<string> EventLines(string text) =>
        text.Split('\n').Where(l => l.StartsWith('[') && !l.StartsWith("[journal]") && !l.StartsWith("[recent]") && !l.StartsWith("[memory]")
            && !l.StartsWith("[since last turn]"));

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
                        // Drop the state header line and recalled memory; keep the events.
                        parts.AddRange(EventLines(t.Text).Select(l => TextUtil.Truncate(l, maxPart)));
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
