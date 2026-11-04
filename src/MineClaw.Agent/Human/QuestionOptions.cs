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
    public string RiskyTimeoutLine { get; set; } = "{player}, skipped {topic} for now, say if you want it done";
    /// <summary>
    /// Said on timeout when the default is an instruction to the bot itself ("Stay on the tree farm standing order; skip the
    /// cobble gen unless told."): {plan} is its first clause, jargon dropped ("stay on the tree farm"). Live 3, 14:35:08 posted
    /// "went with Stay on the tree farm standing order; skip the cobble gen unless told. since you're busy".
    /// </summary>
    public string PlanTimeoutLine { get; set; } = "{player}, gonna {plan} for now since you're busy, say if you want different";
    /// <summary>Said on timeout when the default can't be put into words for a player (long, coordinates).</summary>
    public string VagueTimeoutLine { get; set; } = "{player}, going with my best guess for now since you're busy, say if you want different";

    private static readonly Regex Jargon = new(
        @"\s*\b(?:(?:my|the|your|this)\s+)?(?:standing\s+order|hive\s+task|default|hive|task\s+order)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ClauseBreak = new(@"[;.!?\n(]|\s+(?:unless|until|then|but|and then)\s+|\s+[-\u2013\u2014]\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Coords = new(@"-?\d+\s*,\s*-?\d+", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> PlanVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "stay", "keep", "mine", "chop", "go", "skip", "wait", "continue", "do", "work", "farm", "finish", "craft", "build", "help",
        "follow", "come", "bring", "use", "take", "leave", "stop", "start", "head", "put", "place", "dig", "harvest", "plant",
        "replant", "collect", "gather", "get", "make", "fill", "check", "return", "carry", "hold", "stick", "cut", "clear", "deposit",
    };

    /// <summary>
    /// The line said when a question timed out: never the raw default when that is an instruction or jargon. A short choice
    /// ("left cell") keeps <see cref="TimeoutLine"/>; an instruction becomes "gonna stay on the tree farm for now ...".
    /// </summary>
    public string TimeoutText(string? player, string dflt, bool risky, string? question = null)
    {
        // Live 10-28: "skipped that part for now" posted 4× with no hint which question it meant.
        string line = risky ? RiskyTimeoutLine.Replace("{topic}", Topic(question) is { } t ? $"\"{t}\"" : "that part") : DefaultLine(dflt, question);
        return line.Replace("{player}", player ?? "").Replace("{default}", dflt ?? "").Trim(' ', ',');
    }

    /// <summary>A safe question's timeout line: the short choice itself, the plan it names, or a vague "best guess".</summary>
    private string DefaultLine(string dflt, string? question)
    {
        var d = (dflt ?? "").Trim();
        var clause = ClauseBreak.Split(d)[0];
        clause = Regex.Replace(Jargon.Replace(clause, ""), @"\s+", " ").Trim(' ', ',', ':');
        var words = clause.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool plan = words.Length > 0 && PlanVerbs.Contains(words[0]);
        if (IsPlainChoice(d, clause, words, plan)) return TimeoutLine.Replace("{default}", d);
        if (plan && words.Length <= 10 && !Coords.IsMatch(clause))
        {
            var p = char.ToLowerInvariant(clause[0]) + clause[1..];
            p = Regex.Replace(p, @"\s+for now$", "", RegexOptions.IgnoreCase);
            return PlanTimeoutLine.Replace("{plan}", p);
        }
        return Topic(question) is { } t
            ? VagueTimeoutLine.Replace("my best guess", $"my best guess on \"{t}\"") // which question (live 10-28 11:1x)
            : VagueTimeoutLine;
    }

    /// <summary>A short plain choice ("left cell"): no instruction, no jargon cut off, no coordinates.</summary>
    private static bool IsPlainChoice(string d, string clause, string[] words, bool plan) =>
        !plan && clause == d.TrimEnd('.', '!') && words.Length is > 0 and <= 5 && !Coords.IsMatch(d);

    private static readonly Regex AskLead = new(
        @"^\s*(?:\w+[,:]\s*)?(?:master~?\s*)?(?:(?:can|could|may|should|shall)\s+i|do you want me to|want me to|(?:is it\s+)?ok(?:ay)?\s+(?:if i|to)|mind if i)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"can I redesign the tower to fit your floor?" → "redesign the tower to fit your floor" (≤8 words); null when empty.</summary>
    internal static string? Topic(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return null;
        // The asked part is the last sentence ending in '?' ("…175 columns hang over the void. switch to plan #8 instead?").
        var asks = Regex.Matches(question, @"[^.!?]*\?").Select(m => m.Value).Where(a => a.Trim().Length > 1).ToList();
        var q = asks.Count > 0 ? asks[^1] : question.Split('?', '.', '!')[0];
        // Comma-joined asks ("…hangs over the void with no scaffold, want me to move it to your floor instead?"): the clause
        // that opens with an ask lead is the question.
        var clauses = q.Split(',');
        if (clauses.Length > 1 && clauses.Skip(1).LastOrDefault(c => AskLead.IsMatch(c)) is { } ask) q = ask;
        q = AskLead.Replace(q, "");
        q = Coords.Replace(q, "").Trim(' ', ',', ':', '~', '?');
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;
        return string.Join(' ', words.Take(8)) + (words.Length > 8 ? "…" : "");
    }

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

    /// <summary>
    /// Once per process start (main only): one Normal wake "restarted: standing order X, paused build Z, rules Y" so the brain
    /// re-plans once by its learned rules (live 10-28 07:37-08:45: five restarts, no turn for 70 min).
    /// </summary>
    public bool StartupWake { get; set; } = true;
    /// <summary>
    /// Idle watchdog (main only): idle this many minutes with nothing running or waking while a standing order is stuck or a
    /// paused job waits → one wake. Repeats back off (×2 per "nothing to do" answer) up to <see cref="WatchdogMaxMinutes"/>.
    /// 0 = off.
    /// </summary>
    public double WatchdogMinutes { get; set; } = 10;
    public double WatchdogMaxMinutes { get; set; } = 80;
}
