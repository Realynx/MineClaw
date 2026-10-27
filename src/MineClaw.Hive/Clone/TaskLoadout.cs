using System.Text.RegularExpressions;

namespace MineClaw.Hive.Clone;

/// <summary>
/// The tools a task needs before it starts, from its text: a pickaxe for mining/the cobble gen, an axe for trees, food for
/// anything long (mining, chopping, "all night"). Zero tokens; the clone fetches them from storage first.
/// </summary>
public static partial class TaskLoadout
{
    [GeneratedRegex(@"\b(?:mine|mining|dig|digging|cobble\w*|stone|gen|generator|harvest\w*|ore|quarry|pickaxe)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mining();

    [GeneratedRegex(@"\b(?:chop\w*|trees?|logs?|wood|lumber\w*|axe)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Trees();

    [GeneratedRegex(@"\b(?:all night|overnight|all day|forever|keep (?:going|mining|chopping|working)|until (?:morning|stopped|told)|hours?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Long();

    public const string Pickaxe = "pickaxe";
    public const string Axe = "axe";
    public const string Food = "food";

    /// <summary>Loadout names for a task ("pickaxe", "axe", "food"), or null when it needs nothing.</summary>
    public static string[]? For(string? task)
    {
        if (string.IsNullOrWhiteSpace(task)) return null;
        var list = new List<string>();
        bool mining = Mining().IsMatch(task), trees = Trees().IsMatch(task);
        if (mining) list.Add(Pickaxe);
        if (trees) list.Add(Axe);
        if (mining || trees || Long().IsMatch(task)) list.Add(Food);
        return list.Count == 0 ? null : list.ToArray();
    }
}
