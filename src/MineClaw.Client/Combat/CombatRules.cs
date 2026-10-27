using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>How the engine fights a mob type.</summary>
public enum MobTactic
{
    Melee,
    /// <summary>Ranged (skeleton): close in along safe ground or break line of sight; never chase over edges.</summary>
    Ranged,
    /// <summary>Creeper: watch the fuse.</summary>
    Creeper,
    /// <summary>Enderman: never look at it; fight only when provoked.</summary>
    Enderman,
    /// <summary>Not fought (ghast, giant, squid...).</summary>
    Ignore,
}

/// <summary>Static facts about a mob type in b1.8.1 (EntityXxx constructors / attackEntity).</summary>
public sealed record MobProfile(int Type, double Width, double Height, int MaxHealth, int MeleeDamage, MobTactic Tactic, bool Hostile);

/// <summary>
/// b1.8.1 combat numbers, from the MCP-decompiled b1.8.1 classes (EntityPlayer, EntityLiving, InventoryPlayer,
/// ItemSword, ItemTool, EnumToolMaterial, ItemArmor, EntityCreeper, EntityEnderman, EntityWolf, EntitySlime) and the
/// Beta 1.8 changelog (critical hits and sword blocking are new in b1.8). Pure functions, no state.
/// </summary>
public static class CombatRules
{
    /// <summary>EntityLiving.heartsHalvesLife = 20; a new full hit lands only once heartsLife ≤ 10.</summary>
    public const int InvulnerabilityTicks = 10;
    /// <summary>EntityCreeper: ignites within 3 blocks, keeps fusing while within 7, explodes after 30 fuse ticks.</summary>
    public const double CreeperIgniteRange = 3, CreeperCancelRange = 7;
    public const int CreeperFuseTicks = 30;
    /// <summary>NetServerHandler.handleUseEntity accepts targets with distSq &lt; 36 (6 blocks between positions).</summary>
    public const double ServerReach = 6;
    /// <summary>EntityPlayerSP: using an item (blocking) scales movement input by 0.2.</summary>
    public const float UseItemSlowdown = 0.2f;
    /// <summary>Entity status 0x26 values (World.setEntityState).</summary>
    public const int StatusHurt = 2, StatusDead = 3, StatusWolfSmoke = 6, StatusWolfHearts = 7;
    /// <summary>Animation 0x12 values.</summary>
    public const int AnimSwing = 1, AnimHurt = 2;

    private static readonly int[] MaterialDamage = [0, 1, 2, 3, 0]; // EnumToolMaterial.damageVsEntity: wood, stone, iron, diamond, gold

    /// <summary>Damage of one hit with the item (Item.getDamageVsEntity): swords 4 + 2·material, shovel/pickaxe/axe
    /// base 1/2/3 + material, anything else (hand, blocks, hoes) 1.</summary>
    public static int WeaponDamage(int itemId)
    {
        if (itemId <= 0 || !Items.Exists(itemId)) return 1;
        var info = Items.Get(itemId);
        if (info.ToolTier < 0 || info.ToolTier >= MaterialDamage.Length) return 1;
        int m = MaterialDamage[info.ToolTier];
        return info.Tool switch
        {
            ToolType.Sword => 4 + m * 2,
            ToolType.Shovel => 1 + m,
            ToolType.Pickaxe => 2 + m,
            ToolType.Axe => 3 + m,
            _ => 1,
        };
    }

    public static bool IsSword(int itemId) => itemId > 0 && Items.Exists(itemId) && Items.Get(itemId).Tool == ToolType.Sword;

    /// <summary>Critical hit: falling, not on ground, ladder or water → damage += rand(damage / 2 + 2). Returns the maximum.</summary>
    public static int MaxCritDamage(int damage) => damage + damage / 2 + 1;

    /// <summary>EntityPlayer.damageEntity while blocking: (1 + damage) >> 1.</summary>
    public static int BlockedDamage(int damage) => (1 + damage) >> 1;

