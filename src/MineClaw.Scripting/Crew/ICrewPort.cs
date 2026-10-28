using System.Text.Json;
using MineClaw.World;

namespace MineClaw.Scripting.Crew;

/// <summary>What a script sees of another body (<c>body("67")</c>): live vitals, position, whether it is busy.</summary>
public sealed record BodyInfo(string Name, double Hp, double Food, BlockPos Pos, bool Busy, string? Job = null, int Free = -1);

/// <summary>
/// One crew lane to start: <paramref name="Role"/> is the function the lane calls after loading the script's top level (only
/// function definitions and constants there). <paramref name="CrewId"/> names the shared board; the lane's checkpoints are
/// keyed by (script, body).
/// </summary>
public sealed record LaneSpec(string Body, string Script, string Code, ScriptLanguage Language, string Role, string? ArgsJson, string CrewId,
    int? Version = null, string? Author = null);

/// <summary>A running lane (another body running a role of the same script).</summary>
public interface ILane
{
    string Body { get; }
    /// <summary>"running 40%" / "done" / "failed: ..." (terse).</summary>
    string Status { get; }
    /// <summary>The lane's result line (ok = the role returned normally).</summary>
    Task<ActResult> Completion { get; }
    void Cancel(string reason);
}

/// <summary>
/// The multi-body port (one brain, many bodies). In-process bodies (Bodies:Mode=inproc) start lanes on each body's own script
/// controller; hive mode starts them as clone script spawns (hive jobs) and leases go through the claim board. Scripts never
/// talk to bodies any other way.
/// </summary>
public interface ICrewPort
{
    /// <summary>Short names of the bodies a crew may use ("67", "69", ...; main = "main").</summary>
    IReadOnlyList<string> Bodies();
    /// <summary>Live facts about a body, or null when it is unknown/offline.</summary>
    BodyInfo? Info(string body);
    /// <summary>How many of an item that body holds (-1 = unknown).</summary>
    int Count(string body, string item);
    /// <summary>Starts a lane (the body must be free: a busy body answers a failed lane "body 69 busy: ...").</summary>
    ILane Start(LaneSpec spec);
    /// <summary>One action on another, idle body from this lane (in-process only; hive mode answers that it needs a lane).</summary>
    Task<ActResult> ActAsync(string body, string action, JsonElement args, CancellationToken ct);
    /// <summary>The shared crew board.</summary>
    ICrewBoard Board { get; }
}

/// <summary>
/// The crew's shared state: a small JSON key/value board (≤ 4 KB per crew), one inbox per body and named leases. Values are JSON
/// text. Implementations: <see cref="InMemoryCrewBoard"/> (one process) and the db-backed board (hive processes share the db).
/// </summary>
public interface ICrewBoard
{
    string? Get(string crew, string key);
    /// <summary>False when the crew's board would exceed its size cap.</summary>
    bool Set(string crew, string key, string? json);
    double Inc(string crew, string key, double by);
    void Send(string crew, string to, string json);
    /// <summary>The oldest message for <paramref name="me"/>, removed; null when the inbox is empty.</summary>
    string? TryReceive(string crew, string me);
    bool TryLock(string crew, string key, string owner, TimeSpan ttl);
    void Unlock(string crew, string key, string owner);
}
