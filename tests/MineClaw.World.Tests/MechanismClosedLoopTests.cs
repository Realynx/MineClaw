using MineClaw.World;
using B = MineClaw.Data.Blocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Doors and mechanisms end to end (real b1.8.1 block table): Pathfinder → PathFollower → physics against
/// <see cref="FakeRedstone"/> (vanilla button/plate/lever timings), with the <see cref="MechanismLearner"/> watching.
/// </summary>
public class MechanismClosedLoopTests
{
    private const int Iron = B.IronDoor, Button = 77, Lever = 69, StonePlate = 70, Sticky = 29, Head = 34;

    /// <summary>Stone floor y=63 (x,z −8..8), a 3-high stone wall at x=0 with a 1-wide doorway at z=0.</summary>
    private static WorldState Room()
    {
        var w = MechanismLoop.NewWorld();
        w.Fill(-8, 63, -8, 8, 63, 8, B.Stone);
        w.Fill(0, 64, -8, 0, 66, 8, B.Stone);
        w.Fill(0, 64, 0, 0, 65, 0, 0);
        return w;
    }

    /// <summary>Room whose far side is a small closed pocket: x 1..3, z −1..1.</summary>
    private static WorldState Pocket()
    {
        var w = Room();
        w.Fill(1, 64, -2, 4, 66, -2, B.Stone);
        w.Fill(1, 64, 2, 4, 66, 2, B.Stone);
        w.Fill(4, 64, -2, 4, 66, 2, B.Stone);
        w.Fill(1, 67, -2, 4, 67, 2, B.Stone);
        return w;
    }

    private static void IronDoor(WorldState w, int meta = 0)
    {
        w.Set(0, 64, 0, Iron, meta);
        w.Set(0, 65, 0, Iron, meta | 8);
    }

    private static readonly Vec3d Outside = new(-3.5, 64, 0.5);
    private static readonly Goal Across = new GoalBlock(3, 64, 0);
    private static readonly Goal Inside = new GoalBlock(2, 64, 0);
    private static readonly BlockPos Door = new(0, 64, 0);

    // ------------------------------------------------------------------ basic setups

    [Fact]
    public void Wooden_door_opened_by_hand_and_closed_behind()
    {
        var w = Room();
        w.Set(0, 64, 0, B.WoodenDoor, 0);
        w.Set(0, 65, 0, B.WoodenDoor, 8);
        var loop = new MechanismLoop(w);
        var r = loop.Run(Outside, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.OpenDoor, r.Kinds);
        Assert.False(Openables.IsOpenAt(w, Door), "closed behind");
        Assert.Equal(0, w.Mechanisms.Count); // a hand-opened door is no mechanism
    }

    [Fact]
    public void Iron_door_with_a_button_on_both_sides_there_and_back_and_the_link_is_learned_on_the_way()
    {
        var w = Room();
        IronDoor(w);
        w.Set(-1, 65, 1, Button, 2);     // outside, on the wall's west face
        w.Set(1, 65, 1, Button, 1);      // inside, on its east face
        var loop = new MechanismLoop(w);
        loop.Server.Door(new BlockPos(-1, 65, 1), Door);
        loop.Server.Door(new BlockPos(1, 65, 1), Door);
        var r = loop.Run(Outside, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.OpenDoor, r.Kinds); // unknown yet: the near-side button heuristic
        loop.Idle(60);                                  // the button pops, the door shuts: pulse observed
        Assert.False(Openables.IsOpenAt(w, Door));
        var link = Assert.Single(w.Mechanisms.All());
        Assert.Equal(TriggerKind.Button, link.Kind);
        Assert.Equal(new BlockPos(-1, 65, 1), link.Trigger);
        Assert.Contains(link.Effects, e => e.Pos == Door && e.IsOpenable);
        Assert.InRange(link.PulseTicks!.Value, 19, 22);

        // Back: the far-side button. The way back plans with the learned graph where it applies.
        var back = loop.Run(r.State.Position, new GoalBlock(-3, 64, 0), place: false);
        Assert.True(back.Reached, back.LastFailure);
        Assert.Equal(2, w.Mechanisms.Count);
        loop.Idle(60);
        // Third trip: both links known → a Trigger step, timed within the 20-tick pulse.
        var again = loop.Run(back.State.Position, Across, place: false);
        Assert.True(again.Reached, again.LastFailure);
        Assert.Contains(MoveKind.Trigger, again.Kinds);
        Assert.Contains(again.Steps, s => s.Requires is { } rq && rq.Pos == new BlockPos(-1, 65, 1));
        Assert.False(loop.Server.Crushed);
    }

