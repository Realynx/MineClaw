using MineClaw.Data;

namespace MineClaw.Protocol.Release1122;

/// <summary>One decoded 1.12.2 metadata entry: wire index, serializer id (<c>DataSerializers</c> registration order) and the value
/// (byte → byte, VarInt/facing/opt block → int, float, string/chat → string, slot → <see cref="ItemStack"/>, bool, rotation →
/// (float, float, float), position → long, opt position → long?, opt UUID → Guid?, NBT → null).</summary>
public readonly record struct Meta1122(int Index, int Serializer, object? Value);

/// <summary>What spawned an entity on the 1.12.2 wire: the metadata layout depends on it.</summary>
public enum EntityWireKind { Mob, Object, Player, Orb, Painting }

/// <summary>
/// The 1.12.2 entity metadata layouts (the <c>EntityDataManager.createKey</c> registrations of each class, in class-hierarchy
/// order: <c>Entity</c> 0..5 = flags, air, custom name, name visible, silent, no gravity; <c>EntityLivingBase</c> 6..10 = hand
/// states, health, potion colour, ambient, arrows; <c>EntityLiving</c> 11 = AI flags (players: absorption); subclasses from 12)
/// and their translation onto the canonical indices of <see cref="EntityMeta"/>, both ways (the client connection reads, the
/// fake server's connection writes).
/// </summary>
public static class EntityMeta1122
{
    // ------------------------------------------------------------------ wire

    /// <summary><c>EntityDataManager.readEntries</c>: ubyte index (0xFF ends), VarInt serializer, the value.</summary>
    public static List<Meta1122> Read(ReadOnlySpan<byte> data, ref int pos)
    {
        var list = new List<Meta1122>();
        while (true)
        {
            if (pos >= data.Length) throw new ProtocolException("entity metadata ran past the packet");
            int index = data[pos++];
            if (index == 0xFF) return list;
            var r = new WireReader(data[pos..]);
            int type = r.ReadVarInt();
            pos += r.Position;
            if (type == 5)
            {
                list.Add(new(index, 5, Codec1122.ReadSlot(data, ref pos)));
                continue;
            }
            if (type == 13)
            {
                Nbt.ReadRoot(data[pos..], out int used);
                pos += used;
                list.Add(new(index, 13, null));
                continue;
            }
            r = new WireReader(data[pos..]);
            object? v = type switch
            {
                0 => r.ReadUByte(),
                1 or 10 or 12 => r.ReadVarInt(),
                2 => r.ReadFloat(),
                3 or 4 => r.ReadString(),
                6 => r.ReadBool(),
                7 => (r.ReadFloat(), r.ReadFloat(), r.ReadFloat()),
                8 => r.ReadLong(),
                9 => r.ReadBool() ? r.ReadLong() : null,
                11 => r.ReadBool() ? r.ReadUuid() : null,
                _ => throw new ProtocolException($"entity metadata serializer {type} unknown"),
            };
            pos += r.Position;
            list.Add(new(index, type, v));
        }
    }

    /// <summary><c>EntityDataManager.writeEntries</c> (the serializers the fake server needs: byte, VarInt, float, string, slot,
    /// bool), then the 0xFF end.</summary>
    public static WireWriter WriteMetadata(this WireWriter w, IEnumerable<Meta1122> entries)
    {
        foreach (var e in entries)
        {
            w.WriteUByte((byte)e.Index).WriteVarInt(e.Serializer);
            switch (e.Serializer)
            {
                case 0: w.WriteUByte(Convert.ToByte(e.Value)); break;
                case 1: case 10: case 12: w.WriteVarInt(Convert.ToInt32(e.Value)); break;
                case 2: w.WriteFloat(Convert.ToSingle(e.Value)); break;
                case 3: case 4: w.WriteString((string)e.Value!); break;
                case 5: w.WriteSlot(e.Value is ItemStack s ? s : ItemStack.Empty); break;
                case 6: w.WriteBool((bool)e.Value!); break;
                case 7: { var (a, b, c) = ((float, float, float))e.Value!; w.WriteFloat(a).WriteFloat(b).WriteFloat(c); break; }
                default: throw new NotSupportedException($"writing metadata serializer {e.Serializer}");
            }
        }
        return w.WriteUByte(0xFF);
    }

