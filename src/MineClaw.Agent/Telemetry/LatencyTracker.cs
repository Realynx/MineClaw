using System.Text.Json;
using System.Text.Json.Serialization;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Telemetry;

/// <summary>
/// One chat-triggered reaction, measured from the player's first line (as queued) to what the bot visibly did:
/// <see cref="ReactionMs"/> = first visible reaction (head turn, reflex, movement, game action or a chat line);
/// <see cref="FirstMoveMs"/> = first movement (speculative walks included); <see cref="FirstActionMs"/> = first game action
/// (body tool by brain/worker/script/reflex, or an adopted speculative walk; never a cancelled speculation);
/// <see cref="RoundsBeforeAction"/> = model calls (brain + worker) that completed before that action.
/// </summary>
public sealed record LatencyRecord
{
    public DateTimeOffset Ts { get; init; }
    public string Bot { get; init; } = "";
    public string? Player { get; init; }
    public string Text { get; init; } = "";
    /// <summary>"reflex:stop", "spec:gen", "simple", "plan", "chat".</summary>
    public string Kind { get; init; } = "chat";
    public long? ReactionMs { get; init; }
    public long? FirstMoveMs { get; init; }
    public long? FirstActionMs { get; init; }
    public int? RoundsBeforeAction { get; init; }
    public int Rounds { get; init; }
    public IReadOnlyList<string> Tools { get; init; } = [];
    /// <summary>"adopted" | "cancelled" | null (no speculative step).</summary>
    public string? Speculation { get; init; }
    public string? Effort { get; init; }
    /// <summary>First chat line the player saw: the quick ack, a streamed brain line or a cheap-only reply.</summary>
    public long? FirstChatMs { get; init; }
    /// <summary>The turn's final chat line (brain or cheap-only answer; an ack alone never counts).</summary>
    public long? ConclusiveMs { get; init; }
    /// <summary>A quick cheap-tier ack went out before the brain spoke.</summary>
    public bool? Ack { get; init; }
    /// <summary>"cheap" = answered by the cheap tier alone (no brain call); "correction" = a correction of a cheap-only reply.</summary>
    public string? Path { get; init; }
    /// <summary>What kind of line it was: command | question | banter | greeting | help (<see cref="Brainstem.ChatIntent"/>).</summary>
    public string? Category { get; init; }
    /// <summary>What the first visible reaction was: gaze (head turn), gesture, line (chat), move, action.</summary>
    public string? ReactionBy { get; init; }
    /// <summary>The full answer: the later of the conclusive chat line and the first game action (whichever the turn produced).</summary>
    public long? FullMs { get; init; }
    /// <summary>An instant (brainstem/reaction) line went out before any model spoke.</summary>
    public bool? Instant { get; init; }
}

/// <summary>Medians over the recent window (TUI tokens panel).</summary>
public sealed record LatencySummary(int Turns, long? ReactionP50, long? FirstActionP50, long? FirstMoveP50, double? RoundsP50,
    int SpeculationAdopted, int SpeculationCancelled, long? FirstChatP50 = null, long? ConclusiveP50 = null,
    int CheapOnly = 0, int CheapMisses = 0, int Chats = 0, string? ByKind = null)
{
    /// <summary>"react 0.1s · chat 1.9s/6.2s · act 2.9s · move 0.3s · rounds 0 (n 12) · spec 5/1 · cheap 3/12 (1 miss)".</summary>
    public string Line() =>
        $"react {S(ReactionP50)}" + (FirstChatP50 != null || ConclusiveP50 != null ? $" · chat {S(FirstChatP50)}/{S(ConclusiveP50)}" : "") +
        $" · act {S(FirstActionP50)} · move {S(FirstMoveP50)} · rounds {(RoundsP50 is { } r ? r.ToString("0.#") : "-")} (n {Turns})" +
        (SpeculationAdopted + SpeculationCancelled > 0 ? $" · spec {SpeculationAdopted}/{SpeculationAdopted + SpeculationCancelled}" : "") +
        (CheapOnly + CheapMisses > 0 ? $" · cheap {CheapOnly}/{Math.Max(Chats, CheapOnly)}" + (CheapMisses > 0 ? $" ({CheapMisses} miss)" : "") : "");

    /// <summary>Share of cheap-only replies that weren't corrected (null = none yet).</summary>
    public double? CheapAccuracy => CheapOnly > 0 ? Math.Max(0, CheapOnly - CheapMisses) / (double)CheapOnly : null;

    private static string S(long? ms) => ms is { } v ? (v / 1000.0).ToString("0.0") + "s" : "-";
}

