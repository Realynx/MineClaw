using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

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
            if (ScoreFood(slots[i], i, reserved, health, food, deficit, urgent, o) is not { } c) continue;
            if (c.Score > bestScore) { bestScore = c.Score; best = c.Pick; }
        }
        if (best is { Risky: true } && food > o.StarvingFood) return null;
        return best;
    }

    /// <summary>A stack's worth as the meal: most food + saturation, least waste, risky food last, the hotbar a hair ahead (no swap);
    /// null when it isn't food to pick (empty, reserved, not food, a golden apple kept for critical health).</summary>
    private static (FoodPick Pick, double Score)? ScoreFood(ItemStack s, int slot, IReadOnlySet<int> reserved, int health, int food, int deficit,
        bool urgent, SurvivalOptions o)
    {
        if (s.IsEmpty || reserved.Contains(s.Id) || Items.Food(s.Id, s.Damage) is not { } f) return null;
        if (s.Id == Items.GoldenApple && o.SaveGoldenApples && health > o.CriticalHealth) return null;
        bool risky = HungerRules.IsRisky(s.Id, s.Damage);
        int gain = Math.Min(f.Hunger, deficit), waste = f.Hunger - gain;
        double sat = Math.Min(HungerRules.SaturationGain(f.Hunger, f.Saturation), food + gain);
        double score = gain * 2 + sat - waste * (urgent ? 0.5 : 3) - (risky ? 100 : 0) + (slot >= 36 ? 0.1 : 0);
        return (new FoodPick(s.Id, slot, f.Hunger, waste, risky), score);
    }

    /// <summary>Food to eat right now, or null: <see cref="WantsEat"/> plus the waste rule for regeneration-only meals
    /// (eat a big meal at food 15..17 only when it wastes ≤ <see cref="SurvivalOptions.MaxWaste"/> or health ≤ 14). With the
    /// version's hunger rules and the saturation: a 1.12.2 top-up (hurt at food 18..19 with no saturation: food 20 + saturation
    /// switches on the fast regeneration) counts as a regeneration meal.</summary>
    public static FoodPick? EatNow(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, int health, int food, SurvivalOptions o,
        IHungerRules? rules = null, float saturation = 0)
    {
        var why = WantsEat(health, food, o);
        if (why == EatReason.None && o.EatToRegenerate && rules is not null && rules.WantsTopUp(health, food, saturation)) why = EatReason.Regenerate;
        if (why == EatReason.None) return null;
        var pick = PickFood(slots, reserved, health, food, o);
        if (pick is { } p && why == EatReason.Regenerate && p.Waste > o.MaxWaste && health > 14) return null;
        return pick;
    }

    /// <summary>Any edible, non-reserved item at all (including risky ones and golden apples).</summary>
    public static bool HasAnyFood(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved)
    {
        for (int i = 9; i < Math.Min(45, slots.Count); i++)
            if (!slots[i].IsEmpty && !reserved.Contains(slots[i].Id) && Items.Food(slots[i].Id, slots[i].Damage) is not null) return true;
        return false;
    }

    /// <summary>A safe (non-poisoning) edible, non-reserved item.</summary>
    public static bool HasSafeFood(IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved)
    {
        for (int i = 9; i < Math.Min(45, slots.Count); i++)
        {
            var s = slots[i];
            if (!s.IsEmpty && !reserved.Contains(s.Id) && Items.Food(s.Id, s.Damage) is not null && !HungerRules.IsRisky(s.Id, s.Damage)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ health

    /// <summary>Call on every health update. Returns an alert the first time health crosses the low / critical line;
    /// re-arms only after health rises <see cref="SurvivalOptions.Hysteresis"/> above it.</summary>
    /// <param name="threat">A hostile on the body right now ("zombie 2m away"), or null: then the low alert says what the reflexes do
    /// about it instead of "work goes on" (live 10-29: clone 69 kept chopping at 8/20 with a zombie hitting it).</param>
    /// <param name="underWater">The head is under water (or the drowning reflex is surfacing): that is what happens next.</param>
    public HealthAlert? OnHealth(int health, int food, bool hasFood, string? threat = null, bool underWater = false)
    {
        var o = Options;
        lock (_sync)
        {
            if (health <= 0) return null;
            RearmHealthAlerts(health, o);
            string foodText = hasFood ? $"food {food}" : $"food {food}, no food!";
            if (health <= o.CriticalHealth && !_critical)
            {
                _critical = _low = true;
                return CriticalAlert(health, foodText, hasFood, threat, underWater);
            }
            if (health <= o.LowHealth && !_low)
            {
                _low = true;
                return LowAlert(health, foodText, hasFood, threat, underWater);
            }
            return null;
        }
    }

    /// <summary>An alert re-arms once health rises <see cref="SurvivalOptions.Hysteresis"/> above its line.</summary>
    private void RearmHealthAlerts(int health, SurvivalOptions o)
    {
        if (health > o.LowHealth + o.Hysteresis) _low = false;
        if (health > o.CriticalHealth + o.Hysteresis) _critical = false;
    }

    /// <summary>A normal world has no island interior: the reflexes get away from the mobs or box in.</summary>
    private static HealthAlert CriticalAlert(int health, string foodText, bool hasFood, string? threat, bool underWater)
    {
        string retreat = underWater ? "head under water: surfacing"
            : WorldMode.Current.HasVoidBelow ? "retreating inward" : threat is null ? "getting to safety" : $"{threat}: boxing in or getting away";
        return new HealthAlert(true, $"critical hp {health}/20 ({foodText}): stopped, {retreat}" + (hasFood ? " to eat" : ""));
    }

    private static HealthAlert LowAlert(int health, string foodText, bool hasFood, string? threat, bool underWater)
    {
        string rest = underWater ? "head under water: surfacing first"
            : threat is null ? "work goes on" : $"{threat}: fighting back only if I can win, else boxing in";
        return new HealthAlert(false, $"low health {health}/20 ({foodText}): no fights/spars until it recovers, {rest}" + (hasFood ? ", eating" : ""));
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
        int level = HungerLevelOf(food, o);
        // Risky food is eaten from the starving line on, so it only counts as "no food" while not starving yet.
        bool usable = safe || (any && level >= 2);
        lock (_sync)
        {
            if (level == 0 || usable) { _hungerLevel = 0; return null; }
            if (level <= _hungerLevel) return null;
            _hungerLevel = level;
        }
        string what = any ? "only risky food (rotten flesh/raw chicken)" : "no food in inventory";
        string msg = WithFoodHint(HungerMessage(level, food, what, difficulty));
        return new DangerEvent(DangerKind.Hunger, msg) { Level = level, Critical = level >= 3 };
    }

    /// <summary>0 fed, 1 hungry (≤ EatAtFood), 2 starving (≤ StarvingFood), 3 food 0.</summary>
    private static int HungerLevelOf(int food, SurvivalOptions o) => food > o.EatAtFood ? 0 : food <= 0 ? 3 : food <= o.StarvingFood ? 2 : 1;

    private static string HungerMessage(int level, int food, string what, int difficulty) => level switch
    {
        1 => $"hungry: food {food}, {what}",
        2 => $"starving: food {food}, {what} (no sprint, no regen)",
        _ => $"starving: food 0, losing hp (stops at {HungerRules.StarvationFloor(difficulty)}), {what}",
    };

    /// <summary>The message plus what memory knows about food (at most 100 chars; a hint is optional).</summary>
    private string WithFoodHint(string msg)
    {
        try
        {
            if (FoodHint?.Invoke() is { Length: > 0 } hint) msg += "; memory: " + (hint.Length > 100 ? hint[..100] : hint);
        }
        catch { /* a hint is optional */ }
        return msg;
    }

    // ------------------------------------------------------------------ gating

    /// <summary>
    /// Why a risky action (<paramref name="what"/>: "bridge", "fight", "spar", "mine 32 blocks") must not start now, or null.
    /// <paramref name="hasFood"/> = safe food in the inventory (eaten first when hungry; then only health matters).
    /// The user's rule (and b1.8.1's): nothing stops a pickaxe at low hp or no food. Only fights/spars are gated (hp and food);
    /// void-edge work (bridging, island expansion) only at critical hp; mining, digging, chopping, placing never (live 10-27:
    /// clone 67 at 7/20 with no food, "tools refuse mining below HP 10").
    /// </summary>
    public static string? GateReason(string what, int health, int food, bool hasFood, SurvivalOptions o, bool edge = false,
        IReadOnlyDictionary<int, StatusEffect>? effects = null)
    {
        if (!o.Enabled) return null;
        if (NeedsFood(what)) return FightGateReason(what, health, food, hasFood, o, effects);
        if ((edge || IsEdgeWork(what)) && health <= o.EdgeMinHealth)
            return $"hp {health}/20 too low to {what} over the void (need more than {o.EdgeMinHealth})"
                   + (hasFood ? "; eating to regenerate" : food >= HungerRules.RegenFood ? "; regenerating" : "");
        return null;
    }

    /// <summary>Fights and spars: an effect that drains or kills, low health, or hunger with no food stops them.</summary>
    private static string? FightGateReason(string what, int health, int food, bool hasFood, SurvivalOptions o,
        IReadOnlyDictionary<int, StatusEffect>? effects)
    {
        if (EffectGateReason(what, health, o, effects) is { } fx) return fx;
        if (health < o.GateMinHealth)
            return $"hp {health}/20 too low to {what} (need {o.GateMinHealth})"
                   + (food >= HungerRules.RegenFood ? "; regenerating" : hasFood ? "; eating to regenerate" : $" (food {food}, no food)");
        if (food < o.GateMinFood && !hasFood) return $"too hungry to {what} (food {food}, no food)";
        return null;
    }

    /// <summary>Wither can kill and poison drains to 1 hp: no fight while either is on and health is short of the gate plus a margin.</summary>
    private static string? EffectGateReason(string what, int health, SurvivalOptions o, IReadOnlyDictionary<int, StatusEffect>? effects)
    {
        if (effects is null) return null;
        if (health < o.GateMinHealth + 6 && Active(effects, PotionEffects.Wither) is { } wi)
            return $"hp {health}/20 and withering ({wi / 20}s left): no {what} now";
        if (health < o.GateMinHealth + 4 && Active(effects, PotionEffects.Poison) is { } po)
            return $"hp {health}/20 and poisoned ({po / 20}s left): no {what} now";
        return null;
    }

    /// <summary>Work at a void edge where a knock or a misstep at critical hp is a death: bridging, island expansion.</summary>
    public static bool IsEdgeWork(string what) =>
        what.StartsWith("bridge", StringComparison.OrdinalIgnoreCase) || what.StartsWith("expand", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a started action stays "risky" for the low-health reflex (cancelled at low hp): fights and spars only.</summary>
    public static bool CancelAtLowHealth(string what) => NeedsFood(what);

    /// <summary>
    /// Vanilla b1.8.1 has no hunger penalty on mining, digging, placing or bridging, so only actions where low food
    /// means no health regen mid-danger (fights, spars) are food-gated; everything else is gated on health alone.
    /// </summary>
    public static bool NeedsFood(string what) =>
        what.StartsWith("fight", StringComparison.OrdinalIgnoreCase) || what.StartsWith("spar", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ status

    /// <summary>Ticks left of an active effect, or null.</summary>
    private static int? Active(IReadOnlyDictionary<int, StatusEffect> effects, int id) =>
        effects.TryGetValue(id, out var fx) && fx.RemainingTicks(DateTime.UtcNow) is var left and > 0 ? left : null;

    /// <summary>The effects that change survival decisions, as facts: "poisoned 12s (stops at 1 hp)", "withering 8s (can kill)",
    /// "regenerating", "fire resistant", "water breathing", "night vision", "hunger effect"; empty when none.</summary>
    public static string EffectFacts(IReadOnlyDictionary<int, StatusEffect>? effects)
    {
        if (effects is null || effects.Count == 0) return "";
        var parts = new List<string>(4);
        foreach (var (id, name, timed, note) in EffectLabels)
            if (Active(effects, id) is { } ticks) parts.Add(name + (timed ? Seconds(ticks) : "") + note);
        return string.Join(", ", parts);
    }

    /// <summary>The effects <see cref="EffectFacts"/> names, in its order: the name, whether the time left follows it, and a note.</summary>
    private static readonly (int Id, string Name, bool Timed, string Note)[] EffectLabels =
    [
        (PotionEffects.Poison, "poisoned", true, " (stops at 1 hp)"), (PotionEffects.Wither, "withering", true, " (can kill)"),
        (PotionEffects.Regeneration, "regenerating", true, ""), (PotionEffects.Hunger, "hunger effect", true, ""),
        (PotionEffects.FireResistance, "fire resistant", true, ""), (PotionEffects.WaterBreathing, "water breathing", true, ""),
        (PotionEffects.NightVision, "night vision", false, ""), (PotionEffects.Blindness, "blind", false, ""),
        (PotionEffects.Slowness, "slowed", false, ""), (PotionEffects.MiningFatigue, "mining fatigue", false, ""),
    ];

    /// <summary>" 12s" (an endless effect: nothing).</summary>
    private static string Seconds(int ticks) => ticks >= short.MaxValue ? "" : $" {ticks / 20}s";

    /// <summary>The compact one-line health part of the state header: "hp 14/20 food 9 (no food!)", plus the effect facts
    /// ("hp 9/20 food 18 poisoned 6s (stops at 1 hp)") when the version sends effects.</summary>
    public static string HeaderText(int health, int food, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, SurvivalOptions? o,
        IReadOnlyDictionary<int, StatusEffect>? effects)
    {
        var s = HeaderText(health, food, slots, reserved, o);
        var fx = EffectFacts(effects);
        return fx.Length == 0 ? s : s + " " + fx;
    }

    /// <summary>The compact one-line health part of the state header: "hp 14/20 food 9 (no food!)".</summary>
    public static string HeaderText(int health, int food, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, SurvivalOptions? o = null)
    {
        o ??= new SurvivalOptions();
        return $"hp {health}/20 food {food}" + FoodNote(food, slots, reserved, o) + HealthNote(health, o);
    }

    private static string FoodNote(int food, IReadOnlyList<ItemStack> slots, IReadOnlySet<int> reserved, SurvivalOptions o)
    {
        if (food <= o.EatAtFood && !HasSafeFood(slots, reserved))
            return food <= 0 ? " (starving, no food!)" : HasAnyFood(slots, reserved) ? " (only risky food!)" : " (no food!)";
        return food <= o.StarvingFood ? " (hungry)" : "";
    }

    private static string HealthNote(int health, SurvivalOptions o)
    {
        if (health > 0 && health <= o.CriticalHealth) return " CRITICAL";
        return health > 0 && health <= o.LowHealth ? " LOW" : "";
    }
}
