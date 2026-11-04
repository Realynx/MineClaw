using System.Text;
using B = MineClaw.Data.Blocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Mechanism golden (complexity refactor WP04, checkpoint 3), taken before the mechanism steps, the trigger candidates, the
/// return-trip check and the learner were reshaped: the scenes of <see cref="MechanismClosedLoopTests"/> (hand-opened doors,
/// gates, trapdoors, a double door, iron doors with buttons, plates and levers, piston doors with pulses long and short, pockets,
/// explosives), every plan in full (<see cref="PathGoldenTests.DumpResult"/>), every closed loop's end state and steps, and every
/// link the learner recorded on the way.
/// </summary>
public class MechanismGoldenTests
{
    private const int Iron = B.IronDoor, Button = 77, Lever = 69, StonePlate = 70, Sticky = 29, Head = 34;
    private static readonly Vec3d Outside = new(-3.5, 64, 0.5);
    private static readonly Goal Across = new GoalBlock(3, 64, 0);
    private static readonly Goal Inside = new GoalBlock(2, 64, 0);
    private static readonly BlockPos Door = new(0, 64, 0);

    private static WorldState Room()
    {
        var w = MechanismLoop.NewWorld();
        w.Fill(-8, 63, -8, 8, 63, 8, B.Stone);
        w.Fill(0, 64, -8, 0, 66, 8, B.Stone);
        w.Fill(0, 64, 0, 0, 65, 0, 0);
        return w;
    }

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

    [Fact]
    public void Mechanisms_match_the_golden() => Golden.Check("mechanisms", "871C00EE7C625F100BA74AD9630FFC0F0155920C9022FBA93022545E53D74FFD", sb =>
    {
        HandOpened(sb);
        Buttons(sb);
        Plates(sb);
        Levers(sb);
        Pistons(sb, 40, -5);
        Pistons(sb, 0, -5);
        Pistons(sb, 0, -1);
        Pockets(sb);
        Explosives(sb);
        Taught(sb);
    });

    private static void Plan(StringBuilder sb, string label, PathResult r)
    {
        sb.Append("plan ").Append(label).Append('\n');
        PathGoldenTests.DumpResult(sb, r);
    }

    private static void Run(StringBuilder sb, string label, MechanismLoop loop, MechanismLoop.Outcome o)
    {
        sb.Append("run ").Append(label).Append(' ').Append(o.Reached).Append(" at=").Append(Golden.R(o.State.Position)).Append(" replans=").Append(o.Replans)
            .Append(" ticks=").Append(loop.Ticks).Append(" fail=").Append(o.LastFailure ?? "-").Append(" crushed=").Append(loop.Server.Crushed)
            .Append(" clicks=").Append(loop.Server.Clicks).Append('\n');
        foreach (var s in o.Steps)
        {
            sb.Append("  ").Append(s).Append(" trig=").Append(s.Trigger?.Pos.ToString() ?? "-").Append('/').Append(s.Trigger?.TurnOn.ToString() ?? "-")
                .Append('/').Append(s.Trigger?.Restore.ToString() ?? "-").Append(" req=").Append(s.Requires?.Pos.ToString() ?? "-");
            foreach (var t in s.Opens) sb.Append(" open=").Append(t.Pos).Append('/').Append(t.Actuator?.ToString() ?? "-").Append('/').Append(t.Keep);
            sb.Append('\n');
        }
        Links(sb, loop.World);
    }

    private static void Links(StringBuilder sb, WorldState w)
    {
        foreach (var l in w.Mechanisms.All().OrderBy(l => l.Trigger.X).ThenBy(l => l.Trigger.Y).ThenBy(l => l.Trigger.Z))
            sb.Append("  link ").Append(l.Describe()).Append(" pulse=").Append(l.PulseTicks?.ToString() ?? "-").Append(" lag=").Append(l.LagTicks)
                .Append(" conf=").Append(Golden.R(l.Confidence)).Append(" obs=").Append(l.Observations).Append(" miss=").Append(l.Misses)
                .Append(" effects=").Append(string.Join(';', l.Effects.Select(e => $"{e.Pos}:{e.Off.Id}/{e.Off.Meta}>{e.On.Id}/{e.On.Meta}"))).Append('\n');
    }

