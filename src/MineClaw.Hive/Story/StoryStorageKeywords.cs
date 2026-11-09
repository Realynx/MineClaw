using System.Text.RegularExpressions;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>Storage sign vocabulary: categories, exact IDs and search-like item names. Unmatched labels have no effect.</summary>
public static class StoryStorageKeywords
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Version, string Entry), string> Cache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Version, string Entry), string[]> SignCache = new();
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

    /// <summary>Compile sign searches into the existing filter format, keeping hauling/deposit matching inexpensive.</summary>
    private static string[] SignEntries(string text, IGameData data)
    {
        var key = SearchName(text);
        if (key.StartsWith("minecraft:")) key = key[10..];
        return key.Length == 0 ? [] : SignCache.GetOrAdd((data.VersionId, key), k => FindSignEntries(k.Entry, data));
    }

    private static string SearchName(string text) => Regex.Replace(text.Trim().ToLowerInvariant(), @"[\s_]+", "_");

    private static string[] FindSignEntries(string key, IGameData data)
    {
        var entries = new List<string>();
        if (Normalize(key, data) is { } exact) entries.Add(exact);
        // Explicit IDs and the all-items aliases keep their existing precise meaning.
        if (entries.Contains("*") || Regex.IsMatch(key, @"^\d+(:-?\d+)?$")) return entries.ToArray();
        var categories = StoryCategories.For(data);
        for (int id = 1; id < 4096; id++)
        {
            if (!data.ItemExists(id)) continue;
            var item = data.Item(id);
            if (SearchName(item.Name).Contains(key, StringComparison.Ordinal))
            {
                if (!categories.AnyCovers(entries, id, 0)) entries.Add(id.ToString());
                continue;
            }
            // The supported versions encode named item/block variants in the low four metadata bits. Durability does
            // not narrow an item-name search: once a tool's name matches, worn copies belong in the same chest too.
            var matches = new List<int>();
            for (int meta = 0; meta < 16; meta++)
                if (SearchName(data.DisplayName(id, meta)).Contains(key, StringComparison.Ordinal)
                    || SearchName(data.VariantName(id, meta)).Contains(key, StringComparison.Ordinal)) matches.Add(meta);
            if (matches.Count == 16 || item.MaxDamage > 0 && matches.Count > 0)
            {
                if (!categories.AnyCovers(entries, id, 0)) entries.Add(id.ToString());
            }
            else foreach (var meta in matches)
                if (!categories.AnyCovers(entries, id, meta)) entries.Add($"{id}:{meta}");
        }
        return entries.ToArray();
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
                var target = excluded ? deny : allow;
                var entries = SignEntries(text, data);
                if (entries.Length > 0) target.AddRange(entries);
                else foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    target.AddRange(SignEntries(word, data));
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
