using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Human;

/// <summary>
/// Standing orders for one bot: the <c>standing_order</c> tool (brain sets/clears/shows; main may set one for a clone) and the
/// zero-token resume — when the bot is idle, C# re-issues the order's tool call as the bot's background activity. Resume
/// points: after a turn, after a question timed out, at start (restart / clone respawn), on the idle timer.
/// </summary>
public sealed class StandingOrders
{
    public const string ToolName = "standing_order";

    private readonly MemoryStore _memory;
    private readonly ToolRegistry _tools;
    private readonly ActivityManager _activities;
    private readonly EventQueue _events;
    private readonly IdleOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly object _lock = new();
    private DateTimeOffset _snoozedUntil = DateTimeOffset.MinValue;
    private string? _resumedSignature;

    public StandingOrders(MemoryStore memory, ToolRegistry tools, ActivityManager activities, EventQueue events, string bot,
        IdleOptions? options = null, TimeProvider? time = null, ILogger? log = null)
    {
        _memory = memory;
        _tools = tools;
        _activities = activities;
        _events = events;
        Bot = bot;
        _o = options ?? new IdleOptions();
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _activities.StepFinished += OnStepFinished;
        _activities.QuietOutcome = IsQuietOutcome;
    }

    // ------------------------------------------------------------------ "nothing to do yet" backoff (zero tokens)

