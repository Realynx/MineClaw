using MineClaw.Client.Versioning;
using MineClaw.Data;

namespace MineClaw.Client.Combat;

/// <summary>A weapon pick: <see cref="Slot"/> is a window-0 slot (36..44 hotbar, 9..35 main inventory).</summary>
public readonly record struct WeaponPick(int Slot, int ItemId, int Damage)
{
    public bool InHotbar => Slot is >= 36 and <= 44;
    public int HotbarIndex => Slot - 36;
    public bool IsHand => ItemId <= 0;
}

/// <summary>Chooses what to hold for a fight from the player's 45 window-0 slots.</summary>
public static class WeaponChoice
{
    /// <summary>Items that may never be swung: reserved/protected ids, plus tools about to break (losing them is worse).</summary>
    public static bool Usable(ItemStack s, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield)
    {
        if (s.IsEmpty) return true;
        if (reserved.Contains(s.Id) || neverWield.Contains(s.Id)) return false;
        var info = Items.Exists(s.Id) ? Items.Get(s.Id) : null;
        if (info is { MaxDamage: > 0 } && info.MaxDamage - s.Damage <= 2) return false;
        return true;
    }

    /// <summary>Damage of a full hit with the item under <paramref name="rules"/> (null = b1.8.1).</summary>
    private static int Dmg(int id, ICombatRules? rules) => rules?.WeaponDamage(Math.Max(0, id)) ?? CombatRules.WeaponDamage(id);

    /// <summary>A target takes a new full hit at most every 10 ticks (<c>EntityLivingBase.attackEntityFrom</c>: within
    /// <c>maxHurtResistantTime / 2</c> only damage above the last hit counts), so a cooldown shorter than that (the bare hand's 5)
    /// doesn't hit faster.</summary>
    public const int HurtResistTicks = 10;

    /// <summary>1.9+ damage per second of fully charged hits (charged damage / max(cooldown, hurt resistance)); null without a
    /// cooldown (b1.8.1: spam clicks, so the per-hit damage ranks). Bare hand 1 per 10 ticks = 2/s, a wooden pickaxe 2 per 16.7
    /// = 2.4/s, a wooden sword 4 per 12.5 = 6.4/s.</summary>
    public static double? Dps(int id, ICombatRules? rules)
    {
        if (rules is null) return null;
        int item = Math.Max(0, id);
        double cd = rules.AttackCooldownTicks(item);
        return cd > 0 ? rules.ChargedDamage(item, 1f) * 20.0 / Math.Max(cd, HurtResistTicks) : null;
    }

    /// <summary>Best damage (swords preferred on ties: they can block), in the hotbar if equal. With a 1.9+ cooldown
    /// (<paramref name="rules"/>), the best damage per second: a sword out-hits an axe over time though an axe hits harder.</summary>
    public static WeaponPick Best(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield, ICombatRules? rules = null)
    {
        WeaponPick? best = null;
        double bestScore = double.MinValue;
        double? handDps = Dps(0, rules);
        for (int i = 9; i < 45 && i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.IsEmpty || !Usable(s, reserved, neverWield)) continue;
            int dmg = Dmg(s.Id, rules);
            if (dmg <= 1) continue;
            double score = Dps(s.Id, rules) is { } dps
                ? (dps <= handDps ? double.MinValue : dps * 10 + (CombatRules.IsSword(s.Id) ? 2 : 0) + (i >= 36 ? 1 : 0))
                : dmg * 10 + (CombatRules.IsSword(s.Id) ? 5 : 0) + (i >= 36 ? 1 : 0);
            if (score == double.MinValue) continue;
            if (score > bestScore) { bestScore = score; best = new WeaponPick(i, s.Id, dmg); }
        }
        return best ?? Hand(slots, reserved, neverWield);
    }

    /// <summary>The weakest real weapon (damage &gt; 1), else the hand.</summary>
    public static WeaponPick Weakest(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield, ICombatRules? rules = null)
    {
        WeaponPick? pick = null;
        for (int i = 9; i < 45 && i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.IsEmpty || !Usable(s, reserved, neverWield)) continue;
            int dmg = Dmg(s.Id, rules);
            if (dmg <= 1) continue;
            if (pick is null || dmg < pick.Value.Damage || (dmg == pick.Value.Damage && i >= 36 && !pick.Value.InHotbar))
                pick = new WeaponPick(i, s.Id, dmg);
        }
        return pick ?? Hand(slots, reserved, neverWield);
    }

    /// <summary>A hotbar slot dealing 1 damage: empty first, else a plain item (not a tool, weapon or food).</summary>
    public static WeaponPick Hand(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield, ICombatRules? rules = null)
    {
        for (int i = 36; i < 45 && i < slots.Count; i++)
            if (slots[i].IsEmpty) return new WeaponPick(i, 0, 1);
        for (int i = 36; i < 45 && i < slots.Count; i++)
        {
            var s = slots[i];
            if (Dmg(s.Id, rules) == 1 && (!Items.Exists(s.Id) || Items.Get(s.Id).MaxDamage == 0) && Items.Food(s.Id) is null)
                return new WeaponPick(i, s.Id, 1);
        }
        // Every hotbar slot holds a tool or food: the least damaging one.
        int least = 36;
        for (int i = 37; i < 45 && i < slots.Count; i++)
            if (Dmg(slots[i].Id, rules) < Dmg(slots[least].Id, rules)) least = i;
        return new WeaponPick(least, slots.Count > least ? slots[least].Id : 0, Dmg(slots.Count > least ? slots[least].Id : 0, rules));
    }

    public static WeaponPick ForSpar(SparWeapon mode, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield,
        ICombatRules? rules = null) =>
        mode switch
        {
            SparWeapon.Best => Best(slots, reserved, neverWield, rules),
            SparWeapon.Weakest => Weakest(slots, reserved, neverWield, rules),
            _ => Hand(slots, reserved, neverWield, rules),
        };
}
