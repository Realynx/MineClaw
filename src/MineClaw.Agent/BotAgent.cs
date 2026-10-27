using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Self;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent;

/// <summary>Result of handling one event batch (for logs and tests).</summary>
public sealed record EpisodeOutcome(ModelTier? Tier, int Calls, LlmUsage Usage, decimal CostUsd, IReadOnlyList<string> Said,
    bool Escalated, string? Skipped = null);

/// <summary>
/// The agent loop: wait for a coalesced event batch → triage to a tier → call the model with a compact turn
/// (state header + events) → run tool calls (instant ones in parallel, long-running ones as the bot's
/// activity) → feed results back → repeat until the model ends its turn → commit to bounded context → sleep.
/// </summary>
public sealed class BotAgent
{
    public const string EscalateToolName = "escalate";

    private static readonly LlmToolDefinition EscalateTool = new(EscalateToolName,
        "Hand this turn to the planning model (needs game actions, planning, or tools you lack).",
        ToolSchema.Create().Str("reason").Build());

    private readonly AgentOptions _options;
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly EventQueue _events;
    private readonly ChatRouter _chat;
    private readonly UsageTracker _usage;
    private readonly ContextManager _context;
    private readonly ActivityManager _activities;
    private readonly Triage _triage;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string _staticText;
    private readonly string _compactionSystem;
    private DateTimeOffset _lastEpisode;
    private DateTimeOffset _lastTurnAt;
    private DateTimeOffset _turnSince;
    private DateTimeOffset _lastActive = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPausedNotice = DateTimeOffset.MinValue;

    public BotAgent(AgentOptions options, ILlmClient llm, ToolRegistry tools, EventQueue events, ChatRouter chat,
        UsageTracker usage, ContextManager context, ActivityManager activities, string knowledge = "",
        TimeProvider? time = null, ILogger? log = null, bool knowledgeIsCore = false)
    {
        _options = options;
        _llm = llm;
        _tools = tools;
        _events = events;
        _chat = chat;
        _usage = usage;
        _context = context;
        _activities = activities;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _triage = new Triage(options.Triage);
        _staticText = Prompts.System(options.Persona, options.PersonaExtra, knowledge, knowledgeIsCore, delegation: options.Worker.Enabled,
            terse: options.Terse ?? new TerseOptions());
        _compactionSystem = ContextManager.CompactionInstructions + Prompts.CompactionStyle((options.Terse ?? new TerseOptions()).For(TersePurpose.Compaction));
        _lastEpisode = _time.GetUtcNow();
        _lastTurnAt = _turnSince = _lastEpisode;

        _events.Posted += OnEventPosted;
        _usage.ModeChanged += OnBudgetModeChanged;
        _chat.CommandBlocked += line => _log.LogWarning("blocked outgoing server command: {Line}", line);
    }

    /// <summary>Compact one-line game state ("pos 12,64,-3 hp 18/20 food 17 held stone_pickaxe"), supplied by the client layer.</summary>
    public Func<string>? StateHeader { get; set; }

    /// <summary>Current block position (spatial auto-retrieval, episode positions).</summary>
    public Func<BlockPos?>? Position { get; set; }

    /// <summary>
    /// Optional "what happened since the brain's last turn" hook (e.g. the WorldIndexer's activity digest: what other players
    /// did, salience-ranked). Gets the time of the last turn; its output goes right after the state header as
    /// <c>[since last turn] ...</c> — uncached tail only, capped, deduped against [memory]. Null/empty = nothing.
    /// </summary>
    public Func<DateTimeOffset, string>? TurnDigest { get; set; }

    /// <summary>Builds the per-turn [memory] block (null = no auto-retrieval).</summary>
    public AutoRetriever? Retriever { get; set; }

    /// <summary>Chat log sink (null = no chat logging).</summary>
    public RagIndex? Rag { get; set; }

    /// <summary>Evolving personality rendered into the identity block (null = off).</summary>
    public SelfProfile? Self { get; set; }

    /// <summary>Periodic cheap-tier personality reflection (null = off).</summary>
    public ReflectionPass? Reflection { get; set; }

    /// <summary>
    /// Personality growth v2. When set, the grown personality is NOT in the cached system blocks: it rides as a compact
    /// "[self]" block in the newest user message, and only when it changed (or left the history window).
    /// </summary>
    public SelfGrowth? Growth { get; set; }

    public ContextManager Context => _context;

