namespace MineClaw.World.Versioning;

/// <summary>
/// Entity facts of one version (docs/design/multi-version.md, "entity rules"): mob profiles (hitbox, health, melee damage,
/// tactic) and type names. The <see cref="EntityTracker"/> stamps each spawn with them, so every reader of an
/// <see cref="Entity"/> sees its version's facts without a version parameter. b1.8.1 returns null everywhere: its entities
/// carry no stamp and every reader keeps the b1.8.1 static tables (CombatRules.Profile, <see cref="MobTypes"/>, <see cref="ObjectTypes"/>).
/// </summary>
public interface IEntityRules
{
    /// <summary>The profile of a mob type; null = the b1.8.1 table.</summary>
    MobProfile? Mob(int type);
    /// <summary>The name of an object type (Spawn Object); null = <see cref="ObjectTypes.Name"/>.</summary>
    string? ObjectName(int type);
    /// <summary>Babies (the metadata's baby flag) have half-size hitboxes (1.12.2 <c>EntityZombie.setChildSize</c>,
    /// <c>EntityAgeable.setScale</c>).</summary>
    bool HasBabies { get; }
    /// <summary>The server plays a sound on a fish bite (1.12.2 <c>EntityFishHook.catchingFish</c>: <c>entity.bobber.splash</c> at the
    /// hook); b1.8.1 sends no sounds, so a bite is read from the bobber's motion.</summary>
    bool BiteSounds { get; }
}

/// <summary>b1.8.1: no stamps; every reader uses the existing tables, unchanged.</summary>
public sealed class Beta181EntityRules : IEntityRules
{
    public static readonly Beta181EntityRules Instance = new();
    public MobProfile? Mob(int type) => null;
    public string? ObjectName(int type) => null;
    public bool HasBabies => false;
    public bool BiteSounds => false;
}

/// <summary>
/// 1.12.2 entities, from the decompiled classes' constructors (<c>setSize</c>) and <c>applyEntityAttributes</c> (max health,
/// attack damage at normal difficulty; held weapons add their modifier: the zombie pigman's golden sword +4, the wither
/// skeleton's stone sword +4, the vindicator's iron axe +8, the vex's iron sword +5), cross-checked with minecraft-data's
/// entities.json. The creeper's "melee" is its point-blank blast: <c>Explosion.doExplosionB</c> (impact² + impact) / 2 · 7 ·
/// 2·power + 1 = 43 at power 3. Mobs of the End and woodland mansions are profiled but not fought (Ignore) when they can't be
/// met in an overworld default world at the start; an unknown type gets a safe default: not hostile, not fought.
/// </summary>
public sealed class Release1122EntityRules : IEntityRules
{
    public static readonly Release1122EntityRules Instance = new();

    private static readonly Dictionary<int, MobProfile> Mobs = Build();

