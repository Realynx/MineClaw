using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Bot.Tests;

/// <summary>
/// A strict simulated body for the slicer's tests: walking goes through the real <see cref="Pathfinder"/> (doors open, void
/// rules), a placement must click a solid neighbour's face within 4.5 of the eyes from where the body is now (hidden faces
/// allowed, as on the b1.8.1 server), stairs take their meta from the yaw (mc-dev BlockStairs), water is poured, ridden
/// (<see cref="SwimSim"/> on this world: the same policies the client drives) and taken back with the bucket. A pillar step
/// jumps and places into the feet cell (the body must stand in it, headroom for the jump); digging what the body stands on
/// drops it onto what is below. Every place the body ends up is checked against the void guard (<see cref="Unguarded"/>).
/// </summary>
internal sealed class SimBuildBody : IBuildBody
{
    private readonly WorldState _w;
    public SimBuildBody(WorldState w, Dictionary<(int, int), int>? inv = null, string name = "maid_chan", SimCrewLink? crew = null)
    {
        _w = w;
        Name = name;
        Link = crew;
        crew?.Bodies.Add(this);
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

    public string Name { get; }
    public WorldState World => _w;
    /// <summary>Several sim bodies building at once: one shared lock, the block changes mirrored into every body's world.</summary>
    public SimCrewLink? Link { get; }
    public bool Available { get; set; } = true;
    public double? BackInSeconds { get; set; }
    public IClaimBoard? Claims { get; set; }
    public BlockPos Feet0 => Feet;
    public bool InWater => _swim != null;
    /// <summary>Void-guard violations: a jump beside the void, a 1×1 stand over the void (nothing to walk to or lean on at the
    /// feet on any side, the void next to it), a drop into the void.</summary>
    public List<string> Unguarded { get; } = new();
    /// <summary>Every cell the body stood in, in order (arrivals, pillar tops, drops).</summary>
    public List<BlockPos> Trail { get; } = new();
    /// <summary>Every cell a walk went through (the path's foot cells).</summary>
    public List<BlockPos> Walked { get; } = new();
    public int Pillared { get; private set; }

    private BlockPos Feet => Pathfinder.StartCellFor(_w.PlanningView, _w.Player.Position) ?? _w.Player.Position.ToBlockPos();
    private Vec3d Eye => _swim is { } s ? s.EyePosition : _w.Player.EyePosition;

    private Task<ActionResult> GotoCore(BlockPos stand, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_swim != null) return Task.FromResult(ActionResult.Fail("can't walk while swimming"));
        if (Feet == stand) return Task.FromResult(ActionResult.Success("here"));
        var r = Pathfinder.FindPath(_w, Feet, new GoalBlock(stand), PathOptions.Default with { AllowOpenDoors = true, AllowParkour = false, AllowPartial = false });
        if (!r.Success) { Log.Add($"no path {Feet} -> {stand}: {r.Reason}"); return Task.FromResult(ActionResult.Fail("no path: " + r.Reason)); }
        foreach (var st in r.Steps)
        {
            Walked.Add(new BlockPos(st.X, st.Y, st.Z));
            if (st.Open is { } opens)
                foreach (var o in opens) { Log.Add($"open door {o.Pos.X},{o.Pos.Y},{o.Pos.Z}"); }
        }
        Log.Add($"goto {stand.X},{stand.Y},{stand.Z}");
        Teleport(stand);
        return Task.FromResult(ActionResult.Success("ok"));
    }

    private void Teleport(BlockPos stand)
    {
        Link?.Arrive(this, stand);
        double floor = GoalUseBlock.EyeAt(_w, stand.X, stand.Y, stand.Z).Y - PlayerPhysics.EyeHeight;
        _w.Player.ServerTeleport(new Vec3d(stand.X + 0.5, floor, stand.Z + 0.5), floor + 1.62, 0, 0, true);
        Guard(stand);
    }