    // ------------------------------------------------------------------ layouts

    private enum Conv
    {
        /// <summary>byte ↔ sbyte as is.</summary>
        Byte,
        /// <summary>bool ↔ sbyte 0/1.</summary>
        Bool,
        /// <summary>byte, non-zero ↔ sbyte 1.</summary>
        NonZero,
        /// <summary>byte bit 0 ↔ sbyte 0/1 (arrow critical).</summary>
        Bit0,
        /// <summary>byte ↔ int.</summary>
        ByteInt,
        VarInt,
        Float,
        Slot,
        /// <summary>rotation (float x, y, z degrees) ↔ a three-int vector of whole degrees.</summary>
        Rotation,
    }

    private readonly record struct Rule(int Wire, int Canon, Conv Conv);

    /// <summary>Wire index of <c>EntityLivingBase.HAND_STATES</c> (composed into the canonical flags' 0x10).</summary>
    public const int HandStatesIndex = 6;

    private static readonly Rule[] Living = [new(1, EntityMeta.Air, Conv.VarInt), new(7, EntityMeta.Health, Conv.Float), new(10, EntityMeta.ArrowsInEntity, Conv.VarInt)];
    private static readonly Rule[] PlayerRules = [.. Living, new(11, EntityMeta.Absorption, Conv.Float), new(14, EntityMeta.MainHand, Conv.Byte)];
    private static readonly Rule[] Ageable = [.. Living, new(12, EntityMeta.Baby, Conv.Bool)];
    private static readonly Rule[] Zombie = [.. Living, new(12, EntityMeta.Baby, Conv.Bool), new(14, EntityMeta.Aggressive, Conv.Bool)];
    private static readonly Dictionary<int, Rule[]> MobRules = new()
    {
        // EntityAgeable subclasses: animals and villagers (12 baby), then their own keys.
        [90] = [.. Ageable, new(13, EntityMeta.Mob16, Conv.Bool)],          // pig: SADDLED
        [91] = [.. Ageable, new(13, EntityMeta.Mob16, Conv.Byte)],          // sheep: DYE_COLOR (colour | 0x10 sheared), as b1.8.1's 16
        [92] = Ageable, [93] = Ageable, [96] = Ageable, [102] = Ageable,
        // AbstractHorse.STATUS 13 (OWNER 14): EntityHorse VARIANT 15, ARMOR 16; AbstractChestHorse HAS_CHEST 15; EntityLlama
        // STRENGTH 16, CARPET_COLOR 17, VARIANT 18 (the viewer's looks: canonical 50+).
        [100] = [.. Ageable, new(13, EntityMeta.HorseStatus, Conv.Byte), new(15, EntityMeta.HorseVariant, Conv.VarInt), new(16, EntityMeta.HorseArmor, Conv.VarInt)],
        [28] = [.. Ageable, new(13, EntityMeta.HorseStatus, Conv.Byte)], [29] = [.. Ageable, new(13, EntityMeta.HorseStatus, Conv.Byte)],
        [31] = [.. Ageable, new(13, EntityMeta.HorseStatus, Conv.Byte), new(15, EntityMeta.HasChest, Conv.Bool)],
        [32] = [.. Ageable, new(13, EntityMeta.HorseStatus, Conv.Byte), new(15, EntityMeta.HasChest, Conv.Bool)],
        [103] = [.. Ageable, new(13, EntityMeta.HorseStatus, Conv.Byte), new(15, EntityMeta.HasChest, Conv.Bool),
            new(17, EntityMeta.CarpetColor, Conv.VarInt), new(18, EntityMeta.LlamaVariant, Conv.VarInt)],
        [101] = [.. Ageable, new(13, EntityMeta.RabbitType, Conv.VarInt)],  // rabbit: RABBIT_TYPE
        [120] = [.. Ageable, new(13, EntityMeta.Profession, Conv.VarInt)],  // villager: PROFESSION
        [95] = [.. Ageable, new(13, EntityMeta.Mob16, Conv.Byte), new(17, EntityMeta.CollarColor, Conv.VarInt)], // wolf: EntityTameable.TAMED (1 sitting, 2 angry, 4 tamed), COLLAR_COLOR
        [98] = [.. Ageable, new(13, EntityMeta.Mob16, Conv.Byte), new(15, EntityMeta.OcelotType, Conv.VarInt)],   // ocelot: + OCELOT_VARIANT
        [105] = [.. Ageable, new(13, EntityMeta.Mob16, Conv.Byte), new(15, EntityMeta.ParrotVariant, Conv.VarInt)], // parrot: + VARIANT
        // EntityZombie (IS_CHILD 12, VILLAGER_TYPE 13, ARMS_RAISED 14): zombie, husk, zombie pigman, zombie villager (+ CONVERTING 15).
        [54] = Zombie, [23] = Zombie, [57] = Zombie,
        [27] = [.. Zombie, new(15, EntityMeta.Converting, Conv.Bool), new(16, EntityMeta.Profession, Conv.VarInt)],
        // AbstractSkeleton.SWINGING_ARMS: skeleton, stray, wither skeleton.
        [51] = [.. Living, new(12, EntityMeta.Aggressive, Conv.Bool)],
        [6] = [.. Living, new(12, EntityMeta.Aggressive, Conv.Bool)],
        [5] = [.. Living, new(12, EntityMeta.Aggressive, Conv.Bool)],
        // EntityCreeper: STATE (VarInt −1/1), POWERED, IGNITED.
        [50] = [.. Living, new(12, EntityMeta.Mob16, Conv.VarInt), new(13, EntityMeta.Mob17, Conv.Bool), new(14, EntityMeta.CreeperIgnited, Conv.Bool)],
        [52] = [.. Living, new(12, EntityMeta.Mob16, Conv.Byte)],           // spider: CLIMBING
        [59] = [.. Living, new(12, EntityMeta.Mob16, Conv.Byte)],           // cave spider
        [58] = [.. Living, new(13, EntityMeta.Screaming, Conv.Bool)],       // enderman: CARRIED_BLOCK 12, SCREAMING 13
        [61] = [.. Living, new(12, EntityMeta.Mob16, Conv.Byte)],           // blaze: ON_FIRE
        [55] = [.. Living, new(12, EntityMeta.Mob16, Conv.VarInt)],         // slime: SLIME_SIZE
        [62] = [.. Living, new(12, EntityMeta.Mob16, Conv.VarInt)],         // magma cube
        [56] = [.. Living, new(12, EntityMeta.Mob16, Conv.Bool)],           // ghast: ATTACKING
        [66] = [.. Living, new(12, EntityMeta.Drinking, Conv.Bool)],        // witch: IS_DRINKING
        [68] = [.. Living, new(13, EntityMeta.TargetEntity, Conv.VarInt)],  // guardian: MOVING 12, TARGET_ENTITY 13
        [4] = [.. Living, new(13, EntityMeta.TargetEntity, Conv.VarInt)],   // elder guardian
        [69] = [.. Living, new(14, EntityMeta.Peek, Conv.ByteInt), new(15, EntityMeta.ShulkerColor, Conv.ByteInt)], // shulker: ATTACHED_FACE 12, POS 13, PEEK_TICK 14, COLOR 15
        [36] = [.. Living, new(12, EntityMeta.Aggressive, Conv.NonZero)],   // vindicator: AbstractIllager.AGGRESSIVE
        [34] = [.. Living, new(12, EntityMeta.Aggressive, Conv.NonZero), new(13, EntityMeta.Spell, Conv.ByteInt)], // evoker: + SPELL
        [37] = [.. Living, new(12, EntityMeta.Aggressive, Conv.NonZero), new(13, EntityMeta.Spell, Conv.ByteInt)], // illusioner
    };
    private static readonly Dictionary<int, Rule[]> ObjectRules = new()
    {
        [60] = [new(6, EntityMeta.ArrowCritical, Conv.Bit0)],               // EntityArrow.CRITICAL
        [91] = [new(6, EntityMeta.ArrowCritical, Conv.Bit0)],               // spectral arrow
        [71] = [new(6, EntityMeta.Item, Conv.Slot), new(7, EntityMeta.FrameRotation, Conv.VarInt)], // EntityItemFrame.ITEM, ROTATION
        [1] = [new(9, EntityMeta.BoatType, Conv.VarInt)],                   // EntityBoat.BOAT_TYPE
        // EntityArmorStand (living, spawned as an object): STATUS 11, then the six pose rotations 12..17.
        [78] = [new(11, EntityMeta.StandFlags, Conv.Byte), new(12, EntityMeta.StandHead, Conv.Rotation), new(13, EntityMeta.StandBody, Conv.Rotation),
            new(14, EntityMeta.StandLeftArm, Conv.Rotation), new(15, EntityMeta.StandRightArm, Conv.Rotation),
            new(16, EntityMeta.StandLeftLeg, Conv.Rotation), new(17, EntityMeta.StandRightLeg, Conv.Rotation)],
        [73] = [new(6, EntityMeta.Item, Conv.Slot)],                        // EntityPotion.ITEM
        [76] = [new(6, EntityMeta.Item, Conv.Slot)],                        // EntityFireworkRocket.FIREWORK_ITEM
        [50] = [new(6, EntityMeta.Fuse, Conv.VarInt)],                      // EntityTNTPrimed.FUSE
    };