/// <summary>
/// Measures chat → reaction / first move / first action for every chat that reaches this bot (zero tokens). A measurement
/// opens on the first typed line, collects marks from the brainstem (head turn, reflex), the activity manager (moves),
/// the tool registry (body tools by anyone) and the brain/worker loops (rounds), and closes after the brain's turn — or,
/// when the turn queued work that hasn't started yet, when that work starts (at most <see cref="AwaitActionAfterTurn"/>).
/// </summary>
public sealed class LatencyTracker
{
    private sealed class Open
    {
        public required DateTimeOffset ChatAt;
        public string? Player;
        public string Text = "";
        public string Kind = "chat";
        public DateTimeOffset? Reaction, Move, Action;
        public int Rounds;
        public int? RoundsAtAction;
        public readonly List<string> Tools = new();
        public string? Speculation;
        public DateTimeOffset? SpecStarted;
        public int SpecRounds;
        public string? Effort;
        public bool InTurn;
        public DateTimeOffset? CloseBy;
        public DateTimeOffset? FirstChat, Conclusive;
        public bool? Ack;
        public string? Path;
        public string? Category;
        public string? ReactionBy;
        public bool? Instant;
    }

    /// <summary>Tools that drive the body (moving, digging, placing, containers, combat, gestures, worker/skill/script work).</summary>
    public static readonly IReadOnlySet<string> BodyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "goto", "follow", "mine", "harvest_generator", "collect_drops", "craft", "smelt", "collect_furnace", "deposit", "withdraw",
        "organize_chest", "place", "dig", "use_block", "eat", "chop_tree", "act", "skill", "run_routine", "attack", "defend",
        "guard", "spar",
    };

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly LinkedList<LatencyRecord> _recent = new();
    private readonly int _keep;
    private Open? _open;
    private int _specAdopted, _specCancelled, _cheapOnly, _cheapMisses, _chats;

    public LatencyTracker(TimeProvider? time = null, int keep = 200)
    {
        _time = time ?? TimeProvider.System;
        _keep = Math.Max(10, keep);
    }

    /// <summary>Bot name stamped on records.</summary>
    public string Bot { get; set; } = "";

    /// <summary>How long after the brain's turn a queued action may still count as this chat's first action.</summary>
    public TimeSpan AwaitActionAfterTurn { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>The bot's own names (stripped before the line is classified); null = none.</summary>
    public Func<IEnumerable<string>>? Names { get; set; }

    /// <summary>Raised with each finished measurement (the jsonl writer, tests).</summary>
    public event Action<LatencyRecord>? Recorded;

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>A typed line addressed to this bot was queued at <paramref name="at"/>. Opens a measurement unless one is open.</summary>
    public void OnChat(string? player, string text, DateTimeOffset at)
    {
        LatencyRecord? flushed = null;
        lock (_lock)
        {
            if (_open is { InTurn: false, CloseBy: not null }) flushed = CloseLocked();
            var line = TextUtil.OneLine(text);
            if (_open != null && line.Length > 0 && _open.Text.Contains(TextUtil.Truncate(line, 40), StringComparison.Ordinal)) { }
            else if (_open == null) { _open = new Open { ChatAt = at, Player = player, Text = TextUtil.Truncate(line, 80), Category = CategoryOf(line) }; _chats++; }
            else if (!_open.InTurn && _open.Text.Length < 80)
            {
                _open.Text = TextUtil.Truncate(_open.Text + " / " + TextUtil.OneLine(text), 80);
                _open.Category = Urgent(_open.Category, CategoryOf(line));
            }
        }
        Emit(flushed);
    }

    /// <summary>A visible reaction that isn't an action (head turn, nod, a chat line).</summary>
    public void NoteReaction(string? by = null) => NoteReaction(Now, by);

    /// <summary>A visible reaction at <paramref name="at"/> (the head turn happens as the line arrives, before it is queued).</summary>
    public void NoteReaction(DateTimeOffset at, string? by = null)
    {
        lock (_lock) if (_open is { } o && (o.Reaction == null || at < o.Reaction)) { o.Reaction = at; o.ReactionBy = by ?? o.ReactionBy; }
    }

    /// <summary>An instant line (brainstem reaction, prefetched wording) went out at <paramref name="at"/>, before any model spoke.</summary>
    public void NoteInstantLine(DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_open is not { } o) return;
            o.Instant = true;
            if (o.FirstChat == null || at < o.FirstChat) o.FirstChat = at;
            if (o.Reaction == null || at < o.Reaction) { o.Reaction = at; o.ReactionBy = "line"; }
        }
    }

    private string CategoryOf(string line)
    {
        IEnumerable<string>? names = null;
        try { names = Names?.Invoke(); } catch (Exception) { }
        return Brainstem.ChatIntent.Classify(line, names).Category;
    }

    private static string? Urgent(string? a, string? b)
    {
        foreach (var c in new[] { Brainstem.ChatIntent.Help, Brainstem.ChatIntent.Command, Brainstem.ChatIntent.Question, Brainstem.ChatIntent.Greeting })
            if (a == c || b == c) return c;
        return a ?? b;
    }

    /// <summary>
    /// A chat line went out at <paramref name="at"/>: the first one sets first_chat; a non-ack line also moves conclusive
    /// (the turn's final chat). <paramref name="ack"/> = the quick cheap-tier acknowledgement.
    /// </summary>
    public void NoteChatLine(DateTimeOffset at, bool ack = false)
    {
        lock (_lock)
        {
            if (_open is not { } o) return;
            if (o.FirstChat == null || at < o.FirstChat) o.FirstChat = at;
            if (ack) o.Ack = true;
            else if (o.Conclusive == null || at > o.Conclusive) o.Conclusive = at;
            if (o.Reaction == null || at < o.Reaction) { o.Reaction = at; o.ReactionBy = "line"; }
        }
    }

    /// <summary>This chat was answered by the cheap tier alone (no brain call).</summary>
    public void NoteCheapOnly()
    {
        lock (_lock)
        {
            _cheapOnly++;
            if (_open is { } o) o.Path = "cheap";
        }
    }

    /// <summary>A player corrected a cheap-only reply right after it ("no, I meant..."): a cheap-path miss.</summary>
    public void NoteCheapMiss()
    {
        lock (_lock)
        {
            _cheapMisses++;
            if (_open is { } o) o.Path = "correction";
        }
    }

    /// <summary>(cheap-only replies, their misses) since start.</summary>
    public (int CheapOnly, int Misses) CheapCounts() { lock (_lock) return (_cheapOnly, _cheapMisses); }

    /// <summary>A game action that isn't a body tool (a reflex stop, a look-at): counts as the first action.</summary>
    public void NoteAction()
    {
        LatencyRecord? done = null;
        lock (_lock) if (_open is { } o) done = MarkActionLocked(o, Now);
        Emit(done);
    }

    /// <summary>The brainstem ran a reflex command (counted as the action itself).</summary>
    public void NoteReflex(string name) { lock (_lock) if (_open is { } o) { o.Kind = "reflex:" + name; } }

    /// <summary>Classifies the measurement ("spec:gen", "simple", "plan").</summary>
    public void NoteKind(string kind) { lock (_lock) if (_open is { } o && !o.Kind.StartsWith("reflex", StringComparison.Ordinal)) o.Kind = kind; }

    /// <summary>The effort the brain's turn ran at.</summary>
    public void NoteEffort(string? effort) { lock (_lock) if (_open is { } o) o.Effort = effort; }

    /// <summary>A movement started (<paramref name="speculative"/> = brainstem walk, not yet adopted).</summary>
    public void NoteMove(bool speculative)
    {
        LatencyRecord? done = null;
        lock (_lock)
        {
            if (_open is not { } o) return;
            var now = Now;
            o.Move ??= now;
            if (o.Reaction == null) { o.Reaction = now; o.ReactionBy = "move"; }
            if (speculative && o.SpecStarted == null) { o.SpecStarted = now; o.SpecRounds = o.Rounds; }
            else done = MarkActionLocked(o, now);
        }
        Emit(done);
    }

    /// <summary>A body tool is about to run (brain, worker, script or reflex). Speculative/social steps are not actions.</summary>
    public void NoteTool(string tool, bool filler)
    {
        LatencyRecord? done = null;
        lock (_lock)
        {
            if (_open is not { } o) return;
            if (o.Tools.Count < 16) o.Tools.Add(filler ? "~" + tool : tool);
            if (filler || !(BodyTools.Contains(tool) || tool == "stop")) return; // a stop is the action a "stop" asks for
            var now = Now;
            if (o.Reaction == null) { o.Reaction = now; o.ReactionBy = ActivityManagerMovement(tool) ? "move" : tool == "act" ? "gesture" : "action"; }
            if (ActivityManagerMovement(tool)) o.Move ??= now;
            done = MarkActionLocked(o, now);
        }
        Emit(done);
    }

    /// <summary>A non-body tool the brain called (sequence only).</summary>
    public void NoteSequence(string tool) { lock (_lock) if (_open is { } o && o.Tools.Count < 16) o.Tools.Add(tool); }

    /// <summary>A speculative step was adopted/retargeted (its start counts as the first action) or cancelled.</summary>
    public void NoteSpeculation(bool adopted)
    {
        LatencyRecord? done = null;
        lock (_lock)
        {
            if (adopted) _specAdopted++; else _specCancelled++;
            if (_open is not { } o) return;
            o.Speculation = adopted ? "adopted" : "cancelled";
            if (adopted && o.SpecStarted is { } at) done = MarkActionLocked(o, at, o.SpecRounds);
        }
        Emit(done);
    }

    /// <summary>A model call (brain or worker) completed.</summary>
    public void NoteRound() { lock (_lock) if (_open is { } o) o.Rounds++; }

    /// <summary>The brain starts a turn. <paramref name="chatTurn"/> = woken by typed chat (else nothing is measured).</summary>
    public void BeginTurn(bool chatTurn)
    {
        lock (_lock)
        {
            if (_open is { } o && chatTurn) { o.InTurn = true; o.CloseBy = null; }
        }
    }

    /// <summary>
    /// The brain's turn ended. <paramref name="workPending"/> = it queued/started work whose first game action may still
    /// come (a move, a delegate): the measurement then waits for it (bounded). Returns the finished record, if closed now.
    /// </summary>
    public LatencyRecord? EndTurn(bool workPending)
    {
        LatencyRecord? r = null;
        lock (_lock)
        {
            if (_open is not { InTurn: true } o) return null;
            o.InTurn = false;
            if (o.Action == null && workPending) o.CloseBy = Now + AwaitActionAfterTurn;
            else r = CloseLocked();
        }
        Emit(r);
        return r;
    }

    /// <summary>Closes a measurement that waited too long for its queued action (called from the TUI refresh / tests).</summary>
    public void Tick()
    {
        LatencyRecord? r = null;
        lock (_lock)
            if (_open is { InTurn: false, CloseBy: { } by } && Now >= by) r = CloseLocked();
        Emit(r);
    }

    private static bool ActivityManagerMovement(string tool) => tool is "goto" or "follow";

    private LatencyRecord? MarkActionLocked(Open o, DateTimeOffset at, int? rounds = null)
    {
        if (o.Action != null && o.Action <= at) return null;
        o.Action = at;
        o.RoundsAtAction = rounds ?? (o.RoundsAtAction is { } ra && ra < o.Rounds ? ra : o.Rounds);
        if (o.Reaction is not { } r || r > at) { o.Reaction = at; o.ReactionBy ??= "action"; }
        // The turn already ended and only waited for this: close now.
        return !o.InTurn && o.CloseBy != null ? CloseLocked() : null;
    }

    /// <summary>The full answer: the later of the final line and the first action; only a first line (an ack) when neither came.</summary>
    internal static long? Full(long? conclusive, long? action, long? firstChat) =>
        conclusive is { } c && action is { } a ? Math.Max(c, a) : conclusive ?? action ?? firstChat;

    private LatencyRecord? CloseLocked()
    {
        if (_open is not { } o) return null;
        _open = null;
        long? Ms(DateTimeOffset? t) => t is { } v ? Math.Max(0, (long)(v - o.ChatAt).TotalMilliseconds) : null;
        var rec = new LatencyRecord
        {
            Ts = o.ChatAt, Bot = Bot, Player = o.Player, Text = o.Text, Kind = o.Kind,
            ReactionMs = Ms(o.Reaction), FirstMoveMs = Ms(o.Move), FirstActionMs = Ms(o.Action),
            RoundsBeforeAction = o.Action != null ? o.RoundsAtAction : null, Rounds = o.Rounds, Tools = o.Tools.ToArray(),
            Speculation = o.Speculation, Effort = o.Effort,
            FirstChatMs = Ms(o.FirstChat), ConclusiveMs = Ms(o.Conclusive), Ack = o.Ack, Path = o.Path,
            Category = o.Category, ReactionBy = o.Reaction != null ? o.ReactionBy : null, Instant = o.Instant,
            FullMs = Full(Ms(o.Conclusive), Ms(o.Action), Ms(o.FirstChat)),
        };
        _recent.AddLast(rec);
        while (_recent.Count > _keep) _recent.RemoveFirst();
        return rec;
    }

    private void Emit(LatencyRecord? r)
    {
        if (r == null) return;
        try { Recorded?.Invoke(r); } catch (Exception) { }
    }

    public IReadOnlyList<LatencyRecord> Recent() { lock (_lock) return _recent.ToArray(); }

    /// <summary>p50/p95 of first reaction and full answer by kind of message over the recent records (<see cref="LatencyReport"/>).</summary>
    public string ReportLine() => LatencyReport.Line(Recent());

    /// <summary>Medians over the last <paramref name="window"/> records.</summary>
    public LatencySummary Summary(int window = 50)
    {
        Tick();
        lock (_lock)
        {
            var list = _recent.TakeLast(window).ToList();
            return new LatencySummary(list.Count, P50(list.Select(r => r.ReactionMs)), P50(list.Select(r => r.FirstActionMs)),
                P50(list.Select(r => r.FirstMoveMs)), P50D(list.Where(r => r.RoundsBeforeAction != null).Select(r => (double)r.RoundsBeforeAction!.Value)),
                _specAdopted, _specCancelled, P50(list.Select(r => r.FirstChatMs)), P50(list.Select(r => r.ConclusiveMs)),
                _cheapOnly, _cheapMisses, _chats, LatencyReport.Line(list));
        }
    }

    public static long? P50(IEnumerable<long?> values)
    {
        var v = values.Where(x => x != null).Select(x => x!.Value).OrderBy(x => x).ToList();
        return v.Count == 0 ? null : v[(v.Count - 1) / 2];
    }

    private static double? P50D(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? null : v[(v.Count - 1) / 2];
    }
}

/// <summary>Appends <see cref="LatencyRecord"/>s to <c>latency-YYYY-MM-DD.jsonl</c> next to the usage telemetry.</summary>
public sealed class LatencyJsonlWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _dir;
    private readonly object _lock = new();

    public LatencyJsonlWriter(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    public static string FileName(DateTimeOffset day) => $"latency-{day:yyyy-MM-dd}.jsonl";

    public void Write(LatencyRecord r)
    {
        try
        {
            var line = JsonSerializer.Serialize(r, Json);
            lock (_lock) File.AppendAllText(Path.Combine(_dir, FileName(r.Ts.ToLocalTime())), line + "\n");
        }
        catch (Exception) { /* telemetry never breaks the bot */ }
    }

    public static string Serialize(LatencyRecord r) => JsonSerializer.Serialize(r, Json);
}
