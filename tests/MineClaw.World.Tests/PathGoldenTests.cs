using System.Text;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// Pathing goldens (complexity refactor WP04), taken from the pathfinder and follower before they were reshaped: over the seeded
/// <see cref="GoldenWorlds"/> of both versions, every search's status, cost, explored-node count, scaffold count, reason and exact
/// step list (kind, cell, floor, cost, breaks, place, sneak, gentle, opens, parkour plan with its nominal flight), the search order
/// of a flood (<see cref="Pathfinder.Explore"/>: cells in the order the search closed them, with their costs), and closed loops
/// (follower → edge guard → physics, per-tick input, position, velocity and flags, bit for bit). The search budgets are node
/// counts, never the clock (the timeout is far out of reach), so the digests don't depend on the machine's load.
/// </summary>
public class PathGoldenTests
{
    private static readonly TimeSpan NoClock = TimeSpan.FromMinutes(10);

    public static IEnumerable<object[]> Cases()
    {
        foreach (var v in new[] { false, true })
        foreach (var k in GoldenWorlds.Kinds)
            yield return [k, v];
    }

    private static readonly Dictionary<string, string> Expected = new()
    {
        ["path-cave-1122"] = "95F2BB2E2AB31A22C12CFBB7EBDFA09CD096E8D33710CDEE154B0407FE0ECE89",
        ["path-cave-b181"] = "5ABEA2AFC544CB0EC373222AED0317353416E4AD209C3A7C1F79FBF91D57C227",
        ["path-ditches-1122"] = "15070F0DDFDA6C238DA8A7BFD6AF124AA1D11D7EC6982A1883DDFF7ACCC26A5E",
        ["path-ditches-b181"] = "FEEC7C08D576E5FAA6154617BBBE3F7B1EBE5AABE7006198BF38C66CBF451E5C",
        ["path-doors-1122"] = "2CDAE84EEFFEF105DE4514AF7CFA131035B7F2D7DE14FE9E0B78652538ED0E09",
        ["path-doors-b181"] = "67910EAC8A182000A0A148C8D90F36A0D1274EF56EBF88D0BAAEB99BD9F80860",
        ["path-field-1122"] = "97D07A39829FBC65C864AD029BFC9C4735250717EAE6301D5FB78E444DCAEB49",
        ["path-field-b181"] = "77C4EF068585A1FA9037ED3E6658CECBC6918B1071222C7BE8C40CC6F8258DBA",
        ["path-hills-1122"] = "ED05DF210E015017EA29564B217B482FA281C361C53ED3F62155AED3EC0A0AFF",
        ["path-hills-b181"] = "DA984254EE519BE032D2954255F70CE45B4FE6544B5A3E86BE52D0A24F49F8E7",
        ["path-islands-1122"] = "4C6CD275CF26DACCAFF42CB77E57FAE6FE6DCF02EB11DFE34BA955EE6AB6FD4E",
        ["path-islands-b181"] = "D9F3D8833D781E383759D2A73BCA6BAC16CD9CC3E8F4AD88647D3EB795ED0681",
        ["path-quarry-1122"] = "CC7BB8C4DC2EFCDCA3823E9FD7372BC549C2B9D5B06479C29B77C86F5C02DED3",
        ["path-quarry-b181"] = "389E414010E442C04B42DC8396E1F01700C5F8E795A384E7748D8D988342AA93",
        ["path-water-1122"] = "D8B87FEECB974695D1F6DE0054ACA2F907FDDC639B8BA0B230229572F7CF699A",
        ["path-water-b181"] = "86CEBB86CA3FD008D9FED3ADA751705FDF2E014AE3589F11318B433E091A06ED",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Paths_match_the_golden(string kind, bool release1122)
    {
        string name = $"path-{kind}-{(release1122 ? "1122" : "b181")}";
        Check(name, Expected.GetValueOrDefault(name, ""), sb =>
        {
            using var _ = GoldenWorlds.Data(release1122);
            for (int seed = 1; seed <= 2; seed++) DumpWorld(sb, kind, seed, release1122);
        });
    }

    private static void DumpWorld(StringBuilder sb, string kind, int seed, bool release1122)
    {
        var w = GoldenWorlds.Build(kind, seed, release1122);
        var cells = GoldenWorlds.FootCells(w);
        var rng = new Random(seed * 31 + 7);
        sb.Append("== ").Append(kind).Append(' ').Append(seed).Append(' ').Append(cells.Count).Append('\n');
        if (cells.Count == 0) return;

        // Flood: the search order.
        var start0 = cells[rng.Next(cells.Count)];
        var flood = Pathfinder.Explore(w, start0, PathOptions.Default with { Timeout = NoClock }, maxNodes: 1500);
        sb.Append("flood ").Append(start0).Append(' ').Append(flood.Count).Append('\n');
        foreach (var (c, g) in flood) sb.Append(c.X).Append(',').Append(c.Y).Append(',').Append(c.Z).Append('=').Append(R(g)).Append(' ');
        sb.Append('\n');

        int loops = seed == 1 ? 0 : 2; // closed loops (the costly part) on the first world of each kind
        for (int q = 0; q < 14; q++)
        {
            var start = cells[rng.Next(cells.Count)];
            var near = cells.FindAll(c => Math.Abs(c.X - start.X) + Math.Abs(c.Z - start.Z) <= 16 && c != start);
            var target = near.Count > 0 ? near[rng.Next(near.Count)] : cells[rng.Next(cells.Count)];
            var goal = GoalFor(q, w, start, target, rng);
            var opts = OptionsFor(q, w, rng);
            if (kind == "doors") opts = opts with { AllowOpenDoors = true };
            var r = Pathfinder.FindPath(w, start, goal, opts);
            sb.Append("q").Append(q).Append(' ').Append(start).Append(" -> ").Append(target).Append(' ').Append(goal.GetType().Name).Append('\n');
            DumpResult(sb, r);
            if (r.Status == PathStatus.Found && r.Steps.Count > 0 && loops < 2)
            {
                loops++;
                ClosedLoop(sb, r.Steps.Any(st => st.HasActions) ? Clone(kind, seed, release1122) : w, start, r, opts);
            }
        }
    }

    /// <summary>A fresh copy of the world for a closed loop (its actions change blocks).</summary>
    private static WorldState Clone(string kind, int seed, bool release1122) => GoldenWorlds.Build(kind, seed, release1122);

    private static Goal GoalFor(int q, WorldState w, BlockPos start, BlockPos target, Random rng) => (q % 8) switch
    {
        0 => new GoalBlock(target.X, target.Y, target.Z),
        1 => new GoalNear(target, 2),
        2 => new GoalWithinReach(new BlockPos(target.X, target.Y - 1, target.Z)),
        3 => new GoalUseBlock(w, new BlockPos(target.X, target.Y - 1, target.Z)),
        4 => new GoalYLevel(target.Y),
        5 => new GoalFiltered(new GoalNear(target, 3), (x, _, z) => ((x + z) & 1) == 0, "even"),
        6 => new GoalAny(new GoalBlock(target.X, target.Y, target.Z), new GoalNear(start.X + rng.Next(-8, 9), start.Y, start.Z + rng.Next(-8, 9), 1)),
        _ => new GoalNearPosition(new Vec3d(target.X + 0.3, target.Y, target.Z + 0.7), 1.5),
    };

    private static PathOptions OptionsFor(int q, WorldState w, Random rng)
    {
        var o = new PathOptions { Timeout = NoClock, MaxNodes = 5000 };
        return (q % 7) switch
        {
            0 => o,
            1 => o with { AllowBreak = true },
            2 => o with { AllowPlace = true, ScaffoldBudget = 6, AllowPartial = false },
            3 => o with
            {
                AllowBreak = true, AllowPlace = true, ScaffoldBudget = 3,
                GrowZone = (x, _, z) => x > -10 && x < 12 && z > -12 && z < 10,
                SkyLight = (x, y, z) => GoldenWorlds.Surface(w, x, z) is { } s && y >= s.Y ? 15 : 0,
                Covered = (x, y, z) => GoldenWorlds.Surface(w, x, z) is { } s && y < s.Y - 1,
            },
            4 => o with { CanSprint = false, AllowVoidParkour = true, HasClutchBlock = true, MaxDrop = 2 },
            5 => o with { AllowOpenDoors = true, AllowDiagonal = rng.Next(2) == 0, MaxNodes = 300 },
            _ => o with
            {
                Avoid = new HashSet<BlockPos> { new(0, 64, 0), new(1, 64, 0), new(2, 64, 1) },
                AvoidMoves = new HashSet<(BlockPos, BlockPos)> { (new(3, 64, 3), new(4, 64, 3)) },
                AllowParkour = rng.Next(2) == 0, AllowSwim = rng.Next(3) > 0,
            },
        };
    }

    internal static void DumpResult(StringBuilder sb, PathResult r)
    {
        sb.Append(r.Status).Append(" cost=").Append(R(r.Cost)).Append(" nodes=").Append(r.NodesExplored).Append(" scaffold=").Append(r.ScaffoldUsed)
            .Append(" reason=").Append(r.Reason ?? "-").Append('\n');
        foreach (var s in r.Steps)
        {
            sb.Append("  ").Append(s).Append(" floor=").Append(R(s.Floor)).Append(" cost=").Append(R(s.Cost));
            foreach (var o in s.Opens) sb.Append(" toggle=").Append(o.Pos).Append('/').Append(o.Id).Append('/').Append(o.FromMeta).Append('/').Append(o.Actuator?.ToString() ?? "-").Append('/').Append(o.Keep);
            if (s.Jump is { } j)
            {
                sb.Append(" jump ").Append(j.From).Append("->").Append(j.To).Append(" ff=").Append(R(j.FromFloor)).Append(" tf=").Append(R(j.ToFloor))
                    .Append(" sched=").Append(R(j.Schedule.Yaw)).Append('/').Append(j.Schedule.Sprint).Append('/').Append(j.Schedule.JumpTick)
                    .Append('/').Append(R(j.Schedule.TargetX)).Append('/').Append(R(j.Schedule.TargetZ))
                    .Append(" land=").Append(j.LandTick).Append(" dist=").Append(R(j.Distance)).Append(" margin=").Append(R(j.Margin))
                    .Append(" void=").Append(j.OverVoid).Append(" maxfall=").Append(R(j.MaxFall)).Append(" nominal=");
                foreach (var p in j.Nominal) sb.Append(R(p)).Append(';');
            }
            sb.Append('\n');
        }
    }

    internal static void ClosedLoop(StringBuilder sb, WorldState w, BlockPos start, PathResult path, PathOptions opts, int maxTicks = 300)
    {
        var floor = path.Steps.Count > 0 ? start.Y : start.Y;
        var s = TestWorld.Standing(start.X + 0.5, Pathfinder.StartCellFor(w, new Vec3d(start.X + 0.5, start.Y, start.Z + 0.5)) is { } c ? FloorOf(w, c) : floor, start.Z + 0.5);
        s.Velocity = new Vec3d(0, -0.0784, 0);
        var guard = new EdgeGuard();
        var f = new PathFollower(path.Steps, new PathFollowerOptions { AllowSprint = true });
        GuardDecision last = default;
        sb.Append(" loop\n");
        for (int t = 0; t < maxTicks; t++)
        {
            if ((last.Actions & GuardActions.CancelledJump) != 0) f.NoteJumpCancelled(s);
            if ((last.Actions & GuardActions.ParkourRejected) != 0) f.NoteParkourRejected();
            var o = f.Tick(w, s);
            sb.Append("  t").Append(t).Append(' ').Append(o.Status).Append(" i=").Append(R(o.Input.Forward)).Append('/').Append(R(o.Input.Strafe))
                .Append('/').Append(o.Input.Jump ? 'J' : '-').Append(o.Input.Sneak ? 'S' : '-').Append(o.Input.Sprint ? 'R' : '-')
                .Append('/').Append(R(o.Input.Yaw)).Append('/').Append(R(o.Input.Pitch)).Append(" step=").Append(f.StepIndex);
            if (o.Action is { } a) sb.Append(" act=").Append(a.Kind).Append(a.Pos);
            if (o.Reason is not null) sb.Append(" why=").Append(o.Reason);
            if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) { sb.Append('\n'); break; }
            if (o.Status == FollowerStatus.NeedsAction && o.Action is { } act) Apply(w, act);
            last = guard.Filter(w, s, o.Input, f.CurrentStep?.Kind == MoveKind.Drop, f.Commitment);
            PlayerPhysics.Tick(w, s, last.Input);
            sb.Append(" g=").Append(last.Actions).Append(" p=").Append(R(s.Position)).Append(" v=").Append(R(s.Velocity))
                .Append(s.OnGround ? " G" : " A").Append(s.InWater ? "W" : "").Append(s.OnLadder ? "L" : "").Append(s.Sprinting ? "R" : "")
                .Append(s.Sneaking ? "S" : "").Append(" fd=").Append(R(s.FallDistance)).Append('\n');
            if (s.Position.Y < 0) break;
        }
        sb.Append(" jumps=").Append(f.CompletedJumps.Count).Append(" blocked=").Append(f.BlockedJump?.ToString() ?? "-").Append('\n');
    }

    private static double FloorOf(WorldState w, BlockPos c)
    {
        double top = BlockShapes.CollisionTop(w, c.X, c.Y - 1, c.Z);
        double here = BlockShapes.CollisionTop(w, c.X, c.Y, c.Z);
        return here > 0 ? c.Y + here : top > 0 ? c.Y - 1 + top : c.Y;
    }

    private static void Apply(WorldState w, FollowerAction a)
    {
        switch (a.Kind)
        {
            case FollowerActionKind.Break: w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, 0, 0); break;
            case FollowerActionKind.Place: w.SetBlock(a.Pos.X, a.Pos.Y, a.Pos.Z, GoldenWorlds.Cobble, 0); break;
            default:
            {
                var p = Openables.StateBlock(w, a.Pos);
                int id = w.GetBlock(p.X, p.Y, p.Z);
                if (!Openables.IsOpenable(id)) break; // a button or lever: nothing modelled
                w.SetBlock(p.X, p.Y, p.Z, id, Openables.Toggled(w.GetMeta(p.X, p.Y, p.Z)));
                break;
            }
        }
    }
}
