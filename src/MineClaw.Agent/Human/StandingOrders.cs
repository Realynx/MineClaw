using System.Text.Json;
using System.Text.Json.Nodes;
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
    }

    /// <summary>This bot's login (whose order we run).</summary>
    public string Bot { get; }

    /// <summary>This bot's active order (fresh from the shared db).</summary>
    public StandingOrder? Current => _memory.GetStandingOrder(Bot);

    /// <summary>"[standing: mine gen cell B (fox)]" for the turn header, or null.</summary>
    public string? HeaderTag() => Current?.HeaderTag;

    /// <summary>No resume while set (a hive clone holds its script order until its loadout is fetched).</summary>
    public bool Held { get; set; }

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
        var now = _time.GetUtcNow();
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
        int failures = 0;
        _memory.UpdateStandingOrder(Bot, o =>
        {
            o.FailuresInRow = ok ? 0 : o.FailuresInRow + 1;
            o.LastOutcome = TextUtil.Truncate(TextUtil.OneLine(result.Text), 160);
            failures = o.FailuresInRow;
        });
        if (!ok && failures == Math.Max(1, _o.MaxResumeFailures))
            _events.Post(GameEvent.Notice($"standing order keeps failing ({failures}x), auto-resume paused: {TextUtil.Truncate(result.Text, 120)}",
                EventPriority.Normal));
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
