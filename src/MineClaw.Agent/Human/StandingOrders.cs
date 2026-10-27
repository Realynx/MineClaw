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

    /// <summary>Waits after consecutive "nothing yet" passes: 2 → 4 → 8 → 15 min.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(15)];

    private int _nothingStreak;
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

    /// <summary>The brain stopped everything: no auto-resume for a while (danger, "stop" from a player).</summary>
    public void Snooze(TimeSpan? span = null)
    {
        lock (_lock) _snoozedUntil = _time.GetUtcNow() + (span ?? TimeSpan.FromMinutes(Math.Max(0, _o.StopSnoozeMinutes)));
    }

    /// <summary>
    /// Re-issues the standing order when the bot is idle (no activity, nothing waking pending, not snoozed, not resumed too
    /// recently, not failing repeatedly). Returns true when it started it. <paramref name="why"/> is for logs.
    /// </summary>
    public bool ResumeIfIdle(string why, CancellationToken shutdown = default)
    {
        if (!_o.ResumeStandingOrders || Held) return false;
        var o = Current;
        if (o == null || _activities.IsBusy || _events.HasWaking) return false;
        if (CanResume is { } can && !can(o)) return false;
        var now = _time.GetUtcNow();
        bool backingOff;
        lock (_lock) backingOff = now < _backoffUntil;
        if (backingOff)
        {
            bool? ready = null;
            try { ready = WorkReady?.Invoke(o); } catch (Exception) { }
            if (ready != true) return false;
            _log.LogInformation("standing order {Job}: work is ready again, ending the backoff", o.Job);
        }
        lock (_lock)
        {
            if (now < _snoozedUntil) return false;
            if (o.LastResumed is { } last && now - last < TimeSpan.FromSeconds(Math.Max(0, _o.MinResumeIntervalSeconds))) return false;
            if (o.FailuresInRow >= Math.Max(1, _o.MaxResumeFailures)) return false;
        }
        if (!_tools.TryGet(o.Tool, out var tool))
        {
            _log.LogWarning("standing order {Job}: tool {Tool} missing", o.Job, o.Tool);
            return false;
        }
        lock (_lock) _resumedSignature = ToolCallGuard.Signature(o.Tool, o.Input);
        _memory.UpdateStandingOrder(Bot, x => { x.Resumes++; x.LastResumed = now; });
        _log.LogInformation("standing order resumed ({Why}): {Job} via {Tool}", why, o.Job, o.Tool);
        // Background activity, like any long task: its end posts [task ok]/[task failed]; stop/new activities cancel it.
        _ = _activities.RunAsync([(tool, o.Input)], TimeSpan.Zero, shutdown);
        try { Resumed?.Invoke(o, why); } catch (Exception) { }
        return true;
    }

    private void OnStepFinished(string tool, JsonElement input, ToolResult result, string? cancelReason)
    {
        string? sig;
        lock (_lock) sig = _resumedSignature;
        if (sig == null || ToolCallGuard.Signature(tool, input) != sig || cancelReason != null) return;
        bool ok = result.Ok;
        if (NothingYet.IsMatch(result.Text ?? ""))
        {
            // Not a failure and nothing for the brain: wait in C# (probe the world, else 2 → 4 → 8 → 15 min), no LLM call.
            TimeSpan wait;
            lock (_lock)
            {
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
