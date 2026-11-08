using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests.Js;

public sealed class StoryStorageSignSdkTests
{
    [Fact]
    public async Task Storage_sign_job_forwards_exact_text_and_attachment_to_its_narrow_action()
    {
        using var rig = Rig.Js(more: [new ScriptAction("remove_storage_sign", ["at", "chest", "lines"], Atomic: true)]);
        var r = await rig.Run("story.dig('1,65,1',{storage_sign:{chest:'1,65,2',lines:['wood','','','']}});return 'done';");
        Assert.True(r.Ok, r.Outcome);
        var call = Assert.Single(rig.Norm());
        Assert.Equal("remove_storage_sign {\"at\":\"1,65,1\",\"chest\":\"1,65,2\",\"lines\":[\"wood\",\"\",\"\",\"\"]}", call);
    }

    [Fact]
    public async Task Missing_or_stale_sign_action_never_falls_back_to_forced_dig()
    {
        const string script = "return JSON.stringify(story.dig('1,65,1',{replace:true,storage_sign:{chest:'1,65,2',lines:['wood','','','']}}));";
        using var missing = Rig.Js();
        var r = await missing.Run(script);
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("unavailable", r.Outcome);
        Assert.Empty(missing.Norm());
        using var stale = Rig.Js(more: [new ScriptAction("remove_storage_sign", ["at", "chest", "lines"], Atomic: true)]);
        stale.On["remove_storage_sign"] = (_, _) => Task.FromResult(ActResult.Fail("storage sign changed: text no longer matches"));
        r = await stale.Run(script);
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("text no longer matches", r.Outcome);
        Assert.Single(stale.Norm());
    }

    [Fact]
    public async Task Ordinary_story_deliveries_use_strict_destination_when_available()
    {
        using var rig = Rig.Js(more: [new ScriptAction("deposit_at", ["at", "items"], Atomic: true)]);
        rig.On["deposit_at"] = (_, _) => Task.FromResult(ActResult.Fail("full; remaining items carried"));
        var r = await rig.Run("return JSON.stringify(story.stash({kind:'haul',stash:[{to:'3,65,1',items:['dirt']}]}));");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("remaining items carried", r.Outcome);
        Assert.StartsWith("deposit_at ", Assert.Single(rig.Norm()));
    }
}
