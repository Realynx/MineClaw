namespace MineClaw.Data;

/// <summary>
/// Canonical entity metadata indices (docs/design/multi-version.md, "EntityMetadata"): the vocabulary the Protocol translators
/// write and the World/Client readers use. 0..31 are b1.8.1's own DataWatcher indices (the combat and pickup code read 0, 16
/// and 17); a newer version's translator maps its own layout onto them where the meaning is the same. 32+ are facts b1.8.1
/// never had; only a newer version's connection produces them (b1.8.1's wire can't carry an index above 31).
/// Value types: flags and booleans are <see cref="sbyte"/> (0/1), counts and ids <see cref="int"/>, health <see cref="float"/>,
/// stacks <see cref="ItemStack"/>.
/// </summary>
public static class EntityMeta
{
    /// <summary>Entity flags (sbyte): 0x01 on fire, 0x02 sneaking, 0x04 riding, 0x08 sprinting, 0x10 eating/using an item,
    /// 0x20 invisible (1.8+), 0x40 glowing (1.9+), 0x80 elytra (1.9+).</summary>
    public const int Flags = 0;
    public const int FlagBurning = 0x01, FlagSneaking = 0x02, FlagRiding = 0x04, FlagSprinting = 0x08, FlagUsingItem = 0x10,
        FlagInvisible = 0x20, FlagGlowing = 0x40;

    /// <summary>b1.8.1 index 16, per mob type: creeper state (int, −1 idle / 1 fusing), slime/magma cube size (int 1, 2, 4), wolf
    /// flags (sbyte 1 sitting, 2 angry, 4 tamed), spider climbing (sbyte 1), ghast attacking (sbyte 1), pig saddled (sbyte 1),
    /// sheep wool (sbyte: colour in bits 0..3, 0x10 sheared), blaze on fire (sbyte 1).</summary>
    public const int Mob16 = 16;
    /// <summary>b1.8.1 index 17: creeper powered/charged (sbyte 1).</summary>
    public const int Mob17 = 17;

    // ---- 1.9+ facts (32+) ----
    /// <summary>Health of a living entity (float).</summary>
    public const int Health = 32;
    /// <summary>A baby (zombie, husk, zombie villager, animals) (sbyte 1): half-size hitbox.</summary>
    public const int Baby = 33;
    /// <summary>Creeper lit with flint and steel (sbyte 1): it fuses whatever the distance.</summary>
    public const int CreeperIgnited = 34;
    /// <summary>EntityLivingBase hand states (sbyte): 0x01 an item is in use (eating, drawing a bow, raising a shield), 0x02 in
    /// the off hand.</summary>
    public const int HandStates = 35;
    /// <summary>Arrows stuck in a living entity (int).</summary>
    public const int ArrowsInEntity = 36;
    /// <summary>The item an item frame shows, a thrown potion, a firework (ItemStack).</summary>
    public const int Item = 37;
    /// <summary>A player's absorption hearts (float).</summary>
    public const int Absorption = 38;
    /// <summary>A critical arrow (sbyte 1): shot at full draw; cleared when it lands.</summary>
    public const int ArrowCritical = 40;
    /// <summary>Attacking pose (sbyte 1): zombie arms raised, skeleton/stray drawing, illager aggressive.</summary>
    public const int Aggressive = 41;
    /// <summary>A zombie villager being cured (sbyte 1).</summary>
    public const int Converting = 42;
    /// <summary>A witch drinking a potion (sbyte 1).</summary>
    public const int Drinking = 43;
    /// <summary>An evoker's spell (int: 0 none, 1 summon vex, 2 fangs, 3 wololo).</summary>
    public const int Spell = 44;
    /// <summary>A guardian's laser target (int entity id; 0 = none).</summary>
    public const int TargetEntity = 45;
    /// <summary>A shulker's peek (int 0..100: open = vulnerable).</summary>
    public const int Peek = 46;
    /// <summary>An enderman screaming (sbyte 1): it is attacking someone.</summary>
    public const int Screaming = 47;
    /// <summary>Primed TNT fuse ticks left (int).</summary>
    public const int Fuse = 48;
    /// <summary>A player's main hand (sbyte: 0 left, 1 right).</summary>
    public const int MainHand = 49;

    /// <summary>The flag bit of a <see cref="HandStates"/> value: an item is being used.</summary>
    public const int HandActive = 0x01, HandOffhand = 0x02;
}
