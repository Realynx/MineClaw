using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Telemetry;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Character;

/// <summary>An instant line the body said (or is typing) before any model spoke: who it answered, what, why, when.</summary>
public sealed record InstantLine(string Player, string Text, string Source, DateTimeOffset At)
{
    /// <summary>Completes when the line is out (true) or was held back (false); null = already said.</summary>
    public Task<bool>? Sending { get; init; }
}

/// <summary>
/// The character's voice under the brain (docs/design/reactive-character.md): the prefetched <see cref="LineBank"/>, the
/// background <see cref="LinePrefetcher"/>, and the instant lines said this turn. The brainstem asks <see cref="SayInstant"/>
/// for a moment ("come", "greet", "thanks") the instant a reflex, a speculative start or small talk happens; the line goes out
/// after a human typing delay and the brain's turn sees it as "[already said] …" so the two read as one exchange.
/// </summary>
public sealed class CharacterVoice
{
    private readonly ChatRouter _chat;
    private readonly TimeProvider _time;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly List<InstantLine> _instant = new();
    private readonly Random _rng = new();

    public CharacterVoice(CharacterOptions options, ChatRouter chat, LineBank bank, LinePrefetcher? prefetcher, TimeProvider? time = null, ILogger? log = null)
    {
        Options = options;
        _chat = chat;
        Bank = bank;
        Prefetcher = prefetcher;
        _time = time ?? TimeProvider.System;
        _log = log;
        // Phrase memory: every line the bot sends (prefetched, the brain's, a script's) is remembered, so nothing repeats.
        chat.Said += t => { try { bank.NoteSaid(t); } catch (Exception) { } };
    }

    public CharacterOptions Options { get; }
    public LineBank Bank { get; }
    public LinePrefetcher? Prefetcher { get; }

    /// <summary>The latency tracker an instant line marks (null = none).</summary>
    public LatencyTracker? Latency { get; set; }

    /// <summary>Instant lines sent (telemetry, tests).</summary>
    public int Sent { get; private set; }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// Says a prefetched line for <paramref name="moment"/> to <paramref name="player"/> after a typing delay (fire and forget).
    /// The line is recorded at once (the brain's turn sees it even while it's still being typed). Null = no fitting line ready.
    /// </summary>
    public string? SayInstant(string player, string moment, string source, CancellationToken ct = default)
    {
        if (!Options.InstantLines) return null;
        var line = Bank.Take(moment, player);
        if (line == null) return null;
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rec = new InstantLine(player, line, source, Now) { Sending = done.Task };
        lock (_lock)
        {
            _instant.RemoveAll(l => Now - l.At > TimeSpan.FromMinutes(2));
            _instant.Add(rec);
        }
        var delay = HumanTiming.TypingDelay(line, Options, _rng);
        _ = Task.Run(async () =>
        {
            bool ok = false;
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
                var sent = await _chat.SayAsync(line, null, [player], ct).ConfigureAwait(false);
                ok = sent.Count > 0;
                if (!ok) { _log?.LogInformation("[instant line held back by a chat guard] {Line}", line); return; } // the brain speaks
                Sent++;
                Latency?.NoteInstantLine(Now);
                _log?.LogInformation("[instant line] ({Source}, {Moment}) {Line}", source, moment, line);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log?.LogDebug(ex, "instant line failed"); }
            finally
            {
                if (!ok) lock (_lock) _instant.Remove(rec);
                done.TrySetResult(ok);
            }
        }, CancellationToken.None);
        return line;
    }

    /// <summary>A line a script or reaction said for a player (so the brain's turn knows it was covered).</summary>
    public void NoteInstant(string player, string text, string source)
    {
        lock (_lock) _instant.Add(new InstantLine(player, text, source, Now));
    }

    /// <summary>Instant lines for these players in the last <paramref name="window"/> (default 30 s), oldest first.</summary>
    public IReadOnlyList<InstantLine> For(IEnumerable<string> players, TimeSpan? window = null)
    {
        var w = window ?? TimeSpan.FromSeconds(30);
        var set = players.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock) return _instant.Where(l => set.Contains(l.Player) && Now - l.At <= w).ToList();
    }

    /// <summary>Waits (≤ <paramref name="max"/>) for these lines to be out; returns the ones that were said.</summary>
    public static async Task<IReadOnlyList<InstantLine>> SentAsync(IReadOnlyList<InstantLine> lines, TimeSpan max)
    {
        var said = new List<InstantLine>();
        foreach (var l in lines)
        {
            if (l.Sending is not { } t) { said.Add(l); continue; }
            try { if (await t.WaitAsync(max).ConfigureAwait(false)) said.Add(l); }
            catch (TimeoutException) { }
        }
        return said;
    }

    /// <summary>The turn consumed them (they are history now).</summary>
    public void Consume(IEnumerable<InstantLine> lines)
    {
        lock (_lock) foreach (var l in lines) _instant.Remove(l);
    }

    /// <summary>The moment an addressed line asks for, for an instant line (small talk by intent; null = none).</summary>
    public static string? MomentFor(string text, IEnumerable<string>? names) => LineBank.MomentOf(ChatIntent.Classify(text, names).Intent);

    /// <summary>"[already said] "omw~" (instant, 0.4s after their line): …" for the brain's turn (uncached tail), or "".</summary>
    public static string TurnNote(IReadOnlyList<InstantLine> lines, DateTimeOffset now)
    {
        if (lines.Count == 0) return "";
        var said = string.Join("; ", lines.Select(l => $"\"{l.Text}\" to {l.Player} ({l.Source}, {(now - l.At).TotalSeconds:0.0}s ago)"));
        return $"[already said] {said}: that was you, already in chat. Don't repeat or rephrase it; add only what it didn't cover " +
               "(an answer, a correction: \"actually…\"), or stay silent.";
    }
}
