using System.Text.RegularExpressions;

namespace MineClaw.Agent.Human;

/// <summary>
/// "Agent:Questions": questions never park the bot. ask_player with a <c>default</c> (the recommended answer) returns at once;
/// the bot keeps working on the default, a timeout announces it, and a late answer comes back as chat so the job is amended.
/// </summary>
public sealed class QuestionOptions
{
    /// <summary>How long to wait for an answer before going with the default.</summary>
    public int TimeoutSeconds { get; set; } = 60;
    public int MinTimeoutSeconds { get; set; } = 5;
    public int MaxTimeoutSeconds { get; set; } = 180;
    /// <summary>After the timeout, the player's next line within this window still counts as the (late) answer.</summary>
    public int LateAnswerSeconds { get; set; } = 120;
    /// <summary>The only default a risky/irreversible question may take.</summary>
    public string SafeDefault { get; set; } = "skip that part";
    /// <summary>Said on timeout ({player}, {default}); empty = say nothing.</summary>
    public string TimeoutLine { get; set; } = "{player}, went with {default} since you're busy, say if you want different";
    /// <summary>Said on timeout of a risky question.</summary>
    public string RiskyTimeoutLine { get; set; } = "{player}, skipped that part for now, say if you want it done";
    /// <summary>
    /// Question/default text that marks a choice as unsafe or irreversible (breaking player builds, void-risk actions, rare
    /// items): its default is forced to <see cref="SafeDefault"/>.
    /// </summary>
    public string RiskyPattern { get; set; } =
        @"\b(?:break|destroy|tear down|demolish|dig (?:up|out|through)|remove|mine (?:out|through))\b.{0,40}\b(?:build|house|wall|base|farm|floor|bridge|their|his|her|your|player)\b" +
        @"|\b(?:void|over the (?:edge|side)|bedrock|into (?:the )?lava)\b|\b(?:diamonds?|obsidian|rare|last (?:one|bucket|sapling)|only (?:one|bucket|sapling))\b";

    private Regex? _risky;

    public bool IsRisky(string text) =>
        !string.IsNullOrWhiteSpace(RiskyPattern) &&
        (_risky ??= new Regex(RiskyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).IsMatch(text ?? "");
}

/// <summary>"Agent:Idle": standing-order resume and the idle default (a bot with nothing to do while players are on).</summary>
public sealed class IdleOptions
{
    /// <summary>Return to the standing order whenever idle (after chat, after a question timeout, after start/respawn).</summary>
    public bool ResumeStandingOrders { get; set; } = true;
    /// <summary>Delay after start before resuming the standing order (lets the join settle).</summary>
    public int ResumeOnStartSeconds { get; set; } = 10;
    /// <summary>A resumed order is not re-issued sooner than this after its last resume.</summary>
    public int MinResumeIntervalSeconds { get; set; } = 90;
    /// <summary>Resumed runs failing this many times in a row pause auto-resume (and wake the brain once).</summary>
    public int MaxResumeFailures { get; set; } = 3;
    /// <summary>After the brain calls stop, no auto-resume for this long.</summary>
    public int StopSnoozeMinutes { get; set; } = 10;

    /// <summary>Idle default: no job, no order, nothing pending for this long while players are online → pick a useful job.</summary>
    public bool IdleDefault { get; set; } = true;
    public double AfterMinutes { get; set; } = 2;
    /// <summary>Allowlist the brain picks from (delegate one) or asks about once, with a default.</summary>
    public string[] Jobs { get; set; } = ["top up cobblestone to a stack in storage", "tidy the storage chest", "replant saplings"];
    /// <summary>How often the idle checks run (seconds).</summary>
    public int CheckSeconds { get; set; } = 30;
}
