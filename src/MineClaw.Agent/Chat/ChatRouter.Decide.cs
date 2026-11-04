namespace MineClaw.Agent.Chat;

/// <summary>
/// <see cref="Decide"/>: the full verdict for a line, in the order the class summary of the addressee part lists (names, gaze,
/// content fit, continuity, unclear). The per-player conversation state is read once under the lock (<see cref="AddressState"/>).
/// </summary>
public sealed partial class ChatRouter
{
    /// <summary>What the router knows about a player's conversations at the moment a line of theirs is decided.</summary>
    /// <param name="Request">Our open request to them: our last line asked/told them something, or an ask_player still waits on them.</param>
    /// <param name="LastToForeign">Their last line went to a foreign bot (named it, used its prefix, continued its thread, or it answered).</param>
    private readonly record struct AddressState(
        int Humans, DateTimeOffset? Ours, DateTimeOffset? Asked, DateTimeOffset? RejectUntil, DateTimeOffset? ToUs, DateTimeOffset? Request,
        (string Other, DateTimeOffset At)? Partner, bool BotAround, bool BotOnline, bool ForeignPartner, bool LastToForeign);

    /// <summary>The full verdict for a line (see the class summary for the order).</summary>
    public AddressDecision Decide(ChatMessage msg)
    {
        if (msg.Player == null || IsSelf(msg.Player)) return new(AddressReason.No, "no player");
        var player = msg.Player;
        var text = msg.Text ?? "";
        bool named = msg.Kind != ChatKind.Whisper && NamesUs(text);

        if (IsOtherBot(player)) return DecideOtherBot(msg, player, named);
        if (msg.Kind == ChatKind.Whisper) return new(AddressReason.Whisper, "whisper");
        if (msg.Kind == ChatKind.Emote) return named ? new(AddressReason.Name, "emote names us") : new(AddressReason.No, "emote");
        if (msg.Kind != ChatKind.Public) return new(AddressReason.No, "not chat");

        if (Rejection.IsMatch(text) && (named || CalledPlayer(text, player) == null)) return new(AddressReason.Rejected, "\"not talking to you\"");
        if (named) return new(AddressReason.Name, "names us");
        return DecideUnnamed(player, text, AddressStateOf(player));
    }

    /// <summary>Another (foreign) bot: only when it names us, never its reply to us, at most once per cooldown.</summary>
    private AddressDecision DecideOtherBot(ChatMessage msg, string player, bool named)
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

    private AddressDecision DecideUnnamed(string player, string text, AddressState s)
    {
        if (CalledPlayer(text, player) is { } other) return new(AddressReason.OtherPlayer, $"calls {other}", other);
        if (s.RejectUntil != null) return new(AddressReason.Rejected, $"said not talking to us ({(int)(s.RejectUntil.Value - Now).TotalSeconds}s left)");
        if (s.Humans <= _options.SmallGroupSize && _groupRegex.IsMatch(text)) return new(AddressReason.Group, "group word");

        string? gaze = null;
        try { gaze = LookingAt?.Invoke(player); } catch (Exception) { }
        if (gaze != null && IsSelf(gaze)) return new(AddressReason.Gaze, "looking at us");
        // Content fit: our question to them is newer than their talk with anyone else.
        if (s.Asked is { } a && (s.Partner is not { } p0 || a >= p0.At)) return new(AddressReason.AnswerToUs, "answers our question");
        if (gaze != null) return new(AddressReason.OtherConversation, $"looking at {gaze}", gaze);
        return DecideByThreads(text, s);
    }

    /// <summary>
    /// Live 10-28 15:36: main asked Robin to hop off the blocks, then "I hopped off it" was dropped as unclear because robinchan
    /// had chatted nearby. Recency decides: our open question/instruction to them wins unless their last line went to the
    /// foreign bot or that bot's thread with them is newer than our request.
    /// </summary>
    private AddressDecision DecideByThreads(string text, AddressState s)
    {
        bool ourRequestOpen = s.Request is { } rq1 && !s.LastToForeign && (s.Partner is not { } pr || !s.ForeignPartner || rq1 >= pr.At);
        if (s.Partner is { } pf && s.ForeignPartner)
        {
            if (ForeignThreadDecision(text, s, pf, ourRequestOpen) is { } foreign) return foreign;
        }
        else if (s.Partner is { } p1 && (s.Ours is not { } o1 || p1.At > o1)) return new(AddressReason.OtherConversation, $"talking with {p1.Other}", p1.Other);
        return DecideFallback(text, s, ourRequestOpen);
    }

