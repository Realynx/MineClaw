using System.Text.RegularExpressions;
using MineClaw.Agent.Events;

namespace MineClaw.Agent.Chat;

public enum ChatKind { Public, Whisper, Join, Leave, System }

public sealed record ChatMessage(ChatKind Kind, string? Player, string Text, string Raw);

/// <summary>
/// Turns raw server chat lines into <see cref="GameEvent"/>s (only addressed messages wake the model; others are
/// capped background context), answers <c>ask_player</c> waiters, and sends the bot's replies split/sanitised
/// for b1.8.1 with pacing.
/// </summary>
public sealed class ChatRouter
{
    private readonly ChatOptions _options;
    private readonly EventQueue _events;
    private readonly Func<string, CancellationToken, Task> _sendRaw;
    private readonly TimeProvider _time;
    private readonly Regex[] _whisper, _join, _leave, _public;
    private readonly Regex _groupRegex;
    private readonly object _lock = new();
    private readonly HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTalkedWith = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _incoming = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Line, DateTimeOffset At)> _recentSent = new();
    private readonly List<(string? Player, TaskCompletionSource<string> Tcs)> _waiters = new();
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

    /// <summary>Humans currently online (bot excluded).</summary>
    public IReadOnlyCollection<string> OnlinePlayers { get { lock (_lock) return _online.ToArray(); } }

    /// <summary>Replace the online player list (e.g. from the tab-list packets).</summary>
    public void SetOnlinePlayers(IEnumerable<string> players)
    {
        lock (_lock)
        {
            _online.Clear();
            foreach (var p in players)
                if (!IsSelf(p)) _online.Add(p);
        }
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    // ---------- incoming ----------

    public ChatMessage Parse(string raw)
    {
        var line = ChatText.StripColors(raw ?? "").Trim();
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
    public ChatMessage OnServerChat(string raw)
    {
        var msg = Parse(raw);
        switch (msg.Kind)
        {
            case ChatKind.Join when msg.Player != null && !IsSelf(msg.Player):
                lock (_lock) _online.Add(msg.Player);
                _events.Post(GameEvent.Joined(msg.Player));
                break;
            case ChatKind.Leave when msg.Player != null && !IsSelf(msg.Player):
                lock (_lock) _online.Remove(msg.Player);
                _events.Post(GameEvent.Left(msg.Player));
                break;
            case ChatKind.Public or ChatKind.Whisper when msg.Player != null:
                HandleMessage(msg);
                break;
        }
        return msg;
    }

    private void HandleMessage(ChatMessage msg)
    {
        var player = msg.Player!;
        if (IsSelf(player) || IsEcho(msg.Text) || msg.Text.Length == 0) return;
        lock (_lock) _online.Add(player);

        bool addressed = IsAddressed(msg);

        if (TryCompleteWaiter(player, msg.Text, addressed)) return;

        if (addressed && !AllowIncoming(player)) addressed = false; // spam: keep as context only

        if (msg.Kind == ChatKind.Whisper && addressed)
            _events.Post(GameEvent.Whisper(player, msg.Text));
        else
            _events.Post(GameEvent.Chat(player, msg.Text, addressed));

        if (addressed) lock (_lock) _lastTalkedWith[player] = Now;
    }

    /// <summary>Whether the bot should treat this message as directed at it.</summary>
    public bool IsAddressed(ChatMessage msg)
    {
        if (msg.Player == null || IsSelf(msg.Player)) return false;
        if (msg.Kind == ChatKind.Whisper) return true;
        if (msg.Kind != ChatKind.Public) return false;
        if (_identity.NameRegex.IsMatch(msg.Text)) return true;

        int humans;
        lock (_lock)
        {
            humans = Math.Max(_online.Count, 1);
            if (_lastTalkedWith.TryGetValue(msg.Player, out var at) && Now - at < TimeSpan.FromSeconds(_options.ReplyWindowSeconds))
            {
                // In an ongoing exchange — unless they're clearly talking to another player by name.
                if (!_online.Any(p => !p.Equals(msg.Player, StringComparison.OrdinalIgnoreCase) &&
                                      Regex.IsMatch(msg.Text, @"(?<![A-Za-z0-9_])" + Regex.Escape(p) + @"(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)))
                    return true;
            }
        }
        if (_options.SoloPlayerAlwaysAddressed && humans <= 1) return true;
        if (humans <= _options.SmallGroupSize && _groupRegex.IsMatch(msg.Text)) return true;
        return false;
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
    /// Waits for the next message from <paramref name="player"/> (any of their messages counts), or the next
    /// addressed message from anyone if null. The reply is consumed (not posted as an event). Null on timeout.
    /// </summary>
    public async Task<string?> WaitForReplyAsync(string? player, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = (player, tcs);
        lock (_lock) _waiters.Add(entry);
        try
        {
            return await tcs.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_lock) _waiters.Remove(entry);
        }
    }

    private bool TryCompleteWaiter(string player, string text, bool addressed)
    {
        TaskCompletionSource<string>? hit = null;
        lock (_lock)
        {
            int i = _waiters.FindIndex(w => w.Player != null && w.Player.Equals(player, StringComparison.OrdinalIgnoreCase));
            if (i < 0 && addressed) i = _waiters.FindIndex(w => w.Player == null);
            if (i >= 0)
            {
                hit = _waiters[i].Tcs;
                _waiters.RemoveAt(i);
                _lastTalkedWith[player] = Now;
            }
        }
        if (hit == null) return false;
        hit.TrySetResult(player + ": " + text);
        return true;
    }

    // ---------- outgoing ----------

    /// <summary>
    /// Sends model text to chat: sanitised for b1.8.1, split into ≤100-char lines, capped at
    /// <see cref="ChatOptions.MaxLinesPerReply"/>, paced. Whispers use <see cref="ChatOptions.WhisperFormat"/>.
    /// Returns the lines actually sent.
    /// </summary>
    public async Task<IReadOnlyList<string>> SayAsync(string text, string? whisperTo = null, IEnumerable<string>? audience = null, CancellationToken ct = default)
    {
        var clean = ChatText.Sanitize(text);
        if (clean.Length == 0) return [];

        string prefix = whisperTo != null ? string.Format(_options.WhisperFormat, whisperTo, "") : "";
        int max = ChatText.MaxLineLength - prefix.Length;
        var lines = ChatText.Split(clean, max);
        if (lines.Count > _options.MaxLinesPerReply)
        {
            lines = lines.Take(_options.MaxLinesPerReply).ToList();
            var last = lines[^1];
            lines[^1] = last.Length + 3 <= max ? last + "..." : last[..(max - 3)] + "...";
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var line in lines)
            {
                var wait = _lastSend + TimeSpan.FromMilliseconds(_options.LineDelayMs) - Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                await _sendRaw(prefix + line, ct).ConfigureAwait(false);
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

        lock (_lock)
        {
            if (whisperTo != null) _lastTalkedWith[whisperTo] = Now;
            if (audience != null) foreach (var p in audience) _lastTalkedWith[p] = Now;
        }
        return lines;
    }
}
