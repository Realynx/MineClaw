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
        ["path-cave-1122"] = "C065B9183E679F0B9719276C94AC0325E966E0EEB0C7335C19FA04C6BD5F1724",
        ["path-cave-b181"] = "FE32E8C0E46A10C2A8E5B69CAB45824B6E8FD52D7F6958BB1A4CD71E542BCCA7",
        ["path-ditches-1122"] = "5348A74E47E06EBA0DC6F7CA266D27134BE825DFD035BE4FBA3BE82EA97EDEE9",
        ["path-ditches-b181"] = "C1C72598A0524A93DD819B818013F268A28A8E05AA0F0F2F98CB3928710201B1",
        ["path-doors-1122"] = "E5778137E45C75A2F8A9285F484C9DCE5E66734979064222B179DE5379645FB4",
        ["path-doors-b181"] = "EA40D393E4EBEB4D0DD9AF23F63E7836501ECCCE9804C890EF5131E039019B08",
        ["path-field-1122"] = "DA7CA7E2D19C20DCA2B0A0F16BB215B40E2D2C7D8C34D885E4828AD857EED782",
        ["path-field-b181"] = "FABD40B5D74ADAEFCBBB11A964C4D9B6312797340DCBDC4A2779CA4F878C02D3",
        ["path-hills-1122"] = "514BC23408E8086A066BA71981725DE73626A07FCA5B728C8B6207FAAFF393B4",
        ["path-hills-b181"] = "FCFC79D4D8519A1FEC43898818C458278E15593CADCB9541F15ECA9A5BAED392",
        ["path-islands-1122"] = "4C6CD275CF26DACCAFF42CB77E57FAE6FE6DCF02EB11DFE34BA955EE6AB6FD4E",
        ["path-islands-b181"] = "D9F3D8833D781E383759D2A73BCA6BAC16CD9CC3E8F4AD88647D3EB795ED0681",
        ["path-quarry-1122"] = "CEE943F61B27018C761C8FF44CC6616B093CFB285480E5E3E90F0703F245A758",
        ["path-quarry-b181"] = "E9573E1F46041EDCA6018EBD5D3EB4A8E229C1248FA4F87D7DE57F3A843934E0",
        ["path-water-1122"] = "CA5E94029628BCEE0BCF0613E3DE28D68B7C172F4037FA22A78269A20DB03FF2",
        ["path-water-b181"] = "03D39A0DCD6C259C15D754E00992E831645A5660FC6F44800F677DC0986C748C",
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
        var flood = Pathfinder.Explore(w, start0, PathOptions.Default with { Timeout = NoClock }, maxNodes: 600);
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