    /// <summary>Standing in <paramref name="feet"/>: an isolated 1×1 (no side to step onto or lean on) with the void beside it
    /// is unguarded.</summary>
    private void Guard(BlockPos feet)
    {
        Trail.Add(feet);
        if (Safety.IsOverVoid(_w, feet.X, feet.Y, feet.Z)) { Unguarded.Add($"over the void at {feet}"); return; }
        bool isolated = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.All(d =>
            !BlockShapes.HasCollision(_w, feet.X + d.Item1, feet.Y, feet.Z + d.Item2) && !BlockShapes.HasCollision(_w, feet.X + d.Item1, feet.Y + 1, feet.Z + d.Item2)
            && !BlockShapes.HasCollision(_w, feet.X + d.Item1, feet.Y - 1, feet.Z + d.Item2));
        if (isolated && Safety.VoidNeighbourCount(_w, feet.X, feet.Y, feet.Z) > 0) Unguarded.Add($"on a 1x1 over the void at {feet}");
    }

    private Task<ActionResult> PillarCore(BlockPos feet, int itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_swim != null) return Fail("can't pillar while swimming");
        if (Feet != feet) return Fail($"not standing in {feet} (at {Feet})");
        if (_w.GetBlock(feet) != 0) return Fail("occupied");
        if (!BuildBlocks.NormalCube(_w.GetBlock(feet.Down()))) return Fail("nothing solid under me to place on");
        if (BlockShapes.HasCollision(_w, feet.X, feet.Y + 1, feet.Z) || BlockShapes.HasCollision(_w, feet.X, feet.Y + 2, feet.Z)) return Fail("no room to jump");
        if (Safety.VoidNeighbourCount(_w, feet.X, feet.Y, feet.Z) > 0) Unguarded.Add($"jumped beside the void at {feet}");
        var key = (itemId, 0);
        if (Inv.GetValueOrDefault(key) <= 0) return Fail($"no {itemId}");
        Inv[key]--;
        Set(feet.X, feet.Y, feet.Z, itemId, 0);
        foreach (var world in Worlds) world.Placed.Note(feet, world.Dimension, itemId, Name, DateTimeOffset.UtcNow);
        Link?.Event(this, $"scaffold {feet.X},{feet.Y},{feet.Z}");
        Placed.Add(feet);
        Pillared++;
        Log.Add($"pillar {feet.X},{feet.Y},{feet.Z}");
        Teleport(feet.Up());
        return Task.FromResult(ActionResult.Success("pillared"));
    }

    /// <summary>The block under the body went: it drops onto what is below (the void: a violation).</summary>
    private void Fall()
    {
        if (_swim != null) return;
        var p = _w.Player.Position;
        if (Safety.SupportBlocks(_w, p).Count > 0) return;
        int x = (int)Math.Floor(p.X), z = (int)Math.Floor(p.Z);
        int s = Safety.SupportBelow(_w, x, (int)Math.Floor(p.Y) - 1, z);
        if (s < 0) { Unguarded.Add($"fell into the void at {x},{(int)p.Y},{z}"); return; }
        Log.Add($"drop to y {s + 1}");
        Teleport(new BlockPos(x, s + 1, z));
    }

    private Task<ActionResult> PlaceCore(BuildStep step, int itemId, int damage, CancellationToken ct)
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
        Set(step.Pos.X, step.Pos.Y, step.Pos.Z, step.Id, meta);
        if (BuildBlocks.KindOf(step.Id) == BuildKind.Door) Set(step.Pos.X, step.Pos.Y + 1, step.Pos.Z, step.Id, meta | 8);
        foreach (var world in Worlds) world.Placed.Note(step.Pos, world.Dimension, step.Id, Name, DateTimeOffset.UtcNow);
        Link?.Event(this, $"{(step.Kind == StepKind.Scaffold ? "scaffold" : "place")} {step.Pos.X},{step.Pos.Y},{step.Pos.Z}");
        Placed.Add(step.Pos);
        Log.Add($"{(step.Kind == StepKind.Scaffold ? "scaffold" : "place")} {step.Pos.X},{step.Pos.Y},{step.Pos.Z}{(step.Hidden ? " hidden" : "")}{(_swim != null ? " swimming" : "")}");
        AfterPlace?.Invoke(++_placed);
        return Task.FromResult(ActionResult.Success("placed"));
    }

    private Task<ActionResult> DigCore(BlockPos pos, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int id = _w.GetBlock(pos);
        // The client's dig veto (MinecraftClient.DigSafetyVeto): the block I stand on comes down only as a 1-block step onto
        // something solid (my own pillar), never a longer drop.
        if (_swim == null && Safety.SupportBlocks(_w, _w.Player.Position).Contains(pos) && !Safety.RemovalIsShortSafeDrop(_w, _w.Player.Position, pos))
        {
            Log.Add($"won't dig {pos.X},{pos.Y},{pos.Z}: standing on it");
            return Fail($"won't dig the block I'm standing on at {pos.X},{pos.Y},{pos.Z} (the drop below is too far)");
        }
        Log.Add($"dig {pos.X},{pos.Y},{pos.Z}{(_swim != null ? " swimming" : "")}");
        Set(pos.X, pos.Y, pos.Z, 0, 0);
        Link?.Event(this, $"dig {pos.X},{pos.Y},{pos.Z}");
        if (id != 0) Inv[(id, 0)] = Inv.GetValueOrDefault((id, 0)) + 1;
        Placed.Remove(pos);
        Fall();
        return Task.FromResult(ActionResult.Success("dug"));
    }

    private Task<ActionResult> DigHereCore(BlockPos pos, CancellationToken ct)
    {
        if (Eye.DistanceTo(pos.Center) > 5.2) return Fail($"{pos} out of reach to dig");
        return DigCore(pos, ct);
    }

    public int Count(int itemId, int damage) => Inv.GetValueOrDefault((itemId, BuildBlocks.HasVariants(itemId) && damage >= 0 ? damage : 0));

    public Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct) =>
        Task.FromResult(Count(itemId, damage) >= count ? null : $"missing {Items.Get(itemId).Name} (need {count}, have {Count(itemId, damage)})");

    public Task<string?> ClutchAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public bool IsProtected(BlockPos p, int id) => false;

    // ---- water

    private Task<ActionResult> PourCore(BlockPos source, BlockFace face, CancellationToken ct)
    {
        if (Count(Items.WaterBucket, 0) == 0) return Fail("no water bucket");
        if (!BuildBlocks.Supports(_w.GetBlock(source - face.Normal()))) return Fail("nothing to pour against");
        if (_w.Player.EyePosition.DistanceTo(source.Center) > 4.5) return Fail("too far to pour");
        foreach (var (p, m) in WaterColumn.Cells(_w, source)) Set(p.X, p.Y, p.Z, m == 0 ? Blocks.Water : Blocks.FlowingWater, m);
        Inv[(Items.WaterBucket, 0)]--;
        Inv[(Items.Bucket, 0)] = Inv.GetValueOrDefault((Items.Bucket, 0)) + 1;
        Log.Add($"pour {source.X},{source.Y},{source.Z}");
        return Task.FromResult(ActionResult.Success("poured"));
    }

    private Task<ActionResult> SwimCore(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SwimResult r;
        if (_swim == null)
        {
            if (Feet != entry) { var g = GotoCore(entry, ct).Result; if (!g.Ok) return Task.FromResult(g); }
            r = SwimSim.SimulateDescent(_w, entry, column, feetY);
        }
        else r = SwimSim.SimulateHover(_w, _swim, column, feetY);
        if (!r.Ok) return Fail("swim: " + r.Why);
        _swim = r.End;
        Log.Add($"swim to y {feetY} ({r.Ticks} ticks, min {r.MinFeetY:0.00})");
        return Task.FromResult(ActionResult.Success("swimming"));
    }

    private Task<ActionResult> SwimOutCore(SwimColumn column, BlockPos exit, CancellationToken ct)
    {
        if (_swim == null) return Task.FromResult(ActionResult.Success("not swimming"));
        var r = SwimSim.SimulateAscent(_w, _swim, column, exit);
        if (!r.Ok) return Fail("climb out: " + r.Why);
        _swim = null;
        Teleport(exit);
        Log.Add($"swim out ({r.Ticks} ticks)");
        return Task.FromResult(ActionResult.Success("out"));
    }

    private Task<ActionResult> PickupCore(BlockPos source, CancellationToken ct)
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
            Set(p.X, p.Y, p.Z, 0, 0);
            foreach (var f in BlockFaceExtensions.All) if (f != BlockFace.Top) q.Enqueue(p.Offset(f));
        }
    }

    private static Task<ActionResult> Fail(string why) => Task.FromResult(ActionResult.Fail(why));

    // ---- crew sims: each action under the shared lock, after a short delay so the bodies interleave

    public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct) => Act(() => GotoCore(stand, ct), ct);
    public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct) => Act(() => PlaceCore(step, itemId, damage, ct), ct);
    public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) => Act(() => DigCore(pos, ct), ct);
    public Task<ActionResult> DigHereAsync(BlockPos pos, CancellationToken ct) => Act(() => DigHereCore(pos, ct), ct);
    public Task<ActionResult> PourWaterAsync(BlockPos source, BlockFace face, CancellationToken ct) => Act(() => PourCore(source, face, ct), ct);
    public Task<ActionResult> SwimToDepthAsync(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct) => Act(() => SwimCore(column, entry, feetY, ct), ct);
    public Task<ActionResult> SwimOutAsync(SwimColumn column, BlockPos exit, CancellationToken ct) => Act(() => SwimOutCore(column, exit, ct), ct);
    public Task<ActionResult> PickupWaterAsync(BlockPos source, CancellationToken ct) => Act(() => PickupCore(source, ct), ct);
    public Task<ActionResult> PillarAsync(BlockPos feet, int itemId, CancellationToken ct) => Act(() => PillarCore(feet, itemId, ct), ct);

    private async Task<ActionResult> Act(Func<Task<ActionResult>> f, CancellationToken ct)
    {
        if (Link == null) return Available ? await f() : ActionResult.Fail("dead");
        await Task.Delay(Link.Delay, ct);
        if (!Available) return ActionResult.Fail("dead");
        lock (Link.Sync) return f().GetAwaiter().GetResult();
    }

    private IEnumerable<WorldState> Worlds => Link == null ? [_w] : Link.Bodies.Select(b => b._w).Distinct();

    /// <summary>A block change: this body's world and, in a crew, every other body's (they all see the same blocks).</summary>
    private void Set(int x, int y, int z, int id, int meta)
    {
        foreach (var world in Worlds) world.SetBlock(x, y, z, id, meta);
    }
}

