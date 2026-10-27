using System.Text.Json;
using MineClaw.Agent.Human;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;
using MineClaw.Hive.Ipc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Clone;

/// <summary>
/// A clone's standing order follows main's tasks. An explicit task from main REPLACES whatever order the clone had stored
/// (an earlier hive job, an order main set for it before) and becomes the order itself ("[standing: mine the cobble gen
/// (hive)]" in the turn header), so the brain never goes back to the old job. Live 3, 14:33: 69 was recalled and spawned
/// again with a cobble task, then found "the tree farm" as its standing order in memory, asked, and resumed the trees.
/// Ordering: a process started with a task holds every stored order until that task is applied (the brain loop's "start"
/// resume would otherwise re-issue the old order before the task frame's loadout walk ends), and an old order that had
/// already been resumed is stopped when the new task lands. The brain-task order is a record, never re-issued by C#
/// (the brain runs the task; a script task is the <see cref="CloneScriptRunner"/>'s order). Old orders resume only when
/// a spawn brings no task.
/// </summary>
public sealed class CloneTaskOrders
{
    /// <summary>Input field that marks a brain-task order (holds the task id).</summary>
    public const string TaskField = "hive_task";

    private readonly StandingOrders _orders;
    private readonly MemoryStore _memory;
    private readonly ActivityManager _activities;
    private readonly ILogger _log;

    public CloneTaskOrders(StandingOrders orders, MemoryStore memory, ActivityManager activities, ILogger? log = null)
    {
        _orders = orders;
        _memory = memory;
        _activities = activities;
        _log = log ?? NullLogger.Instance;
        var prev = orders.CanResume;
        orders.CanResume = o => !IsBrainTask(o) && (prev?.Invoke(o) ?? true);
    }

    public string Bot => _orders.Bot;

    /// <summary>The process was started with a task: no stored order resumes before it is applied.</summary>
    public void HoldForTask() => _orders.Held = true;

    /// <summary>
    /// A brain task from main: it becomes this clone's standing order (replacing any other), an old order that is running
    /// stops, and auto-resume is free again for orders main sets later. Returns the replaced order's job (null = none).
    /// </summary>
    public string? Apply(TaskMsg t)
    {
        var old = _orders.Current;
        bool same = old != null && IsBrainTask(old) && TaskIdOf(old) == t.Id;
        string? replaced = old != null && !same ? old.Job : null;
        // A resumed old order is this clone's current activity: the new task replaces it.
        if (replaced != null && old!.LastResumed != null && !IsBrainTask(old) && _activities.IsBusy)
            _activities.Cancel("new task from main");
        _memory.SetStandingOrder(new StandingOrder
        {
            Bot = Bot, Player = CloneScriptRunner.OrderPlayer, Job = Label(t.Text), Tool = MineClaw.Agent.Worker.WorkerAgent.ToolName,
            Input = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["task"] = t.Text, [TaskField] = t.Id }),
        });
        _orders.Held = false;
        if (replaced != null) _log.LogInformation("clone {Bot}: new task from main replaces standing order '{Old}'", Bot, replaced);
        return replaced;
    }

    public static bool IsBrainTask(StandingOrder o) =>
        o.Player == CloneScriptRunner.OrderPlayer && o.Input.ValueKind == JsonValueKind.Object && o.Input.TryGetProperty(TaskField, out _);

    private static string? TaskIdOf(StandingOrder o) =>
        o.Input.ValueKind == JsonValueKind.Object && o.Input.TryGetProperty(TaskField, out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    private static string Label(string text)
    {
        var one = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // Up to the first clause break, at most 60 chars ("Mine the cobble generator on main island at 4,64,6").
        int cut = one.IndexOfAny(['.', ';', '(']);
        if (cut > 12) one = one[..cut];
        return one.Length > 60 ? one[..59].TrimEnd() + "…" : one;
    }
}
