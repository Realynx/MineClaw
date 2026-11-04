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
