using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>A deliberate stay in a planned water column (building down): the column and the feet height to hold.</summary>
public sealed record WaterHold(SwimColumn Column, double FeetY);

/// <summary>
/// Primitives for the blueprint build planner (Bot/Building): each one does exactly one planned thing from where the planner
/// put the body, with no pathing of its own, so a plan step is either done as planned or fails with the reason.
/// <list type="bullet">
/// <item><see cref="PlaceAgainstAsync"/>: click a given face of a given neighbour. mc-dev NetServerHandler.a(Packet15Place)
/// checks only that the player's feet are within 8 blocks (squared distance &lt; 64) of the CLICKED block's centre (plus spawn
/// protection), with no line-of-sight raytrace, and ItemBlock checks only that the target cell is replaceable and holds no
/// entity; so a hidden face (the underside of the floor, the outer face of an edge block) is a valid click. The client keeps
/// its own 4.5 eye reach. Facing metas (stairs, doors, furnaces) come from the yaw alone, so <c>yaw</c> may be set freely.</item>
/// <item>Water columns: pour (ItemBucket: the server raytraces the look, so the face must be in view: leaning out at the edge),
/// descend/hover/ascend on <see cref="SwimSim"/>'s policies (the same per-tick inputs the planner simulated), take the source
/// back (only a meta-0 source fills an empty bucket). While a <see cref="WaterHold"/> is active and the body is inside the
/// column with continuous water below (<see cref="SwimSim.InColumn"/>), the void guard and the falling alarm stand aside, as
/// for <see cref="StepIntoVoidAsync"/>; everywhere else they apply as always.</item>
/// </list>
/// </summary>
public sealed partial class MinecraftClient
{
    private volatile WaterHold? _waterHold;

    /// <summary>The water column the body is riding (planned building down), or null.</summary>
    public WaterHold? ActiveWaterHold => _waterHold;

    private bool InWaterHold(PhysicsState phys) =>
        _waterHold is { } h && SwimSim.InColumn(World, phys.Position, h.Column, Math.Min(h.FeetY, phys.Position.Y));

    /// <summary>Idle input while holding a depth in a column: hover at the held feet height, keeping the current look.</summary>
    private MoveInput? WaterHoldInput(PhysicsState phys) =>
        _waterHold is { } h && phys.InWater && InWaterHold(phys) ? SwimSim.Hover(phys, h.Column, h.FeetY, _lookYaw, _lookPitch) : null;

    // ------------------------------------------------------------------ placement / digging from where I stand

    /// <summary>
    /// Places <paramref name="itemId"/> into <paramref name="target"/> by clicking <paramref name="face"/> of the block at
    /// target − normal(face), from where the body is (no pathing). A face not in view is clicked unseen when
    /// <paramref name="allowHidden"/> (server: distance only). <paramref name="yaw"/> overrides the look's yaw (facing metas).
    /// </summary>
    public Task<ActionResult> PlaceAgainstAsync(BlockPos target, BlockFace face, int itemId, int? damage, float? yaw, bool allowHidden,
        CancellationToken ct = default) =>
        RunActionAsync("place", ct, c => PlaceAgainstCoreAsync(target, face, itemId, damage, yaw, allowHidden, c));

