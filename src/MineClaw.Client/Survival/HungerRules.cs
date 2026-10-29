using MineClaw.Data;

namespace MineClaw.Client.Survival;

/// <summary>
/// b1.8.1 hunger mechanics (FoodStats / ItemFood / EntityPlayer of the b1.8 code base, cross-checked with the
/// Minecraft Wiki "Hunger" / "Food" history sections, which list the 1.11 reductions from these values):
/// <list type="bullet">
/// <item>food 0..20, saturation 0..food (starts at 5); eating adds hunger and hunger × modifier × 2 saturation, the
/// saturation capped at the new food level.</item>
/// <item>exhaustion: every 4.0 removes 1 saturation, or 1 food when saturation is 0 (not on peaceful). Costs: jump 0.2,
/// sprint-jump 0.8, attack 0.3, taking damage 0.3, block broken 0.025, sprinting 0.1/m, swimming 0.015/m,
/// walking/sneaking 0.01/m. Regeneration costs nothing (exhaustion for healing came in 1.6).</item>
/// <item>regeneration: +1 hp every 80 ticks while food ≥ 18.</item>
/// <item>starvation at food 0: −1 hp every 80 ticks; easy stops at 10 hp, normal at 1 hp, hard kills.</item>
/// <item>food ≤ 6: no sprinting.</item>
/// <item>eating takes 32 ticks (ItemFood.getMaxItemUseDuration); food is refused at 20 except the golden apple.</item>
/// <item>food poisoning (Hunger effect, 30 s, +0.025 exhaustion/tick = 15 exhaustion ≈ 3.75 food): rotten flesh 80%,
/// raw chicken 30%.</item>
/// </list>
/// </summary>
public static class HungerRules
{
    public const int MaxFood = 20;
    public const int RegenFood = 18;
    public const int SprintFood = 7;
    public const int EatTicks = 32;
    public const int RegenIntervalTicks = 80;
    /// <summary>Difficulty 1 (easy) starvation stops at this health.</summary>
    public const int EasyStarveFloor = 10;

    /// <summary>Health starvation stops at for a difficulty (FoodMetaData.java:44: damage while health &gt; 10, or
    /// difficulty ≥ 3, or health &gt; 1 on difficulty ≥ 2). Peaceful counts like easy (food never drops there, though).</summary>
    public static int StarvationFloor(int difficulty) => difficulty switch { <= 1 => EasyStarveFloor, 2 => 1, _ => 0 };

    /// <summary>Saturation gained by eating (before the cap at the new food level).</summary>
    public static float SaturationGain(int hunger, float modifier) => hunger * modifier * 2f;

    /// <summary>Food poisoning risk (rotten flesh, raw chicken).</summary>
    public static bool IsRisky(int itemId) => Items.FoodPoisonChance(itemId) > 0;

    /// <summary>Food poisoning risk of a stack: the damage value picks the food on 1.12.2 (fish 349:3 is a pufferfish:
    /// poison and nausea, while 349:0 is plain raw cod).</summary>
    public static bool IsRisky(int itemId, int damage) => Items.FoodPoisonChance(itemId, damage) > 0;
}
