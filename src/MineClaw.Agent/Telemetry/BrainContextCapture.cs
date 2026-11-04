using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Llm;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// The brain's context capture, for the Explorer's Controller panel: a bounded ring of recent turns, each holding the exact
/// <see cref="LlmRequest"/> and <see cref="LlmResponse"/> objects of its calls, recorded where telemetry already sees every call
/// (<see cref="TelemetryLlmClient"/>: the request as built, the response as parsed). Capturing keeps references only — a copy of the
/// request's message list (pointers; the messages are immutable records) and the response — so a brain nobody watches pays a lock
/// and a few allocations per call (measured in the tests: ~1 µs). Everything a viewer sees (redaction, sections, token estimates,
/// JSON) is built when a viewer asks, and the window rendering is cached per call.
/// <para>
/// Turns: calls are grouped by lane (brain / reply / compaction / worker / author / memory / perception / background) and by the
/// first text block of the request's newest plain user message (a brain episode's turn text, a worker's task): every tool round of
/// one turn shares that block object. Sub-entries (worker, author, memory...) link to the brain turn whose ambient
/// <see cref="TurnContext"/> they inherited, else the brain turn running when they began.
/// </para>
/// Secrets never leave: every text passes <see cref="ShardRedactor.Redact"/> plus the configured key values; API keys and headers
/// are not part of an <see cref="LlmRequest"/> at all.
/// </summary>
public sealed class BrainContextCapture
{
    private readonly object _lock = new();
    private readonly BrainCaptureOptions _o;
    private readonly TimeProvider _time;
    private readonly List<CapturedTurn> _brain = new(), _sub = new(), _bg = new();
    private readonly ConditionalWeakTable<TurnInfo, StrongBox<long>> _turnOfInfo = new();
    private readonly List<(long Id, long Version)> _evicted = new();
    private readonly List<string> _secrets = new();
    private long _seq, _turns, _version, _evictFloor;
    private CapturedCall? _lastBrainCall;
    private WindowCache? _windowCache;

    private sealed record WindowCache(long Seq, long Version, string? Filter, CtxWindow Window);

    public BrainContextCapture(BrainCaptureOptions? options = null, TimeProvider? time = null)
    {
        _o = options ?? new BrainCaptureOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The brain id the routes use ("main" or a hive clone's account).</summary>
    public string Brain { get; set; } = "main";
    public bool Enabled => _o.Enabled;
    public int Capacity => Math.Max(1, _o.Turns);
    /// <summary>The model a request runs on when it names none (the tier's; set by <see cref="PromptTelemetry"/>).</summary>
    public Func<LlmRequest, string>? ModelOf { get; set; }
    /// <summary>The brain's context limit and compaction trigger (the token bar).</summary>
    public long ContextLimit { get; set; } = 100_000;
    public long CompactAt { get; set; } = 30_000;
    /// <summary>Renders done so far (tests: capturing alone never renders).</summary>
    public long Renders => Interlocked.Read(ref _renders);
    private long _renders;

    /// <summary>Literal secret values (API keys, the supervisor token) replaced in everything a viewer gets.</summary>
    public void AddSecrets(IEnumerable<string?> values)
    {
        lock (_lock)
            foreach (var v in values)
                if (v is { Length: >= 6 } s && !_secrets.Contains(s)) _secrets.Add(s);
    }

    /// <summary>The redaction every outgoing text passes: token-shaped strings (<see cref="ShardRedactor"/>) and known key values.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string[] secrets;
        lock (_lock) secrets = _secrets.ToArray();
        foreach (var s in secrets) if (text.Contains(s, StringComparison.Ordinal)) text = text.Replace(s, "[redacted]", StringComparison.Ordinal);
        return ShardRedactor.Redact(text, out _);
    }

    // ================================================================ capture (the hot path: references only)

    /// <summary>Called as the request leaves (on the calling flow). Null when off. Never throws.</summary>
    public CapturedCall? Begin(LlmRequest request, string? body)
    {
        if (!_o.Enabled) return null;
        try
        {
            var purpose = PromptPurpose.Normalize(request.Purpose);
            var lane = LaneOf(purpose, request.Tier);
            var messages = request.Messages.ToArray(); // pointer copy: a caller may keep appending to its list
            int start = TurnStart(messages);
            var key = start >= 0 ? KeyBlock(messages[start]) : null;
            var info = TurnContext.Current;
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                var call = new CapturedCall(++_seq, now, request, messages, request.System.ToArray(), lane, purpose, body, start);
                var ring = RingOf(lane);
                var turn = (key != null ? RecentTurnLocked(ring, lane, key) : null) ?? NewTurnLocked(ring, lane, purpose, key, now, info);
                AddCallLocked(turn, call, body);
                if (lane == Lanes.Brain)
                {
                    _lastBrainCall = call;
                    if (info != null) _turnOfInfo.AddOrUpdate(info, new StrongBox<long>(turn.Id));
                }
                call.Version = turn.Version = ++_version;
                return call;
            }
        }
        catch (Exception) { return null; } // the capture never breaks a call
    }

