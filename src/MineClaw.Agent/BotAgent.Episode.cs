using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// The brain loop's steps (<see cref="RunEpisodeAsync"/>): prepare the turn, then per round build the request, call (or restart
/// for new chat), speak what the round says, and run its tools. Request bytes (system, tools, messages, cache placement) are a
/// cost contract: every step keeps the order and content the provider sees.
/// </summary>
public sealed partial class BotAgent
{
    /// <summary>One running episode's state, shared by its rounds (and by the say tool while the turn runs).</summary>
    private sealed class EpisodeTurn
    {
        public required Episode Episode { get; init; }
        public required EventBatch Batch { get; set; }
        public required ModelTier Tier { get; init; }
        public required bool CanEscalate { get; init; }
        public required ChatDebounceOptions Flow { get; init; }
        public required List<string> Audience { get; init; }
        public required string? Speaker { get; init; }
        public required string TurnHead { get; set; }
        public string? WhisperTo { get; set; }
        public List<string> Said { get; } = new();
        /// <summary>A later line from a round that still called tools: said at the end if nothing else was.</summary>
        public string? Held { get; set; }
        public LlmUsage Usage { get; set; } = LlmUsage.Zero;
        public decimal Cost { get; set; }
        public int Calls { get; set; }
        public IReadOnlyList<LlmToolDefinition> Tools { get; set; } = [];
        public IReadOnlyList<LlmSystemBlock> System { get; set; } = [];
        public string? Model { get; set; }
        public int MaxOut { get; set; }
        public string? Effort { get; set; }
        public string? Thinking { get; set; }
        public bool Retry { get; set; }
        public bool Quiet { get; set; }
        public bool WarnOnly { get; set; }
        public bool Talked { get; set; }
        public bool SayToolSpoke { get; set; }
        public TurnSpeech Speech { get; set; } = null!;
        public SpeechCtx Cx { get; set; } = null!;
        public RunResult? Escalated { get; set; }
        /// <summary>The round's first call, cut for new chat (and why it wrote cache): the restart bills it.</summary>
        public (InterruptibleCall Call, string CacheCause)? CutCall { get; set; }
    }

    private enum RoundEnd { Next, Restart, Stop, Escalate }

    // ---------------- turn start

    // AsyncLocal rule: TurnContext.Current is written only from synchronous helpers called by RunEpisodeAsync itself (an
    // async helper's write would not flow back to the turn, its tools or its later rounds).

    private Episode NewEpisode(EventBatch batch, ModelTier tier)
    {
        var episode = new Episode(tier, _time.GetUtcNow()) { IsTest = batch.IsTest };
        episode.Events.AddRange(batch.Events);
        return episode;
    }

    /// <summary>Synchronous on purpose: it sets <see cref="TurnContext.Current"/> for the running turn.</summary>
    private EpisodeTurn PrepareEpisode(Episode episode, EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string turnText, string turnHead)
    {
        episode.Messages.Add(LlmMessage.User(turnText));
        var t = new EpisodeTurn
        {
            Episode = episode, Batch = batch, Tier = tier, CanEscalate = canEscalate, Flow = _options.ChatDebounce ?? new ChatDebounceOptions(),
            WhisperTo = batch.WhisperOnlyFrom, TurnHead = turnHead,
            Audience = batch.Waking.Where(e => e.Player != null).Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Speaker = batch.Waking.FirstOrDefault(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && e.Player != null)?.Player,
        };
        SetTurnContext(t);
        _chat.MarkTurnStart(); // tools (watch_demo, ask_stand) stay quiet when the brain already said its line
        t.Tools = ToolsFor(tier, canEscalate);
        t.Retry = RetryAsked(batch);
        SetTurnVoice(t, batch);
        // Snapshot per episode: a rename mid-turn applies from the next turn, keeping this turn's prefix byte-stable.
        t.System = SystemPrompt;
        // Budget fallback 1 (Economy): the brain's turn runs on the worker model; same tools and history.
        t.Model = tier == ModelTier.Main && mode == BudgetMode.Economy ? _options.Llm.WorkerModel : null;
        t.MaxOut = _options.Llm.MaxTokensFor(tier);
        // Simple chat (a reflex, one clear job, small talk): a cheaper, faster effort for the WHOLE turn (an effort change
        // resets the history cache, so it never flips between rounds). Planning/multi-step turns keep the configured effort.
        bool chatTurn = batch.HasTypedChat;
        (t.Effort, t.Thinking) = TurnEffort(batch, tier, chatTurn, t.Model);
        Latency?.BeginTurn(chatTurn);
        if (chatTurn) Latency?.NoteEffort(t.Effort ?? _options.Llm.EffortFor(tier));
        TurnStartedAt = _time.GetUtcNow();
        _turnEvents = episode.Events; // the honesty guard checks counts against this turn's task results
        return t;
    }

