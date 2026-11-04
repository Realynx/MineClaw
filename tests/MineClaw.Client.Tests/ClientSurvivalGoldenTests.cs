using System.Globalization;
using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Equivalence goldens for the survival and weapon rules the complexity refactor (WP05) reshaped, taken from the implementations
/// before: the action gate (every kind × health × food × food on hand × edge × effects), the food pick and the eat decision over
/// seeded inventories, the low/critical health alerts and the hunger signal over seeded sequences, the effect facts and the header
/// line, 1.12.2's FoodStats tick over seeded states, the weapon picks (best, weakest, hand; with and without the 1.9 cooldown) and
/// the mob profiles; both versions' data. Model-facing text, so every string is pinned.
/// </summary>
public class ClientSurvivalGoldenTests
{
    private static readonly IGameData[] Versions = [Beta181GameData.Instance, Release1122GameData.Instance];
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static Dictionary<int, StatusEffect>? Effects(int k)
    {
        var now = DateTime.UtcNow;
        StatusEffect E(int id, int ticks) => new(id, 0, ticks, now);
        return k switch
        {
            0 => null,
            1 => new(),
            2 => new() { [PotionEffects.Poison] = E(PotionEffects.Poison, 2010) },
            3 => new() { [PotionEffects.Wither] = E(PotionEffects.Wither, 2010) },
            4 => new() { [PotionEffects.Poison] = E(PotionEffects.Poison, 2010), [PotionEffects.Wither] = E(PotionEffects.Wither, short.MaxValue) },
            5 => new() { [PotionEffects.Regeneration] = E(PotionEffects.Regeneration, 2010), [PotionEffects.Hunger] = E(PotionEffects.Hunger, 610),
                [PotionEffects.FireResistance] = E(PotionEffects.FireResistance, short.MaxValue) },
            6 => new() { [PotionEffects.WaterBreathing] = E(PotionEffects.WaterBreathing, 2010), [PotionEffects.NightVision] = E(PotionEffects.NightVision, 2010),
                [PotionEffects.Blindness] = E(PotionEffects.Blindness, 2010), [PotionEffects.Slowness] = E(PotionEffects.Slowness, 2010),
                [PotionEffects.MiningFatigue] = E(PotionEffects.MiningFatigue, 2010) },
            _ => new() { [PotionEffects.Poison] = E(PotionEffects.Poison, 0) },
        };
    }

    private static ItemStack[] Slots(Random r, int[] pool)
    {
        var slots = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
        for (int i = 0; i < r.Next(0, 9); i++)
            slots[r.Next(5, 45)] = new ItemStack((short)pool[r.Next(pool.Length)], (byte)r.Next(1, 17), (short)(r.Next(4) == 0 ? r.Next(4) : 0));
        return slots;
    }

    private static readonly int[] FoodPool =
    [
        Items.Apple, Items.Bread, Items.GoldenApple, Items.CookedPorkchop, Items.Porkchop, Items.Cookie, Items.RawFish, Items.CookedFish,
        Items.MushroomStew, Items.RottenFlesh, Items.RawChicken, Items.CookedChicken, Items.Steak, Items.RawBeef, Items.MelonSlice, 391, 392, 393, 394,
        Blocks.Dirt, Items.Stick, Items.WoodenSword, Items.StoneSword, Items.IronSword, Items.DiamondSword, Items.GoldenSword, Items.WoodenAxe,
        Items.IronAxe, Items.DiamondAxe, Items.StonePickaxe, Items.IronPickaxe, Items.Shears, Items.Bow, 258, 279, Items.WoodenHoe,
    ];

