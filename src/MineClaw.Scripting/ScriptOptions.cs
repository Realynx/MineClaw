namespace MineClaw.Scripting;

/// <summary>
/// Limits and interrupt policy for bot scripts ("Bot:Scripts"). Plain get/set so configuration binding works. Every
/// limit breach ends the script with a terse failure; none of them can be caught by the script itself.
/// </summary>
public sealed class ScriptOptions
{
    /// <summary>Max VM instructions (Lua) / statements (JS) per run (all segments together).</summary>
    public long MaxInstructions { get; set; } = 20_000_000;
    /// <summary>Max active wall time per run, minutes (time spent paused is not counted; see <see cref="MaxPauseMinutes"/>).</summary>
    public double MaxMinutes { get; set; } = 30;
    /// <summary>Max game actions (tool/skill calls, waits, chat) per run.</summary>
    public int MaxActions { get; set; } = 2_000;
    /// <summary>Busy-loop guard: max VM instructions between two actions (a loop that never acts or waits trips it).</summary>
    public int MaxInstructionsWithoutAction { get; set; } = 200_000;
    /// <summary>Max Lua call depth.</summary>
    public int MaxCallDepth { get; set; } = 100;
    /// <summary>Max bytes the script may allocate between two host calls (memory bombs; long runs are fine).</summary>
    public long MaxSegmentAllocBytes { get; set; } = 48L * 1024 * 1024;
    public int MaxStringLength { get; set; } = 100_000;
    public int MaxCodeChars { get; set; } = 4_000;
    /// <summary>A paused script is cancelled after this long without resume/replace.</summary>
    public double MaxPauseMinutes { get; set; } = 10;
    /// <summary>
    /// Addressed chat pauses the script (it resumes by itself after <see cref="ChatAutoResumeSeconds"/>). Off by default: chat
    /// wakes the brain while the script keeps running (body lock, not brain lock); only danger, a trusted "stop"/"wait" and
    /// the brain's own script op=pause|replace|cancel pause it.
    /// </summary>
    public bool PauseOnChat { get; set; } = false;
    public int ChatAutoResumeSeconds { get; set; } = 30;
    /// <summary>
    /// Unattended runs (crew lanes, hive scripts): a danger pause resumes by itself once the body's safety reflexes have let go and
    /// no new danger came for this long, seconds.
    /// </summary>
    public double LaneSafeSeconds { get; set; } = 3;
    /// <summary>Unattended runs: still in danger after this long, seconds, the run fails (its coordinator re-runs the role elsewhere or later).</summary>
    public double LaneDangerMaxSeconds { get; set; } = 120;
    /// <summary>A hive script's coordinator (<c>hive spawn kind=script bodies=N</c>) may run this long, minutes (its lanes keep <see cref="MaxMinutes"/>
    /// each and are re-run by <c>everyBody</c> when one hits it).</summary>
    public double HiveScriptMinutes { get; set; } = 240;
    /// <summary>say/emote rate limit.</summary>
    public int MaxSayPerMinute { get; set; } = 6;
    /// <summary>say/emote lines one script run may send to chat; the rest go to <c>log</c> (say is for players, not data).</summary>
    public int MaxSayPerRun { get; set; } = 2;
    /// <summary>Minimum gap between two say/emote lines of a script; a line sooner than this goes to <c>log</c>.</summary>
    public double SayMinIntervalSeconds { get; set; } = 10;
    /// <summary>Longest single wait/wait_for/ask, seconds.</summary>
    public int MaxWaitSeconds { get; set; } = 600;
    /// <summary>Worker calls wait this long for the script inline, then it continues as the bot's task.</summary>
    public int WorkerInlineSeconds { get; set; } = 60;
    /// <summary>Actions skipped while fast-forwarding to a resume_from checkpoint before giving up.</summary>
    public int MaxSkippedActions { get; set; } = 500;
    /// <summary>Obsolete (kept for config binding): promotion now needs <see cref="PromoteMinRuns"/> runs at <see cref="PromoteMinSuccessRate"/>.</summary>
    public int PromoteAfterSuccesses { get; set; } = 3;
    /// <summary>A saved script's current version joins the skill catalog after this many runs...</summary>
    public int PromoteMinRuns { get; set; } = 3;
    /// <summary>...with at least this success rate (0..1).</summary>
    public double PromoteMinSuccessRate { get; set; } = 0.9;
    /// <summary>A new version that falls below the previous version's success rate over this many runs is reverted.</summary>
    public int AutoRevertMinRuns { get; set; } = 3;
    /// <summary>Saving a script this similar (0..1) to another saved one is refused with "update it instead?" unless forced.</summary>
    public double DuplicateThreshold { get; set; } = 0.8;
    /// <summary>Saved scripts unused this long are flagged for archive by housekeeping (never deleted).</summary>
    public int StaleDays { get; set; } = 14;
    /// <summary>Lint: more static action call sites than this without a cp"label" is a warning.</summary>
    public int LintLongScriptActions { get; set; } = 6;
    /// <summary>wake"reason" at most this often (seconds); extra calls return false.</summary>
    public int MinWakeIntervalSeconds { get; set; } = 30;
    public int StateChars { get; set; } = 400;
    public int ResultChars { get; set; } = 300;

