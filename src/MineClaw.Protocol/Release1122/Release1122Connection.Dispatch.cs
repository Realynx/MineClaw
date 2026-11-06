using MineClaw.Protocol.Packets;

namespace MineClaw.Protocol.Release1122;

/// <summary>
/// The play-state translator table of the 1.12.2 client connection: wire packet id → the method that turns it into a canonical
/// message (or answers it inside the connection and returns null). Built once; a lookup per packet, no allocation. An id with no
/// translator (most sounds, particles, advancements, recipe book, boss bars, scoreboards, a leash, the tracker's bare keep-alive…) is
/// recorded in the traffic log only.
/// </summary>
public sealed partial class Release1122Connection
{
    private delegate IPacket? PlayTranslator(Release1122Connection c, ReadOnlySpan<byte> body);

    /// <summary>One past the highest clientbound play id of protocol 340 (Entity Effect, 0x4F).</summary>
    private const int PlayIdCount = 0x50;

    private static readonly PlayTranslator?[] PlayTranslators = BuildPlayTranslators();

    private static PlayTranslator?[] BuildPlayTranslators()
    {
        var t = new PlayTranslator?[PlayIdCount];
        // P1 (Release1122Connection.cs): the session, the player, the world.
        t[Ids1122.KeepAlive] = static (c, b) => c.TranslateKeepAlive(b);
        t[Ids1122.JoinGame] = static (c, b) => c.TranslateJoinGame(b);
        t[Ids1122.Respawn] = static (c, b) => c.TranslateRespawn(b);
        t[Ids1122.PlayerPosLook] = static (c, b) => c.TranslatePlayerPosLook(b);
        t[Ids1122.Chat] = static (c, b) => c.TranslateChat(b);
        t[Ids1122.Disconnect] = static (c, b) => new KickPacket(ChatComponent.ToPlain(new WireReader(b).ReadString(), c.Lang));
        t[Ids1122.TimeUpdate] = static (_, b) => TranslateTimeUpdate(b);
        t[Ids1122.UpdateHealth] = static (_, b) => TranslateUpdateHealth(b);
        t[Ids1122.SpawnPosition] = static (_, b) => TranslateSpawnPosition(b);
        t[Ids1122.ChangeGameState] = static (_, b) => TranslateChangeGameState(b);
        t[Ids1122.PlayerListItem] = static (c, b) => c.TranslatePlayerList(b);
        t[Ids1122.ChunkData] = static (c, b) => c.TranslateChunkData(b);
        t[Ids1122.UnloadChunk] = static (_, b) => TranslateUnloadChunk(b);
        t[Ids1122.BlockChange] = static (_, b) => TranslateBlockChange(b);
        t[Ids1122.MultiBlockChange] = static (_, b) => TranslateMultiBlockChange(b);
        t[Ids1122.Explosion] = static (_, b) => TranslateExplosion(b);
        // P2 (Release1122Connection.Play2.cs): windows, block entities, block break animations.
        t[Ids1122.OpenWindow] = static (c, b) => c.TranslateOpenWindow(b);
        t[Ids1122.CloseWindow] = static (c, b) => c.TranslateCloseWindow(b);
        t[Ids1122.WindowItems] = static (_, b) => TranslateWindowItems(b);
        t[Ids1122.SetSlot] = static (_, b) => TranslateSetSlot(b);
        t[Ids1122.WindowProperty] = static (c, b) => c.TranslateWindowProperty(b);
        t[Ids1122.ConfirmTransaction] = static (_, b) => TranslateConfirmTransaction(b);
        t[Ids1122.BlockBreakAnim] = static (_, b) => TranslateBlockBreakAnim(b);
        t[Ids1122.UpdateBlockEntity] = static (c, b) => c.TranslateUpdateBlockEntity(b);
        // P4 (Release1122Connection.Entities.cs): every entity kind, metadata, moves, effects, attributes, passengers.
        t[Ids1122.SpawnObject] = static (c, b) => c.TranslateSpawnObject(b);
        t[EntityIds1122.SpawnMob] = static (c, b) => c.TranslateSpawnMob(b);
        t[EntityIds1122.SpawnXpOrb] = static (c, b) => c.TranslateSpawnXpOrb(b);
        t[EntityIds1122.SpawnGlobal] = static (_, b) => TranslateSpawnGlobal(b);
        t[EntityIds1122.SpawnPainting] = static (c, b) => c.TranslateSpawnPainting(b);
        t[Ids1122.SpawnPlayer] = static (c, b) => c.TranslateSpawnPlayer(b);
        t[Ids1122.EntityMetadata] = static (c, b) => c.TranslateEntityMetadata(b);
        t[Ids1122.EntityRelMove] = static (c, b) => c.TranslateRelativeMove(b, withLook: false);
        t[Ids1122.EntityLookRelMove] = static (c, b) => c.TranslateRelativeMove(b, withLook: true);
        t[Ids1122.EntityLook] = static (c, b) => c.TranslateEntityLook(b);
        t[Ids1122.EntityTeleport] = static (c, b) => c.TranslateEntityTeleport(b);
        t[EntityIds1122.HeadLook] = static (c, b) => c.TranslateHeadLook(b);
        t[EntityIds1122.Velocity] = static (_, b) => TranslateVelocity(b);
        t[Ids1122.DestroyEntities] = static (c, b) => c.TranslateDestroyEntities(b);
        t[Ids1122.CollectItem] = static (_, b) => TranslateCollectItem(b);
        t[Ids1122.EntityStatus] = static (_, b) => TranslateEntityStatus(b);
        t[Ids1122.EntityAnimation] = static (_, b) => TranslateEntityAnimation(b);
        t[Ids1122.EntityEquipment] = static (_, b) => TranslateEntityEquipment(b);
        t[EntityIds1122.EntityEffect] = static (_, b) => TranslateEntityEffect(b);
        t[EntityIds1122.RemoveEffect] = static (_, b) => TranslateRemoveEffect(b);
        t[EntityIds1122.EntityProperties] = static (_, b) => TranslateEntityProperties(b);
        t[EntityIds1122.SetPassengers] = static (c, b) => c.TranslateSetPassengers(b);
        t[EntityIds1122.UseBed] = static (_, b) => TranslateUseBed(b);
        // The local player.
        t[Ids1122.HeldItemChangeIn] = static (_, b) => new HoldingChangePacket { Slot = new WireReader(b).ReadSByte() }; // handleHeldItemChange: the server picked the hotbar slot
        t[EntityIds1122.SetExperience] = static (_, b) => TranslateSetExperience(b);
        t[EntityIds1122.CombatEvent] = static (c, b) => c.TranslateCombatEvent(b);
        t[EntityIds1122.SetCooldown] = static (_, b) => TranslateSetCooldown(b);
        t[Ids1122.SoundEffect] = static (_, b) => TranslateSoundEffect(b);
        // EntityIds1122.Entity (SPacketEntity with no move: a keep-alive of the tracker) and EntityIds1122.AttachEntity (a leash,
        // not riding) have no translator: they surface nothing.
        return t;
    }

