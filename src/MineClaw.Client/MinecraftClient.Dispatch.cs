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
    private bool _wasDead;
    private int _windowItemsVersion;

    /// <summary>
    /// Raised on the network thread right BEFORE a 0x68 (full window contents from the server) replaces the local
    /// inventory model. Any count change it causes is the server correcting a wrong prediction, not a real gain/loss
    /// (the Bot's inventory ledger records it as such). Handlers must be quick and must not touch the inventory.
    /// </summary>
    public event Action<int>? WindowItemsArriving;
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
            case ChatPacket { Position: ChatPacket.PositionGameInfo }:
                break; // 1.12.2 action-bar text: not chat
            case ChatPacket chat:
                Raise(() => ChatReceived?.Invoke(chat.Message));
                if (ChatText.TryParsePlayerChat(chat.Message, out var chatFrom, out var chatText))
                {
                    Combat.OnChat(chatFrom, chatText);
                    NoteChatForParkour(chatFrom);
                    OnChatForBody(chatFrom, chatText);
                }
                break;

            // ---- world-level ----
            case TimeUpdatePacket t:
                w.Time = t.Time;
                break;
            case SpawnPositionPacket sp:
                w.Spawn = new BlockPos(sp.X, sp.Y, sp.Z);
                break;
            case RespawnPacket rp:
            {
                int fromDim = w.Dimension;
                w.Clear();
                w.Entities.Clear();
                w.Inventory.Close(-1);
                w.Dimension = rp.Dimension;
                w.Player.Respawned(rp.Dimension);
                w.Player.GameMode = rp.GameMode;
                s.AwaitingTeleport = true;
                _wasDead = false;
                _vehicleId = -1;
                StartSpawnSafety();
                Raise(() => Respawned?.Invoke());
                if (rp.Dimension != fromDim) OnDimensionChanged(fromDim, rp.Dimension);
                break;
            }
            case NewStatePacket ns:
                if (ns.Reason == NewStatePacket.ReasonGameModeChanged) w.Player.GameMode = ns.GameMode;
                else if (ns.Reason == NewStatePacket.ReasonBeginRain) { IsRaining = true; w.Rain = 1f; } // light: World.k
                else if (ns.Reason == NewStatePacket.ReasonEndRain) { IsRaining = false; w.Rain = 0f; w.Thunder = 0f; }
                // 1.12.2 fades: the strengths the sky, the light and the viewer's rain follow (the flag above stays the server's word).
                else if (ns.Reason == NewStatePacket.ReasonRainLevel) w.Rain = Math.Clamp(ns.Value, 0f, 1f);
                else if (ns.Reason == NewStatePacket.ReasonThunderLevel) w.Thunder = Math.Clamp(ns.Value, 0f, 1f);
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
                w.Player.HealthExact = h.HealthExact ?? float.NaN;
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
            case ChunkSectionsMessage cs:
                // Newer versions' chunk data, decoded by the connection (1.12.2: paletted 16³ sections, 256 high).
                foreach (var r in cs.Regions)
                    w.ApplyChunkRegion(r.X, r.Y, r.Z, r.SizeX, r.SizeY, r.SizeZ, r.BlockIds, r.Metadata, r.BlockLight, cs.HasSkyLight ? r.SkyLight : null);
                // The full chunk's biome array (tints, spawn rules: mushroom islands spawn no monsters).
                if (cs.Biomes != null) w.SetBiomes(cs.ChunkX, cs.ChunkZ, cs.Biomes);
                break;
            case BlockChangePacket bc:
                w.SetBlock(bc.X, bc.Y, bc.Z, bc.BlockId, bc.Metadata);
                break;
            case MultiBlockChangePacket mb:
                foreach (var rec in mb.Records) w.SetBlock(rec.X, rec.Y, rec.Z, rec.BlockId, rec.Metadata);
                break;
            case ExplosionPacket ex:
                w.Blasts.Note(new Vec3d(ex.X, ex.Y, ex.Z), ex.Radius); // first: the changes below are the blast's
                foreach (var (x, y, z) in ex.AffectedBlocks) w.SetBlock(x, y, z, 0, 0);
                break;
            case UpdateSignPacket sign:
                _signs[new BlockPos(sign.X, sign.Y, sign.Z)] = sign.Lines;
                w.BlockEntities.Set(new BlockPos(sign.X, sign.Y, sign.Z), BlockEntityInfo.Sign(sign.Lines, sign.Legacy)); // the viewer's sign text
                { int sx = sign.X, sy = sign.Y, sz = sign.Z; var lines = (string[])sign.Lines.Clone(); Raise(() => SignUpdated?.Invoke(sx, sy, sz, lines)); }
                break;

            // ---- entities ----
            case NamedEntitySpawnPacket ne:
                w.Entities.SpawnPlayer(ne.EntityId, ne.PlayerName, Pos(ne.Exact, ne.X, ne.Y, ne.Z),
                    FixedPoint.FromAngle(ne.Yaw), FixedPoint.FromAngle(ne.Pitch), ne.CurrentItem);
                break;
            case PickupSpawnPacket ps:
                w.Entities.SpawnItem(ps.EntityId, new ItemStack(ps.ItemId, ps.Count, ps.Damage), Pos(ps.Exact, ps.X, ps.Y, ps.Z),
                    new Vec3d(FixedPoint.FromPickupMotion(ps.MotionX), FixedPoint.FromPickupMotion(ps.MotionY), FixedPoint.FromPickupMotion(ps.MotionZ)));
                break;
            case AddObjectVehiclePacket ao:
                w.Entities.SpawnObject(ao.EntityId, ao.Type, Pos(ao.Exact, ao.X, ao.Y, ao.Z),
                    ao.HasSpeed ? new Vec3d(FixedPoint.FromVelocity(ao.SpeedX), FixedPoint.FromVelocity(ao.SpeedY), FixedPoint.FromVelocity(ao.SpeedZ)) : default,
                    ao.ThrowerEntityId);
                break;
            case MobSpawnPacket ms:
                w.Entities.SpawnMob(ms.EntityId, ms.Type, Pos(ms.Exact, ms.X, ms.Y, ms.Z), FixedPoint.FromAngle(ms.Yaw),
                    FixedPoint.FromAngle(ms.Pitch), ToDictionary(ms.Metadata));
                break;
            case EntityPaintingPacket pt:
                w.Entities.SpawnPainting(pt.EntityId, pt.Title, new Vec3d(pt.X + 0.5, pt.Y + 0.5, pt.Z + 0.5), pt.Direction);
                break;
            case ExperienceOrbPacket orb:
                w.Entities.SpawnXpOrb(orb.EntityId, Pos(orb.Exact, orb.X, orb.Y, orb.Z), orb.Count);
                break;
            case ThunderboltPacket tb:
                w.Entities.Spawn(new Entity(tb.EntityId, EntityKind.Weather, Abs(tb.X, tb.Y, tb.Z)));
                w.Entities.NoteEvent(tb.EntityId, "lightning", Abs(tb.X, tb.Y, tb.Z)); // the viewer's flash
                // b1.8.1 never says "thunder": a bolt while it rains is the storm (1.12.2's reason 8 keeps it exact afterwards).
                if (IsRaining && w.Thunder < 0.5f) w.Thunder = 1f;
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
                w.Entities.Teleport(et.EntityId, Pos(et.Exact, et.X, et.Y, et.Z), FixedPoint.FromAngle(et.Yaw), FixedPoint.FromAngle(et.Pitch));
                break;
            case EntityMetadataPacket md:
                if (md.EntityId == w.Player.EntityId) OnSelfMetadata(md.Metadata);
                else w.Entities.SetMetadata(md.EntityId, ToDictionary(md.Metadata));
                break;
            case EntityEquipmentPacket eq:
            {
                w.Entities.SetEquipment(eq.EntityId, eq.Slot, eq.Item);
                int eid = eq.EntityId, slot = eq.Slot, item = eq.ItemId, dmg = eq.Damage;
                Raise(() => EquipmentChanged?.Invoke(eid, slot, item, dmg));
                break;
            }
            case AttachEntityPacket at:
            {
                w.Entities.Attach(at.EntityId, at.VehicleId);
                if (at.EntityId == w.Player.EntityId) OnSelfAttached(at.VehicleId);
                int eid = at.EntityId, vid = at.VehicleId;
                Raise(() => EntityAttached?.Invoke(eid, vid));
                break;
            }
            case CollectItemPacket ci:
            {
                if (ci.CollectorEntityId == w.Player.EntityId) Interlocked.Increment(ref _collectedCount);
                NoteOrbCollected(ci.CollectorEntityId, ci.CollectedEntityId);
                int collector = ci.CollectorEntityId, itemEntity = ci.CollectedEntityId;
                ItemStack? stack = w.Entities.Get(itemEntity) is { Kind: EntityKind.Item } ie ? ie.Item : null;
                // Before the next survival tick can eat it: a pickup during a hand-off intent is held for its target.
                if (collector == w.Player.EntityId && stack is { IsEmpty: false } got) Handoffs.OnPickup(got.Id, got.Count);
                Raise(() => ItemCollected?.Invoke(collector, itemEntity, stack));
                break;
            }
            case EntityStatusPacket st:
            {
                if (st.EntityId == w.Player.EntityId && st.Status == 9) Interlocked.Increment(ref _eatAcceptedCount);
                if (st.EntityId == w.Player.EntityId) OnSelfStatus(st.Status);
                if (st.EntityId != w.Player.EntityId) Combat.OnEntityStatus(st.EntityId, st.Status);
                OnEntityStatusSeen(st.EntityId);
                if (EntityEventRing.StatusKind(st.Status) is { } statusKind && w.Entities.Get(st.EntityId) is not { Kind: EntityKind.Object })
                    w.Entities.NoteEvent(st.EntityId, statusKind); // the viewer's hurt/death (a breaking egg's 3 isn't one)
                if (EntityEventRing.GolemStatus(st.Status) is { } golem && w.Entities.Get(st.EntityId) is { Kind: EntityKind.Mob, TypeId: MobTypes.IronGolem })
                {
                    if (golem == "swing") w.Entities.NoteEvent(st.EntityId, golem); // the viewer's golem attack (display only)
                    else w.Entities.HoldRose(st.EntityId, golem == "rose");
                }
                if (st.Status == 2 && st.EntityId != w.Player.EntityId) OnPlayerHurtForPunch(st.EntityId);
                int eid = st.EntityId, status = st.Status;
                Raise(() => EntityStatus?.Invoke(eid, status));
                break;
            }
            case UseBedPacket ub:
            {
                int eid = ub.EntityId, bx = ub.X, by = ub.Y, bz = ub.Z;
                Raise(() => EntityUsedBed?.Invoke(eid, bx, by, bz));
                break;
            }
            case AnimationPacket an:
                if (an.EntityId != w.Player.EntityId)
                {
                    int eid = an.EntityId, anim = an.Animation;
                    if (EntityEventRing.AnimationKind(anim, an.Hand) is { } animKind) w.Entities.NoteEvent(eid, animKind); // the viewer's swing/crit
                    Combat.OnAnimation(eid, anim);
                    Raise(() => EntityAnimated?.Invoke(eid, anim));
                }
                break;
            case BlockBreakAnimationMessage bb:
                if (bb.EntityId != w.Player.EntityId)
                {
                    int eid = bb.EntityId, stage = bb.Stage is >= 0 and <= 9 ? bb.Stage : -1;
                    var bp = new BlockPos(bb.X, bb.Y, bb.Z);
                    Raise(() => BlockBreakProgress?.Invoke(eid, bp, stage));
                }
                break;
            case ItemEntityStackMessage ist:
                if (ist.Item.IsEmpty) w.Entities.Destroy(ist.EntityId);
                else w.Entities.SetItem(ist.EntityId, ist.Item);
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
                w.Inventory.Open(ow.WindowId, (WindowType)ow.InventoryType, ow.Title, ow.SlotCount, ow.Kind);
                if (_lastActivated is { } act && Environment.TickCount64 - act.At <= 5000)
                {
                    _lastActivated = null;
                    int wid = ow.WindowId, wtype = ow.InventoryType;
                    Raise(() => ContainerOpened?.Invoke(wid, wtype, act.Pos.X, act.Pos.Y, act.Pos.Z));
                }
                break;
            case CloseWindowPacket cw:
                w.Inventory.Close(cw.WindowId);
                break;
            case SetSlotPacket ss:
                NoteSlotPacket(ss.WindowId, ss.Slot, fullWindow: false, ss.Item);
                w.Inventory.SetSlot(ss.WindowId, ss.Slot, ss.Item);
                break;
            case WindowItemsPacket wi:
                WindowItemsArriving?.Invoke(wi.WindowId); // synchronous: the inventory ledger tags the model change it causes
                NoteSlotPacket(wi.WindowId, -1, fullWindow: true);
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
                NoteTransaction(tx.WindowId, tx.ActionNumber); // before the result is visible to the waiting click
                _txResults[(tx.WindowId, tx.ActionNumber)] = tx.Accepted;
                break;
            default:
                DispatchEntities(s, packet); // P4: effects, attributes, head look, held item, experience, death message, cooldowns
                break;
        }
    }

    public bool IsRaining { get; private set; }

    /// <summary>The exact position a newer version sent (1.12.2 doubles), else the canonical fixed point.</summary>
    private static Vec3d Pos((double X, double Y, double Z)? exact, int x, int y, int z) =>
        exact is { } e ? new Vec3d(e.X, e.Y, e.Z) : Abs(x, y, z);

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
                if (ActiveVoidStep is { } voidStep) OnDiedDuringVoidStep(voidStep, World.Player.Position);
                Raise(() => Died?.Invoke());
                AbortCurrentAction("died");
                if (Options.AutoRespawn) _ = RespawnLaterAsync(s);
            }
            return;
        }
        _wasDead = false;

        if (oldHealth > 0 && health < oldHealth)
        {
            int dmg = oldHealth - health;
            string? cause = DamageCause(dmg);
            RaiseDanger(DangerKind.Damage, $"took {dmg} damage (hp {health}/20)" + (cause is null ? "" : $": {cause}"), abort: false);
        }
        SurvivalOnHealth(health, food);
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
