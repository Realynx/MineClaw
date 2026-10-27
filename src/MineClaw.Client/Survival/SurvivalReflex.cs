using MineClaw.Data;

namespace MineClaw.Client.Survival;

/// <summary>Why the bot wants to eat now.</summary>
public enum EatReason { None, Hungry, Regenerate, Urgent }

/// <summary>A chosen food stack: item id, inventory slot (window 0), hunger restored, points wasted over 20, poison risk.</summary>
public readonly record struct FoodPick(int ItemId, int Slot, int Hunger, int Waste, bool Risky);

/// <summary>A health threshold crossing (once per episode).</summary>
public readonly record struct HealthAlert(bool Critical, string Message);

/// <summary>
/// The survival "subconscious": pure decisions over health/food/inventory, called from the client's packet and tick
/// threads (state is locked). The client applies them: auto-eat in safe windows, cancel risky actions and retreat at
/// low health, refuse to start risky actions (<see cref="GateReason"/>), and one debounced hungry event when there is
/// no food (<see cref="CheckFood"/>).
/// </summary>
public sealed class SurvivalReflex
{
    private readonly object _sync = new();
    private bool _low, _critical;
    private int _hungerLevel;

    public SurvivalReflex(SurvivalOptions? options = null) => Options = options ?? new SurvivalOptions();

    public SurvivalOptions Options { get; set; }

    /// <summary>Optional cheap memory lookup ("chest at 3,64,2 had bread") appended to the out-of-food event.</summary>
    public Func<string?>? FoodHint { get; set; }

    /// <summary>Current out-of-food severity (0 none, 1 hungry, 2 starving, 3 food 0).</summary>
    public int HungerLevel { get { lock (_sync) return _hungerLevel; } }

    public void Reset()
    {
        lock (_sync) { _low = _critical = false; _hungerLevel = 0; }
    }

    // ------------------------------------------------------------------ eating

    /// <summary>Whether to eat at (health, food): hungry at ≤ <see cref="SurvivalOptions.EatAtFood"/>, or hurt below the
    /// regeneration line (food &lt; 18), urgent when health is low or food is at the starving line.</summary>
    public static EatReason WantsEat(int health, int food, SurvivalOptions o)
    {
        if (food >= HungerRules.MaxFood || health <= 0) return EatReason.None;
        if ((health <= o.LowHealth && food < HungerRules.RegenFood) || food <= o.StarvingFood) return EatReason.Urgent;
        if (food <= o.EatAtFood) return EatReason.Hungry;
        if (o.EatToRegenerate && health < 20 && food < HungerRules.RegenFood) return EatReason.Regenerate;
        return EatReason.None;
    }

    /// <summary>
    /// Best food for the deficit from window-0 slots 9..44 (never reserved items): most food + saturation, least waste;
    /// rotten flesh / raw chicken only when starving (food ≤ <see cref="SurvivalOptions.StarvingFood"/>) and nothing
    /// else is there; golden apples only at critical health. Null when nothing suitable.
    /// </summary>
    public static FoodPick? PickFood(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, int health, int food, SurvivalOptions o)
    {
        int deficit = HungerRules.MaxFood - food;
        if (deficit <= 0) return null;
        bool urgent = health <= o.LowHealth || food <= o.StarvingFood;
        FoodPick? best = null;
        double bestScore = double.MinValue;
        for (int i = 9; i < Math.Min(45, slots.Count); i++)
        {
            var s = slots[i];
            if (s.IsEmpty || reserved.Contains(s.Id) || Items.Food(s.Id) is not { } f) continue;
            if (s.Id == Items.GoldenApple && o.SaveGoldenApples && health > o.CriticalHealth) continue;
            bool risky = HungerRules.IsRisky(s.Id);
            int gain = Math.Min(f.Hunger, deficit), waste = f.Hunger - gain;
            double sat = Math.Min(HungerRules.SaturationGain(f.Hunger, f.Saturation), food + gain);
            double score = gain * 2 + sat - waste * (urgent ? 0.5 : 3) - (risky ? 100 : 0) + (i >= 36 ? 0.1 : 0); // hotbar: no swap
            if (score > bestScore) { bestScore = score; best = new FoodPick(s.Id, i, f.Hunger, waste, risky); }
        }
        if (best is { Risky: true } && food > o.StarvingFood) return null;
        return best;
    }

    /// <summary>Food to eat right now, or null: <see cref="WantsEat"/> plus the waste rule for regeneration-only meals
    /// (eat a big meal at food 15..17 only when it wastes ≤ <see cref="SurvivalOptions.MaxWaste"/> or health ≤ 14).</summary>
    public static FoodPick? EatNow(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, int health, int food, SurvivalOptions o)
    {
        var why = WantsEat(health, food, o);
        if (why == EatReason.None) return null;
        var pick = PickFood(slots, reserved, health, food, o);
        if (pick is { } p && why == EatReason.Regenerate && p.Waste > o.MaxWaste && health > 14) return null;
        return pick;
    }

    /// <summary>Any edible, non-reserved item at all (including risky ones and golden apples).</summary>
    public static bool HasAnyFood(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved)
    {
        for (int i = 9; i < Math.Min(45, slots.Count); i++)
            if (!slots[i].IsEmpty && !reserved.Contains(slots[i].Id) && Items.Food(slots[i].Id) is not null) return true;
        return false;
    }