    /// <summary>
    /// The player's last addressed line went to a foreign bot (named it, used its prefix, or it answered): an unnamed follow-up
    /// stays with that bot. Our plain talk to them doesn't take it back (a question or instruction newer than the bot's thread
    /// does); their line to us does; a help call is ours. Null = no verdict here.
    /// </summary>
    private AddressDecision? ForeignThreadDecision(string text, AddressState s, (string Other, DateTimeOffset At) pf, bool ourRequestOpen)
    {
        if (ourRequestOpen) return new(AddressReason.AnswerToUs, $"answers our request (newer than {pf.Other}'s thread)");
        if (s.ToUs is { } tu1 && pf.At <= tu1) return null;
        if (!AddressingText.IsHelpCall(text)) return new(AddressReason.OtherConversation, $"continues with {pf.Other} (another bot)", pf.Other);
        if (!DropBackgroundChat) return new(AddressReason.Imperative, $"a help call (not left to {pf.Other})");
        return null;
    }

    private AddressDecision DecideFallback(string text, AddressState s, bool ourRequestOpen)
    {
        if (s.Ours != null) return new(AddressReason.Continuity, "continues our conversation");
        if (ourRequestOpen) return new(AddressReason.AnswerToUs, $"answers our request {(int)(Now - s.Request!.Value).TotalSeconds}s ago");
        if (s.BotAround) return new(AddressReason.Unclear, "unclear and another bot is in the conversation");
        if (_options.SoloPlayerAlwaysAddressed && s.Humans <= 1 && !s.BotOnline) return new(AddressReason.Solo, "only player online");
        if (_options.ImperativesAddressed && !DropBackgroundChat && s.Humans <= _options.SmallGroupSize && AddressingText.IsBotDirected(text))
            return new(AddressReason.Imperative, "unnamed imperative / you");
        return new(AddressReason.No, "not for us");
    }

    // ---------------- the state, read once under the lock

    private AddressState AddressStateOf(string player)
    {
        lock (_lock)
        {
            var (partner, foreignPartner) = PartnerLocked(player);
            return new AddressState(
                Humans: Math.Max(_online.Count(p => !IsOtherBotLocked(p)), 1),
                Ours: RecentLocked(_lastTalkedWith, player, _options.ReplyWindowSeconds),
                Asked: RecentLocked(_askedAt, player, _options.AnswerWindowSeconds),
                RejectUntil: _rejectUntil.TryGetValue(player, out var r) && r > Now ? r : null,
                ToUs: _lastToUs.TryGetValue(player, out var tu) ? tu : null,
                Request: OpenRequestLocked(player),
                Partner: partner,
                BotAround: BotAroundLocked(partner),
                BotOnline: _online.Any(IsOtherBotLocked),
                ForeignPartner: foreignPartner,
                LastToForeign: LastToForeignLocked(player));
        }
    }

    // caller holds _lock: the time in this map when it's younger than the window
    private DateTimeOffset? RecentLocked(Dictionary<string, DateTimeOffset> map, string player, int windowSeconds) =>
        map.TryGetValue(player, out var at) && Now - at < TimeSpan.FromSeconds(windowSeconds) ? at : null;

    // caller holds _lock
    private DateTimeOffset? OpenRequestLocked(string player)
    {
        var request = RecentLocked(_requestAt, player, Math.Max(0, _options.OpenRequestSeconds));
        foreach (var w in _waiters)
            if (w.Parts.Count == 0 && w.Player != null && w.Player.Equals(player, StringComparison.OrdinalIgnoreCase) && (request is not { } r0 || w.Created > r0))
                request = w.Created;
        return request;
    }

    // caller holds _lock
    private bool LastToForeignLocked(string player) =>
        _lastLineTo.TryGetValue(player, out var lt) && lt.To != null && IsOtherBotLocked(lt.To)
        && Now - lt.At < TimeSpan.FromSeconds(Math.Max(_options.OtherConversationSeconds, _options.ReplyWindowSeconds));

    /// <summary>A foreign bot's thread lasts the reply window (its jobs take a while); a human-to-human one the shorter window.</summary>
    private ((string Other, DateTimeOffset At)? Partner, bool Foreign) PartnerLocked(string player)
    {
        bool foreign = _partner.TryGetValue(player, out var pt) && IsOtherBotLocked(pt.Other);
        var window = foreign ? Math.Max(_options.OtherConversationSeconds, _options.ReplyWindowSeconds) : _options.OtherConversationSeconds;
        (string Other, DateTimeOffset At)? partner = pt.Other != null && Now - pt.At < TimeSpan.FromSeconds(window) && _online.Contains(pt.Other) ? pt : null;
        return (partner, foreign);
    }

    // caller holds _lock
    private bool BotAroundLocked((string Other, DateTimeOffset At)? partner) =>
        _botSpokeAt.Any(kv => Now - kv.Value < TimeSpan.FromSeconds(90) && _online.Contains(kv.Key))
        || (partner is { } pp && IsOtherBotLocked(pp.Other));
}