    private static Dictionary<int, MobProfile> Build()
    {
        var d = new Dictionary<int, MobProfile>();
        void Add(int type, string name, double w, double h, int hp, int dmg, MobTactic t, bool hostile) =>
            d[type] = new MobProfile(type, w, h, hp, dmg, t, hostile) { Name = name };
        // hostiles
        Add(MobTypes.Creeper, "creeper", 0.6, 1.7, 20, 43, MobTactic.Creeper, true);
        Add(MobTypes.Skeleton, "skeleton", 0.6, 1.99, 20, 2, MobTactic.Ranged, true);
        Add(MobTypes.Stray, "stray", 0.6, 1.99, 20, 2, MobTactic.Ranged, true);            // its arrows add slowness
        Add(MobTypes.WitherSkeleton, "wither_skeleton", 0.7, 2.4, 20, 8, MobTactic.Melee, true); // + wither
        Add(MobTypes.Spider, "spider", 1.4, 0.9, 16, 2, MobTactic.Melee, true);
        Add(MobTypes.CaveSpider, "cave_spider", 0.7, 0.5, 12, 2, MobTactic.Melee, true);    // + poison (normal 7 s, hard 15 s)
        Add(MobTypes.Zombie, "zombie", 0.6, 1.95, 20, 3, MobTactic.Melee, true);
        Add(MobTypes.Husk, "husk", 0.6, 1.95, 20, 3, MobTactic.Melee, true);                // + hunger
        Add(MobTypes.ZombieVillager, "zombie_villager", 0.6, 1.95, 20, 3, MobTactic.Melee, true);
        Add(MobTypes.Slime, "slime", 0.51000005, 0.51000005, 1, 0, MobTactic.Melee, true); // size² hp, size damage (size > 1)
        Add(MobTypes.MagmaCube, "magma_cube", 0.51000005, 0.51000005, 1, 0, MobTactic.Melee, true);
        Add(MobTypes.Ghast, "ghast", 4, 4, 10, 0, MobTactic.Ignore, true);
        Add(MobTypes.Enderman, "enderman", 0.6, 2.9, 40, 7, MobTactic.Enderman, true);
        Add(MobTypes.Silverfish, "silverfish", 0.4, 0.3, 8, 1, MobTactic.Melee, true);
        Add(MobTypes.Endermite, "endermite", 0.4, 0.3, 8, 2, MobTactic.Melee, true);
        Add(MobTypes.Blaze, "blaze", 0.6, 1.8, 20, 6, MobTactic.Ranged, true);
        Add(MobTypes.Witch, "witch", 0.6, 1.95, 26, 0, MobTactic.Ranged, true);             // splash potions from range
        Add(MobTypes.Guardian, "guardian", 0.85, 0.85, 30, 6, MobTactic.Ranged, true);      // laser; lives in water
        Add(MobTypes.ElderGuardian, "elder_guardian", 1.9975, 1.9975, 80, 8, MobTactic.Ignore, true);
        Add(MobTypes.Shulker, "shulker", 1, 1, 30, 4, MobTactic.Ignore, true);              // the End only
        Add(MobTypes.Vindicator, "vindication_illager", 0.6, 1.95, 24, 13, MobTactic.Melee, true); // iron axe: disables shields
        Add(MobTypes.Evoker, "evocation_illager", 0.6, 1.95, 24, 6, MobTactic.Ranged, true);
        Add(MobTypes.Vex, "vex", 0.4, 0.8, 14, 9, MobTactic.Melee, true);
        Add(MobTypes.Illusioner, "illusion_illager", 0.6, 1.95, 32, 2, MobTactic.Ignore, true);
        Add(MobTypes.Giant, "giant", 3.6, 10.8, 100, 50, MobTactic.Ignore, true);
        Add(MobTypes.Wither, "wither", 0.9, 3.5, 300, 0, MobTactic.Ignore, true);
        Add(MobTypes.EnderDragon, "ender_dragon", 16, 8, 200, 0, MobTactic.Ignore, true);
        // neutral: fought only when provoked
        Add(MobTypes.PigZombie, "zombie_pigman", 0.6, 1.95, 20, 9, MobTactic.Melee, false);
        Add(MobTypes.Wolf, "wolf", 0.6, 0.85, 8, 2, MobTactic.Melee, false);                // angry: hostile
        Add(MobTypes.PolarBear, "polar_bear", 1.3, 1.4, 30, 6, MobTactic.Melee, false);
        Add(MobTypes.IronGolem, "villager_golem", 1.4, 2.7, 100, 14, MobTactic.Ignore, false); // 7..21: never picked
        Add(MobTypes.Llama, "llama", 0.9, 1.87, 22, 1, MobTactic.Melee, false);
        // passive
        Add(MobTypes.Pig, "pig", 0.9, 0.9, 10, 0, MobTactic.Melee, false);
        Add(MobTypes.Sheep, "sheep", 0.9, 1.3, 8, 0, MobTactic.Melee, false);
        Add(MobTypes.Cow, "cow", 0.9, 1.4, 10, 0, MobTactic.Melee, false);
        Add(MobTypes.Mooshroom, "mooshroom", 0.9, 1.4, 10, 0, MobTactic.Melee, false);
        Add(MobTypes.Chicken, "chicken", 0.4, 0.7, 4, 0, MobTactic.Melee, false);
        Add(MobTypes.Squid, "squid", 0.8, 0.8, 10, 0, MobTactic.Ignore, false);
        Add(MobTypes.Bat, "bat", 0.5, 0.9, 6, 0, MobTactic.Ignore, false);
        Add(MobTypes.Rabbit, "rabbit", 0.4, 0.5, 3, 0, MobTactic.Melee, false);
        Add(MobTypes.Ocelot, "ocelot", 0.6, 0.7, 10, 0, MobTactic.Melee, false);
        Add(MobTypes.Parrot, "parrot", 0.5, 0.9, 6, 0, MobTactic.Ignore, false);
        Add(MobTypes.Horse, "horse", 1.3964844, 1.6, 22, 0, MobTactic.Melee, false);
        Add(MobTypes.Donkey, "donkey", 1.3964844, 1.6, 22, 0, MobTactic.Melee, false);
        Add(MobTypes.Mule, "mule", 1.3964844, 1.6, 22, 0, MobTactic.Melee, false);
        Add(MobTypes.SkeletonHorse, "skeleton_horse", 1.3964844, 1.6, 15, 0, MobTactic.Ignore, false);
        Add(MobTypes.ZombieHorse, "zombie_horse", 1.3964844, 1.6, 15, 0, MobTactic.Ignore, false);
        Add(MobTypes.SnowGolem, "snowman", 0.7, 1.9, 4, 0, MobTactic.Ignore, false);
        Add(MobTypes.Villager, "villager", 0.6, 1.95, 20, 0, MobTactic.Ignore, false);
        return d;
    }

    /// <summary>The profile of every known type; an unknown type gets the safe default (a 0.6 × 1.8 box, not hostile, not fought).</summary>
    public MobProfile Mob(int type) => Mobs.TryGetValue(type, out var p) ? p : new MobProfile(type, 0.6, 1.8, 20, 0, MobTactic.Ignore, false) { Name = $"mob_{type}" };

    MobProfile? IEntityRules.Mob(int type) => Mob(type);

    public static IEnumerable<MobProfile> All => Mobs.Values;

    /// <summary>The profile of a type the table knows; null for an unknown one.</summary>
    public static MobProfile? Known(int type) => Mobs.GetValueOrDefault(type);

    public string? ObjectName(int type) => ObjectNames.TryGetValue(type, out var name) ? name : $"object_{type}";

    /// <summary>1.12.2 object types (Spawn Object; entities.json).</summary>
    private static readonly Dictionary<int, string> ObjectNames = new()
    {
        [1] = "boat", [2] = "item", [3] = "area_effect_cloud", [10] = "minecart", [50] = "primed_tnt", [51] = "ender_crystal", [60] = "arrow",
        [61] = "snowball", [62] = "egg", [63] = "fireball", [64] = "small_fireball", [65] = "ender_pearl", [66] = "wither_skull",
        [67] = "shulker_bullet", [68] = "llama_spit", [70] = "falling_block", [71] = "item_frame", [72] = "eye_of_ender", [73] = "potion",
        [75] = "xp_bottle", [76] = "firework", [77] = "leash_knot", [78] = "armor_stand", [79] = "evocation_fangs", [90] = "fishing_hook",
        [91] = "spectral_arrow", [93] = "dragon_fireball",
    };

    public bool HasBabies => true;
    public bool BiteSounds => true;
}