    private static IPacket? TranslateUnloadChunk(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new PreChunkPacket { ChunkX = r.ReadInt(), ChunkZ = r.ReadInt(), Load = false };
    }

    private static IPacket? TranslateCollectItem(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new CollectItemPacket { CollectedEntityId = r.ReadVarInt(), CollectorEntityId = r.ReadVarInt() }; // + pickup count
    }

    private static IPacket? TranslateRemoveEffect(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new RemoveEntityEffectPacket { EntityId = r.ReadVarInt(), EffectId = (sbyte)r.ReadUByte() };
    }

    private static IPacket? TranslateSetExperience(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new ExperienceMessage { Bar = r.ReadFloat(), Level = r.ReadVarInt(), Total = r.ReadVarInt() };
    }

    private static IPacket? TranslateSetCooldown(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        return new ItemCooldownMessage { ItemId = r.ReadVarInt(), Ticks = r.ReadVarInt() };
    }

    /// <summary>Sound Effect 0x49 (<c>SPacketSoundEffect</c>: sound id, category, x·8, y·8, z·8, volume, pitch): only the sounds a
    /// reader needs (<see cref="SoundMessage.Ids1122"/>) surface; the rest stay in the traffic log.</summary>
    private static IPacket? TranslateSoundEffect(ReadOnlySpan<byte> body)
    {
        var r = new WireReader(body);
        if (!SoundMessage.Ids1122.TryGetValue(r.ReadVarInt(), out var name)) return null;
        r.ReadVarInt(); // category
        double x = r.ReadInt() / 8.0, y = r.ReadInt() / 8.0, z = r.ReadInt() / 8.0;
        return new SoundMessage { Sound = name, X = x, Y = y, Z = z, Volume = r.ReadFloat(), Pitch = r.ReadFloat() };
    }
}
