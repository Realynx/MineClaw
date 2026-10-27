namespace MineClaw.Scripting;

/// <summary>
/// Limits and interrupt policy for bot scripts ("Bot:Scripts"). Plain get/set so configuration binding works. Every
/// limit breach ends the script with a terse failure; none of them can be caught by the script itself.
/// </summary>
public sealed class ScriptOptions
{
    /// <summary>Max Lua VM instructions per run (all segments together).</summary>
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
    /// <summary>Addressed chat pauses the script (the brain may re-script); it resumes by itself after this many seconds.</summary>
    public bool PauseOnChat { get; set; } = true;
    public int ChatAutoResumeSeconds { get; set; } = 30;
    /// <summary>say/emote rate limit.</summary>
    public int MaxSayPerMinute { get; set; } = 6;
    /// <summary>Longest single wait/wait_for/ask, seconds.</summary>
    public int MaxWaitSeconds { get; set; } = 600;
    /// <summary>Worker calls wait this long for the script inline, then it continues as the bot's task.</summary>
    public int WorkerInlineSeconds { get; set; } = 60;
    /// <summary>Actions skipped while fast-forwarding to a resume_from checkpoint before giving up.</summary>
    public int MaxSkippedActions { get; set; } = 500;
    /// <summary>Successful runs before a saved script joins the skill catalog.</summary>
    public int PromoteAfterSuccesses { get; set; } = 3;
    /// <summary>wake"reason" at most this often (seconds); extra calls return false.</summary>
    public int MinWakeIntervalSeconds { get; set; } = 30;
    public int StateChars { get; set; } = 400;
    public int ResultChars { get; set; } = 300;

    internal TimeSpan MaxWall => TimeSpan.FromMinutes(Math.Max(0.01, MaxMinutes));
    internal TimeSpan MaxPause => TimeSpan.FromMinutes(Math.Max(0.01, MaxPauseMinutes));
}
