using System.Text;
using MineClaw.Client;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Pins <see cref="DepositProcedure.RunAsync"/> (WP07): the exact sequence of port, claim and hand-off calls and the report over
/// scripted scenarios (chests with room, full, refused leases, unreachable chests and step-outs, hand-off, a new labelled chest,
/// unsafe moments, settle failures, a stale empty snapshot, the deadline). Time is a fake clock that ticks one second per read, so
/// the number of rounds before the deadline is deterministic.
/// </summary>
public class DepositTraceGoldenTests
{
    private sealed class TickClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { var n = _now; _now = _now.AddSeconds(1); return n; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }
        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>A scripted bot: every call is traced; the scenario sets what each answers.</summary>
    private sealed class Port(List<string> trace) : IInventoryPort
    {
        public List<ItemMsg> Held = [];
        public List<ItemMsg> Hidden = []; // appear on the next settle (a stale local copy)
        public List<StorageChest> Known = [];
        public Dictionary<BlockPos, Func<ActionResult>> OnDeposit = [];
        public Queue<bool> Safe = new();
        public Queue<bool> SettleOk = new();
        public bool SettleThrows;
        public Queue<bool> StepOut = new();
        public (bool Ok, BlockPos? Pos) Place = (false, null);
        public int Dim;

        public IReadOnlyList<ItemMsg> Items() => Held.ToList();
        public bool CanActSafely { get { var s = Safe.Count > 0 ? Safe.Dequeue() : true; trace.Add($"safe? {s}"); return s; } }
        public int Dimension => Dim;
        public IReadOnlyList<StorageChest> Chests() { trace.Add("chests"); return Known; }

        public Task<ActionResult> DepositToAsync(BlockPos chest, CancellationToken ct)
        {
            var r = OnDeposit.TryGetValue(chest, out var f) ? f() : ActionResult.Success("stored all", Held.Sum(i => i.Count));
            if (r.Ok && r.Count > 0) Take(r.Count);
            trace.Add($"deposit {chest} -> {r}");
            return Task.FromResult(r);
        }

        public Task<ActionResult> TossAllAtAsync(Vec3d at, CancellationToken ct)
        {
            trace.Add($"toss at {at.X},{at.Y},{at.Z}");
            Held.Clear();
            return Task.FromResult(ActionResult.Success("tossed"));
        }

        public Task<(ActionResult Result, BlockPos? Chest)> PlaceLabelledChestAsync(string label, CancellationToken ct)
        {
            trace.Add($"place chest '{label}' -> {Place.Ok}");
            return Task.FromResult((Place.Ok ? ActionResult.Success("placed") : ActionResult.Fail("no chest item"), Place.Pos));
        }

        public Task<bool> SettleAsync(CancellationToken ct)
        {
            Held.AddRange(Hidden);
            Hidden.Clear();
            if (SettleThrows) { trace.Add("settle throws"); throw new InvalidOperationException("boom"); }
            var ok = SettleOk.Count > 0 ? SettleOk.Dequeue() : true;
            trace.Add($"settle {ok}");
            return Task.FromResult(ok);
        }

        public Task<bool> StepOutAsync(CancellationToken ct)
        {
            var s = StepOut.Count > 0 && StepOut.Dequeue();
            trace.Add($"step out -> {s}");
            return Task.FromResult(s);
        }

        private void Take(int n)
        {
            while (n > 0 && Held.Count > 0)
            {
                var first = Held[0];
                int take = Math.Min(n, first.Count);
                n -= take;
                if (take == first.Count) Held.RemoveAt(0);
                else Held[0] = first with { Count = first.Count - take };
            }
        }
    }

