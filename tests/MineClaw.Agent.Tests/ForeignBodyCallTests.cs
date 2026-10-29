using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Live 10-29: Fox "maid tell 420 to come here"; main called goto with body "420" (a DeepSeek hive, not a body main drives): the
/// call ran on main's own body ("Every goto I sent with its body name walked my own body instead"), and main told Fox "420 is right
/// next to u now". A body the brain doesn't drive is refused, nothing moves, and the result says how to reach it.
/// </summary>
public class ForeignBodyCallTests
{
    [Fact]
    public async Task A_tool_call_naming_a_body_this_brain_doesnt_drive_never_runs_on_its_own_body()
    {
        var (rt, llm, _) = TestRuntime.Create(o => o.Chat.Username = "maid_chan");
        int walks = 0;
        rt.Tools.Register("goto", "Walk.", ToolSchema.Create().Str("to").Build(), (_, _) => { walks++; return Task.FromResult(ToolResult.Success("arrived")); });
        llm.ThenTool("goto", new { to = "PoofimaFox", body = "420" }, id: "tu_a")
           .ThenTool("goto", new { to = "PoofimaFox", body = "maid_chan" }, id: "tu_b")
           .ThenText("ok");

        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("PoofimaFox", "maid tell 420 to come here")]), default);

        var first = llm.Requests[1].Messages[^1].ToolResults.Single();
        Assert.True(first.IsError);
        Assert.Contains("not done: 420 isn't a body I drive (nothing moved)", first.Content);
        Assert.Contains("hive op=handover name=420", first.Content);
        Assert.Equal(1, walks); // only the call naming itself walked
    }
}
