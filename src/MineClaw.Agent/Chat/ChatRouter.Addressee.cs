using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>The router's verdict on one line: why, plus a short human-readable detail for the log.</summary>
public readonly record struct AddressDecision(AddressReason Reason, string Why)
{
    public bool Addressed => Reason.IsAddressed();
}

/// <summary>
/// Addressee resolution with a second, foreign bot on the server (live 10-27 23:35-23:45, robinchan): players stay terse, so each
/// line's addressee is scored in C# with no LLM call unless it's still ambiguous:
/// <list type="number">
/// <item>an explicit name or learned nickname (of us, another player or bot: "robinchan, I'm gonna call you c" then "c follow me");</item>
/// <item>gaze and proximity (<see cref="LookingAt"/>: the speaker's look ray / standing right next to someone);</item>
/// <item>content fit: a reply to a question we just asked them;</item>
/// <item>conversation continuity, including the other bot's replies (robinchan just answered Robin → Robin's next unnamed line
/// is robinchan's);</item>
/// <item>unclear while another bot is in the conversation: stay silent.</item>
/// </list>
/// Plus "I'm not talking to you" (a do-not-respond window) and bot loops: another bot's line wakes us only when it names us, never
/// as its reply to us, at most once per <see cref="ChatOptions.BotReplyCooldownMinutes"/>.
/// </summary>
public sealed partial class ChatRouter
{
    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex CallYou = new(
        @"\b(?:i'?m\s+(?:gonna|going\s+to)|i'?ll|i\s+will|imma|gonna|we'?(?:re\s+gonna|ll)|let'?s|i\s+(?:shall|wanna))\s+call\s+(?:you|u|ya|yu)\s+[""']?(?<nick>[A-Za-z0-9_]{1,16})[""']?" +
        @"|\b(?:your|ur)\s+(?:new\s+)?(?:name|nickname)\s+is\s+(?:now\s+)?[""']?(?<nick>[A-Za-z0-9_]{1,16})[""']?" +
        @"|\b(?:you'?re|youre|ur|you\s+are)\s+[""']?(?<nick>[A-Za-z0-9_]{1,16})[""']?\s+(?:now|from\s+now\s+on)\b", Rx);
    private static readonly Regex CallMe = new(
        @"\bcall\s+me\s+[""']?(?<nick>[A-Za-z0-9_-]{1,16})[""']?|\b(?:i'?m|im|i\s+am)\s+[""']?(?<nick>[A-Za-z0-9_-]{1,16})[""']?\s+to\s+(?:you|u)\b", Rx);
    private static readonly Regex Rejection = new(
        @"\b(?:i'?m|im|i\s+am|i\s+was|i\s+wasn'?t|i'?m\s+not|not)\s+(?:not\s+)?talking\s+(?:to|with)\s+(?:you|u|ya)\b|\bwasn'?t\s+(?:talking|speaking)\s+to\s+(?:you|u)\b" +
        @"|\b(?:that|this)\s+(?:wasn'?t|isn'?t|was\s+not)\s+(?:for|to)\s+(?:you|u)\b|\bnot\s+(?:you|u)\s*[,!.]|\bnot\s+(?:for|to)\s+(?:you|u)\b(?!\s+to)", Rx);
    private static readonly Regex IsABot = new(@"\b(?<name>[A-Za-z0-9_]{2,16})\s+(?:is|'s|=)\s+(?:a|an|my|our|her|his|their)\s+(?:ai\s+)?(?:bot|ai|npc)\b", Rx);
    private static readonly Regex SelfBot = new(@"\b(?:i'?m|im|i\s+am)\s+(?:a|an|just\s+a)\s+(?:ai\s+)?(?:bot|ai|npc)\b", Rx);
    private static readonly Regex TemplateMark = new(@"\buwu\b|\bowo\b|\bnya+\b|<3|!!|\*[a-z]+s\*|\b(?:sigma|rizz|skibidi)\b", Rx);
    private static readonly Regex NameFillers = new(@"\b(?:hey|hi|yo|oi|ok|okay|pls|please|plz)\b", Rx);

