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
        var me = world.Player;
        switch (packet)
        {
            case LoginRequestPacket login when !string.IsNullOrEmpty(login.Username):
                SelfName ??= login.Username;
                break;
            case PlayerPositionLookPacket pl:
                me.ServerTeleport(new Vec3d(pl.X, pl.Y, pl.Z), pl.Stance, pl.Yaw, pl.Pitch, pl.OnGround);
                break;
            case PlayerPositionPacket pp:
                me.ServerTeleport(new Vec3d(pp.X, pp.Y, pp.Z), pp.Stance, me.Yaw, me.Pitch, pp.OnGround);
                break;
            case PlayerLookPacket lk:
                me.ServerTeleport(me.Position, me.Position.Y + PlayerPhysics.EyeHeight, lk.Yaw, lk.Pitch, lk.OnGround);
                break;
            case HoldingChangePacket hc:
                _heldSlot = hc.Slot;
                me.HeldSlot = hc.Slot;
                break;
            case PlayerDiggingPacket d when d.Status is PlayerDiggingPacket.StatusStarted or PlayerDiggingPacket.StatusFinished:
            {
                var pos = new BlockPos(d.X, d.Y, d.Z);
                world.SelfEdits.Note(pos);
                int id = world.GetBlock(pos);
                if (d.Status == PlayerDiggingPacket.StatusFinished && id != 0) Self?.Invoke("dug", Blocks.Get(id).Name, pos, id);
                break;
            }
            case PlayerBlockPlacementPacket b when b.Direction != PlayerBlockPlacementPacket.NoDirection:
            {
                var clicked = new BlockPos(b.X, b.Y, b.Z);
                int clickedId = world.GetBlock(clicked);
                if (clickedId != 0 && Blocks.Get(clickedId).Interactable)
                {
                    feed.LastOpenedContainer = clicked; // right-clicked a chest/furnace: the next 0x64 is that one
                    break;
                }
                var target = MinecraftClient.PlacementTarget(b.X, b.Y, b.Z, b.Direction);
                world.SelfEdits.Note(target);
                if (!b.HeldItem.IsEmpty && b.HeldItem.Id < 256) Self?.Invoke("placed", Blocks.Get(b.HeldItem.Id).Name, target, b.HeldItem.Id);
                break;
            }
            case ChatPacket chat:
                Self?.Invoke("said", chat.Message, null, null);
                break;
        }
    }

    // ------------------------------------------------------------------ server → client (mirrors MinecraftClient.Dispatch)

    private void ApplyServer(IPacket packet)
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
                break;
            case ChatPacket chat:
                events.Chat(chat.Message);
                break;
            case TimeUpdatePacket t: w.Time = t.Time; break;
            case SpawnPositionPacket sp: w.Spawn = new BlockPos(sp.X, sp.Y, sp.Z); break;
            case RespawnPacket rp:
                w.Clear();
                w.Entities.Clear();
                w.Inventory.Close(-1);
                w.Dimension = rp.Dimension;
                w.Player.Respawned(rp.Dimension);
                _wasDead = false;
                Self?.Invoke("respawned", "", null, null);
                break;
            case PlayerPositionLookPacket pos:
                w.Player.ServerTeleport(new Vec3d(pos.X, pos.Y, pos.Z), pos.Stance, pos.Yaw, pos.Pitch, pos.OnGround);
                _positionKnown = true;
                break;
            case UpdateHealthPacket h:
                w.Player.SetHealth(h.Health, h.Food, h.FoodSaturation);
                if (h.Health <= 0 && !_wasDead) { _wasDead = true; Self?.Invoke("died", "", w.Player.Position.ToBlockPos(), null); }
                else if (h.Health > 0) _wasDead = false;
                break;
            case PreChunkPacket pc:
                if (pc.Load) w.LoadColumn(pc.ChunkX, pc.ChunkZ);
                else w.UnloadColumn(pc.ChunkX, pc.ChunkZ);
                break;
            case MapChunkPacket mc:
            {
                var r = ChunkDataDecoder.Decode(mc);
                w.ApplyChunkRegion(r.X, r.Y, r.Z, r.SizeX, r.SizeY, r.SizeZ, r.BlockIds, r.Metadata, r.BlockLight, r.SkyLight);
                break;
            }
            case BlockChangePacket bc: w.SetBlock(bc.X, bc.Y, bc.Z, bc.BlockId, bc.Metadata); break;
            case MultiBlockChangePacket mb:
                foreach (var rec in mb.Records) w.SetBlock(rec.X, rec.Y, rec.Z, rec.BlockId, rec.Metadata);
                break;
            case ExplosionPacket ex:
                foreach (var (x, y, z) in ex.AffectedBlocks) w.SetBlock(x, y, z, 0, 0);
                break;
            case UpdateSignPacket sign:
            {
                var bp = new BlockPos(sign.X, sign.Y, sign.Z);
                feed.SignTexts[bp] = sign.Lines;
                events.Sign(bp, (string[])sign.Lines.Clone());
                break;
            }
            case NamedEntitySpawnPacket ne:
                w.Entities.SpawnPlayer(ne.EntityId, ne.PlayerName, Abs(ne.X, ne.Y, ne.Z), FixedPoint.FromAngle(ne.Yaw), FixedPoint.FromAngle(ne.Pitch), ne.CurrentItem);
                break;
            case PickupSpawnPacket ps:
                w.Entities.SpawnItem(ps.EntityId, new ItemStack(ps.ItemId, ps.Count, ps.Damage), Abs(ps.X, ps.Y, ps.Z),
                    new Vec3d(FixedPoint.FromPickupMotion(ps.MotionX), FixedPoint.FromPickupMotion(ps.MotionY), FixedPoint.FromPickupMotion(ps.MotionZ)));
                break;
            case AddObjectVehiclePacket ao:
                w.Entities.SpawnObject(ao.EntityId, ao.Type, Abs(ao.X, ao.Y, ao.Z),
                    ao.HasSpeed ? new Vec3d(FixedPoint.FromVelocity(ao.SpeedX), FixedPoint.FromVelocity(ao.SpeedY), FixedPoint.FromVelocity(ao.SpeedZ)) : default);
                break;
            case MobSpawnPacket ms:
                w.Entities.SpawnMob(ms.EntityId, ms.Type, Abs(ms.X, ms.Y, ms.Z), FixedPoint.FromAngle(ms.Yaw), FixedPoint.FromAngle(ms.Pitch), Dict(ms.Metadata));
                break;
            case EntityPaintingPacket pt: w.Entities.SpawnPainting(pt.EntityId, pt.Title, new Vec3d(pt.X + 0.5, pt.Y + 0.5, pt.Z + 0.5)); break;
            case ExperienceOrbPacket orb: w.Entities.SpawnXpOrb(orb.EntityId, Abs(orb.X, orb.Y, orb.Z), orb.Count); break;
            case EntityVelocityPacket ev when ev.EntityId != w.Player.EntityId:
                w.Entities.SetVelocity(ev.EntityId, new Vec3d(FixedPoint.FromVelocity(ev.VelocityX), FixedPoint.FromVelocity(ev.VelocityY), FixedPoint.FromVelocity(ev.VelocityZ)));
                break;
            case DestroyEntityPacket de: w.Entities.Destroy(de.EntityId); break;
            case EntityRelativeMovePacket rm:
                w.Entities.MoveRelative(rm.EntityId, FixedPoint.FromRelative(rm.DX), FixedPoint.FromRelative(rm.DY), FixedPoint.FromRelative(rm.DZ));
                break;
            case EntityLookPacket el: w.Entities.Look(el.EntityId, FixedPoint.FromAngle(el.Yaw), FixedPoint.FromAngle(el.Pitch)); break;
            case EntityLookRelativeMovePacket lrm:
                w.Entities.MoveRelative(lrm.EntityId, FixedPoint.FromRelative(lrm.DX), FixedPoint.FromRelative(lrm.DY), FixedPoint.FromRelative(lrm.DZ));
                w.Entities.Look(lrm.EntityId, FixedPoint.FromAngle(lrm.Yaw), FixedPoint.FromAngle(lrm.Pitch));
                break;
            case EntityTeleportPacket et:
                w.Entities.Teleport(et.EntityId, Abs(et.X, et.Y, et.Z), FixedPoint.FromAngle(et.Yaw), FixedPoint.FromAngle(et.Pitch));
                break;
            case EntityMetadataPacket md: w.Entities.SetMetadata(md.EntityId, Dict(md.Metadata)); break;
            case EntityEquipmentPacket eq:
                w.Entities.SetEquipment(eq.EntityId, eq.Slot, eq.ItemId < 0 ? ItemStack.Empty : new ItemStack(eq.ItemId, 1, eq.Damage));
                events.Equip(eq.EntityId, eq.Slot, eq.ItemId, eq.Damage);
                break;
            case AttachEntityPacket at:
                w.Entities.Attach(at.EntityId, at.VehicleId);
                events.Attach(at.EntityId, at.VehicleId);
                break;
            case CollectItemPacket ci: events.Collect(ci.CollectedEntityId, ci.CollectorEntityId); break;
            case EntityStatusPacket st: events.Status(st.EntityId, st.Status); break;
            case UseBedPacket ub: events.Bed(ub.EntityId, new BlockPos(ub.X, ub.Y, ub.Z)); break;
            case AnimationPacket an when an.EntityId != w.Player.EntityId:
                events.EntityAnimatedRaise(an.EntityId, an.Animation);
                break;
            case BlockActionPacket ba: events.Action(new BlockPos(ba.X, ba.Y, ba.Z), ba.Data1, ba.Data2); break;
            case PlayerListItemPacket pl:
                w.Entities.SetPlayerListEntry(pl.PlayerName, pl.Online);
                if (SelfName is { } me && pl.PlayerName.Equals(me, StringComparison.OrdinalIgnoreCase)) break;
                if (pl.Online ? _online.Add(pl.PlayerName) : _online.Remove(pl.PlayerName))
                {
                    if (pl.Online) { if (_positionKnown) events.Join(pl.PlayerName); }
                    else events.Leave(pl.PlayerName);
                }
                break;
            case OpenWindowPacket ow: w.Inventory.Open(ow.WindowId, (WindowType)ow.InventoryType, ow.Title, ow.SlotCount); break;
            case CloseWindowPacket cw: w.Inventory.Close(cw.WindowId); break;
            case SetSlotPacket ss: w.Inventory.SetSlot(ss.WindowId, ss.Slot, ss.Item); break;
            case WindowItemsPacket wi: w.Inventory.SetWindowItems(wi.WindowId, wi.Items); break;
            case UpdateProgressBarPacket pb: w.Inventory.SetProperty(pb.WindowId, pb.ProgressBar, pb.Value); break;
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