    /// <summary>The turn among the lane's 8 newest whose key block is this very object (another tool round of it); null = new turn.</summary>
    private static CapturedTurn? RecentTurnLocked(List<CapturedTurn> ring, string lane, LlmBlock key)
    {
        for (int i = ring.Count - 1, n = 0; i >= 0 && n < 8; i--, n++)
            if (ring[i].Lane == lane && ReferenceEquals(ring[i].Key, key)) return ring[i];
        return null;
    }

    /// <summary>Opens a turn (a sub-entry links to its brain turn) and evicts the ring's oldest past its capacity.</summary>
    private CapturedTurn NewTurnLocked(List<CapturedTurn> ring, string lane, string purpose, LlmBlock? key, DateTimeOffset now, TurnInfo? info)
    {
        long? parent = lane != Lanes.Brain ? ParentLocked(info) : null;
        var turn = new CapturedTurn(++_turns, lane, purpose, key, now, parent);
        ring.Add(turn);
        int cap = lane == Lanes.Background ? Math.Max(1, _o.BackgroundTurns) : Capacity;
        while (ring.Count > cap)
        {
            _evicted.Add((ring[0].Id, _version + 1));
            ring.RemoveAt(0);
        }
        while (_evicted.Count > 256) { _evictFloor = _evicted[0].Version; _evicted.RemoveAt(0); }
        return turn;
    }

    /// <summary>The brain turn whose ambient turn info a sub-entry inherited, else the brain turn running now.</summary>
    private long? ParentLocked(TurnInfo? info) =>
        info != null && _turnOfInfo.TryGetValue(info, out var box) ? box.Value : _lastBrainCall?.Turn.Id;

    private void AddCallLocked(CapturedTurn turn, CapturedCall call, string? body)
    {
        call.Turn = turn;
        call.Round = turn.Calls.Count + turn.Dropped;
        turn.Calls.Add(call);
        if (turn.Calls.Count > Math.Max(2, _o.CallsPerTurn)) { turn.Calls.RemoveAt(1); turn.Dropped++; } // keep the first call (the turn's input)
        if (body != null) turn.Body ??= body;
    }