    /// <summary>
    /// Current system prompt, in cache order: (1) the static block — instructions + core knowledge; tools render
    /// before it and share its breakpoint; (2) the identity + self-profile block with its own 5m breakpoint, so a rename
    /// or personality update only rewrites that small block plus history. Per-turn data never goes here — state header,
    /// [memory] and events ride in the newest user message.
    /// </summary>
    public IReadOnlyList<LlmSystemBlock> SystemPrompt =>
        [new LlmSystemBlock(_staticText, Cache: true, Ttl: StaticTtl()), new LlmSystemBlock(DynamicSystemText(), Cache: true, Ttl: "5m")];

    private string DynamicSystemText()
    {
        var identity = Prompts.Identity(_chat.Username, _chat.DisplayName, _chat.Aliases);
        string self = "";
        if (Growth == null)
            try { self = Self?.Render() ?? ""; } catch (Exception ex) { _log.LogWarning(ex, "self profile render failed"); }
        return self.Length > 0 ? identity + "\n\n" + self : identity;
    }

    private string StaticTtl() => _options.Llm.StaticPrefixTtl?.ToLowerInvariant() switch
    {
        "1h" => "1h",
        "auto" => _time.GetUtcNow() - _lastActive <= TimeSpan.FromMinutes(Math.Max(1, _options.Llm.ActiveSessionMinutes)) ? "1h" : "5m",
        _ => "5m",
    };

    /// <summary>Raised after each handled batch.</summary>
    public event Action<EpisodeOutcome>? EpisodeCompleted;

    public async Task RunAsync(CancellationToken ct)
    {
        _activities.Shutdown = ct;
        using var idleTimer = StartIdleTimer(ct);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            EventBatch batch;
            try { batch = await _events.WaitBatchAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                await HandleBatchAsync(batch, ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                _log.LogError(ex, "agent turn failed ({Failures} in a row)", failures);
                var backoff = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(failures, 5))));
                try { await Task.Delay(backoff, _time, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Handles one batch end-to-end (public for tests and for hosts that drive the loop themselves).</summary>
    public async Task<EpisodeOutcome> HandleBatchAsync(EventBatch batch, CancellationToken ct)
    {
        _lastEpisode = _time.GetUtcNow();
        _turnSince = _lastTurnAt;
        LogChat(batch);
        if (batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote)) _lastActive = _lastEpisode;
        var mode = _usage.CheckMode();
        if (mode == BudgetMode.Paused)
        {
            await NotifyPausedAsync(batch, ct).ConfigureAwait(false);
            return Done(new EpisodeOutcome(null, 0, LlmUsage.Zero, 0, [], false, "paused: " + _usage.Reason));
        }

        _lastTurnAt = _lastEpisode; // only turns that actually reach a model move the digest window
        var decision = _triage.Choose(batch, mode);
        _log.LogDebug("triage → {Tier} ({Why})", decision.Tier, decision.Why);
        var outcome = await RunEpisodeAsync(batch, decision.Tier, decision.CanEscalate, mode, null, ct).ConfigureAwait(false);

        if (outcome.Escalation is { } esc)
        {
            var prelude = $"(escalated from quick mode: {esc.Reason}" + (esc.Said.Count > 0 ? $"; you already said: \"{string.Join(" ", esc.Said)}\")" : ")");
            var main = await RunEpisodeAsync(batch, ModelTier.Main, false, mode, prelude, ct).ConfigureAwait(false);
            outcome = main with
            {
                Calls = main.Calls + outcome.Calls,
                Usage = main.Usage.Add(outcome.Usage),
                Cost = main.Cost + outcome.Cost,
                Said = [.. outcome.Said, .. main.Said],
                Escalation = esc,
            };
        }

        if (_context.NeedsCompaction)
        {
            try
            {
                int folded = await _context.CompactAsync(_usage.Mode == BudgetMode.Paused ? null : SummarizeAsync, ct).ConfigureAwait(false);
                _log.LogDebug("compacted {Count} episodes into journal", folded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "compaction failed"); }
        }

        if (Reflection != null)
        {
            Reflection.NoteEpisode();
            if (Reflection.IsDue && _usage.CheckMode() == BudgetMode.Normal)
            {
                try
                {
                    int changed = await Reflection.RunAsync(ct).ConfigureAwait(false);
                    _log.LogInformation("self reflection: {Changed} change(s)", changed);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "self reflection failed"); }
            }
        }

        return Done(new EpisodeOutcome(outcome.Tier, outcome.Calls, outcome.Usage, outcome.Cost, outcome.Said, outcome.Escalation != null));
    }

