using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Events;

namespace MineClaw.Hive.Chat;

/// <summary>
/// A per-hive public-chat budget (lines per minute, a sliding window). Lines a player asked for always pass but count; unasked
/// lines (narration, "done!", idle chatter) pass only while the budget has room. One instance per hive process / body.
/// </summary>
public sealed class ChatVerbosityBudget(double linesPerMinute, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly object _lock = new();

    public double LinesPerMinute => linesPerMinute;
    public int Dropped { get; private set; }

    /// <summary>Lines said in the last minute.</summary>
    public int Used { get { lock (_lock) { Prune(); return _sent.Count; } } }

    private void Prune()
    {
        var cut = _time.GetUtcNow() - TimeSpan.FromMinutes(1);
        while (_sent.Count > 0 && _sent.Peek() < cut) _sent.Dequeue();
    }

    /// <summary>True = say it (and count it). <paramref name="addressed"/> lines always pass.</summary>
    public bool TryTake(bool addressed)
    {
        lock (_lock)
        {
            Prune();
            if (linesPerMinute <= 0 || addressed || _sent.Count < Math.Max(1, (int)Math.Floor(linesPerMinute)))
            {
                _sent.Enqueue(_time.GetUtcNow());
                return true;
            }
            Dropped++;
            return false;
        }
    }
}

/// <summary>
/// The hive's speech wrapper around a bot's <see cref="BotAgent.SpeechGate"/>: drops sentences about internal plumbing (the bus,
/// whitelisting, allocating accounts, subhive/script-only kinds, raw logins, "[hive" tags) that the narration guards don't know,
/// and applies the per-hive verbosity budget. "I'll get a clone to help" stays: that's for the player.
/// </summary>
public static class HiveSpeech
{
    private static readonly Regex Plumbing = new(
        @"[^.!?\n]*(?:\[hive|\bwhitelist(?:ed|ing)?\b|\b(?:off-?channel|hive ?bus|mem_write|mem_changed|hive_report)\b|\bsub-?hives?\b|\bscript-only\b|" +
        @"\ballocat(?:e|ed|ing)\s+(?:a\s+|an\s+|new\s+)*accounts?\b|\bmaid_chan_\d+\b)[^.!?\n]*[.!?]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The text without internal-coordination sentences (empty = nothing left to say).</summary>
    public static string Scrub(string text)
    {
        var clean = Plumbing.Replace(text ?? "", "").Trim();
        clean = Regex.Replace(clean, @"\s{2,}", " ");
        return clean.Length == 0 || clean.All(c => !char.IsLetterOrDigit(c)) ? "" : clean;
    }

    public static bool Addressed(EventBatch batch) =>
        batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && e.Player != null);

    /// <summary>Wraps <paramref name="inner"/> (may be null): inner first, then the scrub, then the budget.</summary>
    /// <param name="dropped">Gets what was kept out of chat (a clone forwards it to main as progress over IPC).</param>
    public static Func<EventBatch, string, bool, string?> Wrap(Func<EventBatch, string, bool, string?>? inner, ChatVerbosityBudget budget,
        Action<string>? dropped = null) => (batch, text, already) =>
    {
        var t = inner != null ? inner(batch, text, already) : text;
        if (string.IsNullOrWhiteSpace(t)) return t;
        var clean = Scrub(t);
        if (clean.Length == 0) { dropped?.Invoke(t); return null; }
        if (!budget.TryTake(Addressed(batch))) { dropped?.Invoke(clean); return null; }
        return clean;
    };
}