    /// <summary>
    /// Whether this turn's plain text is public chat. A turn woken only by task/system events (a standing-order run finishing, a
    /// hive notice, an idle check-in) is quiet: its text is a private log line, never public chat (live 10-27 23:30: "I didn't say
    /// anything in chat." + a run report on every run); only the say tool speaks there, and only while a player is engaged. Woken
    /// only by a danger (a player getting hurt, my own low hp): the warning line is public, the rest of the turn's text is internal
    /// (live 10-28 01:3x: after "foxy!! u getting poked at the cactus farm, careful uwu" the same turn narrated "I warned Fox
    /// that... I'm also back on the cobble generator now..." into public chat).
    /// </summary>
    private void SetTurnVoice(EpisodeTurn t, EventBatch batch)
    {
        t.Quiet = IsQuietBatch(batch);
        t.WarnOnly = !t.Quiet && IsWarningBatch(batch);
        try { if (t.Quiet && SpeaksOn?.Invoke(batch) == true) t.Quiet = false; } catch (Exception ex) { _log.LogDebug(ex, "speaks-on hook failed"); }
        _turnStatusAsked = MetaSpeech.AsksStatus(batch.Waking.Where(EventQueue.IsTyped).Select(e => e.Text));
    }

    /// <summary>Two-phase replies: streamed brain lines go out as they finish; a quick ack if the brain is slow to say anything.</summary>
    private void StartEpisodeSpeech(EpisodeTurn t, TurnSpeech speech, string? preAck, CancellationToken ct)
    {
        t.Speech = speech;
        if (_instantThisTurn.Count > 0)
        {
            // The instant line is this turn's first line: no quick ack after it, and a brain line repeating it never goes out.
            lock (speech.Lock) { speech.Ack = string.Join(" | ", _instantThisTurn.Select(i => i.Text)); speech.AckNoted = true; }
            Voice?.Consume(_instantThisTurn);
        }
        t.Cx = new SpeechCtx(t.Episode.Events, t.Said, t.Audience, () => t.WhisperTo, t.Episode.IsTest);
        StartAck(speech, t.Batch, t.Tier, t.TurnHead, preAck, t.WhisperTo, t.Audience.ToArray(), ct);
        // A turn says at most its in-persona lines (live 10-28 05:27: a hand-over + death-notice turn said its thank-you with the
        // say tool, then its final text, "I picked the drop tower build back up since Fox already approved it. ... I thanked him
        // in chat and asked if he's ok.", went to public chat). After the say tool spoke, or once a turn no player typed in has
        // said its line, the rest of the brain's plain text is its own recap: internal.
        t.Talked = t.Batch.HasTypedChat;
        _turnSay = async (text, c) =>
        {
            var r = await SayToolAsync(text, t.Quiet, t.WarnOnly, t.Episode.Events, speech, t.Cx, c).ConfigureAwait(false);
            if (r.Ok && r.Text == "said") t.SayToolSpoke = true;
            return r;
        };
    }

    // ---------------- one round

