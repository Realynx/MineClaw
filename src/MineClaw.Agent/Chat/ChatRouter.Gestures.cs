namespace MineClaw.Agent.Chat;

/// <summary>What a player's nod or head shake answered.</summary>
public enum GestureAnswer
{
    /// <summary>Nothing was asked: plain body language.</summary>
    None,
    /// <summary>An ask_player (or a script's / the acquisition planner's ask) waiting on them: completed with "yes (nod)".</summary>
    Waiter,
    /// <summary>The bot's own chat question to them (a line with '?' within <see cref="ChatOptions.GestureAnswerSeconds"/>).</summary>
    Question,
}

public sealed partial class ChatRouter
{
    /// <summary>The text a nod / head shake answers a waiter with (ReplyIntent and GestureClassifier.IsYesNo read it as yes/no).</summary>
    public static string GestureReply(bool yes) => yes ? "yes (nod)" : "no (head shake)";

    /// <summary>
    /// A player nodded (<paramref name="yes"/>) or shook their head at the bot (Fox 10-29: "nodding my head yes or no"): the
    /// universal yes/no, mapped onto what is pending. Their own ask_player waiter (or an "anyone" one when the gesture was aimed
    /// at us) gets <see cref="GestureReply"/> at once, no debounce: a gesture is one whole answer. Else our chat question to them
    /// within <see cref="ChatOptions.GestureAnswerSeconds"/> is answered: that open question and request close (a second nod is
    /// no second answer) and the caller wakes the brain with it. Hive: a thread another bot owns keeps its replies.
    /// </summary>
    public GestureAnswer AnswerGesture(string player, bool yes, bool atMe)
    {
        if (string.IsNullOrWhiteSpace(player) || IsSelf(player) || IsHive(player)) return GestureAnswer.None;
        var text = GestureReply(yes);
        if (GestureOwnedElsewhere(player, text)) return GestureAnswer.None;
        Waiter? done;
        GestureAnswer result;
        lock (_lock) result = AnswerGestureLocked(player, text, atMe, out done);
        if (done != null) Complete(done);
        return result;
    }

    /// <summary>Hive: a thread another bot owns keeps its replies (a failing hook owns nothing).</summary>
    private bool GestureOwnedElsewhere(string player, string text)
    {
        try { return OwnedElsewhere?.Invoke(new ChatMessage(ChatKind.Public, player, text, text)) == true; }
        catch (Exception) { return false; }
    }

    /// <summary>The gesture answers a waiter (handed back in <paramref name="done"/>), else our open question, else nothing.</summary>
    private GestureAnswer AnswerGestureLocked(string player, string text, bool atMe, out Waiter? done)
    {
        done = null;
        if (_rejectUntil.TryGetValue(player, out var r) && r > Now) return GestureAnswer.None;
        var result = TakeGestureWaiterLocked(player, text, atMe, out done) ? GestureAnswer.Waiter
            : CloseQuestionLocked(player) ? GestureAnswer.Question
            : GestureAnswer.None;
        if (result != GestureAnswer.None)
        {
            _lastTalkedWith[player] = Now;
            _lastToUs[player] = Now;
        }
        return result;
    }

    /// <summary>Their own waiter, or an "anyone" one when the gesture was aimed at us, gets the gesture as its whole reply.</summary>
    private bool TakeGestureWaiterLocked(string player, string text, bool atMe, out Waiter? done)
    {
        done = _waiters.FirstOrDefault(x => x.Parts.Count == 0 && x.Player != null && x.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
               ?? (atMe ? _waiters.FirstOrDefault(x => x.Parts.Count == 0 && x.Player == null) : null);
        if (done == null) return false;
        done.From = player;
        done.First = Now;
        done.Parts.Add(text);
        _waiters.Remove(done);
        return true;
    }

    /// <summary>Our chat question to them within the answer window: closed (with its request), so a second nod is no second answer.</summary>
    private bool CloseQuestionLocked(string player)
    {
        if (!(_askedAt.TryGetValue(player, out var q) && Now - q <= TimeSpan.FromSeconds(Math.Max(0, _options.GestureAnswerSeconds)))) return false;
        _askedAt.Remove(player);
        _requestAt.Remove(player);
        return true;
    }

    /// <summary>The bot asked this player something within <see cref="ChatOptions.GestureAnswerSeconds"/> or an ask waits on them.</summary>
    public bool AwaitsAnswerFrom(string player)
    {
        lock (_lock)
            return _waiters.Any(x => x.Parts.Count == 0 && x.Player != null && x.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
                   || (_askedAt.TryGetValue(player, out var q) && Now - q <= TimeSpan.FromSeconds(Math.Max(0, _options.GestureAnswerSeconds)));
    }
}
