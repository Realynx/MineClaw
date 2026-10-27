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

    /// <summary>Best damage (swords preferred on ties: they can block), in the hotbar if equal.</summary>
    public static WeaponPick Best(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield)
    {
        WeaponPick? best = null;
        double bestScore = double.MinValue;
        for (int i = 9; i < 45 && i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.IsEmpty || !Usable(s, reserved, neverWield)) continue;
            int dmg = CombatRules.WeaponDamage(s.Id);
            if (dmg <= 1) continue;
            double score = dmg * 10 + (CombatRules.IsSword(s.Id) ? 5 : 0) + (i >= 36 ? 1 : 0);
            if (score > bestScore) { bestScore = score; best = new WeaponPick(i, s.Id, dmg); }
        }
        return best ?? Hand(slots, reserved, neverWield);
    }

    /// <summary>The weakest real weapon (damage &gt; 1), else the hand.</summary>
    public static WeaponPick Weakest(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield)
    {
        WeaponPick? pick = null;
        for (int i = 9; i < 45 && i < slots.Count; i++)
        {
            var s = slots[i];
            if (s.IsEmpty || !Usable(s, reserved, neverWield)) continue;
            int dmg = CombatRules.WeaponDamage(s.Id);
            if (dmg <= 1) continue;
            if (pick is null || dmg < pick.Value.Damage || (dmg == pick.Value.Damage && i >= 36 && !pick.Value.InHotbar))
                pick = new WeaponPick(i, s.Id, dmg);
        }
        return pick ?? Hand(slots, reserved, neverWield);
    }

    /// <summary>A hotbar slot dealing 1 damage: empty first, else a plain item (not a tool, weapon or food).</summary>
    public static WeaponPick Hand(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield)
    {
        for (int i = 36; i < 45 && i < slots.Count; i++)
            if (slots[i].IsEmpty) return new WeaponPick(i, 0, 1);
        for (int i = 36; i < 45 && i < slots.Count; i++)
        {
            var s = slots[i];
            if (CombatRules.WeaponDamage(s.Id) == 1 && (!Items.Exists(s.Id) || Items.Get(s.Id).MaxDamage == 0) && Items.Food(s.Id) is null)
                return new WeaponPick(i, s.Id, 1);
        }
        // Every hotbar slot holds a tool or food: the least damaging one.
        int least = 36;
        for (int i = 37; i < 45 && i < slots.Count; i++)
            if (CombatRules.WeaponDamage(slots[i].Id) < CombatRules.WeaponDamage(slots[least].Id)) least = i;
        return new WeaponPick(least, slots.Count > least ? slots[least].Id : 0, CombatRules.WeaponDamage(slots.Count > least ? slots[least].Id : 0));
    }

    public static WeaponPick ForSpar(SparWeapon mode, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, IReadOnlySet<int> neverWield) =>
        mode switch
        {
            SparWeapon.Best => Best(slots, reserved, neverWield),
            SparWeapon.Weakest => Weakest(slots, reserved, neverWield),
            _ => Hand(slots, reserved, neverWield),
        };
}