    private EpisodeOutcome Done(EpisodeOutcome o)
    {
        _log.LogInformation("turn: tier {Tier} calls {Calls} {Usage} ${Cost:0.0000} | {Budget}", o.Tier, o.Calls, o.Usage, o.CostUsd, _usage.Summary());
        EpisodeCompleted?.Invoke(o);
        return o;
    }

    // ---------------- episode ----------------

    private sealed record Escalation(string Reason, IReadOnlyList<string> Said);

    private sealed record RunResult(ModelTier Tier, int Calls, LlmUsage Usage, decimal Cost, IReadOnlyList<string> Said, Escalation? Escalation);

    private async Task<RunResult> RunEpisodeAsync(EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string? prelude, CancellationToken ct)
    {
        var episode = new Episode(tier, _time.GetUtcNow()) { IsTest = batch.IsTest };
        episode.Events.AddRange(batch.Events);
        episode.Messages.Add(LlmMessage.User(await BuildTurnTextAsync(batch, tier, canEscalate, mode, prelude, ct).ConfigureAwait(false)));

        string? whisperTo = batch.WhisperOnlyFrom;
        var audience = batch.Waking.Where(e => e.Player != null).Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var speaker = batch.Waking.FirstOrDefault(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && e.Player != null)?.Player;
        // Ambient for tools (and background tasks started from this turn): who asked, and whether it's injected test chat.
        TurnContext.Current = new TurnInfo(speaker, audience.ToArray(), batch.IsTest);
        var said = new List<string>();
        var usage = LlmUsage.Zero;
        decimal cost = 0;
        int calls = 0;
        var tools = ToolsFor(tier, canEscalate);
        // Snapshot per episode: a rename mid-turn applies from the next turn, keeping this turn's prefix byte-stable.
        var system = SystemPrompt;
        // Budget fallback 1 (Economy): the brain's turn runs on the worker model; same tools and history.
        string? model = tier == ModelTier.Main && mode == BudgetMode.Economy ? _options.Llm.WorkerModel : null;
        int maxOut = _options.Llm.MaxTokensFor(tier);

        for (int round = 0; round < _options.MaxToolRounds; round++)
        {
            var nowMode = _usage.CheckMode();
            if (nowMode == BudgetMode.Paused || (tier == ModelTier.Main && nowMode == BudgetMode.CheapOnly && round > 0)) break;

            if (!FitHardCap(tier, system, tools, episode.Messages, maxOut))
            {
                _log.LogError("context can't fit under the {Cap} token hard cap; ending turn without a call", _context.Options.MaxTokens);
                break;
            }

            var request = new LlmRequest
            {
                Tier = tier,
                Model = model,
                System = system,
                Tools = tools,
                Messages = _context.BuildMessages(tier, episode.Messages),
                Purpose = tier == ModelTier.Cheap ? "chat" : "task",
            };
            LastRequestTokens = EstimateRequestTokens(request, maxOut);
            var response = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
            calls++;
            usage = usage.Add(response.Usage);
            cost += _usage.Record(response.Model, response.Usage, tier);
            _log.LogInformation("call {Label} ({Model}) round {Round}: {Usage} cache {Hit:P0}",
                UsageTracker.TierLabel(_options.Llm.TierOfModel(response.Model) ?? tier), response.Model, round, response.Usage, UsageTracker.CacheHit(response.Usage));

            var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
            if (content.Count == 0) content.Add(new LlmText("(silent)"));
            episode.Messages.Add(new LlmMessage(LlmRole.Assistant, content));

            var text = response.Text.Trim();
            if (!IsSilent(text))
            {
                var lines = await _chat.SayAsync(text, whisperTo, audience, ct).ConfigureAwait(false);
                said.AddRange(lines);
                LogSaid(lines, whisperTo, episode.IsTest);
            }

            if (response.StopReason == StopReasons.Refusal)
            {
                _log.LogWarning("model refused");
                break;
            }

            var uses = response.ToolUses.ToList();
            if (uses.Count == 0) break;

            var esc = uses.FirstOrDefault(u => u.Name == EscalateToolName);
            if (esc != null && canEscalate)
            {
                // Discard the cheap exchange (its escalate call never enters history) and let the main tier redo the turn.
                return new RunResult(tier, calls, usage, cost, said, new Escalation(esc.Input.Str("reason") ?? "needs planning", said));
            }

            var results = await ExecuteToolsAsync(uses, tier, ct).ConfigureAwait(false);
            var blocks = new List<LlmBlock>(results);

            // Let urgent/new events reach the model mid-turn (appended after the tool results — append-only).
            var fresh = _events.TryTakeUrgent(EventPriority.Normal);
            if (fresh != null)
            {
                LogChat(fresh);
                episode.Events.AddRange(fresh.Events);
                blocks.Add(new LlmText("[new events]\n" + fresh.Format()));
                foreach (var p in fresh.Waking.Where(e => e.Player != null).Select(e => e.Player!))
                    if (!audience.Contains(p, StringComparer.OrdinalIgnoreCase)) audience.Add(p);
                if (fresh.WhisperOnlyFrom == null && fresh.Waking.Any()) whisperTo = null;
            }
            episode.Messages.Add(new LlmMessage(LlmRole.User, blocks));
        }

        _context.Commit(episode);
        return new RunResult(tier, calls, usage, cost, said, null);
    }

