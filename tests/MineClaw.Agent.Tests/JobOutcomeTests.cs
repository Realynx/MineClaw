using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class JobOutcomeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_chain_reports_earlier_counts_even_when_its_last_step_fails(bool fail)
    {
        var tools = new ToolRegistry();
        var events = new EventQueue();
        var acts = new ActivityManager(tools, events);
        var gate = new TaskCompletionSource(ToolCreationOptions());
        tools.Register("dig", "dig", ToolSchema.Create().Build(), async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return ToolResult.Success("dug 0; already air at y66 (16 skipped)");
        }, longRunning: true);
        tools.Register("plant", "plant", ToolSchema.Create().Build(), (_, _) =>
            Task.FromResult(fail ? ToolResult.Error("planted 0: no seeds") : ToolResult.Success("planted 12")), longRunning: true);
        tools.TryGet("dig", out var dig);
        tools.TryGet("plant", out var plant);
        await acts.RunAsync([(dig!, JsonSerializer.SerializeToElement(new { })), (plant!, JsonSerializer.SerializeToElement(new { }))], TimeSpan.Zero, default);
        gate.SetResult();
        await Until(() => events.Snapshot().Count > 0);
        var e = Assert.Single(events.Snapshot());
        Assert.Contains("dug 0", e.Text);
        Assert.Contains("already air at y66", e.Text);
        Assert.Contains(fail ? "planted 0: no seeds" : "planted 12", e.Text);
    }

    [Fact]
    public async Task Explicit_cancellation_reports_the_partial_job_instead_of_disappearing()
    {
        var tools = new ToolRegistry();
        var events = new EventQueue();
        var acts = new ActivityManager(tools, events);
        var entered = new TaskCompletionSource(ToolCreationOptions());
        tools.Register("field", "field", ToolSchema.Create().Build(), async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { return ToolResult.Error("dug 3; planted 0: interrupted"); }
            return ToolResult.Success("never");
        }, longRunning: true);
        tools.TryGet("field", out var field);
        await acts.RunAsync([(field!, JsonSerializer.SerializeToElement(new { }))], TimeSpan.Zero, default);
        await entered.Task;
        acts.Cancel("stop");
        await Until(() => !acts.IsBusy);
        var e = Assert.Single(events.Snapshot());
        Assert.Equal(EventKind.TaskFailed, e.Kind);
        Assert.Contains("cancelled: stop", e.Text);
        Assert.Contains("dug 3; planted 0", e.Text);
        Assert.Equal(EventPriority.Background, e.Priority);
    }

    [Fact]
    public async Task Cancelled_worker_emits_its_finished_outcome_with_action_counts()
    {
        var (rt, llm, _) = TestRuntime.Create();
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(ToolCreationOptions());
        rt.Tools.Register("field", "field", ToolSchema.Create().Build(), async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("never");
        }, longRunning: true);
        rt.ApplyBrainSplit();
        llm.Fallback = r => llm.Resp(r, StopReasons.ToolUse,
            new LlmToolUse("w1", "field", JsonSerializer.SerializeToElement(new { })));
        MineClaw.Agent.Worker.WorkerOutcome? finished = null;
        rt.Worker!.Finished += o => finished = o;
        var running = rt.Worker.RunAsync("prepare field", null, 5, cts.Token);
        await entered.Task;
        cts.Cancel();
        var outcome = await running;
        Assert.Same(outcome, finished);
        Assert.False(outcome.Ok);
        Assert.Equal("cancelled", outcome.StopReason);
        Assert.Contains("1 actions", outcome.Summary);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delegate_result_always_includes_observed_action_count(bool ok)
    {
        var (rt, llm, _) = TestRuntime.Create();
        rt.ApplyBrainSplit();
        llm.Fallback = r => llm.Resp(r, StopReasons.EndTurn,
            new LlmText(ok ? "ok: 0 changes: already clear" : "failed: 0 changes: no seeds"));
        var result = await rt.Tools.ExecuteAsync("delegate", JsonSerializer.SerializeToElement(new
        {
            task = "prepare field", done_when = "field planted",
        }), default);
        Assert.Equal(ok, result.Ok);
        Assert.Contains("0 actions", result.Text);
        Assert.Contains("0 changes", result.Text);
    }

    private static TaskCreationOptions ToolCreationOptions() => TaskCreationOptions.RunContinuationsAsynchronously;

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(10, timeout.Token);
    }
}
