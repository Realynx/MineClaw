using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Recording;
using MineClaw.World;

namespace MineClaw.Replay;

/// <summary>
/// Applies recorded packets to a fresh <see cref="WorldState"/> the way <c>MinecraftClient.Dispatch</c> does (world,
/// entities, inventory/windows, signs), raising the per-entity signals on a <see cref="ManualPlayerEvents"/> so the
/// indexer and activity tracker see exactly what they'd see live. Outbound packets update the bot's own position and
/// mark its digs/placements in <see cref="WorldState.SelfEdits"/>. Never sends anything.
/// </summary>
public sealed class WorldApplier(WorldState world, ManualPlayerEvents events, ManualIndexFeed feed)
{
    private readonly HashSet<string> _online = new(StringComparer.OrdinalIgnoreCase);
    private bool _positionKnown;
    private bool _wasDead;
    private int _heldSlot;

    public string? SelfName { get; set; }

    /// <summary>The bot's own notable outbound actions and state (dug, placed, died, respawned, said).</summary>
    public event Action<string, string, BlockPos?, int?>? Self;

    public void Apply(RecordedFrame f)
    {
        IPacket p;
        try { p = f.Parse(); }
        catch (ProtocolException) { return; }
        if (f.Direction == PacketDirection.ClientBound) ApplyServer(p);
        else ApplyClient(p);
    }

    // ------------------------------------------------------------------ client → server (the bot itself)

    private void ApplyClient(IPacket packet)
    {
        if (ApplySelfMove(packet)) return;
        var me = world.Player;
        switch (packet)
        {
            case LoginRequestPacket login when !string.IsNullOrEmpty(login.Username):
                SelfName ??= login.Username;
                break;
            case HoldingChangePacket hc:
                _heldSlot = hc.Slot;
                me.HeldSlot = hc.Slot;
                break;
            case PlayerDiggingPacket d when d.Status is PlayerDiggingPacket.StatusStarted or PlayerDiggingPacket.StatusFinished:
                OnDig(d);
                break;
            case PlayerBlockPlacementPacket b when b.Direction != PlayerBlockPlacementPacket.NoDirection:
                OnPlace(b);
                break;
            case ChatPacket chat:
                Self?.Invoke("said", chat.Message, null, null);
                break;
        }
    }

    /// <summary>The bot's own position/look packets (the only client packets that move it).</summary>
    private bool ApplySelfMove(IPacket packet)
    {
        var me = world.Player;
        switch (packet)
        {
            case PlayerPositionLookPacket pl:
                me.ServerTeleport(new Vec3d(pl.X, pl.Y, pl.Z), pl.Stance, pl.Yaw, pl.Pitch, pl.OnGround);
                return true;
            case PlayerPositionPacket pp:
                me.ServerTeleport(new Vec3d(pp.X, pp.Y, pp.Z), pp.Stance, me.Yaw, me.Pitch, pp.OnGround);
                return true;
            case PlayerLookPacket lk:
                me.ServerTeleport(me.Position, me.Position.Y + PlayerPhysics.EyeHeight, lk.Yaw, lk.Pitch, lk.OnGround);
                return true;
            default: return false;
        }
    }

    private void OnDig(PlayerDiggingPacket d)
    {
        var pos = new BlockPos(d.X, d.Y, d.Z);
        world.SelfEdits.Note(pos);
        int id = world.GetBlock(pos);
        if (d.Status == PlayerDiggingPacket.StatusFinished && id != 0) Self?.Invoke("dug", Blocks.Get(id).Name, pos, id);
    }

    private void OnPlace(PlayerBlockPlacementPacket b)
    {
        var clicked = new BlockPos(b.X, b.Y, b.Z);
        int clickedId = world.GetBlock(clicked);
        if (clickedId != 0 && Blocks.Get(clickedId).Interactable)
        {
            feed.LastOpenedContainer = clicked; // right-clicked a chest/furnace: the next 0x64 is that one
            return;
        }
        var target = MinecraftClient.PlacementTarget(b.X, b.Y, b.Z, b.Direction);
        world.SelfEdits.Note(target);
        if (!b.HeldItem.IsEmpty && b.HeldItem.Id < 256) Self?.Invoke("placed", Blocks.Get(b.HeldItem.Id).Name, target, b.HeldItem.Id);
    }

    // ------------------------------------------------------------------ server → client (mirrors MinecraftClient.Dispatch)

    /// <summary>Each packet type belongs to exactly one group, so trying the groups in turn is the original single switch.</summary>
    private void ApplyServer(IPacket packet)
    {
        _ = ApplySession(packet) || ApplyBlocks(packet) || ApplySpawns(packet) || ApplyEntityUpdates(packet) || ApplyEntityEvents(packet)
            || ApplyWindows(packet);
    }

