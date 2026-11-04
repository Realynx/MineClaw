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

    /// <summary>Is <paramref name="job"/> still <paramref name="body"/>'s? False once the director took it back (real work came up for
    /// an idle pawn): <c>story.during</c> then cuts what the script is doing. A director that never takes jobs back keeps the default.</summary>
    bool Holds(string body, string job) => true;

    /// <summary>
    /// <paramref name="body"/> can't get out of where it is by itself (no stairs to dig, no blocks to pillar with): the colony sends help
    /// (another pawn brings blocks) or, as a last resort, teleports it home. Returns what happens, for the pawn's report.
    /// </summary>
    string Stuck(string body, string why) => "no colony to ask for help";
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