    private async Task<string> BuildTurnTextAsync(EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string? prelude, CancellationToken ct)
    {
        string memory = "";
        if (Retriever != null)
        {
            try
            {
                BlockPos? pos = null;
                try { pos = Position?.Invoke(); } catch (Exception) { }
                memory = await Retriever.BuildAsync(batch, tier, pos, NameWords(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "auto-retrieval failed"); }
        }
        var sb = new StringBuilder();
        string header = "";
        try { header = StateHeader?.Invoke() ?? ""; } catch (Exception ex) { _log.LogWarning(ex, "state header failed"); }
        var activity = _activities.Describe();
        var status = string.Join(" | ", new[] { header.Trim(), activity != null ? "task: " + activity : (header.Contains("task:") ? "" : "task: idle") }
            .Where(s => s.Length > 0));
        sb.Append(status.TrimStart('[')).Append('\n');
        var digest = BuildDigest(memory);
        if (digest.Length > 0) sb.Append(digest).Append('\n');
        if (mode == BudgetMode.CheapOnly) sb.Append(Prompts.CheapOnlyHint).Append('\n');
        else if (tier == ModelTier.Cheap && canEscalate) sb.Append(Prompts.CheapTierHint).Append('\n');
        if (prelude != null) sb.Append(prelude).Append('\n');
        var self = SelfBlock(batch, tier);
        if (self.Length > 0) sb.Append(self).Append('\n');
        if (memory.Length > 0) sb.Append(memory).Append('\n');
        var events = batch.Format();
        sb.Append(events);
        var text = sb.ToString().TrimEnd();
        try { TurnBuilt?.Invoke(new TurnInput(_time.GetUtcNow(), tier, status, digest, memory, events, text, self)); }
        catch (Exception ex) { _log.LogDebug(ex, "TurnBuilt handler failed"); }
        return text;
    }

    /// <summary>The "[self]" block for this turn ("" when unchanged and still visible in history, or nothing grown yet).</summary>
    private string SelfBlock(EventBatch batch, ModelTier tier)
    {
        if (Growth == null) return "";
        try
        {
            var block = Growth.RenderBlock(batch.Events.Where(e => e.Player != null && !e.IsTest).Select(e => e.Player!));
            return Growth.Gate.Next(block, tier, _context);
        }
        catch (Exception ex) { _log.LogWarning(ex, "self block render failed"); return ""; }
    }

    /// <summary>Raised with the parts of each turn's first user message (telemetry/display only; never changes the prompt).</summary>
    public event Action<TurnInput>? TurnBuilt;

    /// <summary>
    /// "[since last turn] ..." from <see cref="TurnDigest"/>: one line, capped at <see cref="AgentOptions.TurnDigestMaxChars"/>,
    /// with parts already present in the [memory] block dropped. Lives in the uncached newest user message only.
    /// </summary>
    private string BuildDigest(string memory)
    {
        if (TurnDigest == null || _options.TurnDigestMaxChars <= 0) return "";
        string raw;
        try { raw = TurnDigest(_turnSince) ?? ""; }
        catch (Exception ex) { _log.LogWarning(ex, "turn digest failed"); return ""; }
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var mem = memory.Length > 0 ? Norm(memory) : "";
        var parts = raw.Replace("\r", "").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.StartsWith(DigestTag, StringComparison.OrdinalIgnoreCase) ? p[DigestTag.Length..].Trim() : p)
            .Where(p => p.Length > 0 && (mem.Length == 0 || !mem.Contains(Norm(p), StringComparison.Ordinal)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (parts.Count == 0) return "";
        var line = DigestTag + " " + string.Join("; ", parts);
        return TextUtil.Truncate(line, _options.TurnDigestMaxChars);

        static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public const string DigestTag = "[since last turn]";

    private IReadOnlyList<LlmToolDefinition> ToolsFor(ModelTier tier, bool canEscalate)
    {
        if (tier == ModelTier.Main) return _tools.Definitions(ModelTier.Main);
        var defs = _tools.Definitions(ModelTier.Cheap).ToList();
        if (canEscalate) defs.Add(EscalateTool);
        return defs;
    }

    /// <summary>Estimated tokens of the most recent brain/cheap request (system + tools + messages + max output).</summary>
    public int LastRequestTokens { get; private set; }

    internal static int EstimateRequestTokens(LlmRequest r, int maxOut) =>
        r.System.Sum(s => TextUtil.EstimateTokens(s.Text)) + Worker.WorkerAgent.ToolTokens(r.Tools) + ContextManager.EstimateTokens(r.Messages) + maxOut;

    /// <summary>
    /// Enforces <see cref="ContextOptions.MaxTokens"/> (× <see cref="ContextOptions.HardCapSafety"/>) before a call, whatever
    /// grew: the current turn is cut first (tool results, then long texts) down to half the room if it's the hog, then history
    /// is folded mechanically, then the current turn is cut to whatever is left. False = it can't fit; don't call.
    /// </summary>
    private bool FitHardCap(ModelTier tier, IReadOnlyList<LlmSystemBlock> system, IReadOnlyList<LlmToolDefinition> tools, List<LlmMessage> current, int maxOut)
    {
        var o = _context.Options;
        int cap = (int)(o.MaxTokens * Math.Clamp(o.HardCapSafety, 0.1, 1.0));
        int fixedTokens = system.Sum(s => TextUtil.EstimateTokens(s.Text)) + Worker.WorkerAgent.ToolTokens(tools) + maxOut + 16;
        int History() => tier == ModelTier.Main ? _context.EstimateTokens() : _context.EstimateCheapTokens();
        int cur = ContextManager.EstimateTokens(current);
        if (fixedTokens + History() + cur <= cap) return true;

        int room = cap - fixedTokens;
        if (room <= 0) return false;
        _log.LogWarning("hard cap: request ≈{Tokens} tokens > {Cap}; trimming", fixedTokens + History() + cur, cap);
        if (cur > room - History() && cur > room / 2) cur = ContextManager.Shrink(current, Math.Max(room / 2, room - History()), o.OldToolResultChars);
        if (History() + cur > room) _context.EnforceHardCap(room - cur, tier);
        if (History() + cur > room) cur = ContextManager.Shrink(current, room - History(), o.OldToolResultChars);
        return fixedTokens + History() + cur <= cap;
    }

    private async Task<IReadOnlyList<LlmToolResult>> ExecuteToolsAsync(List<LlmToolUse> uses, ModelTier tier, CancellationToken ct)
    {
        var results = new ToolResult?[uses.Count];
        var instant = new List<Task>();
        var chain = new List<(int Index, IAgentTool Tool, LlmToolUse Use)>();

        for (int i = 0; i < uses.Count; i++)
        {
            var u = uses[i];
            if (!_tools.TryGet(u.Name, out var tool))
            {
                results[i] = ToolResult.Error(u.Name == EscalateToolName ? "can't escalate right now" : $"unknown tool '{u.Name}'");
                continue;
            }
            if (!_tools.Allows(tool, tier))
            {
                results[i] = ToolResult.Error(tier == ModelTier.Cheap ? "not available in quick mode"
                    : $"'{u.Name}' is a worker tool: use delegate");
                continue;
            }
            if (tool.IsLongRunning) chain.Add((i, tool, u));
            else
            {
                int idx = i;
                instant.Add(Task.Run(async () => results[idx] = await _tools.ExecuteAsync(tool, u.Input, ct).ConfigureAwait(false), ct));
            }
        }

        Task chainTask = Task.CompletedTask;
        if (chain.Count > 0)
        {
            chainTask = Task.Run(async () =>
            {
                var r = await _activities.RunAsync(chain.Select(c => (c.Tool, c.Use.Input)).ToList(),
                    TimeSpan.FromSeconds(_options.InlineToolWaitSeconds), ct).ConfigureAwait(false);
                for (int k = 0; k < chain.Count; k++) results[chain[k].Index] = r[k];
            }, ct);
        }

        await Task.WhenAll(instant.Append(chainTask)).ConfigureAwait(false);

        return uses.Select((u, i) =>
        {
            var r = results[i] ?? ToolResult.Error("no result");
            _log.LogDebug("tool {Name} → {Result}", u.Name, r);
            return new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok);
        }).ToList();
    }

    private static bool IsSilent(string text) =>
        text.Length == 0 || text is "." or "-" or "..." || text.Equals("(silent)", StringComparison.OrdinalIgnoreCase);

    // ---------------- support ----------------

    private IEnumerable<string> NameWords() => [_chat.Username, _chat.DisplayName, .. _chat.Aliases];

    private void LogChat(EventBatch batch)
    {
        if (Rag == null) return;
        try
        {
            foreach (var e in batch.Events)
            {
                string? kind = e.Kind switch
                {
                    EventKind.Chat => "chat", EventKind.Whisper => "whisper", EventKind.Emote => "emote",
                    EventKind.PlayerJoined => "join", EventKind.PlayerLeft => "leave", _ => null,
                };
                // Injected test chat (console /as) is kept, tagged: it never counts as the real player speaking.
                if (kind != null) Rag.LogChat(e.At, e.IsTest ? "test_" + kind : kind, e.Player, e.Text);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "chat log write failed"); }
    }

    private void LogSaid(IReadOnlyList<string> lines, string? whisperTo, bool test = false)
    {
        if (Rag == null) return;
        try
        {
            foreach (var l in lines) Rag.LogChat(_time.GetUtcNow(), (test ? "test_" : "") + (whisperTo != null ? "bot_whisper" : "bot"), _chat.Username, l);
        }
        catch (Exception ex) { _log.LogWarning(ex, "chat log write failed"); }
    }

    private async Task<string> SummarizeAsync(string prompt, CancellationToken ct)
    {
        var response = await _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Cheap,
            System = [new LlmSystemBlock(_compactionSystem)],
            Messages = [LlmMessage.User(prompt)],
            MaxTokens = 500,
            CacheHistory = false,
            Purpose = "compact",
        }, ct).ConfigureAwait(false);
        _usage.Record(response.Model, response.Usage, ModelTier.Cheap);
        return response.Text;
    }

