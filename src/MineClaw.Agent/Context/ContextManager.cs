using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Context;

/// <summary>A model reply that cannot serve as a completed memory checkpoint.</summary>
internal sealed class IncompleteCompactionSummaryException(string stopReason)
    : Exception($"Incomplete compaction summary (stop reason: {stopReason}).");

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
    /// <summary>
    /// The structured events this turn handled (the batch plus mid-turn arrivals). Signal hook for personality growth:
    /// extracting from these costs zero tokens and never re-parses rendered text. Not part of any prompt.
    /// </summary>
    public List<GameEvent> Events { get; } = new();
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
        // Byte-stable history: the episode is committed exactly as it was sent. Thinking blocks stay (the API cached the
        // turn's rounds WITH them; stripping them here rewrote the whole turn's cache on the next request: ≈$0.75 one
        // night). They leave only with the episode (compaction) or a hard-cap squeeze. Seal only fills what was never
        // sent (a cut turn's unanswered tool_use), so it can't change a cached message.
        Seal(episode.Messages);
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

    /// <summary>
    /// Real tokens per estimated token for the main rendering (≥ 1; the brain loop calibrates it from API usage). The
    /// 3.5-chars/token estimate runs ~1.5× low on tool JSON (live 10-27: 47K real at 30K estimated), so without it the 30K
    /// compaction threshold meant ~45K real and never fired. Compaction thresholds compare estimate × scale.
    /// </summary>
    public double TokenScale { get; set; } = 1.0;

    private int Scaled(int estimate) => (int)Math.Min(int.MaxValue, estimate * Math.Max(1.0, TokenScale));

    public bool NeedsCompaction
    {
        get
        {
            int main = Scaled(EstimateTokens()), cheap = EstimateCheapTokens();
            return main > _options.CompactAtTokens || cheap > _options.CheapCompactAtTokens || main > _options.MaxTokens;
        }
    }

    /// <summary>
    /// Folds the oldest episodes into the journal until both renderings fit their targets. <paramref name="summarize"/>
    /// gets a prompt and returns the new journal (cheap tier). Rejected model checkpoints retain history when it fits
    /// the hard ceiling; other failures or pressure beyond that ceiling use a labelled, lossy mechanical digest.
    /// </summary>
    public async Task<int> CompactAsync(Func<string, CancellationToken, Task<string>>? summarize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (old, journal) = OldestOverTarget();
        string next = journal;
        if (old.Count > 0)
        {
            // Null: a rejected checkpoint while the intact history still fits.
            if (await FoldIntoJournalAsync(summarize, old, journal, ct).ConfigureAwait(false) is not { } folded) return 0;
            next = folded;
        }

        ct.ThrowIfCancellationRequested();
        Recompact(old, next);
        if (old.Count > 0) Compacted?.Invoke(old, next);
        return old.Count;
    }

    /// <summary>The oldest episodes to fold until both renderings fit their targets (never the newest), and the journal now.</summary>
    private (List<Episode> Old, string Journal) OldestOverTarget()
    {
        lock (_lock)
        {
            int jt = TextUtil.EstimateTokens(Journal);
            int main = jt + _episodes.Sum(e => e.MainTokens);
            int cheap = jt + _episodes.Sum(e => e.CheapTokens);
            int take = 0;
            while (take < _episodes.Count - 1 && (Scaled(main) > _options.CompactToTokens || cheap > _options.CheapCompactToTokens))
            {
                main -= _episodes[take].MainTokens;
                cheap -= _episodes[take].CheapTokens;
                take++;
            }
            return (_episodes.Take(take).ToList(), Journal);
        }
    }

    /// <summary>
    /// The journal with <paramref name="old"/> folded in by the summarizer, or mechanically when it fails; null when it gave an
    /// unfinished checkpoint and the history still fits the hard ceiling.
    /// </summary>
    private async Task<string?> FoldIntoJournalAsync(Func<string, CancellationToken, Task<string>>? summarize, List<Episode> old, string journal,
        CancellationToken ct)
    {
        var digest = string.Join("\n", old.Select(e => Flatten(e, 160)));
        string next;
        try
        {
            if (summarize == null) throw new InvalidOperationException("no summarizer");
            var prompt = $"Journal so far:\n{(journal.Length > 0 ? journal : "(empty)")}\n\nNewer log to fold in:\n{digest}";
            next = (await summarize(prompt, ct).ConfigureAwait(false)).Trim();
            if (next.Length == 0) throw new InvalidOperationException("empty summary");
        }
        catch (IncompleteCompactionSummaryException) when (!ct.IsCancellationRequested &&
            Scaled(EstimateTokens()) <= _options.MaxTokens && EstimateCheapTokens() <= _options.MaxTokens)
        {
            // A soft compaction threshold is not a reason to replace intact history with an unfinished checkpoint.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
        {
            next = MechanicalJournal(journal, digest);
        }
        return TextUtil.Truncate(next, _options.JournalMaxChars);
    }

    private string MechanicalJournal(string journal, string digest)
    {
        const string label = "[mechanical fallback; lossy]";
        var next = (journal + "\n" + digest).Trim();
        // Reserve room for the label even when retaining only the newest part of the digest.
        int chars = Math.Max(0, _options.JournalMaxChars - label.Length - 1);
        if (next.Length > chars) next = chars > 3 ? "..." + next[^(chars - 3)..] : "";
        return label + "\n" + next;
    }

    /// <summary>Drops the folded episodes, sets the journal, and (the cache rebuilds anyway) trims what stays.</summary>
    private void Recompact(List<Episode> old, string next)
    {
        lock (_lock)
        {
            foreach (var e in old) _episodes.Remove(e);
            Journal = next;
            // The cache is being rebuilt anyway: this is the only time older tool results get truncated, and the only time
            // images (see) leave history: each becomes its one-line stub, so a picture is never re-sent past a compaction.
            foreach (var e in _episodes) StripImages(e);
            for (int i = 0; i < _episodes.Count - _options.KeepFullEpisodes; i++) Retruncate(_episodes[i], _options.OldToolResultChars);
            int total = TextUtil.EstimateTokens(Journal) + _episodes.Sum(e => e.MainTokens);
            if (total > _options.MaxTokens)
                foreach (var e in _episodes) Retruncate(e, Math.Max(40, _options.OldToolResultChars / 2));
        }
    }

    private static void StripImages(Episode e)
    {
        if (!e.Messages.Any(m => m.Content.Any(b => b is LlmToolResult { Image: not null }))) return;
        StripImages(e.Messages);
        e.MainTokens = EstimateTokens(e.Messages);
    }

    /// <summary>Replaces every tool-result image with its text stub (see <see cref="LlmToolResult.WithoutImage"/>).</summary>
    public static void StripImages(List<LlmMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            if (!m.Content.Any(b => b is LlmToolResult { Image: not null })) continue;
            messages[i] = m with { Content = m.Content.Select(b => b is LlmToolResult r ? r.WithoutImage() : b).ToList() };
        }
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
            est = TruncateOlderToolResults(messages, toolResultChars);
            if (est <= maxTokens) return est;
        }
        TruncateToolResults(messages, toolResultChars);
        est = EstimateTokens(messages);
        // 2. halve the longest text / tool-result block until it fits (bounded loop).
        for (int guard = 0; est > maxTokens && guard < 200; guard++)
        {
            if (!HalveLongestBlock(messages, est, maxTokens)) break;
            est = EstimateTokens(messages);
        }
        return est;
    }

    private static int TruncateOlderToolResults(List<LlmMessage> messages, int toolResultChars)
    {
        var older = messages.GetRange(0, messages.Count - 1);
        TruncateToolResults(older, toolResultChars);
        for (int i = 0; i < older.Count; i++) messages[i] = older[i];
        return EstimateTokens(messages);
    }

    /// <summary>Cuts the longest text / tool-result block toward the target; false when nothing longer than 24 chars is left.</summary>
    private static bool HalveLongestBlock(List<LlmMessage> messages, int est, int maxTokens)
    {
        var (bestMsg, bestBlock, bestLen) = LongestBlock(messages);
        if (bestMsg < 0 || bestLen <= 24) return false;
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
        return true;
    }

    private static (int Message, int Block, int Length) LongestBlock(List<LlmMessage> messages)
    {
        int bestMsg = -1, bestBlock = -1, bestLen = 0;
        for (int i = 0; i < messages.Count; i++)
            for (int j = 0; j < messages[i].Content.Count; j++)
            {
                int len = messages[i].Content[j] switch { LlmText t => t.Text.Length, LlmToolResult r => r.Content.Length, _ => 0 };
                if (len > bestLen) { bestLen = len; bestMsg = i; bestBlock = j; }
            }
        return (bestMsg, bestBlock, bestLen);
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
            foreach (var b in m.Content) AddCheapBlock(m.Role, b, user, said, uses);
        var u1 = TextUtil.Truncate(user.Count > 0 ? string.Join("\n", user) : "(nothing)", maxChars);
        var a1 = TextUtil.Truncate(said.Count > 0 ? string.Join(" ", said) : "(silent)", maxChars);
        return (LlmMessage.User(u1), LlmMessage.Assistant(a1));
    }

    /// <summary>Events go to the user side, what was said to the assistant side, and each tool call as one "[did]" line.</summary>
    private static void AddCheapBlock(LlmRole role, LlmBlock b, List<string> user, List<string> said, Dictionary<string, LlmToolUse> uses)
    {
        switch (b)
        {
            case LlmText t when role == LlmRole.User:
                user.AddRange(EventLines(t.Text).Select(l => TextUtil.Truncate(l, 160)));
                break;
            case LlmText t when role == LlmRole.Assistant && t.Text != "(silent)" && t.Text != "(stopped)":
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

    /// <summary>Event lines of a turn text (state header, journal and auto-recalled memory dropped).</summary>
    internal static IEnumerable<string> EventLines(string text) =>
        text.Split('\n').Where(l => l.StartsWith('[') && !l.StartsWith("[journal]") && !l.StartsWith("[recent]") && !l.StartsWith("[memory]")
            && !l.StartsWith("[since last turn]") && !l.StartsWith(SelfBlockTag));

    /// <summary>First line of the per-turn personality block (dropped from transcripts/summaries like [memory]).</summary>
    public const string SelfBlockTag = "[self]";

    // ---------- helpers ----------

    /// <summary>Makes a message list well-formed: every tool_use answered, ends with an assistant message, no empty text.</summary>
    public static void Seal(List<LlmMessage> messages)
    {
        for (int i = 0; i < messages.Count; i++)
        {
            var m = WithoutEmptyText(messages, i);
            if (m.Role == LlmRole.Assistant) AnswerToolUses(messages, i, m);
        }
        if (messages.Count > 0 && messages[^1].Role == LlmRole.User)
            messages.Add(LlmMessage.Assistant("(stopped)"));
    }

    /// <summary>Message <paramref name="i"/> without blank text blocks (a placeholder when nothing is left); returns it.</summary>
    private static LlmMessage WithoutEmptyText(List<LlmMessage> messages, int i)
    {
        var m = messages[i];
        var cleaned = m.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
        if (cleaned.Count == 0) cleaned.Add(new LlmText(m.Role == LlmRole.Assistant ? "(silent)" : "(nothing)"));
        if (cleaned.Count != m.Content.Count) messages[i] = m = m with { Content = cleaned };
        return m;
    }

    /// <summary>Every tool_use of assistant message <paramref name="i"/> gets a result ("cancelled" when missing), results first.</summary>
    private static void AnswerToolUses(List<LlmMessage> messages, int i, LlmMessage m)
    {
        var ids = m.ToolUses.Select(u => u.Id).ToList();
        if (ids.Count == 0) return;
        var next = i + 1 < messages.Count ? messages[i + 1] : null;
        if (next == null || next.Role != LlmRole.User)
        {
            messages.Insert(i + 1, new LlmMessage(LlmRole.User, ids.Select(id => (LlmBlock)new LlmToolResult(id, "cancelled", true)).ToList()));
            return;
        }
        var answered = next.ToolResults.Select(r => r.ToolUseId).ToHashSet();
        var missing = ids.Where(id => !answered.Contains(id)).ToList();
        if (missing.Count == 0) return;
        // tool_result blocks must come first in the user message.
        var blocks = missing.Select(id => (LlmBlock)new LlmToolResult(id, "cancelled", true)).ToList();
        messages[i + 1] = next with { Content = [.. next.Content.OfType<LlmToolResult>(), .. blocks, .. next.Content.Where(b => b is not LlmToolResult)] };
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
        StripImages(messages); // any squeeze drops pictures first (they're the priciest blocks)
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
            LlmToolResult r => 8 + TextUtil.EstimateTokens(r.Content) + (r.Image?.Tokens ?? 0),
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
