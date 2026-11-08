using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

public sealed class StorySharedCraftingTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Crafting_shares_tables_and_auto_tasks_but_keeps_manual_bills_local(bool automatic, bool useOtherTable)
    {
        var (c, h, w) = StoryRig.Make(3);
        c.Meta.Gear.AutoCraftTools = false;
        P3 first = new(3, 64, 0), second = new(8, 64, 0);
        foreach (var at in new[] { first, second })
        {
            w.Set(at.X, at.Y, at.Z, 58);
            Assert.Null(c.WorkbenchOp("designate", at).Error);
        }
        foreach (var p in c.Pawns.Values)
        {
            foreach (var type in WorkTypes.All) p.Priorities[type] = type == WorkTypes.Craft ? 1 : 0;
            h.Lives[p.Name] = FakeStoryHost.Bare(0, 64, 0, new Stack(5, 0, 16));
        }
        var owner = c.Benches.Single(b => b.Pos == first);
        var a = new BillState { Id = "test-a", Recipe = "craft:stick", Target = 4, Mode = "count" };
        var b = new BillState { Id = "test-b", Recipe = "craft:stick", Target = 4, Mode = "count" };
        if (automatic) { a.ForBuild = b.ForBuild = StoryColony.NeedTag; owner.Tasks.AddRange([a, b]); }
        else owner.Bills.AddRange([a, b]);
        if (useOtherTable) h.Lives[c.P(1)] = FakeStoryHost.Bare(8, 64, 0, new Stack(5, 0, 16));
        var j1 = c.Next(0);
        var j2 = c.Next(1);
        Assert.Equal("craft", j1["kind"]!.GetValue<string>());
        Assert.Equal("craft", j2["kind"]!.GetValue<string>());
        Assert.Equal(first.ToString(), j1["at"]!.GetValue<string>());
        var destination = automatic && useOtherTable ? second : first;
        Assert.Equal(destination.ToString(), j2["at"]!.GetValue<string>());
        Assert.NotEqual(c.CurrentOf(c.P(0))!.SourceId, c.CurrentOf(c.P(1))!.SourceId);
        if (automatic) Assert.Equal(destination.Json().ToJsonString(), c.State()["craft_tasks"]!.AsArray().Single(t => t!["id"]!.GetValue<string>() == b.Id)!["bench"]!.ToJsonString());
        Assert.NotEqual("craft", c.Next(2)["kind"]!.GetValue<string>());
        Assert.True(c.Report(c.P(1), j2["id"]!.GetValue<string>(), "done", null, null));
        Assert.Equal(4, b.Done);
        Assert.Equal(0, a.Done);
        Assert.NotEqual("craft", c.Next(1)["kind"]!.GetValue<string>());
        Assert.Empty(c.Benches.Single(x => x.Pos == second).Bills);
        Assert.Empty(c.Benches.Single(x => x.Pos == second).Tasks);
    }
}