    private void OnEventPosted(GameEvent e)
    {
        if (e.Wakes && e.Priority >= _options.InterruptPriority)
        {
            var cancelled = _activities.Cancel("interrupted by " + e.Kind);
            if (cancelled != null) _log.LogInformation("interrupted {Activity} due to {Event}", cancelled, e.Format());
        }
    }

    private void OnBudgetModeChanged(BudgetMode mode, string reason)
    {
        string? notice = mode switch
        {
            BudgetMode.Paused => $"heads up: pausing my brain ({reason}). i'll be back when it resets",
            BudgetMode.CheapOnly => $"heads up: {reason}, so i'm in chat-only mode for now",
            BudgetMode.Normal => "budget's fine again, back to full brain",
            BudgetMode.Economy => null, // quiet: brain on the worker model, players won't notice much
            _ => null,
        };
        _log.LogWarning("budget mode → {Mode}: {Reason}", mode, reason);
        if (notice != null) _ = SafeSayAsync(notice);
    }

    private async Task NotifyPausedAsync(EventBatch batch, CancellationToken ct)
    {
        if (!batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper)) return;
        var now = _time.GetUtcNow();
        if (now - _lastPausedNotice < TimeSpan.FromMinutes(_options.PausedNoticeMinutes)) return;
        _lastPausedNotice = now;
        await _chat.SayAsync($"zzz... my brain's paused ({_usage.Reason}). talk soon!", batch.WhisperOnlyFrom, ct: ct).ConfigureAwait(false);
    }

    private async Task SafeSayAsync(string text)
    {
        try { await _chat.SayAsync(text).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "chat send failed"); }
    }

    private ITimer? StartIdleTimer(CancellationToken ct)
    {
        if (_options.IdleCheckInMinutes <= 0) return null;
        var period = TimeSpan.FromMinutes(1);
        return _time.CreateTimer(_ =>
        {
            if (ct.IsCancellationRequested || _activities.IsBusy) return;
            if (_time.GetUtcNow() - _lastEpisode >= TimeSpan.FromMinutes(_options.IdleCheckInMinutes))
            {
                _lastEpisode = _time.GetUtcNow();
                _events.Post(GameEvent.Idle());
            }
        }, null, period, period);
    }
}