    /// <summary><see cref="PlaceAgainstAsync"/> under the caller's action lock (the shelter reflex places its box with it).</summary>
    private async Task<ActionResult> PlaceAgainstCoreAsync(BlockPos target, BlockFace face, int itemId, int? damage, float? yaw, bool allowHidden,
        CancellationToken c)
        {
            if (World.Inventory.CountItem(itemId, damage) == 0) return ActionResult.Fail($"no {ItemName(itemId, damage ?? 0)} in inventory");
            if (!World.IsLoaded(target.X, target.Z)) return ActionResult.Fail($"{Fmt(target)} is not loaded");
            int existing = World.GetBlock(target);
            if (!Aiming.IsReplaceable(World, existing)) return ActionResult.Fail($"{Fmt(target)} is occupied by {BlockName(existing, World.GetMeta(target))}");
            if (EditBlocked(target)) return ActionResult.Fail(BlockedReason(target));
            var n = face.Normal();
            var against = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
            int aid = World.GetBlock(against);
            if (aid == 0 || BlockShapes.IsLiquid(aid) || !BlockShapes.HasCollision(World, against.X, against.Y, against.Z))
                return ActionResult.Fail($"nothing solid at {Fmt(against)} to place against");
            if (Blocks.Get(aid).Interactable) return ActionResult.Fail($"{BlockName(aid, 0)} at {Fmt(against)} would open instead of taking the block");
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            bool sneakBefore = _idleSneak;
            _idleSneak = _waterHold is null; // sneak on land (edge clamping); in a column the hover steers
            try
            {
                var eye = World.Player.EyePosition;
                Vec3d point;
                float aimYaw, aimPitch;
                if (FaceAim.Find(World, eye, against, face, Options.Reach) is { } seen) (point, aimYaw, aimPitch) = (seen.Point, seen.Yaw, seen.Pitch);
                else
                {
                    point = FaceAim.FaceCenter(World, against, face);
                    if (!allowHidden) return ActionResult.Fail($"the {face.ToString().ToLowerInvariant()} face of {Fmt(against)} isn't in view from {Fmt(World.Player.Position)}");
                    // Server: feet within 8 of the clicked block's centre; client: our own eye reach to the face.
                    if (eye.DistanceTo(point) > Options.Reach + 0.3 || World.Player.Position.DistanceTo(against.Center) >= 8)
                        return ActionResult.Fail($"the face of {Fmt(against)} is {eye.DistanceTo(point):0.0} from my eyes (reach {Options.Reach:0.#})");
                    (aimYaw, aimPitch) = PlayerPhysics.YawPitchTo(eye, point);
                    Log($"place: clicking the {face.ToString().ToLowerInvariant()} face of {Fmt(against)} unseen for {Fmt(target)}");
                }
                if (await SelectItemAsync(itemId, damage, c).ConfigureAwait(false) < 0) return ActionResult.Fail($"couldn't hold {ItemName(itemId)}");
                await LookCoreAsync(yaw ?? aimYaw, aimPitch, c).ConfigureAwait(false);
                if (AABB.ForPlayer(World.Player.Position).Intersects(AABB.Block(target.X, target.Y, target.Z)) || FallingIntoCell(target))
                    return ActionResult.Fail(InTheWay + Fmt(target));
                Send(new PlayerBlockPlacementPacket { X = against.X, Y = against.Y, Z = against.Z, Direction = (byte)face, HeldItem = HeldStack() });
                Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
                if (!await WaitUntilAsync(() => !Aiming.IsReplaceable(World, World.GetBlock(target)), TimeSpan.FromMilliseconds(1500), c).ConfigureAwait(false))
                {
                    NoteEditReverted(target, "placement");
                    return ActionResult.Fail($"{ItemName(itemId)} didn't appear at {Fmt(target)}" + SpawnProtectionHint(target));
                }
                NoteEditOk(target);
                return ActionResult.Success($"placed {ItemName(itemId)} at {Fmt(target)}");
            }
            finally { _idleSneak = sneakBefore; }
        }

    /// <summary>Digs a block from where the body is (no pathing): in a column or on a planned stand.</summary>
    public Task<ActionResult> DigHereAsync(BlockPos pos, CancellationToken ct = default) =>
        RunActionAsync("dig", ct, c => DigCoreAsync(pos, c, allowPathing: false));