    private bool ApplySession(IPacket packet)
    {
        var w = world;
        switch (packet)
        {
            case LoginRequestPacket login:
                w.Player.EntityId = login.EntityId;
                w.Entities.SelfId = login.EntityId;
                w.Seed = login.MapSeed;
                w.Dimension = login.Dimension;
                w.Player.Dimension = login.Dimension;
                return true;
            case ChatPacket chat: events.Chat(chat.Message); return true;
            case TimeUpdatePacket t: w.Time = t.Time; return true;
            case SpawnPositionPacket sp: w.Spawn = new BlockPos(sp.X, sp.Y, sp.Z); return true;
            case RespawnPacket rp: OnRespawn(rp); return true;
            case PlayerPositionLookPacket pos:
                w.Player.ServerTeleport(new Vec3d(pos.X, pos.Y, pos.Z), pos.Stance, pos.Yaw, pos.Pitch, pos.OnGround);
                _positionKnown = true;
                return true;
            case UpdateHealthPacket h: OnHealth(h); return true;
            case PlayerListItemPacket pl: OnPlayerList(pl); return true;
            default: return false;
        }
    }

    private void OnRespawn(RespawnPacket rp)
    {
        var w = world;
        w.Clear();
        w.Entities.Clear();
        w.Inventory.Close(-1);
        w.Dimension = rp.Dimension;
        w.Player.Respawned(rp.Dimension);
        _wasDead = false;
        Self?.Invoke("respawned", "", null, null);
    }

    private void OnHealth(UpdateHealthPacket h)
    {
        var w = world;
        w.Player.SetHealth(h.Health, h.Food, h.FoodSaturation);
        if (h.Health <= 0 && !_wasDead) { _wasDead = true; Self?.Invoke("died", "", w.Player.Position.ToBlockPos(), null); }
        else if (h.Health > 0) _wasDead = false;
    }

    private void OnPlayerList(PlayerListItemPacket pl)
    {
        world.Entities.SetPlayerListEntry(pl.PlayerName, pl.Online);
        if (SelfName is { } me && pl.PlayerName.Equals(me, StringComparison.OrdinalIgnoreCase)) return;
        if (!(pl.Online ? _online.Add(pl.PlayerName) : _online.Remove(pl.PlayerName))) return;
        if (!pl.Online) events.Leave(pl.PlayerName);
        else if (_positionKnown) events.Join(pl.PlayerName);
    }

    private bool ApplyBlocks(IPacket packet)
    {
        var w = world;
        switch (packet)
        {
            case PreChunkPacket pc:
                if (pc.Load) w.LoadColumn(pc.ChunkX, pc.ChunkZ);
                else w.UnloadColumn(pc.ChunkX, pc.ChunkZ);
                return true;
            case MapChunkPacket mc:
            {
                var r = ChunkDataDecoder.Decode(mc);
                w.ApplyChunkRegion(r.X, r.Y, r.Z, r.SizeX, r.SizeY, r.SizeZ, r.BlockIds, r.Metadata, r.BlockLight, r.SkyLight);
                return true;
            }
            case BlockChangePacket bc: w.SetBlock(bc.X, bc.Y, bc.Z, bc.BlockId, bc.Metadata); return true;
            case MultiBlockChangePacket mb:
                foreach (var rec in mb.Records) w.SetBlock(rec.X, rec.Y, rec.Z, rec.BlockId, rec.Metadata);
                return true;
            case ExplosionPacket ex:
                foreach (var (x, y, z) in ex.AffectedBlocks) w.SetBlock(x, y, z, 0, 0);
                return true;
            case UpdateSignPacket sign:
            {
                var bp = new BlockPos(sign.X, sign.Y, sign.Z);
                feed.SignTexts[bp] = sign.Lines;
                events.Sign(bp, (string[])sign.Lines.Clone());
                return true;
            }
            default: return false;
        }
    }

    private bool ApplySpawns(IPacket packet)
    {
        var e = world.Entities;
        switch (packet)
        {
            case NamedEntitySpawnPacket ne:
                e.SpawnPlayer(ne.EntityId, ne.PlayerName, Abs(ne.X, ne.Y, ne.Z), FixedPoint.FromAngle(ne.Yaw), FixedPoint.FromAngle(ne.Pitch), ne.CurrentItem);
                return true;
            case PickupSpawnPacket ps:
                e.SpawnItem(ps.EntityId, new ItemStack(ps.ItemId, ps.Count, ps.Damage), Abs(ps.X, ps.Y, ps.Z),
                    new Vec3d(FixedPoint.FromPickupMotion(ps.MotionX), FixedPoint.FromPickupMotion(ps.MotionY), FixedPoint.FromPickupMotion(ps.MotionZ)));
                return true;
            case AddObjectVehiclePacket ao:
                e.SpawnObject(ao.EntityId, ao.Type, Abs(ao.X, ao.Y, ao.Z),
                    ao.HasSpeed ? new Vec3d(FixedPoint.FromVelocity(ao.SpeedX), FixedPoint.FromVelocity(ao.SpeedY), FixedPoint.FromVelocity(ao.SpeedZ)) : default);
                return true;
            case MobSpawnPacket ms:
                e.SpawnMob(ms.EntityId, ms.Type, Abs(ms.X, ms.Y, ms.Z), FixedPoint.FromAngle(ms.Yaw), FixedPoint.FromAngle(ms.Pitch), Dict(ms.Metadata));
                return true;
            case EntityPaintingPacket pt: e.SpawnPainting(pt.EntityId, pt.Title, new Vec3d(pt.X + 0.5, pt.Y + 0.5, pt.Z + 0.5)); return true;
            case ExperienceOrbPacket orb: e.SpawnXpOrb(orb.EntityId, Abs(orb.X, orb.Y, orb.Z), orb.Count); return true;
            case DestroyEntityPacket de: e.Destroy(de.EntityId); return true;
            default: return false;
        }
    }