    /// <summary>A safe (non-poisoning) edible, non-reserved item.</summary>
    public static bool HasSafeFood(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved)
    {
        for (int i = 9; i < Math.Min(45, slots.Count); i++)
        {
            var s = slots[i];
            if (!s.IsEmpty && !reserved.Contains(s.Id) && Items.Food(s.Id) is not null && !HungerRules.IsRisky(s.Id)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ health

    /// <summary>Call on every health update. Returns an alert the first time health crosses the low / critical line;
    /// re-arms only after health rises <see cref="SurvivalOptions.Hysteresis"/> above it.</summary>
    public HealthAlert? OnHealth(int health, int food, bool hasFood)
    {
        var o = Options;
        lock (_sync)
        {
            if (health <= 0) return null;
            if (health > o.LowHealth + o.Hysteresis) _low = false;
            if (health > o.CriticalHealth + o.Hysteresis) _critical = false;
            string foodText = hasFood ? $"food {food}" : $"food {food}, no food!";
            if (health <= o.CriticalHealth && !_critical)
            {
                _critical = _low = true;
                return new HealthAlert(true, $"critical hp {health}/20 ({foodText}): stopped, retreating inward" + (hasFood ? " to eat" : ""));
            }
            if (health <= o.LowHealth && !_low)
            {
                _low = true;
                return new HealthAlert(false, $"low health {health}/20 ({foodText}): risky actions cancelled" + (hasFood ? ", eating" : ""));
            }
            return null;
        }
    }

    /// <summary>Health is at or below the low line (the episode is still open).</summary>
    public bool IsLow(int health) => health > 0 && health <= Options.LowHealth;

    // ------------------------------------------------------------------ out of food

    /// <summary>
    /// The debounced "hungry and no food" signal: severity 1 hungry (food ≤ EatAtFood), 2 starving (≤ StarvingFood),
    /// 3 food 0 (starvation damage). Returned only when the severity increases; it resets once food is back above
    /// EatAtFood or something edible is in the inventory.
    /// </summary>
    public DangerEvent? CheckFood(int food, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, int difficulty = 1)
    {
        var o = Options;
        bool safe = HasSafeFood(slots, reserved), any = HasAnyFood(slots, reserved);
        int level = food > o.EatAtFood ? 0 : food <= 0 ? 3 : food <= o.StarvingFood ? 2 : 1;
        // Risky food is eaten from the starving line on, so it only counts as "no food" while not starving yet.
        bool usable = safe || (any && level >= 2);
        lock (_sync)
        {
            if (level == 0 || usable) { _hungerLevel = 0; return null; }
            if (level <= _hungerLevel) return null;
            _hungerLevel = level;
        }
        string what = any ? "only risky food (rotten flesh/raw chicken)" : "no food in inventory";
        string msg = level switch
        {
            1 => $"hungry: food {food}, {what}",
            2 => $"starving: food {food}, {what} (no sprint, no regen)",
            _ => $"starving: food 0, losing hp (stops at {HungerRules.StarvationFloor(difficulty)}), {what}",
        };
        try
        {
            if (FoodHint?.Invoke() is { Length: > 0 } hint) msg += "; memory: " + (hint.Length > 100 ? hint[..100] : hint);
        }
        catch { /* a hint is optional */ }
        return new DangerEvent(DangerKind.Hunger, msg) { Level = level, Critical = level >= 3 };
    }

    // ------------------------------------------------------------------ gating

    /// <summary>
    /// Why a risky action (<paramref name="what"/>: "bridge", "fight", "spar", "mining trip") must not start now, or null.
    /// <paramref name="hasFood"/> = safe food in the inventory (eaten first when hungry; then only health matters).
    /// </summary>
    public static string? GateReason(string what, int health, int food, bool hasFood, SurvivalOptions o)
    {
        if (!o.Enabled) return null;
        if (health < o.GateMinHealth)
            return $"hp {health}/20 too low to {what} (need {o.GateMinHealth})"
                   + (food >= HungerRules.RegenFood ? "; regenerating" : hasFood ? "; eating to regenerate" : $" (food {food}, no food)");
        if (food < o.GateMinFood && !hasFood) return $"too hungry to {what} (food {food}, no food)";
        return null;
    }

    // ------------------------------------------------------------------ status

    /// <summary>The compact one-line health part of the state header: "hp 14/20 food 9 (no food!)".</summary>
    public static string HeaderText(int health, int food, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, SurvivalOptions? o = null)
    {
        o ??= new SurvivalOptions();
        string s = $"hp {health}/20 food {food}";
        bool safe = HasSafeFood(slots, reserved);
        if (food <= o.EatAtFood && !safe)
            s += food <= 0 ? " (starving, no food!)" : HasAnyFood(slots, reserved) ? " (only risky food!)" : " (no food!)";
        else if (food <= o.StarvingFood) s += " (hungry)";
        if (health > 0 && health <= o.CriticalHealth) s += " CRITICAL";
        else if (health > 0 && health <= o.LowHealth) s += " LOW";
        return s;
    }
}
