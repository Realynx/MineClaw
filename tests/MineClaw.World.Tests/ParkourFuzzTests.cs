using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;
using static MineClaw.World.Tests.TestBlocks;

namespace MineClaw.World.Tests;

/// <summary>
/// 5k randomized gap-jump scenarios on synthetic islands, closed loop (Pathfinder → PathFollower → EdgeGuard →
/// PlayerPhysics, with the clutch): random gap widths/angles/heights, slab/stair/full landings, void / shallow ground /
/// lava below, ceilings, food (sprint) and void-policy flags, knockback mid-flight and blocks vanishing mid run-up.
/// Invariants: never ends dead, over the void or in lava; void jumps only when the policy allows them; ground jumps
/// never fall further than the planned miss.
/// </summary>
public partial class ParkourFuzzTests(ITestOutputHelper output)
{
    internal sealed class SparseWorld : IBlockAccess
    {
        private readonly Dictionary<(int, int, int), (int Id, int Meta)> _b = new();
        public int GetBlock(int x, int y, int z) => _b.TryGetValue((x, y, z), out var v) ? v.Id : 0;
        public int GetMeta(int x, int y, int z) => _b.TryGetValue((x, y, z), out var v) ? v.Meta : 0;
        public bool IsLoaded(int x, int z) => x is >= -40 and <= 40 && z is >= -40 and <= 40;
        public BlockInfo Info(int id) => TestBlocks.Get(id);

        public void Set(int x, int y, int z, int id, int meta = 0)
        {
            if (id == 0) _b.Remove((x, y, z));
            else _b[(x, y, z)] = (id, meta);
        }

        public void Fill(int x0, int y0, int z0, int x1, int y1, int z1, int id, int meta = 0)
        {
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
                Set(x, y, z, id, meta);
        }
    }

    internal sealed record Case(int Seed, string Below, bool AllowVoid, bool Clutch, bool Sprint, string Event, ParkourHarness.Outcome R);

    [Fact]
    public void FiveThousandRandomJumps_NeverEndOverTheVoid()
    {
        const int N = 5000;
        var bad = new ConcurrentBag<string>();
        var cases = new ConcurrentBag<Case>();
        Parallel.For(0, N, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, seed =>
        {
            var c = RunOne(seed);
            cases.Add(c);
            var r = c.R;
            string tag = $"#{seed} below={c.Below} void={c.AllowVoid} clutch={c.Clutch} sprint={c.Sprint} event={c.Event}: {r}";
            // A knockback that leaves no block face within reach is fatal for any action: counted, not a parkour bug.
            if (r.Died && c.Event == "knockback" && r.NoClutchFace) { }
            else if (r.Died) bad.Add("died " + tag);
            else if (r.State.InLava) bad.Add("lava " + tag);
            else if (!r.State.OnGround && !r.State.InWater) bad.Add("airborne at the end " + tag);
            else if (r.Failures.Contains("ended over the void")) bad.Add("over the void at the end " + tag);
            if (r.Jumps.Any(j => j.OverVoid) && !(c.AllowVoid && c.Clutch)) bad.Add("void jump without the policy " + tag);
            if (r.EverOverVoid && !(c.AllowVoid && c.Clutch) && c.Event == "none") bad.Add("over the void without a void jump " + tag);
        });
        int reached = cases.Count(c => c.R.Reached);
        int jumped = cases.Count(c => c.R.Jumps.Count > 0);
        int voidJumps = cases.Sum(c => c.R.Jumps.Count(j => j.OverVoid));
        int clutches = cases.Sum(c => c.R.Clutches);
        int knock = cases.Count(c => c.Event == "knockback");
        int vanish = cases.Count(c => c.Event == "vanish");
        int aborted = cases.Count(c => c.R.Failures.Any(f => f.Contains("aborted")));
        int doomed = cases.Count(c => c.R.Died && c.Event == "knockback" && c.R.NoClutchFace);
        int knockSaved = cases.Count(c => c.Event == "knockback" && !c.R.Died);
        output.WriteLine($"{N} runs: reached {reached}, with a gap jump {jumped} ({voidJumps} over the void), knockbacks {knock}, " +
                         $"vanishing landings {vanish}, run-ups aborted {aborted}, clutch blocks {clutches}, failures {bad.Count}; " +
                         $"knockbacks survived {knockSaved}/{knock}, unsalvageable (no block face in reach) {doomed}");
        foreach (var b in bad.Take(10)) output.WriteLine(b);
        Assert.True(bad.IsEmpty, $"{bad.Count} bad runs, e.g. {bad.FirstOrDefault()}");
        Assert.True(doomed * 20 <= knock, $"{doomed} of {knock} knockbacks were unsalvageable");
        Assert.True(jumped > N / 8, $"only {jumped} runs jumped: the fuzz isn't exercising parkour");
        Assert.True(cases.Count(c => c.Event == "knockback") > 50 && cases.Count(c => c.Event == "vanish") > 30);
    }