    /// <summary>
    /// The build slicer's pillar step (nerd pole): standing in <paramref name="feet"/>, jump and place <paramref name="itemId"/>
    /// into that cell under myself at the top of the jump, landing on it. Never beside the void (the jump isn't sneak-guarded)
    /// and never under a ceiling.
    /// </summary>
    public Task<ActionResult> PillarPlaceAsync(BlockPos feet, int itemId, CancellationToken ct = default) =>
        RunActionAsync("pillar", ct, async c =>
        {
            var at = FeetCell();
            if (at != feet) return ActionResult.Fail($"not standing in {Fmt(feet)} to pillar (at {Fmt(at)})");
            if (Safety.VoidNeighbourCount(World, feet.X, feet.Y, feet.Z) > 0) return ActionResult.Fail($"won't pillar at {Fmt(feet)}: the void is beside it");
            if (BlockShapes.HasCollision(World, feet.X, feet.Y + 2, feet.Z)) return ActionResult.Fail($"no room to jump at {Fmt(feet)}");
            if (World.Inventory.CountItem(itemId) == 0) return ActionResult.Fail($"no {ItemName(itemId)} to pillar with");
            return await PillarOnceAsync(feet, itemId, c).ConfigureAwait(false)
                ? ActionResult.Success($"pillared {ItemName(itemId)} at {Fmt(feet)}")
                : ActionResult.Fail($"pillar at {Fmt(feet)} didn't hold");
        });

    // ------------------------------------------------------------------ water columns

    /// <summary>
    /// Pours the water bucket so a source appears at <paramref name="source"/>, beside the block at source − normal(face) (the
    /// edge floor block). The server raytraces the look (ItemBucket), so the face must be in view: from the edge stand the outer
    /// face only shows when leaning out (sneaking: never over the void).
    /// </summary>
    public Task<ActionResult> PourWaterAsync(BlockPos source, BlockFace face, CancellationToken ct = default) =>
        RunActionAsync("pour", ct, async c =>
        {
            if (World.Inventory.CountItem(Items.WaterBucket) == 0) return ActionResult.Fail("no water bucket");
            if (World.Dimension == -1) return ActionResult.Fail("water evaporates in the nether");
            if (BlockShapes.IsWater(World.GetBlock(source)) && World.GetMeta(source) == 0) return ActionResult.Success($"water source already at {Fmt(source)}");
            if (!Aiming.IsReplaceable(World, World.GetBlock(source))) return ActionResult.Fail($"{Fmt(source)} isn't empty");
            var n = face.Normal();
            var against = new BlockPos(source.X - n.X, source.Y - n.Y, source.Z - n.Z);
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            DigAim? Aim() => FaceAim.Find(World, World.Player.EyePosition, against, face, Options.Reach) is { } a
                             && BucketHit(World.Player.EyePosition, a.Point, liquids: false) == against ? a : null;
            bool sneakBefore = _idleSneak;
            _idleSneak = true;
            try
            {
                var aim = Aim();
                if (aim is null && await LeanForPlaceAsync(source, c).ConfigureAwait(false)) aim = Aim();
                if (aim is not { } a) return ActionResult.Fail($"can't see the {face.ToString().ToLowerInvariant()} face of {Fmt(against)} to pour against");
                if (await SelectItemAsync(Items.WaterBucket, null, c).ConfigureAwait(false) < 0) return ActionResult.Fail("couldn't hold the water bucket");
                await LookCoreAsync(a.Yaw, a.Pitch, c).ConfigureAwait(false);
                Send(PlayerBlockPlacementPacket.UseItem(HeldStack()));
                if (!await WaitUntilAsync(() => BlockShapes.IsWater(World.GetBlock(source)), TimeSpan.FromMilliseconds(2000), c).ConfigureAwait(false))
                    return ActionResult.Fail($"water didn't appear at {Fmt(source)}");
                return ActionResult.Success($"poured water at {Fmt(source)}");
            }
            finally { _idleSneak = sneakBefore; }
        });

