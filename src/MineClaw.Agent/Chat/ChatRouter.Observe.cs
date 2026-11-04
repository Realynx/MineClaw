using System.Text.RegularExpressions;

namespace MineClaw.Agent.Chat;

/// <summary><see cref="Observe"/>: what the router learns from a player line before it is decided.</summary>
public sealed partial class ChatRouter
{
    private sealed record ObservedLine(string Player, string Text, DateTimeOffset Now, bool NamesUs, string? Called)
    {
        public List<(string Name, string Why)> Detected { get; } = new();
        public List<(string? Player, string Bot, string Prefix, int Hits)> Prefixes { get; } = new();
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
        var line = new ObservedLine(player, text, now, namesUs, namesUs ? null : CalledPlayer(text, player));
        lock (_lock)
        {
            DetectBotsLocked(line);
            NoteForeignAnswerLocked(line);
            LearnNicknamesLocked(line);
            NoteThreadsLocked(line);
            // --- "I'm not talking to you"
            if (Rejection.IsMatch(text) && (namesUs || line.Called == null)) RejectLocked(player, line.Now);
        }
        foreach (var (name, why) in line.Detected)
            try { BotDetected?.Invoke(name, why); } catch (Exception) { }
        foreach (var (who, bot, prefix, hits) in line.Prefixes) RaisePrefix(who, bot, prefix, hits, confirmed: false);
        // A foreign bot advertising its syntax ("say c help for my list", live 13:49) or a trusted player's rule for it.
        if (IsOtherBot(player))
        {
            if (PrefixAd.Match(text) is { Success: true } ad && NormalizePrefix(ad.Groups["p"].Value) is { } p) ConfirmPrefix(null, player, p);
        }
        else if (IsRegular?.Invoke(player) == true) LearnPrefixRule(player, text);
    }

    // --- bots (caller holds _lock)
    private void DetectBotsLocked(ObservedLine line)
    {
        DetectNamedBotsLocked(line);
        DetectChattyBotLocked(line);
        _lastHeardLine = (line.Player, line.Text, line.Now);
        if (_options.OtherBots.Contains(line.Player, StringComparer.OrdinalIgnoreCase) || _knownBots.Contains(line.Player)) _botSpokeAt[line.Player] = line.Now;
    }

    /// <summary>"robinchan is a bot", "I'm a bot".</summary>
    private void DetectNamedBotsLocked(ObservedLine line)
    {
        foreach (Match m in IsABot.Matches(line.Text))
        {
            var who = ResolveLocked(m.Groups["name"].Value);
            if (who != null && !IsSelf(who) && !IsHive(who) && _knownBots.Add(who)) line.Detected.Add((who, $"{line.Player} said \"{TextUtilTruncate(m.Value, 40)}\""));
        }
        if (SelfBot.IsMatch(line.Text) && _knownBots.Add(line.Player)) line.Detected.Add((line.Player, "says it is a bot"));
    }

    /// <summary>Fast long replies to someone else, and template chatter on top of an instant reply.</summary>
    private void DetectChattyBotLocked(ObservedLine line)
    {
        var (player, text, now) = (line.Player, line.Text, line.Now);
        if (_lastHeardLine is { } prev && !prev.Player.Equals(player, StringComparison.OrdinalIgnoreCase)
            && now - prev.At <= TimeSpan.FromSeconds(2) && text.Length >= 25)
        {
            int n = _fastReplies[player] = _fastReplies.GetValueOrDefault(player) + 1;
            if (n >= 2 && _knownBots.Add(player)) line.Detected.Add((player, $"{n} long replies within 2 s"));
        }
        if (TemplateMark.Matches(text).Count >= 2)
        {
            int n = _templateLines[player] = _templateLines.GetValueOrDefault(player) + 1;
            if (n >= 3 && _fastReplies.GetValueOrDefault(player) >= 1 && _knownBots.Add(player)) line.Detected.Add((player, "template chatter + instant replies"));
        }
    }

