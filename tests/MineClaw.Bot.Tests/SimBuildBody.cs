using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// A strict simulated body for the slicer's tests: walking goes through the real <see cref="Pathfinder"/> (doors open, void
/// rules), a placement must click a solid neighbour's face within 4.5 of the eyes from where the body is now (hidden faces
/// allowed, as on the b1.8.1 server), stairs take their meta from the yaw (mc-dev BlockStairs), water is poured, ridden
/// (<see cref="SwimSim"/> on this world: the same policies the client drives) and taken back with the bucket.
/// </summary>
internal sealed class SimBuildBody : IBuildBody
{
    private readonly WorldState _w;
    public SimBuildBody(WorldState w, Dictionary<(int, int), int>? inv = null)
    {
        _w = w;
        Inv = inv ?? new() { [(Blocks.Cobblestone, 0)] = 512, [(Blocks.Planks, 0)] = 128, [(Items.WoodenDoor, 0)] = 4, [(Blocks.GlassPane, 0)] = 16 };
    }

    public Dictionary<(int Id, int Dmg), int> Inv { get; }
    public List<string> Log { get; } = new();
    public HashSet<BlockPos> Placed { get; } = new();
    public int PlaceLimit = int.MaxValue;
    public CancellationTokenSource? Stop;
    /// <summary>Called after each successful placement (a "player" interferes, tests).</summary>
    public Action<int>? AfterPlace;
    private PhysicsState? _swim;
    private int _placed;

    public string Name => "maid_chan";
    public WorldState World => _w;
    public bool InWater => _swim != null;

    private BlockPos Feet => Pathfinder.StartCellFor(_w.PlanningView, _w.Player.Position) ?? _w.Player.Position.ToBlockPos();
    private Vec3d Eye => _swim is { } s ? s.EyePosition : _w.Player.EyePosition;