    private static void HandOpened(StringBuilder sb)
    {
        var w = Room();
        w.Set(0, 64, 0, B.WoodenDoor, 0);
        w.Set(0, 65, 0, B.WoodenDoor, 8);
        var loop = new MechanismLoop(w);
        Run(sb, "wooden", loop, loop.Run(Outside, Across));
        Run(sb, "wooden-back", loop, loop.Run(loop.Bot.Position, new GoalBlock(-3, 64, 2), place: false));

        var t = MechanismLoop.NewWorld();
        t.Fill(-4, 63, -4, 4, 63, 4, B.Stone);
        t.Fill(-4, 66, -4, 4, 66, 4, B.Stone);
        t.Fill(0, 64, 1, 0, 67, 1, B.Stone);
        t.Fill(0, 64, 0, 0, 66, 0, B.Ladder, 2);
        t.Set(0, 67, -1, B.Stone);
        t.Set(0, 67, 0, B.Trapdoor, 1);
        var tl = new MechanismLoop(t);
        Run(sb, "trapdoor", tl, tl.Run(new Vec3d(0.5, 64, -1.5), new GoalBlock(-1, 67, 0)));

        var g = Room();
        g.Fill(0, 64, -8, 0, 66, 8, 0);
        g.Fill(0, 64, -8, 0, 64, 8, B.Fence);
        g.Set(0, 64, 0, B.FenceGate, 0);
        var gl = new MechanismLoop(g);
        Run(sb, "gate", gl, gl.Run(Outside, Across));
        Plan(sb, "gate-back", gl.Plan(gl.Bot.Position, new GoalBlock(-2, 64, -3)));

        var d = Room();
        d.Fill(0, 64, 1, 0, 65, 1, 0);
        d.Set(0, 64, 0, B.WoodenDoor, 0);
        d.Set(0, 65, 0, B.WoodenDoor, 8);
        d.Set(0, 64, 1, B.WoodenDoor, 7);
        d.Set(0, 65, 1, B.WoodenDoor, 15);
        Plan(sb, "double-closed", Pathfinder.FindPath(d, Outside, Across, PathOptions.Default with { AllowPartial = false }));
        var dl = new MechanismLoop(d);
        Run(sb, "double", dl, dl.Run(Outside, new GoalBlock(3, 64, 1)));
        Plan(sb, "double-diag", dl.Plan(dl.Bot.Position, new GoalBlock(-4, 64, -2)));
    }

    private static void Buttons(StringBuilder sb)
    {
        var w = Room();
        IronDoor(w);
        w.Set(-1, 65, 1, Button, 2);
        w.Set(1, 65, 1, Button, 1);
        var loop = new MechanismLoop(w);
        loop.Server.Door(new BlockPos(-1, 65, 1), Door);
        loop.Server.Door(new BlockPos(1, 65, 1), Door);
        Plan(sb, "buttons-unknown", loop.Plan(Outside, Across));
        var r = loop.Run(Outside, Across);
        Run(sb, "buttons-1", loop, r);
        loop.Idle(60);
        var back = loop.Run(r.State.Position, new GoalBlock(-3, 64, 0), place: false);
        Run(sb, "buttons-2", loop, back);
        loop.Idle(60);
        Plan(sb, "buttons-known", loop.Plan(back.State.Position, Across));
        Plan(sb, "buttons-known-diag", loop.Plan(back.State.Position, new GoalNear(new BlockPos(4, 64, 3), 1)));
        var again = loop.Run(back.State.Position, Across, place: false);
        Run(sb, "buttons-3", loop, again);
        loop.Tune = o => o with { PulseMargin = 6, PulseMarginFraction = 0.2 };
        Plan(sb, "buttons-margin", loop.Plan(again.State.Position, new GoalBlock(-4, 64, 1)));
        loop.Tune = o => o with { MinMechanismConfidence = 0.99 };
        Plan(sb, "buttons-unsure", loop.Plan(again.State.Position, new GoalBlock(-4, 64, 1)));
    }

