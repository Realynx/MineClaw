using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Bills see the chests (docs/web/story-mode.md "Bills see the chests"): any planks match a "from any planks" bill, a chest nobody has
/// looked into gets a look before the bill waits, a body's look replaces the record, a deposit retries the waiting bill at once, and the
/// reason says what was looked at.
/// </summary>
public class StoryStockCheckTests
{
    private static readonly P3 Table = new(3, 64, 0), Chest = new(3, 64, 2);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (StoryColony C, FakeStoryHost H, FakeClock T) Rig(params Stack[] stock)
    {
        var clock = new FakeClock();
        var (c, h, w) = StoryRig.Make(pawns: 1, data: Release1122GameData.Instance, time: clock);
        h.KnowsChests = true;
        foreach (var t in WorkTypes.All) c.Pawns.Values.Single().Priorities[t] = t == WorkTypes.Craft ? 1 : 0;
        w.Set(Table.X, Table.Y, Table.Z, 58);
        c.StorageOp("set", [Chest], null, null, null);
        foreach (var s in stock) c.AddContents(c.StorageAt(Chest)!, s);
        Assert.Null(c.WorkbenchOp("designate", Table).Error);
        return (c, h, clock);
    }

    private static string Bill(StoryColony c, string recipe, int target = 4)
    {
        var r = c.BillsOp("add", new JsonObject { ["bench"] = Table.Json(), ["bill"] = new JsonObject { ["recipe"] = recipe, ["target"] = target } });
        Assert.Null(r.Error);
        return r.Result!["id"]!.GetValue<string>();
    }

    private static string Kind(JsonObject j) => j["kind"]!.GetValue<string>();

    [Fact]
    public void Spruce_planks_beside_the_bench_make_sticks()
    {
        var (c, h, t) = Rig(new Stack(5, 1, 6));
        h.Seen[Chest] = ([new Stack(5, 1, 6)], t.Now);
        Bill(c, "craft:stick");
        var j = c.Next();
        Assert.Equal("craft", Kind(j));
        Assert.Contains(j["fetch"]!.AsArray(), f => f!["n"]!.GetValue<int>() == 2);
    }

    [Fact]
    public void A_chest_nobody_has_looked_into_is_looked_into_before_the_bill_waits_and_the_look_fixes_the_record()
    {
        var (c, h, t) = Rig(); // the record: empty; nobody has looked inside
        var id = Bill(c, "craft:stick");
        var look = c.Next();
        Assert.Equal("look", Kind(look));
        Assert.Equal(Chest.ToString(), look["at"]!.GetValue<string>());
        Assert.Null(c.Benches.Single().Bills.Single().SuspendedReason); // not waiting: checking

        // The player had put planks in; the pawn's look shows them.
        t.Now += TimeSpan.FromSeconds(5);
        h.Seen[Chest] = ([new Stack(5, 0, 8)], t.Now);
        Assert.True(c.Report(c.P(), look["id"]!.GetValue<string>(), "done", "opened chest: oak wood planks x8", null));
        Assert.Equal(8, c.StorageAt(Chest)!.Contents.Single().Count);
        var j = c.Next();
        Assert.Equal("craft", Kind(j));
    }

    [Fact]
    public void A_bill_waits_saying_what_it_looked_at_and_a_deposit_retries_it_at_once()
    {
        var (c, h, t) = Rig();
        t.Now += TimeSpan.FromSeconds(1);
        h.Seen[Chest] = ([], t.Now); // looked into a moment ago: empty
        var id = Bill(c, "craft:stick");
        Assert.NotEqual("craft", Kind(c.Next()));
        var why = c.Benches.Single().Bills.Single().SuspendedReason!;
        Assert.StartsWith("no planks: checked 1 chest within 32 of the bench", why);

        // Planks stored (a pawn's deposit): the bill runs on the next pick, no timer.
        c.Report(c.P(), c.CurrentOf(c.P())?.Id ?? "", "done", null, null);
        c.AddContents(c.StorageAt(Chest)!, new Stack(5, 2, 4));
        Assert.Equal("craft", Kind(c.Next()));
        Assert.Null(c.Benches.Single().Bills.Single().SuspendedReason);
    }

    [Fact]
    public void A_newer_look_replaces_the_record_but_an_older_one_never_does()
    {
        var (c, h, t) = Rig(new Stack(4, 0, 10));
        var st = c.StorageAt(Chest)!;
        h.Seen[Chest] = ([new Stack(5, 0, 3)], t.Now - TimeSpan.FromMinutes(1)); // before the record's last change
        Assert.False(c.RefreshSeen([st], force: true));
        Assert.Equal(10, st.Contents.Single().Count);
        t.Now += TimeSpan.FromSeconds(3);
        h.Seen[Chest] = ([new Stack(5, 0, 3)], t.Now);
        Assert.True(c.RefreshSeen([st], force: true));
        Assert.Equal((5, 3), (st.Contents.Single().Id, st.Contents.Single().Count));
        Assert.Equal(t.Now, st.SeenAt);
    }
}
