using System.Collections.Concurrent;
using MineClaw.Agent;

namespace MineClaw.Bot;

/// <summary>
/// The editing context of the protection policy (ARCHITECTURE.md "Protection policy", editing context): saving a design is a
/// record, not a lock. Fox, 10-30: "Just because a build is saved doesn't mean it should be protected. I want the AI to keep
/// working on the build, but it saves it, and then it can't edit it." (main captured his spiral staircase at his request, then
/// refused every next column he asked for: "part of the spiral_staircase build"; and its own "stair_shaft_seal_y16" mark
/// blocked the doorway the room dig needed until the brain deleted the mark.)
/// <para>The rule, in one line: soft protection on a saved design (or on one of the bot's own marks) yields to <b>explicitly
/// targeted, requested</b> work. Explicit = a dig at a named cell (the dig tool, a script's dig, a build/revert step), never a
/// pick (mine, materials, path digs, chop). Requested = its build/revert/undo job runs in this flow, or a trusted player asked
/// (the turn's speaker, or a trusted line in the last <see cref="AskMinutes"/>). A design must also be open for editing: a
/// trusted player touched it (captured, committed, built, reverted it, the brain worked on it in their turn, they named it or
/// talked to me standing at it) in the last <see cref="LeaseMinutes"/>, its job is running, or the hive authored it for
/// itself. Never-break marks, containers, other players' builds nobody asked about and every autonomous pick are unchanged.</para>
/// Shared by every body of a process (one per <see cref="Protection"/>); leases are in memory (a restart closes them).
/// </summary>
public sealed class EditContext(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<long, (DateTimeOffset Until, string Why)> _leases = new();
    private (string Player, DateTimeOffset At)? _lastAsk;
    private static readonly AsyncLocal<long?> _job = new();

    /// <summary>How long a trusted touch keeps a design open for editing (like a permit).</summary>
    public int LeaseMinutes { get; set; } = 30;
    /// <summary>How long a trusted player's line makes the bot's work "requested" (a [task] wake after their ask has no speaker).</summary>
    public int AskMinutes { get; set; } = 10;
    /// <summary>The trusted players (the bot's config).</summary>
    public Func<IReadOnlyList<string>> Trusted { get; set; } = () => [];
    /// <summary>A hive bot's name (this bot included): its marks are the bot's own.</summary>
    public Func<string, bool> IsBot { get; set; } = _ => false;
    /// <summary>A design the hive authored for itself (no player requester): open for its own requested work without a lease.</summary>
    public Func<long, bool>? HiveDesign { get; set; }

    public bool IsTrusted(string? player) => player is { Length: > 0 } p && Trusted().Contains(p, StringComparer.OrdinalIgnoreCase);

    /// <summary>The design whose build/revert/undo job runs in this async flow (the executor's digs edit it), or null.</summary>
    public static long? Job => _job.Value;

    /// <summary>Marks this flow as design <paramref name="id"/>'s job until disposed (the build/revert/undo entry points).</summary>
    public static IDisposable JobFor(long id)
    {
        var prev = _job.Value;
        _job.Value = id;
        return new Restore(prev);
    }

    private sealed class Restore(long? prev) : IDisposable
    {
        public void Dispose() => _job.Value = prev;
    }

    /// <summary>A trusted player's line (any line: the chat hook calls this for every accepted line of a trusted player).</summary>
    public void NoteAsk(string? player)
    {
        if (IsTrusted(player)) _lastAsk = (player!, _time.GetUtcNow());
    }

    /// <summary>Opens design <paramref name="id"/> for editing for <see cref="LeaseMinutes"/> ("Fox captured it").</summary>
    public void Touch(long id, string why) => _leases[id] = (_time.GetUtcNow() + TimeSpan.FromMinutes(Math.Max(1, LeaseMinutes)), why);

    /// <summary>Closes a design's lease (deleted).</summary>
    public void Close(long id) => _leases.TryRemove(id, out _);

    /// <summary>Why design <paramref name="id"/> is open for editing ("its build job", "Fox captured it 4 min ago"), or null.</summary>
    public string? Open(long id)
    {
        if (Job == id) return "its own job";
        var now = _time.GetUtcNow();
        if (_leases.TryGetValue(id, out var l))
        {
            if (l.Until > now) return l.Why;
            _leases.TryRemove(id, out _);
        }
        return HiveDesign?.Invoke(id) == true ? "the hive's own design" : null;
    }

    /// <summary>Who asked for the work running in this flow: "its build job", the turn's trusted speaker, or a trusted line in the
    /// last <see cref="AskMinutes"/>; null = the bot's own choice (autonomous: protection is unchanged).</summary>
    public string? Requested()
    {
        if (Job != null) return "its build job";
        if (TurnContext.Current?.Speaker is { } sp && IsTrusted(sp)) return sp;
        if (_lastAsk is { } a && _time.GetUtcNow() - a.At <= TimeSpan.FromMinutes(Math.Max(1, AskMinutes)) && IsTrusted(a.Player)) return a.Player;
        return null;
    }

    /// <summary>A mark the bot (or a hive bot) made itself: no owner, or a bot's name.</summary>
    public bool BotMade(string? owner) => string.IsNullOrWhiteSpace(owner) || IsBot(owner.Trim());
}