    /// <summary>ItemArmor.damageReduceAmountArray (helmet, chestplate, leggings, boots) — the same for every material in Beta.</summary>
    public static int ArmorPoints(int itemId) => itemId switch
    {
        >= 298 and <= 317 => ((itemId - 298) % 4) switch { 0 => 3, 1 => 8, 2 => 6, _ => 3 },
        _ => 0,
    };

    /// <summary>InventoryPlayer.getTotalArmorValue: ((Σpoints − 1) · Σremaining) / Σmax + 1, scaled by durability.</summary>
    public static int ArmorValue(IEnumerable<ItemStack> armor)
    {
        int points = 0, remaining = 0, max = 0;
        foreach (var s in armor)
        {
            if (s.IsEmpty || ArmorPoints(s.Id) == 0) continue;
            int m = Items.Get(s.Id).MaxDamage;
            points += ArmorPoints(s.Id);
            max += m;
            remaining += Math.Max(0, m - s.Damage);
        }
        return max == 0 ? 0 : (points - 1) * remaining / max + 1;
    }

    /// <summary>EntityPlayer armor calculation: damage · (25 − armor) / 25 (fraction carried over); ceiling = worst case.</summary>
    public static int DamageAfterArmor(double damage, int armorValue) =>
        (int)Math.Ceiling(damage * (25 - Math.Clamp(armorValue, 0, 24)) / 25.0 - 1e-9);

    public static MobProfile Profile(int type) => type switch
    {
        MobTypes.Zombie => new(type, 0.6, 1.8, 20, 5, MobTactic.Melee, true),
        MobTypes.Skeleton => new(type, 0.6, 1.8, 20, 4, MobTactic.Ranged, true),
        MobTypes.Spider => new(type, 1.4, 0.9, 16, 2, MobTactic.Melee, true),
        MobTypes.CaveSpider => new(type, 0.7, 0.5, 12, 2, MobTactic.Melee, true),
        MobTypes.Creeper => new(type, 0.6, 1.8, 20, 25, MobTactic.Creeper, true),
        MobTypes.Enderman => new(type, 0.6, 2.9, 40, 7, MobTactic.Enderman, true),
        MobTypes.Slime => new(type, 0.6, 0.6, 1, 0, MobTactic.Melee, true), // scaled by size (metadata 16)
        MobTypes.Silverfish => new(type, 0.3, 0.7, 8, 1, MobTactic.Melee, true),
        MobTypes.PigZombie => new(type, 0.6, 1.8, 20, 5, MobTactic.Melee, false),
        MobTypes.Wolf => new(type, 0.8, 0.8, 8, 2, MobTactic.Melee, false),
        MobTypes.Pig => new(type, 0.9, 0.9, 10, 0, MobTactic.Melee, false),
        MobTypes.Cow => new(type, 0.9, 1.3, 10, 0, MobTactic.Melee, false),
        MobTypes.Sheep => new(type, 0.9, 1.3, 8, 0, MobTactic.Melee, false),
        MobTypes.Chicken => new(type, 0.3, 0.4, 4, 0, MobTactic.Melee, false),
        _ => new(type, 0.6, 1.8, 20, 0, MobTactic.Ignore, MobTypes.IsHostile(type)),
    };

    /// <summary>Slime size from metadata 16 (1, 2, 4); 1 if unknown.</summary>
    public static int SlimeSize(Entity e) => Math.Max(1, e.MetaInt(16) ?? 1);

    /// <summary>Creeper metadata 16: −1 idle, 1 fusing.</summary>
    public static bool IsCreeperFusing(Entity e) => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Creeper && (e.MetaInt(16) ?? -1) > 0;