    /// <summary>
    /// A foreign bot answering a human's line that wasn't ours: that line (and the player's thread) went to the bot, and its
    /// command prefix counts one hit ("c help" answered with robinchan's list). Caller holds _lock.
    /// </summary>
    private void NoteForeignAnswerLocked(ObservedLine line)
    {
        var (player, now) = (line.Player, line.Now);
        if (!IsOtherBotLocked(player) || line.NamesUs || _lastHumanLine is not { ToUs: false } h || now - h.At > TimeSpan.FromSeconds(BotAnswerSeconds)
            || (h.To != null && !h.To.Equals(player, StringComparison.OrdinalIgnoreCase)))
            return;
        _partner[h.Player] = (player, now);
        _partner[player] = (h.Player, now);
        _lastLineTo[h.Player] = (h.At, player);
        if (h.Prefix != null)
        {
            var hits = _prefixHits.TryGetValue(player, out var ph) ? ph : _prefixHits[player] = new(StringComparer.OrdinalIgnoreCase);
            int n = hits[h.Prefix] = hits.GetValueOrDefault(h.Prefix) + 1;
            line.Prefixes.Add((h.Player, player, h.Prefix, n));
        }
        _lastHumanLine = null; // one answer per line
    }

    /// <summary>Nicknames ("robinchan, I'm gonna call you c" → c = robinchan; "I'm robin-sama to you" → kiwi). Caller holds _lock.</summary>
    private void LearnNicknamesLocked(ObservedLine line)
    {
        if (CallYou.Match(line.Text) is { Success: true } cy)
        {
            var nick = cy.Groups["nick"].Value;
            var target = NickTargetLocked(line);
            if (target != null && !IsStopWord(nick))
            {
                if (IsSelf(target)) _selfNicks.Add(nick);
                else AddLearnedNickLocked(target, nick);
            }
        }
        if (CallMe.Match(line.Text) is { Success: true } cm && !IsStopWord(cm.Groups["nick"].Value)) AddLearnedNickLocked(line.Player, cm.Groups["nick"].Value);
    }

    // caller holds _lock: whom "I'm gonna call you X" names (us, the player it calls, or their conversation partner)
    private string? NickTargetLocked(ObservedLine line)
    {
        if (line.NamesUs) return _options.Username;
        return line.Called
            ?? (_partner.TryGetValue(line.Player, out var pt) && line.Now - pt.At <= TimeSpan.FromSeconds(_options.OtherConversationSeconds) ? pt.Other : null);
    }

    // caller holds _lock
    private void AddLearnedNickLocked(string player, string nick) =>
        (_learnedNicks.TryGetValue(player, out var set) ? set : _learnedNicks[player] = new(StringComparer.OrdinalIgnoreCase)).Add(nick);

    /// <summary>Who is talking with whom. Caller holds _lock.</summary>
    private void NoteThreadsLocked(ObservedLine line)
    {
        var (player, now) = (line.Player, line.Now);
        if (line.Called is { } called)
        {
            _partner[player] = (called, now);
            _partner[called] = (player, now);
        }
        else if (!line.NamesUs) ContinueThreadsLocked(player, now);
    }

    /// <summary>
    /// This player answering someone who just talked to them (robinchan answering Robin): that thread goes on, unless this player
    /// has talked to us since ("c trees", "maid_chan get an axe", then "and some sticks" is ours) — or we asked/told them something
    /// since (recency: the newest exchange is the one they answer). Caller holds _lock.
    /// </summary>
    private void ContinueThreadsLocked(string player, DateTimeOffset now)
    {
        var toUsAt = _lastToUs.TryGetValue(player, out var tus) ? tus : DateTimeOffset.MinValue;
        if (_requestAt.TryGetValue(player, out var rqa) && rqa > toUsAt) toUsAt = rqa;
        foreach (var (a, _) in _partner.Where(kv => !kv.Key.Equals(player, StringComparison.OrdinalIgnoreCase)
                     && kv.Value.Other.Equals(player, StringComparison.OrdinalIgnoreCase) && now - kv.Value.At <= TimeSpan.FromSeconds(20)
                     && kv.Value.At > toUsAt).ToList())
        {
            _partner[a] = (player, now);
            _partner[player] = (a, now);
        }
    }

    // caller holds _lock
    private void RejectLocked(string player, DateTimeOffset now)
    {
        _rejectUntil[player] = now + TimeSpan.FromSeconds(Math.Max(0, _options.RejectWindowSeconds));
        _lastTalkedWith.Remove(player);
        _askedAt.Remove(player);
        _requestAt.Remove(player);
    }
}
