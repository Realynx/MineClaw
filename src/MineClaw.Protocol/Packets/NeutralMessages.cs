using MineClaw.Data;

namespace MineClaw.Protocol.Packets;

/// <summary>
/// Another player's dig progress (1.12.2 Block Break Animation 0x08, <c>SPacketBlockBreakAnim</c>): the digger's entity id,
/// the block and the crack stage 0..9 (−1 or ≥ 10 = stopped). Version-neutral: b1.8.1 sends nothing like it (its observers
/// infer digs from arm swings).
/// </summary>
public sealed class BlockBreakAnimationMessage : IPacket
{
    public byte Id => AnimationPacket.PacketId;
    public int EntityId { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public int Stage { get; init; }

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("BlockBreakAnimationMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("BlockBreakAnimationMessage has no b1.8.1 wire form");
    public override string ToString() => $"BlockBreakAnimation(entity {EntityId} at {X},{Y},{Z} stage {Stage})";
}

/// <summary>
/// A dropped item entity's stack changed after its spawn (1.12.2 Entity Metadata index 6 of <c>EntityItem</c>: stacks merging,
/// a partial pickup). The spawn itself is surfaced as <see cref="PickupSpawnPacket"/>. Version-neutral: b1.8.1 item entities
/// never change their stack.
/// </summary>
public sealed class ItemEntityStackMessage : IPacket
{
    public byte Id => EntityMetadataPacket.PacketId;
    public int EntityId { get; init; }
    public ItemStack Item { get; init; } = ItemStack.Empty;

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("ItemEntityStackMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("ItemEntityStackMessage has no b1.8.1 wire form");
    public override string ToString() => $"ItemEntityStack(entity {EntityId}: {Item})";
}

/// <summary>An entity's head yaw (1.12.2 Entity Head Look 0x36, <c>SPacketEntityHeadLook</c>; b1.8.1 sends none): where a mob
/// or player is looking, which can differ from its body yaw.</summary>
public sealed class EntityHeadLookMessage : IPacket
{
    public byte Id => EntityLookPacket.PacketId;
    public int EntityId { get; init; }
    /// <summary>Angle byte (256 = 360°), like the canonical look packets.</summary>
    public byte Yaw { get; init; }

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("EntityHeadLookMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("EntityHeadLookMessage has no b1.8.1 wire form");
    public override string ToString() => $"EntityHeadLook(entity {EntityId} yaw {Yaw})";
}

/// <summary>One attribute modifier (<c>AttributeModifier</c>): operation 0 adds to the base, 1 adds base × amount, 2 multiplies
/// the total by (1 + amount).</summary>
public readonly record struct AttributeModifierValue(Guid Uuid, double Amount, int Operation);

/// <summary>One synced attribute of an entity (<c>SPacketEntityProperties.Snapshot</c>): key ("generic.movementSpeed",
/// "generic.attackSpeed"…), base value and modifiers.</summary>
public sealed record EntityAttribute(string Key, double Base, IReadOnlyList<AttributeModifierValue> Modifiers)
{
    /// <summary><c>ModifiableAttributeInstance.computeValue</c>: base + Σop0, then + base' × Σop1, then × Π(1 + op2).</summary>
    public double Value
    {
        get
        {
            double d = Base;
            foreach (var m in Modifiers) if (m.Operation == 0) d += m.Amount;
            double e = d;
            foreach (var m in Modifiers) if (m.Operation == 1) e += d * m.Amount;
            foreach (var m in Modifiers) if (m.Operation == 2) e *= 1 + m.Amount;
            return e;
        }
    }
}

/// <summary>An entity's synced attributes (1.12.2 Entity Properties 0x4E): movement speed (players and mobs), attack speed
/// (players), max health, armor… b1.8.1 has no attributes.</summary>
public sealed class EntityPropertiesMessage : IPacket
{
    public byte Id => EntityMetadataPacket.PacketId;
    public int EntityId { get; init; }
    public IReadOnlyList<EntityAttribute> Attributes { get; init; } = [];

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("EntityPropertiesMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("EntityPropertiesMessage has no b1.8.1 wire form");
    public override string ToString() => $"EntityProperties(entity {EntityId}: {string.Join(", ", Attributes.Select(a => $"{a.Key}={a.Value:0.###}"))})";
}

/// <summary>The local player's experience (1.12.2 Set Experience 0x40: float bar 0..1, VarInt level, VarInt total). b1.8.1's
/// <see cref="ExperiencePacket"/> carries bytes and a short.</summary>
public sealed class ExperienceMessage : IPacket
{
    public byte Id => ExperiencePacket.PacketId;
    public float Bar { get; init; }
    public int Level { get; init; }
    public int Total { get; init; }

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("ExperienceMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("ExperienceMessage has no b1.8.1 wire form");
    public override string ToString() => $"Experience(level {Level}, bar {Bar:0.##}, total {Total})";
}

/// <summary>The local player died (1.12.2 Combat Event 0x2D ENTITY_DIED, <c>SPacketCombatEvent</c>): the death screen's message
/// (already flattened to plain text) and the killer's entity id (−1 = none).</summary>
public sealed class DeathMessage : IPacket
{
    public byte Id => UpdateHealthPacket.PacketId;
    public int PlayerId { get; init; }
    public int KillerId { get; init; } = -1;
    public string Message { get; init; } = "";

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("DeathMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("DeathMessage has no b1.8.1 wire form");
    public override string ToString() => $"Death(player {PlayerId}, killer {KillerId}: {Message})";
}

/// <summary>An item's use cooldown (1.12.2 Set Cooldown 0x17, <c>SPacketCooldown</c>): a shield disabled by an axe (100 ticks),
/// ender pearls, chorus fruit. 0 ticks = cleared.</summary>
public sealed class ItemCooldownMessage : IPacket
{
    public byte Id => SetSlotPacket.PacketId;
    public int ItemId { get; init; }
    public int Ticks { get; init; }

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("ItemCooldownMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("ItemCooldownMessage has no b1.8.1 wire form");
    public override string ToString() => $"ItemCooldown(item {ItemId}: {Ticks} ticks)";
}

/// <summary>A sound the server played at a spot (1.12.2 Sound Effect 0x49, <c>SPacketSoundEffect</c>, position to 1/8 block). Only
/// the sounds in <see cref="Ids1122"/> surface: <c>entity.bobber.splash</c>, a fish biting (<c>EntityFishHook.catchingFish</c>).
/// b1.8.1 sends none (its sounds are played by the client).</summary>
public sealed class SoundMessage : IPacket
{
    public const string BobberSplash = "entity.bobber.splash";
    /// <summary>The surfaced sounds by 1.12.2 sound id (<c>SoundEvent.registerSounds</c> order; minecraft-data sounds.json).</summary>
    public static readonly IReadOnlyDictionary<int, string> Ids1122 = LoadSoundIds();

    private static IReadOnlyDictionary<int, string> LoadSoundIds()
    {
        using var stream = typeof(SoundMessage).Assembly.GetManifestResourceStream("mcdata.1.12.2.sounds.json")!;
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        return json.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetInt32(), e => e.GetProperty("name").GetString()!);
    }

    public byte Id => SoundEffectPacket.PacketId;
    public string Sound { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public float Volume { get; init; } = 1;
    public float Pitch { get; init; } = 1;

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("SoundMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("SoundMessage has no b1.8.1 wire form");
    public override string ToString() => $"Sound({Sound} at {X:0.##},{Y:0.##},{Z:0.##})";
}

/// <summary>
/// A block entity's looks (1.12.2 Chunk Data block entities and Update Block Entity, <c>SPacketUpdateTileEntity</c>): beds, banners,
/// skulls, flower pots, spawners, chests… for the viewer. <see cref="Info"/> null = the block entity is gone. Signs keep travelling
/// as <see cref="UpdateSignPacket"/>. Version-neutral: b1.8.1 sends only signs.
/// </summary>
public sealed class BlockEntityMessage : IPacket
{
    public byte Id => UpdateSignPacket.PacketId;
    public int X { get; init; }
    public int Y { get; init; }
    public int Z { get; init; }
    public BlockEntityInfo? Info { get; init; }

    public void Read(MinecraftReader r, PacketDirection d) => throw new NotSupportedException("BlockEntityMessage has no b1.8.1 wire form");
    public void Write(MinecraftWriter w, PacketDirection d) => throw new NotSupportedException("BlockEntityMessage has no b1.8.1 wire form");
    public override string ToString() => $"BlockEntity({X},{Y},{Z}: {Info?.Kind ?? "removed"})";
}
