using System.Text.RegularExpressions;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>
/// Conservative estimate of a sparring opponent's health. b1.8 servers never send other players' health, so the
/// estimate starts at 20 (or the last estimate, assuming no regeneration), and every confirmed hit subtracts the
/// worst case: maximum critical damage if the bot could have been falling, minus only a fraction of the armor seen in
/// 0x05 equipment, rounded up, plus a margin. Damage from anything else subtracts a fixed assumption.
/// </summary>
public sealed class OpponentHpModel
{
    private readonly CombatOptions _o;

    public OpponentHpModel(CombatOptions options, double startHealth = 20)
    {
        _o = options;
        Estimate = Math.Clamp(startHealth, 0, 20);
        Start = Estimate;
    }

    public double Start { get; }
    public double Estimate { get; private set; }
    public int ConfirmedHits { get; private set; }
    public int OtherDamageEvents { get; private set; }

    /// <summary>Armor value credited for the opponent: b1.8 armor value from their equipment × <see cref="CombatOptions.SparArmorTrust"/>, floored.</summary>
    public int CreditedArmor(IEnumerable<ItemStack> armor) => (int)Math.Floor(CombatRules.ArmorValue(armor) * Math.Clamp(_o.SparArmorTrust, 0, 1));

    /// <summary>Worst case damage of one hit with the held item: crit maximum when falling, after credited armor, plus margin.</summary>
    public double WorstHit(int weaponItemId, bool couldCrit, IEnumerable<ItemStack> armor)
    {
        int dmg = CombatRules.WeaponDamage(weaponItemId);
        if (couldCrit) dmg = CombatRules.MaxCritDamage(dmg);
        int after = CombatRules.DamageAfterArmor(dmg, CreditedArmor(armor));
        return Math.Ceiling(Math.Max(1, after) * (1 + Math.Max(0, _o.SparDamageMargin)));
    }

    public double OnConfirmedHit(int weaponItemId, bool couldCrit, IEnumerable<ItemStack> armor)
    {
        ConfirmedHits++;
        double d = WorstHit(weaponItemId, couldCrit, armor);
        Estimate = Math.Max(0, Estimate - d);
        return d;
    }

    public void OnOtherDamage()
    {
        OtherDamageEvents++;
        Estimate = Math.Max(0, Estimate - _o.SparOtherDamageAssumed);
    }

    /// <summary>Why the spar must stop now (or null): estimate at the threshold, or the hit cap reached.</summary>
    public string? StopReason()
    {
        if (Estimate <= _o.SparOpponentStopHealth) return "est hp threshold";
        if (ConfirmedHits >= _o.SparMaxHits) return "max hits";
        return null;
    }

    /// <summary>Would one more worst-case hit leave the opponent at or below 2 HP? (Never risk a fatal hit.)</summary>
    public bool NextHitRisky(double worstHit) => Estimate - worstHit <= 2;
}

/// <summary>Chat words that start or stop sparring.</summary>
public static partial class SparWords
{
    /// <summary>Any configured stop word as a whole word, case-insensitive ("gg", "stop!", "I yield").</summary>
    public static bool IsStop(string message, IReadOnlyList<string> stopWords)
    {
        foreach (Match m in Word().Matches(message))
            foreach (var s in stopWords)
                if (string.Equals(m.Value, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The player asks for a fight: "spar", "duel", "pvp", "1v1", "fight me".</summary>
    public static bool IsRequest(string message) => Request().IsMatch(message);

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(spar+(ing)?|duel|pvp|1v1|fight me|fight with me)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Request();
}
