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

    /// <summary>Air supply (b1.8.1 short / 1.12.2 VarInt <c>Entity.AIR</c>): 300 full, counting down while the eye is in water, 0 and
    /// below = drowning (2 damage at −20, then 0 again).</summary>
    public const int Air = 1;

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

    // ---- looks (50+): what a renderer needs and the bot never reads (the browser viewer's /api/world/entities) ----
    /// <summary>A horse's colour and markings (int: colour in bits 0..7, markings in 8..15; 1.12.2 <c>EntityHorse.HORSE_VARIANT</c>).</summary>
    public const int HorseVariant = 50;
    /// <summary>Horse-family status (sbyte: 0x02 tame, 0x04 saddled, 0x08 bred, 0x20 rearing; <c>AbstractHorse.STATUS</c>).</summary>
    public const int HorseStatus = 51;
    /// <summary>A horse's armour (int: 0 none, 1 iron, 2 gold, 3 diamond; <c>EntityHorse.HORSE_ARMOR</c>).</summary>
    public const int HorseArmor = 52;
    /// <summary>A donkey, mule or llama carrying a chest (sbyte 1; <c>AbstractChestHorse.DATA_ID_CHEST</c>).</summary>
    public const int HasChest = 53;
    /// <summary>A llama's colour (int: 0 creamy, 1 white, 2 brown, 3 gray).</summary>
    public const int LlamaVariant = 54;
    /// <summary>A llama's carpet colour (int: dye meta, −1 none).</summary>
    public const int CarpetColor = 55;
    /// <summary>A villager's or zombie villager's profession (int: 0 farmer, 1 librarian, 2 priest, 3 blacksmith, 4 butcher, 5 nitwit).</summary>
    public const int Profession = 56;
    /// <summary>An ocelot's skin (int: 0 wild, 1 black, 2 red, 3 siamese).</summary>
    public const int OcelotType = 57;
    /// <summary>A rabbit's coat (int: 0 brown, 1 white, 2 black, 3 white splotched, 4 gold, 5 salt, 99 the killer bunny; RenderRabbit).</summary>
    public const int RabbitType = 58;
    /// <summary>A parrot's colour (int: 0 red-blue, 1 blue, 2 green, 3 yellow-blue, 4 grey; RenderParrot's textures).</summary>
    public const int ParrotVariant = 59;
    /// <summary>A tame wolf's collar (int: <c>EnumDyeColor.getDyeDamage</c>, 0 black..15 white, 1 red by default — not the wool meta).</summary>
    public const int CollarColor = 60;
    /// <summary>An item frame's item rotation (int 0..7).</summary>
    public const int FrameRotation = 61;
    /// <summary>A boat's wood (int: 0 oak, 1 spruce, 2 birch, 3 jungle, 4 acacia, 5 dark oak).</summary>
    public const int BoatType = 62;
    /// <summary>Armour stand flags (sbyte: 0x01 small, 0x04 arms, 0x08 no base plate, 0x10 marker).</summary>
    public const int StandFlags = 63;
    /// <summary>Armour stand pose, head / body / left arm / right arm / left leg / right leg (the Protocol's three-int vector: whole degrees x, y, z).</summary>
    public const int StandHead = 64, StandBody = 65, StandLeftArm = 66, StandRightArm = 67, StandLeftLeg = 68, StandRightLeg = 69;
    /// <summary>A shulker's colour (int: <c>EnumDyeColor</c> metadata; 10 purple by default).</summary>
    public const int ShulkerColor = 70;

    /// <summary>The flag bit of a <see cref="HandStates"/> value: an item is being used.</summary>
    public const int HandActive = 0x01, HandOffhand = 0x02;
}