    /// <summary>Called once the call returned (response), failed (error) or was cancelled. Never throws.</summary>
    public void End(CapturedCall? call, LlmResponse? response, Exception? error, PromptUsageRecord? record)
    {
        if (call == null) return;
        try
        {
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                call.Response = response;
                call.Error = error;
                call.Record = record;
                call.EndedAt = now;
                call.Turn.End = now;
                call.Version = call.Turn.Version = ++_version;
            }
        }
        catch (Exception) { }
    }

    // ================================================================ queries (built on demand)

    /// <summary>The page or turn a query asks for (the in-process routes and the clones' <c>brain_ctx</c> IPC answer).</summary>
    public BrainContextReply Query(BrainContextQuery q) =>
        q.Turn is { } n ? new BrainContextReply(null, Turn(n, q.Body)) : new BrainContextReply(Page(q.Since, q.Body), null);

    /// <summary>Turn summaries changed after <paramref name="since"/> (a version), the live window when it changed, evicted ids.</summary>
    /// <param name="body">The subhive filter: mark the turns and tool calls that targeted this body.</param>
    public BrainContextPage Page(long since, string? body = null)
    {
        body = BodyFilter(body);
        List<CapturedTurn> turns;
        List<long> evicted;
        long version;
        CapturedCall? last;
        CtxInFlight? inFlight;
        bool full;
        lock (_lock)
        {
            version = _version;
            full = since <= 0 || since < _evictFloor || since > version;
            turns = _brain.Concat(_sub).Concat(_bg).Where(t => full || t.Version > since).OrderBy(t => t.Id).ToList();
            evicted = full ? [] : _evicted.Where(e => e.Version > since).Select(e => e.Id).ToList();
            last = _lastBrainCall != null && _brain.Contains(_lastBrainCall.Turn) ? _lastBrainCall : null;
            inFlight = InFlightLocked();
        }
        var summaries = turns.Select(t => Summary(t, body)).ToList();
        CtxWindow? window = null;
        if (last != null && (full || last.Version > since)) window = Window(last, body);
        return new BrainContextPage(Brain, version, _time.GetUtcNow(), Capacity, _o.Enabled, inFlight, summaries, window, evicted, body,
            PageNote(full, since));
    }

    private static string? BodyFilter(string? body) => string.IsNullOrWhiteSpace(body) ? null : body.Trim();

    private string? PageNote(bool full, long since) =>
        !_o.Enabled ? "capture off (Telemetry:Capture:Enabled)" : full && since > 0 ? "full" : null;

    /// <summary>One turn in full; null = not (or no longer) kept.</summary>
    public BrainTurnDetail? Turn(long id, string? body = null)
    {
        body = BodyFilter(body);
        CapturedTurn? turn;
        List<CapturedTurn> children;
        lock (_lock)
        {
            turn = _brain.Concat(_sub).Concat(_bg).FirstOrDefault(t => t.Id == id);
            if (turn == null) return null;
            children = _sub.Concat(_bg).Where(t => t.Parent == id).OrderBy(t => t.Id).ToList();
        }
        Interlocked.Increment(ref _renders);
        CapturedCall[] calls;
        lock (_lock) calls = turn.Calls.ToArray();
        var first = calls[0];
        var list = new List<CtxCall>();
        CapturedCall? prev = null;
        foreach (var c in calls)
        {
            // What this call added: the first call from the turn's input on; later calls what came after the previous call's
            // messages (the previous response is the assistant message, then the tool results).
            int from = prev == null ? Math.Max(0, c.TurnStart) : Math.Min(c.Messages.Length, prev.Messages.Length + 1);
            if (prev != null && c.TurnStart > prev.TurnStart) from = Math.Max(0, c.TurnStart); // a restarted turn: new input
            var added = new List<CtxMessage>();
            for (int i = from; i < c.Messages.Length; i++) added.Add(Message(c.Messages[i], i, c.TurnStart, body));
            list.Add(Call(c, from, added, body));
            prev = c;
        }
        if (body != null) MarkResults(list.SelectMany(c => c.Added.Append(new CtxMessage(-1, "assistant", c.Response))).ToList());
        return new BrainTurnDetail(Brain, Summary(turn, body), SystemBlocks(first, ScaleOf(first)), ToolNames(first.Request), list,
            children.Select(t => Summary(t, body)).ToList(), body);
    }

    /// <summary>The newest brain call (tests; the window's source).</summary>
    public CapturedCall? LastBrainCall { get { lock (_lock) return _lastBrainCall; } }

    /// <summary>Every kept call, oldest first (tests).</summary>
    public IReadOnlyList<CapturedCall> Calls()
    {
        lock (_lock) return _brain.Concat(_sub).Concat(_bg).SelectMany(t => t.Calls).OrderBy(c => c.Seq).ToList();
    }

    public int TurnCount(string? lane = null)
    {
        lock (_lock) return lane == null ? _brain.Count + _sub.Count + _bg.Count : RingOf(lane).Count(t => t.Lane == lane);
    }

    private CtxInFlight? InFlightLocked()
    {
        // The brain's own call first (the "now thinking…" line), else the newest sub-entry call in flight.
        CapturedCall? best = null;
        foreach (var t in _brain.Concat(_sub))
            foreach (var c in t.Calls)
            {
                if (c.EndedAt != null) continue;
                bool brain = c.Lane == Lanes.Brain, bestBrain = best?.Lane == Lanes.Brain;
                if (best == null || (brain && !bestBrain) || (brain == bestBrain && c.Seq > best.Seq)) best = c;
            }
        return best == null ? null : new CtxInFlight(best.Seq, best.Turn.Id, best.Lane, best.Purpose, ModelName(best), best.At, best.Body);
    }

    // ================================================================ rendering

    private CtxTurnSummary Summary(CapturedTurn t, string? body)
    {
        CapturedCall[] calls;
        DateTimeOffset? end;
        long version;
        lock (_lock) { calls = t.Calls.ToArray(); end = t.End; version = t.Version; }
        var sum = new TurnTotals();
        foreach (var c in calls)
        {
            sum.Add(c);
            sum.AddError(c.Error, Redact);
            if (body != null && !sum.Targets) sum.Targets = CallTargets(c, body);
        }
        var last = calls[^1];
        return new CtxTurnSummary(t.Id, t.Lane, t.Purpose, t.Start, sum.InFlight ? null : end, sum.InFlight, calls.Length + t.Dropped, ModelName(last),
            TierName(last), last.Record?.Provider ?? last.Response?.Provider, sum.In, sum.Out, sum.CacheRead, sum.CacheWrite, Math.Round(sum.Cost, 6),
            sum.Latency, Preview(calls[0]), sum.Tools, sum.Thinking, t.Parent, t.Body, sum.Targets, version, sum.Error, t.Dropped);
    }

    /// <summary>A turn summary's sums over its calls: tokens, cost, latency, tools used, thinking, in flight, the last error.</summary>
    private sealed class TurnTotals
    {
        public long In, Out, CacheRead, CacheWrite, Latency;
        public decimal Cost;
        public readonly List<string> Tools = new();
        public bool Thinking, InFlight, Targets;
        public string? Error;

        public void Add(CapturedCall c)
        {
            if (c.EndedAt == null) InFlight = true;
            var u = c.Usage();
            if (u != null) { In += u.InputTokens; Out += u.OutputTokens; CacheRead += u.CacheReadTokens; CacheWrite += u.CacheWriteTokens; }
            Cost += c.Record?.CostUsd ?? 0;
            Latency += c.LatencyMs();
            if (c.Response is { } r) AddResponse(r);
        }

        private void AddResponse(LlmResponse r)
        {
            foreach (var tu in r.ToolUses) if (!Tools.Contains(tu.Name)) Tools.Add(tu.Name);
            Thinking |= r.Content.Any(b => b is LlmThinking or LlmRedactedThinking) || !string.IsNullOrEmpty(r.Reasoning);
        }

        /// <summary>A failure names itself (redacted); a cancel is "interrupted" unless a failure was already seen.</summary>
        public void AddError(Exception? error, Func<string?, string> redact)
        {
            if (error is { } e && e is not OperationCanceledException) Error = e.GetType().Name + ": " + redact(e.Message);
            else if (error is OperationCanceledException && Error == null) Error = "interrupted";
        }
    }

    private CtxWindow Window(CapturedCall c, string? filter)
    {
        long v;
        lock (_lock) v = c.Version;
        if (CachedWindow(c.Seq, v, filter) is { } cached) return cached;
        return RenderWindow(c, v, filter);
    }

    private CtxWindow? CachedWindow(long seq, long version, string? filter) =>
        Volatile.Read(ref _windowCache) is { } wc && wc.Seq == seq && wc.Version == version && wc.Filter == filter ? wc.Window : null;

    private CtxWindow RenderWindow(CapturedCall c, long v, string? filter)
    {
        Interlocked.Increment(ref _renders);
        double scale = ScaleOf(c);
        var system = SystemBlocks(c, scale);
        var messages = new List<CtxMessage>(c.Messages.Length + 1);
        for (int i = 0; i < c.Messages.Length; i++) messages.Add(Message(c.Messages[i], i, c.TurnStart, filter));
        if (c.Response is { } r) messages.Add(new CtxMessage(c.Messages.Length, "assistant", Blocks(r.Content, r.Reasoning, filter, false), "response"));
        if (filter != null) MarkResults(messages);
        int toolTokens = (int)Math.Round(ToolChars(c.Request) / 3.5 * scale);
        long cached = toolTokens + system.Where(s => s.InCachedPrefix).Sum(s => (long)s.Tokens);
        if (!system.Any(s => s.InCachedPrefix)) cached = 0;
        var w = new CtxWindow(c.Seq, c.Turn.Id, c.At, ModelName(c), TierName(c), system, c.Request.Tools.Count, toolTokens, ToolNames(c.Request),
            messages, c.Record?.PromptTokens ?? 0, (long)Math.Round(EstimateChars(c) / 3.5), c.Record?.ContextLimit is > 0 and var lim ? lim : ContextLimit,
            CompactAt, cached, UsageOf(c), c.EndedAt == null);
        Volatile.Write(ref _windowCache, new WindowCache(c.Seq, v, filter, w));
        return w;
    }

    private CtxCall Call(CapturedCall c, int contextCount, List<CtxMessage> added, string? body) =>
        new(c.Seq, c.At, c.Purpose, TierName(c), ModelName(c), c.Record?.Provider ?? c.Response?.Provider, c.Body, c.Round, c.EndedAt == null,
            c.Response?.StopReason ?? (c.Error is OperationCanceledException ? "interrupted" : c.Error != null ? "error" : null), UsageOf(c),
            c.Error is { } e ? e.GetType().Name + ": " + Redact(e.Message) : null, contextCount, added,
            c.Response is { } r ? Blocks(r.Content, r.Reasoning, body, false) : [], c.Request.MaxTokens, c.Request.Effort, c.Request.Thinking);

    private List<CtxSystemBlock> SystemBlocks(CapturedCall c, double scale)
    {
        int lastCache = -1;
        for (int i = 0; i < c.System.Length; i++) if (c.System[i].Cache) lastCache = i;
        var list = new List<CtxSystemBlock>(c.System.Length);
        for (int i = 0; i < c.System.Length; i++)
        {
            var s = c.System[i];
            list.Add(new CtxSystemBlock(i, Redact(s.Text), s.Cache, s.Ttl, (int)Math.Round(s.Text.Length / 3.5 * scale), i <= lastCache));
        }
        return list;
    }

    private CtxMessage Message(LlmMessage m, int index, int turnStart, string? filter)
    {
        bool turn = index == turnStart && m.Role == LlmRole.User;
        // every plain user message is some turn's input (this one's or an earlier one's in history): its [tag] sections are labelled
        bool input = m.Role == LlmRole.User && !m.Content.Any(b => b is LlmToolResult);
        var blocks = Blocks(m.Content, null, filter, input);
        string? tag = blocks.Any(b => b.Label == "journal") ? "journal"
            : turn ? "turn"
            : m.Content.Any(b => b is LlmToolResult) ? "tool_results" : null;
        return new CtxMessage(index, m.Role == LlmRole.User ? "user" : "assistant", blocks, tag);
    }

    private List<CtxBlock> Blocks(IReadOnlyList<LlmBlock> content, string? reasoning, string? filter, bool turnInput)
    {
        var list = new List<CtxBlock>(content.Count + 1);
        if (!string.IsNullOrEmpty(reasoning)) list.Add(new CtxBlock("reasoning", Redact(reasoning), ReasoningLabel));
        foreach (var b in content) AddBlock(list, b, filter, turnInput);
        return list;
    }

    private void AddBlock(List<CtxBlock> list, LlmBlock b, string? filter, bool turnInput)
    {
        switch (b)
        {
            case LlmText t:
                AddText(list, t.Text, turnInput);
                break;
            case LlmToolUse u:
                list.Add(new CtxBlock("tool_use", Name: u.Name, Id: u.Id, Input: Redact(Raw(u.Input)), Target: filter != null && UseTargets(u, filter)));
                break;
            case LlmToolResult r:
                list.Add(new CtxBlock("tool_result", Redact(r.Content), r.Image is { } img ? $"+ image {img.Width}x{img.Height} (~{img.Tokens} tok)" : null,
                    Id: r.ToolUseId, IsError: r.IsError, Bytes: r.Image?.Data.Length));
                break;
            case LlmImageBlock ib:
                list.Add(new CtxBlock("image", Label: $"image {ib.Image.Width}x{ib.Image.Height} (~{ib.Image.Tokens} tok)", Bytes: ib.Image.Data.Length));
                break;
            case LlmThinking th:
                list.Add(new CtxBlock("thinking", Redact(th.Thinking), th.Thinking.Length == 0 ? ThinkingOmittedLabel : ThinkingLabel,
                    Bytes: th.Signature.Length));
                break;
            case LlmRedactedThinking rt:
                list.Add(new CtxBlock("redacted_thinking", Label: RedactedThinkingLabel, Bytes: rt.Data.Length));
                break;
        }
    }

    /// <summary>A text block: the journal (its marker cut), a turn input's [tag] sections, or the plain text.</summary>
    private void AddText(List<CtxBlock> list, string text, bool turnInput)
    {
        if (text.StartsWith("[journal]", StringComparison.Ordinal))
            list.Add(new CtxBlock("text", Redact(text["[journal]".Length..].TrimStart('\n')), "journal"));
        else if (turnInput)
            foreach (var (label, section) in Sections(text)) list.Add(new CtxBlock("text", Redact(section), label));
        else
            list.Add(new CtxBlock("text", Redact(text)));
    }

    /// <summary>Tool results inherit their call's target flag (the viewer highlights the pair). Blocks are records: replaced in place.</summary>
    private static void MarkResults(List<CtxMessage> messages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in messages) foreach (var b in m.Blocks) if (b is { Kind: "tool_use", Target: true, Id: { } id }) ids.Add(id);
        if (ids.Count == 0) return;
        foreach (var m in messages)
            for (int i = 0; i < m.Blocks.Count; i++)
                if (m.Blocks[i] is { Kind: "tool_result", Target: false, Id: { } rid } rb && ids.Contains(rid)) m.Blocks[i] = rb with { Target = true };
    }

    public const string ThinkingLabel = "Claude thinking (as the API returned it: a summary on Claude 4+ models)";
    public const string ThinkingOmittedLabel = "Claude thinking (omitted by the API: signature only)";
    public const string RedactedThinkingLabel = "redacted thinking (encrypted by the API, not readable)";
    public const string ReasoningLabel = "model reasoning (e.g. DeepSeek through Ollama: its reasoning content)";

    private static readonly Regex SectionHead = new(@"^\[(?<tag>[a-z][a-z0-9 _\-]{0,30})\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>A turn's input split at line-leading "[tag]" markers: ("state", ...), ("memory", ...) — the per-turn data the viewer labels.</summary>
    public static IEnumerable<(string? Label, string Text)> Sections(string text)
    {
        var heads = SectionHead.Matches(text);
        // The untagged head before the [tag] blocks of a brain turn's input is the state header (pos, hp, food, held, task).
        if (heads.Count == 0) { yield return (null, text); yield break; }
        if (heads[0].Index > 0 && text[..heads[0].Index].Trim().Length > 0) yield return ("state", text[..heads[0].Index].TrimEnd('\n'));
        for (int i = 0; i < heads.Count; i++)
        {
            int end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
            yield return (heads[i].Groups["tag"].Value.ToLowerInvariant(), text[heads[i].Index..end].TrimEnd('\n'));
        }
    }

    // ================================================================ the subhive filter

    /// <summary>A call worked for the body (its body scope) or one of its tool calls named it (<c>body</c>, or bodies/hive <c>name</c>).</summary>
    public static bool CallTargets(CapturedCall c, string body) =>
        SameBody(c.Body, body) || (c.Response?.ToolUses.Any(u => UseTargets(u, body)) ?? false);

    /// <summary>A tool call names the body: a <c>body</c> argument anywhere in its input ("67", ["67","69"], "all"), or the
    /// <c>name</c> of a bodies/hive call.</summary>
    public static bool UseTargets(LlmToolUse u, string body)
    {
        try
        {
            if (u.Input.ValueKind != JsonValueKind.Object) return false;
            if (u.Name is "bodies" or "hive" && u.Input.TryGetProperty("name", out var n) && Names(n).Any(x => SameBody(x, body) || x == "all")) return true;
            return BodyArgs(u.Input).Any(x => SameBody(x, body) || x == "all");
        }
        catch (Exception) { return false; }
    }

    private static IEnumerable<string> BodyArgs(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "body") foreach (var s in Names(p.Value)) yield return s;
                else foreach (var s in BodyArgs(p.Value)) yield return s;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) foreach (var s in BodyArgs(x)) yield return s;
    }

    private static IEnumerable<string> Names(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) { if (e.GetString() is { } s) yield return s.Trim().ToLowerInvariant(); }
        else if (e.ValueKind == JsonValueKind.Number) yield return e.GetRawText();
        else if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) foreach (var s in Names(x)) yield return s;
    }

    /// <summary>"67" = "maid_chan_67" = "MAID_CHAN_67".</summary>
    public static bool SameBody(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        a = a.Trim(); b = b.Trim();
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return a.EndsWith("_" + b, StringComparison.OrdinalIgnoreCase) || b.EndsWith("_" + a, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ helpers

    private List<CapturedTurn> RingOf(string lane) => lane == Lanes.Brain ? _brain : lane == Lanes.Background ? _bg : _sub;

    public static class Lanes
    {
        public const string Brain = "brain", Reply = "reply", Compaction = "compaction", Worker = "worker", Author = "author", Memory = "memory",
            Perception = "perception", Background = "background";
    }

    /// <summary>Which lane a call belongs to (normalized purpose).</summary>
    public static string LaneOf(string purpose, ModelTier tier) =>
        LaneOfPurpose.TryGetValue(purpose, out var lane) ? lane
        : purpose.StartsWith("author", StringComparison.Ordinal) ? Lanes.Author
        : tier == ModelTier.Worker ? Lanes.Worker
        : Lanes.Background;

    /// <summary>The purposes that name their lane outright (the rest: an author purpose, else the worker tier, else background).</summary>
    private static readonly Dictionary<string, string> LaneOfPurpose = new(StringComparer.Ordinal)
    {
        [PromptPurpose.ChatTurn] = Lanes.Brain,
        ["quick_ack"] = Lanes.Reply, ["quick_reply"] = Lanes.Reply,
        [PromptPurpose.Compaction] = Lanes.Compaction,
        [PromptPurpose.DelegateRound] = Lanes.Worker,
        [PromptPurpose.AskMemory] = Lanes.Memory,
        ["perception"] = Lanes.Perception,
        ["patch"] = Lanes.Author, ["blueprint"] = Lanes.Author,
    };

    /// <summary>The newest user message with text and no tool results: the turn's input (-1 = none).</summary>
    internal static int TurnStart(LlmMessage[] messages)
    {
        for (int i = messages.Length - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (m.Role != LlmRole.User) continue;
            bool text = false, results = false;
            foreach (var b in m.Content)
            {
                if (b is LlmToolResult) { results = true; break; }
                if (b is LlmText) text = true;
            }
            if (text && !results) return i;
        }
        return -1;
    }

    /// <summary>The block object every round of a turn shares: the first text block that isn't the (rebuilt) journal.</summary>
    private static LlmBlock? KeyBlock(LlmMessage m)
    {
        foreach (var b in m.Content)
            if (b is LlmText t && !t.Text.StartsWith("[journal]", StringComparison.Ordinal)) return t;
        return m.Content.Count > 0 ? m.Content[0] : null;
    }

    private string ModelName(CapturedCall c) =>
        c.Record?.Model ?? c.Response?.Model ?? c.Request.Model ?? SafeModel(c.Request) ?? "?";

    private string? SafeModel(LlmRequest r)
    {
        try { return ModelOf?.Invoke(r); } catch (Exception) { return null; }
    }

    private static string TierName(CapturedCall c) => c.Record?.Tier is { Length: > 0 } t ? t : c.Request.Tier switch
    {
        ModelTier.Main => "opus", ModelTier.Worker => "worker", _ => "memory",
    };

    private static CtxUsage? UsageOf(CapturedCall c)
    {
        var u = c.Usage();
        if (u == null) return null;
        return new CtxUsage(u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CacheWrite1hTokens, c.Record?.CostUsd ?? 0,
            c.LatencyMs(), u.InputTokens + u.CacheReadTokens + u.CacheWriteTokens);
    }

    private string Preview(CapturedCall first)
    {
        if (first.TurnStart >= 0 && first.TurnStart < first.Messages.Length)
        {
            var text = string.Join(" ", first.Messages[first.TurnStart].Content.OfType<LlmText>()
                .Where(t => !t.Text.StartsWith("[journal]", StringComparison.Ordinal)).Select(t => t.Text));
            // The events are what woke the turn: prefer the part after the per-turn data blocks when there is one.
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var ev = lines.LastOrDefault(l => !l.StartsWith('[')) ?? lines.LastOrDefault() ?? "";
            return Redact(ev.Length > 160 ? ev[..157] + "..." : ev);
        }
        return first.Purpose;
    }

    private static List<string> ToolNames(LlmRequest r) => r.Tools.Select(t => t.Name).ToList();

    private static long ToolChars(LlmRequest r)
    {
        long n = 0;
        foreach (var t in r.Tools) n += t.Name.Length + t.Description.Length + t.InputSchema.GetRawText().Length;
        return n;
    }

    private static long EstimateChars(CapturedCall c)
    {
        long n = ToolChars(c.Request);
        foreach (var s in c.System) n += s.Text.Length;
        foreach (var m in c.Messages)
            foreach (var b in m.Content)
                n += b switch
                {
                    LlmText t => t.Text.Length,
                    LlmToolUse u => u.Name.Length + u.Input.GetRawText().Length,
                    LlmToolResult r => r.Content.Length + (r.Image?.Tokens ?? 0) * 7 / 2,
                    LlmThinking th => th.Thinking.Length,
                    LlmImageBlock ib => ib.Image.Tokens * 7 / 2,
                    _ => 0,
                };
        return n;
    }

    /// <summary>Real prompt tokens ÷ the char estimate (1 while unknown), so block estimates add up to what the API counted.</summary>
    private static double ScaleOf(CapturedCall c)
    {
        long prompt = c.Record?.PromptTokens ?? 0;
        if (prompt <= 0) return 1;
        double est = EstimateChars(c) / 3.5;
        return est <= 0 ? 1 : Math.Clamp(prompt / est, 0.25, 4);
    }

    private static string Raw(JsonElement e)
    {
        try { return e.ValueKind == JsonValueKind.Undefined ? "{}" : e.GetRawText(); }
        catch (Exception) { return "{}"; }
    }
}

/// <summary>One captured call: the exact request (and its message list as sent) and the response, by reference.</summary>
public sealed class CapturedCall
{
    internal CapturedCall(long seq, DateTimeOffset at, LlmRequest request, LlmMessage[] messages, LlmSystemBlock[] system, string lane, string purpose,
        string? body, int turnStart)
    {
        Seq = seq;
        At = at;
        Request = request;
        Messages = messages;
        System = system;
        Lane = lane;
        Purpose = purpose;
        Body = body;
        TurnStart = turnStart;
        _sw = Stopwatch.StartNew();
    }

    private readonly Stopwatch _sw;
    public long Seq { get; }
    public DateTimeOffset At { get; }
    /// <summary>The request object exactly as the caller built it.</summary>
    public LlmRequest Request { get; }
    /// <summary>The request's messages as they were when it left (the caller's list may grow afterwards).</summary>
    public LlmMessage[] Messages { get; }
    public LlmSystemBlock[] System { get; }
    public string Lane { get; }
    public string Purpose { get; }
    public string? Body { get; }
    /// <summary>Index of the turn's input message in <see cref="Messages"/> (-1 = none).</summary>
    public int TurnStart { get; }
    public int Round { get; internal set; }
    /// <summary>The response object exactly as the client parsed it (null while in flight, or on error/cancel).</summary>
    public LlmResponse? Response { get; internal set; }
    public Exception? Error { get; internal set; }
    public PromptUsageRecord? Record { get; internal set; }
    public DateTimeOffset? EndedAt { get; internal set; }
    internal long Version { get; set; }
    internal CapturedTurn Turn { get; set; } = null!;
    private long _latency = -1;

    internal LlmUsage? Usage() => Response?.Usage ?? (Error as LlmInterruptedException)?.PartialUsage ?? (Error as LlmCostCapException)?.PartialUsage;

    internal long LatencyMs()
    {
        if (Record is { } r) return r.LatencyMs;
        if (EndedAt != null) { if (_latency < 0) _latency = _sw.ElapsedMilliseconds; return _latency; }
        return _sw.ElapsedMilliseconds;
    }
}

internal sealed class CapturedTurn(long id, string lane, string purpose, LlmBlock? key, DateTimeOffset start, long? parent)
{
    public long Id { get; } = id;
    public string Lane { get; } = lane;
    public string Purpose { get; } = purpose;
    public LlmBlock? Key { get; } = key;
    public DateTimeOffset Start { get; } = start;
    public long? Parent { get; } = parent;
    public DateTimeOffset? End { get; set; }
    public string? Body { get; set; }
    public List<CapturedCall> Calls { get; } = new();
    public int Dropped { get; set; }
    public long Version { get; set; }
}
