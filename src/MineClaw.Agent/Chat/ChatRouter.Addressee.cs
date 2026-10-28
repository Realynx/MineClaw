using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary>The router's verdict on one line: why, plus a short human-readable detail for the log, and whom the line is for
/// when it's someone else (<paramref name="To"/>: the login it calls, looks at or continues with).</summary>
public readonly record struct AddressDecision(AddressReason Reason, string Why, string? To = null)
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

    // Foreign-bot threads (live 10-28 14:41: "c trees" to robinchan, then "get an axe, cut them down like real players" taken by
    // main): a human's last line not for us, which a foreign bot answering within BotAnswerSeconds puts in that bot's thread;
    // command prefixes ("c ") learned from prefixed lines a bot answered; when each player last addressed us.
    private const int BotAnswerSeconds = 5, PrefixHitsToLearn = 2;
    private (string Player, DateTimeOffset At, bool ToUs, string? To, string? Prefix)? _lastHumanLine;
    private readonly Dictionary<string, Dictionary<string, int>> _prefixHits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastToUs = new(StringComparer.OrdinalIgnoreCase);
    // Our open question/instruction to each player, and whom each player's last line went to (a login; us = our username).
    private readonly Dictionary<string, DateTimeOffset> _requestAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset At, string? To)> _lastLineTo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex PrefixWord = new(@"^\s*(?<p>[A-Za-z]{1,2})\s+[A-Za-z0-9]", Rx);
    private static readonly Regex PrefixMark = new(@"^\s*(?<p>[!#$%&.~>+=-])[A-Za-z]", Rx);
    private static readonly HashSet<string> NotPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "a", "u", "k", "y", "n", "ok", "oh", "no", "so", "go", "hi", "yo", "ya", "ye", "is", "it", "do", "to", "we", "me", "my",
        "up", "if", "an", "at", "be", "by", "in", "of", "on", "or", "ty", "gg", "xd", "ur", "im", "hm", "uh", "um", "ah", "eh", "wb",
        "gj", "gn", "he", "us", "as", "am", "aw", "ew", "np", "nm", "ig", "bc", "rn", "tf", "jk", "lo", "ha",
    };

    /// <summary>A line's command-prefix candidate ("c " in "c trees", "!" in "!help"), lower-case; null = none.</summary>
    internal static string? CommandPrefix(string text)
    {
        if (PrefixWord.Match(text ?? "") is { Success: true } w && !NotPrefixes.Contains(w.Groups["p"].Value)) return w.Groups["p"].Value.ToLowerInvariant() + " ";
        if (PrefixMark.Match(text ?? "") is { Success: true } m) return m.Groups["p"].Value;
        return null;
    }

    /// <summary>Command prefixes known for a foreign bot: learned (players' lines starting with it that the bot answered) or
    /// confirmed (a trusted player's "when I say c, I'm talking to robinchan", the bot's own "say c help").</summary>
    public IReadOnlyList<string> CommandPrefixesOf(string bot)
    {
        lock (_lock)
        {
            var list = _prefixHits.TryGetValue(bot, out var hits) ? hits.Where(kv => kv.Value >= PrefixHitsToLearn).Select(kv => kv.Key).ToList() : [];
            if (_confirmedPrefixes.TryGetValue(bot, out var c)) list.AddRange(c.Where(p => !list.Contains(p)));
            return list;
        }
    }

    // Known prefixes survive restarts (live 10-28 15:45: "c trees" woke main again after a restart wiped the learned "c "):
    // the runtime persists every change (CommandPrefixStore) and restores them on start.
    private readonly Dictionary<string, HashSet<string>> _confirmedPrefixes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A prefix count or confirmation changed: (player who used/declared it, or null; bot; prefix; hits; confirmed).
    /// Never raised for test chat.</summary>
    public event Action<string?, string, string, int, bool>? PrefixLearned;

    /// <summary>Restores a persisted prefix (startup): the bot counts as a foreign bot from now on.</summary>
    public void RestorePrefix(string bot, string prefix, int hits, bool confirmed)
    {
        if (string.IsNullOrWhiteSpace(bot) || string.IsNullOrEmpty(prefix) || IsSelf(bot) || IsHive(bot)) return;
        lock (_lock)
        {
            _knownBots.Add(bot);
            if (hits > 0)
            {
                var h = _prefixHits.TryGetValue(bot, out var ph) ? ph : _prefixHits[bot] = new(StringComparer.OrdinalIgnoreCase);
                h[prefix] = Math.Max(h.GetValueOrDefault(prefix), hits);
            }
            if (confirmed) (_confirmedPrefixes.TryGetValue(bot, out var c) ? c : _confirmedPrefixes[bot] = new(StringComparer.OrdinalIgnoreCase)).Add(prefix);
        }
    }

    // "when I say c, I'm talking to robinchan", "I'm talking to robinchan when I say c" (live 10-28 00:24 / 14:51), and a stored
    // rule's paraphrase ("when kiwi says c she's talking to robinchan").
    private const string PrefixToken = @"[""'`]?(?<p>[A-Za-z]{1,2}(?![A-Za-z0-9])|[!#$%&.~>+=-])[""'`]?";
    private const string SaySubject = @"(?:i|she|he|they|[A-Za-z0-9_]{2,16})\s+says?";
    private const string TalksTo = @"(?:(?:i'?m|im|i\s+am|she'?s|he'?s|they'?re|she\s+is|he\s+is|[A-Za-z0-9_]{2,16}\s+is)\s+(?:talking|speaking)\s+to|(?:it'?s|its|that'?s|thats)\s+(?:for|to)|i\s+mean|it\s+means)";
    private const string BotToken = @"@?(?<bot>[A-Za-z0-9_-]{2,16})";
    private static readonly Regex PrefixRule = new(
        @"\bwhen(?:ever)?\s+" + SaySubject + @"\s+" + PrefixToken + @"\s*,?\s*(?:then\s+)?" + TalksTo + @"\s+" + BotToken +
        @"|\b" + TalksTo + @"\s+" + BotToken + @"\s*,?\s*when(?:ever)?\s+" + SaySubject + @"\s+" + PrefixToken + @"(?![A-Za-z0-9])", Rx);
    private static readonly Regex PrefixAd = new(@"\bsay\s+[""'`]?(?:(?<p>[A-Za-z]{1,2})\s+|(?<p>[!#$%&.~>+=-]))(?:help|commands|list)\b", Rx);

    /// <summary>
    /// A trusted player's standing rule naming a foreign bot's command prefix ("when I say c, I'm talking to robinchan"):
    /// registers it at once, no observed answers needed. True when the text was such a rule.
    /// </summary>
    public bool LearnPrefixRule(string player, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || PrefixRule.Match(text) is not { Success: true } m) return false;
        var prefix = NormalizePrefix(m.Groups["p"].Value);
        string? bot;
        lock (_lock) bot = PrefixTargetLocked(m.Groups["bot"].Value);
        if (prefix == null || bot == null) return false;
        ConfirmPrefix(player, bot, prefix);
        return true;
    }

    private static string? NormalizePrefix(string p) =>
        p.Length == 0 ? null : char.IsLetter(p[0]) ? (NotPrefixes.Contains(p) ? null : p.ToLowerInvariant() + " ") : p;

    // caller holds _lock: the foreign bot a prefix rule names (a configured/known bot, or an online non-regular account)
    private string? PrefixTargetLocked(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bot = _options.OtherBots.FirstOrDefault(b => b.Equals(name, StringComparison.OrdinalIgnoreCase) || PlayerNames.Key(b) == PlayerNames.Key(name))
                  ?? _knownBots.FirstOrDefault(b => b.Equals(name, StringComparison.OrdinalIgnoreCase) || PlayerNames.Key(b) == PlayerNames.Key(name))
                  ?? ResolveLocked(name);
        if (bot == null || IsSelf(bot) || IsHive(bot)) return null;
        if (IsOtherBotLocked(bot)) return bot;
        try { if (IsRegular?.Invoke(bot) == true) return null; } catch (Exception) { return null; } // a human friend: not a command bot
        _knownBots.Add(bot);
        return bot;
    }

    private void ConfirmPrefix(string? player, string bot, string prefix)
    {
        bool added;
        lock (_lock) added = (_confirmedPrefixes.TryGetValue(bot, out var c) ? c : _confirmedPrefixes[bot] = new(StringComparer.OrdinalIgnoreCase)).Add(prefix);
        if (added) RaisePrefix(player, bot, prefix, hits: 0, confirmed: true);
    }

    private void RaisePrefix(string? player, string bot, string prefix, int hits, bool confirmed)
    {
        if (_injectingTest) return; // test chat never persists
        try { PrefixLearned?.Invoke(player, bot, prefix, hits, confirmed); } catch (Exception) { }
    }

    // caller holds _lock: the foreign bot whose known prefix this line uses (confirmed first, then most answered). Online or not:
    // a line in a bot's command syntax never wakes main.
    private string? PrefixBotLocked(string text)
    {
        if (CommandPrefix(text) is not { } prefix) return null;
        var confirmed = _confirmedPrefixes.Where(kv => kv.Value.Contains(prefix) && IsOtherBotLocked(kv.Key)).Select(kv => kv.Key)
            .OrderByDescending(b => _online.Contains(b)).FirstOrDefault();
        if (confirmed != null) return confirmed;
        return _prefixHits.Where(kv => IsOtherBotLocked(kv.Key) && kv.Value.GetValueOrDefault(prefix) >= PrefixHitsToLearn)
            .OrderByDescending(kv => _online.Contains(kv.Key)).ThenByDescending(kv => kv.Value[prefix]).Select(kv => kv.Key).FirstOrDefault();
    }

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

    /// <summary>
    /// A bot line about another online account, called by a regular's nickname hidden in its name ("robin" for robinchan) while
    /// the turn's waking events concern that account and not the regular: the line names the account (<see cref="PlayerNames.Unfold"/>).
    /// </summary>
    public string UnfoldNames(string text, IEnumerable<Events.GameEvent> turn)
    {
        string[] online;
        lock (_lock) online = _online.ToArray();
        var waking = turn.Where(e => e.Wakes).Select(e => (e.Player, e.Text)).ToList();
        return PlayerNames.Unfold(text, waking, online, NicknamesOf);
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
        string[] all;
        lock (_lock) all = _online.ToArray();
        var online = all.Where(p => !p.Equals(speaker, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Exact accounts first ("robin-chan look" is robinchan), then nicknames in what is left (never "robin" inside it).
        var canon = PlayerNames.Canonical(text, all);
        foreach (var p in online)
            if (AddressingText.CallsLogin(canon, p)) return p;
        var bare = PlayerNames.WithoutLogins(canon, all);
        foreach (var p in online)
            foreach (var n in NicknamesOf(p))
                if (AddressingText.Vocative(bare, n)) return p;
        // A foreign bot's learned command prefix ("c trees" is robinchan's), from a human only.
        if (IsOtherBot(speaker)) return null;
        lock (_lock) return PrefixBotLocked(text);
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
        var prefixes = new List<(string? Player, string Bot, string Prefix, int Hits)>();
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

            // --- a foreign bot answering a human's line that wasn't ours: that line (and the player's thread) went to the bot,
            // and its command prefix counts one hit ("c help" answered with robinchan's list).
            if (IsOtherBotLocked(player) && !namesUs && _lastHumanLine is { ToUs: false } h && now - h.At <= TimeSpan.FromSeconds(BotAnswerSeconds)
                && (h.To == null || h.To.Equals(player, StringComparison.OrdinalIgnoreCase)))
            {
                _partner[h.Player] = (player, now);
                _partner[player] = (h.Player, now);
                _lastLineTo[h.Player] = (h.At, player);
                if (h.Prefix != null)
                {
                    var hits = _prefixHits.TryGetValue(player, out var ph) ? ph : _prefixHits[player] = new(StringComparer.OrdinalIgnoreCase);
                    int n = hits[h.Prefix] = hits.GetValueOrDefault(h.Prefix) + 1;
                    prefixes.Add((h.Player, player, h.Prefix, n));
                }
                _lastHumanLine = null; // one answer per line
            }

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
                // This player answering someone who just talked to them (robinchan answering Robin): that thread goes on, unless
                // this player has talked to us since ("c trees", "maid_chan get an axe", then "and some sticks" is ours).
                var toUsAt = _lastToUs.TryGetValue(player, out var tus) ? tus : DateTimeOffset.MinValue;
                // ...or we asked/told them something since (recency: the newest exchange is the one they answer).
                if (_requestAt.TryGetValue(player, out var rqa) && rqa > toUsAt) toUsAt = rqa;
                foreach (var (a, pa) in _partner.Where(kv => !kv.Key.Equals(player, StringComparison.OrdinalIgnoreCase)
                             && kv.Value.Other.Equals(player, StringComparison.OrdinalIgnoreCase) && now - kv.Value.At <= TimeSpan.FromSeconds(20)
                             && kv.Value.At > toUsAt).ToList())
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
                _requestAt.Remove(player);
            }
        }
        foreach (var (name, why) in detected)
            try { BotDetected?.Invoke(name, why); } catch (Exception) { }
        foreach (var (who, bot, prefix, hits) in prefixes) RaisePrefix(who, bot, prefix, hits, confirmed: false);
        // A foreign bot advertising its syntax ("say c help for my list", live 13:49) or a trusted player's rule for it.
        if (IsOtherBot(player))
        {
            if (PrefixAd.Match(text) is { Success: true } ad && NormalizePrefix(ad.Groups["p"].Value) is { } p) ConfirmPrefix(null, player, p);
        }
        else if (IsRegular?.Invoke(player) == true) LearnPrefixRule(player, text);
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
        var login = _online.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? _online.FirstOrDefault(p => PlayerNames.Key(p) == PlayerNames.Key(name)); // exact accounts win over nicknames
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
        DateTimeOffset? ours, asked, rejectUntil, toUs, request;
        (string Other, DateTimeOffset At)? partner;
        bool botAround, botOnline, foreignPartner, lastToForeign;
        lock (_lock)
        {
            // Our open request to them: our last line asked/told them something, or an ask_player still waits on them.
            request = _requestAt.TryGetValue(player, out var rq) && Now - rq < TimeSpan.FromSeconds(Math.Max(0, _options.OpenRequestSeconds)) ? rq : null;
            foreach (var w in _waiters)
                if (w.Parts.Count == 0 && w.Player != null && w.Player.Equals(player, StringComparison.OrdinalIgnoreCase) && (request is not { } r0 || w.Created > r0))
                    request = w.Created;
            // Their last line went to a foreign bot (named it, used its prefix, continued its thread, or it answered).
            lastToForeign = _lastLineTo.TryGetValue(player, out var lt) && lt.To != null && IsOtherBotLocked(lt.To)
                            && Now - lt.At < TimeSpan.FromSeconds(Math.Max(_options.OtherConversationSeconds, _options.ReplyWindowSeconds));
            humans = Math.Max(_online.Count(p => !IsOtherBotLocked(p)), 1);
            botOnline = _online.Any(IsOtherBotLocked);
            ours = _lastTalkedWith.TryGetValue(player, out var at) && Now - at < TimeSpan.FromSeconds(_options.ReplyWindowSeconds) ? at : null;
            asked = _askedAt.TryGetValue(player, out var q) && Now - q < TimeSpan.FromSeconds(_options.AnswerWindowSeconds) ? q : null;
            rejectUntil = _rejectUntil.TryGetValue(player, out var r) && r > Now ? r : null;
            // A foreign bot's thread lasts the reply window (its jobs take a while); a human-to-human one the shorter window.
            foreignPartner = _partner.TryGetValue(player, out var pt) && IsOtherBotLocked(pt.Other);
            var partnerWindow = foreignPartner ? Math.Max(_options.OtherConversationSeconds, _options.ReplyWindowSeconds) : _options.OtherConversationSeconds;
            partner = pt.Other != null && Now - pt.At < TimeSpan.FromSeconds(partnerWindow) && _online.Contains(pt.Other) ? pt : null;
            toUs = _lastToUs.TryGetValue(player, out var tu) ? tu : null;
            botAround = _botSpokeAt.Any(kv => Now - kv.Value < TimeSpan.FromSeconds(90) && _online.Contains(kv.Key))
                        || (partner is { } pp && IsOtherBotLocked(pp.Other));
        }
        if (CalledPlayer(text, player) is { } other) return new(AddressReason.OtherPlayer, $"calls {other}", other);
        if (rejectUntil != null) return new(AddressReason.Rejected, $"said not talking to us ({(int)(rejectUntil.Value - Now).TotalSeconds}s left)");
        if (humans <= _options.SmallGroupSize && _groupRegex.IsMatch(text)) return new(AddressReason.Group, "group word");

        string? gaze = null;
        try { gaze = LookingAt?.Invoke(player); } catch (Exception) { }
        if (gaze != null && IsSelf(gaze)) return new(AddressReason.Gaze, "looking at us");
        // Content fit: our question to them is newer than their talk with anyone else.
        if (asked is { } a && (partner is not { } p0 || a >= p0.At)) return new(AddressReason.AnswerToUs, "answers our question");
        if (gaze != null) return new(AddressReason.OtherConversation, $"looking at {gaze}", gaze);
        // Live 10-28 15:36: main asked Robin to hop off the blocks, then "I hopped off it" was dropped as unclear because robinchan
        // had chatted nearby. Recency decides: our open question/instruction to them wins unless their last line went to the
        // foreign bot or that bot's thread with them is newer than our request.
        bool ourRequestOpen = request is { } rq1 && !lastToForeign && (partner is not { } pr || !foreignPartner || rq1 >= pr.At);
        if (partner is { } pf && foreignPartner)
        {
            // The player's last addressed line went to a foreign bot (named it, used its prefix, or it answered): an unnamed
            // follow-up stays with that bot. Our plain talk to them doesn't take it back (a question or instruction newer than the
            // bot's thread does); their line to us does; a help call is ours.
            if (ourRequestOpen) return new(AddressReason.AnswerToUs, $"answers our request (newer than {pf.Other}'s thread)");
            if (toUs is not { } tu1 || pf.At > tu1)
            {
                if (!AddressingText.IsHelpCall(text)) return new(AddressReason.OtherConversation, $"continues with {pf.Other} (another bot)", pf.Other);
                if (!DropBackgroundChat) return new(AddressReason.Imperative, $"a help call (not left to {pf.Other})");
            }
        }
        else if (partner is { } p1 && (ours is not { } o1 || p1.At > o1)) return new(AddressReason.OtherConversation, $"talking with {p1.Other}", p1.Other);
        if (ours != null) return new(AddressReason.Continuity, "continues our conversation");
        if (ourRequestOpen) return new(AddressReason.AnswerToUs, $"answers our request {(int)(Now - request!.Value).TotalSeconds}s ago");
        if (botAround) return new(AddressReason.Unclear, "unclear and another bot is in the conversation");
        if (_options.SoloPlayerAlwaysAddressed && humans <= 1 && !botOnline) return new(AddressReason.Solo, "only player online");
        if (_options.ImperativesAddressed && !DropBackgroundChat && humans <= _options.SmallGroupSize && AddressingText.IsBotDirected(text))
            return new(AddressReason.Imperative, "unnamed imperative / you");
        return new(AddressReason.No, "not for us");
    }

    // caller holds _lock
    private bool IsOtherBotLocked(string name) =>
        !IsSelf(name) && !IsHive(name) && (_options.OtherBots.Contains(name, StringComparer.OrdinalIgnoreCase) || _knownBots.Contains(name));

    /// <summary>
    /// The bot asked these players something (a line with '?'): their next unnamed line is the answer. A question or an
    /// instruction ("hop off it when i get there pls") also opens a request (<see cref="ChatOptions.OpenRequestSeconds"/>) that
    /// their reply to us closes.
    /// </summary>
    private void NoteAsked(IEnumerable<string> players, IEnumerable<string> lines)
    {
        var list = lines.ToList();
        bool asked = list.Any(l => l.Contains('?'));
        if (!asked && !list.Any(IsRequestLine)) return;
        lock (_lock)
            foreach (var p in players)
            {
                if (asked) _askedAt[p] = Now;
                _requestAt[p] = Now;
            }
    }

    private static readonly Regex RequestRx = new(
        @"(?:^|[,.!~;:]\s*)\W*(?:(?:ok(?:ay)?|now|and|also|so|then|just|pls|plz|please|hey|oh|ooh|ty)\W+)*" +
        @"(?:hop|step|get|go|come|stand|stay|wait|move|punch|hit|say|type|tell|show|give|drop|toss|throw|bring|hand|put|place|pick|" +
        @"point|look|open|close|let|lead|follow|grab|fetch|take|check|try|keep|stop|jump|walk|run|back|climb|meet|answer)\b" +
        @"|\b(?:pls|plz|please|can\s+(?:you|u)|could\s+(?:you|u)|would\s+(?:you|u)|need\s+(?:you|u)\s+to|lmk|let\s+me\s+know)\b", Rx);

    /// <summary>A bot line that asks the player to do something (a request they will answer or report back on).</summary>
    internal static bool IsRequestLine(string line) => RequestRx.IsMatch(line ?? "");
}