    private static Rule[] RulesFor(EntityWireKind kind, int type) => kind switch
    {
        EntityWireKind.Player => PlayerRules,
        EntityWireKind.Mob => MobRules.TryGetValue(type, out var r) ? r : Living,
        EntityWireKind.Object => ObjectRules.TryGetValue(type, out var o) ? o : [],
        _ => [],
    };

    private static bool IsLiving(EntityWireKind kind) => kind is EntityWireKind.Mob or EntityWireKind.Player;

    /// <summary>
    /// Translates decoded entries to canonical ones. <paramref name="rawFlags"/>/<paramref name="handStates"/> keep the
    /// entity's last wire flags and hand states, which compose the canonical flags (0x10 = an item in use, from the hand
    /// states; 1.12.2's own 0x10 bit is unused). Entries with no canonical meaning are dropped.
    /// </summary>
    public static EntityMetadata ToCanonical(EntityWireKind kind, int type, IReadOnlyList<Meta1122> entries, ref int rawFlags, ref int handStates)
    {
        var rules = RulesFor(kind, type);
        var outList = new List<MetadataEntry>();
        bool flagsChanged = false;
        foreach (var e in entries)
        {
            if (e.Index == 0 && e.Value is byte f) { rawFlags = f; flagsChanged = true; continue; }
            if (e.Index == HandStatesIndex && IsLiving(kind) && e.Value is byte h)
            {
                handStates = h;
                flagsChanged = true;
                outList.Add(MetadataEntry.FromByte(EntityMeta.HandStates, (sbyte)h));
                continue;
            }
            foreach (var rule in rules)
            {
                if (rule.Wire != e.Index) continue;
                if (ToEntry(rule, e.Value) is { } m) outList.Add(m);
                break;
            }
        }
        if (flagsChanged)
        {
            int flags = rawFlags & ~EntityMeta.FlagUsingItem;
            if ((handStates & EntityMeta.HandActive) != 0) flags |= EntityMeta.FlagUsingItem;
            outList.Insert(0, MetadataEntry.FromByte(EntityMeta.Flags, (sbyte)flags));
        }
        return new EntityMetadata(outList);
    }