    // learned at runtime (case-insensitive logins)
    private readonly Dictionary<string, HashSet<string>> _learnedNicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _selfNicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownBots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Other, DateTimeOffset At)> _partner = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _rejectUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _askedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _botSpokeAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _fastReplies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _templateLines = new(StringComparer.OrdinalIgnoreCase);
    private (string Player, string Text, DateTimeOffset At)? _lastHeardLine;

    /// <summary>
    /// Gaze/proximity (bot layer): whom <c>player</c> is looking at (a ray within ~10°, ~8 blocks) or standing right next to,
    /// as a login (this bot's own username = us). Null = nobody / unknown.
    /// </summary>
    public Func<string, string?>? LookingAt { get; set; }

    /// <summary>Raised for every player line with the verdict (logged: "addressing: kiwi \"c follow me\" → not us (OtherPlayer: ...)").</summary>
    public event Action<ChatMessage, AddressDecision>? AddressDecided;

    /// <summary>Raised when an account is first taken for a (foreign) bot, with the reason.</summary>
    public event Action<string, string>? BotDetected;

    /// <summary>True for a known non-hive bot account (config <see cref="ChatOptions.OtherBots"/> or learned).</summary>
    public bool IsOtherBot(string? name)
    {
        if (name == null || IsSelf(name) || IsHive(name)) return false;
        if (_options.OtherBots.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        lock (_lock) return _knownBots.Contains(name);
    }

    /// <summary>All names a player goes by: configured nicknames plus ones learned in chat ("I'm gonna call you c").</summary>
    public IReadOnlyList<string> NicknamesOf(string player)
    {
        var list = new List<string>();
        if (_options.PlayerNicknames.TryGetValue(player, out var cfg)) list.AddRange(cfg);
        lock (_lock) if (_learnedNicks.TryGetValue(player, out var l)) list.AddRange(l);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Names players gave this bot in chat (count as its name, like the aliases).</summary>
    public IReadOnlyCollection<string> LearnedSelfNicknames { get { lock (_lock) return _selfNicks.ToArray(); } }

    private bool NamesUs(string text)
    {
        if (_identity.NameRegex.IsMatch(text ?? "")) return true;
        string[] nicks;
        lock (_lock) nicks = _selfNicks.ToArray();
        return nicks.Any(n => AddressingText.Vocative(text, n) || (n.Length >= 3 && AddressingText.Mentions(text, n)));
    }

    /// <summary>A line that is only our name ("maidchan", "hey maid?"): it points at the same player's line just before.</summary>
    internal bool IsNameOnly(string text)
    {
        var rest = _identity.NameRegex.Replace(text ?? "", " ");
        rest = NameFillers.Replace(rest, " ");
        return rest.All(c => !char.IsLetterOrDigit(c)) && _identity.NameRegex.IsMatch(text ?? "");
    }

    /// <summary>The online player (or us = our username) a line calls by login or nickname, else null.</summary>
    private string? CalledPlayer(string text, string speaker)
    {
        string[] online;
        lock (_lock) online = _online.Where(p => !p.Equals(speaker, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var p in online)
        {
            if (AddressingText.CallsLogin(text, p)) return p;
            foreach (var n in NicknamesOf(p))
                if (AddressingText.Vocative(text, n)) return p;
        }
        return null;
    }

    /// <summary>
    /// Learns from a player line before it is decided: bots ("robinchan is a bot", fast long replies, template chatter),
    /// nicknames, who is talking with whom, and "I'm not talking to you".
    /// </summary>
    private void Observe(ChatMessage msg)
    {
        if (msg.Kind != ChatKind.Public || msg.Player is not { } player) return;
        var text = msg.Text ?? "";
        var now = Now;
        bool namesUs = NamesUs(text);
        var called = namesUs ? null : CalledPlayer(text, player);
        var detected = new List<(string Name, string Why)>();
        lock (_lock)
        {
            // --- bots
            foreach (Match m in IsABot.Matches(text))
            {
                var who = ResolveLocked(m.Groups["name"].Value);
                if (who != null && !IsSelf(who) && !IsHive(who) && _knownBots.Add(who)) detected.Add((who, $"{player} said \"{TextUtilTruncate(m.Value, 40)}\""));
            }
            if (SelfBot.IsMatch(text) && _knownBots.Add(player)) detected.Add((player, "says it is a bot"));
            if (_lastHeardLine is { } prev && !prev.Player.Equals(player, StringComparison.OrdinalIgnoreCase)
                && now - prev.At <= TimeSpan.FromSeconds(2) && text.Length >= 25)
            {
                int n = _fastReplies[player] = _fastReplies.GetValueOrDefault(player) + 1;
                if (n >= 2 && _knownBots.Add(player)) detected.Add((player, $"{n} long replies within 2 s"));
            }
            if (TemplateMark.Matches(text).Count >= 2)
            {
                int n = _templateLines[player] = _templateLines.GetValueOrDefault(player) + 1;
                if (n >= 3 && _fastReplies.GetValueOrDefault(player) >= 1 && _knownBots.Add(player)) detected.Add((player, "template chatter + instant replies"));
            }
            _lastHeardLine = (player, text, now);
            if (_options.OtherBots.Contains(player, StringComparer.OrdinalIgnoreCase) || _knownBots.Contains(player)) _botSpokeAt[player] = now;

            // --- nicknames ("robinchan, I'm gonna call you c" → c = robinchan; "I'm robin-sama to you" → kiwi)
            if (CallYou.Match(text) is { Success: true } cy)
            {
                var nick = cy.Groups["nick"].Value;
                string? target = namesUs ? _options.Username : called
                    ?? (_partner.TryGetValue(player, out var pt) && now - pt.At <= TimeSpan.FromSeconds(_options.OtherConversationSeconds) ? pt.Other : null);
                if (target != null && !IsStopWord(nick))
                {
                    if (IsSelf(target)) _selfNicks.Add(nick);
                    else (_learnedNicks.TryGetValue(target, out var set) ? set : _learnedNicks[target] = new(StringComparer.OrdinalIgnoreCase)).Add(nick);
                }
            }
            if (CallMe.Match(text) is { Success: true } cm && !IsStopWord(cm.Groups["nick"].Value))
                (_learnedNicks.TryGetValue(player, out var mine) ? mine : _learnedNicks[player] = new(StringComparer.OrdinalIgnoreCase)).Add(cm.Groups["nick"].Value);

            // --- who is talking with whom
            if (called != null)
            {
                _partner[player] = (called, now);
                _partner[called] = (player, now);
            }
            else if (!namesUs)
            {
                // This player answering someone who just talked to them (robinchan answering Robin): that thread goes on.
                foreach (var (a, pa) in _partner.Where(kv => !kv.Key.Equals(player, StringComparison.OrdinalIgnoreCase)
                             && kv.Value.Other.Equals(player, StringComparison.OrdinalIgnoreCase) && now - kv.Value.At <= TimeSpan.FromSeconds(20)).ToList())
                {
                    _partner[a] = (player, now);
                    _partner[player] = (a, now);
                }
            }

            // --- "I'm not talking to you"
            if (Rejection.IsMatch(text) && (namesUs || called == null))
            {
                _rejectUntil[player] = now + TimeSpan.FromSeconds(Math.Max(0, _options.RejectWindowSeconds));
                _lastTalkedWith.Remove(player);
                _askedAt.Remove(player);
            }
        }
        foreach (var (name, why) in detected)
            try { BotDetected?.Invoke(name, why); } catch (Exception) { }
    }

    private static readonly HashSet<string> StopNicks = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "that", "this", "it", "him", "her", "them", "back", "later", "tomorrow", "now", "when", "if", "so", "and",
        "here", "over", "out", "up", "too", "done", "ok", "okay", "lol",
    };

    private static bool IsStopWord(string nick) => StopNicks.Contains(nick);

    // caller holds _lock: a login or nickname → the online login it means
    private string? ResolveLocked(string name)
    {
        if (IsSelf(name)) return _options.Username;
        if (_identity.NameRegex.IsMatch(name)) return _options.Username;
        var login = _online.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (login != null) return login;
        foreach (var p in _online)
        {
            if (_options.PlayerNicknames.TryGetValue(p, out var n) && n.Contains(name, StringComparer.OrdinalIgnoreCase)) return p;
            if (_learnedNicks.TryGetValue(p, out var l) && l.Contains(name)) return p;
        }
        return null;
    }

    /// <summary>The full verdict for a line (see the class summary for the order).</summary>
    public AddressDecision Decide(ChatMessage msg)
    {
        if (msg.Player == null || IsSelf(msg.Player)) return new(AddressReason.No, "no player");
        var player = msg.Player;
        var text = msg.Text ?? "";
        bool named = msg.Kind != ChatKind.Whisper && NamesUs(text);

        // Another (foreign) bot: only when it names us, never its reply to us, at most once per cooldown.
        if (IsOtherBot(player))
        {
            if (msg.Kind == ChatKind.Whisper) return new(AddressReason.FromBot, "a bot's whisper");
            if (!named) return new(AddressReason.FromBot, "a bot's line that doesn't name us");
            lock (_lock)
                if (_lastTalkedWith.TryGetValue(player, out var t) && Now - t < TimeSpan.FromMinutes(Math.Max(0, _options.BotReplyCooldownMinutes)))
                    return new(AddressReason.FromBot, $"a bot's reply/ping {(int)(Now - t).TotalSeconds}s after we last talked with it (1 per {_options.BotReplyCooldownMinutes:0} min)");
            // Never a bot-to-bot exchange on its own: a human must have addressed us in this exchange.
            if (!AddressedWithin(TimeSpan.FromSeconds(60))) return new(AddressReason.FromBot, "a bot named us but no human is in the exchange");
            return new(AddressReason.Name, "a bot named us while a human talks with us");
        }
        if (msg.Kind == ChatKind.Whisper) return new(AddressReason.Whisper, "whisper");
        if (msg.Kind == ChatKind.Emote) return named ? new(AddressReason.Name, "emote names us") : new(AddressReason.No, "emote");
        if (msg.Kind != ChatKind.Public) return new(AddressReason.No, "not chat");

        if (Rejection.IsMatch(text) && (named || CalledPlayer(text, player) == null)) return new(AddressReason.Rejected, "\"not talking to you\"");
        if (named) return new(AddressReason.Name, "names us");

        int humans;
        DateTimeOffset? ours, asked, rejectUntil;
        (string Other, DateTimeOffset At)? partner;
        bool botAround, botOnline;
        lock (_lock)
        {
            humans = Math.Max(_online.Count(p => !IsOtherBotLocked(p)), 1);
            botOnline = _online.Any(IsOtherBotLocked);
            ours = _lastTalkedWith.TryGetValue(player, out var at) && Now - at < TimeSpan.FromSeconds(_options.ReplyWindowSeconds) ? at : null;
            asked = _askedAt.TryGetValue(player, out var q) && Now - q < TimeSpan.FromSeconds(_options.AnswerWindowSeconds) ? q : null;
            rejectUntil = _rejectUntil.TryGetValue(player, out var r) && r > Now ? r : null;
            partner = _partner.TryGetValue(player, out var pt) && Now - pt.At < TimeSpan.FromSeconds(_options.OtherConversationSeconds) && _online.Contains(pt.Other) ? pt : null;
            botAround = _botSpokeAt.Any(kv => Now - kv.Value < TimeSpan.FromSeconds(90) && _online.Contains(kv.Key))
                        || (partner is { } pp && IsOtherBotLocked(pp.Other));
        }
        if (CalledPlayer(text, player) is { } other) return new(AddressReason.OtherPlayer, $"calls {other}");
        if (rejectUntil != null) return new(AddressReason.Rejected, $"said not talking to us ({(int)(rejectUntil.Value - Now).TotalSeconds}s left)");
        if (humans <= _options.SmallGroupSize && _groupRegex.IsMatch(text)) return new(AddressReason.Group, "group word");

        string? gaze = null;
        try { gaze = LookingAt?.Invoke(player); } catch (Exception) { }
        if (gaze != null && IsSelf(gaze)) return new(AddressReason.Gaze, "looking at us");
        // Content fit: our question to them is newer than their talk with anyone else.
        if (asked is { } a && (partner is not { } p0 || a >= p0.At)) return new(AddressReason.AnswerToUs, "answers our question");
        if (gaze != null) return new(AddressReason.OtherConversation, $"looking at {gaze}");
        if (partner is { } p1 && (ours is not { } o1 || p1.At > o1)) return new(AddressReason.OtherConversation, $"talking with {p1.Other}");
        if (ours != null) return new(AddressReason.Continuity, "continues our conversation");
        if (botAround) return new(AddressReason.Unclear, "unclear and another bot is in the conversation");
        if (_options.SoloPlayerAlwaysAddressed && humans <= 1 && !botOnline) return new(AddressReason.Solo, "only player online");
        if (_options.ImperativesAddressed && !DropBackgroundChat && humans <= _options.SmallGroupSize && AddressingText.IsBotDirected(text))
            return new(AddressReason.Imperative, "unnamed imperative / you");
        return new(AddressReason.No, "not for us");
    }

    // caller holds _lock
    private bool IsOtherBotLocked(string name) =>
        !IsSelf(name) && !IsHive(name) && (_options.OtherBots.Contains(name, StringComparer.OrdinalIgnoreCase) || _knownBots.Contains(name));

    /// <summary>The bot asked these players something (a line with '?'): their next unnamed line is the answer.</summary>
    private void NoteAsked(IEnumerable<string> players, IEnumerable<string> lines)
    {
        if (!lines.Any(l => l.Contains('?'))) return;
        lock (_lock) foreach (var p in players) _askedAt[p] = Now;
    }
}