    /// <summary>Wolf metadata 16 flags: 1 sitting, 2 angry, 4 tamed.</summary>
    public static bool IsTamedWolf(Entity e) => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Wolf && ((e.MetaInt(16) ?? 0) & 4) != 0;
    public static bool IsAngryWolf(Entity e) => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Wolf && ((e.MetaInt(16) ?? 0) & 2) != 0 && !IsTamedWolf(e);

    /// <summary>Hitbox of an entity at its feet position (players 0.6×1.8).</summary>
    public static AABB Hitbox(Entity e)
    {
        if (e.Kind != EntityKind.Mob) return AABB.ForPlayer(e.Position);
        var p = Profile(e.TypeId);
        double s = e.TypeId == MobTypes.Slime ? SlimeSize(e) : 1;
        return AABB.ForEntity(e.Position, p.Width * s, p.Height * s);
    }

    /// <summary>Hostile right now: hostile types (minus harmless size-1 slimes), angry wild wolves.</summary>
    public static bool IsThreat(Entity e)
    {
        if (e.Kind != EntityKind.Mob) return false;
        if (e.TypeId == MobTypes.Wolf) return IsAngryWolf(e);
        if (e.TypeId == MobTypes.Slime) return SlimeSize(e) > 1;
        var p = Profile(e.TypeId);
        return p.Hostile && p.Tactic != MobTactic.Ignore;
    }

    /// <summary>Distance from the eye to the nearest point of the hitbox grown by the 0.1 collision border (the vanilla
    /// mouse-over ray tests getCollisionBorderSize = 0.1).</summary>
    public static double ReachTo(Vec3d eye, AABB box)
    {
        var b = box.Grow(0.1, 0.1, 0.1);
        double dx = Math.Max(Math.Max(b.MinX - eye.X, 0), eye.X - b.MaxX);
        double dy = Math.Max(Math.Max(b.MinY - eye.Y, 0), eye.Y - b.MaxY);
        double dz = Math.Max(Math.Max(b.MinZ - eye.Z, 0), eye.Z - b.MaxZ);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Point on the hitbox to aim at (body center; endermen at the legs so the gaze stays off their middle).</summary>
    public static Vec3d AimPoint(Entity e)
    {
        var b = Hitbox(e);
        double y = e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Enderman ? b.MinY + 0.4 : b.MinY + (b.MaxY - b.MinY) * 0.6;
        return new Vec3d((b.MinX + b.MaxX) / 2, y, (b.MinZ + b.MaxZ) / 2);
    }

    /// <summary>
    /// EntityEnderman.shouldAttackPlayer: the player's look vector · (enderman bbox center − player eye) normalized
    /// &gt; 1 − 0.025 / distance. We keep a much wider cone (≥ <paramref name="minAngleDeg"/>) around that point.
    /// Returns a pitch that looks away (lower, or higher if already looking down), or the input when safe.
    /// </summary>
    public static float AvoidEndermanGaze(Vec3d eye, float yaw, float pitch, IEnumerable<Vec3d> endermanCenters, double minAngleDeg = 15)
    {
        foreach (var c in endermanCenters)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (!LooksAt(eye, yaw, pitch, c, minAngleDeg)) break;
                var (_, headPitch) = PlayerPhysics.YawPitchTo(eye, c);
                float down = headPitch + (float)minAngleDeg + 10, up = headPitch - (float)minAngleDeg - 10;
                pitch = down <= 90 && !LooksAt(eye, yaw, down, c, minAngleDeg) ? down : Math.Max(-90, up);
            }
        }
        return pitch;
    }

    public static bool LooksAt(Vec3d eye, float yaw, float pitch, Vec3d point, double minAngleDeg)
    {
        var to = point - eye;
        double d = to.Length;
        if (d < 1e-6 || d > 64) return false;
        double dot = PlayerPhysics.LookVector(yaw, pitch).Dot(to * (1 / d));
        double vanilla = 1 - 0.025 / d;
        double wide = Math.Cos(minAngleDeg * Math.PI / 180);
        return dot > Math.Min(vanilla, wide);
    }
}
