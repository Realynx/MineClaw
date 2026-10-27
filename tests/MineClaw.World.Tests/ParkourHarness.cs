using MineClaw.World;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// Closed loop for gap jumps, exactly like the Client's goto: Pathfinder → PathFollower → EdgeGuard.Filter (with the
/// follower's parkour commitment) → PlayerPhysics, re-planning on failure with the follower's blocked jumps avoided, and
/// the Client's last-chance clutch (EdgeGuard.PlanClutch → place) when the guard reports Unrecoverable.
/// </summary>
internal static class ParkourHarness
{
    public sealed record Outcome(bool Reached, PhysicsState State, double MinY, bool EverOverVoid, bool Died, int Replans,
        int Ticks, GuardActions Seen, List<MoveKind> Kinds, List<ParkourPlan> Jumps, List<string> Failures, PathStatus FirstStatus,
        int Clutches, double MaxFall, bool NoClutchFace)
    {
        public string? LastFailure => Failures.Count > 0 ? Failures[^1] : null;
        public override string ToString() =>
            $"reached={Reached} at {State.Position} died={Died} replans={Replans} ticks={Ticks} kinds=[{string.Join(",", Kinds)}] " +
            $"jumps=[{string.Join("; ", Jumps.Select(j => j.Describe()))}] failures=[{string.Join(" | ", Failures)}] first={FirstStatus} clutch={Clutches}";
    }

    /// <summary><paramref name="onTick"/> runs before each tick's follower call (world edits, knockback: return a velocity
    /// to replace the motion, like 0x1C).</summary>
    public static Outcome Run(IBlockAccess w, Vec3d start, Goal goal, PathOptions? opts = null, int maxReplans = 4, int maxTicks = 3000,
        Func<int, PhysicsState, PathFollower, Vec3d?>? onTick = null, Func<bool>? canSprint = null, bool clutch = false,
        Action<BlockPos, int>? place = null, Action<string>? trace = null)
    {
        opts ??= PathOptions.Default with { AllowPartial = false };
        var guard = new EdgeGuard();
        var s = TestWorld.Standing(start.X, start.Y, start.Z);
        s.Velocity = new Vec3d(0, -0.0784, 0);
        double minY = s.Position.Y, peak = s.Position.Y, maxFall = 0;
        bool overVoid = false;
        var seen = GuardActions.None;
        var kinds = new List<MoveKind>();
        var jumps = new List<ParkourPlan>();
        var failures = new List<string>();
        PathStatus? first = null;
        int replans = 0, ticks = 0, clutches = 0;
        bool noFace = false;
        var blocked = new HashSet<(BlockPos, BlockPos)>();
        place ??= (p, id) => ((WorldState)w).SetBlock(p.X, p.Y, p.Z, id, 0);
        Outcome Out(bool reached) => new(reached, s, minY, overVoid, s.Position.Y < 20, replans, ticks, seen, kinds, jumps, failures,
            first ?? PathStatus.NoPath, clutches, maxFall, noFace);
        while (true)
        {
            var startCell = Pathfinder.StartCellFor(w, s.Position) ?? s.Position.ToBlockPos();
            if (s.OnGround && goal.IsReached(startCell)) return Out(true);
            var o2 = blocked.Count == 0 ? opts : opts with { AvoidJumps = blocked.ToHashSet() };
            var path = Pathfinder.FindPath(w, s.Position, goal, o2);
            first ??= path.Status;
            if (path.Status != PathStatus.Found || path.Steps.Count == 0)
            {
                failures.Add(path.Reason ?? path.Status.ToString());
                // Settle wherever we are (e.g. mid-air after an abort) before giving up.
                for (int i = 0; i < 40 && !s.OnGround; i++) Step(new MoveInput(Sneak: true, Yaw: s.Yaw), null, null);
                return Out(false);
            }
            kinds.AddRange(path.Steps.Select(p => p.Kind));
            var f = new PathFollower(path.Steps, new PathFollowerOptions { CanSprint = canSprint });
            GuardDecision last = default;
            FollowerOutput o;
            while (true)
            {
                if (++ticks > maxTicks) { failures.Add("tick budget (deadlock?)"); return Out(false); }
                if ((last.Actions & GuardActions.CancelledJump) != 0) f.NoteJumpCancelled(s);
                if ((last.Actions & GuardActions.ParkourRejected) != 0) f.NoteParkourRejected();
                if (onTick?.Invoke(ticks, s, f) is { } kb) s.Velocity = kb;
                o = f.Tick(w, s);
                if (o.Status is FollowerStatus.Finished or FollowerStatus.Failed) break;
                if (o.Status == FollowerStatus.NeedsAction && o.Action is { } a)
                {
                    if (a.Kind == FollowerActionKind.Place) place(a.Pos, Cobble);
                    else place(a.Pos, Air);
                }
                last = Step(o.Input, f.CurrentStep?.Kind == MoveKind.Drop, f.Commitment);
                if (s.Position.Y < 20) { failures.Add("fell into the void"); return Out(false); }
            }
            jumps.AddRange(f.CompletedJumps);
            if (o.Status == FollowerStatus.Failed)
            {
                failures.Add(o.Reason ?? "failed");
                if (f.BlockedJump is { } bj) blocked.Add(bj);
                // Like the Client: idle (sneak) a couple of ticks, guard still on, then re-plan.
                for (int i = 0; i < 2 || (!s.OnGround && i < 60); i++) Step(new MoveInput(Sneak: true, Yaw: s.Yaw), false, null);
                if (s.Position.Y < 20) { failures.Add("fell into the void"); return Out(false); }
                if (++replans > maxReplans) return Out(false);
            }
        }

        GuardDecision Step(MoveInput input, bool? drop, ParkourCommit? commit)
        {
            var d = guard.Filter(w, s, input, drop ?? false, commit);
            seen |= d.Actions;
            PlayerPhysics.Tick(w, s, d.Input);
            trace?.Invoke($"t{ticks} {s.Position} v{s.Velocity} g={s.OnGround} {d.Actions} in=f{d.Input.Forward} j{d.Input.Jump} sn{d.Input.Sneak} sp{d.Input.Sprint} y{d.Input.Yaw:0} commit={commit?.Tick}");
            if (d.Unrecoverable && clutch && clutches < 6)
            {
                if (guard.PlanClutch(w, s, Aiming.DefaultReach, Cobble) is { } plan)
                {
                    place(plan.Target, Cobble);
                    guard.NoteClutchPlaced(plan.Target);
                    clutches++;
                }
                else if (clutches == 0) noFace = true; // nothing solid within reach to place against
            }
            minY = Math.Min(minY, s.Position.Y);
            if (s.OnGround) { maxFall = Math.Max(maxFall, peak - s.Position.Y); peak = s.Position.Y; }
            else peak = Math.Max(peak, s.Position.Y);
            overVoid |= Safety.IsPlayerOverVoid(w, s.Position);
            return d;
        }
    }
}
