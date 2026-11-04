using System.Text;
using B = MineClaw.Data.Blocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Mechanism learner golden (complexity refactor WP04, checkpoint 3), taken before <see cref="MechanismLearner"/> was reshaped:
/// a button door shown by a player (new, seen again, demonstrated), its wire cut until the link is forgotten, a lever on and off,
/// a plate pressed by the body, a piston door with a pulse extender, noise the learner ignores (liquids, wiring, fire, my own
/// edits), a trigger removed, a dimension change; every observation and the graph after each phase.
/// </summary>
public class LearnerGoldenTests
{
    private const int Iron = B.IronDoor, Button = 77, Lever = 69, StonePlate = 70, Sticky = 29, Head = 34;

    [Fact]
    public void Learning_matches_the_golden() => Golden.Check("learner", "ECDBA50E0E6B1A83443639A556998A1C9232377AA2DBA2F8B9555D5CB314B2BC", sb =>
    {
        var w = MechanismLoop.NewWorld();
        w.Fill(-8, 63, -8, 8, 63, 8, B.Stone);
        w.Fill(0, 64, -8, 0, 66, 8, B.Stone);
        w.Set(0, 64, 0, Iron, 0);
        w.Set(0, 65, 0, Iron, 8);
        w.Set(-1, 65, 1, Button, 2);
        w.Set(0, 64, 3, Iron, 0);
        w.Set(0, 65, 3, Iron, 8);
        w.Set(-1, 65, 4, Lever, 2);
        w.Set(-2, 64, -3, StonePlate, 0);
        w.Set(0, 64, -3, Iron, 0);
        w.Set(0, 65, -3, Iron, 8);
        var loop = new MechanismLoop(w);
        loop.Learner.SelfName = "Bot";
        loop.Learner.Observed += o => sb.Append("  obs ").Append(o.Link.Describe()).Append(" by=").Append(o.By ?? "-").Append(" new=").Append(o.IsNew)
            .Append(" miss=").Append(o.Miss).Append(" demo=").Append(o.Demonstrated).Append(" conf=").Append(Golden.R(o.Link.Confidence)).Append('\n');
        var button = loop.Server.Door(new BlockPos(-1, 65, 1), new BlockPos(0, 64, 0));
        var leverWire = loop.Server.Door(new BlockPos(-1, 65, 4), new BlockPos(0, 64, 3));
        loop.Server.Door(new BlockPos(-2, 64, -3), new BlockPos(0, 64, -3), lag: 2);
        w.Entities.SpawnPlayer(100, "Alice", new Vec3d(-2.5, 64, 1.5), 0, 0);
        loop.Bot.Position = new Vec3d(-6.5, 64, -6.5);

        Phase(sb, loop, "button-first", () => loop.Server.Click(new BlockPos(-1, 65, 1)), 60);
        Phase(sb, loop, "button-again", () => loop.Server.Click(new BlockPos(-1, 65, 1)), 60);
        loop.Learner.NoteDemonstration("alice");
        Phase(sb, loop, "button-demo", () => loop.Server.Click(new BlockPos(-1, 65, 1)), 60);
        Phase(sb, loop, "lever-on", () => loop.Server.Click(new BlockPos(-1, 65, 4)), 30);
        Phase(sb, loop, "lever-off", () => loop.Server.Click(new BlockPos(-1, 65, 4)), 30);
        Phase(sb, loop, "plate", () => loop.Bot.Position = new Vec3d(-1.5, 64, -2.5), 10);
        Phase(sb, loop, "plate-off", () => loop.Bot.Position = new Vec3d(-6.5, 64, -6.5), 60);
        Phase(sb, loop, "noise", () =>
        {
            loop.Server.Click(new BlockPos(-1, 65, 4));
            w.Set(-3, 64, 2, 8, 0);
            w.Set(-3, 64, 3, 55, 0);
            w.Set(-4, 64, 3, 51, 0);
            w.Set(-4, 64, 3, 0, 0);
            w.SelfEdits.Note(new BlockPos(-5, 64, 3));
            w.Set(-5, 64, 3, B.Stone, 0);
        }, 30);
        button.Effects.Clear();
        for (int i = 0; i < 6; i++) Phase(sb, loop, $"cut-{i}", () => loop.Server.Click(new BlockPos(-1, 65, 1)), 40);
        leverWire.Effects.Clear();
        for (int i = 0; i < 6; i++) Phase(sb, loop, $"lever-cut-{i}", () => loop.Server.Click(new BlockPos(-1, 65, 4)), 30);
        Phase(sb, loop, "lever-removed", () => w.Set(-1, 65, 4, 0, 0), 5);
        Phase(sb, loop, "dimension", () =>
        {
            loop.Server.Click(new BlockPos(-2, 64, -3));
            w.Dimension = -1;
        }, 30);
        w.Dimension = 0;

        // A piston door with a pulse extender, shown by a player standing at the button.
        var p = MechanismLoop.NewWorld();
        p.Fill(-8, 63, -8, 8, 63, 8, B.Stone);
        p.Fill(0, 64, -8, 0, 66, 8, B.Stone);
        var pl = new MechanismLoop(p);
        pl.Learner.Observed += o => sb.Append("  pobs ").Append(o.Link.Describe()).Append(" by=").Append(o.By ?? "-").Append(" lag=").Append(o.Link.LagTicks)
            .Append(" pulse=").Append(o.Link.PulseTicks?.ToString() ?? "-").Append('\n');
        var pb = new BlockPos(-1, 65, -5);
        p.Set(pb.X, pb.Y, pb.Z, Button, 2);
        var wire = pl.Server.Add(pb, lag: 1, extend: 30, piston: true);
        for (int y = 64; y <= 65; y++)
        {
            p.Set(0, y, -2, Sticky, 3 | 8);
            p.Set(0, y, -1, Head, 3 | 8);
            p.Set(0, y, 0, B.Stone);
            p.Set(0, y, 1, B.Stone);
            wire.Effects.Add((new BlockPos(0, y, -2), new BlockState(Sticky, 11), new BlockState(Sticky, 3)));
            wire.Effects.Add((new BlockPos(0, y, -1), new BlockState(Head, 11), new BlockState(B.Stone, 0)));
            wire.Effects.Add((new BlockPos(0, y, 0), new BlockState(B.Stone, 0), new BlockState(0, 0)));
            wire.Effects.Add((new BlockPos(0, y, 1), new BlockState(B.Stone, 0), new BlockState(0, 0)));
        }
        p.Entities.SpawnPlayer(101, "Bob", new Vec3d(-1.5, 64, -3.5), 0, 0);
        pl.Bot.Position = new Vec3d(-6.5, 64, 6.5);
        Phase(sb, pl, "piston", () => pl.Server.Click(pb), 120);
        Phase(sb, pl, "piston-again", () => pl.Server.Click(pb), 120);
    });

