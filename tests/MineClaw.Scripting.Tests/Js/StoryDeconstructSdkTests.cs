using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests.Js;

public sealed class StoryDeconstructSdkTests
{
    [Fact]
    public async Task Only_explicit_chest_designation_uses_the_empty_check()
    {
        using var rig = Rig.Js(more: [new ScriptAction("deconstruct_chest", ["at"], Atomic: true)]);
        var r = await rig.Run("story.dig('1,65,1',{deconstruct:true}); story.dig('2,65,1'); return 'done';");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("deconstruct_chest {\"at\":\"1,65,1\"}", rig.Norm());
        Assert.Contains("dig {\"at\":\"2,65,1\"}", rig.Norm());
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("dig ") && c.Contains("1,65,1"));
    }

    [Fact]
    public async Task Missing_or_refusing_safe_tool_never_falls_back_to_plain_or_replacement_dig()
    {
        using var missing = Rig.Js();
        var r = await missing.Run("return JSON.stringify(story.dig('1,65,1',{deconstruct:true,replace:true}));");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("unavailable", r.Outcome);
        Assert.Empty(missing.Norm());
        using var blocked = Rig.Js(more: [new ScriptAction("deconstruct_chest", ["at"], Atomic: true)]);
        blocked.On["deconstruct_chest"] = (_, _) => Task.FromResult(ActResult.Fail("chest deconstruction waiting: evacuate contents"));
        r = await blocked.Run("return JSON.stringify(story.dig('1,65,1',{deconstruct:true}));");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("evacuate contents", r.Outcome);
        Assert.Single(blocked.Norm());
    }

    [Fact]
    public async Task Evacuation_stash_keeps_failed_delivery_carried_without_generic_chest_fallback()
    {
        using var rig = Rig.Js(more: [new ScriptAction("deposit_at", ["at", "items"], Atomic: true)]);
        rig.On["deposit_at"] = (_, _) => Task.FromResult(ActResult.Fail("chest at 3,65,1 is full; remaining items carried"));
        var r = await rig.Run("return JSON.stringify(story.stash({deconstruct_id:'d1',stash:[{to:'3,65,1',items:['dirt']},{to:'4,65,1',items:['stone']}]}));");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("remaining items carried", r.Outcome);
        Assert.Single(rig.Norm());
        Assert.StartsWith("deposit_at ", rig.Norm().Single());
        using var missing = Rig.Js();
        r = await missing.Run("return JSON.stringify(story.stash({deconstruct_id:'d1',stash:[{to:'3,65,1',items:['dirt']}]}));");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("unavailable", r.Outcome);
        Assert.Empty(missing.Norm());
    }
}
