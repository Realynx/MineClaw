using System.Collections.Concurrent;
using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Right-click primitives beyond plain block activation, faithful to b1.8.1 (protocol 17):
/// <list type="bullet">
/// <item>Use the held item on a block face: 0x0F with that face (server: <c>blockActivated</c> first, then
/// <c>Item.onItemUse</c>; it does not raytrace). Items whose action is <c>onItemRightClick</c> (buckets, bow, food,
/// snowballs...) are followed by 0x0F with direction −1, exactly like the vanilla client when onItemUse declines.</item>
/// <item>Place against a chosen face (wall signs, torches, ladders, buttons, levers, trapdoors), sneaking like every
/// placement so the EdgeGuard's edge clamping applies.</item>
/// <item>Right-click an entity: 0x07 with LeftClick = false (<c>useCurrentItemOnEntity</c>: shears, bucket, bone, saddle,
/// boats and minecarts). The server needs line of sight and distSq &lt; 36.</item>
/// <item>Vehicles: entered by right-clicking them; left by sneaking (0x13 crouch) with a right-click on the vehicle
/// again as fallback (b1.8.1 <c>mountEntity</c> toggles). Riding freezes local physics (the server drives the body).</item>
/// <item>Signs: C→S 0x82 with four sanitized lines (≤ 15 allowed chars) after placing; verified by the server's 0x82
/// echo (<c>markBlockNeedsUpdate</c> → tile-entity description packet).</item>
/// <item>Bow: 0x0F direction −1 with the bow held starts the draw, 0x0E status 5 releases after the charge; aim is
/// solved with the b1.8 arrow physics (<see cref="ArrowBallistics"/>).</item>
/// </list>
/// </summary>
public sealed partial class MinecraftClient
{
    private volatile int _vehicleId = -1;
    private readonly ConcurrentDictionary<int, long> _entityStatusSeq = new();
    private long _statusSeq;

    /// <summary>Entity id of the boat/minecart the bot rides (from 0x27), or −1.</summary>
    public int RidingEntityId => _vehicleId;

    /// <summary>Raised on the event dispatcher when a 0x09 respawn moves the bot to another dimension
    /// (−1 nether, 0 overworld): (from, to). The current action was cancelled, signs and weather cleared.</summary>
    public event Action<int, int>? DimensionChanged;

    /// <summary>Entity "use" range from the eye (vanilla survival pick range for entities is 3 blocks).</summary>
    public const double EntityUseReach = 3.0;

    // ------------------------------------------------------------------ use item on a block face

    /// <summary>Holds <paramref name="itemId"/> (optionally a specific <paramref name="damage"/>, e.g. bone meal = dye 15)
    /// and right-clicks <paramref name="face"/> of <paramref name="pos"/>: hoe on dirt/grass, bone meal on crops/saplings,
    /// seeds on farmland, flint and steel, buckets on a face, minecart on rail. Looks at that face first (pathing into reach
    /// if needed) and succeeds only when a block change or an inventory change confirms the effect.</summary>
    public Task<ActionResult> UseItemOnBlockAsync(BlockPos pos, BlockFace face, int itemId, int? damage = null, CancellationToken ct = default) =>
        RunActionAsync("use_on_block", ct, c => UseOnBlockCoreAsync(pos, face, itemId, damage, c));

    /// <summary>b1.8.1 items that act through <c>onItemRightClick</c> (server-side raytrace or no target at all).</summary>
    public static bool UsesRightClickAction(int itemId) =>
        itemId is Items.Bucket or Items.WaterBucket or Items.LavaBucket or Items.MilkBucket or Items.Bow or Items.Snowball
            or Items.Egg or Items.FishingRod or Items.Boat or Items.MushroomStew
        || Items.Food(itemId) is not null;

    private static bool IsHoe(int id) => id is >= Items.WoodenHoe and <= Items.GoldenHoe;

