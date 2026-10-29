using System.Text.RegularExpressions;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Chat;

public enum ChatKind { Public, Whisper, Join, Leave, System, Emote }

public sealed record ChatMessage(ChatKind Kind, string? Player, string Text, string Raw);

/// <summary>
/// Turns raw server chat lines into <see cref="GameEvent"/>s (only addressed messages wake the model; others are
/// capped background context), answers <c>ask_player</c> waiters, and sends the bot's replies split/sanitised
/// for b1.8.1 with pacing.
/// </summary>
public sealed partial class ChatRouter
{
    private readonly ChatOptions _options;
    private readonly EventQueue _events;
    private readonly Func<string, CancellationToken, Task> _sendRaw;
    private readonly TimeProvider _time;
    private readonly Regex[] _whisper, _join, _leave, _public;
    private readonly Regex _groupRegex;
    private readonly object _lock = new();
    private readonly HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _joinedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTalkedWith = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _incoming = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Line, DateTimeOffset At)> _recentSent = new();
    private readonly List<Waiter> _waiters = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;

    /// <param name="sendRaw">Sends one raw chat packet (≤100 chars). Implemented by the client layer.</param>
    public ChatRouter(ChatOptions options, EventQueue events, Func<string, CancellationToken, Task> sendRaw, TimeProvider? time = null)
    {
        _options = options;
        _events = events;
        _sendRaw = sendRaw;
        _time = time ?? TimeProvider.System;
        _whisper = Compile(options.WhisperPatterns);
        _join = Compile(options.JoinPatterns);
        _leave = Compile(options.LeavePatterns);
        _public = Compile(options.PublicPatterns);
        _groupRegex = new Regex(@"(?<![A-Za-z0-9_])(?:" + string.Join("|", options.GroupWords.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        SetIdentity(options.DisplayName, options.Aliases);
    }

    /// <summary>
    /// The bot just joined the game (bot layer, on spawn): starts the startup-chatter window, during which unprompted chat is
    /// capped at <see cref="ChatOptions.StartupMaxLines"/> lines in total until a player talks to the bot.
    /// </summary>
    public void BeginSession()
    {
        lock (_lock)
        {
            _startupUntil = Now + TimeSpan.FromSeconds(Math.Max(0, _options.StartupWindowSeconds));
            _startupLinesLeft = Math.Max(0, _options.StartupMaxLines);
        }
    }

    // Startup chatter (live 2026-10-27: five lines about new builds and notes before anyone spoke): until a player talks to the
    // bot or the window ends, unprompted chat is capped at StartupMaxLines lines in total.
    private DateTimeOffset _startupUntil = DateTimeOffset.MinValue;
    private int _startupLinesLeft;

    public ChatOptions Options => _options;

    /// <summary>Login username (fixed for the session).</summary>
    public string Username => _options.Username;

    /// <summary>Current name the bot answers to in chat (changeable at runtime).</summary>
    public string DisplayName => _identity.DisplayName;

    /// <summary>Current extra names (changeable at runtime).</summary>
    public IReadOnlyList<string> Aliases => _identity.Aliases;

    /// <summary>Raised after the display name / aliases change.</summary>
    public event Action? IdentityChanged;

    private sealed record Identity(string DisplayName, IReadOnlyList<string> Aliases, Regex NameRegex);

    private volatile Identity _identity = null!;

    /// <summary>
    /// Replaces the names the bot answers to. Takes effect for the very next chat line. The login username
    /// always counts as a name too (it's what appears in &lt;name&gt; chat).
    /// </summary>
    public void SetIdentity(string displayName, IEnumerable<string> aliases)
    {
        var dn = displayName.Trim();
        var al = aliases.Select(a => a.Trim()).Where(a => a.Length > 0 && !a.Equals(dn, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new[] { _options.Username, dn }.Concat(al).Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderByDescending(n => n.Length) // longest first so "claude chan" wins over "claude"
            .Select(n => string.Join(@"[\s_-]*", n.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)));
        var regex = new Regex(@"(?<![A-Za-z0-9_])@?(?:" + string.Join("|", names) + @")(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        _identity = new Identity(dn, al, regex);
        IdentityChanged?.Invoke();
    }

    /// <summary>
    /// Hive: true for the logins of the other hive bots (main + clones). Their chat lines, /me emotes, joins and leaves are
    /// hive-internal: never addressed, never "players", never a reason to reply. Null = no hive.
    /// </summary>
    public Func<string, bool>? IsHiveBot { get; set; }

    /// <summary>
    /// Hive chat arbiter: final say on whether THIS bot handles a player message, given (message, locally addressed).
    /// Every hive process runs the same deterministic owner rule, so each player message gets exactly one replier. Null = no hive.
    /// </summary>
    public Func<ChatMessage, bool, bool>? Arbiter { get; set; }

    /// <summary>Raised after an addressed player message was accepted by this bot (hive: sticky conversation owner).</summary>
    public event Action<ChatMessage>? Accepted;

    /// <summary>
    /// Server-command gate: sees every player line with this bot's final verdict (trusted players' exact "/x" lines become
    /// one-shot authorisations) and decides which /commands model text may contain. Null = only /me is ever sent.
    /// </summary>
    public CommandGate? Commands { get; set; }

    /// <summary>
    /// Hive: extra "explicitly addressed" test (a clone's arbiter knows "67, ..." is for it even though "67" isn't a name).
    /// Used for command authorisations, which need an explicit address (or a whisper).
    /// </summary>
    public Func<ChatMessage, bool>? ExplicitlyAddressed { get; set; }

    /// <summary>
    /// Hive clone: chat not handled by this bot is dropped instead of riding along as background context, so it can never
    /// wake (or be answered by) a clone and costs no tokens. ask_player waiters still get their player's lines.
    /// </summary>
    public bool DropBackgroundChat { get; set; }

    /// <summary>True when the message names this bot (display name, aliases or login).</summary>
    public bool Names(string text) => _identity.NameRegex.IsMatch(text ?? "");

    private bool IsHive(string? name) => name != null && IsHiveBot?.Invoke(name) == true;

    /// <summary>Humans currently online (bot excluded).</summary>
    public IReadOnlyCollection<string> OnlinePlayers { get { lock (_lock) return _online.ToArray(); } }

    /// <summary>Humans who joined at or after <paramref name="since"/> and are online now (a turn's "X logged off" is stale).</summary>
    public IReadOnlyCollection<string> JoinedSince(DateTimeOffset since)
    {
        lock (_lock) return _joinedAt.Where(kv => kv.Value >= since && _online.Contains(kv.Key)).Select(kv => kv.Key).ToArray();
    }

    /// <summary>Replace the online player list (e.g. from the tab-list packets).</summary>
    public void SetOnlinePlayers(IEnumerable<string> players)
    {
        lock (_lock)
        {
            _online.Clear();
            foreach (var p in players)
                if (!IsSelf(p) && !IsHive(p)) _online.Add(p);
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---------- incoming ----------

    /// <summary>
    /// /me as relayed by the server: "* name action". The server inserts the name, so it reliably identifies the sender;
    /// the action is never a system event ("* kiwi fell out of the world" is kiwi emoting, not a death).
    /// </summary>
    private static readonly Regex EmoteLine = new(@"^\* (?<name>[A-Za-z0-9_]{1,16}) (?<msg>.+)$", RegexOptions.Compiled);

    /// <summary>Raised with a line the bot tried to send that was dropped because it was a server command (not /me).</summary>
    public event Action<string>? CommandBlocked;

    /// <summary>The bot's own outgoing text (whole, before line splitting), e.g. "I'll pass it to 67" opens a hand-off.</summary>
    public event Action<string>? Said;

    /// <summary>The bot just spoke to this player (its line's audience): a hive clone tells main, so the player's unnamed
    /// follow-ups continue with it for a short window.</summary>
    public event Action<string>? SpokeTo;

    public ChatMessage Parse(string raw)
    {
        var line = ChatText.StripColors(raw ?? "").Trim();
        if (EmoteLine.Match(line) is { Success: true } em)
            return new(ChatKind.Emote, em.Groups["name"].Value, em.Groups["msg"].Value.Trim(), raw!);
        foreach (var r in _whisper)
            if (r.Match(line) is { Success: true } m) return new(ChatKind.Whisper, CleanName(m.Groups["name"].Value), m.Groups["msg"].Value.Trim(), raw!);
        foreach (var r in _join)
            if (r.Match(line) is { Success: true } m) return new(ChatKind.Join, CleanName(m.Groups["name"].Value), line, raw!);
        foreach (var r in _leave)
            if (r.Match(line) is { Success: true } m) return new(ChatKind.Leave, CleanName(m.Groups["name"].Value), line, raw!);
        for (int i = 0; i < _public.Length; i++)
        {
            var m = _public[i].Match(line);
            if (!m.Success) continue;
            var name = CleanName(m.Groups["name"].Value);
            if (name == null) continue;
            // Loose "name: msg" patterns also match server notices ("Usage: ..."); require a known player when we know who's online.
            if (i > 0 && !IsSelf(name))
            {
                lock (_lock) if (_online.Count > 0 && !_online.Contains(name)) continue;
            }
            return new(ChatKind.Public, name, m.Groups["msg"].Value.Trim(), raw!);
        }
        return new(ChatKind.System, null, line, raw!);
    }

    /// <summary>Feed every raw chat line from the server here.</summary>
    public ChatMessage OnServerChat(string raw) => OnServerChat(raw, test: false);

    /// <summary>
    /// <paramref name="test"/> = the line was injected by a harness impersonating a player (console <c>/as</c>): it is handled
    /// normally but its events carry <see cref="GameEvent.IsTest"/>, so memory tags it source=test and reflection ignores it.
    /// </summary>
    public ChatMessage OnServerChat(string raw, bool test)
    {
        _injectingTest = test;
        try { return Route(raw); }
        finally { _injectingTest = false; }
    }

    [ThreadStatic] private static bool _injectingTest;

    private void Post(GameEvent e) => _events.Post(_injectingTest ? e with { IsTest = true } : e);

    private ChatMessage Route(string raw)
    {
        var msg = Parse(raw);
        switch (msg.Kind)
        {
            case ChatKind.Join or ChatKind.Leave when IsHive(msg.Player):
                break; // a hive bot logging in/out is not a player event
            case ChatKind.Join when msg.Player != null && !IsSelf(msg.Player):
                lock (_lock) { _online.Add(msg.Player); _joinedAt[msg.Player] = Now; }
                Post(GameEvent.Joined(msg.Player));
                break;
            case ChatKind.Leave when msg.Player != null && !IsSelf(msg.Player):
                lock (_lock) _online.Remove(msg.Player);
                Post(GameEvent.Left(msg.Player));
                break;
            case ChatKind.Public or ChatKind.Whisper or ChatKind.Emote when msg.Player != null:
                HandleMessage(msg);
                break;
            case ChatKind.System:
                Deaths.Observe(msg.Text);
                break;
        }
        return msg;
    }

    private void HandleMessage(ChatMessage msg)
    {
        var player = msg.Player!;
        if (IsSelf(player) || IsHive(player) || IsEcho(msg.Text) || msg.Text.Length == 0) return;
        lock (_lock)
        {
            _online.Add(player);
            _recentHeard.Enqueue((player, msg.Text, Now));
            while (_recentHeard.Count > 24) _recentHeard.Dequeue();
        }

        Observe(msg);
        var decision = Decide(msg);
        var reason = decision.Reason;
        bool addressed = reason.IsAddressed();
        if (Arbiter != null)
        {
            try { addressed = Arbiter(msg, addressed); }
            catch (Exception) { /* arbiter failure: fall back to the local decision */ }
        }
        try { AddressDecided?.Invoke(msg, decision with { Why = addressed == decision.Addressed ? decision.Why : decision.Why + "; hive: another bot's" }); }
        catch (Exception) { }
        var missed = NoteAddressing(msg, reason, addressed);
        // A line for a foreign bot (it calls it, uses its prefix, or continues its thread): main stays out, keeps a note.
        string? foreign = !addressed && decision.To is { } to && IsOtherBot(to) ? to : null;
        if (msg.Kind == ChatKind.Public && !IsOtherBot(player))
            lock (_lock)
            {
                bool toUs = addressed || decision.Addressed;
                _lastHumanLine = (player, Now, toUs, decision.To, CommandPrefix(msg.Text));
                _lastLineTo[player] = (Now, toUs ? _options.Username : decision.To);
                if (toUs) { _lastToUs[player] = Now; _requestAt.Remove(player); } // their reply closes our open request
                if (foreign != null) _partner[player] = (foreign, Now); // the thread goes on with each follow-up
            }
        if (!addressed && ShouldClassify(msg, reason))
        {
            _ = ClassifyThenDeliverAsync(msg);
            return;
        }
        // "dump your stuff into a chest" then "maidchan": the bare name points at the line just before it.
        if (addressed && reason == AddressReason.Name && missed is { } prev && IsNameOnly(msg.Text))
        {
            _events.RemovePending(e => e.Kind == EventKind.Chat && !e.Wakes && player.Equals(e.Player, StringComparison.OrdinalIgnoreCase) && e.Text == prev);
            Deliver(msg with { Text = prev, Raw = prev }, true, AddressReason.Name);
        }
        Deliver(msg, addressed, reason, foreign);
    }

    /// <param name="foreignBot">The foreign bot this (unaddressed) line is for: no waiter takes it, and it rides along as a
    /// Background note ("kiwi is instructing robinchan: ...") instead of plain chat.</param>
    private void Deliver(ChatMessage msg, bool addressed, AddressReason reason = AddressReason.Name, string? foreignBot = null)
    {
        var player = msg.Player!;
        double? confidence = addressed ? reason.Confidence() : null;
        if (addressed) lock (_lock) _startupUntil = DateTimeOffset.MinValue; // a conversation: the startup cap is over
        if (addressed) Accepted?.Invoke(msg);
        if (Commands != null)
        {
            bool named = msg.Kind == ChatKind.Whisper || _identity.NameRegex.IsMatch(msg.Text);
            if (!named && ExplicitlyAddressed != null)
            {
                try { named = ExplicitlyAddressed(msg); } catch (Exception) { }
            }
            Commands.Observe(msg, addressed, named);
        }

        if (msg.Kind == ChatKind.Emote)
        {
            if (!addressed && DropBackgroundChat) return;
            // Emotes are personality/treatment signals, never answers to ask_player.
            if (addressed && !AllowIncoming(player)) addressed = false;
            Post(GameEvent.Emote(player, msg.Text, addressed) with { AddressConfidence = confidence });
            if (addressed) lock (_lock) { _lastTalkedWith[player] = Now; if (!IsOtherBotLocked(player)) _lastAddressedAt = Now; }
            return;
        }

        // Hive: another bot owns this conversation thread (it asked / is watching a demo), or the player named another bot:
        // that reply is theirs; never let our own waiter eat it.
        bool elsewhere = false;
        if (OwnedElsewhere != null)
        {
            try { elsewhere = OwnedElsewhere(msg); } catch (Exception) { }
        }
        if (elsewhere || foreignBot != null) addressed = false;
        else if (TryCompleteWaiter(player, msg.Text, addressed)) return;

        if (addressed && !AllowIncoming(player)) addressed = false; // spam: keep as context only
        if (!addressed && DropBackgroundChat) return;

        if (foreignBot != null && !addressed)
            Post(GameEvent.Notice($"{player} is instructing {foreignBot}: {msg.Text}", EventPriority.Background));
        else if (msg.Kind == ChatKind.Whisper && addressed)
            Post(GameEvent.Whisper(player, msg.Text) with { AddressConfidence = confidence });
        else
            Post(GameEvent.Chat(player, msg.Text, addressed) with { AddressConfidence = addressed ? confidence : null });

        if (addressed) lock (_lock) { _lastTalkedWith[player] = Now; if (!IsOtherBotLocked(player)) _lastAddressedAt = Now; }
    }

    private DateTimeOffset _lastAddressedAt = DateTimeOffset.MinValue;

    /// <summary>A human (not another bot) addressed the bot (chat or whisper it took) within <paramref name="window"/>.</summary>
    public bool AddressedWithin(TimeSpan window)
    {
        lock (_lock) return _lastAddressedAt != DateTimeOffset.MinValue && Now - _lastAddressedAt <= window;
    }

    /// <summary>
    /// Hive hook: true when a player's message belongs to ANOTHER bot (it holds the thread lease, or the player named it),
    /// so this router neither answers it nor completes its own waiters with it. Null = single bot.
    /// </summary>
    public Func<ChatMessage, bool>? OwnedElsewhere { get; set; }

    /// <summary>(player, open, ttl): this bot opened/closed a conversation thread (a question or demo waiting on that player).</summary>
    public event Action<string, bool, TimeSpan>? ThreadChanged;

    /// <summary>
    /// Holds the conversation thread with <paramref name="player"/> while this bot waits on them (ask_player, ask_stand,
    /// watch_demo): the hive arbiter gives this bot their replies until the handle is disposed or the lease times out.
    /// </summary>
    public IDisposable HoldThread(string player, TimeSpan ttl)
    {
        try { ThreadChanged?.Invoke(player, true, ttl); } catch (Exception) { }
        return new ThreadHandle(this, player);
    }

    private sealed class ThreadHandle(ChatRouter chat, string player) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            try { chat.ThreadChanged?.Invoke(player, false, TimeSpan.Zero); } catch (Exception) { }
        }
    }

    // ---------- this turn's speech (so a tool doesn't repeat what the brain just said) ----------

    private DateTimeOffset _turnStart = DateTimeOffset.MaxValue;
    private DateTimeOffset _modelSpokeAt = DateTimeOffset.MinValue;

    private readonly Queue<(string Player, string Text, DateTimeOffset At)> _recentHeard = new();

    /// <summary>
    /// Landmark describer for a block ("the west cell of the gen, next to the lava"), set by the bot layer; questions use it
    /// instead of raw coordinates. Null = none.
    /// </summary>
    public Func<int, int, int, string?>? Landmark { get; set; }

    /// <summary>The last few lines (at most 10 min old) a player said (any player when null), oldest first.</summary>
    public IReadOnlyList<string> RecentFrom(string? player)
    {
        lock (_lock)
        {
            var cutoff = Now - TimeSpan.FromMinutes(10);
            return _recentHeard.Where(h => h.At >= cutoff && (player == null || h.Player.Equals(player, StringComparison.OrdinalIgnoreCase)))
                .Select(h => h.Text).ToList();
        }
    }

    /// <summary>The brain started a turn (BotAgent).</summary>
    public void MarkTurnStart() { lock (_lock) _turnStart = Now; }

    /// <summary>The brain's own text went to chat this turn (BotAgent).</summary>
    public void NoteModelSpeech() { lock (_lock) _modelSpokeAt = Now; }

    /// <summary>True when the brain already said something in the current turn (tools then stay quiet).</summary>
    public bool ModelSpokeThisTurn { get { lock (_lock) return _modelSpokeAt >= _turnStart && _turnStart != DateTimeOffset.MaxValue; } }

    /// <summary>Whether the bot should treat this message as directed at it.</summary>
    public bool IsAddressed(ChatMessage msg) => ReasonFor(msg).IsAddressed();

    /// <summary>Why a line is (or isn't) for the bot: name, whisper, conversation continuity, solo, group, imperative...</summary>
    public AddressReason ReasonFor(ChatMessage msg) => Decide(msg).Reason;

    // ---------- addressing: cheap classifier + telemetry ----------

    /// <summary>Regular (trusted) players: the classifier only runs for them. Null = nobody.</summary>
    public Func<string, bool>? IsRegular { get; set; }

    /// <summary>The bot is near this player (bot layer; within Classifier.NearBlocks). Null = unknown (thread only).</summary>
    public Func<string, bool>? IsNear { get; set; }

    /// <summary>
    /// The cheap "is this line for the bot?" call (AgentRuntime: one tiny Haiku call; args: line, recent chat). Null result =
    /// couldn't decide. Only asked for unsure lines from regulars while the bot is near them or in an active thread with them.
    /// </summary>
    public Func<ChatMessage, IReadOnlyList<string>, CancellationToken, Task<bool?>>? Classifier { get; set; }

    /// <summary>Addressing counters (continuity, imperatives, classifier calls/yes, misses).</summary>
    public AddressingStats Addressing { get; } = new();

    /// <summary>(player, missed line, how it was finally addressed): a line we didn't take that the player then repeated by name.</summary>
    public event Action<string, string, string>? AddressMissed;

    private readonly Dictionary<string, (string Text, DateTimeOffset At)> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _classifierCalls = new();

    /// <summary>Telemetry + the ignored-line memory; returns the same player's ignored line this one (by name) now picks up.</summary>
    private string? NoteAddressing(ChatMessage msg, AddressReason reason, bool addressed)
    {
        if (msg.Kind != ChatKind.Public) return null;
        var player = msg.Player!;
        string? missed = null;
        lock (_lock)
        {
            if (reason == AddressReason.Continuity) Addressing.Continuity++;
            else if (reason == AddressReason.Imperative) Addressing.Imperatives++;
            if (!addressed)
            {
                if (reason is not (AddressReason.OtherPlayer or AddressReason.Rejected or AddressReason.FromBot)) _ignored[player] = (msg.Text, Now);
            }
            else if (_ignored.Remove(player, out var prev) && Now - prev.At < TimeSpan.FromSeconds(_options.ReplyWindowSeconds) &&
                     reason is AddressReason.Name)
            {
                Addressing.Misses++;
                missed = prev.Text;
                if (Now - prev.At > TimeSpan.FromSeconds(Math.Max(0, _options.NameLinkSeconds))) { NoteMiss(player, missed, reason); return null; }
            }
        }
        if (missed != null) NoteMiss(player, missed, reason);
        return missed;
    }

    private void NoteMiss(string player, string missed, AddressReason reason)
    {
        try { AddressMissed?.Invoke(player, missed, reason.ToString()); } catch (Exception) { }
    }

    private bool ShouldClassify(ChatMessage msg, AddressReason reason)
    {
        var c = _options.Classifier;
        // Only the leftover "no idea" lines: never a line for someone else, a rejection, a bot's line or "unclear with a bot around".
        if (Classifier == null || !c.Enabled || msg.Kind != ChatKind.Public || reason != AddressReason.No) return false;
        var player = msg.Player!;
        if (IsRegular?.Invoke(player) != true) return false;
        lock (_lock)
        {
            if (_waiters.Any(w => w.Player != null && w.Player.Equals(player, StringComparison.OrdinalIgnoreCase))) return false; // a waiter takes it anyway
            bool thread = _lastTalkedWith.TryGetValue(player, out var at) && Now - at < TimeSpan.FromMinutes(Math.Max(0, c.ThreadMinutes));
            bool near = false;
            try { near = IsNear?.Invoke(player) == true; } catch (Exception) { }
            if (!thread && !near) return false;
            var cutoff = Now - TimeSpan.FromHours(1);
            while (_classifierCalls.Count > 0 && _classifierCalls.Peek() < cutoff) _classifierCalls.Dequeue();
            if (_classifierCalls.Count >= Math.Max(0, c.MaxPerHour)) return false;
            _classifierCalls.Enqueue(Now);
        }
        // In a hive only the bot that would own the line asks (one call per line, not one per process).
        if (Arbiter != null)
        {
            try { if (!Arbiter(msg, true)) return false; } catch (Exception) { return false; }
        }
        return true;
    }

    private async Task ClassifyThenDeliverAsync(ChatMessage msg)
    {
        bool? yes = null;
        lock (_lock) Addressing.ClassifierCalls++;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Math.Max(200, _options.Classifier.TimeoutMs)));
            yes = await Classifier!(msg, RecentContext(8), cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* timeout / API error: the line stays background context */ }
        bool addressed = yes == true;
        if (addressed)
        {
            lock (_lock) { Addressing.ClassifierYes++; _ignored.Remove(msg.Player!); _lastToUs[msg.Player!] = Now; }
            if (Arbiter != null)
            {
                try { addressed = Arbiter(msg, true); } catch (Exception) { }
            }
        }
        Deliver(msg, addressed, AddressReason.Classifier);
    }

    /// <summary>The last few lines heard and said (oldest first, "name: text") for the classifier prompt.</summary>
    private IReadOnlyList<string> RecentContext(int max)
    {
        lock (_lock)
        {
            var cutoff = Now - TimeSpan.FromMinutes(3);
            var heard = _recentHeard.Where(h => h.At >= cutoff).Select(h => (h.At, Line: $"{h.Player}: {h.Text}"));
            var said = _recentSent.Where(h => h.At >= cutoff).Select(h => (h.At, Line: $"you: {h.Line}"));
            return heard.Concat(said).OrderBy(x => x.At).TakeLast(max).Select(x => x.Line).ToList();
        }
    }

    /// <summary>
    /// Who a bot line is spoken to when the caller gave no audience (a task-event turn): online players it names (login or
    /// nickname: "master, ..."), else the player the bot was last talking with. Their next line then continues the thread.
    /// </summary>
    internal IReadOnlyList<string> InferAudience(string text)
    {
        lock (_lock)
        {
            // Exact accounts win: a regular's nickname is never read inside another account's name ("robin" in "robin-chan").
            var bare = PlayerNames.WithoutLogins(text, _online);
            var named = _online.Where(p => PlayerNames.MentionsLogin(text, p) ||
                                           (_options.PlayerNicknames.TryGetValue(p, out var nicks) && nicks.Any(n => AddressingText.Mentions(bare, n))) ||
                                           (_learnedNicks.TryGetValue(p, out var learned) && learned.Any(n => n.Length >= 2 && AddressingText.Mentions(bare, n))))
                .ToList();
            if (named.Count > 0) return named;
            var window = TimeSpan.FromSeconds(_options.ReplyWindowSeconds);
            var recent = _lastTalkedWith.Where(kv => _online.Contains(kv.Key) && Now - kv.Value < window).OrderByDescending(kv => kv.Value).ToList();
            return recent.Count > 0 ? [recent[0].Key] : [];
        }
    }

    private bool AllowIncoming(string player)
    {
        lock (_lock)
        {
            if (!_incoming.TryGetValue(player, out var q)) _incoming[player] = q = new Queue<DateTimeOffset>();
            var cutoff = Now - TimeSpan.FromSeconds(_options.PerPlayerWindowSeconds);
            while (q.Count > 0 && q.Peek() < cutoff) q.Dequeue();
            if (q.Count >= _options.PerPlayerMaxMessages) return false;
            q.Enqueue(Now);
            return true;
        }
    }

    private bool IsSelf(string? name) => name != null && name.Equals(_options.Username, StringComparison.OrdinalIgnoreCase);

    private bool IsEcho(string text)
    {
        lock (_lock)
        {
            var cutoff = Now - TimeSpan.FromSeconds(10);
            return _recentSent.Any(s => s.At > cutoff && s.Line == text);
        }
    }

    private static string? CleanName(string raw)
    {
        // "<[Admin] Bob>" / "<~Bob>" → "Bob": last identifier-looking token.
        var m = Regex.Matches(raw, "[A-Za-z0-9_]{1,16}");
        return m.Count == 0 ? null : m[^1].Value;
    }

    private static Regex[] Compile(string[] patterns) =>
        patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant)).ToArray();

    // ---------- ask_player support ----------

    /// <summary>
    /// ask_player &amp; co. reply merging: after a waiter's first line, more lines from the same player within
    /// <see cref="ChatDebounceOptions.ReplyQuietMs"/> are added to the same answer ("the left / one near the lava") instead of
    /// waking a separate turn. Null (or ReplyQuietMs 0) = the first line completes the waiter at once.
    /// </summary>
    public ChatDebounceOptions? Debounce { get; set; }

    /// <summary>Counters (reply merges) shared with the event queue.</summary>
    public ChatFlowStats? Stats { get; set; }

    private sealed class Waiter(string? player)
    {
        public readonly string? Player = player;
        public readonly TaskCompletionSource<string> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Parts = new();
        public string? From;
        public DateTimeOffset First;
        /// <summary>When the wait began (an open ask_player to this player counts as our request).</summary>
        public DateTimeOffset Created;
        public ITimer? Timer;
        public string Result => From + ": " + string.Join(" / ", Parts);
    }

    /// <summary>
    /// Waits for the next message from <paramref name="player"/> (any of their messages counts), or the next
    /// addressed message from anyone if null. The reply is consumed (not posted as an event); a burst of lines becomes one
    /// reply (see <see cref="Debounce"/>). Null on timeout.
    /// </summary>
    /// <summary>Replies being waited for right now (ask_player, watch_demo...): tests await a listener instead of sleeping.</summary>
    public int ReplyWaiters { get { lock (_lock) return _waiters.Count; } }

    public async Task<string?> WaitForReplyAsync(string? player, TimeSpan timeout, CancellationToken ct)
    {
        var w = new Waiter(player) { Created = Now };
        lock (_lock) _waiters.Add(w);
        try
        {
            return await w.Tcs.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Timed out while a reply was still being typed: what arrived is the answer.
            lock (_lock) return w.Parts.Count > 0 ? w.Result : null;
        }
        finally
        {
            lock (_lock) _waiters.Remove(w);
            w.Timer?.Dispose();
        }
    }

    private bool TryCompleteWaiter(string player, string text, bool addressed)
    {
        Waiter? done = null;
        lock (_lock)
        {
            // A reply already being collected from this player first, then their own waiter, then an "anyone" waiter.
            var w = _waiters.FirstOrDefault(x => x.Parts.Count > 0 && string.Equals(x.From, player, StringComparison.OrdinalIgnoreCase))
                ?? _waiters.FirstOrDefault(x => x.Parts.Count == 0 && x.Player != null && x.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
                ?? (addressed ? _waiters.FirstOrDefault(x => x.Parts.Count == 0 && x.Player == null) : null);
            if (w == null) return false;
            if (w.Parts.Count == 0) { w.From = player; w.First = Now; }
            w.Parts.Add(text);
            _lastTalkedWith[player] = Now;
            _lastToUs[player] = Now; // an answer to us

            int quiet = Debounce is { Enabled: true } d ? Math.Max(0, d.ReplyQuietMs) : 0;
            int max = Debounce?.MaxMs ?? 0;
            if (quiet == 0 || Debounce!.IsImmediate(text) || Now - w.First >= TimeSpan.FromMilliseconds(max))
            {
                _waiters.Remove(w);
                done = w;
            }
            else
            {
                w.Timer?.Dispose();
                w.Timer = _time.CreateTimer(_ => FinishWaiter(w), null, TimeSpan.FromMilliseconds(quiet), Timeout.InfiniteTimeSpan);
            }
        }
        if (done != null) Complete(done);
        return true;
    }

    private void FinishWaiter(Waiter w)
    {
        lock (_lock)
        {
            if (!_waiters.Remove(w)) return;
        }
        Complete(w);
    }

    private void Complete(Waiter w)
    {
        string result;
        lock (_lock) result = w.Result;
        if (w.Parts.Count > 1) Stats?.ReplyMerged();
        w.Tcs.TrySetResult(result);
    }

    // ---------- outgoing ----------

    /// <summary>
    /// Sends model text to chat: sanitised for b1.8.1, split into ≤100-char lines, capped at
    /// <see cref="ChatOptions.MaxLinesPerReply"/>, paced. Whispers use <see cref="ChatOptions.WhisperFormat"/>.
    /// Returns the lines actually sent.
    /// </summary>
    public async Task<IReadOnlyList<string>> SayAsync(string text, string? whisperTo = null, IEnumerable<string>? audience = null, CancellationToken ct = default)
    {
        var to = audience?.ToList() ?? [];
        if (whisperTo != null && to.Count == 0) to.Add(whisperTo);
        if (to.Count == 0) to.AddRange(InferAudience(text ?? ""));
        // At most one food ask per player per 10 minutes, whoever writes the line (brain, tool, script).
        text = Food.Filter(text ?? "", to, Now, PlayerAskedAboutFood);
        text = Greeted.Filter(text, to, Now); // one hello per player per few minutes (join turn + their first line)
        text = Deaths.Filter(text, to, out var stale); // "robin run to the chest" after "kiwi was slain by Zombie": stale
        if (stale.Count > 0) try { StaleDropped?.Invoke(string.Join(" ", stale)); } catch (Exception) { }
        string prefix = whisperTo != null ? string.Format(_options.WhisperFormat, whisperTo, "") : "";
        int max = ChatText.MaxLineLength - prefix.Length;
        // A line of model text starting with "/me " is an emote (sent as-is, always public); any other "/..." line would be a
        // server command: logged and dropped. Everything else is chat, in order.
        var lines = new List<(string Text, bool Emote)>();
        var pending = new List<string>();
        void FlushText()
        {
            if (pending.Count == 0) return;
            var clean = ChatText.Sanitize(string.Join(" ", pending));
            pending.Clear();
            if (clean.Length > 0) lines.AddRange(ChatText.Split(clean, max).Select(l => (l, false)));
        }
        foreach (var segment in (text ?? "").Replace("\r", "").Split('\n').SelectMany(SplitInlineEmotes))
        {
            var t = segment.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
            {
                FlushText();
                var action = ChatText.Sanitize(t[4..]);
                if (action.Length > 0) lines.Add(("/me " + TextUtilTruncate(action, ChatText.MaxLineLength - 4), true));
            }
            else if (t.StartsWith('/') && Commands != null && Commands.IsAlwaysAllowed(t) && !Commands.IsBlocked(t))
            {
                FlushText(); // e.g. "/tell kiwi psst": always-allowed commands from model text are sent as typed (one line)
                lines.Add((TextUtilTruncate("/" + ChatText.Sanitize(t[1..]), ChatText.MaxLineLength), true));
            }
            else if (t.StartsWith('/'))
            {
                FlushText();
                CommandBlocked?.Invoke(t);
            }
            else pending.Add(t);
        }
        FlushText();
        if (lines.Count == 0) return [];
        int cap = _options.MaxLinesPerReply;
        // A safety warning (a mob on someone, a creeper, a death) is never capped as startup chatter (one per threat per 30 s).
        bool warning = Warnings.Take(text, to);
        lock (_lock)
        {
            if (Now < _startupUntil && !warning)
            {
                cap = Math.Min(cap, _startupLinesLeft);
                _startupLinesLeft = Math.Max(0, _startupLinesLeft - lines.Count);
            }
        }
        if (cap <= 0) return [];
        try { Said?.Invoke(text); } catch (Exception) { /* listeners never break chat */ }
        if (lines.Count > cap)
        {
            lines = lines.Take(cap).ToList();
            var (last, emote) = lines[^1];
            int lim = emote ? ChatText.MaxLineLength : max;
            lines[^1] = (last.Length + 3 <= lim ? last + "..." : last[..(lim - 3)] + "...", emote);
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (line, emote) in lines)
            {
                var wait = _lastSend + TimeSpan.FromMilliseconds(_options.LineDelayMs) - Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                await _sendRaw(emote ? line : prefix + line, ct).ConfigureAwait(false);
                _lastSend = Now;
                lock (_lock)
                {
                    _recentSent.Enqueue((line, Now));
                    while (_recentSent.Count > 20) _recentSent.Dequeue();
                }
            }
        }
        finally
        {
            _sendLock.Release();
        }

        if (!lines.Any(l => !l.Emote) && audience == null) to.Clear(); // an emote alone isn't talking to anyone
        string[] spoke;
        lock (_lock)
        {
            if (whisperTo != null) _lastTalkedWith[whisperTo] = Now;
            foreach (var p in to) _lastTalkedWith[p] = Now;
            spoke = [.. to, .. whisperTo != null ? [whisperTo] : Array.Empty<string>()];
        }
        NoteAsked(spoke, lines.Where(l => !l.Emote).Select(l => l.Text));
        foreach (var p in spoke.Distinct(StringComparer.OrdinalIgnoreCase))
            try { SpokeTo?.Invoke(p); } catch (Exception) { /* listeners never break chat */ }
        return lines.Select(l => l.Text).ToList();
    }

    /// <summary>The food-ask guard (one ask per player per 10 minutes).</summary>
    public FoodAsks Food { get; } = new();

    /// <summary>The greeting guard (one hello per player per 3 minutes).</summary>
    public Greetings Greeted { get; } = new();

    /// <summary>Players who just died: advice queued for them is dropped (<see cref="RecentDeaths"/>).</summary>
    public RecentDeaths Deaths => _deaths ??= new RecentDeaths(() => Now);
    private RecentDeaths? _deaths;

    /// <summary>Raised with the advice dropped because its player just died.</summary>
    public event Action<string>? StaleDropped;

    /// <summary>Safety warnings that override the quiet gates (<see cref="SafetyWarnings"/>), rate-limited per threat.</summary>
    public SafetyWarnings Warnings => _warnings ??= new SafetyWarnings(() => Now);
    private SafetyWarnings? _warnings;

    private bool PlayerAskedAboutFood(string player)
    {
        lock (_lock)
        {
            var cutoff = Now - TimeSpan.FromSeconds(90);
            return _recentHeard.Any(h => h.At >= cutoff && h.Player.Equals(player, StringComparison.OrdinalIgnoreCase) && FoodAsks.IsFoodTopic(h.Text));
        }
    }

    /// <summary>
    /// Sends one command line exactly as given (run_command after <see cref="CommandGate.Authorize"/>; the gated sender
    /// still checks it). Paced like chat. False if it was too long to send.
    /// </summary>
    public async Task<bool> SendCommandAsync(string command, CancellationToken ct = default)
    {
        var line = (command ?? "").Trim();
        if (line.Length == 0 || line.Length > ChatText.MaxLineLength) return false;
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastSend + TimeSpan.FromMilliseconds(_options.LineDelayMs) - Now;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
            await _sendRaw(line, ct).ConfigureAwait(false);
            _lastSend = Now;
        }
        finally { _sendLock.Release(); }
        return true;
    }

    private static string TextUtilTruncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static readonly System.Text.RegularExpressions.Regex InlineMeRx = new(@"(?<=\s)/me\s+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex SentenceEndRx = new(@"(?<=[.!?])\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// "Hai, foxy sama~ /me curtsies. Your maid has chopped..." (night review B18: sent literally) becomes the chat part,
    /// the emote (up to its sentence end) as its own "/me" line, then the rest. A command line ("/tell ...") is left alone.
    /// </summary>
    internal static IEnumerable<string> SplitInlineEmotes(string segment)
    {
        var t = segment.Trim();
        if (t.StartsWith('/') && !t.StartsWith("/me ", StringComparison.OrdinalIgnoreCase)) { yield return segment; yield break; }
        while (true)
        {
            var m = InlineMeRx.Match(t);
            if (!m.Success) { yield return t; yield break; }
            var after = t[(m.Index + m.Length)..];
            var end = SentenceEndRx.Match(after);
            var action = (end.Success ? after[..end.Index] : after).Trim();
            var before = t[..m.Index].TrimEnd();
            if (before.Length > 0) yield return before;
            if (action.Length > 0) yield return "/me " + action;
            if (!end.Success) yield break;
            t = after[(end.Index + end.Length)..].Trim();
            if (t.Length == 0) yield break;
        }
    }
}