    /// <summary>
    /// A pass of the order that found nothing to do ("no grown oak trees... 17 saplings still growing... Nothing chopped"): the
    /// order isn't failing, the world just isn't ready.
    /// </summary>
    internal static readonly Regex NothingYet = new(
        @"\bnothing\s+(?:to\s+(?:do|chop|mine|harvest|collect)|chopped|mined|harvested|collected|ready|grown|left\s+to|grew)\b" +
        @"|\bno\s+(?:more\s+)?(?:grown|mature|ripe|ready|full-?grown)\b|\b(?:still|only)\s+(?:growing|saplings?|seeds?)\b" +
        @"|\bnot\s+(?:grown|ready|mature|ripe|regenerated)(?:\s+yet)?\b|\b0\s+(?:chopped|mined|harvested|logs?|trees?)\b|\bnone\s+(?:ready|grown)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A pass that stopped because every known chest is full (the deposit tool's line: "all 3 known chests full (…); 141 cobblestone
    /// in bag; craft+place a chest (have 300 logs) or keep carrying"). Not the order failing: storage is the blocker, and only the
    /// brain decides to place a chest or carry on. Live 10-28 11:10/12:05: gen_loop failed "store: chest is full" every pass.
    /// </summary>
    internal static readonly Regex StorageFull = new(
        @"\b(?:all \d+ known chests? full|no known chest)\b.*?(?:or keep carrying|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    /// <summary>Waits after consecutive "nothing yet" passes: 2 → 4 → 8 → 15 min.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(15)];

    private int _nothingStreak;
    private bool _storageFull;      // the current backoff waits on full chests: the work probe can't end it
    private string? _fullWoke;      // the storage-full line the brain already heard (once per stretch of full passes)
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private string? _quietText;
    private ITimer? _poll;

    /// <summary>
    /// Bot layer: is there work for this order again, read from the world model (a sapling grew into a tree, a gen cell
    /// regenerated, a crop matured)? True ends the backoff at once; null = can't tell (the backoff runs out instead).
    /// </summary>
    public Func<StandingOrder, bool?>? WorkReady { get; set; }

    /// <summary>When the order may next resume by itself (MinValue = not backing off).</summary>
    public DateTimeOffset BackoffUntil { get { lock (_lock) return _backoffUntil; } }

    /// <summary>Consecutive passes that found nothing to do.</summary>
    public int NothingStreak { get { lock (_lock) return _nothingStreak; } }

    /// <summary>How often the work-ready probe is polled while backing off.</summary>
    public TimeSpan ProbeEvery { get; init; } = TimeSpan.FromSeconds(20);

    private bool IsQuietOutcome(string text)
    {
        lock (_lock)
        {
            if (_quietText == null || !text.Contains(_quietText, StringComparison.Ordinal)) return false;
            _quietText = null;
            return true;
        }
    }

    private void StartProbe()
    {
        if (WorkReady == null) return;
        lock (_lock)
            _poll ??= _time.CreateTimer(_ => { try { ProbeTick(); } catch (Exception) { } }, null, ProbeEvery, ProbeEvery);
    }

    /// <summary>One probe poll (the timer calls it; tests call it directly): resumes the order as soon as there's work again.</summary>
    public bool ProbeTick()
    {
        if (BackoffUntil == DateTimeOffset.MinValue) { StopProbe(); return false; }
        return ResumeIfIdle("work ready");
    }

    private void StopProbe()
    {
        lock (_lock) { _poll?.Dispose(); _poll = null; }
    }

    /// <summary>This bot's login (whose order we run).</summary>
    public string Bot { get; }

    /// <summary>This bot's active order (fresh from the shared db).</summary>
    public StandingOrder? Current => _memory.GetStandingOrder(Bot);

    /// <summary>"[standing: mine gen cell B (fox)]" for the turn header, or null.</summary>
    public string? HeaderTag() => Current?.HeaderTag;

    /// <summary>No resume while set (a hive clone holds its script order until its loadout is fetched).</summary>
    public bool Held { get; set; }

    /// <summary>False for an order C# must not re-issue (a hive clone's brain task is its order but the brain runs it). Null = all.</summary>
    public Func<StandingOrder, bool>? CanResume { get; set; }

    /// <summary>Raised after an order was re-issued (tests, logs).</summary>
    public event Action<StandingOrder, string>? Resumed;

    /// <summary>
    /// True while something else holds the body (a live script on this body's lane, paused included): no resume then. The
    /// script tool answers "already running" to a second run, and live 10-28 03:44-03:48 those answers counted as three failed
    /// resumes, paused auto-resume in the db for good, and main sat idle after every later restart.
    /// </summary>
    public Func<bool>? BodyBusy { get; set; }

    /// <summary>
    /// Bot layer: checks an order's call before C# re-issues it (the saved script still exists and is JS after the Lua→JS
    /// migration; a pinned old version re-resolves to the newest). <see cref="OrderCheck.Input"/> = the call rewritten (persisted);
    /// <see cref="OrderCheck.Problem"/> = it can't run as stored (the brain is woken once with why). Null = re-issue as stored.
    /// </summary>
    public Func<StandingOrder, OrderCheck>? Prepare { get; set; }

    private string? _problemPosted;
    private string? _lastProblem;

    /// <summary>The brain stopped everything: no auto-resume for a while (danger, "stop" from a player).</summary>
    public void Snooze(TimeSpan? span = null)
    {
        lock (_lock) _snoozedUntil = _time.GetUtcNow() + (span ?? TimeSpan.FromMinutes(Math.Max(0, _o.StopSnoozeMinutes)));
    }

    /// <summary>
    /// Re-issues the standing order when the bot is idle (no activity, no script holding the body, nothing waking pending, not
    /// snoozed, not resumed too recently, not failing repeatedly). Returns true when it started it. <paramref name="why"/> is
    /// for logs; "start" (the restart resume) skips the min-interval check: the run it guards died with the old process.
    /// </summary>
    public bool ResumeIfIdle(string why, CancellationToken shutdown = default)
    {
        if (!_o.ResumeStandingOrders || Held) return false;
        var o = Current;
        if (o == null || _activities.IsBusy || _events.HasWaking) return false;
        if (CanResume is { } can && !can(o)) return false;
        try { if (BodyBusy?.Invoke() == true) return false; } catch (Exception) { }
        var now = _time.GetUtcNow();
        bool backingOff;
        lock (_lock) backingOff = now < _backoffUntil;
        if (backingOff)
        {
            bool? ready = null;
            bool full;
            lock (_lock) full = _storageFull;
            // Full chests: a regenerated gen cell is no reason to try again (WorkReady counts as false until the wait runs out).
            if (!full) try { ready = WorkReady?.Invoke(o); } catch (Exception) { }
            if (ready != true) return false;
            _log.LogInformation("standing order {Job}: work is ready again, ending the backoff", o.Job);
        }
        lock (_lock)
        {
            if (now < _snoozedUntil) return false;
            if (why != "start" && o.LastResumed is { } last && now - last < TimeSpan.FromSeconds(Math.Max(0, _o.MinResumeIntervalSeconds))) return false;
            if (o.FailuresInRow >= Math.Max(1, _o.MaxResumeFailures)) return false;
        }
        if (!_tools.TryGet(o.Tool, out var tool))
        {
            _log.LogWarning("standing order {Job}: tool {Tool} missing", o.Job, o.Tool);
            ReportProblem(o, $"its tool {o.Tool} isn't available here");
            return false;
        }
        if (Prepare is { } prepare)
        {
            OrderCheck check;
            try { check = prepare(o); }
            catch (Exception ex) { _log.LogDebug(ex, "standing order check failed"); check = default; }
            if (check.Problem is { Length: > 0 } problem)
            {
                ReportProblem(o, problem);
                return false;
            }
            if (check.Input is { } rewritten)
            {
                o.Input = rewritten;
                _memory.UpdateStandingOrder(Bot, x => x.Input = rewritten);
                _log.LogInformation("standing order {Job}: call updated ({Note})", o.Job, check.Note ?? "re-resolved");
            }
        }
        lock (_lock)
        {
            _resumedSignature = ToolCallGuard.Signature(o.Tool, o.Input);
            _problemPosted = null;
            _lastProblem = null;
        }
        _memory.UpdateStandingOrder(Bot, x => { x.Resumes++; x.LastResumed = now; });
        _log.LogInformation("standing order resumed ({Why}): {Job} via {Tool}", why, o.Job, o.Tool);
        // Background activity, like any long task: its end posts [task ok]/[task failed]; stop/new activities cancel it.
        _ = _activities.RunAsync([(tool, o.Input)], TimeSpan.Zero, shutdown);
        try { Resumed?.Invoke(o, why); } catch (Exception) { }
        return true;
    }

    /// <summary>An order whose call can't run as stored: one Normal wake per problem (never silent, never a loop).</summary>
    private void ReportProblem(StandingOrder o, string problem)
    {
        var key = ToolCallGuard.Signature(o.Tool, o.Input) + "|" + problem;
        lock (_lock)
        {
            _lastProblem = problem;
            if (_problemPosted == key) return;
            _problemPosted = key;
        }
        _log.LogWarning("standing order {Job} can't resume: {Problem}", o.Job, problem);
        _events.Post(GameEvent.Notice($"standing order \"{o.Job}\" can't resume: {TextUtil.Truncate(problem, 160)}; set it again " +
            "(standing_order set) or clear it", EventPriority.Normal) with { MustWake = true });
    }

    /// <summary>
    /// Once per process start, before the start resume: a failure pause persisted by the previous process is lifted (a restart
    /// is a fresh chance, usually after a fix; three new failures pause it again and wake the brain). Returns a note for the
    /// startup wake ("auto-resume had paused after 3 failures (last: ...); retrying") or null.
    /// </summary>
    public string? OnProcessStart()
    {
        var o = Current;
        if (o == null || o.FailuresInRow < Math.Max(1, _o.MaxResumeFailures)) return null;
        _memory.UpdateStandingOrder(Bot, x => x.FailuresInRow = 0);
        _log.LogInformation("standing order {Job}: failure pause ({N}x) lifted by the restart", o.Job, o.FailuresInRow);
        return $"auto-resume had paused after {o.FailuresInRow} failures" +
               (o.LastOutcome is { Length: > 0 } l ? $" (last: {TextUtil.Truncate(l, 80)})" : "") + "; retrying";
    }

    /// <summary>Why the order isn't running by itself right now ("auto-resume paused after 3 failures"), or null.</summary>
    public string? WhyNot()
    {
        var o = Current;
        if (o == null) return null;
        if (!_o.ResumeStandingOrders) return "auto-resume is off";
        if (Held) return "held";
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_lastProblem != null) return "can't resume: " + _lastProblem;
            if (o.FailuresInRow >= Math.Max(1, _o.MaxResumeFailures))
                return $"auto-resume paused after {o.FailuresInRow} failures" +
                       (o.LastOutcome is { Length: > 0 } l ? $" (last: {TextUtil.Truncate(l, 80)})" : "");
            if (now < _snoozedUntil) return $"snoozed after a stop ({(int)Math.Ceiling((_snoozedUntil - now).TotalMinutes)}m left)";
            if (now < _backoffUntil && _storageFull)
                return $"storage full on the last pass, next try in {(int)Math.Ceiling((_backoffUntil - now).TotalMinutes)}m";
            if (now < _backoffUntil) return $"nothing to do on the last pass, next check in {(int)Math.Ceiling((_backoffUntil - now).TotalMinutes)}m";
        }
        return null;
    }