    public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_swim != null) return Task.FromResult(ActionResult.Fail("can't walk while swimming"));
        if (Feet == stand) return Task.FromResult(ActionResult.Success("here"));
        var r = Pathfinder.FindPath(_w, Feet, new GoalBlock(stand), PathOptions.Default with { AllowOpenDoors = true, AllowParkour = false, AllowPartial = false });
        if (!r.Success) { Log.Add($"no path {Feet} -> {stand}: {r.Reason}"); return Task.FromResult(ActionResult.Fail("no path: " + r.Reason)); }
        foreach (var st in r.Steps)
            if (st.Open is { } opens)
                foreach (var o in opens) { Log.Add($"open door {o.Pos.X},{o.Pos.Y},{o.Pos.Z}"); }
        Log.Add($"goto {stand.X},{stand.Y},{stand.Z}");
        Teleport(stand);
        return Task.FromResult(ActionResult.Success("ok"));
    }

    private void Teleport(BlockPos stand)
    {
        double floor = GoalUseBlock.EyeAt(_w, stand.X, stand.Y, stand.Z).Y - PlayerPhysics.EyeHeight;
        _w.Player.ServerTeleport(new Vec3d(stand.X + 0.5, floor, stand.Z + 0.5), floor + 1.62, 0, 0, true);
    }

    public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (PlaceLimit-- <= 0) { Stop?.Cancel(); ct.ThrowIfCancellationRequested(); }
        var key = (itemId, BuildBlocks.HasVariants(itemId) ? damage : 0);
        if (Inv.GetValueOrDefault(key) <= 0) return Fail($"no {itemId}");
        if (!Aiming.IsReplaceable(_w, _w.GetBlock(step.Pos))) return Fail("occupied");
        var face = step.Face ?? BlockFaceExtensions.All.FirstOrDefault(f => BuildBlocks.Supports(_w.GetBlock(step.Pos - f.Normal())));
        var against = step.Pos - face.Normal();
        if (!BuildBlocks.Supports(_w.GetBlock(against))) return Fail($"nothing solid at {against} to click");
        var n = face.Normal();
        var point = new Vec3d(step.Pos.X + 0.5 - n.X * 0.5, step.Pos.Y + 0.5 - n.Y * 0.5, step.Pos.Z + 0.5 - n.Z * 0.5);
        double d = Eye.DistanceTo(point);
        if (d > 4.5 + 0.35) return Fail($"face {d:0.0} away from my eyes");
        var feet = _swim?.Position ?? _w.Player.Position;
        if (AABB.ForPlayer(feet).Intersects(AABB.Block(step.Pos.X, step.Pos.Y, step.Pos.Z))) return Fail("standing in it");
        int meta = step.Meta;
        if (BuildBlocks.KindOf(step.Id) == BuildKind.Stairs && step.Yaw is { } yaw)
            meta = BuildBlocks.StairsMeta(BuildPlanner.Quarter(yaw)); // the server: BlockStairs.postPlace from the yaw
        Inv[key]--;
        _w.SetBlock(step.Pos.X, step.Pos.Y, step.Pos.Z, step.Id, meta);
        if (BuildBlocks.KindOf(step.Id) == BuildKind.Door) _w.SetBlock(step.Pos.X, step.Pos.Y + 1, step.Pos.Z, step.Id, meta | 8);
        _w.Placed.Note(step.Pos, _w.Dimension, step.Id, Name, DateTimeOffset.UtcNow);
        Placed.Add(step.Pos);
        Log.Add($"{(step.Kind == StepKind.Scaffold ? "scaffold" : "place")} {step.Pos.X},{step.Pos.Y},{step.Pos.Z}{(step.Hidden ? " hidden" : "")}{(_swim != null ? " swimming" : "")}");
        AfterPlace?.Invoke(++_placed);
        return Task.FromResult(ActionResult.Success("placed"));
    }

    public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int id = _w.GetBlock(pos);
        Log.Add($"dig {pos.X},{pos.Y},{pos.Z}{(_swim != null ? " swimming" : "")}");
        _w.SetBlock(pos.X, pos.Y, pos.Z, 0, 0);
        if (id != 0) Inv[(id, 0)] = Inv.GetValueOrDefault((id, 0)) + 1;
        Placed.Remove(pos);
        return Task.FromResult(ActionResult.Success("dug"));
    }

    public Task<ActionResult> DigHereAsync(BlockPos pos, CancellationToken ct)
    {
        if (Eye.DistanceTo(pos.Center) > 5.2) return Fail($"{pos} out of reach to dig");
        return DigAsync(pos, ct);
    }

    public int Count(int itemId, int damage) => Inv.GetValueOrDefault((itemId, BuildBlocks.HasVariants(itemId) && damage >= 0 ? damage : 0));

    public Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct) =>
        Task.FromResult(Count(itemId, damage) >= count ? null : $"missing {Items.Get(itemId).Name} (need {count}, have {Count(itemId, damage)})");

    public Task<string?> ClutchAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public bool IsProtected(BlockPos p, int id) => false;

    // ---- water

    public Task<ActionResult> PourWaterAsync(BlockPos source, BlockFace face, CancellationToken ct)
    {
        if (Count(Items.WaterBucket, 0) == 0) return Fail("no water bucket");
        if (!BuildBlocks.Supports(_w.GetBlock(source - face.Normal()))) return Fail("nothing to pour against");
        if (_w.Player.EyePosition.DistanceTo(source.Center) > 4.5) return Fail("too far to pour");
        foreach (var (p, m) in WaterColumn.Cells(_w, source)) _w.SetBlock(p.X, p.Y, p.Z, m == 0 ? Blocks.Water : Blocks.FlowingWater, m);
        Inv[(Items.WaterBucket, 0)]--;
        Inv[(Items.Bucket, 0)] = Inv.GetValueOrDefault((Items.Bucket, 0)) + 1;
        Log.Add($"pour {source.X},{source.Y},{source.Z}");
        return Task.FromResult(ActionResult.Success("poured"));
    }

    public Task<ActionResult> SwimToDepthAsync(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SwimResult r;
        if (_swim == null)
        {
            if (Feet != entry) { var g = GotoAsync(entry, ct).Result; if (!g.Ok) return Task.FromResult(g); }
            r = SwimSim.SimulateDescent(_w, entry, column, feetY);
        }
        else r = SwimSim.SimulateHover(_w, _swim, column, feetY);
        if (!r.Ok) return Fail("swim: " + r.Why);
        _swim = r.End;
        Log.Add($"swim to y {feetY} ({r.Ticks} ticks, min {r.MinFeetY:0.00})");
        return Task.FromResult(ActionResult.Success("swimming"));
    }

    public Task<ActionResult> SwimOutAsync(SwimColumn column, BlockPos exit, CancellationToken ct)
    {
        if (_swim == null) return Task.FromResult(ActionResult.Success("not swimming"));
        var r = SwimSim.SimulateAscent(_w, _swim, column, exit);
        if (!r.Ok) return Fail("climb out: " + r.Why);
        _swim = null;
        Teleport(exit);
        Log.Add($"swim out ({r.Ticks} ticks)");
        return Task.FromResult(ActionResult.Success("out"));
    }

    public Task<ActionResult> PickupWaterAsync(BlockPos source, CancellationToken ct)
    {
        if (_swim != null) return Fail("still swimming");
        if (Count(Items.Bucket, 0) == 0) return Fail("no bucket");
        if (!BlockShapes.IsWater(_w.GetBlock(source)) || _w.GetMeta(source) != 0) return Fail("no source there");
        // BlockFlowing: without its source the falling/spread water evaluates to -1 and dries up.
        DryFrom(source);
        Inv[(Items.Bucket, 0)]--;
        Inv[(Items.WaterBucket, 0)] = Inv.GetValueOrDefault((Items.WaterBucket, 0)) + 1;
        Log.Add($"pickup {source.X},{source.Y},{source.Z}");
        return Task.FromResult(ActionResult.Success("picked up"));
    }

    private void DryFrom(BlockPos source)
    {
        var q = new Queue<BlockPos>([source]);
        var seen = new HashSet<BlockPos>();
        while (q.Count > 0)
        {
            var p = q.Dequeue();
            if (!seen.Add(p) || !BlockShapes.IsWater(_w.GetBlock(p))) continue;
            _w.SetBlock(p.X, p.Y, p.Z, 0, 0);
            foreach (var f in BlockFaceExtensions.All) if (f != BlockFace.Top) q.Enqueue(p.Offset(f));
        }
    }

    private static Task<ActionResult> Fail(string why) => Task.FromResult(ActionResult.Fail(why));
}