    /// <summary>Takes the water source back with an empty bucket (the ray passes flowing water and stops at the first source).</summary>
    public Task<ActionResult> PickupWaterAsync(BlockPos source, CancellationToken ct = default) =>
        RunActionAsync("pickup", ct, async c =>
        {
            if (!BlockShapes.IsWater(World.GetBlock(source))) return ActionResult.Success($"no water left at {Fmt(source)}");
            if (World.GetMeta(source) != 0) return ActionResult.Fail($"{Fmt(source)} is flowing water, not the source");
            if (World.Inventory.CountItem(Items.Bucket) == 0) return ActionResult.Fail("no empty bucket");
            await EnsureNoWindowAsync(c).ConfigureAwait(false);
            var eye = World.Player.EyePosition;
            Vec3d? point = null;
            foreach (var p in SourcePoints(source))
                if (eye.DistanceTo(p) <= Options.Reach && BucketHit(eye, p, liquids: true) == source) { point = p; break; }
            if (point is not { } pt) return ActionResult.Fail($"no clear line to the water source at {Fmt(source)} from {Fmt(World.Player.Position)}");
            int before = World.Inventory.CountItem(Items.WaterBucket);
            if (await SelectItemAsync(Items.Bucket, null, c).ConfigureAwait(false) < 0) return ActionResult.Fail("couldn't hold the bucket");
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(eye, pt);
            await LookCoreAsync(yaw, pitch, c).ConfigureAwait(false);
            Send(PlayerBlockPlacementPacket.UseItem(HeldStack()));
            if (!await WaitUntilAsync(() => World.Inventory.CountItem(Items.WaterBucket) > before || !BlockShapes.IsWater(World.GetBlock(source)),
                    TimeSpan.FromMilliseconds(2000), c).ConfigureAwait(false))
                return ActionResult.Fail($"the bucket didn't fill at {Fmt(source)}");
            return ActionResult.Success($"took the water back at {Fmt(source)}");
        });

    /// <summary>
    /// Walks to <paramref name="entry"/> (pathing) unless already in the column, then steps in and sinks to
    /// <paramref name="feetY"/>, holding that depth afterwards (idle hover) until <see cref="SwimOutAsync"/>. Refuses when the
    /// column hasn't formed down past the depth yet (never riding water that ends above the last block).
    /// </summary>
    public Task<ActionResult> SwimToDepthAsync(SwimColumn column, BlockPos entry, double feetY, CancellationToken ct = default) =>
        RunActionAsync("swim", ct, async c =>
        {
            int need = (int)Math.Floor(feetY) - 1;
            if (!await WaitUntilAsync(() => ColumnFormed(column, need), TimeSpan.FromMilliseconds(Options.TickInterval.TotalMilliseconds *
                    (WaterColumn.TicksToReach(column.Source, need) + 40)), c).ConfigureAwait(false))
                return ActionResult.Fail($"the water at {Fmt(column.Source)} doesn't reach down to y {need} yet");
            var me = World.Player.Physics;
            bool inside = _waterHold?.Column == column && me.InWater;
            if (!inside)
            {
                if (Pathfinder.StartCellFor(World, World.Player.Position) != entry)
                {
                    var g = await GotoCoreAsync(new GoalBlock(entry), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, c).ConfigureAwait(false);
                    if (!g.Ok) return ActionResult.Fail($"couldn't get to the column's edge at {Fmt(entry)}: {g.Message}");
                }
                Log($"swim: stepping into the water column at {Fmt(column.Source)} on purpose, down to y {feetY:0.#}");
            }
            _waterHold = new WaterHold(column, feetY);
            long until = TickCount + 900;
            int settled = 0;
            await DriveAsync(phys =>
            {
                var input = inside ? SwimSim.Hover(phys, column, feetY, phys.Yaw, 30) : SwimSim.Descend(phys, column, feetY);
                settled = SwimSim.Settled(phys, column, feetY) ? settled + 1 : 0;
                return new DriverStep(input, Done: settled >= 6 || TickCount > until, AllowDrop: true);
            }, c).ConfigureAwait(false);
            var at = World.Player.Physics;
            if (!SwimSim.Settled(at, column, feetY) && !(at.InWater && Math.Abs(at.Position.Y - feetY) < 0.6))
                return ActionResult.Fail($"couldn't hold y {feetY:0.#} in the water column (at {Fmt(at.Position)})");
            return ActionResult.Success($"in the water column at y {at.Position.Y:0.#}");
        });

