using System.Collections.Concurrent;

namespace MineClaw.Scripting.Crew;

/// <summary>
/// The crews whose coordinator is still running in this process (opened by its first lane, closed when it ends). A lane's body is
/// that crew's business until then: its dangers, pauses and per-lane results don't wake the brain (the reflexes and the coordinator
/// deal with them; <c>everyBody</c> re-runs a role that ended), only the crew's one result does. Live 10-29: a DeepSeek hive of 20
/// bodies woke on every lane's zombie and every lane's end, and its notes flooded.
/// </summary>
public static class Crews
{
    private static readonly ConcurrentDictionary<string, int> Live = new(StringComparer.Ordinal);

    public static void Open(string crew) => Live.AddOrUpdate(crew, 1, (_, n) => n + 1);

    public static void Close(string crew)
    {
        while (Live.TryGetValue(crew, out var n))
        {
            if (n <= 1 ? Live.TryRemove(new KeyValuePair<string, int>(crew, n)) : Live.TryUpdate(crew, n - 1, n)) return;
        }
    }

    public static bool IsLive(string? crew) => crew != null && Live.ContainsKey(crew);

    private static readonly System.Text.RegularExpressions.Regex CrewCall =
        new(@"\b(?:everyBody|parallel|spawn)\s*\(", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The code works its bodies through roles (<c>everyBody</c>/<c>parallel</c>/<c>spawn</c>): a coordinator, not a one-body job.</summary>
    public static bool IsCrewScript(string? code) => code != null && CrewCall.IsMatch(code);

    /// <summary>A body's run belongs to a live crew: a lane now, or its last run was a lane of a crew still going.</summary>
    public static bool Owns(ScriptController c) =>
        c.Current is { Request.Role: not null } || c.Last is { Request: { Role: not null, CrewId: var id } } && IsLive(id);
}