    [Fact]
    public void Iron_door_with_a_plate_on_one_side_lets_me_out_but_not_in()
    {
        var w = Pocket();
        IronDoor(w);
        w.Set(1, 64, 0, StonePlate, 0);   // inside, in front of the door
        var loop = new MechanismLoop(w);
        loop.Server.Door(new BlockPos(1, 64, 0), Door);
        // Out: walk onto the plate, the door opens, step through before the 20-tick check pops it.
        var r = loop.Run(new Vec3d(2.5, 64, 0.5), new GoalBlock(-3, 64, 0));
        Assert.True(r.Reached, r.LastFailure);
        loop.Idle(45);
        Assert.False(Openables.IsOpenAt(w, Door), "the plate popped on its next check and the door shut");
        // In: no trigger on the outside → no route (never walks into the closed door).
        var inPlan = loop.Plan(r.State.Position, Inside);
        Assert.False(inPlan.Success, inPlan.ToString());
    }

    [Fact]
    public void Lever_door_restores_the_lever_when_it_can_and_leaves_it_on_when_it_is_the_only_way_back()
    {
        // A 2-high wall with the lever on top, reachable and visible from both sides.
        var w = Room();
        w.Fill(0, 66, -8, 0, 66, 8, 0);
        w.Set(0, 66, 0, B.Stone);
        IronDoor(w);
        var lever = new BlockPos(0, 66, 1);
        w.Set(lever.X, lever.Y, lever.Z, Lever, 5);
        var loop = new MechanismLoop(w);
        loop.Server.Door(lever, Door);
        loop.Server.Click(lever);                       // someone shows it once (learned)…
        loop.Idle(20);
        loop.Server.Click(lever);                       // …and puts it back
        loop.Idle(20);
        var link = Assert.Single(w.Mechanisms.All());
        Assert.Equal(TriggerKind.Lever, link.Kind);
        Assert.Equal(2, link.Observations);
        var r = loop.Run(Outside, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(r.Steps, s => s.Kind == MoveKind.Trigger && s.Trigger is { Restore: true });
        Assert.False(Openables.IsActuatorOn(Lever, w.GetMeta(lever)), "lever flipped back once through");
        Assert.False(Openables.IsOpenAt(w, Door));

        // Lever only on the outside of a pocket: restoring it would lock me in → it stays on.
        var p = Pocket();
        IronDoor(p);
        var outer = new BlockPos(-1, 65, 1);
        p.Set(outer.X, outer.Y, outer.Z, Lever, 2);
        var l2 = new MechanismLoop(p);
        l2.Server.Door(outer, Door);
        l2.Server.Click(outer);
        l2.Idle(20);
        l2.Server.Click(outer);
        l2.Idle(20);
        var r2 = l2.Run(Outside, Inside);
        Assert.True(r2.Reached, r2.LastFailure);
        Assert.True(Openables.IsActuatorOn(Lever, p.GetMeta(outer)), "left on: the only way back out");
        Assert.True(Openables.IsOpenAt(p, Door));
    }

    [Fact]
    public void Trapdoor_hatch_over_a_ladder()
    {
        var w = MechanismLoop.NewWorld();
        w.Fill(-4, 63, -4, 4, 63, 4, B.Stone);
        w.Fill(-4, 66, -4, 4, 66, 4, B.Stone);
        w.Fill(0, 64, 1, 0, 67, 1, B.Stone);
        w.Fill(0, 64, 0, 0, 66, 0, B.Ladder, 2);
        w.Set(0, 67, -1, B.Stone);
        w.Set(0, 67, 0, B.Trapdoor, 1);
        var r = new MechanismLoop(w).Run(new Vec3d(0.5, 64, -1.5), new GoalBlock(-1, 67, 0));
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.ClimbUp, r.Kinds);
    }

    [Fact]
    public void Fence_gate()
    {
        var w = Room();
        w.Fill(0, 64, -8, 0, 66, 8, 0);
        w.Fill(0, 64, -8, 0, 64, 8, B.Fence);
        w.Set(0, 64, 0, B.FenceGate, 0);
        var r = new MechanismLoop(w).Run(Outside, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.OpenGate, r.Kinds);
        Assert.False(Openables.IsOpenAt(w, Door));
    }