    /// <summary>"already running: ..." — the same script/job is live: the order is in effect, not a failed pass.</summary>
    internal static bool AlreadyRunning(string? text) =>
        (text ?? "").TrimStart().StartsWith("already running", StringComparison.OrdinalIgnoreCase);

    private void OnStepFinished(string tool, JsonElement input, ToolResult result, string? cancelReason)
    {
        string? sig;
        lock (_lock) sig = _resumedSignature;
        if (sig == null || ToolCallGuard.Signature(tool, input) != sig || cancelReason != null) return;
        bool ok = result.Ok;
        if (AlreadyRunning(result.Text))
        {
            _memory.UpdateStandingOrder(Bot, o => o.LastOutcome = TextUtil.Truncate(TextUtil.OneLine(result.Text ?? ""), 160));
            return;
        }
        if (!ok && StorageFull.Match(result.Text ?? "") is { Success: true } full)
        {
            OnStorageFull(result.Text!, full.Value);
            return;
        }
        if (NothingYet.IsMatch(result.Text ?? ""))
        {
            // Not a failure and nothing for the brain: wait in C# (probe the world, else 2 → 4 → 8 → 15 min), no LLM call.
            TimeSpan wait;
            lock (_lock)
            {
                if (_storageFull) _nothingStreak = 0;
                _storageFull = false;
                _nothingStreak++;
                wait = Backoff[Math.Min(_nothingStreak, Backoff.Length) - 1];
                _backoffUntil = _time.GetUtcNow() + wait;
                var head = (result.Text ?? "").Trim().Split('\n')[0];
                _quietText = head.Length > 0 ? head[..Math.Min(60, head.Length)] : null;
            }
            _memory.UpdateStandingOrder(Bot, o => o.LastOutcome = TextUtil.Truncate(TextUtil.OneLine(result.Text), 160));
            _log.LogInformation("standing order: nothing to do yet ({N}x), backing off {Min} min", _nothingStreak, wait.TotalMinutes);
            StartProbe();
            return;
        }
        lock (_lock)
        {
            _nothingStreak = 0;
            _backoffUntil = DateTimeOffset.MinValue;
            _storageFull = false;
            _fullWoke = null;
        }
        StopProbe();
        int failures = 0;
        _memory.UpdateStandingOrder(Bot, o =>
        {
            o.FailuresInRow = ok ? 0 : o.FailuresInRow + 1;
            o.LastOutcome = TextUtil.Truncate(TextUtil.OneLine(result.Text), 160);
            failures = o.FailuresInRow;
        });
        if (!ok && failures == Math.Max(1, _o.MaxResumeFailures))
            _events.Post(GameEvent.Notice($"standing order keeps failing ({failures}x), auto-resume paused: {TextUtil.Truncate(result.Text, 120)}",
                EventPriority.Normal) with { MustWake = true });
    }

