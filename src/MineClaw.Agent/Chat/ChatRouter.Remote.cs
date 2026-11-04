namespace MineClaw.Agent.Chat;

/// <summary>
/// A trusted player's line from outside the game (the web viewer), addressed to one bot. The caller (the supervisor's viewer
/// route) has already checked the player against TrustedPlayers and its own token; <paramref name="AllowCommands"/> is true only
/// when the request and the host config both allow command grants.
/// </summary>
/// <param name="Origin">Provenance tag carried by the events and chat_log (<c>"viewer"</c>).</param>
/// <param name="Target">The bot (login) or body the line is for: exactly that bot's router takes it.</param>
public sealed record RemoteChat(string Player, string Text, string Origin, string Target, bool AllowCommands = false, string? RequestId = null);

/// <summary>
/// Remote chat (<see cref="OnRemoteChat"/>): the sibling of <see cref="OnServerChat(string)"/> for lines that never went through
/// the server. The <see cref="ChatMessage"/> is built directly (never a fabricated <c>&lt;name&gt;</c> raw line, which
/// <see cref="CommandGate"/> treats as proof), it is addressed by construction to <see cref="RemoteChat.Target"/> (no
/// addressing guesswork, no hive arbiter, no thread lease elsewhere), the player is not marked online, it has its own rate
/// bucket, and its events carry <see cref="Events.GameEvent.Origin"/> (real speech, not test). Everything after that is the
/// normal delivery: waiters, debounce, the engaged window, replies to in-game chat.
/// </summary>
public sealed partial class ChatRouter
{
    [ThreadStatic] private static RemoteChat? _remote;

    /// <summary>
    /// Hands a remote line to this router. Returns false when it isn't this bot's (another router in the hive takes it: every
    /// process gets the same line, as with <c>/as</c>) or it's empty.
    /// </summary>
    public bool OnRemoteChat(RemoteChat chat)
    {
        if (!IsRemoteLine(chat, out var player, out var text, out var target)) return false;
        var msg = new ChatMessage(ChatKind.Public, player, text, "[" + chat.Origin + "] " + text);
        bool self = IsSelf(target);
        if (!self && !HostsBody(msg with { Text = target })) return false;
        // A body this brain drives: the brain reads whom the line is for the way it would in game ("113, come here").
        if (!self && !HostsBody(msg)) msg = msg with { Text = $"{target}, {text}" };
        _remote = chat with { Player = player, Text = msg.Text, Target = target };
        try { HandleRemote(msg); }
        finally { _remote = null; }
        return true;
    }

    /// <summary>A whole remote line (player, text and target, trimmed) from a player: not this bot nor a hive bot.</summary>
    private bool IsRemoteLine(RemoteChat chat, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? player, out string text,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? target)
    {
        player = chat.Player?.Trim();
        text = ChatText.StripColors(chat.Text ?? "").Trim();
        target = chat.Target?.Trim();
        if (string.IsNullOrEmpty(player) || text.Length == 0 || string.IsNullOrEmpty(target)) return false;
        return !IsSelf(player) && !IsHive(player);
    }

    // A body (not this login) is ours when the hive/body hooks say a line naming it is explicitly for this bot.
    private bool HostsBody(ChatMessage msg)
    {
        try { return ExplicitlyAddressed?.Invoke(msg) == true; }
        catch (Exception) { return false; }
    }

    private void HandleRemote(ChatMessage msg)
    {
        var player = msg.Player!;
        lock (_lock)
        {
            // No _online.Add: the player may not be in game (greetings, audiences and HumansOnline stay the server's).
            _recentHeard.Enqueue((player, msg.Text, Now));
            while (_recentHeard.Count > 24) _recentHeard.Dequeue();
        }
        Observe(msg); // nicknames, "not talking to you"... learned the same as in game
        try { AddressDecided?.Invoke(msg, new AddressDecision(AddressReason.Name, $"{_remote!.Origin} line for {_remote.Target}")); }
        catch (Exception) { }
        lock (_lock)
        {
            _lastHumanLine = (player, Now, true, null, CommandPrefix(msg.Text));
            _lastLineTo[player] = (Now, _options.Username);
            _lastToUs[player] = Now;
            _requestAt.Remove(player); // their reply closes our open request
        }
        Deliver(msg, true, AddressReason.Name);
    }

    // Deliver's command step for a remote line: CommandGate.Observe is never called (it would also close the player's in-game
    // requests on every other bot); a grant is armed only for the bot that takes the line, and only when both flags allowed it.
    private void NoteRemoteCommands(RemoteChat remote, ChatMessage msg, bool addressed)
    {
        if (Commands == null || !addressed || !remote.AllowCommands) return;
        Commands.ObserveRemote(msg.Player!, msg.Text, remote.Origin);
    }

    // A separate incoming-rate bucket per origin: a viewer flood never demotes the player's real in-game lines to context.
    private static string RemoteBucket(string player, RemoteChat remote) => player + "\0" + remote.Origin;
}
