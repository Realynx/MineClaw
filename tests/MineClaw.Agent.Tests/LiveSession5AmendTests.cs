using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Agent.Worker;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-27 21:26: Fox said "I need torches", then "just like 5" seconds later. The amendment reached the running worker only
/// through an Opus round (amend_task, 6 s later), after it had already crafted 24; it ended holding 32. A player's correction now
/// goes from chat straight into the worker's next round (zero tokens).
/// </summary>
public class LiveSession5AmendTests
{
    [Theory]
    [InlineData("just like 5", "count", 5)]
    [InlineData("only 3", "count", 3)]
    [InlineData("actually just 2 pls", "count", 2)]
    [InlineData("i only need 4", "count", 4)]
    [InlineData("stop at 10", "count", 10)]
    [InlineData("make it 6", "count", 6)]
    [InlineData("5 is enough", "count", 5)]
    [InlineData("never mind", "cancel", null)]
    [InlineData("nvm", "cancel", null)]
    [InlineData("oh wait, forget it", "cancel", null)]
    [InlineData("that's enough!", "cancel", null)]
    public void Amendments_are_recognised(string line, string kind, int? count)
    {
        var a = TaskAmendment.Detect(line);
        Assert.NotNull(a);
        Assert.Equal(kind, a!.Kind);
        Assert.Equal(count, a.Count);
    }

    [Theory]
    [InlineData("I need torches :3")]
    [InlineData("just like that, nice")]
    [InlineData("bring me 5 bread and then go mine 64 cobble at the gen please")]
    [InlineData("only the oak logs")]
    [InlineData("I got 5 diamonds today")]
    [InlineData("mind the void")]
    public void Ordinary_lines_are_not_amendments(string line) => Assert.Null(TaskAmendment.Detect(line));

    [Fact]
    public async Task Just_like_5_reaches_the_running_torch_worker_before_its_craft()
    {
        var (rt, llm, _) = TestRuntime.Create();
        using var _rt = rt;
        var worker = rt.Worker!;
        var withdrew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var crafted = new List<string>();
        rt.Tools.Register("withdraw", "Take.", ToolSchema.Empty, async (_, _) => { withdrew.TrySetResult(); await go.Task; return ToolResult.Success("took 43 charcoal"); },
            tiers: ToolTiers.Worker);
        rt.Tools.Register("craft", "Craft.", ToolSchema.Create().Str("item").Int("count").Build(), (i, _) =>
        {
            crafted.Add(i.GetRawText());
            return Task.FromResult(ToolResult.Success("crafted"));
        }, tiers: ToolTiers.Worker);
        // Round 0: take coal. Round 1 (after the amendment): the worker must already see it, then crafts what it was told.
        llm.ThenTool("withdraw", new { })
           .Then(r =>
           {
               var seen = string.Concat(r.Messages[^1].Content.OfType<LlmText>().Select(t => t.Text));
               int n = seen.Contains("the amount is now 5", StringComparison.Ordinal) ? 5 : 24;
               return new LlmResponse([new LlmToolUse("tu_2", "craft", System.Text.Json.JsonSerializer.SerializeToElement(new { item = "torch", count = n }))],
                   StopReasons.ToolUse, LlmUsage.Zero, "claude-sonnet-5-5");
           })
           .ThenText("ok: crafted torches");

        var task = worker.RunAsync("Make torches for PoofimaFox: from chest 14,64,-5 take the coal, craft a stack of torches", null, 1, default);
        await withdrew.Task;
        // Same order as live: the request, then the correction while the worker is mid-task.
        rt.Chat.OnServerChat("<PoofimaFox> I need torches :3");
        rt.Chat.OnServerChat("<PoofimaFox> just like 5");
        go.SetResult();
        var o = await task;

        Assert.True(o.Ok, o.Summary);
        Assert.Single(crafted);
        Assert.Contains("\"count\":5", crafted[0]);
        Assert.Contains("[player] PoofimaFox says \"just like 5\"", string.Concat(llm.Requests[1].Messages[^1].Content.OfType<LlmText>().Select(t => t.Text)));
        Assert.Equal("PoofimaFox: \"just like 5\"", worker.LastLiveAmendment ?? "(none: the line didn't count as addressed)");
    }

    [Fact]
    public void No_task_running_means_no_amendment()
    {
        var (rt, _, _) = TestRuntime.Create();
        using var _rt = rt;
        Assert.False(rt.Worker!.AmendFromChat("PoofimaFox", "just like 5"));
    }
}
