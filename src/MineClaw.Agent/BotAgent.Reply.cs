using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// Two-phase replies: respond quickly, then conclusively (<see cref="ReplyOptions"/>).
/// <list type="number">
/// <item>Streamed chat: the brain's completed chat lines go out the moment they finish streaming (sentence/line boundaries),
/// each through the usual filters (speech gate, meta speech, repeats, live fact checks). The end of the round sends only what
/// never streamed. Once a line of a call is out, the call can't be cut for new chat (no half-sent restarts).</item>
/// <item>Quick ack: no brain line <see cref="ReplyOptions.AckAfterMs"/> after the addressed message → one Haiku call writes a
/// ≤5-word non-committal line; linted, else silence. At most one per turn; the brain's next round gets
/// <c>(already in chat: "…"; don't repeat or mention it)</c> in its newest user message.</item>
/// <item>Cheap-only: safe small talk is triaged by the same single cheap call; a final answer there means no brain call.
/// A correction right after it escalates and counts a miss.</item>
/// </list>
/// </summary>
public sealed partial class BotAgent
{
    public const string AckPurpose = "quick_ack";
    public const string QuickReplyPurpose = "quick_reply";
    public const string AckNoteFormat = "(already in chat: \"{0}\"; don't repeat or mention it)";
    public const string QuickReplyTag = "[quick reply: cheap tier answered, no brain call]";

