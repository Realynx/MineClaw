using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>
/// 1.12.2 melee (multi-version P4), ported from the decompiled source:
/// <list type="bullet">
/// <item>Weapon attributes: the player's attack damage base 1.0 and attack speed base 4.0 (<c>EntityPlayer.applyEntityAttributes</c>)
/// plus the held item's operation-0 modifiers: <c>ItemSword</c> 3 + material damage, speed −2.4; <c>ItemTool</c> (pickaxe 1 / −2.8,
/// shovel 1.5 / −3.0) + material damage; <c>ItemAxe</c> its own tables (damage 6, 8, 8, 8, 6; speed −3.2, −3.2, −3.1, −3.0, −3.0
/// for wood, stone, iron, diamond, gold); <c>ItemHoe</c> damage 0, speed material damage + 1 − 4. Material damage
/// (<c>Item.ToolMaterial</c>): wood 0, stone 1, iron 2, diamond 3, gold 0.</item>
/// <item>The cooldown (<c>EntityPlayer.getCooldownPeriod</c> = (float)(1 / speed · 20); <c>getCooledAttackStrength(0.5F)</c> =
/// clamp((ticks + 0.5) / period)); <c>ticksSinceLastSwing</c> is reset by every attack, every arm swing the server sees
/// (<c>EntityPlayerMP.swingArm</c>) and a held-item change (<c>EntityPlayer.onUpdate</c>).</item>
/// <item><c>attackTargetEntityWithCurrentItem</c>: damage × (0.2 + 0.8·s²); s &gt; 0.9 enables the sprint knockback, the crit
/// (falling, off the ground, no ladder/water/blindness/riding, not sprinting; × 1.5) and the sweep (a sword, on the ground,
/// moved less than the AI speed this tick, neither crit nor sprint knockback: every living entity in the target's box grown by
/// (1, 0.25, 1) within 3 blocks takes 1 damage and a 0.4 knockback).</item>
/// <item>Shields (<c>EntityLivingBase.canBlockDamageSource</c>, <c>isActiveItemStackBlocking</c>): raised for 5 ticks, a hit from the
/// front (the source on the side the look points to) is stopped entirely; an axe (<c>EntityPlayer.blockUsingShield</c>, a player's
/// always, a mob's 25 %) puts the shield on a 100-tick cooldown (<c>disableShield</c>).</item>
/// <item>Damage taken: <c>EntityPlayer.attackEntityFrom</c> difficulty scaling (easy min(d/2 + 1, d), hard ×1.5) and
/// <c>util.CombatRules.getDamageAfterAbsorb</c> (f = 2 + toughness/4; armor' = clamp(armor − d/f, armor/5, 20); d·(1 − armor'/25)).</item>
/// </list>
/// </summary>
public sealed class Release1122CombatRules : ICombatRules
{
    public static readonly Release1122CombatRules Instance = new();

    public const int ShieldId = 442, TotemId = 449;
    public const double BaseAttackDamage = 1.0, BaseAttackSpeed = 4.0;
    /// <summary>The knockback of a hit (<c>attackEntityFrom</c> → <c>knockBack(attacker, 0.4F, …)</c>).</summary>
    public const float HitKnockback = 0.4f;

    private static readonly float[] MaterialDamage = [0f, 1f, 2f, 3f, 0f];
    private static readonly float[] AxeDamage = [6f, 8f, 8f, 8f, 6f];
    private static readonly float[] AxeSpeed = [-3.2f, -3.2f, -3.1f, -3.0f, -3.0f];

    private static ItemInfo? Info(int itemId) =>
        itemId > 0 && Release1122GameData.Instance.ItemExists(itemId) ? Release1122GameData.Instance.Item(itemId) : null;

    /// <summary>The held item's operation-0 modifiers (attack damage, attack speed) — float literals widened like the source.</summary>
    public static (double Damage, double Speed) ItemModifiers(int itemId)
    {
        if (Info(itemId) is not { } i || i.ToolTier is < 0 or > 4) return (0, 0);
        int m = i.ToolTier;
        return i.Tool switch
        {
            ToolType.Sword => (3.0f + MaterialDamage[m], -2.4000000953674316),
            ToolType.Pickaxe => (1.0f + MaterialDamage[m], -2.8f),
            ToolType.Shovel => (1.5f + MaterialDamage[m], -3.0f),
            ToolType.Axe => (AxeDamage[m], AxeSpeed[m]),
            ToolType.Hoe => (0, MaterialDamage[m] + 1.0f - 4.0f),
            _ => (0, 0),
        };
    }

    public double AttackDamage(int itemId) => BaseAttackDamage + ItemModifiers(itemId).Damage;
    public static double AttackSpeed(int itemId) => BaseAttackSpeed + ItemModifiers(itemId).Speed;
    public int WeaponDamage(int itemId) => (int)AttackDamage(itemId);

    /// <summary><c>getCooldownPeriod</c>: (float)(1 / attack speed · 20).</summary>
    public static float CooldownPeriod(int itemId) => (float)(1.0 / AttackSpeed(itemId) * 20.0);
    public double AttackCooldownTicks(int itemId) => CooldownPeriod(itemId);

    public float AttackStrength(int ticksSinceSwing, int itemId) => Math.Clamp((ticksSinceSwing + 0.5f) / CooldownPeriod(itemId), 0f, 1f);