    private async Task<ActionResult> UseOnBlockCoreAsync(BlockPos pos, BlockFace face, int itemId, int? damage, CancellationToken ct)
    {
        if (face == BlockFace.None) return ActionResult.Fail("a face is required");
        if (pos.Y is < 0 or >= WorldState.Height) return ActionResult.Fail($"{Fmt(pos)} is outside the world");
        if (!World.IsLoaded(pos.X, pos.Z)) return ActionResult.Fail($"{Fmt(pos)} is not loaded");
        if (World.Inventory.CountItem(itemId, damage) == 0) return ActionResult.Fail($"no {ItemName(itemId, damage ?? 0)} in inventory");
        int id = World.GetBlock(pos);
        string name = BlockName(id, World.GetMeta(pos));
        if (id != 0 && Blocks.Get(id).Interactable)
            return ActionResult.Fail($"{name} at {Fmt(pos)} would be opened/used instead (b1.8.1 activates the block first)");
        if ((IsHoe(itemId) || itemId == Items.FlintAndSteel) && IsBlockProtected(pos, id))
            return ActionResult.Fail($"{name} at {Fmt(pos)} is protected");
        var beside = pos.Offset(face);
        if (itemId == Items.FlintAndSteel)
        {
            if (IsBlockProtected(beside, World.GetBlock(beside))) return ActionResult.Fail($"{Fmt(beside)} is protected");
            if (id == Blocks.Tnt || BlockFaceExtensions.All.Any(f => World.GetBlock(beside.Offset(f)) == Blocks.Tnt))
                return ActionResult.Fail("won't light a fire next to TNT");
        }

        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        bool raytraced = UsesRightClickAction(itemId);
        var aim = FaceAim.Find(World, World.Player.EyePosition, pos, face, Options.Reach);
        if (aim is null)
        {
            await GotoCoreAsync(new GoalWithinReach(pos, Options.Reach - 0.6), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct)
                .ConfigureAwait(false);
            aim = FaceAim.Find(World, World.Player.EyePosition, pos, face, Options.Reach);
        }
        Vec3d lookAt;
        if (aim is { } a) lookAt = a.Point;
        else
        {
            // 0x0F names the face itself and the server doesn't raytrace it, so a hidden face in reach is still usable —
            // except for items the server raytraces from my look (buckets...).
            var center = FaceAim.FaceCenter(World, pos, face);
            if (raytraced || World.Player.EyePosition.DistanceTo(center) > Options.Reach)
                return ActionResult.Fail($"can't see/reach the {face.ToString().ToLowerInvariant()} face of {name} at {Fmt(pos)}");
            lookAt = center;
        }

        if (await SelectItemAsync(itemId, damage, ct).ConfigureAwait(false) < 0) return ActionResult.Fail($"couldn't hold {ItemName(itemId, damage ?? 0)}");
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, lookAt);
        await LookCoreAsync(yaw, pitch, ct).ConfigureAwait(false);

        int heldSlot = 36 + World.Player.HeldSlot;
        var heldBefore = World.Inventory.GetPlayerSlot(heldSlot);
        int countBefore = World.Inventory.CountItem(itemId);
        int id0 = World.GetBlock(pos), m0 = World.GetMeta(pos), bid0 = World.GetBlock(beside), bm0 = World.GetMeta(beside);
        bool Changed() =>
            World.GetBlock(pos) != id0 || World.GetMeta(pos) != m0 || World.GetBlock(beside) != bid0 || World.GetMeta(beside) != bm0
            || !Same(World.Inventory.GetPlayerSlot(heldSlot), heldBefore) || World.Inventory.CountItem(itemId) != countBefore;

        _lastActivated = null;
        Send(new PlayerBlockPlacementPacket { X = pos.X, Y = pos.Y, Z = pos.Z, Direction = (byte)face, HeldItem = HeldStack() });
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        if (raytraced) Send(PlayerBlockPlacementPacket.UseItem(HeldStack()));

