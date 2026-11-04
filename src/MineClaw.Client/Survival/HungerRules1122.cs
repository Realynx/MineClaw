using MineClaw.Client.Versioning;

namespace MineClaw.Client.Survival;

/// <summary>
/// 1.12.2 hunger (multi-version P4), ported from the decompiled <c>FoodStats</c> and the <c>addExhaustion</c> call sites:
/// <list type="bullet">
/// <item><c>FoodStats.onUpdate</c>: exhaustion over 4 costs 1 saturation, else 1 food (not on peaceful); with natural regeneration,
/// food 20 and saturation &gt; 0 heal min(sat, 6)/6 every 10 ticks at an exhaustion of min(sat, 6); food ≥ 18 heals 1 every 80 ticks
/// at 6 exhaustion; food 0 hurts 1 every 80 ticks while health &gt; 10, or on hard, or health &gt; 1 on normal. Exhaustion is capped
/// at 40 (<c>addExhaustion</c>).</item>
/// <item>Exhaustion (the 1.11 values): jump 0.05, sprint-jump 0.2 (<c>EntityPlayer.jump</c>), attack 0.1
/// (<c>attackTargetEntityWithCurrentItem</c>), damage taken 0.1 (<c>DamageSource.hungerDamage</c>), a block broken 0.005
/// (<c>Block.harvestBlock</c>), sprinting 0.1/m, swimming 0.01/m, walking and sneaking 0 (<c>addMovementStat</c>), the hunger effect
/// 0.005 × level per tick (<c>Potion.performEffect</c>).</item>
/// <item>No sprinting at food ≤ 6; eating takes 32 ticks; <c>addStats</c> as b1.8.1 (saturation = hunger · modifier · 2, capped at food).</item>
/// </list>
/// </summary>
public sealed class Release1122HungerRules : IHungerRules
{
    public static readonly Release1122HungerRules Instance = new();

    public int MaxFood => 20;
    public int RegenFood => 18;
    public int SprintFood => 7;
    public int EatTicks => 32;
    public int RegenIntervalTicks => 80;
    public bool RegenCostsExhaustion => true;
    public int FastRegenFood => 20;
    public int StarvationFloor(int difficulty) => HungerRules.StarvationFloor(difficulty); // the same rule as b1.8.1 (FoodStats.onUpdate)
    public float SaturationGain(int hunger, float modifier) => hunger * modifier * 2.0f;

    public float Exhaustion(ExhaustionCause cause) => cause switch
    {
        ExhaustionCause.Jump => 0.05f, ExhaustionCause.SprintJump => 0.2f, ExhaustionCause.Attack => 0.1f, ExhaustionCause.Damage => 0.1f,
        ExhaustionCause.BlockBroken => 0.005f, ExhaustionCause.SprintMeter => 0.1f, ExhaustionCause.SwimMeter => 0.01f,
        _ => 0f,
    };

    /// <summary>Resting: at food 20 a saturated body heals 1 hp per 10 ticks while saturation ≥ 6 (min(sat, 6)/6 per 10 ticks), so
    /// 120/min, slowing as it drains; food 18..19 (or 20 without saturation) 15/min.</summary>
    public double RegenPerMinute(int food, float saturation)
    {
        if (food >= FastRegenFood && saturation > 0) return 120.0 * Math.Min(saturation, 6f) / 6.0;
        return food >= RegenFood ? 1200.0 / RegenIntervalTicks : 0;
    }

    /// <summary>Hurt (≤ 14) and one meal short of the saturated fast regeneration: eat (the waste rule still applies).</summary>
    public bool WantsTopUp(int health, int food, float saturation) => health <= 14 && food >= RegenFood && food < MaxFood && saturation < 1f;
}

/// <summary>
/// A tick-exact port of 1.12.2 <c>FoodStats</c> for predictions and tests: the server owns the real one (the client only sees
/// Update Health), so the survival reflex uses it to know what regeneration and starvation will do.
/// </summary>
public sealed class FoodStats1122
{
    public int Food { get; set; } = 20;
    public float Saturation { get; set; } = 5.0f;
    public float Exhaustion { get; set; }
    public int Timer { get; private set; }
    public float Health { get; set; } = 20f;
    public float MaxHealth { get; set; } = 20f;
    public int Difficulty { get; set; } = 2;
    public bool NaturalRegeneration { get; set; } = true;

    /// <summary><c>addExhaustion</c>: capped at 40.</summary>
    public void AddExhaustion(float e) => Exhaustion = Math.Min(Exhaustion + e, 40.0f);

    /// <summary><c>addStats</c>.</summary>
    public void Eat(int hunger, float modifier)
    {
        Food = Math.Min(hunger + Food, 20);
        Saturation = Math.Min(Saturation + hunger * modifier * 2.0f, Food);
    }

    private bool ShouldHeal => Health > 0 && Health < MaxHealth;

    /// <summary>One <c>onUpdate</c>.</summary>
    public void Tick()
    {
        SpendExhaustion();
        if (NaturalRegeneration && Saturation > 0.0f && ShouldHeal && Food >= 20) SaturatedHeal();
        else if (NaturalRegeneration && Food >= 18 && ShouldHeal) FoodHeal();
        else if (Food <= 0) Starve();
        else Timer = 0;
    }

    /// <summary>Every 4 exhaustion costs a saturation point, or (none left, not peaceful) a food point.</summary>
    private void SpendExhaustion()
    {
        if (Exhaustion <= 4.0f) return;
        Exhaustion -= 4.0f;
        if (Saturation > 0.0f) Saturation = Math.Max(Saturation - 1.0f, 0.0f);
        else if (Difficulty != 0) Food = Math.Max(Food - 1, 0);
    }

    /// <summary>Full food with saturation: every 10 ticks heal by the saturation spent (at most 6 → 1 hp).</summary>
    private void SaturatedHeal()
    {
        if (++Timer < 10) return;
        float f = Math.Min(Saturation, 6.0f);
        Health = Math.Min(MaxHealth, Health + f / 6.0f);
        AddExhaustion(f);
        Timer = 0;
    }

    /// <summary>Food 18+: 1 hp every 80 ticks for 6 exhaustion.</summary>
    private void FoodHeal()
    {
        if (++Timer < 80) return;
        Health = Math.Min(MaxHealth, Health + 1.0f);
        AddExhaustion(6.0f);
        Timer = 0;
    }

    /// <summary>Food 0: 1 hp every 80 ticks, down to 10 (easy), 1 (normal) or death (hard).</summary>
    private void Starve()
    {
        if (++Timer < 80) return;
        if (Health > 10.0f || Difficulty >= 3 || (Health > 1.0f && Difficulty == 2))
            Health -= 1.0f; // STARVE bypasses armor: its hungerDamage is 0 (DamageSource.setDamageBypassesArmor)
        Timer = 0;
    }
}
