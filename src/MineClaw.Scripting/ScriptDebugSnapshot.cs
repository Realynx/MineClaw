using System.Text.Json.Nodes;

namespace MineClaw.Scripting;

/// <summary>One call-stack frame ("mineGen sdk/50-jobs.js:12:5"); <see cref="Script"/> = the script's own source.</summary>
public sealed record ScriptFrame(string Function, string File, int Line, int Column, bool Script);

/// <summary>A local variable at the current script frame: its name, a short preview of the value and the scope it lives in.</summary>
public sealed record ScriptLocal(string Name, string Value, string Scope);

/// <summary>One action kind's timing so far: calls, failures, total seconds, average ms.</summary>
public sealed record ScriptTimingRow(string Action, int Calls, int Fails, double Seconds, double AvgMs);

/// <summary>The host action in flight (or the last one): "dig" with "5,64,7", for how long, whether it is atomic, how it ended.</summary>
public sealed record ScriptActionView(string Label, string? Args, double Seconds, bool Atomic, bool Running, bool? Ok = null);

/// <summary>A running state machine: its id, current state, transition count, history (oldest first) and blackboard.</summary>
public sealed record ScriptMachineView(string Id, string State, int Transitions, IReadOnlyList<Sdk.MachineStep> History, JsonNode? Blackboard,
    double? BlackboardAgeSeconds = null);

/// <summary>A crew lane this script started: lane id, the body it runs on, its terse status.</summary>
public sealed record ScriptLaneView(int Id, string Body, string Status);

/// <summary>
/// A debugger's view of one running (or just ended) script, read off the script thread from what the script thread published:
/// the current line (live while it steps), the call stack, the machine, params, checkpoint, the action in flight, the pause
/// state and reason, timings and crew lanes. Cheap: building one never blocks the script; see
/// <see cref="ScriptSession.DebugSnapshot"/>. Serializable (snake_case JSON over HTTP and the hive IPC).
/// </summary>
public sealed record ScriptDebugSnapshot
{
    public int SessionId { get; init; }
    public string Name { get; init; } = "";
    public int? Version { get; init; }
    /// <summary>"js" / "lua".</summary>
    public string Lang { get; init; } = "js";
    /// <summary>starting|running|paused|done|failed|cancelled.</summary>
    public string Status { get; init; } = "";
    public bool Live { get; init; }
    public string? Body { get; init; }
    public string? Author { get; init; }
    /// <summary>Crew lane: the role function this lane runs; null for the coordinator / a plain script.</summary>
    public string? Role { get; init; }
    public string? Crew { get; init; }
    public DateTimeOffset At { get; init; }
    public DateTimeOffset Started { get; init; }
    /// <summary>Bumps every time the script thread publishes a new sample (a client can tell a frozen feed from a quiet one).</summary>
    public long Seq { get; init; }

    /// <summary>The line in the script source the debugger highlights (1-based; 0 = unknown).</summary>
    public int Line { get; init; }
    public int Column { get; init; }
    /// <summary>True when <see cref="Line"/> is where the engine is right now (stepping); false = the last host call's site.</summary>
    public bool LineLive { get; init; }
    /// <summary>Where the engine is when that isn't the script itself ("sdk/50-jobs.js:41"), else null.</summary>
    public string? InSdk { get; init; }
    public string? Snippet { get; init; }
    /// <summary>Innermost frame first.</summary>
    public IReadOnlyList<ScriptFrame> Stack { get; init; } = [];
    public double? StackAgeSeconds { get; init; }

    public ScriptActionView? Action { get; init; }
    public int Actions { get; init; }
    public double ElapsedSeconds { get; init; }
    public IReadOnlyList<ScriptTimingRow> Timings { get; init; } = [];

    /// <summary>The effective params (defaults &lt; saved &lt; this run), or null when the script declared none.</summary>
    public JsonNode? Params { get; init; }
    /// <summary>This run's own values (<c>args</c>), as given.</summary>
    public JsonNode? Args { get; init; }
    public string? Checkpoint { get; init; }
    public int CheckpointsPassed { get; init; }
    public int CheckpointTotal { get; init; }

    public bool Paused { get; init; }
    /// <summary>A pause was requested and takes effect at the next safe point.</summary>
    public bool PausePending { get; init; }
    /// <summary>chat|script|brain|danger|stop.</summary>
    public string? PauseKind { get; init; }
    public string? PauseReason { get; init; }
    public double? PausedSeconds { get; init; }

    /// <summary>"planning: lowhp" / "running learned handler for failed" while an interrupt is fixed inline (the panel shows "patching: …").</summary>
    public string? Patching { get; init; }
    /// <summary>The last patch of this run ("learned: resume (moved off the cell)", "escalated: needs a new chest").</summary>
    public string? PatchNote { get; init; }
    public int Patches { get; init; }

    public ScriptMachineView? Machine { get; init; }

    /// <summary>Locals at the innermost script frame (null = not available; <see cref="LocalsNote"/> says why).</summary>
    public IReadOnlyList<ScriptLocal>? Locals { get; init; }
    public string? LocalsNote { get; init; }
    public double? LocalsAgeSeconds { get; init; }

    public IReadOnlyList<ScriptLaneView> Lanes { get; init; } = [];

    public double? ProgressPct { get; init; }
    public string? ProgressNote { get; init; }
    public string? LastFailure { get; init; }
    public string? Outcome { get; init; }
    public IReadOnlyList<string> Logs { get; init; } = [];

    /// <summary>The script source (only when asked for: <c>?code=1</c>).</summary>
    public string? Code { get; init; }

    /// <summary>What a later version may do from the panel. Read-only for now: always empty.</summary>
    // TODO(debugger pause/resume): when the admin-write route exists (POST /api/admin/scripts/{body}/pause|resume, behind
    // Supervisor:AllowAdminWrites + the token header), list "pause"/"resume" here and route them to
    // ScriptSession.RequestPause(PauseKind.Brain, "paused from the Explorer", abort: false) / ScriptSession.Resume().
    public IReadOnlyList<string> Controls { get; init; } = [];
}