    /// <summary>Swims up the column and climbs out onto <paramref name="exit"/>; the water hold ends there.</summary>
    public Task<ActionResult> SwimOutAsync(SwimColumn column, BlockPos exit, CancellationToken ct = default) =>
        RunActionAsync("swim", ct, async c =>
        {
            if (_waterHold is null && !World.Player.Physics.InWater) return ActionResult.Success("not in a water column");
            long until = TickCount + 2400;
            bool done = false;
            await DriveAsync(phys =>
            {
                done = SwimSim.Exited(phys, exit);
                return new DriverStep(SwimSim.Ascend(phys, column, exit), Done: done || TickCount > until, AllowDrop: true);
            }, c).ConfigureAwait(false);
            if (!done && !SwimSim.Exited(World.Player.Physics, exit))
                return ActionResult.Fail($"couldn't climb out of the water column onto {Fmt(exit)} (at {Fmt(World.Player.Position)})");
            _waterHold = null;
            return ActionResult.Success($"climbed out onto {Fmt(exit)}");
        });

    /// <summary>Ends a water hold without moving (the column is gone or the plan changed).</summary>
    public void ReleaseWaterHold() => _waterHold = null;

    private bool ColumnFormed(SwimColumn c, int downTo)
    {
        for (int y = c.SourceY; y >= Math.Max(0, downTo); y--)
            if (!BlockShapes.IsWater(World.GetBlock(c.X, y, c.Z)) && !BlockShapes.HasCollision(World, c.X, y, c.Z)) return false;
        return true;
    }

    /// <summary>Points just inside the source's open faces (top first) for the empty bucket's ray.</summary>
    private static IEnumerable<Vec3d> SourcePoints(BlockPos s)
    {
        yield return new Vec3d(s.X + 0.5, s.Y + 0.98, s.Z + 0.5);
        foreach (var (dx, dz) in new[] { (0.3, 0.0), (-0.3, 0.0), (0.0, 0.3), (0.0, -0.3) }) yield return new Vec3d(s.X + 0.5 + dx, s.Y + 0.98, s.Z + 0.5 + dz);
        yield return new Vec3d(s.X + 0.02, s.Y + 0.6, s.Z + 0.5);
        yield return new Vec3d(s.X + 0.98, s.Y + 0.6, s.Z + 0.5);
        yield return new Vec3d(s.X + 0.5, s.Y + 0.6, s.Z + 0.02);
        yield return new Vec3d(s.X + 0.5, s.Y + 0.6, s.Z + 0.98);
    }

    /// <summary>What the server's bucket raytrace (World.rayTrace) hits first along eye→target: a solid block, or with
    /// <paramref name="liquids"/> also a liquid SOURCE (flowing liquid is passed through: BlockFluids.a(meta, true) = meta 0).</summary>
    private BlockPos? BucketHit(Vec3d eye, Vec3d target, bool liquids)
    {
        var d = target - eye;
        double len = d.Length;
        var dir = d.Normalized();
        BlockPos? last = null;
        var boxes = new List<AABB>();
        for (double t = 0.02; t <= len + 0.05; t += 0.02)
        {
            var q = eye + dir * t;
            var cell = q.ToBlockPos();
            if (cell == last) continue;
            last = cell;
            int id = World.GetBlock(cell);
            if (id == 0) continue;
            if (BlockShapes.IsLiquid(id)) { if (liquids && World.GetMeta(cell) == 0) return cell; continue; }
            boxes.Clear();
            BlockShapes.AddSelectionBoxes(World, cell.X, cell.Y, cell.Z, boxes);
            if (boxes.Count > 0) return cell;
        }
        return null;
    }
}
