using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private readonly ConcurrentDictionary<(int Window, short Action), bool> _txResults = new();
    private readonly ConcurrentDictionary<BlockPos, string[]> _signs = new();
    private int _lastHealth = -1, _lastFood = -1;
    private bool _wasDead, _lowHealthAlerted, _hungerAlerted;
    private int _windowItemsVersion;
    private int _eatAcceptedCount;
    private int _collectedCount;

    /// <summary>Sign texts seen (position → 4 lines).</summary>
    public IReadOnlyDictionary<BlockPos, string[]> Signs => _signs;

    /// <summary>Applies one server packet to the world state. Runs on the reader thread.</summary>
    private void Dispatch(Session s, IPacket packet)
    {
        var w = World;
        switch (packet)
        {
            // ---- connection ----
            case KeepAlivePacket ka:
                Send(s, new KeepAlivePacket(ka.KeepAliveId));
                break;
            case HandshakePacket hs:
                if (!hs.IsOfflineMode)
                {
                    LastKickReason = "server is in online mode";
                    EndSession(s, "server requires online-mode authentication (hash " + hs.ConnectionHash + ")", sendQuit: true);
                    return;
                }
                Send(s, LoginRequestPacket.ForClient(Options.Username));
                break;
            case LoginRequestPacket login:
                w.Player.EntityId = login.EntityId;
                w.Entities.SelfId = login.EntityId;
                w.Seed = login.MapSeed;
                w.Dimension = login.Dimension;
                w.MaxPlayers = login.MaxPlayers;
                w.Player.GameMode = login.ServerMode;
                w.Player.Dimension = login.Dimension;
                Difficulty = login.Difficulty;
                Log($"logged in: entity {login.EntityId}, dim {login.Dimension}, mode {login.ServerMode}");
                break;
            case KickPacket kick:
                LastKickReason = kick.Reason;
                Log($"kicked: {kick.Reason}");
                Raise(() => Kicked?.Invoke(kick.Reason));
                EndSession(s, "kicked: " + kick.Reason);
                return;
            case ChatPacket chat:
                Raise(() => ChatReceived?.Invoke(chat.Message));
                break;

            // ---- world-level ----
            case TimeUpdatePacket t:
                w.Time = t.Time;
                break;
            case SpawnPositionPacket sp:
                w.Spawn = new BlockPos(sp.X, sp.Y, sp.Z);
                break;
            case RespawnPacket rp:
                w.Clear();
                w.Entities.Clear();
                w.Inventory.Close(-1);
                w.Dimension = rp.Dimension;
                w.Player.Respawned(rp.Dimension);
                w.Player.GameMode = rp.GameMode;
                s.AwaitingTeleport = true;
                _wasDead = false;
                Raise(() => Respawned?.Invoke());
                break;
            case NewStatePacket ns:
                if (ns.Reason == NewStatePacket.ReasonGameModeChanged) w.Player.GameMode = ns.GameMode;
                else if (ns.Reason == NewStatePacket.ReasonBeginRain) IsRaining = true;
                else if (ns.Reason == NewStatePacket.ReasonEndRain) IsRaining = false;
                break;

            // ---- own player ----
            case PlayerPositionLookPacket pos:
                s.Teleports.Enqueue(pos);
                break;
            case PlayerPositionPacket:
            case PlayerLookPacket:
            case PlayerOnGroundPacket:
                break; // not sent by the b1.8 server
            case UpdateHealthPacket h:
                OnHealth(s, h.Health, h.Food, h.FoodSaturation);
                break;
            case ExperiencePacket xp:
                w.Player.SetExperience(xp.Experience, xp.Level, xp.TotalExperience);
                break;

            // ---- chunks & blocks ----
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
            case BlockChangePacket bc:
                w.SetBlock(bc.X, bc.Y, bc.Z, bc.BlockId, bc.Metadata);
                break;
            case MultiBlockChangePacket mb:
                foreach (var rec in mb.Records) w.SetBlock(rec.X, rec.Y, rec.Z, rec.BlockId, rec.Metadata);
                break;
            case ExplosionPacket ex:
                foreach (var (x, y, z) in ex.AffectedBlocks) w.SetBlock(x, y, z, 0, 0);
                break;
            case UpdateSignPacket sign:
                _signs[new BlockPos(sign.X, sign.Y, sign.Z)] = sign.Lines;
                break;

            // ---- entities ----
            case NamedEntitySpawnPacket ne:
                w.Entities.SpawnPlayer(ne.EntityId, ne.PlayerName, Abs(ne.X, ne.Y, ne.Z),
                    FixedPoint.FromAngle(ne.Yaw), FixedPoint.FromAngle(ne.Pitch), ne.CurrentItem);
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
                w.Entities.SpawnMob(ms.EntityId, ms.Type, Abs(ms.X, ms.Y, ms.Z), FixedPoint.FromAngle(ms.Yaw),
                    FixedPoint.FromAngle(ms.Pitch), ToDictionary(ms.Metadata));
                break;
            case EntityPaintingPacket pt:
                w.Entities.SpawnPainting(pt.EntityId, pt.Title, new Vec3d(pt.X + 0.5, pt.Y + 0.5, pt.Z + 0.5));
                break;
            case ExperienceOrbPacket orb:
                w.Entities.SpawnXpOrb(orb.EntityId, Abs(orb.X, orb.Y, orb.Z), orb.Count);
                break;
            case ThunderboltPacket tb:
                w.Entities.Spawn(new Entity(tb.EntityId, EntityKind.Weather, Abs(tb.X, tb.Y, tb.Z)));
                break;
            case EntityVelocityPacket ev:
            {
                var vel = new Vec3d(FixedPoint.FromVelocity(ev.VelocityX), FixedPoint.FromVelocity(ev.VelocityY), FixedPoint.FromVelocity(ev.VelocityZ));
                // Vanilla b1.8.1 (NetClientHandler.handleEntityVelocity → getEntityByID returns thePlayer for our own id)
                // applies this to the local player too: knockback from mobs, arrows, explosions. Handed to the tick thread.
                if (ev.EntityId == w.Player.EntityId) _selfVelocity.Enqueue(vel);
                else w.Entities.SetVelocity(ev.EntityId, vel);
                break;
            }
            case DestroyEntityPacket de:
                w.Entities.Destroy(de.EntityId);
                break;
            case EntityRelativeMovePacket rm:
                w.Entities.MoveRelative(rm.EntityId, FixedPoint.FromRelative(rm.DX), FixedPoint.FromRelative(rm.DY), FixedPoint.FromRelative(rm.DZ));
                break;
            case EntityLookPacket el:
                w.Entities.Look(el.EntityId, FixedPoint.FromAngle(el.Yaw), FixedPoint.FromAngle(el.Pitch));
                break;
            case EntityLookRelativeMovePacket lrm:
                w.Entities.MoveRelative(lrm.EntityId, FixedPoint.FromRelative(lrm.DX), FixedPoint.FromRelative(lrm.DY), FixedPoint.FromRelative(lrm.DZ));
                w.Entities.Look(lrm.EntityId, FixedPoint.FromAngle(lrm.Yaw), FixedPoint.FromAngle(lrm.Pitch));
                break;
            case EntityTeleportPacket et:
                w.Entities.Teleport(et.EntityId, Abs(et.X, et.Y, et.Z), FixedPoint.FromAngle(et.Yaw), FixedPoint.FromAngle(et.Pitch));
                break;
            case EntityMetadataPacket md:
                w.Entities.SetMetadata(md.EntityId, ToDictionary(md.Metadata));
                break;
            case EntityEquipmentPacket eq:
                if (eq.Slot == 0) w.Entities.SetHeldItem(eq.EntityId, eq.ItemId < 0 ? 0 : eq.ItemId);
                break;
            case AttachEntityPacket at:
                w.Entities.Attach(at.EntityId, at.VehicleId);
                break;
            case CollectItemPacket ci:
                if (ci.CollectorEntityId == w.Player.EntityId) Interlocked.Increment(ref _collectedCount);
                break;
            case EntityStatusPacket st:
                if (st.EntityId == w.Player.EntityId && st.Status == 9) Interlocked.Increment(ref _eatAcceptedCount);
                break;
            case AnimationPacket an:
                if (an.EntityId != w.Player.EntityId)
                {
                    int eid = an.EntityId, anim = an.Animation;
                    Raise(() => EntityAnimated?.Invoke(eid, anim));
                }
                break;
            case BlockActionPacket ba:
                {
                    var bp = new BlockPos(ba.X, ba.Y, ba.Z);
                    int d1 = ba.Data1, d2 = ba.Data2;
                    Raise(() => BlockAction?.Invoke(bp, d1, d2));
                }
                break;
            case PlayerListItemPacket pl:
                OnPlayerList(s, pl);
                break;

            // ---- windows ----
            case OpenWindowPacket ow:
                w.Inventory.Open(ow.WindowId, (WindowType)ow.InventoryType, ow.Title, ow.SlotCount);
                break;
            case CloseWindowPacket cw:
                w.Inventory.Close(cw.WindowId);
                break;
            case SetSlotPacket ss:
                w.Inventory.SetSlot(ss.WindowId, ss.Slot, ss.Item);
                break;
            case WindowItemsPacket wi:
                w.Inventory.SetWindowItems(wi.WindowId, wi.Items);
                Interlocked.Increment(ref _windowItemsVersion);
                break;
            case UpdateProgressBarPacket pb:
                w.Inventory.SetProperty(pb.WindowId, pb.ProgressBar, pb.Value);
                break;
            case TransactionPacket tx:
                if (!tx.Accepted)
                {
                    // Vanilla: acknowledge so the server unlocks the window; it resyncs with 0x68 + cursor 0x67.
                    Send(s, new TransactionPacket { WindowId = tx.WindowId, ActionNumber = tx.ActionNumber, Accepted = true });
                    Log($"click {tx.ActionNumber} in window {tx.WindowId} rejected; resyncing");
                }
                _txResults[(tx.WindowId, tx.ActionNumber)] = tx.Accepted;
                break;
        }
    }

    public bool IsRaining { get; private set; }

    private static Vec3d Abs(int x, int y, int z) =>
        new(FixedPoint.FromAbsolute(x), FixedPoint.FromAbsolute(y), FixedPoint.FromAbsolute(z));

    private static Dictionary<int, object> ToDictionary(EntityMetadata md)
    {
        var d = new Dictionary<int, object>();
        foreach (var e in md) d[e.Index] = e.Value;
        return d;
    }

    private void OnHealth(Session s, int health, int food, float saturation)
    {
        var player = World.Player;
        player.SetHealth(health, food, saturation);
        int oldHealth = _lastHealth, oldFood = _lastFood;
        _lastHealth = health;
        _lastFood = food;
        if (health != oldHealth || food != oldFood) Raise(() => HealthChanged?.Invoke(health, food));

        if (health <= 0)
        {
            if (!_wasDead)
            {
                _wasDead = true;
                Log("died");
                Raise(() => Died?.Invoke());
                AbortCurrentAction("died");
                if (Options.AutoRespawn) _ = RespawnLaterAsync(s);
            }
            return;
        }
        _wasDead = false;

        if (oldHealth > 0 && health < oldHealth)
            RaiseDanger(DangerKind.Damage, $"took {oldHealth - health} damage (hp {health}/20)", abort: false);
        if (health <= Options.LowHealthThreshold)
        {
            if (!_lowHealthAlerted)
            {
                _lowHealthAlerted = true;
                RaiseDanger(DangerKind.LowHealth, $"low health {health}/20", abort: true);
            }
        }
        else if (health > Options.LowHealthThreshold + 2) _lowHealthAlerted = false;

        if (food <= 6)
        {
            if (!_hungerAlerted)
            {
                _hungerAlerted = true;
                RaiseDanger(DangerKind.Hunger, $"hungry (food {food}/20)", abort: false);
            }
        }
        else if (food > 8) _hungerAlerted = false;
    }

    private async Task RespawnLaterAsync(Session s)
    {
        try
        {
            await Task.Delay(Options.RespawnDelay, s.Cts.Token).ConfigureAwait(false);
            if (!World.Player.IsDead || s.Ended) return;
            Log("respawning");
            Send(s, new RespawnPacket
            {
                Dimension = (sbyte)World.Dimension,
                Difficulty = (sbyte)Difficulty,
                GameMode = (sbyte)World.Player.GameMode,
                WorldHeight = 128,
                MapSeed = World.Seed,
            });
        }
        catch (OperationCanceledException) { /* disconnected */ }
    }

    /// <summary>Sends a respawn request now (when <see cref="ClientOptions.AutoRespawn"/> is off).</summary>
    public ActionResult Respawn()
    {
        var s = _session;
        if (s is null || s.Ended) return ActionResult.Fail("not connected");
        if (!World.Player.IsDead) return ActionResult.Fail("not dead");
        Send(s, new RespawnPacket
        {
            Dimension = (sbyte)World.Dimension, Difficulty = (sbyte)Difficulty, GameMode = (sbyte)World.Player.GameMode,
            WorldHeight = 128, MapSeed = World.Seed,
        });
        return ActionResult.Success("respawn requested");
    }

    private void OnPlayerList(Session s, PlayerListItemPacket pl)
    {
        World.Entities.SetPlayerListEntry(pl.PlayerName, pl.Online);
        if (string.Equals(pl.PlayerName, Options.Username, StringComparison.OrdinalIgnoreCase)) return;
        bool changed;
        lock (s.Online) changed = pl.Online ? s.Online.Add(pl.PlayerName) : s.Online.Remove(pl.PlayerName);
        if (!changed) return;
        // The initial list on login lists everyone already online: report those as joined too only after spawn.
        if (pl.Online)
        {
            if (s.PositionKnown) Raise(() => PlayerJoined?.Invoke(pl.PlayerName));
        }
        else Raise(() => PlayerLeft?.Invoke(pl.PlayerName));
    }
}
