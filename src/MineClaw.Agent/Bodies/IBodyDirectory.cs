using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Bodies;

/// <summary>
/// What the brain loop knows about its bodies (one brain, many bodies, step 3). The Bot layer implements it over the
/// BodyRegistry; null on <see cref="BotAgent.Bodies"/> (hive mode, one body) keeps the loop exactly as it was. Bodies are
/// named by their short names ("main", "67"); a tool call's <c>body</c> ("67", ["67","69"], "all", "any") resolves here.
/// </summary>
public interface IBodyDirectory
{
    /// <summary>More than one body is configured (in-process bodies): <c>body</c> routing, [bodies], focus are on.</summary>
    bool Active { get; }

    /// <summary>Short names of the bodies in game now, main first.</summary>
    IReadOnlyList<string> Online();

    /// <summary>Short names of every configured body (offline ones too), main first.</summary>
    IReadOnlyList<string> Known();

    /// <summary>A body's short name from any of its names ("maid_chan_67", "67", "main", the main login), or null.</summary>
    string? Canonical(string? name);

    /// <summary>The body's activity slot (null = unknown or not in game).</summary>
    ActivityManager? Activities(string body);

    /// <summary>Who holds the body when it isn't free for brain body tools ("script cobble_loop 40%"), or null.</summary>
    string? Holder(string body);

    /// <summary>Acts on <paramref name="body"/> for the current async flow until disposed (the Bot layer's BodyScope).</summary>
    IDisposable? Push(string body);

    /// <summary>The failure-memory key of a body for <see cref="ToolCallGuard"/> (null = main).</summary>
    string? GuardKey(string body);

    /// <summary>Stops the body's job: its activity is cancelled and its script lane stopped. Returns what stopped, or null.</summary>
    string? Stop(string body);

    /// <summary>"any": the fittest free body for this job text among <paramref name="candidates"/>, or null (none fits; <paramref name="why"/>).</summary>
    string? Pick(string job, IReadOnlyList<string> candidates, out string? why);

    /// <summary>The focus set by <c>bodies op=focus</c> (null = main).</summary>
    string? StandingFocus { get; }

    /// <summary>The bodies a player line addresses, short names in order (main included when named).</summary>
    IReadOnlyList<string> Addressed(string text, bool emote = false);

    /// <summary>The [bodies] line for a turn whose focus is <paramref name="focus"/> ("" = none).</summary>
    string StatusBlock(string? focus);

    /// <summary>The focus body's one-line state header (null = main's, as before).</summary>
    string? StateHeader(string body);

    /// <summary>Called when one brain call started jobs on several bodies at once (their results are reported together).</summary>
    void Started(IReadOnlyList<string> bodies);
}

/// <summary>Turn focus from a batch (pure, tests).</summary>
public static class BodyFocus
{
    /// <summary>
    /// The body the waking chat names (the first named clone; a group address or only main named = main), else the standing
    /// focus. Null = main.
    /// </summary>
    public static string? For(EventBatch batch, IBodyDirectory dir)
    {
        foreach (var e in batch.Waking)
        {
            if (!EventQueue.IsTyped(e)) continue;
            // A whisper to a clone's account is for that body.
            if (e.Kind == EventKind.Whisper && GameEvent.BodyTag(e.Body) is { } whisperedTo) return whisperedTo;
            if (BodyAddressing.IsGroupAddress(e.Text)) return null;
            var named = dir.Addressed(e.Text, e.Kind == EventKind.Emote);
            if (named.Count == 0) continue;
            var first = named.FirstOrDefault(n => !n.Equals("main", StringComparison.OrdinalIgnoreCase));
            return first; // only main named → main (null)
        }
        return dir.StandingFocus is { } f && !f.Equals("main", StringComparison.OrdinalIgnoreCase) ? f : null;
    }
}