    /// <summary>
    /// Every known chest full: like work not ready — back off (2 → 4 → 8 → 15 min, the work probe can't cut it short), no failure
    /// counted, and the brain hears the line once (Normal, MustWake) instead of three failures and a paused order.
    /// </summary>
    private void OnStorageFull(string text, string line)
    {
        TimeSpan wait;
        bool wake;
        lock (_lock)
        {
            if (!_storageFull) _nothingStreak = 0;
            _storageFull = true;
            _nothingStreak++;
            wait = Backoff[Math.Min(_nothingStreak, Backoff.Length) - 1];
            _backoffUntil = _time.GetUtcNow() + wait;
            var head = text.Trim().Split('\n')[0];
            _quietText = head.Length > 0 ? head[..Math.Min(60, head.Length)] : null; // the [task failed] for it stays context
            wake = _fullWoke == null;
            _fullWoke = line;
        }
        var o = Current;
        _memory.UpdateStandingOrder(Bot, x => x.LastOutcome = TextUtil.Truncate(TextUtil.OneLine(text), 160));
        _log.LogInformation("standing order: storage full ({N}x), backing off {Min} min", _nothingStreak, wait.TotalMinutes);
        if (wake)
            _events.Post(GameEvent.Notice($"standing order \"{o?.Job}\" is waiting (not failing): {TextUtil.Truncate(TextUtil.OneLine(line), 200)}; " +
                $"next try in {(int)wait.TotalMinutes}m", EventPriority.Normal) with { MustWake = true });
    }

