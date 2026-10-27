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
public sealed partial class BotAgent
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
    private string _staticText;
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

        Guard = new ToolCallGuard(activities, _time, TimeSpan.FromMinutes(Math.Max(0, options.FailureMemoryMinutes)));
        _events.Posted += OnEventPosted;
        _context.Compacted += (_, _) => Interlocked.Exchange(ref _compactedSinceCall, 1);
        _usage.ModeChanged += OnBudgetModeChanged;
        _chat.CommandBlocked += line => _log.LogWarning("blocked outgoing server command: {Line}", line);
        RegisterSayTool();
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

    /// <summary>
    /// Optional live-perception line for this turn, e.g. the latest player gestures ("[pointing] PoofimaFox punched
    /// cobblestone at the gen (west cell) 3s ago"), so "this one" + a punch resolves to a block. Main and clones.
    /// </summary>
    public Func<string?>? TurnPerception { get; set; }

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

    /// <summary>In-flight dedupe + short-term failure memory for long-running tool calls.</summary>
    public ToolCallGuard Guard { get; }

    /// <summary>
    /// Optional deterministic hint for the newest user message (uncached tail), e.g. the hive's
    /// "[hive: 1 clone online: maid_chan_67 (idle)]". Null/empty = nothing.
    /// </summary>
    public Func<EventBatch, string?>? TurnHint { get; set; }

    /// <summary>
    /// Optional detail for the "[busy]" line when chat wakes the brain while a task runs (e.g. "worker: mine! > harvest (running)").
    /// </summary>
    public Func<string?>? BusyDetail { get; set; }

    /// <summary>Chat-flow counters (debounce merges, restarts, injections, busy wakes) for tuning <see cref="AgentOptions.ChatDebounce"/>.</summary>
    public ChatFlowStats ChatFlow => _events.Stats;

    /// <summary>This bot's standing order (resume when idle, "[standing: ...]" header). Null = none.</summary>
    public Human.StandingOrders? Orders { get; set; }

    /// <summary>Any human player online (hive bots and this bot excluded). Null = the chat router's online list.</summary>
    public Func<bool>? HumansOnline { get; set; }

    /// <summary>
    /// Minutes of quiet before an idle check-in right now: <see cref="AgentOptions.IdleCheckInMinutes"/> while players are on,
    /// at least <see cref="AgentOptions.IdleCheckInMinutesNoPlayers"/> (default hourly) when nobody is: unattended nights
    /// don't burn tokens on check-ins. 0 = no check-ins now.
    /// </summary>
    public int IdleCheckInMinutesNow()
    {
        if (_options.IdleCheckInMinutes <= 0) return 0;
        bool humans;
        try { humans = HumansOnline?.Invoke() ?? _chat.OnlinePlayers.Count > 0; } catch (Exception) { humans = true; }
        if (humans) return _options.IdleCheckInMinutes;
        return _options.IdleCheckInMinutesNoPlayers <= 0 ? 0 : Math.Max(_options.IdleCheckInMinutes, _options.IdleCheckInMinutesNoPlayers);
    }

    /// <summary>
    /// Final say over what this bot types into public chat: (everything this turn has seen so far, the text, whether it
    /// already spoke this turn) -> the text to send, or null to stay silent. A hive clone uses it to speak only when a player
    /// addressed it (or a hive task needs a short ack) and never to narrate hive status. Null = always speak.
    /// </summary>
    public Func<EventBatch, string, bool, string?>? SpeechGate { get; set; }

    /// <summary>Raised with text the <see cref="SpeechGate"/> kept out of chat (a clone forwards it to main as progress).</summary>
    public event Action<string>? Muted;

    /// <summary>
    /// Write public chat, emotes and joins/leaves to the chat log. Off in hive clones: the main process is the single writer
    /// of public chat (every process sees the same lines); clones still log their own lines and whispers.
    /// </summary>
    public bool LogPublicChat { get; set; } = true;

    /// <summary>
    /// Current system prompt, in cache order: (1) the static block — instructions + core knowledge; tools render
    /// before it and share its breakpoint; (2) the identity + self-profile block with its own 5m breakpoint, so a rename
    /// or personality update only rewrites that small block plus history. Per-turn data never goes here — state header,
    /// [memory] and events ride in the newest user message.
    /// </summary>
    public IReadOnlyList<LlmSystemBlock> SystemPrompt =>
        [new LlmSystemBlock(_staticText, Cache: true, Ttl: StaticTtl()), new LlmSystemBlock(DynamicSystemText(), Cache: true, Ttl: "5m")];

    /// <summary>
    /// Appends a static block to the brain's cached system prompt (e.g. the Lua script cheat-sheet: only the Opus brain
    /// writes scripts). Startup only, before the first turn: it is part of the cached prefix.
    /// </summary>
    public void ExtendSystem(string block)
    {
        if (string.IsNullOrWhiteSpace(block) || _staticText.Contains(block.Trim(), StringComparison.Ordinal)) return;
        _staticText = _staticText.TrimEnd() + "\n" + block.Trim() + "\n";
    }

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
        if (Orders != null) _ = ResumeOnStartAsync(ct);
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
        _events.BeginTurn(); // repeats of lines this turn already has are dropped until it ends
        try { return await HandleBatchCoreAsync(batch, ct).ConfigureAwait(false); }
        finally { _events.EndTurn(); }
    }

    private async Task<EpisodeOutcome> HandleBatchCoreAsync(EventBatch batch, CancellationToken ct)
    {
        _restartsThisTurn = 0;
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
        // A batch of nothing but events the previous turn already handled (injected mid-turn, then re-delivered) is not a
        // reason for another (silent) turn.
        if (AlreadyHandled(batch))
        {
            _log.LogInformation("skipped turn: every waking event was handled by the previous turn");
            return Done(new EpisodeOutcome(null, 0, LlmUsage.Zero, 0, [], false, "already handled"));
        }
        var decision = _triage.Choose(batch, mode);
        _log.LogDebug("triage → {Tier} ({Why})", decision.Tier, decision.Why);
        // Two-phase replies: safe small talk may be answered by the cheap tier alone; a correction of that escalates.
        var quick = await TryQuickAsync(batch, decision, mode, ct).ConfigureAwait(false);
        RunResult outcome;
        if (quick.Final != null) outcome = quick.Final;
        else
        {
            outcome = await RunEpisodeAsync(batch, decision.Tier, decision.CanEscalate, mode, quick.Prelude, ct, quick.Ack).ConfigureAwait(false);
            if (quick.Calls > 0)
                outcome = outcome with { Calls = outcome.Calls + quick.Calls, Usage = outcome.Usage.Add(quick.Usage ?? LlmUsage.Zero), Cost = outcome.Cost + quick.Cost };
        }

        if (outcome.Escalation is { } esc)
        {
            var prelude = $"(escalated from quick mode: {esc.Reason}" + (esc.Said.Count > 0 ? $"; already in chat: \"{string.Join(" ", esc.Said)}\", don't repeat or mention it)" : ")");
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

        // Idle again after answering: go back to the standing order (zero tokens).
        try { Orders?.ResumeIfIdle("after turn", _activities.Shutdown); } catch (Exception ex) { _log.LogWarning(ex, "standing order resume failed"); }
        if (batch.Waking.Any(e => e.Kind != EventKind.IdleTick)) _idleDefaultFired = false;
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

    private async Task<RunResult> RunEpisodeAsync(EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string? prelude, CancellationToken ct,
        string? preAck = null)
    {
        var episode = new Episode(tier, _time.GetUtcNow()) { IsTest = batch.IsTest };
        episode.Events.AddRange(batch.Events);
        var (turnText, turnHead) = await BuildTurnTextAsync(batch, tier, canEscalate, mode, prelude, ct).ConfigureAwait(false);
        episode.Messages.Add(LlmMessage.User(turnText));
        var flow = _options.ChatDebounce ?? new ChatDebounceOptions();

        string? whisperTo = batch.WhisperOnlyFrom;
        var audience = batch.Waking.Where(e => e.Player != null).Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var speaker = batch.Waking.FirstOrDefault(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && e.Player != null)?.Player;
        // Ambient for tools (and background tasks started from this turn): who asked, and whether it's injected test chat.
        TurnContext.Current = new TurnInfo(speaker, audience.ToArray(), batch.IsTest);
        _chat.MarkTurnStart(); // tools (watch_demo, ask_stand) stay quiet when the brain already said its line
        var said = new List<string>();
        string? held = null; // a later line from a round that still called tools: said at the end if nothing else was
        var usage = LlmUsage.Zero;
        decimal cost = 0;
        int calls = 0;
        var tools = ToolsFor(tier, canEscalate);
        bool retry = RetryAsked(batch);
        // A turn woken only by task/system events (a standing-order run finishing, a hive notice, an idle check-in): the brain's
        // plain text is a private log line, never public chat (live 10-27 23:30: "I didn't say anything in chat." + a run report
        // on every run). Only the say tool speaks there, and only while a player is engaged.
        bool quiet = IsQuietBatch(batch);
        try { if (quiet && SpeaksOn?.Invoke(batch) == true) quiet = false; } catch (Exception ex) { _log.LogDebug(ex, "speaks-on hook failed"); }
        _turnStatusAsked = MetaSpeech.AsksStatus(batch.Waking.Where(EventQueue.IsTyped).Select(e => e.Text));
        // Snapshot per episode: a rename mid-turn applies from the next turn, keeping this turn's prefix byte-stable.
        var system = SystemPrompt;
        // Budget fallback 1 (Economy): the brain's turn runs on the worker model; same tools and history.
        string? model = tier == ModelTier.Main && mode == BudgetMode.Economy ? _options.Llm.WorkerModel : null;
        int maxOut = _options.Llm.MaxTokensFor(tier);
        // Simple chat (a reflex, one clear job, small talk): a cheaper, faster effort for the WHOLE turn (an effort change
        // resets the history cache, so it never flips between rounds). Planning/multi-step turns keep the configured effort.
        bool chatTurn = batch.HasTypedChat;
        var (effort, thinking) = TurnEffort(batch, tier, chatTurn, model);
        Latency?.BeginTurn(chatTurn);
        if (chatTurn) Latency?.NoteEffort(effort ?? _options.Llm.EffortFor(tier));
        TurnStartedAt = _time.GetUtcNow();
        // Two-phase replies: streamed brain lines go out as they finish; a quick ack if the brain is slow to say anything.
        using var speech = new TurnSpeech(ct);
        var cx = new SpeechCtx(episode.Events, said, audience, () => whisperTo, episode.IsTest);
        StartAck(speech, batch, tier, turnHead, preAck, whisperTo, audience.ToArray(), ct);
        _turnSay = (t, c) => SayToolAsync(t, quiet, episode.Events, speech, cx, c);
        try
        {

        for (int round = 0; round < _options.MaxToolRounds; round++)
        {
            var nowMode = _usage.CheckMode();
            if (nowMode == BudgetMode.Paused || (tier == ModelTier.Main && nowMode == BudgetMode.CheapOnly && round > 0)) break;

            if (!FitHardCap(tier, system, tools, episode.Messages, maxOut))
            {
                _log.LogError("context can't fit under the {Cap} token hard cap; ending turn without a call", _context.Options.MaxTokens);
                break;
            }

            speech.BeginRound();
            AddAckNote(speech, episode.Messages);
            // once the brain spoke, later rounds keep the held-line rule; a quiet turn never streams (its text isn't chat)
            bool streamThis = tier == ModelTier.Main && said.Count == 0 && !quiet;
            var messages = _context.BuildMessages(tier, episode.Messages);
            // A call that re-writes a big cold history is not cut for a new chat line: the cut call's cache write never lands,
            // so its restart writes it all again (live 10-27 15:55: 2 × 38K). The new line joins at the next round instead.
            bool coldBig = tier == ModelTier.Main && _context.EstimateTokens() * _context.TokenScale > 15_000
                && Warmth.ColdReason(CacheWarmth.Key(model ?? _options.Llm.ModelFor(tier), effort, thinking), _time.GetUtcNow(), BlockCount(messages)) != null;
            var request = new LlmRequest
            {
                Tier = tier,
                Model = model,
                System = system,
                Tools = tools,
                Messages = messages,
                Purpose = tier == ModelTier.Cheap ? "chat" : "task",
                Interruptible = round == 0 && CanRestart(flow) && !coldBig,
                Effort = effort,
                Thinking = thinking,
                OnToolUse = EarlyStarter(tier, retry, model ?? _options.Llm.ModelFor(tier), ct),
                OnTextDelta = streamThis ? StreamHook(speech, cx, ct) : null,
            };
            LastRequestTokens = EstimateRequestTokens(request, maxOut);
            var variant = CacheWarmth.Key(model ?? _options.Llm.ModelFor(tier), effort, thinking);
            var callAt = _time.GetUtcNow();
            int reqBlocks = BlockCount(request.Messages);
            bool compactedBefore = Interlocked.Exchange(ref _compactedSinceCall, 0) != 0;
            string cacheCause = tier == ModelTier.Main
                ? CacheWriteGuard.Cause(Warmth.Last, variant, Warmth.ColdReason(variant, callAt, reqBlocks), Warmth.LastCallAt, callAt, compactedBefore)
                : "cheap-tier call (its own prefix: cold or new)";
            if (tier == ModelTier.Main) { Warmth.Note(variant, callAt, reqBlocks); _lastMainVariant = (effort, thinking); }
            LlmResponse response;
            if (!request.Interruptible)
            {
                response = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
            }
            else
            {
                // (a) A player adds a line while the first response is still generating (no tool ran, nothing said yet):
                // cancel the call, let them finish typing, and restart this round with every line in one user message.
                // History is untouched (the cancelled call never enters it), so the prefix stays cached.
                var call = await CompleteInterruptibleAsync(request, tier, maxOut, ct, speech.TryCut).ConfigureAwait(false);
                if (call.Response is null)
                {
                    Task pendingChain;
                    lock (speech.Lock) { speech.Cut = true; pendingChain = speech.Chain; speech.AckNoted = false; }
                    await DrainAsync(pendingChain).ConfigureAwait(false); // nothing of the cut call was sent: its text is dropped
                    _restartsThisTurn++;
                    DropEarlyStarts("restarted for new chat");
                    if (call.Partial is { } partial)
                    {
                        calls++;
                        usage = usage.Add(partial);
                        cost += call.PartialCost;
                        WarnCacheWrite(partial, tier, round, cacheCause, interrupted: true);
                    }
                    var more = await TakeAfterTypingAsync(flow, ct).ConfigureAwait(false);
                    if (more != null)
                    {
                        LogChat(more);
                        batch = new EventBatch([.. batch.Events, .. more.Events]);
                        episode.Events.AddRange(more.Events);
                        foreach (var p in more.Waking.Where(e => e.Player != null).Select(e => e.Player!))
                            if (!audience.Contains(p, StringComparer.OrdinalIgnoreCase)) audience.Add(p);
                        whisperTo = batch.WhisperOnlyFrom;
                        retry |= RetryAsked(more);
                        if (quiet && more.Waking.Any(IsPlayerFacing)) quiet = false;
                        _turnStatusAsked |= MetaSpeech.AsksStatus(more.Waking.Where(EventQueue.IsTyped).Select(e => e.Text));
                        TurnContext.Current = new TurnInfo(speaker, audience.ToArray(), batch.IsTest);
                    }
                    turnHead = RefreshHere(turnHead, batch);
                    episode.Messages[0] = LlmMessage.User((turnHead + batch.Format()).TrimEnd());
                    _log.LogInformation("restarted turn for new chat ({Restarts}/{Max}); {Flow}", _restartsThisTurn, flow.MaxRestartsPerTurn, _events.Stats.Summary());
                    round--;
                    continue;
                }
                response = call.Response;
            }
            calls++;
            Latency?.NoteRound();
            _avgOutTokens = _avgOutTokens <= 0 ? response.Usage.OutputTokens : 0.8 * _avgOutTokens + 0.2 * response.Usage.OutputTokens;
            usage = usage.Add(response.Usage);
            cost += _usage.Record(response.Model, response.Usage, tier);
            WarnCacheWrite(response.Usage, tier, round, cacheCause);
            if (tier == ModelTier.Main) Calibrate(request, response.Usage);
            _log.LogInformation("call {Label} ({Model}) round {Round}: {Usage} cache {Hit:P0}",
                UsageTracker.TierLabel(_options.Llm.TierOfModel(response.Model) ?? tier), response.Model, round, response.Usage, UsageTracker.CacheHit(response.Usage));

            var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
            if (content.Count == 0) content.Add(new LlmText("(silent)"));
            episode.Messages.Add(new LlmMessage(LlmRole.Assistant, content));

            var text = response.Text.Trim();
            bool streamed, wasCut;
            Task streamChain;
            lock (speech.Lock) { streamed = speech.SawDelta; wasCut = speech.Cut; streamChain = speech.Chain; }
            if (streamed && !wasCut)
            {
                // Streamed round: its lines already went out (or go out now, in order); nothing is sent twice.
                await FinishStreamAsync(speech, cx, ct).ConfigureAwait(false);
                text = "";
            }
            else if (wasCut) await DrainAsync(streamChain).ConfigureAwait(false); // cut too late to cancel: nothing of it was sent, speak it whole
            if (quiet && !IsSilent(text))
            {
                KeepInternal(text);
                text = "";
            }
            if (!IsSilent(text) && whisperTo == null && SpeechGate != null)
            {
                string? gated;
                try { gated = SpeechGate(new EventBatch(episode.Events), text, said.Count > 0); }
                catch (Exception ex) { _log.LogWarning(ex, "speech gate failed"); gated = text; }
                if (gated == null || IsSilent(gated.Trim()))
                {
                    _log.LogDebug("muted: {Text}", text);
                    try { Muted?.Invoke(text); } catch (Exception) { }
                    text = "";
                }
                else text = gated.Trim();
            }
            if (!IsSilent(text))
            {
                // Never narrate plumbing ("Delegating the craft.", "running script"): drop purely meta sentences.
                text = MetaSpeech.Strip(text, out var meta, _turnStatusAsked);
                foreach (var m in meta) _log.LogInformation("[meta suppressed] {Line}", m);
            }
            if (!IsSilent(text))
            {
                // One chat output per addressed message: never restate what this turn already said (paraphrase after a
                // tool round), and no second line from a round that is still calling tools — it waits for the end.
                text = SpeechDedupe.DropRepeats(text, said, out var dup);
                foreach (var d in dup) _log.LogInformation("[repeat suppressed] {Line}", d);
                if (!IsSilent(text) && said.Count > 0 && response.ToolUses.Any()) { held = text; text = ""; }
            }
            if (!IsSilent(text) && AckOf(speech) is { } ackLine && SameLine(text, ackLine)) text = ""; // the ack already said exactly this
            if (!IsSilent(text)) text = LastLook(text, audience);
            if (!IsSilent(text))
            {
                held = null;
                await SayBrainAsync(speech, cx, text, ct).ConfigureAwait(false);
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

            IReadOnlyList<LlmToolResult> results;
            using (ToolCaller.Push(tier, response.Model))
                results = await ExecuteToolsAsync(uses, tier, retry, ct).ConfigureAwait(false);
            var blocks = new List<LlmBlock>(results);

            // (b) Let urgent/new events reach the model mid-turn: appended as a text block AFTER the tool results in the same user
            // message, so tool_use/tool_result pairing stays valid and history stays append-only.
            var fresh = _events.TryTakeUrgent(flow.Enabled && !flow.InjectMidTurn ? EventPriority.High : EventPriority.Normal);
            if (fresh != null)
            {
                LogChat(fresh);
                episode.Events.AddRange(fresh.Events);
                if (flow.Enabled)
                {
                    blocks.Add(new LlmText(fresh.Format(fresh: true)));
                    _events.Stats.Injected(fresh.Events.Count);
                }
                else blocks.Add(new LlmText("[new events]\n" + fresh.Format()));
                foreach (var p in fresh.Waking.Where(e => e.Player != null).Select(e => e.Player!))
                    if (!audience.Contains(p, StringComparer.OrdinalIgnoreCase)) audience.Add(p);
                if (fresh.WhisperOnlyFrom == null && fresh.Waking.Any()) whisperTo = null;
                retry |= RetryAsked(fresh);
                // A player spoke mid-turn: from here on the turn answers them (its text is chat again).
                if (quiet && fresh.Waking.Any(IsPlayerFacing)) quiet = false;
                _turnStatusAsked |= MetaSpeech.AsksStatus(fresh.Waking.Where(EventQueue.IsTyped).Select(e => e.Text));
            }
            episode.Messages.Add(new LlmMessage(LlmRole.User, blocks));
        }
        if (held != null && SpeechDedupe.DropRepeats(held, said, out _) is { Length: > 0 } heldText && LastLook(heldText, audience) is { Length: > 0 } last
            && !IsSilent(last))
        {
            await SayBrainAsync(speech, cx, last, ct).ConfigureAwait(false);
        }
        }
        finally
        {
            _turnSay = null;
            _early.Clear();
            Interlocked.Exchange(ref _earlyChains, 0);
            TurnStartedAt = null;
            // A speculative walk the brain neither adopted nor replaced: it decided otherwise (talk, a question) — stop it.
            if (_activities.CancelSpeculative("not adopted by the brain")) _log.LogDebug("speculative step cancelled: not adopted");
            Latency?.EndTurn(workPending: _activities.IsBusy);
        }

        lock (_handled)
        {
            _handled.Clear();
            foreach (var e in episode.Events.Where(e => e.Wakes)) _handled.Add(HandledKey(e));
        }
        _context.Commit(episode);
        string? ackSaid;
        lock (speech.Lock) ackSaid = speech.Ack;
        return new RunResult(tier, calls, usage, cost, ackSaid != null ? [ackSaid, .. said] : said, null);
    }

    /// <summary>
    /// Right before sending (night review B20, L3): drop claims the live state already contradicts (a player "logged off" who
    /// re-joined during the turn; "I'm back"/"done" while this turn's walk/job still runs), and say places as landmarks
    /// ("the west cell of the gen") instead of raw x,y,z unless the player talks in coordinates.
    /// </summary>
    private string LastLook(string text, IReadOnlyCollection<string> audience)
    {
        var dropped = new List<string>();
        var since = TurnStartedAt ?? _time.GetUtcNow();
        try
        {
            text = SpeechFacts.DropStalePresence(text, _chat.OnlinePlayers, _chat.JoinedSince(since), dropped);
            if (_activities.Current is { } cur && cur.Started >= since)
            {
                bool moving = cur.Kind == ActivityKind.Move || (cur.Steps.Count > 0 && cur.Steps.All(st => ActivityManager.MovementTools.Contains(st.Tool)));
                text = SpeechFacts.DropPrematureClaims(text, moving, working: cur.Kind == ActivityKind.Job, dropped);
            }
            if (_chat.Landmark is { } landmark && Human.Questions.HasCoords(text))
            {
                var recent = audience.Count > 0 ? audience.SelectMany(p => _chat.RecentFrom(p)).ToList() : _chat.RecentFrom(null);
                var human = Human.Questions.Humanize(text, recent, landmark);
                if (human != text) _log.LogInformation("[coords as landmarks] {Text}", human);
                text = human;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "speech fact check failed"); }
        foreach (var d in dropped) _log.LogInformation("[stale claim suppressed] {Line}", d);
        return text.Trim();
    }

    /// <summary>When the brain's current turn started (null = idle). The brainstem's "thinking" fidget reads it.</summary>
    public DateTimeOffset? TurnStartedAt { get; private set; }

    /// <summary>Chat → reaction/first-action telemetry (null = off).</summary>
    public LatencyTracker? Latency { get; set; }

    /// <summary>
    /// Pre-observation for chat-triggered turns: "[here] ..." (≤~250 tokens) with what the brain would otherwise look up first
    /// (landmark, inventory, the speaker's place and facing, nearby builds, the job). Built fresh from live state for every
    /// turn (and again on a restart), deduped against the state header, [pointing] and [memory]. Null = off.
    /// </summary>
    public Func<EventBatch, string?>? HereBlock { get; set; }

    public const string HereTag = "[here]";
    public const string BodyTag = "[body:";

    private (string? Effort, string? Thinking) TurnEffort(EventBatch batch, ModelTier tier, bool chatTurn, string? model = null)
    {
        var bs = _options.Brainstem;
        if (!chatTurn || tier != ModelTier.Main || bs is not { Enabled: true }) return (null, null);
        var lines = batch.Waking.Where(EventQueue.IsTyped).Select(e => e.Text).ToList();
        bool simple = Brainstem.IntentMatcher.IsSimple(lines, NameWords());
        var kind = simple ? KindOf(lines) : "plan";
        Latency?.NoteKind(kind);
        if (!simple) return (null, null);
        (string? Effort, string? Thinking) want = (bs.SimpleTurnEffort is { Length: > 0 } e ? e : null, bs.SimpleTurnThinking is { Length: > 0 } t ? t : null);
        // The history cache is per effort/thinking variant: a cheaper-effort turn whose variant went cold (>4.5 min) would
        // re-write the whole history (live 10-27 15:55: 2 × 38K cache-write tokens). Stay on the warm variant instead.
        var key = CacheWarmth.Key(model ?? _options.Llm.ModelFor(tier), want.Effort, want.Thinking);
        int blocks = BlockCount(_context.BuildMessages(ModelTier.Main, [LlmMessage.User("(turn)")]));
        if (_lastMainVariant is { } last && Warmth.ShouldStay(key, downgrade: true, (int)(_context.EstimateTokens() * _context.TokenScale), _time.GetUtcNow(), blocks))
        {
            _log.LogDebug("effort: staying on {Effort} (the {Want} history cache is cold; switching would re-write ~{Tokens} tokens)",
                last.Effort ?? _options.Llm.EffortFor(tier), want.Effort, (int)(_context.EstimateTokens() * _context.TokenScale));
            return last;
        }
        return want;
    }

    /// <summary>Which brain effort/thinking variants have a warm history cache (see <see cref="CacheWarmth"/>).</summary>
    public CacheWarmth Warmth { get; } = new();

    /// <summary>Effort/thinking of the last main-tier call (null/null = configured).</summary>
    private (string? Effort, string? Thinking)? _lastMainVariant;

    private string KindOf(IReadOnlyList<string> lines)
    {
        var all = string.Join(" ", lines);
        var reflex = Brainstem.IntentMatcher.Reflex(all, NameWords(), _ => true);
        if (reflex.IsReflex) return "reflex:" + reflex.ReflexName;
        var spec = Brainstem.IntentMatcher.Speculative(all, NameWords());
        return spec.Kind switch
        {
            Brainstem.IntentKind.SpecGenerator => "spec:gen", Brainstem.IntentKind.SpecTree => "spec:tree",
            Brainstem.IntentKind.SpecToSpeaker => "spec:come", Brainstem.IntentKind.SpecBring => "spec:bring",
            Brainstem.IntentKind.SpecPlace => "spec:place", _ => "simple",
        };
    }

    private string _hereLine = "";

    /// <summary>The [here] line for this batch ("" = none), deduped against what the turn head already says.</summary>
    private string BuildHere(EventBatch batch, string already)
    {
        var bs = _options.Brainstem;
        if (HereBlock == null || bs is not { Enabled: true, HereBlock: true } || !batch.HasTypedChat) return "";
        string raw;
        try { raw = HereBlock(batch)?.Trim() ?? ""; }
        catch (Exception ex) { _log.LogWarning(ex, "[here] block failed"); return ""; }
        if (raw.Length == 0) return "";
        if (raw.StartsWith(HereTag, StringComparison.OrdinalIgnoreCase)) raw = raw[HereTag.Length..].Trim();
        var seen = Norm(already);
        var parts = raw.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(section => string.Join("; ", section.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => x.Length > 0 && !seen.Contains(Norm(x), StringComparison.Ordinal))))
            .Where(x => x.Length > 0).ToList();
        if (parts.Count == 0) return "";
        return TextUtil.Truncate(HereTag + " " + string.Join(" | ", parts), (int)(Math.Max(40, bs.HereMaxTokens) * 3.5));

        static string Norm(string s) => string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A restarted turn re-reads the world: the [here] line is rebuilt (never stale), the rest of the head kept.</summary>
    private string RefreshHere(string head, EventBatch batch)
    {
        if (_hereLine.Length == 0 || !head.Contains(_hereLine, StringComparison.Ordinal)) return head;
        var without = head.Replace(_hereLine + "\n", "", StringComparison.Ordinal);
        var fresh = BuildHere(batch, without);
        var updated = fresh.Length > 0 ? head.Replace(_hereLine, fresh, StringComparison.Ordinal) : without;
        _hereLine = fresh;
        return updated;
    }

    /// <summary>The first user message of a turn: (full text, the part before the events) — a restart swaps only the events.</summary>
    private async Task<(string Text, string Head)> BuildTurnTextAsync(EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string? prelude, CancellationToken ct)
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
        // Body lock visibility: a running script holds the body; the brain sees it without asking.
        if (BodyHolderNow() is { } holder) sb.Append(BodyTag).Append(' ').Append(holder).Append("]\n");
        string? standing = null;
        try { standing = Orders?.HeaderTag(); } catch (Exception ex) { _log.LogDebug(ex, "standing order read failed"); }
        if (standing != null) sb.Append(standing).Append('\n');
        var digest = BuildDigest(memory);
        if (digest.Length > 0) sb.Append(digest).Append('\n');
        string perception = "";
        try { perception = TurnPerception?.Invoke()?.Trim() ?? ""; } catch (Exception ex) { _log.LogWarning(ex, "turn perception failed"); }
        if (perception.Length > 0) sb.Append(perception).Append('\n');
        if (mode == BudgetMode.CheapOnly) sb.Append(Prompts.CheapOnlyHint).Append('\n');
        else if (tier == ModelTier.Cheap && canEscalate) sb.Append(Prompts.CheapTierHint).Append('\n');
        if (prelude != null) sb.Append(prelude).Append('\n');
        var self = SelfBlock(batch, tier);
        if (self.Length > 0) sb.Append(self).Append('\n');
        if (memory.Length > 0) sb.Append(memory).Append('\n');
        // [here]: what the brain would look up first, so it can act in round 0 (chat turns only; deduped, built live).
        _hereLine = BuildHere(batch, sb.ToString());
        if (_hereLine.Length > 0) sb.Append(_hereLine).Append('\n');
        string hint = "";
        if (TurnHint != null)
        {
            try { hint = TurnHint(batch)?.Trim() ?? ""; }
            catch (Exception ex) { _log.LogWarning(ex, "turn hint failed"); }
            if (hint.Length > 0) sb.Append(hint).Append('\n');
        }
        var busy = BusyLine(batch);
        if (busy.Length > 0) sb.Append(busy).Append('\n');
        var head = sb.ToString();
        var events = batch.Format();
        sb.Append(events);
        var text = sb.ToString().TrimEnd();
        try { TurnBuilt?.Invoke(new TurnInput(_time.GetUtcNow(), tier, status, digest, memory, events, text, self)); }
        catch (Exception ex) { _log.LogDebug(ex, "TurnBuilt handler failed"); }
        return (text, head);
    }

    public const string BusyTag = "[busy]";

    /// <summary>
    /// (c) Chat woke the brain while a delegate/script runs: one short line (uncached tail) so it amends instead of restarting.
    /// The full how-to (amend_task, script op=replace with carry) is in the cached system prompt.
    /// </summary>
    private string BusyLine(EventBatch batch)
    {
        var flow = _options.ChatDebounce;
        if (flow is not { Enabled: true, BusyHint: true } || !batch.HasTypedChat || _activities.Current is not { } act) return "";
        string? detail = null;
        try { detail = BusyDetail?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "busy detail failed"); }
        _events.Stats.BusyWake();
        return $"{BusyTag} {act.Label} #{act.Id} still running: amend it, don't restart" +
            (string.IsNullOrWhiteSpace(detail) ? "" : "; " + TextUtil.Truncate(TextUtil.OneLine(detail), 140));
    }

    // ---------------- restart on new chat ----------------

    private int _restartsThisTurn;
    private int _compactedSinceCall;

    /// <summary>Content blocks of a request's messages (the API's cache lookback counts these).</summary>
    internal static int BlockCount(IReadOnlyList<LlmMessage> messages) => messages.Sum(m => m.Content.Count);

    /// <summary>WARN on a call that re-wrote more than <see cref="CacheWriteGuard.WarnTokens"/> of prompt cache, with its likely cause.</summary>
    private void WarnCacheWrite(LlmUsage u, ModelTier tier, int round, string cause, bool interrupted = false)
    {
        if (CacheWriteGuard.Check(u, UsageTracker.TierLabel(tier), round, cause, interrupted) is { } w) _log.LogWarning("{Warning}", w);
    }

    /// <summary>
    /// Real prompt tokens vs the 3.5-chars/token estimate (live 10-27: 47K real at 30K estimated, so the 30K compaction threshold
    /// never fired and history reached 36K+). The scale feeds compaction so its thresholds mean real tokens.
    /// </summary>
    private void Calibrate(LlmRequest request, LlmUsage u)
    {
        long real = u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens;
        int est = EstimateRequestTokens(request, 0);
        if (real < 2_000 || est < 2_000) return; // tiny/fake calls say nothing about the ratio
        double ratio = Math.Clamp(real / (double)est, 1.0, 2.5);
        _context.TokenScale = Math.Round(0.7 * _context.TokenScale + 0.3 * ratio, 3);
    }
    private double _avgOutTokens;

    private bool CanRestart(ChatDebounceOptions flow) =>
        flow is { Enabled: true, RestartOnNewChat: true } && _restartsThisTurn < Math.Max(0, flow.MaxRestartsPerTurn);

    private sealed record InterruptibleCall(LlmResponse? Response, LlmUsage? Partial, decimal PartialCost);

    private long SavedEstimate(int maxOut, long wastedOut) =>
        // Without the restart the stale answer would have finished (about an average reply) and the new line would have
        // needed one more round (about another reply); the cache reads of that round and of the restart cancel out.
        (long)Math.Round(2 * (_avgOutTokens > 0 ? _avgOutTokens : Math.Min(150, maxOut))) - wastedOut;

    /// <summary>
    /// Runs the call, cancelling it as soon as addressed chat is posted (or is already pending). Response null = cancelled
    /// for new chat; the partial usage the client reported is recorded against the budget and the restart counters.
    /// </summary>
    private async Task<InterruptibleCall> CompleteInterruptibleAsync(LlmRequest request, ModelTier tier, int maxOut, CancellationToken ct,
        Func<bool>? mayCut = null)
    {
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnPosted(GameEvent e)
        {
            if (!EventQueue.IsTyped(e)) return;
            // A line of this call already streamed out: never cut it (the new chat joins at the round boundary instead).
            if (mayCut != null && !mayCut()) return;
            try { _ = cut.CancelAsync(); } catch (ObjectDisposedException) { }
        }
        _events.Posted += OnPosted;
        try
        {
            if (!_events.HasTypedChat)
                return new InterruptibleCall(await _llm.CompleteAsync(request, cut.Token).ConfigureAwait(false), null, 0);
        }
        catch (OperationCanceledException oce) when (cut.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            var interrupted = oce as LlmInterruptedException;
            var partial = interrupted?.PartialUsage;
            decimal usd = partial != null ? _usage.Record(interrupted!.Model, partial, tier) : 0;
            long wastedOut = partial?.OutputTokens ?? 0;
            _events.Stats.Restarted(partial?.InputTokens ?? 0, partial?.CacheReadTokens ?? 0, wastedOut, usd, SavedEstimate(maxOut, wastedOut));
            return new InterruptibleCall(null, partial, usd);
        }
        finally { _events.Posted -= OnPosted; }

        // Chat arrived while the turn text was being built: restart without calling (nothing spent).
        _events.Stats.Restarted(0, 0, 0, 0, SavedEstimate(maxOut, 0));
        return new InterruptibleCall(null, null, 0);
    }

    /// <summary>After a cancel: wait for the typing window of whoever is still typing (bounded), then take what's pending.</summary>
    private async Task<EventBatch?> TakeAfterTypingAsync(ChatDebounceOptions flow, CancellationToken ct)
    {
        if (_events.HasWaking)
        {
            try { await _events.WaitReadyAsync(ct).WaitAsync(TimeSpan.FromMilliseconds(Math.Max(0, flow.MaxMs) + 500), _time, ct).ConfigureAwait(false); }
            catch (TimeoutException) { }
        }
        return _events.TryTakeBatch();
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

    /// <summary>A player explicitly asked to try again in this batch (bypasses the failure memory).</summary>
    private static bool RetryAsked(EventBatch batch) =>
        batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper && ToolCallGuard.IsRetryRequest(e.Text));

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
                if (!LogPublicChat && e.Kind != EventKind.Whisper) continue; // hive clone: main logs public chat
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
        if (EventQueue.IsTyped(e) && e.Kind is EventKind.Chat or EventKind.Whisper)
        {
            Latency?.OnChat(e.Player, e.Text, _time.GetUtcNow());
            // A player said something new (the door is open now, they moved, they re-ask): old dead ends don't apply.
            int forgot = Guard.ForgetAll();
            if (forgot > 0) _log.LogDebug("tool guard: forgot {Count} failure(s) on new chat", forgot);
        }
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
        var idle = _options.Idle ?? new Human.IdleOptions();
        if (_options.IdleCheckInMinutes <= 0 && !idle.IdleDefault && !(idle.ResumeStandingOrders && Orders != null)) return null;
        var period = TimeSpan.FromSeconds(Math.Clamp(idle.CheckSeconds, 5, 60));
        return _time.CreateTimer(_ =>
        {
            if (ct.IsCancellationRequested) return;
            try { IdleTick(); } catch (Exception ex) { _log.LogWarning(ex, "idle tick failed"); }
        }, null, period, period);
    }

    private async Task ResumeOnStartAsync(CancellationToken ct)
    {
        // Restart / reboot / clone respawn: once the join settled, go back to the standing order.
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, (_options.Idle ?? new Human.IdleOptions()).ResumeOnStartSeconds)), _time, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        try { Orders?.ResumeIfIdle("start", ct); } catch (Exception ex) { _log.LogWarning(ex, "standing order resume failed"); }
    }

    private bool _idleDefaultFired;
    private DateTimeOffset _lastBusyAt = DateTimeOffset.MinValue;

    public const string IdleDefaultTag = "nothing to do";

    /// <summary>
    /// One idle check (the timer calls it; public for tests): busy → nothing; a standing order → resume it; no job, no order,
    /// nothing pending for <see cref="Human.IdleOptions.AfterMinutes"/> while players are online → one "[idle]" wake to pick a
    /// useful job from the allowlist (once per idle stretch); no players → sleep. Then the legacy idle check-in. Returns what
    /// it did ("resumed", "idle default", "check-in") or null.
    /// </summary>
    public string? IdleTick()
    {
        var now = _time.GetUtcNow();
        var idle = _options.Idle ?? new Human.IdleOptions();
        if (_activities.IsBusy)
        {
            _lastBusyAt = now;
            _idleDefaultFired = false;
            return null;
        }
        if (Orders?.ResumeIfIdle("idle", _activities.Shutdown) == true) return "resumed";
        if (_events.HasWaking) return null;

        bool humans;
        try { humans = HumansOnline?.Invoke() ?? _chat.OnlinePlayers.Count > 0; } catch (Exception) { humans = true; }
        var quietSince = _lastEpisode > _lastBusyAt ? _lastEpisode : _lastBusyAt;
        if (idle.IdleDefault && humans && !_idleDefaultFired && Orders?.Current == null && idle.Jobs.Length > 0 &&
            now - quietSince >= TimeSpan.FromMinutes(Math.Max(0, idle.AfterMinutes)))
        {
            _idleDefaultFired = true;
            _events.Post(GameEvent.Idle($"{IdleDefaultTag} for {(int)Math.Round((now - quietSince).TotalMinutes)}m and players are on: pick one useful job " +
                $"({string.Join("; ", idle.Jobs)}) and delegate it, or ask once with a default"));
            return "idle default";
        }

        // A standing order or a script is the bot's plan already: no check-in to ask what to do.
        if (Orders?.Current != null || BodyHolderNow() != null) return null;
        int minutes = IdleCheckInMinutesNow();
        if (minutes > 0 && now - _lastEpisode >= TimeSpan.FromMinutes(minutes))
        {
            _lastEpisode = now;
            _events.Post(GameEvent.Idle());
            return "check-in";
        }
        return null;
    }
}
