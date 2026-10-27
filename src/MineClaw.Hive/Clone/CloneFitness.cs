using System.Text.RegularExpressions;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Clone;

/// <summary>What a hive task mostly asks of a clone's body (zero tokens, from the task text and any inline script).</summary>
public enum TaskIntent { General, Fight, Mine, Chop, Build, Farm, Explore }

/// <summary>A clone's last known body: health, food, items, where it logged out. Null fields = never seen.</summary>
public sealed record CloneVitals(int? Hp, int? Food, IReadOnlyList<ItemMsg>? Items, double? X, double? Y, double? Z, int Dimension = 0,
    DateTimeOffset? At = null)
{
    public static readonly CloneVitals Unknown = new(null, null, null, null, null, null);
}

/// <summary>One candidate's fit for a task: higher is better; <see cref="Blocker"/> = must not take it ("needs food").</summary>
public sealed record CloneFit(string Name, double Score, string? Blocker, string Summary);

/// <summary>
/// Scores hive clones for a task (live 10-27 15:44: "go fight mobs" went to 67 at hp 7, food 0, stone tools only, while 69 was
/// free and healthy, because an unnamed spawn took the first idle slot). Health, food (in the bar and carried), tools and armor
/// for the task, and distance to main; a clone that can't safely do it is blocked with the reason and the fix.
/// b1.8.1: health regenerates only at food ≥ 18; starving (food 0) drains health; clones keep hp/food across logins but
/// deposit their whole inventory at logout (an offline clone's items are what its loadout fetches).
/// </summary>
public static class CloneFitness
{
    private static readonly Regex Fight = new(@"\b(?:fight\w*|kill\w*|hunt\w*|mobs?|zombies?|spiders?|skeletons?|creepers?|attack\w*|spar\w*|combat|defend\w*|guard\w*|slay\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Mine = new(@"\b(?:mine|mining|dig\w*|cobble\w*|stone|gen|generator|ore|quarry|pickaxe)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Chop = new(@"\b(?:chop\w*|trees?|logs?|wood|lumber\w*|axe|saplings?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Build = new(@"\b(?:build\w*|bridge\w*|place|wall|floor|pillar|extend\w*|platform)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Farm = new(@"\b(?:farm\w*|wheat|seeds?|harvest\w*|crops?|plant\w*|hoe)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Explore = new(@"\b(?:explore\w*|nether|scout\w*|look around|find)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The task's main intent (fighting wins: it is the one that can kill a weak clone).</summary>
    public static TaskIntent IntentOf(string? task)
    {
        var t = task ?? "";
        if (Fight.IsMatch(t)) return TaskIntent.Fight;
        if (Mine.IsMatch(t)) return TaskIntent.Mine;
        if (Chop.IsMatch(t)) return TaskIntent.Chop;
        if (Farm.IsMatch(t)) return TaskIntent.Farm;
        if (Build.IsMatch(t)) return TaskIntent.Build;
        if (Explore.IsMatch(t)) return TaskIntent.Explore;
        return TaskIntent.General;
    }

    public static string Verb(TaskIntent i) => i switch
    {
        TaskIntent.Fight => "fighting", TaskIntent.Mine => "mining", TaskIntent.Chop => "chopping", TaskIntent.Build => "building",
        TaskIntent.Farm => "farming", TaskIntent.Explore => "exploring", _ => "this job",
    };

    private static readonly string[] FoodWords = ["bread", "apple", "porkchop", "pork", "fish", "cookie", "steak", "beef", "chicken", "stew", "melon", "cake", "rotten"];

    public static bool IsFood(ItemMsg i) => FoodWords.Any(w => i.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tool tier of the best item whose name contains <paramref name="kind"/> (wood 1 .. diamond 4, gold 1), 0 = none.</summary>
    public static int Tier(IReadOnlyList<ItemMsg>? items, string kind)
    {
        int best = 0;
        foreach (var i in items ?? [])
        {
            var n = i.Name.ToLowerInvariant();
            if (!n.Contains(kind) || (kind == "axe" && n.Contains("pickaxe"))) continue;
            int t = n.Contains("diamond") ? 4 : n.Contains("iron") ? 3 : n.Contains("stone") ? 2 : 1;
            best = Math.Max(best, t);
        }
        return best;
    }

    private static int Armor(IReadOnlyList<ItemMsg>? items) =>
        (items ?? []).Count(i => i.Name.Contains("helmet", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("chestplate", StringComparison.OrdinalIgnoreCase)
            || i.Name.Contains("leggings", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("boots", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fit of one clone for <paramref name="intent"/>. <paramref name="online"/> = logged in and free (no launch wait);
    /// <paramref name="mainX"/>/<paramref name="mainZ"/> = main's position (closer is better).
    /// </summary>
    public static CloneFit Score(string name, CloneVitals v, TaskIntent intent, bool online, double? mainX = null, double? mainZ = null)
    {
        bool carriesFood = v.Items?.Any(IsFood) == true;
        int hp = v.Hp ?? 14, food = v.Food ?? 14; // never seen: assume middling, below a known-healthy clone
        double s = hp / 20.0 * 3 + food / 20.0 * 2 + (carriesFood ? 0.8 : 0);
        int weapon = Math.Max(Tier(v.Items, "sword"), Tier(v.Items, "axe") - 1);
        switch (intent)
        {
            case TaskIntent.Fight: s += weapon * 0.4 + Armor(v.Items) * 0.3; break;
            case TaskIntent.Mine: s += Tier(v.Items, "pickaxe") * 0.4; break;
            case TaskIntent.Chop: s += Tier(v.Items, "axe") * 0.3; break;
        }
        if (online) s += 0.5;
        if (v.X is { } x && v.Z is { } z && mainX is { } mx && mainZ is { } mz)
            s -= Math.Min(2.0, Math.Sqrt((x - mx) * (x - mx) + (z - mz) * (z - mz)) / 32.0);
        if (v.Hp == null) s -= 0.6; // unknown is a risk too

        string? blocker = null;
        if (v.Hp is { } h && h <= 4) blocker = $"hp {h} is critical: needs food and rest first";
        else if (intent == TaskIntent.Fight && v.Hp is { } fh && (fh < 10 || (fh < 14 && food < 6)) && !carriesFood)
            blocker = $"hp {fh}, food {food}{(v.Food is 0 ? " (starving)" : "")}, no food: needs food (bread or cooked meat) before fighting";
        else if (v.Food is 0 && !carriesFood && v.Hp is { } sh && sh <= 10)
            blocker = $"starving (food 0, hp {sh}), no food: needs food first";

        var summary = v.Hp == null ? "vitals unknown" : $"hp {v.Hp} food {v.Food}"
            + (intent == TaskIntent.Fight ? WeaponText(v.Items) : intent == TaskIntent.Mine ? (Tier(v.Items, "pickaxe") > 0 ? ", has a pickaxe" : "") : "");
        return new CloneFit(name, Math.Round(s, 3), blocker, summary);
    }

    private static string WeaponText(IReadOnlyList<ItemMsg>? items)
    {
        var w = (items ?? []).Where(i => i.Name.Contains("sword", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("axe", StringComparison.OrdinalIgnoreCase)
            && !i.Name.Contains("pickaxe", StringComparison.OrdinalIgnoreCase)).Select(i => i.Name.ToLowerInvariant()).FirstOrDefault();
        return w != null ? $", {w}" : "";
    }
}