    private static void Plates(StringBuilder sb)
    {
        var w = Pocket();
        IronDoor(w);
        w.Set(1, 64, 0, StonePlate, 0);
        var loop = new MechanismLoop(w);
        loop.Server.Door(new BlockPos(1, 64, 0), Door);
        Plan(sb, "plate-presumed", loop.Plan(new Vec3d(2.5, 64, 0.5), new GoalBlock(-3, 64, 0)));
        var r = loop.Run(new Vec3d(2.5, 64, 0.5), new GoalBlock(-3, 64, 0));
        Run(sb, "plate-out", loop, r);
        loop.Idle(45);
        Plan(sb, "plate-in", loop.Plan(r.State.Position, Inside));
        Plan(sb, "plate-in-oneway", Pathfinder.FindPath(w, r.State.Position, Inside, loop.Options() with { AllowOneWay = true }));

        // Plates on both sides of an iron door (the classic setup), unknown: presumed links.
        var b = Room();
        IronDoor(b);
        b.Set(-1, 64, 0, StonePlate, 0);
        b.Set(1, 64, 0, StonePlate, 0);
        b.Set(-2, 64, 3, StonePlate, 0);
        var bl = new MechanismLoop(b);
        bl.Server.Door(new BlockPos(-1, 64, 0), Door);
        bl.Server.Door(new BlockPos(1, 64, 0), Door);
        Plan(sb, "plates-both", bl.Plan(Outside, Across));
        Plan(sb, "plates-wander", bl.Plan(new Vec3d(-4.5, 64, 4.5), new GoalBlock(-1, 64, 5)));
        Run(sb, "plates-both", bl, bl.Run(Outside, Across));
    }

    private static void Levers(StringBuilder sb)
    {
        var w = Room();
        w.Fill(0, 66, -8, 0, 66, 8, 0);
        w.Set(0, 66, 0, B.Stone);
        IronDoor(w);
        var lever = new BlockPos(0, 66, 1);
        w.Set(lever.X, lever.Y, lever.Z, Lever, 5);
        var loop = new MechanismLoop(w);
        loop.Server.Door(lever, Door);
        loop.Server.Click(lever);
        loop.Idle(20);
        loop.Server.Click(lever);
        loop.Idle(20);
        Links(sb, w);
        Plan(sb, "lever", loop.Plan(Outside, Across));
        Run(sb, "lever", loop, loop.Run(Outside, Across));
        loop.Tune = o => o with { RestoreLevers = false };
        Plan(sb, "lever-norestore", loop.Plan(loop.Bot.Position, new GoalBlock(-3, 64, -1)));

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
        Plan(sb, "lever-pocket", l2.Plan(Outside, Inside));
        Run(sb, "lever-pocket", l2, l2.Run(Outside, Inside));
        Plan(sb, "lever-pocket-out", l2.Plan(l2.Bot.Position, new GoalBlock(-3, 64, 0)));
    }

    private static void Pistons(StringBuilder sb, int extend, int buttonZ)
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
        string label = $"piston-{extend}-{buttonZ}";
        Plan(sb, label + "-unknown", loop.Plan(Outside, Across));
        w.Entities.SpawnPlayer(100, "Alice", new Vec3d(-1.5, 64, button.Z + 1.5), 0, 0);
        loop.Server.Click(button);
        loop.Idle(120);
        Links(sb, w);
        Plan(sb, label, loop.Plan(Outside, Across));
        Plan(sb, label + "-near", loop.Plan(new Vec3d(-1.5, 64, 1.5), new GoalBlock(2, 64, 1)));
        Run(sb, label, loop, loop.Run(Outside, Across));
        loop.Idle(80);
        Plan(sb, label + "-back", loop.Plan(loop.Bot.Position, new GoalBlock(-3, 64, 0)));

