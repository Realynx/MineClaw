using System.Text.RegularExpressions;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Clone;

/// <summary>What a hive task mostly asks of a clone's body (zero tokens, from the task text and any inline script).</summary>
public enum TaskIntent { General, Fight, Mine, Chop, Build, Farm, Explore, Deliver }

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
    // Each intent: STRONG words name the job itself (2 points), WEAK words are tools/materials that ride along in any task text
    // (1 point): live 10-27 21:57 "get 420 to harvest the trees" (a stone axe, logs, the chest) was read as MINE because "stone"
    // matched first, so a gen failure blocked tree work.
    private static readonly (TaskIntent Intent, Regex Strong, Regex? Weak)[] Intents =
    [
        (TaskIntent.Mine, R(@"\b(?:mine|mining|dig\w*|cobble\w*|gen|generator|harvest_generator|ore|quarry)\b"), R(@"\b(?:stone|pickaxe|pick)\b")),
        (TaskIntent.Chop, R(@"\b(?:chop\w*|trees?|logs?|lumber\w*|tree farm|chop_tree)\b"), R(@"\b(?:wood|axe|saplings?|replant\w*)\b")),
        (TaskIntent.Farm, R(@"\b(?:farm\w*|wheat|seeds?|crops?|plant\w*|till\w*)\b"), R(@"\b(?:harvest\w*|hoe|bread)\b")),
        (TaskIntent.Build, R(@"\b(?:build\w*|bridge\w*|wall|floor|pillar|extend\w*|platform)\b"), R(@"\b(?:place|planks?)\b")),
        (TaskIntent.Deliver, R(@"\b(?:deliver\w*|bring|fetch\w*|hand (?:over|it|them)|give (?:it|them|\w+ (?:to|the))|toss\w*)\b"), null),
        (TaskIntent.Explore, R(@"\b(?:explore\w*|nether|scout\w*|look around|find)\b"), null),
    ];

    private static Regex R(string p) => new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The task's main intent: fighting wins (it is the one that can kill a weak clone); otherwise the best-scoring job
    /// (strong words 2, tool/material words 1; ties in table order).</summary>
    public static TaskIntent IntentOf(string? task)
    {
        var t = task ?? "";
        if (Fight.IsMatch(t)) return TaskIntent.Fight;
        var best = TaskIntent.General;
        int bestScore = 0;
        foreach (var (intent, strong, weak) in Intents)
        {
            int score = 2 * strong.Matches(t).Count + (weak?.Matches(t).Count ?? 0);
            if (score > bestScore) { best = intent; bestScore = score; }
        }
        return best;
    }

    public static string Verb(TaskIntent i) => i switch
    {
        TaskIntent.Fight => "fighting", TaskIntent.Mine => "mining", TaskIntent.Chop => "chopping", TaskIntent.Build => "building",
        TaskIntent.Farm => "farming", TaskIntent.Explore => "exploring", TaskIntent.Deliver => "delivering", _ => "this job",
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
        double s = Points(v, intent, online, carriesFood, mainX, mainZ);
        return new CloneFit(name, Math.Round(s, 3), Blocker(v, intent, carriesFood), Summary(v, intent));
    }

    /// <summary>Health, food (and carrying some), the intent's gear, being online, closeness to main; unknown vitals cost a little.</summary>
    private static double Points(CloneVitals v, TaskIntent intent, bool online, bool carriesFood, double? mainX, double? mainZ)
    {
        int hp = v.Hp ?? 14, food = v.Food ?? 14; // never seen: assume middling, below a known-healthy clone
        double s = hp / 20.0 * 3 + food / 20.0 * 2 + (carriesFood ? 0.8 : 0);
        s += GearPoints(v.Items, intent);
        if (online) s += 0.5;
        if (v.X is { } x && v.Z is { } z && mainX is { } mx && mainZ is { } mz)
            s -= Math.Min(2.0, Math.Sqrt((x - mx) * (x - mx) + (z - mz) * (z - mz)) / 32.0);
        if (v.Hp == null) s -= 0.6; // unknown is a risk too
        return s;
    }

    private static double GearPoints(IReadOnlyList<ItemMsg>? items, TaskIntent intent) => intent switch
    {
        TaskIntent.Fight => Math.Max(Tier(items, "sword"), Tier(items, "axe") - 1) * 0.4 + Armor(items) * 0.3,
        TaskIntent.Mine => Tier(items, "pickaxe") * 0.4,
        TaskIntent.Chop => Tier(items, "axe") * 0.3,
        _ => 0,
    };

    /// <summary>Why this clone can't take the job now: critical health, unfit to fight without food, or starving.</summary>
    private static string? Blocker(CloneVitals v, TaskIntent intent, bool carriesFood)
    {
        int food = v.Food ?? 14;
        if (v.Hp is { } h && h <= 4) return $"hp {h} is critical: needs food and rest first";
        if (intent == TaskIntent.Fight && FightBlocker(v, food, carriesFood) is { } fight) return fight;
        if (v.Food is 0 && !carriesFood && v.Hp is { } sh && sh <= 10) return $"starving (food 0, hp {sh}), no food: needs food first";
        return null;
    }

    private static string? FightBlocker(CloneVitals v, int food, bool carriesFood)
    {
        if (v.Hp is not { } fh || !(fh < 10 || (fh < 14 && food < 6)) || carriesFood) return null;
        return $"hp {fh}, food {food}{(v.Food is 0 ? " (starving)" : "")}, no food: needs food (bread or cooked meat) before fighting";
    }

    private static string Summary(CloneVitals v, TaskIntent intent) =>
        v.Hp == null ? "vitals unknown" : $"hp {v.Hp} food {v.Food}"
            + (intent == TaskIntent.Fight ? WeaponText(v.Items) : intent == TaskIntent.Mine ? (Tier(v.Items, "pickaxe") > 0 ? ", has a pickaxe" : "") : "");

    private static string WeaponText(IReadOnlyList<ItemMsg>? items)
    {
        var w = (items ?? []).Where(i => i.Name.Contains("sword", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("axe", StringComparison.OrdinalIgnoreCase)
            && !i.Name.Contains("pickaxe", StringComparison.OrdinalIgnoreCase)).Select(i => i.Name.ToLowerInvariant()).FirstOrDefault();
        return w != null ? $", {w}" : "";
    }
}
