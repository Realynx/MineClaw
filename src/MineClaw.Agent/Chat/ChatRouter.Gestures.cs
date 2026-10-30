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
        try
        {
            if (OwnedElsewhere?.Invoke(new ChatMessage(ChatKind.Public, player, text, text)) == true) return GestureAnswer.None;
        }
        catch (Exception) { }
        Waiter? done = null;
        var result = GestureAnswer.None;
        lock (_lock)
        {
            if (_rejectUntil.TryGetValue(player, out var r) && r > Now) return GestureAnswer.None;
            var w = _waiters.FirstOrDefault(x => x.Parts.Count == 0 && x.Player != null && x.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
                    ?? (atMe ? _waiters.FirstOrDefault(x => x.Parts.Count == 0 && x.Player == null) : null);
            if (w != null)
            {
                w.From = player;
                w.First = Now;
                w.Parts.Add(text);
                _waiters.Remove(w);
                done = w;
                result = GestureAnswer.Waiter;
            }
            else if (_askedAt.TryGetValue(player, out var q) && Now - q <= TimeSpan.FromSeconds(Math.Max(0, _options.GestureAnswerSeconds)))
            {
                _askedAt.Remove(player);
                _requestAt.Remove(player);
                result = GestureAnswer.Question;
            }
            if (result != GestureAnswer.None)
            {
                _lastTalkedWith[player] = Now;
                _lastToUs[player] = Now;
            }
        }
        if (done != null) Complete(done);
        return result;
    }

    /// <summary>The bot asked this player something within <see cref="ChatOptions.GestureAnswerSeconds"/> or an ask waits on them.</summary>
    public bool AwaitsAnswerFrom(string player)
    {
        lock (_lock)
            return _waiters.Any(x => x.Parts.Count == 0 && x.Player != null && x.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
                   || (_askedAt.TryGetValue(player, out var q) && Now - q <= TimeSpan.FromSeconds(Math.Max(0, _options.GestureAnswerSeconds)));
    }
}