    private async Task<RoundEnd> RunRoundAsync(EpisodeTurn t, int round, CancellationToken ct)
    {
        if (!CanCallRound(t, round)) return RoundEnd.Stop;
        t.Speech.BeginRound();
        AddAckNote(t.Speech, t.Episode.Messages);
        var request = BuildRoundRequest(t, round, ct);
        LastRequestTokens = EstimateRequestTokens(request, t.MaxOut);
        var cacheCause = NoteRoundCache(t, request);
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
            var call = await CompleteInterruptibleAsync(request, t.Tier, t.MaxOut, ct, t.Speech.TryCut).ConfigureAwait(false);
            if (call.Response is null)
            {
                t.CutCall = (call, cacheCause);
                return RoundEnd.Restart;
            }
            response = call.Response;
        }
        RecordRoundCall(t, request, response, round, cacheCause);
        await SpeakRoundAsync(t, response, ct).ConfigureAwait(false);
        return await FinishRoundAsync(t, response, ct).ConfigureAwait(false);
    }

    private bool CanCallRound(EpisodeTurn t, int round)
    {
        var nowMode = _usage.CheckMode();
        if (nowMode == BudgetMode.Paused || (t.Tier == ModelTier.Main && nowMode == BudgetMode.CheapOnly && round > 0)) return false;
        if (FitHardCap(t.Tier, t.System, t.Tools, t.Episode.Messages, t.MaxOut)) return true;
        _log.LogError("context can't fit under the {Cap} token hard cap; ending turn without a call", _context.Options.MaxTokens);
        return false;
    }

    private string ModelIdOf(EpisodeTurn t) => t.Model ?? _options.Llm.ModelFor(t.Tier);

    private LlmRequest BuildRoundRequest(EpisodeTurn t, int round, CancellationToken ct)
    {
        // once the brain spoke, later rounds keep the held-line rule; a quiet turn never streams (its text isn't chat)
        bool streamThis = t.Tier == ModelTier.Main && t.Said.Count == 0 && !t.Quiet && !t.WarnOnly;
        var messages = _context.BuildMessages(t.Tier, t.Episode.Messages);
        return new LlmRequest
        {
            Tier = t.Tier,
            Model = t.Model,
            System = t.System,
            Tools = t.Tools,
            Messages = messages,
            Purpose = t.Tier == ModelTier.Cheap ? "chat" : "task",
            Interruptible = round == 0 && CanRestart(t.Flow) && !ColdBigHistory(t, messages),
            Effort = t.Effort,
            Thinking = t.Thinking,
            OnToolUse = EarlyStarter(t.Tier, t.Retry, ModelIdOf(t), ct),
            OnTextDelta = streamThis ? StreamHook(t.Speech, t.Cx, ct) : null,
        };
    }

    /// <summary>
    /// A call that re-writes a big cold history is not cut for a new chat line: the cut call's cache write never lands, so its
    /// restart writes it all again (live 10-27 15:55: 2 × 38K). The new line joins at the next round instead.
    /// </summary>
    private bool ColdBigHistory(EpisodeTurn t, IReadOnlyList<LlmMessage> messages) =>
        t.Tier == ModelTier.Main && _context.EstimateTokens() * _context.TokenScale > 15_000
        && Warmth.ColdReason(CacheWarmth.Key(ModelIdOf(t), t.Effort, t.Thinking), _time.GetUtcNow(), BlockCount(messages)) != null;

    /// <summary>Why this call writes cache (for the cache-write warning), and the main tier's warmth bookkeeping.</summary>
    private string NoteRoundCache(EpisodeTurn t, LlmRequest request)
    {
        var variant = CacheWarmth.Key(ModelIdOf(t), t.Effort, t.Thinking);
        var callAt = _time.GetUtcNow();
        int reqBlocks = BlockCount(request.Messages);
        bool compactedBefore = Interlocked.Exchange(ref _compactedSinceCall, 0) != 0;
        if (t.Tier != ModelTier.Main) return "cheap-tier call (its own prefix: cold or new)";
        string cacheCause = CacheWriteGuard.Cause(Warmth.Last, variant, Warmth.ColdReason(variant, callAt, reqBlocks), Warmth.LastCallAt, callAt, compactedBefore);
        Warmth.Note(variant, callAt, reqBlocks);
        _lastMainVariant = (t.Effort, t.Thinking);
        return cacheCause;
    }

    /// <summary>
    /// The first call was cut for new chat: bill what it used and take the new lines (true = some joined the turn). The caller
    /// then sets the turn context and rebuilds the turn's user message (<see cref="RebuildTurnMessage"/>).
    /// </summary>
    private async Task<bool> TakeRestartLinesAsync(EpisodeTurn t, int round, CancellationToken ct)
    {
        var (call, cacheCause) = t.CutCall!.Value;
        t.CutCall = null;
        var speech = t.Speech;
        Task pendingChain;
        lock (speech.Lock) { speech.Cut = true; pendingChain = speech.Chain; speech.AckNoted = false; }
        await DrainAsync(pendingChain).ConfigureAwait(false); // nothing of the cut call was sent: its text is dropped
        _restartsThisTurn++;
        DropEarlyStarts("restarted for new chat");
        if (call.Partial is { } partial)
        {
            t.Calls++;
            t.Usage = t.Usage.Add(partial);
            t.Cost += call.PartialCost;
            WarnCacheWrite(partial, t.Tier, round, cacheCause, interrupted: true);
        }
        var more = await TakeAfterTypingAsync(t.Flow, ct).ConfigureAwait(false);
        if (more == null) return false;
        LogChat(more);
        t.Batch = new EventBatch([.. t.Batch.Events, .. more.Events]);
        t.Episode.Events.AddRange(more.Events);
        AddToAudience(t, more);
        t.WhisperTo = t.Batch.WhisperOnlyFrom;
        JoinSpeech(t, more);
        return true;
    }

    /// <summary>Ambient for tools (and background tasks started from this turn): who asked, and whether it's injected test chat.</summary>
    private static void SetTurnContext(EpisodeTurn t) => TurnContext.Current = new TurnInfo(t.Speaker, t.Audience.ToArray(), t.Batch.IsTest);

    /// <summary>A restarted round's user message: the [here] line rebuilt, every line in one message.</summary>
    private void RebuildTurnMessage(EpisodeTurn t)
    {
        t.TurnHead = RefreshHere(t.TurnHead, t.Batch);
        t.Episode.Messages[0] = LlmMessage.User((t.TurnHead + t.Batch.Format()).TrimEnd());
        _log.LogInformation("restarted turn for new chat ({Restarts}/{Max}); {Flow}", _restartsThisTurn, t.Flow.MaxRestartsPerTurn, _events.Stats.Summary());
    }

    private static void AddToAudience(EpisodeTurn t, EventBatch more)
    {
        foreach (var p in more.Waking.Where(e => e.Player != null).Select(e => e.Player!))
            if (!t.Audience.Contains(p, StringComparer.OrdinalIgnoreCase)) t.Audience.Add(p);
    }

    /// <summary>New lines joined the turn: a retry ask, a player speaking (the turn's text is chat again), a status question.</summary>
    private void JoinSpeech(EpisodeTurn t, EventBatch more)
    {
        t.Retry |= RetryAsked(more);
        (t.Quiet, t.WarnOnly) = AfterJoined(t.Quiet, t.WarnOnly, more);
        t.Talked |= more.HasTypedChat;
        _turnStatusAsked |= MetaSpeech.AsksStatus(more.Waking.Where(EventQueue.IsTyped).Select(e => e.Text));
    }

    private void RecordRoundCall(EpisodeTurn t, LlmRequest request, LlmResponse response, int round, string cacheCause)
    {
        t.Calls++;
        Latency?.NoteRound();
        _avgOutTokens = _avgOutTokens <= 0 ? response.Usage.OutputTokens : 0.8 * _avgOutTokens + 0.2 * response.Usage.OutputTokens;
        t.Usage = t.Usage.Add(response.Usage);
        t.Cost += _usage.Record(response.Model, response.Usage, t.Tier);
        WarnCacheWrite(response.Usage, t.Tier, round, cacheCause);
        if (t.Tier == ModelTier.Main) Calibrate(request, response.Usage);
        _log.LogInformation("call {Label} ({Model}) round {Round}: {Usage} cache {Hit:P0}",
            UsageTracker.TierLabel(_options.Llm.TierOfModel(response.Model) ?? t.Tier), response.Model, round, response.Usage, UsageTracker.CacheHit(response.Usage));

        var content = response.Content.Where(b => b is not LlmText tx || !string.IsNullOrWhiteSpace(tx.Text)).ToList();
        if (content.Count == 0) content.Add(new LlmText("(silent)"));
        t.Episode.Messages.Add(new LlmMessage(LlmRole.Assistant, content));
    }

    // ---------------- what a round says

    /// <summary>The round's text through every speech rule, then out (or held for the end of the turn).</summary>
    private async Task SpeakRoundAsync(EpisodeTurn t, LlmResponse response, CancellationToken ct)
    {
        var text = await AfterStreamAsync(t, response.Text.Trim(), ct).ConfigureAwait(false);
        text = KeepPrivateText(t, text);
        text = GateSpeech(t, text);
        text = StripMetaSpeech(t, text);
        text = WarningOnly(t, text);
        text = DedupeOrHold(t, text, response);
        if (!IsSilent(text) && AckOf(t.Speech) is { } ackLine && SameLine(text, ackLine)) text = ""; // the ack already said exactly this
        if (!IsSilent(text)) text = LastLook(text, t.Audience);
        if (IsSilent(text)) return;
        t.Held = null;
        await SayBrainAsync(t.Speech, t.Cx, text, ct).ConfigureAwait(false);
    }

    /// <summary>Streamed round: its lines already went out (or go out now, in order); nothing is sent twice. Cut too late to cancel:
    /// nothing of it was sent, speak it whole.</summary>
    private async Task<string> AfterStreamAsync(EpisodeTurn t, string text, CancellationToken ct)
    {
        var speech = t.Speech;
        bool streamed, wasCut;
        Task streamChain;
        lock (speech.Lock) { streamed = speech.SawDelta; wasCut = speech.Cut; streamChain = speech.Chain; }
        if (streamed && !wasCut)
        {
            await FinishStreamAsync(speech, t.Cx, ct).ConfigureAwait(false);
            return "";
        }
        if (wasCut) await DrainAsync(streamChain).ConfigureAwait(false);
        return text;
    }

    /// <summary>A quiet turn's text, or anything after the say tool spoke / after an untyped turn said its line: internal.</summary>
    private string KeepPrivateText(EpisodeTurn t, string text)
    {
        if (IsSilent(text)) return text;
        if (!t.Quiet && !t.SayToolSpoke && (t.Talked || t.Said.Count == 0)) return text;
        KeepInternal(text);
        return "";
    }

    private string GateSpeech(EpisodeTurn t, string text)
    {
        if (IsSilent(text) || t.WhisperTo != null || SpeechGate == null) return text;
        string? gated;
        try { gated = SpeechGate(new EventBatch(t.Episode.Events), text, t.Said.Count > 0); }
        catch (Exception ex) { _log.LogWarning(ex, "speech gate failed"); gated = text; }
        if (gated != null && !IsSilent(gated.Trim())) return gated.Trim();
        _log.LogDebug("muted: {Text}", text);
        try { Muted?.Invoke(text); } catch (Exception) { }
        return "";
    }

    /// <summary>Never narrate plumbing ("Delegating the craft.", "running script"): drop purely meta sentences.</summary>
    private string StripMetaSpeech(EpisodeTurn t, string text)
    {
        if (IsSilent(text)) return text;
        text = MetaSpeech.Strip(text, out var meta, _turnStatusAsked, AddresseeNames(t.Audience));
        foreach (var m in meta) _log.LogInformation("[meta suppressed] {Line}", m);
        return text;
    }

    private string WarningOnly(EpisodeTurn t, string text)
    {
        if (!t.WarnOnly || IsSilent(text)) return text;
        var (warning, rest) = WarningLine(text, t.Said.Count > 0);
        if (!IsSilent(rest)) KeepInternal(rest);
        return warning;
    }

    /// <summary>
    /// One chat output per addressed message: never restate what this turn already said (paraphrase after a tool round), and no
    /// second line from a round that is still calling tools — it waits for the end.
    /// </summary>
    private string DedupeOrHold(EpisodeTurn t, string text, LlmResponse response)
    {
        if (IsSilent(text)) return text;
        text = SpeechDedupe.DropRepeats(text, t.Said, out var dup);
        foreach (var d in dup) _log.LogInformation("[repeat suppressed] {Line}", d);
        if (IsSilent(text) || t.Said.Count == 0 || !response.ToolUses.Any()) return text;
        t.Held = text;
        return "";
    }

    /// <summary>The held line, if nothing after it was said and it still says something new.</summary>
    private async Task SayHeldAsync(EpisodeTurn t, CancellationToken ct)
    {
        if (t.Held != null && SpeechDedupe.DropRepeats(t.Held, t.Said, out _) is { Length: > 0 } heldText && LastLook(heldText, t.Audience) is { Length: > 0 } last
            && !IsSilent(last))
        {
            await SayBrainAsync(t.Speech, t.Cx, last, ct).ConfigureAwait(false);
        }
    }

    // ---------------- after the text: stop, escalate, or run the tools

    private async Task<RoundEnd> FinishRoundAsync(EpisodeTurn t, LlmResponse response, CancellationToken ct)
    {
        if (response.StopReason == StopReasons.MaxTokens)
        {
            var cappedUses = response.ToolUses.ToList();
            if (cappedUses.Count > 0)
                t.Episode.Messages.Add(new LlmMessage(LlmRole.User, (await TruncatedToolsAsync(cappedUses).ConfigureAwait(false)).ToList<LlmBlock>()));
            _log.LogWarning("response truncated; remaining tools not executed");
            return RoundEnd.Stop;
        }
        if (response.StopReason == StopReasons.Refusal)
        {
            _log.LogWarning("model refused");
            return RoundEnd.Stop;
        }
        var uses = response.ToolUses.ToList();
        if (uses.Count == 0) return RoundEnd.Stop;

        var esc = uses.FirstOrDefault(u => u.Name == EscalateToolName);
        if (esc != null && t.CanEscalate)
        {
            // Discard the cheap exchange (its escalate call never enters history) and let the main tier redo the turn.
            t.Escalated = new RunResult(t.Tier, t.Calls, t.Usage, t.Cost, t.Said, new Escalation(esc.Input.Str("reason") ?? "needs planning", t.Said));
            return RoundEnd.Escalate;
        }

        IReadOnlyList<LlmToolResult> results;
        using (ToolCaller.Push(t.Tier, response.Model))
            results = await ExecuteToolsAsync(uses, t.Tier, t.Retry, ct).ConfigureAwait(false);
        var blocks = new List<LlmBlock>(results);
        InjectFreshEvents(t, blocks);
        t.Episode.Messages.Add(new LlmMessage(LlmRole.User, blocks));
        return RoundEnd.Next;
    }

    /// <summary>
    /// (b) Let urgent/new events reach the model mid-turn: appended as a text block AFTER the tool results in the same user
    /// message, so tool_use/tool_result pairing stays valid and history stays append-only.
    /// </summary>
    private void InjectFreshEvents(EpisodeTurn t, List<LlmBlock> blocks)
    {
        var flow = t.Flow;
        var fresh = _events.TryTakeUrgent(flow.Enabled && !flow.InjectMidTurn ? EventPriority.High : EventPriority.Normal);
        if (fresh == null) return;
        LogChat(fresh);
        t.Episode.Events.AddRange(fresh.Events);
        if (flow.Enabled)
        {
            blocks.Add(new LlmText(fresh.Format(fresh: true)));
            _events.Stats.Injected(fresh.Events.Count);
        }
        else blocks.Add(new LlmText("[new events]\n" + fresh.Format()));
        AddToAudience(t, fresh);
        if (fresh.WhisperOnlyFrom == null && fresh.Waking.Any()) t.WhisperTo = null;
        // A player spoke mid-turn: from here on the turn answers them (its text is chat again).
        JoinSpeech(t, fresh);
    }

    // ---------------- turn end

    private void EndEpisodeTurn()
    {
        _turnSay = null;
        _early.Clear();
        Interlocked.Exchange(ref _earlyChains, 0);
        TurnStartedAt = null;
        _turnEvents = null;
        // A speculative walk the brain neither adopted nor replaced: it decided otherwise (talk, a question) — stop it.
        if (_activities.CancelSpeculative("not adopted by the brain")) _log.LogDebug("speculative step cancelled: not adopted");
        Latency?.EndTurn(workPending: _activities.IsBusy);
        try { TurnEnded?.Invoke(); } catch (Exception) { }
    }

    private RunResult CommitEpisode(EpisodeTurn t)
    {
        lock (_handled)
        {
            _handled.Clear();
            foreach (var e in t.Episode.Events.Where(e => e.Wakes)) _handled.Add(HandledKey(e));
        }
        _context.Commit(t.Episode);
        string? ackSaid;
        lock (t.Speech.Lock) ackSaid = t.Speech.Ack;
        return new RunResult(t.Tier, t.Calls, t.Usage, t.Cost, ackSaid != null ? [ackSaid, .. t.Said] : t.Said, null);
    }
}