    // ------------------------------------------------------------------ JavaScript (Jint) and the Lua migration

    /// <summary>Largest JS array a script may build (Jint constraint).</summary>
    public int MaxArraySize { get; set; } = 100_000;
    /// <summary>Longest a single regular expression may run, ms.</summary>
    public int RegexTimeoutMs { get; set; } = 500;
    /// <summary>
    /// Legacy runtime: saved Lua versions not yet migrated still run (LuaCSharp). False = a Lua version is refused with
    /// "not migrated yet". New code is always JS (see <see cref="AcceptLuaCode"/>).
    /// </summary>
    public bool LegacyLua { get; set; } = true;
    /// <summary>Inline Lua sent as code (tests and tools only; the brain and the author write JS). Off = refused.</summary>
    public bool AcceptLuaCode { get; set; } = false;
    /// <summary>One-time background migration at startup: every saved Lua script → JS by the Opus author ("Scripting:MigrateLua").</summary>
    public bool MigrateLua { get; set; } = true;
    /// <summary>Author spend cap per migrated script, USD.</summary>
    public decimal MigrateMaxUsdPerScript { get; set; } = 0.25m;
    /// <summary>Author spend cap for the whole migration, USD.</summary>
    public decimal MigrateMaxUsd { get; set; } = 5.00m;
    /// <summary>Machine idle wait between ticks that did no action, ms (not counted as an action).</summary>
    public int MachineIdleMs { get; set; } = 250;

    /// <summary>
    /// The live debugger's probe (Explorer script panel, <c>/api/bodies/{name}/script</c>): action tracing and, only while a
    /// debugger holds a lease, ≈ 5 Hz sampling of stack, locals and blackboard. Off = snapshots show the status line only.
    /// </summary>
    public bool DebugProbe { get; set; } = true;

    // ------------------------------------------------------------------ inline interrupt patches ("Bot:Scripts")

    /// <summary>
    /// Interrupts a few lines of JS can fix (a mob in the way, chest full, a player on the target, a broken tool...) go through the
    /// script's onInterrupt → a learned handler → one small Opus patch call before any full brain turn. Off = every interrupt
    /// wakes the brain as before (declared onInterrupt handlers still run).
    /// </summary>
    public bool Patches { get; set; } = true;
    /// <summary>Host actions one patch (or declared/learned handler) may run.</summary>
    public int PatchMaxActions { get; set; } = 8;
    /// <summary>Wall time one patch may run, seconds (its actions are cut at the deadline).</summary>
    public double PatchMaxSeconds { get; set; } = 45;
    /// <summary>Statements one patch may run (a busy loop in a patch aborts the patch, never the script).</summary>
    public int PatchMaxStatements { get; set; } = 50_000;
    /// <summary>How long a patch turn may take to answer before the interrupt escalates, seconds.</summary>
    public double PatchPlanTimeoutSeconds { get; set; } = 20;
    /// <summary>Patches (all sources) per run; beyond that every interrupt goes to the brain.</summary>
    public int PatchMaxPerRun { get; set; } = 8;
    /// <summary>A patch "worked" when the script progressed and the same interrupt didn't come back within this, seconds.</summary>
    public double PatchVerifySeconds { get; set; } = 90;
    /// <summary>A saved handler that failed this many times in a row is demoted (the patch turn runs again).</summary>
    public int HandlerDemoteAfter { get; set; } = 2;
    /// <summary>restart_step restarts per run (a patch that keeps restarting escalates).</summary>
    public int PatchMaxRestarts { get; set; } = 3;

    /// <summary>A copy to tweak (a hive script's coordinator gets its own time limit).</summary>
    public ScriptOptions Copy() => (ScriptOptions)MemberwiseClone();

    internal TimeSpan MaxWall => TimeSpan.FromMinutes(Math.Max(0.01, MaxMinutes));
    internal TimeSpan MaxPause => TimeSpan.FromMinutes(Math.Max(0.01, MaxPauseMinutes));
}
