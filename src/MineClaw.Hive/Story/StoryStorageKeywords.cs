using System.Text.RegularExpressions;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>Storage sign vocabulary. Plain labels have no effect; recognised categories or exact item names do.</summary>
public static class StoryStorageKeywords
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Version, string Entry), string> Cache = new();
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["seeds"] = "plants_seeds", ["saplings"] = "plants_saplings", ["flowers"] = "plants_flowers",
        ["logs"] = "wood_logs", ["planks"] = "wood_planks", ["sticks"] = "wood_sticks",
        ["stone"] = "stone_stone", ["glass"] = "stone_glass", ["soil"] = "stone_soil",
        ["armor"] = "armour", ["metals"] = "ores_metals", ["minerals"] = "gems_minerals",
        ["redstone"] = "redstone_mech", ["all"] = "*", ["anything"] = "*",
    };

    public static string? Normalize(string entry, IGameData data)
    {
        var key = entry.Trim().ToLowerInvariant().Replace(' ', '_');
        var result = Cache.GetOrAdd((data.VersionId, key), k => NormalizeCore(k.Entry, data) ?? "");
        return result.Length == 0 ? null : result;
    }

    private static string? NormalizeCore(string key, IGameData data)
    {
        if (key.StartsWith("minecraft:")) key = key[10..];
        if (Aliases.TryGetValue(key, out var alias)) key = alias;
        if (key == "*" || StoryCategories.For(data).Get(key) != null) return key;
        if (Regex.IsMatch(key, @"^\d+(:-?\d+)?$"))
        {
            var parts = key.Split(':');
            return int.TryParse(parts[0], out var id) && data.ItemExists(id)
                && (parts.Length == 1 || int.TryParse(parts[1], out var meta) && meta >= -1 && meta <= short.MaxValue) ? key : null;
        }
        // Do not let the general fuzzy resolver turn an unrelated label into a storage rule.
        for (int id = 1; id < 4096; id++)
            if (data.ItemExists(id) && data.Item(id).Name.Equals(key, StringComparison.OrdinalIgnoreCase)) return id.ToString();
        if (data.ItemByNameWithDamage(key) is { } item && item.Damage >= 0
            && (data.VariantName(item.Item.Id, item.Damage).Equals(key, StringComparison.OrdinalIgnoreCase)
                || data.DisplayName(item.Item.Id, item.Damage).Replace(' ', '_').Equals(key, StringComparison.OrdinalIgnoreCase)))
            return $"{item.Item.Id}:{item.Damage}";
        return null;
    }

    public static StorageFilter? Parse(IReadOnlyList<string> lines, IGameData data)
    {
        var allow = new List<string>();
        var deny = new List<string>();
        foreach (var line in lines)
            foreach (var part in line.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries))
            {
                var text = Regex.Replace(part, "§.", "").Trim();
                if (text.StartsWith('[') && text.EndsWith(']')) text = text[1..^1].Trim();
                bool excluded = text.StartsWith('-') || text.StartsWith('!');
                if (excluded) text = text[1..].Trim();
                if (text.StartsWith("no ", StringComparison.OrdinalIgnoreCase)) { excluded = true; text = text[3..]; }
                if (Normalize(text, data) is { } entry) (excluded ? deny : allow).Add(entry);
                else foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (Normalize(word, data) is { } token) (excluded ? deny : allow).Add(token);
            }
        if (allow.Count == 0 && deny.Count == 0) return null;
        return new StorageFilter { Allow = allow.Count == 0 ? ["*"] : allow.Distinct().ToList(), Deny = deny.Distinct().ToList() };
    }

    public static P3? AttachedChest(IStoryWorld world, P3 at)
    {
        var (id, meta) = world.Block(at.X, at.Y, at.Z);
        P3? chest = id == 63 ? at.Add(0, -1, 0) : id == 68 ? meta switch
        {
            2 => at.Add(0, 0, 1), 3 => at.Add(0, 0, -1), 4 => at.Add(1, 0, 0), 5 => at.Add(-1, 0, 0), _ => null,
        } : null;
        return chest is { } c && world.Loaded(c.X, c.Z) && world.Block(c.X, c.Y, c.Z).Id is 54 or 146 ? c : null;
    }
}