    [Fact]
    public void Double_door_pair_b181_mirrored_half_is_stored_open_but_shut()
    {
        // ItemDoor b1.8.1: the second door of a pair gets facing ((f−1)&3)+4 — the open bit set, panel on the same plane.
        var w = Room();
        w.Fill(0, 64, 1, 0, 65, 1, 0);
        w.Set(0, 64, 0, B.WoodenDoor, 0);
        w.Set(0, 65, 0, B.WoodenDoor, 8);
        w.Set(0, 64, 1, B.WoodenDoor, 7);
        w.Set(0, 65, 1, B.WoodenDoor, 15);
        Assert.True(Openables.IsOpenAt(w, new BlockPos(0, 64, 1)), "raw open bit");
        Assert.False(Openables.LooksOpen(w, new BlockPos(0, 64, 1)), "but it blocks the doorway");
        Assert.False(Pathfinder.FindPath(w, Outside, Across, PathOptions.Default with { AllowPartial = false }).Success,
            "both halves of the pair are shut");
        var r = new MechanismLoop(w).Run(Outside, new GoalBlock(3, 64, 1));
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.OpenDoor, r.Kinds);
        Assert.False(Openables.LooksOpen(w, new BlockPos(0, 64, 0)));
        Assert.False(Openables.LooksOpen(w, new BlockPos(0, 64, 1)));
    }

    // ------------------------------------------------------------------ piston door, learned by observation

    /// <summary>
    /// A 2×2 sticky-piston door in the wall at z 0..1, y 64..65 (closed: pistons extended, blocks in the doorway), and a
    /// button hidden on the wall 5 blocks away. Wired with a pulse extender (<paramref name="extend"/> extra ticks).
    /// </summary>
    private static (WorldState W, MechanismLoop Loop, BlockPos Button) PistonDoor(int extend, int buttonZ = -5)
    {
        var w = Room();
        w.Set(0, 64, 0, B.Stone);
        w.Set(0, 65, 0, B.Stone);
        var loop = new MechanismLoop(w);
        var button = new BlockPos(-1, 65, buttonZ);
        w.Set(button.X, button.Y, button.Z, Button, 2);
        var wire = loop.Server.Add(button, lag: 1, extend: extend, piston: true);
        for (int y = 64; y <= 65; y++)
        {
            w.Set(0, y, -2, Sticky, 3 | 8);
            w.Set(0, y, -1, Head, 3 | 8);
            w.Set(0, y, 0, B.Stone);
            w.Set(0, y, 1, B.Stone);
            w.Set(0, y, 2, Head, 2 | 8);
            w.Set(0, y, 3, Sticky, 2 | 8);
            wire.Effects.Add((new BlockPos(0, y, -2), new BlockState(Sticky, 11), new BlockState(Sticky, 3)));
            wire.Effects.Add((new BlockPos(0, y, -1), new BlockState(Head, 11), new BlockState(B.Stone, 0)));
            wire.Effects.Add((new BlockPos(0, y, 0), new BlockState(B.Stone, 0), new BlockState(0, 0)));
            wire.Effects.Add((new BlockPos(0, y, 1), new BlockState(B.Stone, 0), new BlockState(0, 0)));
            wire.Effects.Add((new BlockPos(0, y, 2), new BlockState(Head, 10), new BlockState(B.Stone, 0)));
            wire.Effects.Add((new BlockPos(0, y, 3), new BlockState(Sticky, 10), new BlockState(Sticky, 2)));
        }
        return (w, loop, button);
    }

    /// <summary>Alice stands by the hidden button and presses it once; the bot watches.</summary>
    private static MechanismObservation Demonstrate(WorldState w, MechanismLoop loop, BlockPos button)
    {
        w.Entities.SpawnPlayer(100, "Alice", new Vec3d(-1.5, 64, button.Z + 1.5), 0, 0);
        MechanismObservation? seen = null;
        loop.Learner.Observed += o => seen ??= o;
        loop.Server.Click(button);
        loop.Idle(120);
        return seen ?? throw new Xunit.Sdk.XunitException("nothing learned");
    }

    [Fact]
    public void Piston_door_with_a_hidden_button_is_learned_from_one_player_activation_and_then_used_in_time()
    {
        var (w, loop, button) = PistonDoor(extend: 40);
        Assert.False(loop.Plan(Outside, Across).Success, "a wall until we know better");
        var seen = Demonstrate(w, loop, button);
        Assert.True(seen.IsNew);
        Assert.Equal("Alice", seen.By);
        var link = Assert.Single(w.Mechanisms.All());
        Assert.Equal(button, link.Trigger);
        Assert.Equal(TriggerKind.Button, link.Kind);
        Assert.Equal(0, link.Dimension);
        Assert.Equal(12, link.Effects.Count);
        Assert.True(link.HasSolidChanges && link.MovesPistons);
        Assert.Contains(link.Effects, e => e.Pos == new BlockPos(0, 64, 0) && e.Off.Id == B.Stone && e.On.Id == 0);
        Assert.InRange(link.PulseTicks!.Value, 60, 64); // 20 (button) + 40 (extender) + lag
        Assert.InRange(link.LagTicks, 1, 2);
        Assert.False(link.Dangerous);

        var plan = loop.Plan(Outside, Across);
        Assert.True(plan.Success, plan.ToString());
        var trig = Assert.Single(plan.Steps, s => s.Kind == MoveKind.Trigger);
        Assert.Equal(button, trig.Trigger!.Pos);
        Assert.Contains(plan.Steps, s => s.Requires?.Pos == button && s.X == 0);

        var r = loop.Run(Outside, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.False(loop.Server.Crushed, "out of the doorway before the pistons pushed back");
        loop.Idle(80);
        Assert.Equal(B.Stone, w.GetBlock(0, 64, 0)); // closed again
    }

    [Fact]
    public void Pulse_too_short_for_the_walk_is_refused_and_a_closer_button_makes_it()
    {
        // Plain button (20 ticks), five blocks from the doorway: can't get through in time.
        var (w, loop, button) = PistonDoor(extend: 0);
        Demonstrate(w, loop, button);
        var link = Assert.Single(w.Mechanisms.All());
        Assert.InRange(link.PulseTicks!.Value, 20, 25);
        var plan = loop.Plan(Outside, Across);
        Assert.False(plan.Success, plan.ToString());
        Assert.Contains("pulse", plan.Reason);

        // Same door, button right next to the doorway: 20 ticks is enough.
        var (w2, loop2, b2) = PistonDoor(extend: 0, buttonZ: -1);
        Demonstrate(w2, loop2, b2);
        var r = loop2.Run(Outside, Across);
        Assert.True(r.Reached, r.LastFailure);
        Assert.Contains(MoveKind.Trigger, r.Kinds);
        Assert.False(loop2.Server.Crushed);
    }

    [Fact]
    public void Never_stands_in_an_unknown_pistons_push_path()
    {
        var w = Room();
        w.Fill(0, 64, 0, 0, 65, 0, 0);
        w.Set(0, 64, -1, Sticky, 3); // retracted piston in the wall, facing the doorway
        var plan = Pathfinder.FindPath(w, Outside, Across, PathOptions.Default with { AllowPartial = false });
        Assert.False(plan.Success, plan.ToString());
        w.Set(0, 64, 3, 0);          // another gap, no piston
        w.Set(0, 65, 3, 0);
        plan = Pathfinder.FindPath(w, Outside, Across, PathOptions.Default with { AllowPartial = false });
        Assert.True(plan.Success, plan.ToString());
        Assert.DoesNotContain(plan.Steps, s => s.X == 0 && s.Z == 0);
    }

    // ------------------------------------------------------------------ return-trip safety

    [Fact]
    public void Never_enters_a_pocket_through_an_iron_door_without_a_way_back_out()
    {
        var w = Pocket();
        IronDoor(w);
        w.Set(-1, 65, 1, Button, 2);   // outside only
        var o = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false };
        var r = Pathfinder.FindPath(w, Outside, Inside, o);
        Assert.False(r.Success, r.ToString());
        Assert.Contains("way back out", r.Reason);
        Assert.True(Pathfinder.FindPath(w, Outside, Inside, o with { AllowOneWay = true }).Success, "explicitly asked");

        w.Set(1, 65, 1, Button, 1);    // a button inside too
        var loop = new MechanismLoop(w);
        loop.Server.Door(new BlockPos(-1, 65, 1), Door);
        loop.Server.Door(new BlockPos(1, 65, 1), Door);
        var run = loop.Run(Outside, Inside);
        Assert.True(run.Reached, run.LastFailure);
        loop.Idle(40);
        var back = loop.Run(run.State.Position, new GoalBlock(-3, 64, 0), place: false);
        Assert.True(back.Reached, back.LastFailure);
    }

    [Fact]
    public void Learned_trigger_into_a_pocket_with_no_trigger_inside_is_refused()
    {
        var (w, loop, button) = PistonDoor(extend: 40);
        // Make the far side a pocket.
        w.Fill(1, 64, -2, 4, 66, -2, B.Stone);
        w.Fill(1, 64, 3, 4, 66, 3, B.Stone);
        w.Fill(4, 64, -2, 4, 66, 3, B.Stone);
        w.Fill(1, 67, -2, 4, 67, 3, B.Stone);
        Demonstrate(w, loop, button);
        var plan = loop.Plan(Outside, Inside);
        Assert.False(plan.Success, plan.ToString());
        Assert.Contains("way back out", plan.Reason);
    }

    // ------------------------------------------------------------------ plates and explosives

    /// <summary>1-wide corridor along x at z=0 (walls z=±1), a stone plate at x=0 on the only way.</summary>
    private static WorldState Corridor()
    {
        var w = MechanismLoop.NewWorld();
        w.Fill(-6, 63, -6, 6, 63, 6, B.Stone);
        w.Fill(-3, 64, -1, 3, 65, -1, B.Stone);
        w.Fill(-3, 64, 1, 3, 65, 1, B.Stone);
        w.Fill(-3, 64, -6, -3, 65, -2, B.Stone);
        w.Fill(-3, 64, 2, -3, 65, 6, B.Stone);
        w.Set(0, 64, 0, StonePlate, 0);
        return w;
    }

    [Fact]
    public void Plate_linked_to_tnt_is_never_stepped_on()
    {
        var w = Corridor();
        w.Set(0, 50, 12, B.Tnt);                      // wired far away, under the island
        var loop = new MechanismLoop(w, new MechanismLearnerOptions { EffectRadius = 16 });
        var wire = loop.Server.Add(new BlockPos(0, 64, 0));
        wire.Effects.Add((new BlockPos(0, 50, 12), new BlockState(B.Tnt, 0), new BlockState(0, 0)));
        // A mob/player stepping on it once shows what it does: the TNT block vanishes (ignited).
        var pig = new AABB(0.3, 64, 0.3, 0.7, 64.9, 0.7);
        loop.Server.Bodies.Add(() => pig);
        loop.Idle(2);
        pig = new AABB(10, 64, 10, 10.4, 64.9, 10.4);
        loop.Idle(40);
        var link = Assert.Single(w.Mechanisms.All());
        Assert.True(link.Dangerous, link.Describe());
        w.Set(0, 50, 12, B.Tnt);                      // (re-armed)

        var start = new Vec3d(-2.5, 64, 0.5);
        var goal = new GoalBlock(2, 64, 0);
        var plan = loop.Plan(start, goal);
        Assert.False(plan.Success, "the only way is over the TNT plate: refused");
        Assert.True(Pathfinder.FindPath(w, start, goal, loop.Options() with { AllowDangerousTriggers = true }).Success, "explicitly asked");

        // Open the side: the route goes around the plate.
        w.Fill(-3, 64, 1, 3, 65, 1, 0);
        w.Fill(-3, 64, 2, 3, 65, 2, B.Stone);
        plan = loop.Plan(start, goal);
        Assert.True(plan.Success, plan.ToString());
        Assert.DoesNotContain(plan.Steps, s => s.X == 0 && s.Z == 0);
    }

    [Fact]
    public void Unknown_plate_with_explosives_near_is_avoided_and_unknown_triggers_there_are_never_used()
    {
        var w = Corridor();
        w.Set(2, 62, 3, B.Tnt);
        var start = new Vec3d(-2.5, 64, 0.5);
        var goal = new GoalBlock(2, 64, 0);
        Assert.False(Pathfinder.FindPath(w, start, goal, PathOptions.Default with { AllowPartial = false }).Success);
        w.Set(2, 62, 3, B.Stone);
        Assert.True(Pathfinder.FindPath(w, start, goal, PathOptions.Default with { AllowPartial = false }).Success);

        // Iron door + button with a dispenser next to it: the heuristic never presses it.
        var room = Room();
        IronDoor(room);
        room.Set(-1, 65, 1, Button, 2);
        var o = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false };
        Assert.True(Pathfinder.FindPath(room, Outside, Across, o).Success);
        room.Set(-2, 66, 2, B.Dispenser);
        Assert.False(Pathfinder.FindPath(room, Outside, Across, o).Success);
    }

    [Fact]
    public void Unknown_lever_in_a_players_build_is_not_flipped_unless_allowed()
    {
        var w = Room();
        IronDoor(w);
        w.Set(-1, 65, 1, Lever, 2);
        var o = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false, IsBuild = (_, _) => true };
        Assert.False(Pathfinder.FindPath(w, Outside, Across, o).Success);
        Assert.True(Pathfinder.FindPath(w, Outside, Across, o with { AllowUnknownLevers = true }).Success);
    }
}