    private bool ApplyEntityUpdates(IPacket packet)
    {
        var e = world.Entities;
        switch (packet)
        {
            case EntityVelocityPacket ev when ev.EntityId != world.Player.EntityId:
                e.SetVelocity(ev.EntityId, new Vec3d(FixedPoint.FromVelocity(ev.VelocityX), FixedPoint.FromVelocity(ev.VelocityY), FixedPoint.FromVelocity(ev.VelocityZ)));
                return true;
            case EntityRelativeMovePacket rm:
                e.MoveRelative(rm.EntityId, FixedPoint.FromRelative(rm.DX), FixedPoint.FromRelative(rm.DY), FixedPoint.FromRelative(rm.DZ));
                return true;
            case EntityLookPacket el: e.Look(el.EntityId, FixedPoint.FromAngle(el.Yaw), FixedPoint.FromAngle(el.Pitch)); return true;
            case EntityLookRelativeMovePacket lrm:
                e.MoveRelative(lrm.EntityId, FixedPoint.FromRelative(lrm.DX), FixedPoint.FromRelative(lrm.DY), FixedPoint.FromRelative(lrm.DZ));
                e.Look(lrm.EntityId, FixedPoint.FromAngle(lrm.Yaw), FixedPoint.FromAngle(lrm.Pitch));
                return true;
            case EntityTeleportPacket et:
                e.Teleport(et.EntityId, Abs(et.X, et.Y, et.Z), FixedPoint.FromAngle(et.Yaw), FixedPoint.FromAngle(et.Pitch));
                return true;
            case EntityMetadataPacket md: e.SetMetadata(md.EntityId, Dict(md.Metadata)); return true;
            case EntityEquipmentPacket eq:
                e.SetEquipment(eq.EntityId, eq.Slot, eq.ItemId < 0 ? ItemStack.Empty : new ItemStack(eq.ItemId, 1, eq.Damage));
                events.Equip(eq.EntityId, eq.Slot, eq.ItemId, eq.Damage);
                return true;
            default: return false;
        }
    }

    private bool ApplyEntityEvents(IPacket packet)
    {
        switch (packet)
        {
            case AttachEntityPacket at:
                world.Entities.Attach(at.EntityId, at.VehicleId);
                events.Attach(at.EntityId, at.VehicleId);
                return true;
            case CollectItemPacket ci: events.Collect(ci.CollectedEntityId, ci.CollectorEntityId); return true;
            case EntityStatusPacket st: events.Status(st.EntityId, st.Status); return true;
            case UseBedPacket ub: events.Bed(ub.EntityId, new BlockPos(ub.X, ub.Y, ub.Z)); return true;
            case AnimationPacket an when an.EntityId != world.Player.EntityId: events.EntityAnimatedRaise(an.EntityId, an.Animation); return true;
            case BlockActionPacket ba: events.Action(new BlockPos(ba.X, ba.Y, ba.Z), ba.Data1, ba.Data2); return true;
            default: return false;
        }
    }

    private bool ApplyWindows(IPacket packet)
    {
        var inv = world.Inventory;
        switch (packet)
        {
            case OpenWindowPacket ow: inv.Open(ow.WindowId, (WindowType)ow.InventoryType, ow.Title, ow.SlotCount); return true;
            case CloseWindowPacket cw: inv.Close(cw.WindowId); return true;
            case SetSlotPacket ss: inv.SetSlot(ss.WindowId, ss.Slot, ss.Item); return true;
            case WindowItemsPacket wi: inv.SetWindowItems(wi.WindowId, wi.Items); return true;
            case UpdateProgressBarPacket pb: inv.SetProperty(pb.WindowId, pb.ProgressBar, pb.Value); return true;
            default: return false;
        }
    }

    private static Vec3d Abs(int x, int y, int z) => new(FixedPoint.FromAbsolute(x), FixedPoint.FromAbsolute(y), FixedPoint.FromAbsolute(z));

    private static Dictionary<int, object> Dict(EntityMetadata md)
    {
        var d = new Dictionary<int, object>();
        foreach (var e in md) d[e.Index] = e.Value;
        return d;
    }
}

internal static class ManualPlayerEventsExtensions
{
    /// <summary>Any animation id (1 swing, 2 hurt...) through the manual source.</summary>
    public static void EntityAnimatedRaise(this ManualPlayerEvents e, int entity, int anim)
    {
        if (anim == 2) e.Hurt(entity);
        else if (anim == 1) e.Swing(entity);
    }
}