    [Fact]
    public void Survival_rules_match_the_golden() => SimRulesGoldenTests.Check("client-survival", "DAE119183CB6123306AFFF06E6B598A497B525CD562AC9A69713A5B9A8349FE1", sb =>
    {
        var o = new SurvivalOptions();
        IReadOnlySet<int> none = new HashSet<int>(), reserved = new HashSet<int> { Items.Bread, Items.Apple };
        foreach (var opts in new[] { o, o with { Enabled = false } })
            foreach (var what in new[] { "fight", "Fight zombie", "spar", "bridge", "expand island", "mine 32 blocks", "dig", "place" })
                for (int hp = 0; hp <= 20; hp += 1)
                    foreach (int food in new[] { 0, 5, 6, 17, 18, 20 })
                        foreach (bool hasFood in new[] { false, true })
                            foreach (bool edge in new[] { false, true })
                                for (int fx = 0; fx < 8; fx++)
                                    sb.Append(SurvivalReflex.GateReason(what, hp, food, hasFood, opts, edge, Effects(fx)) ?? "-").Append('\n');
        for (int fx = 0; fx < 8; fx++) sb.Append(SurvivalReflex.EffectFacts(Effects(fx))).Append('\n');

        foreach (var d in Versions)
            using (GameData.Override(d))
            {
                var r = new Random(13);
                IHungerRules rules = d == Versions[0] ? GameVersionProfiles.Beta181.Hunger : GameVersionProfiles.Java1122.Hunger;
                for (int k = 0; k < 400; k++)
                {
                    var slots = Slots(r, FoodPool);
                    int hp = r.Next(0, 21), food = r.Next(0, 21);
                    var res = k % 3 == 0 ? reserved : none;
                    var pick = SurvivalReflex.PickFood(slots, res, hp, food, o);
                    var eat = SurvivalReflex.EatNow(slots, res, hp, food, o, rules, (float)r.NextDouble() * 5);
                    sb.Append(d.VersionId).Append(' ').Append(hp).Append('/').Append(food).Append(' ').Append(pick?.ToString() ?? "-")
                        .Append(" | ").Append(eat?.ToString() ?? "-").Append(" | ").Append(SurvivalReflex.HeaderText(hp, food, slots, res, o, Effects(k % 8)))
                        .Append(" | ").Append(SurvivalReflex.HeaderText(hp, food, slots, res)).Append('\n');
                }
                var reflex = new SurvivalReflex(o);
                for (int k = 0; k < 300; k++)
                {
                    var alert = reflex.OnHealth(r.Next(-1, 21), r.Next(0, 21), r.Next(2) == 0, r.Next(3) == 0 ? "zombie 2m away" : null, r.Next(5) == 0);
                    var slots = Slots(r, FoodPool);
                    var hunger = reflex.CheckFood(r.Next(-1, 21), slots, k % 4 == 0 ? reserved : none, r.Next(0, 4));
                    sb.Append(alert?.ToString() ?? "-").Append(" | ").Append(hunger is null ? "-" : $"{hunger.Kind} {hunger.Message} {hunger.Level} {hunger.Critical}")
                        .Append('\n');
                }
            }

        var fr = new Random(17);
        for (int k = 0; k < 200; k++)
        {
            var fs = new FoodStats1122
            {
                Food = fr.Next(0, 21), Saturation = (float)(fr.NextDouble() * 6), Exhaustion = (float)(fr.NextDouble() * 8),
                Health = fr.Next(0, 21) + (fr.Next(2) == 0 ? 0.5f : 0), MaxHealth = 20, Difficulty = fr.Next(0, 4), NaturalRegeneration = fr.Next(5) > 0,
            };
            for (int t = 0; t < 120; t++)
            {
                if (t % 17 == 0) fs.AddExhaustion((float)fr.NextDouble() * 3);
                fs.Tick();
            }
            sb.Append($"{fs.Food} {F(fs.Saturation)} {F(fs.Exhaustion)} {F(fs.Health)} {fs.Timer}\n");
        }
    });

    [Fact]
    public void Weapon_and_mob_rules_match_the_golden() => SimRulesGoldenTests.Check("client-weapons", "AC03F287EE0C07F1F9FDFB6FF6E04B69E7AE20192BFCE670DF34193DE4578435", sb =>
    {
        IReadOnlySet<int> none = new HashSet<int>(), never = new HashSet<int> { Items.DiamondSword };
        foreach (var d in Versions)
            using (GameData.Override(d))
            {
                var r = new Random(23);
                ICombatRules? modern = d == Versions[0] ? null : GameVersionProfiles.Java1122.Combat;
                for (int k = 0; k < 600; k++)
                {
                    var slots = Slots(r, FoodPool);
                    if (k % 5 == 0) for (int i = 36; i < 45; i++) slots[i] = new ItemStack((short)FoodPool[r.Next(FoodPool.Length)], 1, 0);
                    var nw = k % 7 == 0 ? never : none;
                    foreach (var rules in new[] { null, modern })
                        sb.Append(WeaponChoice.Best(slots, none, nw, rules)).Append(' ').Append(WeaponChoice.Weakest(slots, none, nw, rules)).Append(' ')
                            .Append(WeaponChoice.Hand(slots, none, nw, rules)).Append(" | ");
                    sb.Append('\n');
                }
                for (int type = -1; type < 260; type++) sb.Append(CombatRules.Profile(type)).Append('\n');
            }
    });
}