    private sealed class Claims(List<string> trace) : IClaimBoard
    {
        public HashSet<BlockPos> Busy = [];
        public HashSet<BlockPos> Refuse = [];
        public string Owner => "maid_chan_67";
        public bool IsFree(ClaimKey key) => !Busy.Contains(key.Min);
        public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default)
        {
            bool ok = !Refuse.Contains(key.Min);
            trace.Add($"claim {key.Id} ttl={ttl?.TotalSeconds} note={note} -> {ok}");
            return Task.FromResult(ok ? ClaimResult.Granted(Owner, DateTimeOffset.MaxValue) : ClaimResult.Refused("x", DateTimeOffset.MaxValue));
        }
        public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) { trace.Add($"release {key.Id}"); return Task.CompletedTask; }
    }

    private sealed class Main(List<string> trace) : IHandoffTarget
    {
        public Queue<Vec3d?> Offers = new();
        public int Collected;
        public Task<Vec3d?> OfferAsync(int items, CancellationToken ct)
        {
            var at = Offers.Count > 0 ? Offers.Dequeue() : null;
            trace.Add($"offer {items} -> {(at is { } a ? $"{a.X},{a.Y},{a.Z}" : "null")}");
            return Task.FromResult(at);
        }
        public Task<int> ConfirmAsync(CancellationToken ct) { trace.Add($"confirm -> {Collected}"); return Task.FromResult(Collected); }
    }

    private static ItemMsg Stack(int id, int n) => new(id, 0, n, "item" + id);
    private static readonly BlockPos A = new(1, 64, 2), B = new(5, 64, 2), C = new(9, 64, 2), P = new(0, 65, 0);

    private static async Task Run(StringBuilder sb, string name, Action<Port, Claims, Main> setup, bool withMain = true, int timeout = 30)
    {
        var trace = new List<string>();
        var port = new Port(trace);
        var claims = new Claims(trace);
        var main = new Main(trace);
        setup(port, claims, main);
        var proc = new DepositProcedure(port, claims, "storage", withMain ? main : null, time: new TickClock()) { RetryDelay = TimeSpan.FromSeconds(2) };
        var r = await proc.RunAsync(TimeSpan.FromSeconds(timeout), CancellationToken.None);
        sb.Append("== ").Append(name).Append('\n');
        foreach (var t in trace) sb.Append("  ").Append(t).Append('\n');
        sb.Append("  => ").Append(r).Append('\n');
    }

    internal static async Task<string> Dump()
    {
        var sb = new StringBuilder();
        await Run(sb, "already empty", (p, c, m) => { });
        await Run(sb, "stale empty, hidden items", (p, c, m) => { p.Hidden.Add(Stack(4, 3)); p.Known.Add(new(A, 5, 2)); });
        await Run(sb, "one chest with room", (p, c, m) => { p.Held.Add(Stack(1, 64)); p.Held.Add(Stack(2, 10)); p.Known.Add(new(A, 5, 2)); p.Dim = -1; });
        await Run(sb, "partial deposits", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 64)); p.Held.Add(Stack(2, 10));
            p.Known.AddRange([new(A, 0, 1), new(B, 3, 4)]);
            p.OnDeposit[B] = () => ActionResult.Success("stored some", 40);
        });
        await Run(sb, "busy and refused chests", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.AddRange([new(A, 5, 1), new(B, 5, 2), new(C, 5, 3)]);
            c.Busy.Add(A); c.Refuse.Add(B);
        });
        await Run(sb, "unreachable, step out works", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.Add(new(A, 5, 1));
            int n = 0;
            p.OnDeposit[A] = () => n++ == 0 ? ActionResult.Fail("can't reach chest") : ActionResult.Success("stored", 5);
            p.StepOut.Enqueue(true);
        });
        await Run(sb, "unreachable, no step out, hand-off", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.Add(new(A, 5, 1));
            p.OnDeposit[A] = () => ActionResult.Fail("no path to chest");
            m.Offers.Enqueue(new Vec3d(3.5, 64, 4.5));
            m.Collected = 5;
        });
        await Run(sb, "full storage, no main, new chest", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.Add(new(A, 0, 1));
            p.OnDeposit[A] = () => ActionResult.Fail("chest is full");
            p.Place = (true, P);
        }, withMain: false);
        await Run(sb, "nothing works until the deadline", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.Add(new(A, 0, 1));
            p.OnDeposit[A] = () => ActionResult.Fail("no route");
            p.StepOut.Enqueue(false); p.StepOut.Enqueue(false); p.StepOut.Enqueue(false);
            m.Offers.Enqueue(null);
            p.SettleOk.Enqueue(false);
        }, timeout: 40);
        await Run(sb, "unsafe then ok, settle throws", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.Add(new(A, 5, 1));
            p.Safe.Enqueue(false); p.Safe.Enqueue(false);
            p.SettleThrows = true;
        });
        await Run(sb, "short deadline", (p, c, m) => { p.Held.Add(Stack(1, 5)); p.Safe.Enqueue(false); p.Safe.Enqueue(false); }, timeout: 3);
        await Run(sb, "deposit moves nothing, then hand-off twice", (p, c, m) =>
        {
            p.Held.Add(Stack(1, 5));
            p.Known.Add(new(A, 5, 1));
            p.OnDeposit[A] = () => ActionResult.Success("stored nothing", 0);
            m.Offers.Enqueue(null); m.Offers.Enqueue(null); m.Offers.Enqueue(new Vec3d(0, 64, 0));
            m.Collected = 2;
        }, timeout: 60);
        return sb.ToString();
    }

    [Fact]
    public async Task Deposit_traces_match_the_golden_dump()
    {
        var actual = (await Dump()).ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "deposit-traces.txt"), actual);
        Assert.Equal(DepositTraceGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