        if (!await WaitUntilAsync(Changed, TimeSpan.FromMilliseconds(1200), ct).ConfigureAwait(false))
            return ActionResult.Fail($"{ItemName(itemId, damage ?? 0)} on {name} at {Fmt(pos)} had no effect" + SpawnProtectionHint(pos));
        await WaitTicksAsync(1, ct).ConfigureAwait(false); // let the paired block change / SetSlot land too
        string effect = World.GetBlock(pos) != id0 ? $"{name} -> {BlockName(World.GetBlock(pos), World.GetMeta(pos))}"
            : World.GetMeta(pos) != m0 ? $"{name} meta {m0} -> {World.GetMeta(pos)}"
            : World.GetBlock(beside) != bid0 || World.GetMeta(beside) != bm0 ? $"{BlockName(World.GetBlock(beside), 0)} at {Fmt(beside)}"
            : "inventory changed";
        return ActionResult.Success($"used {ItemName(itemId, damage ?? 0)} on {name} at {Fmt(pos)}: {effect}");
    }

    private static bool Same(ItemStack a, ItemStack b) =>
        a.IsEmpty ? b.IsEmpty : !b.IsEmpty && a.Id == b.Id && a.Count == b.Count && a.Damage == b.Damage;

    // ------------------------------------------------------------------ placing against a chosen face

    /// <summary>Places <paramref name="itemId"/> into (x,y,z) by clicking <paramref name="face"/> of the neighbour at
    /// (x,y,z) − normal(face): <see cref="BlockFace.Top"/> = on the block below (sign post, floor torch), a horizontal face =
    /// hung on that wall (wall sign meta = face, ladder, torch, button, lever, trapdoor). Sneaks like every placement.</summary>
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, BlockFace face, CancellationToken ct = default) =>
        RunActionAsync("place", ct, c => PlaceCoreAsync(new BlockPos(x, y, z), itemId, c, face: face));

    /// <summary>Places a wall-mounted block (wall sign, torch, ladder, button, lever, trapdoor) into (x,y,z) on a solid
    /// neighbouring wall chosen by <see cref="WallFaceFor"/> (never an interactable block such as a chest).
    /// <paramref name="exclude"/> vetoes walls (e.g. the chest a label is for).</summary>
    public Task<ActionResult> PlaceOnWallAsync(int x, int y, int z, int itemId, Func<BlockPos, bool>? exclude = null, CancellationToken ct = default) =>
        RunActionAsync("place", ct, async c =>
        {
            var pos = new BlockPos(x, y, z);
            var face = WallFaceFor(pos, exclude);
            if (face is null) return ActionResult.Fail($"no solid wall next to {Fmt(pos)} to hang a {ItemName(itemId)} on");
            return await PlaceCoreAsync(pos, itemId, c, face: face).ConfigureAwait(false);
        });

    /// <summary>The 0x0F face for hanging a block in <paramref name="target"/> on a horizontal neighbour (see
    /// <see cref="FaceAim.WallFace"/>), or null.</summary>
    public BlockFace? WallFaceFor(BlockPos target, Func<BlockPos, bool>? exclude = null) =>
        FaceAim.WallFace(World, target, World.Player.EyePosition, Options.Reach, exclude);

    /// <summary>Aim for placing into <paramref name="target"/> by clicking <paramref name="face"/> of its neighbour.</summary>
    private PlaceAim? FacePlaceAim(BlockPos target, BlockFace face)
    {
        if (!Aiming.IsReplaceable(World, World.GetBlock(target))) return null;
        if (AABB.ForPlayer(World.Player.Position).Intersects(AABB.Block(target.X, target.Y, target.Z))) return null;
        var n = face.Normal();
        var against = new BlockPos(target.X - n.X, target.Y - n.Y, target.Z - n.Z);
        int id = World.GetBlock(against);
        if (id == 0 || BlockShapes.IsLiquid(id) || Blocks.Get(id).Interactable) return null;
        var a = FaceAim.Find(World, World.Player.EyePosition, against, face, Options.Reach);
        return a is { } v ? new PlaceAim(target, against, face, v.Point, v.Yaw, v.Pitch, v.Distance) : null;
    }

    // ------------------------------------------------------------------ entities

    /// <summary>Right-clicks an entity (0x07, LeftClick = false) holding <paramref name="itemId"/> (or whatever is held):
    /// shears on a sheep, bucket on a cow, bone on a wolf, saddle on a pig, or getting into a boat/minecart. Walks within
    /// <see cref="EntityUseReach"/> and line of sight first. Not an attack, so not gated by the combat check. Succeeds when
    /// the server answers: inventory change, entity metadata/status, or (vehicles) the 0x27 attach.</summary>
    public Task<ActionResult> UseEntityAsync(int entityId, int? itemId = null, int? damage = null, CancellationToken ct = default) =>
        RunActionAsync("use_entity", ct, c => UseEntityCoreAsync(entityId, itemId, damage, c));

    private async Task<ActionResult> UseEntityCoreAsync(int entityId, int? itemId, int? damage, CancellationToken ct)
    {
        if (entityId == World.Player.EntityId) return ActionResult.Fail("can't use myself");
        var e = World.Entities.Get(entityId);
        if (e is null) return ActionResult.Fail($"entity {entityId} isn't around");
        if (e.Kind == EntityKind.Player) return ActionResult.Fail("right-clicking a player does nothing in b1.8.1");
        if (e.Kind is EntityKind.Item or EntityKind.XpOrb or EntityKind.Weather) return ActionResult.Fail($"{e.DisplayName} can't be used");
        if (itemId is { } want && World.Inventory.CountItem(want, damage) == 0) return ActionResult.Fail($"no {ItemName(want, damage ?? 0)}");
        await EnsureNoWindowAsync(ct).ConfigureAwait(false);

        for (int attempt = 0; ; attempt++)
        {
            e = World.Entities.Get(entityId);
            if (e is null) return ActionResult.Fail($"lost the entity {entityId}");
            var eye = World.Player.EyePosition;
            var c = EntityUsePoint(e);
            if (eye.DistanceTo(c) <= EntityUseReach && InSight(eye, c)) break;
            if (attempt >= 2) return ActionResult.Fail($"can't get within reach and sight of {e.DisplayName} at {Fmt(e.Position)}");
            await GotoCoreAsync(new GoalNearPosition(e.Position, 1.8), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), MaxReplans = 2 }, ct)
                .ConfigureAwait(false);
        }
        if (itemId is { } item && await SelectItemAsync(item, damage, ct).ConfigureAwait(false) < 0)
            return ActionResult.Fail($"couldn't hold {ItemName(item, damage ?? 0)}");
        var target = World.Entities.Get(entityId) ?? e;
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, EntityUsePoint(target));
        await LookCoreAsync(yaw, pitch, ct).ConfigureAwait(false);

        int heldSlot = 36 + World.Player.HeldSlot;
        var heldBefore = World.Inventory.GetPlayerSlot(heldSlot);
        var held = HeldStack();
        var metaBefore = World.Entities.Get(entityId)?.Metadata;
        long statusBefore = _entityStatusSeq.GetValueOrDefault(entityId);
        int vehicleBefore = _vehicleId;
        int itemsBefore = World.Entities.Nearby(target.Position, 3, x => x.Kind == EntityKind.Item).Count;
        Send(new UseEntityPacket { UserEntityId = World.Player.EntityId, TargetEntityId = entityId, LeftClick = false });

        string? effect = null;
        bool done = await WaitUntilAsync(() =>
        {
            if (_vehicleId != vehicleBefore) effect = _vehicleId == entityId ? "riding it" : "got out";
            else if (!Same(World.Inventory.GetPlayerSlot(heldSlot), heldBefore)) effect = "inventory changed";
            else if (_entityStatusSeq.GetValueOrDefault(entityId) != statusBefore) effect = "it reacted";
            else if (!ReferenceEquals(World.Entities.Get(entityId)?.Metadata, metaBefore)) effect = "its state changed";
            else if (World.Entities.Nearby(target.Position, 3, x => x.Kind == EntityKind.Item).Count > itemsBefore) effect = "dropped items";
            return effect is not null;
        }, TimeSpan.FromMilliseconds(1500), ct).ConfigureAwait(false);
        string what = held.IsEmpty ? "hand" : ItemName(held.Id, held.Damage);
        return done
            ? ActionResult.Success($"used {what} on {target.DisplayName}: {effect}")
            : ActionResult.Fail($"{what} on {target.DisplayName} had no effect");
    }

    /// <summary>Where to look at an entity to right-click it (a bit above its feet; boats/carts are low).</summary>
    private static Vec3d EntityUsePoint(Entity e) =>
        e.Position + new Vec3d(0, e.Kind == EntityKind.Object ? 0.3 : e.Kind == EntityKind.Mob ? 0.6 : 0.5, 0);

    private bool InSight(Vec3d eye, Vec3d point)
    {
        var hit = Raycast.Cast(World, eye, point - eye, eye.DistanceTo(point));
        return hit is null;
    }

    /// <summary>Gets out of the boat/minecart: sneak (0x13 crouch), falling back to right-clicking the vehicle again
    /// (b1.8.1 <c>mountEntity</c> toggles); verified by the 0x27 detach.</summary>
    public Task<ActionResult> DismountAsync(CancellationToken ct = default) =>
        RunActionAsync("dismount", ct, async c =>
        {
            int v = _vehicleId;
            if (v < 0) return ActionResult.Success("not riding anything");
            _idleSneak = true;
            bool off = await WaitUntilAsync(() => _vehicleId < 0, TimeSpan.FromMilliseconds(Options.TickInterval.TotalMilliseconds * 10 + 500), c)
                .ConfigureAwait(false);
            _idleSneak = false;
            await WaitTicksAsync(2, c).ConfigureAwait(false);
            if (!off)
            {
                Send(new UseEntityPacket { UserEntityId = World.Player.EntityId, TargetEntityId = v, LeftClick = false });
                off = await WaitUntilAsync(() => _vehicleId < 0, TimeSpan.FromMilliseconds(1500), c).ConfigureAwait(false);
            }
            return off ? ActionResult.Success($"got out at {Fmt(World.Player.Position)}") : ActionResult.Fail("still riding (the server ignored sneak and right-click)");
        });

    /// <summary>Tick thread, while riding: the server moves the body (it ignores our positions); hold physics, follow the
    /// vehicle's tracked position, keep sending the sneak state (sneaking dismounts) and the look.</summary>
    private bool HoldWhileRiding(Session s, PhysicsState phys, bool teleported)
    {
        if (_vehicleId < 0) return false;
        if (World.Entities.Get(_vehicleId) is { } v) phys.Position = v.Position + new Vec3d(0, 0.6, 0);
        phys.Velocity = Vec3d.Zero;
        phys.Sneaking = _idleSneak;
        phys.Sprinting = false;
        phys.Yaw = _lookYaw;
        phys.Pitch = _lookPitch;
        phys.Frozen = true;
        World.Player.SyncFromPhysics();
        if (!teleported) SendMovement(s, phys);
        return true;
    }

    // ------------------------------------------------------------------ signs

    /// <summary>Writes four lines on the sign at <paramref name="pos"/> (C→S 0x82, right after placing it): each line
    /// sanitized to the b1.8.1 allowed characters and cut to 15. Verified by the server echoing those lines.</summary>
    public Task<ActionResult> WriteSignAsync(BlockPos pos, string[] lines, CancellationToken ct = default) =>
        RunActionAsync("write_sign", ct, async c =>
        {
            int id = World.GetBlock(pos);
            if (id is not (Blocks.SignPost or Blocks.WallSign)) return ActionResult.Fail($"no sign at {Fmt(pos)}");
            var clean = SignLines(lines);
            Send(new UpdateSignPacket { X = pos.X, Y = (short)pos.Y, Z = pos.Z, Lines = clean });
            bool echoed = await WaitUntilAsync(() => _signs.TryGetValue(pos, out var l) && l.SequenceEqual(clean),
                TimeSpan.FromMilliseconds(2000), c).ConfigureAwait(false);
            if (!echoed)
            {
                if (_signs.TryGetValue(pos, out var now) && now.Contains("!?")) return ActionResult.Fail("the server rejected a line (\"!?\")");
                return ActionResult.Fail($"sign at {Fmt(pos)} didn't take the text (already written, or protected)");
            }
            return ActionResult.Success($"wrote sign at {Fmt(pos)}: \"{string.Join(" | ", clean.Where(l => l.Length > 0))}\"");
        });

    /// <summary>Four sign lines as the b1.8.1 server accepts them (NetServerHandler.handleUpdateSign: ≤ 15 chars, only
    /// ChatAllowedCharacters, otherwise the line becomes "!?").</summary>
    public static string[] SignLines(IReadOnlyList<string>? lines)
    {
        var r = new string[4];
        for (int i = 0; i < 4; i++)
        {
            var l = lines is not null && i < lines.Count ? ChatText.Sanitize(lines[i] ?? "") : "";
            l = l.Replace("§", "");
            r[i] = (l.Length > UpdateSignPacket.MaxLineLength ? l[..UpdateSignPacket.MaxLineLength] : l).TrimEnd();
        }
        return r;
    }

    // ------------------------------------------------------------------ bow

    /// <summary>0x0E status 5: release the item in use (bow shot; also ends eating early).</summary>
    public Task<ActionResult> ReleaseUseItemAsync(CancellationToken ct = default) =>
        RunActionAsync("release_use", ct, async c =>
        {
            Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusReleaseUseItem, X = 0, Y = 0, Z = 0, Face = 255 });
            await WaitTicksAsync(1, c).ConfigureAwait(false);
            return ActionResult.Success("released");
        });

    /// <summary>Draws the bow for <paramref name="chargeTicks"/> (20 = full power, speed 3.0), keeps the gravity-compensated
    /// aim on <paramref name="target"/> while drawing, then releases (0x0E status 5). Verified by an arrow leaving the
    /// inventory.</summary>
    public Task<ActionResult> ShootAsync(Vec3d target, int chargeTicks = ArrowBallistics.FullChargeTicks, CancellationToken ct = default) =>
        RunActionAsync("shoot", ct, c => ShootCoreAsync(() => target, null, $"{Fmt(target)}", chargeTicks, c));

    /// <summary><see cref="ShootAsync(Vec3d,int,CancellationToken)"/> at a tracked entity (its body center, led by its
    /// observed velocity). Refused for anything the combat gate won't attack (players without spar consent, pets...).</summary>
    public Task<ActionResult> ShootAsync(int entityId, int chargeTicks = ArrowBallistics.FullChargeTicks, CancellationToken ct = default) =>
        RunActionAsync("shoot", ct, c =>
        {
            var e = World.Entities.Get(entityId);
            if (e is null) return Task.FromResult(ActionResult.Fail($"entity {entityId} isn't around"));
            if (!Combat.MayAttack(e))
            {
                Interlocked.Increment(ref _refusedAttacks);
                return Task.FromResult(ActionResult.Fail($"won't shoot {e.DisplayName}"));
            }
            return ShootCoreAsync(() => World.Entities.Get(entityId) is { } x ? x.Position + new Vec3d(0, 0.9, 0) : null, entityId, e.DisplayName, chargeTicks, c);
        });

    private async Task<ActionResult> ShootCoreAsync(Func<Vec3d?> target, int? entityId, string what, int chargeTicks, CancellationToken ct)
    {
        if (World.Inventory.CountItem(Items.Bow) == 0) return ActionResult.Fail("no bow");
        int arrows = World.Inventory.CountItem(Items.Arrow);
        if (arrows == 0) return ActionResult.Fail("no arrows");
        chargeTicks = Math.Clamp(chargeTicks, 3, 60);
        double speed = ArrowBallistics.Speed(chargeTicks);
        if (target() is not { } t0) return ActionResult.Fail($"lost {what}");
        if (ArrowBallistics.Solve(World.Player.EyePosition, t0, speed) is not { } first)
            return ActionResult.Fail($"{what} is out of bow range ({World.Player.EyePosition.DistanceTo(t0):0} blocks, power {ArrowBallistics.Power(chargeTicks):0.00})");

        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        if (await SelectItemAsync(Items.Bow, null, ct).ConfigureAwait(false) < 0) return ActionResult.Fail("couldn't hold the bow");
        await LookCoreAsync(first.Yaw, first.Pitch, ct).ConfigureAwait(false);
        Send(PlayerBlockPlacementPacket.UseItem(HeldStack())); // ItemBow.onItemRightClick: setItemInUse (needs an arrow)

        // The server counts the draw in its own player ticks (one per movement packet): hold a tick longer.
        long start = TickCount;
        var p0 = t0;
        Vec3d vel = Vec3d.Zero;
        (float Yaw, float Pitch, int Ticks)? aim = first;
        while (TickCount < start + chargeTicks + 1)
        {
            if (HeldStack().Id != Items.Bow) return ActionResult.Fail("the bow left my hand while drawing");
            if (target() is not { } now)
            {
                Release();
                return ActionResult.Fail($"lost {what} while drawing");
            }
            long elapsed = Math.Max(1, TickCount - start);
            vel = entityId is null ? Vec3d.Zero : (now - p0) * (1.0 / elapsed);
            aim = ArrowBallistics.SolveLeading(World.Player.EyePosition, now, vel, speed) ?? aim;
            if (aim is { } a) { _lookYaw = a.Yaw; _lookPitch = Math.Clamp(a.Pitch, -90f, 90f); }
            await WaitTicksAsync(1, ct).ConfigureAwait(false);
        }
        if (target() is { } last && ArrowBallistics.SolveLeading(World.Player.EyePosition, last, vel, speed) is { } fin) aim = fin;
        await LookCoreAsync(aim!.Value.Yaw, aim.Value.Pitch, ct).ConfigureAwait(false); // the look goes out before the release
        Release();
        bool shot = await WaitUntilAsync(() => World.Inventory.CountItem(Items.Arrow) < arrows, TimeSpan.FromMilliseconds(1500), ct)
            .ConfigureAwait(false);
        return shot
            ? ActionResult.Success($"shot an arrow at {what} (power {ArrowBallistics.Power(chargeTicks):0.00}, pitch {aim.Value.Pitch:0.#}, {World.Inventory.CountItem(Items.Arrow)} arrows left)")
            : ActionResult.Fail("the bow didn't fire (no arrow used)");

        void Release() => Send(new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusReleaseUseItem, X = 0, Y = 0, Z = 0, Face = 255 });
    }

    // ------------------------------------------------------------------ dispatch hooks (reader thread)

    private void OnEntityStatusSeen(int entityId) => _entityStatusSeq[entityId] = Interlocked.Increment(ref _statusSeq);

    private void OnSelfAttached(int vehicleId)
    {
        int before = _vehicleId;
        _vehicleId = vehicleId;
        if (before != vehicleId) Log(vehicleId < 0 ? "left the vehicle" : $"riding entity {vehicleId}");
    }

    /// <summary>0x09 to another dimension: everything bound to the old world goes (the world/entity reset, chunk freeze
    /// and EdgeGuard re-arm happen in the respawn handler and the next 0x0D).</summary>
    private void OnDimensionChanged(int from, int to)
    {
        Log($"dimension change {from} -> {to}");
        AbortCurrentAction("dimension changed");
        _signs.Clear();
        IsRaining = false;
        _vehicleId = -1;
        _entityStatusSeq.Clear();
        _combatInstance?.RequestStop("dimension changed");
        Raise(() => DimensionChanged?.Invoke(from, to));
    }
}