    private static int Deg(float f) => float.IsFinite(f) ? (int)MathF.Round(f) : 0;

    private static MetadataEntry? ToEntry(Rule rule, object? v)
    {
        byte i = (byte)rule.Canon;
        return (rule.Conv, v) switch
        {
            (Conv.Byte, byte b) => MetadataEntry.FromByte(i, (sbyte)b),
            (Conv.Bool, bool b) => MetadataEntry.FromByte(i, b ? (sbyte)1 : (sbyte)0),
            (Conv.NonZero, byte b) => MetadataEntry.FromByte(i, b != 0 ? (sbyte)1 : (sbyte)0),
            (Conv.Bit0, byte b) => MetadataEntry.FromByte(i, (b & 1) != 0 ? (sbyte)1 : (sbyte)0),
            (Conv.ByteInt, byte b) => MetadataEntry.FromInt(i, b),
            (Conv.VarInt, int n) => MetadataEntry.FromInt(i, n),
            (Conv.Float, float x) => MetadataEntry.FromFloat(i, x),
            (Conv.Slot, ItemStack s) => MetadataEntry.FromItem(i, s),
            (Conv.Rotation, ValueTuple<float, float, float> t) => MetadataEntry.FromVector(i, new MetadataVector(Deg(t.Item1), Deg(t.Item2), Deg(t.Item3))),
            _ => null,
        };
    }