    /// <summary>Ticks after a swing until the charge reaches <paramref name="min"/> (the first whole tick).</summary>
    public int TicksToCharge(int itemId, float min)
    {
        for (int t = 0; t < 400; t++) if (AttackStrength(t, itemId) >= min) return t;
        return 400;
    }

    public double ChargedDamage(int itemId, float strength) => (float)AttackDamage(itemId) * (0.2f + strength * strength * 0.8f);

    public int InvulnerabilityTicks => CombatRules.InvulnerabilityTicks;
    public double ServerReach => 6;
    public double ServerReachUnseen => 3;
    public bool SwordBlocks => false;

    public bool IsCritical(double motionY, bool onGround, bool onLadder, bool inWater) => motionY < 0 && !onGround && !onLadder && !inWater;

    public bool IsCriticalHit(in AttackContext a) =>
        a.Strength > 0.9f && a.FallDistance > 0 && !a.OnGround && !a.OnLadder && !a.InWater && !a.Blind && !a.Riding && a.TargetLiving && !a.Sprinting;

    public double CritDamage(double damage) => (float)damage * 1.5f;

    public bool IsSweep(in AttackContext a)
    {
        bool charged = a.Strength > 0.9f;
        bool sprintKnock = a.Sprinting && charged;
        return charged && !IsCriticalHit(a) && !sprintKnock && a.OnGround && a.WalkedThisTick < a.AiMoveSpeed
               && Info(a.HeldItem) is { Tool: ToolType.Sword };
    }

    public AABB SweepBox(AABB target) => target.Grow(1.0, 0.25, 1.0);
    public bool SwingBeforeAttack => false;

    public bool CanBlock(ItemStack main, ItemStack offhand) => main.Id == ShieldId || offhand.Id == ShieldId;
    public int BlockHand(ItemStack main, ItemStack offhand) => main.Id == ShieldId ? 0 : 1;
    public int BlockRaiseTicks => 5;
    public double BlockedDamage(double damage) => 0;
    public bool DisablesShield(int attackerHeld) => Info(attackerHeld) is { Tool: ToolType.Axe };
    public int ShieldDisableTicks => 100;
    public bool SavesFromDeath(int itemId) => itemId == TotemId;

    public double MonsterDamageToPlayer(double damage, int difficulty)
    {
        float d = (float)damage;
        return difficulty switch
        {
            <= 0 => 0,
            1 => Math.Min(d / 2.0f + 1.0f, d),
            2 => d,
            _ => d * 3.0f / 2.0f,
        };
    }

    /// <summary>ItemArmor.ArmorMaterial (boots, legs, chest, helmet): leather 1 2 3 1, chain 1 4 5 2, iron 2 5 6 2, gold 1 3 5 2,
    /// diamond 3 6 8 3 (toughness 2). Ids 298..317 = leather, chain, iron, diamond, gold × helmet, chest, legs, boots.</summary>
    public static (int Points, float Toughness) ArmorOf(int itemId)
    {
        if (itemId is < 298 or > 317) return (0, 0);
        int material = (itemId - 298) / 4, piece = (itemId - 298) % 4; // piece 0 helmet .. 3 boots
        int[] arr = material switch
        {
            0 => [1, 2, 3, 1], 1 => [1, 4, 5, 2], 2 => [2, 5, 6, 2], 3 => [3, 6, 8, 3], _ => [1, 3, 5, 2],
        };
        return (arr[3 - piece], material == 3 ? 2f : 0f);
    }

    public double DamageAfterArmor(double damage, IEnumerable<ItemStack> armor)
    {
        float points = 0, toughness = 0;
        foreach (var s in armor)
        {
            if (s.IsEmpty) continue;
            var (p, t) = ArmorOf(s.Id);
            points += p;
            toughness += t;
        }
        return DamageAfterAbsorb((float)damage, points, toughness);
    }

    /// <summary><c>util.CombatRules.getDamageAfterAbsorb</c>.</summary>
    public static float DamageAfterAbsorb(float damage, float totalArmor, float toughness)
    {
        float f = 2.0f + toughness / 4.0f;
        float g = Math.Clamp(totalArmor - damage / f, totalArmor * 0.2f, 20.0f);
        return damage * (1.0f - g / 25.0f);
    }

    /// <summary><c>EntityLivingBase.knockBack</c>: motion halved, then <paramref name="strength"/> away from the attacker; the
    /// vertical part only when the victim is on the ground (halved + strength, capped at 0.4).</summary>
    public static Vec3d Knockback(Vec3d current, Vec3d victim, Vec3d attacker, bool victimOnGround, float strength = HitKnockback)
    {
        double xr = attacker.X - victim.X, zr = attacker.Z - victim.Z;
        float f = MathF.Sqrt((float)(xr * xr + zr * zr));
        if (f < 1e-4f) { xr = 0.01; zr = 0; f = 0.01f; }
        double vx = current.X / 2.0 - xr / f * strength, vz = current.Z / 2.0 - zr / f * strength, vy = current.Y;
        if (victimOnGround)
        {
            vy = vy / 2.0 + strength;
            if (vy > 0.4000000059604645) vy = 0.4000000059604645;
        }
        return new Vec3d(vx, vy, vz);
    }
}
