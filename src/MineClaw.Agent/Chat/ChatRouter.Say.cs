namespace MineClaw.Agent.Chat;

/// <summary><see cref="SayAsync"/>: model text to chat lines, the caps, the paced send, and what saying it leaves behind.</summary>
public sealed partial class ChatRouter
{
    /// <summary>
    /// Sends model text to chat: sanitised for b1.8.1, split into ≤100-char lines, capped at
    /// <see cref="ChatOptions.MaxLinesPerReply"/>, paced. Whispers use <see cref="ChatOptions.WhisperFormat"/>.
    /// Returns the lines actually sent.
    /// </summary>
    public async Task<IReadOnlyList<string>> SayAsync(string text, string? whisperTo = null, IEnumerable<string>? audience = null, CancellationToken ct = default)
    {
        var to = SayAudience(text, whisperTo, audience);
        text = FilterOutgoing(text, to);
        string prefix = whisperTo != null ? string.Format(_options.WhisperFormat, whisperTo, "") : "";
        int max = ChatText.MaxLineLength - prefix.Length;
        var lines = ChatLines(text, max);
        if (lines.Count == 0) return [];
        int cap = LineCap(text, to, lines.Count);
        if (cap <= 0) return [];
        try { Said?.Invoke(text); } catch (Exception) { /* listeners never break chat */ }
        if (lines.Count > cap) lines = CapLines(lines, cap, max);
        await SendLinesAsync(lines, prefix, ct).ConfigureAwait(false);
        NoteSpoken(lines, to, whisperTo, audience);
        return lines.Select(l => l.Text).ToList();
    }

    private List<string> SayAudience(string text, string? whisperTo, IEnumerable<string>? audience)
    {
        var to = audience?.ToList() ?? [];
        if (whisperTo != null && to.Count == 0) to.Add(whisperTo);
        if (to.Count == 0) to.AddRange(InferAudience(text ?? ""));
        return to;
    }

    private string FilterOutgoing(string text, List<string> to)
    {
        // At most one food ask per player per 10 minutes, whoever writes the line (brain, tool, script).
        text = Food.Filter(text ?? "", to, Now, PlayerAskedAboutFood);
        text = Greeted.Filter(text, to, Now); // one hello per player per few minutes (join turn + their first line)
        text = Deaths.Filter(text, to, out var stale); // "robin run to the chest" after "kiwi was slain by Zombie": stale
        if (stale.Count > 0) try { StaleDropped?.Invoke(string.Join(" ", stale)); } catch (Exception) { }
        return text;
    }

    /// <summary>
    /// A line of model text starting with "/me " is an emote (sent as-is, always public); any other "/..." line would be a
    /// server command: logged and dropped. Everything else is chat, in order.
    /// </summary>
    private List<(string Text, bool Emote)> ChatLines(string text, int max)
    {
        var lines = new List<(string Text, bool Emote)>();
        var pending = new List<string>();
        foreach (var segment in (text ?? "").Replace("\r", "").Split('\n').SelectMany(SplitInlineEmotes))
        {
            var t = segment.Trim();
            if (t.Length == 0) continue;
            if (!t.StartsWith('/'))
            {
                pending.Add(t);
                continue;
            }
            FlushChat(lines, pending, max);
            if (SlashLine(t) is { } line) lines.Add((line, true));
        }
        FlushChat(lines, pending, max);
        return lines;
    }

    private static void FlushChat(List<(string Text, bool Emote)> lines, List<string> pending, int max)
    {
        if (pending.Count == 0) return;
        var clean = ChatText.Sanitize(string.Join(" ", pending));
        pending.Clear();
        if (clean.Length > 0) lines.AddRange(ChatText.Split(clean, max).Select(l => (l, false)));
    }

    /// <summary>"/me ..." and always-allowed commands ("/tell kiwi psst") go out as typed (one line); any other command is dropped.</summary>
    private string? SlashLine(string t)
    {
        if (t.StartsWith("/me ", StringComparison.OrdinalIgnoreCase))
        {
            var action = ChatText.Sanitize(t[4..]);
            return action.Length > 0 ? "/me " + TextUtilTruncate(action, ChatText.MaxLineLength - 4) : null;
        }
        if (Commands != null && Commands.IsAlwaysAllowed(t) && !Commands.IsBlocked(t))
            return TextUtilTruncate("/" + ChatText.Sanitize(t[1..]), ChatText.MaxLineLength);
        CommandBlocked?.Invoke(t);
        return null;
    }

    /// <summary>The reply's line cap; during the startup window the session's startup budget too.</summary>
    private int LineCap(string text, List<string> to, int count)
    {
        int cap = _options.MaxLinesPerReply;
        // A safety warning (a mob on someone, a creeper, a death) is never capped as startup chatter (one per threat per 30 s).
        bool warning = Warnings.Take(text, to);
        lock (_lock)
        {
            if (Now < _startupUntil && !warning)
            {
                cap = Math.Min(cap, _startupLinesLeft);
                _startupLinesLeft = Math.Max(0, _startupLinesLeft - count);
            }
        }
        return cap;
    }

    private static List<(string Text, bool Emote)> CapLines(List<(string Text, bool Emote)> lines, int cap, int max)
    {
        lines = lines.Take(cap).ToList();
        var (last, emote) = lines[^1];
        int lim = emote ? ChatText.MaxLineLength : max;
        lines[^1] = (last.Length + 3 <= lim ? last + "..." : last[..(lim - 3)] + "...", emote);
        return lines;
    }

    private async Task SendLinesAsync(List<(string Text, bool Emote)> lines, string prefix, CancellationToken ct)
    {
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
    }

    /// <summary>Who we just talked with (their unnamed follow-ups continue with us) and the questions/requests we left open.</summary>
    private void NoteSpoken(List<(string Text, bool Emote)> lines, List<string> to, string? whisperTo, IEnumerable<string>? audience)
    {
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
    }
}