    /// <summary>The inverse (the fake server's connection): canonical entries → 1.12.2 entries for that entity's class.
    /// Canonical flags 0x10 becomes hand states 1 (main hand) unless hand states are given.</summary>
    public static List<Meta1122> FromCanonical(EntityWireKind kind, int type, EntityMetadata md)
    {
        var rules = RulesFor(kind, type);
        var list = new List<Meta1122>();
        bool handGiven = md.Get(EntityMeta.HandStates) is not null;
        foreach (var e in md)
        {
            if (e.Index == EntityMeta.Flags && e.AsInt() is { } f)
            {
                list.Add(new(0, 0, (byte)(f & ~EntityMeta.FlagUsingItem & 0xFF)));
                if (IsLiving(kind) && !handGiven) list.Add(new(HandStatesIndex, 0, (f & EntityMeta.FlagUsingItem) != 0 ? (byte)1 : (byte)0));
                continue;
            }
            if (e.Index == EntityMeta.HandStates && IsLiving(kind) && e.AsInt() is { } h) { list.Add(new(HandStatesIndex, 0, (byte)h)); continue; }
            foreach (var rule in rules)
            {
                if (rule.Canon != e.Index) continue;
                object? wire = rule.Conv switch
                {
                    Conv.Byte or Conv.NonZero or Conv.Bit0 => (byte)(e.AsInt() ?? 0),
                    Conv.Bool => (e.AsInt() ?? 0) != 0,
                    Conv.ByteInt => (byte)(e.AsInt() ?? 0),
                    Conv.VarInt => e.AsInt() ?? 0,
                    Conv.Float => e.Value is float x ? x : (float)(e.AsInt() ?? 0),
                    Conv.Slot => e.Value is ItemStack s ? s : ItemStack.Empty,
                    Conv.Rotation => e.Value is MetadataVector v ? ((float)v.X, (float)v.Y, (float)v.Z) : (0f, 0f, 0f),
                    _ => null,
                };
                int ser = rule.Conv switch
                {
                    Conv.Bool => 6, Conv.VarInt => 1, Conv.Float => 2, Conv.Slot => 5, Conv.Rotation => 7, _ => 0,
                };
                list.Add(new(rule.Wire, ser, wire));
                break;
            }
        }
        return list;
    }
}
