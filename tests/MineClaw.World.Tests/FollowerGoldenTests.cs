using System.Text;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// Boosted follower golden (complexity refactor WP04, checkpoint 3), taken before the follower's boost room, step checks and
/// restore requests were reshaped: closed loops over the seeded <see cref="GoldenWorlds"/> of both versions the way the client
/// drives Story Mode's Fast / Faster (follower tick → <see cref="PathFollower.BoostRoom"/> → edge guard → physics →
/// <see cref="SpeedBoost.Plan"/> / <see cref="SpeedBoost.Apply"/> with that room), with doors restored behind, breaks, places and
/// opens applied; every tick's input, room, plan and state bit for bit.
/// </summary>
public class FollowerGoldenTests
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
        ["follower-cave-1122"] = "1E223668624E83E1B27FD04C1F824D0C5238FA6A7A0AFE251502DDF98D5E5346",
        ["follower-cave-b181"] = "72011C1A97F5CC9DA8FEE41E6F3E90B62F0BAF61A411FA6E8A0E47CB06D8A909",
        ["follower-ditches-1122"] = "69A2AF25C6E4F4605924FD54D3952770693255BA5A351BA112F78890CCCD0A8C",
        ["follower-ditches-b181"] = "050AB02267C9068C4C7851EC7F9AF97B3C9056AE83150262E084C3B09EBFE860",
        ["follower-doors-1122"] = "301C02F89FA9C57DB8BB574E02A6852AAEE14995E08CDC7F0B9066D027FD7358",
        ["follower-doors-b181"] = "EDC8AE3F28831BFDE09FC6FEBB5B810CF9B92CB54C86B7A698DF70B0EA969623",
        ["follower-field-1122"] = "3740364CE59C66B1763002616B05AECF8BC9C8EB4D31ED0253D0A66C827F7520",
        ["follower-field-b181"] = "21D1988BAFE4B1DFFF25E1482E87BB80D585595E1D6EC65E0DE6E7F837DE2CF2",
        ["follower-hills-1122"] = "7F3837872AFBC504597C91D6151382C883E068B24F0272A0F34676F65B6BA221",
        ["follower-hills-b181"] = "64E149C449B02D1DE2602CD16E3A4CC1530671904EE9B0BB5F220C1782EBEA23",
        ["follower-islands-1122"] = "9DC24FCD9440E30508CCE225C8A98A6538197C6D2DC325D7ECABB32CA46AD359",
        ["follower-islands-b181"] = "4420FC5ADCA8E83990A970C35961DC1C9A95C0FB094A3161ED6791EC81B71F4E",
        ["follower-quarry-1122"] = "2DC0847E26D5DA64A1E27AB27B2D78118B0D38E05B3DD6F4D7D828C85239CF09",
        ["follower-quarry-b181"] = "3B9E1FD0D28CEC9CB91A4EEA11BCFEBA48571D013B7362F0D6B96F2BBF268F3C",
        ["follower-water-1122"] = "19E7040F422368835F584FAD7DF023B1E6C464E2D66A5B8C5D3945BC6C3091FA",
        ["follower-water-b181"] = "7A932361F8C299971A61E5B9D522E40813D14EFC7B03B96337B11D2CD35B89E4",
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Boosted_loops_match_the_golden(string kind, bool release1122)
    {
        string name = $"follower-{kind}-{(release1122 ? "1122" : "b181")}";
        Check(name, Expected.GetValueOrDefault(name, ""), sb =>
        {
            using var _ = GoldenWorlds.Data(release1122);
            var probe = GoldenWorlds.Build(kind, 1, release1122);
            var cells = GoldenWorlds.FootCells(probe);
            var rng = new Random(kind.Length * 53 + (release1122 ? 11 : 0));
            if (cells.Count == 0) return;
            for (int q = 0; q < 8; q++)
            {
                var w = GoldenWorlds.Build(kind, 1, release1122);
                var start = cells[rng.Next(cells.Count)];
                var near = cells.FindAll(c => Math.Abs(c.X - start.X) + Math.Abs(c.Z - start.Z) is > 3 and <= 14);
                if (near.Count == 0) continue;
                var target = near[rng.Next(near.Count)];
                var opts = new PathOptions
                {
                    Timeout = NoClock, MaxNodes = 4000, AllowOpenDoors = true, AllowBreak = q % 3 == 1, AllowPlace = q % 4 == 2,
                    ScaffoldBudget = 4, AllowDiagonal = q % 2 == 0,
                };
                Goal goal = q % 2 == 0 ? new GoalBlock(target.X, target.Y, target.Z) : new GoalNear(target, 1);
                var r = Pathfinder.FindPath(w, start, goal, opts);
                sb.Append("q").Append(q).Append(' ').Append(start).Append(" -> ").Append(target).Append(' ').Append(r.Status).Append(' ').Append(r.Steps.Count).Append('\n');
                if (r.Status != PathStatus.Found || r.Steps.Count == 0) continue;
                Loop(sb, w, start, r, q % 2 == 0 ? 3.0 : 2.0, restoreDoors: q % 3 != 2);
            }
        });
    }

    private static void Loop(StringBuilder sb, WorldState w, BlockPos start, PathResult path, double factor, bool restoreDoors)
    {
        var s = TestWorld.Standing(start.X + 0.5, start.Y, start.Z + 0.5);
        s.Velocity = new Vec3d(0, -0.0784, 0);
        var guard = new EdgeGuard();
        var f = new PathFollower(path.Steps, new PathFollowerOptions { AllowSprint = true, RestoreDoors = restoreDoors });
        GuardDecision last = default;
        for (int t = 0; t < 260; t++)
        {
            if ((last.Actions & GuardActions.CancelledJump) != 0) f.NoteJumpCancelled(s);
            if ((last.Actions & GuardActions.ParkourRejected) != 0) f.NoteParkourRejected();
            var o = f.Tick(w, s);
            bool drop = f.CurrentStep?.Kind == MoveKind.Drop;
            double room = o.Status == FollowerStatus.Moving && !drop ? f.BoostRoom(s) : 0;
            sb.Append(" t").Append(t).Append(' ').Append(o.Status).Append(" i=").Append(R(o.Input.Forward)).Append('/').Append(R(o.Input.Strafe))
                .Append('/').Append(o.Input.Jump ? 'J' : '-').Append(o.Input.Sneak ? 'S' : '-').Append(o.Input.Sprint ? 'R' : '-')
                .Append('/').Append(R(o.Input.Yaw)).Append(" step=").Append(f.StepIndex).Append(" room=").Append(R(room));
            if (o.Action is { } a) sb.Append(" act=").Append(a.Kind).Append(a.Pos);
            if (o.Reason is not null) sb.Append(" why=").Append(o.Reason);
            if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) { sb.Append('\n'); break; }
            if (o.Status == FollowerStatus.NeedsAction && o.Action is { } act) Apply(w, act);
            last = guard.Filter(w, s, o.Input, drop, f.Commitment);
            var before = s.Position;
            bool ground = s.OnGround;
            PlayerPhysics.Tick(w, s, last.Input);
            if (room > 0 && SpeedBoost.Plan(w, s, before, ground, factor, room) is { } plan)
            {
                SpeedBoost.Apply(w, s, plan);
                sb.Append(" boost=").Append(R(plan.Ex)).Append('/').Append(R(plan.Ez)).Append('/').Append(plan.Mode);
            }
            sb.Append(" p=").Append(R(s.Position)).Append(" v=").Append(R(s.Velocity)).Append(s.OnGround ? " G" : " A").Append('\n');
            if (s.Position.Y < 0) break;
        }
        sb.Append(" failed-trigger=").Append(f.FailedTrigger?.ToString() ?? "-").Append(" missed=").Append(f.PulseMissed?.Pos.ToString() ?? "-").Append('\n');
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
                if (!Openables.IsOpenable(id)) break;
                w.SetBlock(p.X, p.Y, p.Z, id, Openables.Toggled(w.GetMeta(p.X, p.Y, p.Z)));
                break;
            }
        }
    }
}