    private static void Phase(StringBuilder sb, MechanismLoop loop, string label, Action act, int ticks)
    {
        sb.Append(label).Append('\n');
        act();
        for (int t = 0; t < ticks; t++)
        {
            loop.Idle(1);
            if (t % 10 == 9) sb.Append("  t").Append(t).Append(" pending=").Append(loop.Learner.Pending).Append('\n');
        }
        foreach (var l in loop.Learner.Graph.All().OrderBy(l => l.Trigger.X).ThenBy(l => l.Trigger.Y).ThenBy(l => l.Trigger.Z))
            sb.Append("  link ").Append(l.Describe()).Append(" pulse=").Append(l.PulseTicks?.ToString() ?? "-").Append(" lag=").Append(l.LagTicks)
                .Append(" conf=").Append(Golden.R(l.Confidence)).Append(" obs=").Append(l.Observations).Append(" miss=").Append(l.Misses)
                .Append(" by=").Append(l.By ?? "-").Append(" taught=").Append(l.Taught).Append(" dim=").Append(l.Dimension)
                .Append(" effects=").Append(string.Join(';', l.Effects.Select(e => $"{e.Pos}:{e.Off.Id}/{e.Off.Meta}>{e.On.Id}/{e.On.Meta}x{e.Seen}"))).Append('\n');
    }
}