    private static readonly Regex SentenceEnd = new(@"[.!?~]+(?=\s)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private ReplyOptions ReplyOpts => _options.Brainstem?.Reply ?? new ReplyOptions();
    private bool ReplyOn => _options.Brainstem is { Enabled: true };

    /// <summary>One turn's speech state (streamed lines, the ack). Every field is guarded by <see cref="Lock"/>.</summary>
    private sealed class TurnSpeech : IDisposable
    {
        public readonly object Lock = new();
        public readonly StringBuilder Buf = new();
        /// <summary>A brain line went out (or is going out): the ack is off for this turn.</summary>
        public bool BrainSpoke;
        /// <summary>The current call was cut for new chat: its text is dropped.</summary>
        public bool Cut;
        /// <summary>A line of the current call went out: it may no longer be cut.</summary>
        public bool SentThisCall;
        /// <summary>The current call streamed text: the end of the round speaks only the rest.</summary>
        public bool SawDelta;
        public bool FirstFlushed;
        public int LinesThisRound;
        public Task Chain = Task.CompletedTask;
        public string? Ack;
        public bool AckNoted;
        public Task? AckSend;
        public readonly CancellationTokenSource AckCts;

        public TurnSpeech(CancellationToken ct) => AckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        public void BeginRound()
        {
            lock (Lock) { Buf.Clear(); Cut = SentThisCall = SawDelta = FirstFlushed = false; LinesThisRound = 0; }
        }

        /// <summary>The interruptible call may be cut only while nothing of it was sent.</summary>
        public bool TryCut()
        {
            lock (Lock)
            {
                if (SentThisCall) return false;
                Cut = true;
                return true;
            }
        }

        public void Dispose()
        {
            try { AckCts.Cancel(); } catch (ObjectDisposedException) { }
            AckCts.Dispose();
        }
    }

    /// <summary>What streamed speech needs from the running turn (live references; the loop mutates them only between rounds).</summary>
    private sealed record SpeechCtx(List<GameEvent> Events, List<string> Said, List<string> Audience, Func<string?> WhisperTo, bool IsTest);

    // ---------------- 1. streamed chat ----------------

    private Action<string>? StreamHook(TurnSpeech sp, SpeechCtx cx, CancellationToken ct)
    {
        var o = ReplyOpts;
        if (!ReplyOn || !o.StreamChat) return null;
        return delta =>
        {
            lock (sp.Lock)
            {
                if (sp.Cut) return;
                sp.SawDelta = true;
                sp.Buf.Append(delta);
                foreach (var seg in TakeSegments(sp, o.StreamMinChars, final: false)) Enqueue(sp, cx, seg, ct);
            }
        };
    }

    /// <summary>Completed segments: up to a line end, or a sentence end (the first of the round at once, later ones once
    /// <paramref name="minChars"/> are buffered). <paramref name="final"/> = the rest too. Caller holds the lock.</summary>
    private static List<string> TakeSegments(TurnSpeech sp, int minChars, bool final)
    {
        var segs = new List<string>();
        while (sp.Buf.Length > 0)
        {
            var s = sp.Buf.ToString();
            int cut = s.IndexOf('\n') is var nl and >= 0 ? nl + 1 : -1;
            if (cut < 0)
                foreach (Match m in SentenceEnd.Matches(s))
                {
                    int end = m.Index + m.Length;
                    if (!sp.FirstFlushed || end >= minChars) { cut = end; break; }
                }
            if (cut < 0) break;
            var seg = s[..cut].Trim();
            sp.Buf.Remove(0, cut);
            if (seg.Length > 0) { segs.Add(seg); sp.FirstFlushed = true; }
        }
        if (final && sp.Buf.Length > 0)
        {
            var rest = sp.Buf.ToString().Trim();
            sp.Buf.Clear();
            if (rest.Length > 0) segs.Add(rest);
        }
        return segs;
    }

    /// <summary>Chains one segment behind the previous ones (order kept; never on the stream thread). Caller holds the lock.</summary>
    private void Enqueue(TurnSpeech sp, SpeechCtx cx, string seg, CancellationToken ct) =>
        sp.Chain = sp.Chain.ContinueWith(_ => SpeakSegmentAsync(sp, cx, seg, ct), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

    /// <summary>End of a streamed round: the unsent rest goes through the same path, then every line is out.</summary>
    private async Task FinishStreamAsync(TurnSpeech sp, SpeechCtx cx, CancellationToken ct)
    {
        Task chain;
        lock (sp.Lock)
        {
            if (!sp.Cut) foreach (var seg in TakeSegments(sp, ReplyOpts.StreamMinChars, final: true)) Enqueue(sp, cx, seg, ct);
            chain = sp.Chain;
        }
        await DrainAsync(chain).ConfigureAwait(false);
    }

    private async Task DrainAsync(Task chain)
    {
        try { await chain.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "streamed chat failed"); }
    }

    private async Task SpeakSegmentAsync(TurnSpeech sp, SpeechCtx cx, string seg, CancellationToken ct)
    {
        string? ack;
        lock (sp.Lock) { if (sp.Cut) return; ack = sp.Ack; }
        var text = FilterStreamed(seg, cx, ack);
        if (IsSilent(text)) return;
        int max = Math.Max(1, _chat.Options.MaxLinesPerReply);
        lock (sp.Lock)
        {
            if (sp.Cut) return;
            if (sp.LinesThisRound >= max) { _log.LogInformation("[stream cap] {Line}", text); return; }
            sp.SentThisCall = true;
        }
        var lines = await SayBrainAsync(sp, cx, text, ct).ConfigureAwait(false);
        lock (sp.Lock) sp.LinesThisRound += lines.Count;
    }

    /// <summary>The per-line chat filters of the end-of-round path, for one streamed segment.</summary>
    private string FilterStreamed(string seg, SpeechCtx cx, string? ack)
    {
        var text = seg.Trim();
        if (IsSilent(text)) return "";
        List<string> said;
        lock (cx.Said) said = cx.Said.ToList();
        if (cx.WhisperTo() == null && SpeechGate != null)
        {
            string? gated;
            try { gated = SpeechGate(new EventBatch(cx.Events.ToList()), text, said.Count > 0); }
            catch (Exception ex) { _log.LogWarning(ex, "speech gate failed"); gated = text; }
            if (gated == null || IsSilent(gated.Trim()))
            {
                _log.LogDebug("muted: {Text}", text);
                try { Muted?.Invoke(text); } catch (Exception) { }
                return "";
            }
            text = gated.Trim();
        }
        text = MetaSpeech.Strip(text, out var meta, _turnStatusAsked, AddresseeNames(cx.Audience.ToArray()));
        foreach (var m in meta) _log.LogInformation("[meta suppressed] {Line}", m);
        if (IsSilent(text.Trim())) return "";
        text = SpeechDedupe.DropRepeats(text, said, out var dup);
        foreach (var d in dup) _log.LogInformation("[repeat suppressed] {Line}", d);
        if (IsSilent(text)) return "";
        if (ack != null && SameLine(text, ack)) { _log.LogInformation("[repeat of ack suppressed] {Line}", text); return ""; }
        return LastLook(text, cx.Audience.ToArray());
    }

    private static string? AckOf(TurnSpeech sp) { lock (sp.Lock) return sp.Ack; }

    private static bool SameLine(string a, string b)
    {
        static string N(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z]+", "");
        return N(a).Length > 0 && N(a) == N(b);
    }

    /// <summary>Every brain chat line goes out here: after an ack still being typed, marks the turn as spoken, telemetry.</summary>
    private async Task<IReadOnlyList<string>> SayBrainAsync(TurnSpeech sp, SpeechCtx cx, string text, CancellationToken ct)
    {
        Task? ack;
        lock (sp.Lock) { sp.BrainSpoke = true; ack = sp.AckSend; }
        if (ack != null) { try { await ack.ConfigureAwait(false); } catch (Exception) { } }
        var whisperTo = cx.WhisperTo();
        List<GameEvent> turn;
        lock (cx.Events) turn = cx.Events.ToList();
        text = _chat.UnfoldNames(text, turn); // "robin" for robinchan in a turn about robinchan, not Robin
        var lines = await _chat.SayAsync(text, whisperTo, cx.Audience.ToArray(), ct).ConfigureAwait(false);
        if (lines.Count > 0)
        {
            _chat.NoteModelSpeech();
            Latency?.NoteReaction();
            Latency?.NoteChatLine(_time.GetUtcNow());
        }
        lock (cx.Said) cx.Said.AddRange(lines);
        LogSaid(lines, whisperTo, cx.IsTest);
        return lines;
    }

    // ---------------- 2. quick ack ----------------

    /// <summary>The ack, once sent, rides in the brain's newest user message (uncached tail) of the next call.</summary>
    private static void AddAckNote(TurnSpeech sp, List<LlmMessage> messages)
    {
        string ack;
        lock (sp.Lock)
        {
            if (sp.Ack == null || sp.AckNoted || messages.Count == 0 || messages[^1].Role != LlmRole.User) return;
            ack = sp.Ack;
            sp.AckNoted = true;
        }
        var last = messages[^1];
        messages[^1] = last with { Content = [.. last.Content, new LlmText(string.Format(AckNoteFormat, ack))] };
    }

    private void StartAck(TurnSpeech sp, EventBatch batch, ModelTier tier, string turnHead, string? preAck, string? whisperTo,
        string[] audience, CancellationToken ct)
    {
        var o = ReplyOpts;
        if (!ReplyOn || !o.QuickAck || tier != ModelTier.Main || !batch.HasTypedChat) return;
        var typed = batch.Waking.Where(EventQueue.IsTyped).ToList();
        if (typed.Count == 0) return;
        // Only after the addressee decision says it's for us (live 10-27: Robin asked robinchan "you have to respond to
        // everything currently?" and main acked "hm, lemme think~"): never on an ambiguous or someone else's line.
        if (!SureAddressed(typed, o))
        {
            _log.LogInformation("quick ack skipped: not sure the line is for us ({Lines})", string.Join(" / ", typed.Select(e => $"{e.Player}: {TextUtil.Truncate(e.Text, 40)} ({e.AddressConfidence:0.0})")));
            return;
        }
        var lines = typed.Select(e => e.Text).ToList();
        // A reflex already answered with the body (the brain sees "[reflex] ..."): no words needed first.
        if (turnHead.Contains("[reflex]", StringComparison.Ordinal) || lines.All(l => IntentMatcher.Reflex(l, NameWords(), _ => true).IsReflex)) return;
        if (preAck == null && o.AckSkipSmallTalk && QuickReply.Classify(lines, NameWords(), typed.All(e => e.Kind == EventKind.Emote)) != QuickKind.Brain) return;
        var chatAt = typed.Min(e => e.At);
        var now = _time.GetUtcNow();
        if (chatAt > now || now - chatAt > TimeSpan.FromSeconds(30)) chatAt = now; // a clock that doesn't match the queue's stamp
        var due = chatAt + TimeSpan.FromMilliseconds(Math.Max(0, o.AckAfterMs));
        var callAt = due - TimeSpan.FromMilliseconds(Math.Clamp(o.AckCallLeadMs, 0, Math.Max(0, o.AckAfterMs)));
        var player = typed[0].Player ?? "player";
        var line = TextUtil.Truncate(string.Join(" / ", lines.Select(TextUtil.OneLine)), 200);
        var token = sp.AckCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await WaitUntilAsync(sp, callAt, token).ConfigureAwait(false)) return;
                var ack = preAck;
                if (ack == null)
                {
                    if (_usage.CheckMode() == BudgetMode.Paused) return;
                    ack = await AckCallAsync(player, line, token).ConfigureAwait(false);
                    if (ack == null) return;
                }
                if (!await WaitUntilAsync(sp, due, token).ConfigureAwait(false))
                {
                    _log.LogDebug("quick ack not needed: the brain spoke first ({Ack})", ack);
                    return;
                }
                if (whisperTo == null && SpeechGate != null)
                {
                    try { ack = SpeechGate(batch, ack, false); } catch (Exception ex) { _log.LogWarning(ex, "speech gate failed"); }
                    if (ack == null || IsSilent(ack.Trim())) return;
                }
                var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (sp.Lock)
                {
                    if (sp.BrainSpoke || sp.Ack != null || sp.AckSend != null) return;
                    sp.AckSend = send.Task;
                }
                try
                {
                    var sent = await _chat.SayAsync(ack, whisperTo, audience, ct).ConfigureAwait(false);
                    if (sent.Count == 0) return;
                    lock (sp.Lock) sp.Ack = string.Join(" ", sent);
                    Latency?.NoteChatLine(_time.GetUtcNow(), ack: true);
                    LogSaid(sent, whisperTo, batch.IsTest);
                    _log.LogInformation("[quick ack] (cheap tier) {Ack}", string.Join(" ", sent));
                }
                finally { send.TrySetResult(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning(ex, "quick ack failed"); }
        }, CancellationToken.None);
    }

    /// <summary>Every typed line is for us with at least <see cref="ReplyOptions.AckMinConfidence"/> (router-less events count as sure).</summary>
    private static bool SureAddressed(IEnumerable<GameEvent> typed, ReplyOptions o) =>
        typed.All(e => e.AddressConfidence is not { } c || c >= o.AckMinConfidence);

    /// <summary>Waits (on <see cref="_time"/>) until <paramref name="at"/>; false when the brain spoke first or the turn ended.</summary>
    private async Task<bool> WaitUntilAsync(TurnSpeech sp, DateTimeOffset at, CancellationToken ct)
    {
        while (true)
        {
            lock (sp.Lock) if (sp.BrainSpoke) return false;
            if (ct.IsCancellationRequested) return false;
            var left = at - _time.GetUtcNow();
            if (left <= TimeSpan.Zero) return true;
            try { await Task.Delay(left < TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private string Persona() => string.IsNullOrWhiteSpace(_options.Persona) ? Prompts.DefaultPersona : _options.Persona;

    private async Task<string?> AckCallAsync(string player, string line, CancellationToken ct)
    {
        var o = ReplyOpts;
        var response = await _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Cheap,
            System = [new LlmSystemBlock(QuickReply.AckSystem(_chat.DisplayName, Persona(), o.AckMaxWords, o.AckMaxChars))],
            Messages = [LlmMessage.User($"<{player}> {line}")],
            MaxTokens = Math.Max(16, o.MaxTokens),
            CacheHistory = false,
            Purpose = AckPurpose,
        }, ct).ConfigureAwait(false);
        _usage.Record(response.Model, response.Usage, ModelTier.Cheap);
        var ack = QuickReply.LintAck(response.Text, o.AckMaxWords, o.AckMaxChars);
        if (ack == null) _log.LogInformation("[quick ack rejected by lint] {Text}", TextUtil.OneLine(response.Text));
        return ack;
    }

    // ---------------- 3. cheap-only replies ----------------

    private sealed record QuickResult(RunResult? Final = null, string? Ack = null, string? Prelude = null, int Calls = 0,
        LlmUsage? Usage = null, decimal Cost = 0);

    private (string Player, DateTimeOffset At, string Reply)? _lastCheap;

    /// <summary>
    /// Before the brain: a correction of the last cheap-only reply escalates (a miss); safe small talk gets one cheap call that
    /// either answers it for good (no brain call) or hands back an ack for the brain's turn.
    /// </summary>
    private async Task<QuickResult> TryQuickAsync(EventBatch batch, TriageDecision decision, BudgetMode mode, CancellationToken ct)
    {
        var o = ReplyOpts;
        if (!ReplyOn || decision.Tier != ModelTier.Main || !batch.HasTypedChat) return new();
        var waking = batch.Waking.ToList();
        var typed = waking.Where(EventQueue.IsTyped).ToList();
        var now = _time.GetUtcNow();

        if (_lastCheap is { } last)
        {
            bool samePlayer = typed.Any(e => last.Player.Equals(e.Player, StringComparison.OrdinalIgnoreCase));
            if (samePlayer) _lastCheap = null;
            if (samePlayer && now - last.At <= TimeSpan.FromSeconds(Math.Max(0, o.CorrectionWindowSeconds)) && typed.Any(e => QuickReply.IsCorrection(e.Text)))
            {
                Latency?.NoteCheapMiss();
                _log.LogInformation("cheap-path miss: {Player} corrected the quick reply \"{Reply}\"; escalating to the brain", last.Player, last.Reply);
                return new(Prelude: $"(your quick reply \"{last.Reply}\" missed: {last.Player} is correcting it; answer properly)");
            }
        }

        if (!o.CheapOnly || mode is not (BudgetMode.Normal or BudgetMode.Economy)) return new();
        if (!SureAddressed(typed, o)) return new(); // might be someone else's line: no cheap answer, no ack; the brain decides
        if (waking.Count != typed.Count || batch.HasDanger || typed.Any(e => e.Player == null)) return new();
        bool emote = typed.All(e => e.Kind == EventKind.Emote);
        var kind = QuickReply.Classify(typed.Select(e => e.Text).ToList(), NameWords(), emote);
        if (kind == QuickKind.Brain) return new();

        string header = "";
        try { header = StateHeader?.Invoke() ?? ""; } catch (Exception ex) { _log.LogDebug(ex, "state header failed"); }
        var activity = _activities.Describe();
        var state = "[state] " + header.Trim() + " | task: " + (activity ?? "idle");
        var here = BuildHere(batch, header);
        var players = typed.Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? self = null;
        try { self = Growth != null ? Growth.RenderBlock(players) : Self?.Render(); } catch (Exception ex) { _log.LogDebug(ex, "self render failed"); }
        var events = batch.Format();
        var context = state + "\n" + (here.Length > 0 ? here + "\n" : "");

        LlmResponse response;
        try
        {
            response = await _llm.CompleteAsync(new LlmRequest
            {
                Tier = ModelTier.Cheap,
                System = [new LlmSystemBlock(QuickReply.TriageSystem(_chat.DisplayName, Persona(), self, o.AckMaxWords))],
                Messages = [LlmMessage.User(context + events)],
                MaxTokens = Math.Max(24, o.MaxTokens),
                CacheHistory = false,
                Purpose = QuickReplyPurpose,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "quick reply call failed; the brain answers");
            return new();
        }
        decimal cost = _usage.Record(response.Model, response.Usage, ModelTier.Cheap);
        var (needsBrain, say) = QuickReply.Parse(response.Text);
        if (needsBrain)
        {
            var ack = QuickReply.LintAck(say, o.AckMaxWords, o.AckMaxChars);
            _log.LogInformation("quick reply: cheap tier says the brain is needed ({Kind}); ack {Ack}", kind, ack ?? "(none)");
            return new(Ack: ack, Calls: 1, Usage: response.Usage, Cost: cost);
        }
        var final = QuickReply.LintFinal(say, context, o.CheapMaxChars);
        if (final == null)
        {
            _log.LogInformation("[quick reply rejected by lint] {Text}; the brain answers", TextUtil.OneLine(response.Text));
            return new(Calls: 1, Usage: response.Usage, Cost: cost);
        }

        string? whisperTo = batch.WhisperOnlyFrom;
        var said = new List<string>();
        Latency?.BeginTurn(true);
        Latency?.NoteKind("cheap:" + kind.ToString().ToLowerInvariant());
        try
        {
            string? text = final;
            if (whisperTo == null && SpeechGate != null)
            {
                try { text = SpeechGate(batch, final, false); } catch (Exception ex) { _log.LogWarning(ex, "speech gate failed"); }
                if (text == null || IsSilent(text.Trim())) { try { Muted?.Invoke(final); } catch (Exception) { } text = ""; }
            }
            if (!IsSilent(text))
            {
                text = MetaSpeech.Strip(text, out _, MetaSpeech.AsksStatus(batch.Waking.Where(EventQueue.IsTyped).Select(e => e.Text)), AddresseeNames(players));
                if (!IsSilent(text.Trim())) text = LastLook(text, players);
            }
            if (!IsSilent(text))
            {
                var lines = await _chat.SayAsync(text, whisperTo, players, ct).ConfigureAwait(false);
                said.AddRange(lines);
                if (lines.Count > 0) Latency?.NoteChatLine(_time.GetUtcNow());
                LogSaid(lines, whisperTo, batch.IsTest);
            }
            Latency?.NoteCheapOnly();
        }
        finally { Latency?.EndTurn(workPending: false); }
        _log.LogInformation("quick reply (cheap tier, no brain call) to {Players}: {Text}", string.Join(",", players), string.Join(" ", said));

        // History + episodes: the brain sees this exchange next turn (a small appended tail, not a rewrite of the cached prefix).
        var episode = new Episode(ModelTier.Cheap, now) { IsTest = batch.IsTest };
        episode.Events.AddRange(batch.Events);
        episode.Messages.Add(LlmMessage.User(QuickReplyTag + "\n" + events));
        episode.Messages.Add(LlmMessage.Assistant(said.Count > 0 ? string.Join(" ", said) : "(silent)"));
        lock (_handled)
        {
            _handled.Clear();
            foreach (var e in episode.Events.Where(e => e.Wakes)) _handled.Add(HandledKey(e));
        }
        _context.Commit(episode);
        if (said.Count > 0) _lastCheap = (players[0], _time.GetUtcNow(), string.Join(" ", said));
        return new(Final: new RunResult(ModelTier.Cheap, 1, response.Usage, cost, said, null));
    }
}