/// <summary>
/// Sim bodies building at once: each action takes the shared lock (one world-changing action at a time, interleaved between
/// bodies by a short delay), every placement/dig is mirrored into every body's world and logged in one global order, and each
/// arrival is checked against where the other bodies stand (two bodies in one cell is a violation).
/// </summary>
internal sealed class SimCrewLink
{
    public readonly object Sync = new();
    public int Delay { get; init; } = 1;
    public List<SimBuildBody> Bodies { get; } = new();
    public List<(int Seq, string Body, string What)> Events { get; } = new();
    public List<string> Violations { get; } = new();
    private readonly Dictionary<string, BlockPos> _at = new();
    private int _seq;

    public void Arrive(SimBuildBody b, BlockPos feet)
    {
        lock (Sync)
        {
            foreach (var (other, p) in _at)
                if (other != b.Name && Bodies.Any(x => x.Name == other && x.Available) && p.X == feet.X && p.Z == feet.Z && Math.Abs(p.Y - feet.Y) <= 1)
                    Violations.Add($"{b.Name} stepped into {feet} where {other} stands");
            _at[b.Name] = feet;
        }
    }

    public void Event(SimBuildBody b, string what)
    {
        lock (Sync) Events.Add((++_seq, b.Name, what));
    }

    /// <summary>The first time each cell got a block (global order).</summary>
    public Dictionary<BlockPos, int> FirstPlaced()
    {
        var d = new Dictionary<BlockPos, int>();
        lock (Sync)
            foreach (var (seq, _, what) in Events)
            {
                if (!what.StartsWith("place ") && !what.StartsWith("scaffold ")) continue;
                var c = what[(what.IndexOf(' ') + 1)..].Split(',').Select(int.Parse).ToArray();
                d.TryAdd(new BlockPos(c[0], c[1], c[2]), seq);
            }
        return d;
    }
}