    // ------------------------------------------------------------------ the tool

    public const string Description =
        "Ongoing job a player gave (\"mine the gen all night\"), resumed whenever idle. set: job + script(+args) or task(+done_when); " +
        "clear on \"that's enough\"; for=clone.";

    public void Register()
    {
        _tools.Register(ToolName, Description,
            ToolSchema.Create().Str("op", enumValues: ["set", "clear", "show"]).Str("job", required: false)
                .Str("script", required: false).Raw("args", """{"type":"object"}""", required: false)
                .Str("task", required: false).Str("done_when", required: false).Str("for", required: false).Build(),
            (input, _) => Task.FromResult(Execute(input)), tiers: ToolTiers.Main);
    }

    private ToolResult Execute(JsonElement input)
    {
        var op = (input.Str("op") ?? "show").Trim().ToLowerInvariant();
        var bot = input.Str("for") is { Length: > 0 } f ? f.Trim() : Bot;
        var player = TurnContext.Current?.Speaker;
        switch (op)
        {
            case "show":
                return ToolResult.Success(_memory.GetStandingOrder(bot) is { } cur ? $"{cur.HeaderTag} via {cur.Tool}; resumed {cur.Resumes}x" +
                    (cur.LastOutcome != null ? "; last: " + cur.LastOutcome : "") : "no standing order");
            case "clear":
                return _memory.ClearStandingOrder(bot, player ?? "brain") ? ToolResult.Success($"standing order of {bot} cleared")
                    : ToolResult.Success("no standing order");
            case "set":
            {
                var job = input.Str("job");
                string tool;
                JsonElement args;
                if (input.Str("script") is { Length: > 0 } script)
                {
                    tool = "script";
                    var o = new JsonObject { ["op"] = "run", ["name"] = script.Trim() };
                    if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                        o["args"] = JsonNode.Parse(a.GetRawText());
                    args = JsonSerializer.SerializeToElement(o);
                    job ??= script;
                }
                else if (input.Str("task") is { Length: > 0 } task)
                {
                    tool = Worker.WorkerAgent.ToolName;
                    var o = new JsonObject { ["task"] = task.Trim() };
                    if (input.Str("done_when") is { Length: > 0 } dw) o["done_when"] = dw.Trim();
                    args = JsonSerializer.SerializeToElement(o);
                    job ??= task;
                }
                else return ToolResult.Error("set needs script (saved name) or task");
                if (!_tools.Contains(tool)) return ToolResult.Error($"no {tool} tool here");
                var order = _memory.SetStandingOrder(new StandingOrder
                {
                    Bot = bot, Player = player, Job = TextUtil.Truncate(TextUtil.OneLine(job!.Trim()), 60), Tool = tool, Input = args,
                });
                // Our own order starts right away when idle; a clone picks its up through the shared db.
                bool started = string.Equals(bot, Bot, StringComparison.OrdinalIgnoreCase) && ResumeIfIdle("set");
                return ToolResult.Success($"standing order set: {order.HeaderTag}" + (started ? "; started" : "; resumes when idle"));
            }
            default:
                return ToolResult.Error("op is set|clear|show");
        }
    }
}

/// <summary>
/// <see cref="StandingOrders.Prepare"/>'s verdict on an order's stored call: <see cref="Input"/> = the call to use instead
/// (persisted), <see cref="Problem"/> = why it can't run (the brain hears it once), <see cref="Note"/> = what was rewritten.
/// Default = run as stored.
/// </summary>
public readonly record struct OrderCheck(JsonElement? Input = null, string? Problem = null, string? Note = null)
{
    public static OrderCheck Rewrite(JsonElement input, string note) => new(input, null, note);
    public static OrderCheck Invalid(string problem) => new(null, problem);
}