        var open = Room();
        open.Fill(0, 64, 0, 0, 65, 0, 0);
        open.Set(0, 64, -1, Sticky, 3);
        Plan(sb, "piston-unknown-path", Pathfinder.FindPath(open, Outside, Across, PathOptions.Default with { AllowPartial = false }));
    }

    private static void Pockets(StringBuilder sb)
    {
        var w = Pocket();
        IronDoor(w);
        w.Set(-1, 65, 1, Button, 2);
        var o = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false };
        Plan(sb, "pocket", Pathfinder.FindPath(w, Outside, Inside, o));
        Plan(sb, "pocket-oneway", Pathfinder.FindPath(w, Outside, Inside, o with { AllowOneWay = true }));

        var p = Pocket();
        IronDoor(p);
        var outer = new BlockPos(-1, 65, 1);
        p.Set(outer.X, outer.Y, outer.Z, Button, 2);
        p.Mechanisms.Teach(p, outer, [Door], 0, "Fox", DateTimeOffset.UnixEpoch);
        var po = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false, Mechanisms = p.Mechanisms.View(0) };
        Plan(sb, "pocket-taught", Pathfinder.FindPath(p, Outside, Inside, po));
        p.Set(1, 65, -1, Button, 1);
        p.Mechanisms.Teach(p, new BlockPos(1, 65, -1), [Door], 0, "Fox", DateTimeOffset.UnixEpoch);
        Plan(sb, "pocket-taught-both", Pathfinder.FindPath(p, Outside, Inside, po with { Mechanisms = p.Mechanisms.View(0) }));
    }

    private static void Explosives(StringBuilder sb)
    {
        var w = Room();
        w.Fill(0, 64, 0, 0, 65, 0, 0);
        w.Set(-2, 64, 0, StonePlate, 0);
        w.Set(-2, 62, 0, 46);
        var loop = new MechanismLoop(w);
        Plan(sb, "tnt-plate", loop.Plan(Outside, Across));
        loop.Tune = o => o with { AllowDangerousTriggers = true };
        Plan(sb, "tnt-plate-allowed", loop.Plan(Outside, Across));

        var l = Room();
        IronDoor(l);
        var lever = new BlockPos(-1, 65, 1);
        l.Set(lever.X, lever.Y, lever.Z, Lever, 2);
        l.Set(-4, 62, 4, 46);
        var ll = new MechanismLoop(l);
        Plan(sb, "unknown-lever", ll.Plan(Outside, Across));
        ll.Vetoed.Add(lever);
        Plan(sb, "unknown-lever-vetoed", ll.Plan(Outside, Across));
    }

    private static void Taught(StringBuilder sb)
    {
        // Taught links: a lever already on, a button with a short observed pulse and lag, a hidden wall (non-openable effects).
        var w = Room();
        IronDoor(w);
        w.Set(-1, 65, 1, Lever, 2 | 8);
        var link = w.Mechanisms.Teach(w, new BlockPos(-1, 65, 1), [Door], 0, "Fox", DateTimeOffset.UnixEpoch);
        sb.Append("taught ").Append(link.Describe()).Append('\n');
        var o = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false, Mechanisms = w.Mechanisms.View(0) };
        Plan(sb, "taught-lever-on", Pathfinder.FindPath(w, Outside, Across, o));

        var h = Room();
        h.Set(0, 64, 0, B.Stone);
        h.Set(0, 65, 0, B.Stone);
        h.Set(-1, 65, -2, Button, 2);
        var hl = h.Mechanisms.Teach(h, new BlockPos(-1, 65, -2), [new BlockPos(0, 64, 0), new BlockPos(0, 65, 0)], 0, "Fox", DateTimeOffset.UnixEpoch);
        foreach (var (pulse, lag) in new[] { (30, 0), (12, 2), (6, 0) })
        {
            h.Mechanisms.Put(hl with { PulseTicks = pulse, LagTicks = lag });
            var ho = PathOptions.Default with { AllowOpenDoors = true, AllowPartial = false, Mechanisms = h.Mechanisms.View(0) };
            Plan(sb, $"wall-{pulse}-{lag}", Pathfinder.FindPath(h, Outside, Across, ho));
        }
    }
}
