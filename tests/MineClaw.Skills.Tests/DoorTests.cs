using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class DoorTests
{
    /// <summary>Stone floor y=63, a 2-high stone wall at x=0 with a doorway at z=0; me at x=-2.</summary>
    internal static FakeGame Room(int doorId = Blocks.WoodenDoor, int lowerMeta = 0)
    {
        var g = new FakeGame();
        g.Fill(-6, 63, -6, 6, 63, 6, Blocks.Stone);
        g.Fill(0, 64, -6, 0, 66, 6, Blocks.Stone);
        if (doorId == Blocks.FenceGate)
        {
            g.Fill(0, 64, -6, 0, 66, 6, 0);
            g.Fill(0, 64, -6, 0, 64, 6, Blocks.Fence);
            g.Set(0, 64, 0, Blocks.FenceGate, lowerMeta);
        }
        else
        {
            g.Set(0, 64, 0, doorId, lowerMeta);
            g.Set(0, 65, 0, doorId, lowerMeta | 8);
        }
        g.Stand(-1.5, 64, 0.5);
        return g;
    }

    private static PathOptions Doors => PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false };
    private static readonly Goal Across = new GoalBlock(2, 64, 0);

    [Fact]
    public void Door_state_comes_from_metadata_both_halves()
    {
        var g = Room();
        Assert.False(Openables.IsOpenAt(g.World, new BlockPos(0, 65, 0)));
        Assert.Equal(new BlockPos(0, 64, 0), Openables.StateBlock(g.World, new BlockPos(0, 65, 0)));
        g.Set(0, 64, 0, Blocks.WoodenDoor, 4);
        Assert.True(Openables.IsOpenAt(g.World, new BlockPos(0, 65, 0)));
    }

    [Fact]
    public async Task Open_and_close_verify_by_block_change_and_are_idempotent()
    {
        var g = Room();
        var r = await g.Run("open_door", new { pos = "0,65,0" });
        Assert.True(r.Ok, r.Text);
        Assert.True(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)));
        Assert.True(Openables.IsOpen(g.World.GetMeta(0, 65, 0)), "upper half follows");
        Assert.Contains("already open", (await g.Run("open_door", new { pos = "0,64,0" })).Text);
        var c = await g.Run("close_door", new { kind = "door" });
        Assert.True(c.Ok, c.Text);
        Assert.False(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)));
    }

    [Fact]
    public async Task Locked_door_reports_no_block_change()
    {
        var g = Room();
        g.Locked.Add(new BlockPos(0, 64, 0));
        var r = await g.Run("open_door", new { pos = "0,64,0" });
        Assert.False(r.Ok);
        Assert.Contains("no block change", r.Text);
        Assert.False(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)));
    }

    [Fact]
    public void Closed_door_is_an_obstacle_unless_opening_is_allowed()
    {
        var g = Room();
        var none = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, PathOptions.Default with { AllowPartial = false });
        Assert.False(none.Success, "a closed door must never be walked into");
        var p = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors);
        Assert.True(p.Success, p.ToString());
        var step = Assert.Single(p.Steps, s => s.Opens.Count > 0);
        Assert.Equal(MoveKind.OpenDoor, step.Kind);
        Assert.Equal(new BlockPos(0, 64, 0), step.Opens[0].Pos);
        Assert.Null(step.Opens[0].Actuator);
    }

    [Fact]
    public void Open_door_is_passable_without_toggling()
    {
        var g = Room(lowerMeta: 4);
        var p = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, PathOptions.Default with { AllowPartial = false });
        Assert.True(p.Success, p.ToString());
        Assert.All(p.Steps, s => Assert.Empty(s.Opens));
    }

    [Fact]
    public void Fence_gate_plans_an_OpenGate_step()
    {
        var g = Room(Blocks.FenceGate);
        Assert.False(Pathfinder.FindPath(g.World, g.World.Player.Position, Across, PathOptions.Default with { AllowPartial = false }).Success);
        var p = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors);
        Assert.True(p.Success, p.ToString());
        Assert.Contains(p.Steps, s => s.Kind == MoveKind.OpenGate && s.Opens[0].Id == Blocks.FenceGate);
    }

    [Fact]
    public void Iron_door_needs_a_near_side_button()
    {
        var g = Room(Blocks.IronDoor);
        Assert.False(Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors).Success, "iron door without redstone = wall");
        g.Set(3, 65, 1, Blocks.StoneButton); // far side only
        Assert.False(Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors).Success);
        g.Set(-1, 65, 1, Blocks.StoneButton); // near side
        var p = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors);
        Assert.True(p.Success, p.ToString());
        var t = p.Steps.Single(s => s.Opens.Count > 0).Opens[0];
        Assert.Equal(new BlockPos(-1, 65, 1), t.Actuator);
    }

    [Fact]
    public async Task Iron_door_skill_presses_button_or_reports_obstacle()
    {
        var g = Room(Blocks.IronDoor);
        var r = await g.Run("open_door", new { pos = "0,64,0" });
        Assert.False(r.Ok);
        Assert.Contains("redstone", r.Text);
        g.Set(-1, 65, 1, Blocks.StoneButton);
        r = await g.Run("open_door", new { pos = "0,64,0" });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("button", r.Text);
        Assert.True(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)));
    }

    [Fact]
    public async Task Goto_via_doors_opens_passes_and_recloses()
    {
        var g = Room();
        var r = await g.Run("goto_via_doors", new { pos = "2,64,0" });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(new BlockPos(2, 64, 0), g.World.Player.Position.ToBlockPos());
        Assert.False(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)), "door restored to how it was found");
        Assert.Contains("re-closed", r.Text);
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Goto_via_doors_through_a_locked_door_fails_honestly()
    {
        var g = Room();
        g.Locked.Add(new BlockPos(0, 64, 0));
        var r = await g.Run("goto_via_doors", new { pos = "2,64,0" });
        Assert.False(r.Ok);
        Assert.Contains("stuck at door", r.Text);
    }

    [Fact]
    public void Trapdoor_over_the_void_is_never_a_floor()
    {
        var g = new FakeGame();
        g.Fill(-3, 63, -3, 3, 63, 3, Blocks.Stone);
        g.Set(4, 63, 0, Blocks.Trapdoor, 0); // closed hatch over nothing
        g.Stand(0.5, 64, 0.5);
        var p = Pathfinder.FindPath(g.World, g.World.Player.Position, new GoalBlock(4, 63, 0), Doors with { MaxNodes = 2000 });
        Assert.False(p.Success, "standing on a closed trapdoor over the void must be refused");
    }

    [Fact]
    public async Task Wont_open_the_trapdoor_I_stand_on()
    {
        var g = new FakeGame();
        g.Fill(-3, 60, -3, 3, 60, 3, Blocks.Stone);
        g.Set(0, 63, 0, Blocks.Trapdoor, 0);
        g.Stand(0.5, 63.1875, 0.5);
        var r = await g.Run("open_door", new { pos = "0,63,0" });
        Assert.False(r.Ok);
        Assert.Contains("standing on it", r.Text);
    }

    [Fact]
    public void Follower_requests_activate_waits_for_meta_then_restores_door()
    {
        var g = Room();
        var path = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors);
        Assert.True(path.Success);
        var follower = new PathFollower(path.Steps);
        var s = new PhysicsState();
        s.Teleport(g.World.Player.Position, 0, 0);
        s.OnGround = true;
        int activations = 0;
        bool sawOpen = false;
        for (int tick = 0; tick < 600 && !follower.IsFinished; tick++)
        {
            var o = follower.Tick(g.World, s);
            if (o.Status == FollowerStatus.Failed) Assert.Fail(o.Reason);
            if (o.Status == FollowerStatus.NeedsAction && o.Action is { Kind: FollowerActionKind.Activate } a)
            {
                activations++;
                g.ActivateAsync(a.Pos, CancellationToken.None).Wait(); // the "server" flips the door (0x35)
                sawOpen |= Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)) == true;
                continue;
            }
            PlayerPhysics.Tick(g.World, s, o.Input);
        }
        // Drain the restore request after the last step.
        for (int i = 0; i < 5; i++)
        {
            var o = follower.Tick(g.World, s);
            if (o.Action is { Kind: FollowerActionKind.Activate } a) { activations++; g.ActivateAsync(a.Pos, CancellationToken.None).Wait(); }
            else break;
        }
        Assert.True(sawOpen);
        Assert.Equal(2, activations); // open + close behind
        Assert.Equal(2, (int)Math.Floor(s.Position.X));
        Assert.False(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)));
    }

    [Fact]
    public void Follower_gives_up_on_a_locked_door()
    {
        var g = Room();
        g.Locked.Add(new BlockPos(0, 64, 0));
        var path = Pathfinder.FindPath(g.World, g.World.Player.Position, Across, Doors);
        var follower = new PathFollower(path.Steps);
        var s = new PhysicsState();
        s.Teleport(g.World.Player.Position, 0, 0);
        s.OnGround = true;
        string? reason = null;
        for (int tick = 0; tick < 600; tick++)
        {
            var o = follower.Tick(g.World, s);
            if (o.Status == FollowerStatus.Failed) { reason = o.Reason; break; }
            if (o.Action is { Kind: FollowerActionKind.Activate } a) { g.ActivateAsync(a.Pos, CancellationToken.None).Wait(); continue; }
            PlayerPhysics.Tick(g.World, s, o.Input);
        }
        Assert.NotNull(reason);
        Assert.Contains("locked", reason);
        Assert.True(s.Position.X < 0.2, "never walked into the closed door");
    }

    [Fact]
    public async Task Lever_and_button_skills_verify_state()
    {
        var g = Room(Blocks.IronDoor);
        g.Set(-1, 65, 1, Blocks.Lever, 0);
        var r = await g.Run("use_lever", new { pos = "-1,65,1", on = true });
        Assert.True(r.Ok, r.Text);
        Assert.True(Openables.IsOpenAt(g.World, new BlockPos(0, 64, 0)), "lever powers the iron door");
        Assert.Contains("already on", (await g.Run("use_lever", new { pos = "-1,65,1", on = true })).Text);
        Assert.False((await g.Run("press_button", new { pos = "-1,65,1" })).Ok, "not a button");
    }
}
