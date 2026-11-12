using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MineClaw.Scripting.Story;

/// <summary>
/// The Story Mode colony director as a pawn script sees it (the <c>story</c> SDK module, docs/web/story-mode.md): the next job for
/// this body and its reports. No model is ever asked: the director picks from the priorities, schedule, orders and designations
/// the browser set. The Hive layer implements it; tests fake it.
/// </summary>
public interface IStoryPort
{
    /// <summary>The next job for <paramref name="body"/> (account): <c>{id, type, kind, label, at?, ...}</c>, or null = nothing now.</summary>
    JsonObject? Next(string body);

    /// <summary>A report on job <paramref name="job"/>: status progress|done|failed, an optional note and percentage. False = the job
    /// is no longer this body's (superseded: drop it and ask for the next).</summary>
    bool Report(string body, string job, string status, string? note, double? pct);

    /// <summary>After one reserved mining cell was removed, leases newly exposed ore for this same job. Entries are
    /// {at:"x,y,z",ore:canonicalBlockId}; null means the job was cancelled. Called before the ordinary per-cell report.</summary>
    JsonArray? DigExposed(string body, string job, string cell, int beforeId, int beforeMeta) => [];

    /// <summary>Is <paramref name="job"/> still <paramref name="body"/>'s? False once the director took it back (real work came up for
    /// an idle pawn): <c>story.during</c> then cuts what the script is doing. A director that never takes jobs back keeps the default.</summary>
    bool Holds(string body, string job) => true;

    /// <summary>
    /// <paramref name="body"/> can't get out of where it is by itself (no stairs to dig, no blocks to pillar with): the colony sends help
    /// (another pawn brings blocks) or, as a last resort, teleports it home. Returns what happens, for the pawn's report.
    /// </summary>
    string Stuck(string body, string why) => "no colony to ask for help";

    /// <summary>
    /// The colony teleports a stuck pawn home (its <c>rescue_teleport</c> option). Fox 11-12: "If a bot can't navigate something without
    /// deleting its nerd pole, then it should teleport." With it on, <c>story.escape</c> poles only as high as it can take the pole back
    /// from the rim afterwards; deeper, it asks for the teleport. Off (and off-colony): it poles up to the rim, however high.
    /// </summary>
    bool Teleports(string body) => false;

    /// <summary>
    /// A block <paramref name="body"/> put down for itself (<paramref name="placed"/>: a reach pillar step, a build's scaffold, an escape
    /// pillar; <paramref name="why"/> says which) or took back again. Fox 11-05: "Bots should delete whatever they put down unless it
    /// was a directive to build it." The colony keeps every such cell until the world shows it gone; one its pawn leaves behind (the
    /// job failed, was cut, the pawn went offline or was teleported) becomes a cleanup job for any pawn.
    /// </summary>
    void Temporary(string body, string cell, bool placed, string why) { }
}

/// <summary>Which director a body's script talks to: a process-wide resolver (account → port), set by the colony host.</summary>
public static class StoryPorts
{
    private static readonly ConcurrentDictionary<string, IStoryPort> ByBody = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A pawn's account joins its colony's director (re-binding replaces).</summary>
    public static void Bind(string body, IStoryPort port) => ByBody[body] = port;

    public static void Unbind(string body, IStoryPort? port = null)
    {
        if (port == null) ByBody.TryRemove(body, out _);
        else ByBody.TryRemove(new KeyValuePair<string, IStoryPort>(body, port));
    }

    public static IStoryPort? For(string? body) => body != null && ByBody.TryGetValue(body, out var p) ? p : null;
}
