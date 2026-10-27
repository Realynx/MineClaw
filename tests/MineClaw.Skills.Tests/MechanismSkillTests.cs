using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class MechanismSkillTests
{
    [Fact]
    public async Task Unknown_button_next_to_tnt_is_never_pressed()
    {
        var g = DoorTests.Room(Blocks.IronDoor);
        g.Set(-1, 65, 1, Blocks.StoneButton, 2);
        g.Set(-2, 62, 2, Blocks.Tnt);
        var r = await g.Run("press_button", new { pos = "-1,65,1" });
        Assert.False(r.Ok);
        Assert.Contains("TNT", r.Text);
        Assert.Equal(0, g.World.GetMeta(-1, 65, 1) & 8);
        var o = await g.Run("open_door", new { pos = "0,64,0" });
        Assert.False(o.Ok, o.Text);
    }

    [Fact]
    public async Task How_to_open_asks_with_go_around_default_then_a_taught_link_answers_and_opens()
    {
        var g = DoorTests.Room(Blocks.IronDoor);
        g.Set(-3, 64, 3, Blocks.Lever, 5);          // far from the door: no heuristic match
        var q = await g.Run("how_to_open", new { pos = "0,64,0" });
        Assert.False(q.Ok);
        Assert.StartsWith("needs_input:", q.Text);
        Assert.Contains("go around", q.Text);
        Assert.Equal(new BlockPos(0, 64, 0), g.World.Mechanisms.Question!.Value.Subject);

        var t = await g.Run("teach_mechanism", new { trigger = "-3,64,3", target = "0,65,0" });
        Assert.True(t.Ok, t.Text);
        var link = Assert.Single(g.World.Mechanisms.All());
        Assert.Equal(TriggerKind.Lever, link.Kind);
        Assert.True(link.Taught);
        Assert.Contains(link.Effects, e => e.Pos == new BlockPos(0, 64, 0));
        var a = await g.Run("how_to_open", new { pos = "0,64,0" });
        Assert.True(a.Ok, a.Text);
        Assert.Contains("lever@-3,64,3", a.Text);
        var list = await g.Run("mechanisms");
        Assert.Contains("lever@-3,64,3", list.Text);
    }

    [Fact]
    public async Task Open_door_reads_the_doorway_not_the_raw_bit_for_b181_door_pairs()
    {
        var g = DoorTests.Room();
        g.Fill(0, 64, 1, 0, 65, 1, 0);
        g.Set(0, 64, 1, Blocks.WoodenDoor, 7);       // mirrored half of a pair: open bit set, but shut
        g.Set(0, 65, 1, Blocks.WoodenDoor, 15);
        var r = await g.Run("open_door", new { pos = "0,64,1" });
        Assert.True(r.Ok, r.Text);
        Assert.DoesNotContain("already", r.Text);
        Assert.True(Openables.LooksOpen(g.World, new BlockPos(0, 64, 1)));
        Assert.Contains("already open", (await g.Run("open_door", new { pos = "0,64,1" })).Text);
    }
}