    internal static (SparseWorld W, Vec3d Start, BlockPos Goal, string Below) World(int seed)
    {
        var rng = new Random(seed * 7919 + 13);
        var w = new SparseWorld();
        int sx = rng.Next(1, 5), sz = rng.Next(1, 5);
        w.Fill(-sx + 1, 62, -sz / 2, 0, 63, -sz / 2 + sz - 1, Dirt); // start island, top 64, east edge at x = 0
        int gap = rng.Next(1, 5);
        int lz = rng.Next(-3, 4);
        int dy = rng.Next(-3, 2);
        int tx = 1 + gap, lw = rng.Next(1, 5), ld = rng.Next(1, 4);
        int top = 63 + dy;
        w.Fill(tx, top - 2, lz, tx + lw - 1, top, lz + ld - 1, Stone);
        switch (rng.Next(4))
        {
            case 0: w.Fill(tx, top, lz, tx, top, lz + ld - 1, Slab); break;                       // slab eave (floor −0.5)
            case 1: w.Fill(tx, top + 1, lz, tx, top + 1, lz + ld - 1, BlockShapes.WoodStairs, 0); break; // stair step up
            case 2: if (lw > 1) w.Fill(tx + 1, top + 1, lz, tx + lw - 1, top + 1, lz + ld - 1, Planks); break; // roof ridge behind
        }
        string below = rng.Next(10) switch { < 4 => "void", < 6 => "gap-floor", < 9 => "yard", _ => "lava" };
        int pitY = Math.Min(61, 61 + Math.Min(0, dy));
        if (below == "gap-floor") w.Fill(1, pitY, -6, tx - 1, pitY, 6, Stone);   // floor only under the gap: void around
        if (below == "yard") w.Fill(-sx - 3, pitY, -8, tx + lw + 3, pitY, 8, Stone);
        if (below == "lava") w.Fill(1, pitY, -6, tx - 1, pitY, 6, StillLava);
        if (rng.Next(7) == 0) w.Fill(-sx, 66 + rng.Next(0, 3), -3, tx + lw, 66 + rng.Next(0, 3), 3, Stone); // ceiling
        var start = new Vec3d(-sx / 2 + 0.5, 64, -sz / 2 + rng.Next(sz) + 0.5);
        var goal = new BlockPos(tx + lw - 1, 64 + dy, lz + ld / 2);
        while (w.GetBlock(goal.X, goal.Y, goal.Z) != 0 || w.GetBlock(goal.X, goal.Y + 1, goal.Z) != 0) goal = goal.Up();
        return (w, start, goal, below);
    }

    internal static Case RunOne(int seed, Action<string>? trace = null)
    {
        var (w, start, goal, below) = World(seed);
        var rng = new Random(seed * 104729 + 7);
        bool allowVoid = rng.Next(2) == 0, clutch = rng.Next(3) != 0, sprint = rng.Next(5) != 0;
        string ev = rng.Next(10) switch { < 2 => "knockback", 2 => "vanish", _ => "none" };
        if (ev == "knockback" && !clutch) ev = "none"; // a knockback over the void with no block to place is fatal in any action
        var kb = new Vec3d((rng.NextDouble() * 2 - 1) * 0.4, 0.2 + rng.NextDouble() * 0.2, (rng.NextDouble() * 2 - 1) * 0.4);
        int evTick = rng.Next(0, 8);
        bool fired = false;
        Vec3d? Hook(int t, PhysicsState s, PathFollower f)
        {
            if (fired || ev == "none" || f.Commitment is not { } c) return null;
            if (ev == "knockback" && c.Tick == c.Plan.Schedule.JumpTick + 1 + evTick % 6)
            {
                fired = true;
                return kb;
            }
            if (ev == "vanish" && c.Tick == Math.Min(evTick, c.Plan.Schedule.JumpTick - 1))
            {
                fired = true;
                var to = c.Plan.To;
                w.Set(to.X, to.Y, to.Z, Air);
                w.Set(to.X, to.Y - 1, to.Z, Air); // the landing block is mined away during the run-up
            }
            return null;
        }
        var opts = PathOptions.Default with
        {
            AllowPartial = false, AllowVoidParkour = allowVoid, HasClutchBlock = clutch, CanSprint = sprint,
            MaxNodes = 4000, Timeout = TimeSpan.FromSeconds(5),
        };
        var r = ParkourHarness.Run(w, start, new GoalBlock(goal.X, goal.Y, goal.Z), opts, maxReplans: 3, maxTicks: 1500,
            onTick: Hook, canSprint: () => sprint, clutch: clutch, place: (p, id) => w.Set(p.X, p.Y, p.Z, id), trace: trace);
        if (!r.Died && Safety.IsPlayerOverVoid(w, r.State.Position) && r.State.OnGround)
            r = r with { Failures = [.. r.Failures, "ended over the void"] };
        return new Case(seed, below, allowVoid, clutch, sprint, fired ? ev : "none", r);
    }
}
