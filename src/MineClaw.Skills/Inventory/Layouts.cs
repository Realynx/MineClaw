using System.Collections.Concurrent;
using System.Text;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Inventory;

/// <summary>Storage categories for organizing (order = chest assignment and in-chest sort order).</summary>
public static class StorageCategory
{
    public static readonly string[] Order = ["ores", "wood", "stone", "farming", "food", "tools", "mob", "misc"];

    private static readonly HashSet<int> Ores =
    [
        Items.Coal, Items.Diamond, Items.IronIngot, Items.GoldIngot, Items.Redstone, Items.GlowstoneDust, Items.Flint, Items.ClayBall, Items.Brick,
        Blocks.CoalOre, Blocks.IronOre, Blocks.GoldOre, Blocks.DiamondOre, Blocks.LapisOre, Blocks.RedstoneOre,
        Blocks.IronBlock, Blocks.GoldBlock, Blocks.DiamondBlock, Blocks.LapisBlock, Blocks.Obsidian, Blocks.Glowstone,
    ];
    private static readonly HashSet<int> Wood = [Blocks.Log, MineClaw.Client.Trees.Wood.Log2, Blocks.Planks, Blocks.Sapling, Blocks.Leaves, MineClaw.Client.Trees.Wood.Leaves2, Items.Stick, Items.Bowl, Blocks.WoodenStairs, Blocks.Fence];
    private static readonly HashSet<int> Stone =
    [
        Blocks.Stone, Blocks.Cobblestone, Blocks.Dirt, Blocks.Grass, Blocks.Sand, Blocks.Gravel, Blocks.Sandstone, Blocks.Glass, Blocks.StoneBricks,
        Blocks.Bricks, Blocks.Netherrack, Blocks.SoulSand, Blocks.Clay, Blocks.Slab, Blocks.CobblestoneStairs, Blocks.MossyCobblestone, Blocks.Ice, Blocks.SnowBlock,
    ];
    private static readonly HashSet<int> Farming =
    [
        Items.Seeds, Items.Wheat, Items.SugarCane, Items.PumpkinSeeds, Items.MelonSeeds, Items.Egg, Items.Sugar, Items.Paper,
        Blocks.Pumpkin, Blocks.Melon, Blocks.Cactus, Blocks.RedMushroom, Blocks.BrownMushroom, Blocks.SugarCane, Blocks.Dandelion, Blocks.Rose,
    ];
    private static readonly HashSet<int> Mob =
        [Items.String, Items.Feather, Items.Gunpowder, Items.Leather, Items.Bone, Items.Slimeball, Items.RottenFlesh, Items.EnderPearl, Items.Arrow];
    private static readonly HashSet<int> ToolLike =
        [Items.Bow, Items.FishingRod, Items.Shears, Items.FlintAndSteel, Items.Bucket, Items.WaterBucket, Items.LavaBucket, Items.MilkBucket, Items.Compass, Items.Clock];

    public static string Of(int id, int damage = 0)
    {
        if (id == Items.Dye) return damage == Items.LapisLazuli ? "ores" : damage == Items.BoneMeal ? "farming" : "misc";
        if (Mob.Contains(id)) return "mob";
        if (IsToolLike(id)) return "tools";
        foreach (var (set, category) in BeforeFood)
            if (set.Contains(id)) return category;
        if (Items.Food(id) != null) return "food";
        if (Stone.Contains(id)) return "stone";
        return "misc";
    }

    /// <summary>The sets checked (in this order) after mob drops and tools, before food.</summary>
    private static readonly (HashSet<int> Set, string Category)[] BeforeFood = [(Ores, "ores"), (Wood, "wood"), (Farming, "farming")];

    private static bool IsToolLike(int id) =>
        ToolLike.Contains(id) || (id >= Items.LeatherHelmet && id <= Items.GoldenBoots) || (Items.Exists(id) && Items.IsTool(id));

    /// <summary>Other words that name a category, checked in this order after the category names themselves.</summary>
    private static readonly (string[] Words, string Category)[] Hints =
    [
        (["ingot", "mineral"], "ores"), (["log", "plank"], "wood"), (["cobble", "block", "build"], "stone"),
        (["crop", "seed", "farm"], "farming"), (["tool", "armor", "weapon"], "tools"),
    ];

    public static int Rank(string category) => Array.IndexOf(Order, category) is var i and >= 0 ? i : Order.Length;

    /// <summary>"ores", "ingots", "storage:ores", "Ore chest" → canonical category, else null.</summary>
    public static string? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant().Replace("storage:", "");
        foreach (var c in Order) if (t.Contains(c.TrimEnd('s'))) return c;
        foreach (var (words, category) in Hints)
            if (words.Any(w => t.Contains(w))) return category;
        return null;
    }
}

/// <summary>A container slot layout: per slot the item (id, damage) or null for "empty / don't care".</summary>
public sealed record SlotLayout(IReadOnlyList<(int Id, int Damage)?> Slots, bool StrictEmpty = true)
{
    /// <summary>
    /// Parses a compact 9-column template: rows separated by '/', cells by ',', "name*N" repeats, "_" empty.
    /// e.g. <c>"cobble*3,dirt*2,_*4 / log*9"</c>. Rows shorter than 9 are padded with empty cells.
    /// </summary>
    public static SlotLayout Parse(string template, int slots = 27)
    {
        var cells = new List<(int, int)?>();
        foreach (var rowText in template.Split('/', StringSplitOptions.TrimEntries)) cells.AddRange(ParseRow(rowText));
        if (cells.Count > slots) throw new FormatException($"layout has {cells.Count / 9} rows; chest has {slots / 9}");
        while (cells.Count < slots) cells.Add(null);
        return new SlotLayout(cells);
    }

    /// <summary>One row: its cells ("name*N" repeats, "_" empty), padded to 9.</summary>
    private static List<(int, int)?> ParseRow(string rowText)
    {
        var row = new List<(int, int)?>();
        foreach (var raw in rowText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('*', StringSplitOptions.TrimEntries);
            int n = parts.Length > 1 && int.TryParse(parts[1], out var k) ? Math.Clamp(k, 1, 54) : 1;
            var item = Cell(parts[0]);
            for (int i = 0; i < n; i++) row.Add(item);
        }
        if (row.Count > 9) throw new FormatException($"layout row '{rowText}' has {row.Count} cells (max 9)");
        while (row.Count < 9) row.Add(null);
        return row;
    }

    private static (int, int)? Cell(string name) =>
        name is "_" or "" or "empty" ? null
        : SkillJson.Item(name) is { } it ? (it.Id, Math.Max(0, it.Damage))
        : throw new FormatException($"unknown item '{name}' in layout");

    /// <summary>Compact template text for the current contents (used to save "how it looks now").</summary>
    public static string Describe(IReadOnlyList<ItemStack> slots)
    {
        var rows = new List<string>();
        for (int r = 0; r * 9 < slots.Count; r++) rows.Add(DescribeRow(slots, r));
        while (rows.Count > 0 && rows[^1] is "_*9") rows.RemoveAt(rows.Count - 1);
        return string.Join(" / ", rows);
    }

    /// <summary>Row r as runs of equal cells ("cobblestone*3,_*6").</summary>
    private static string DescribeRow(IReadOnlyList<ItemStack> slots, int r)
    {
        var parts = new List<string>();
        string? cur = null;
        int run = 0;
        for (int c = 0; c < 9 && r * 9 + c < slots.Count; c++)
        {
            var s = slots[r * 9 + c];
            string code = s.IsEmpty ? "_" : Code(s.Id, s.Damage);
            if (code == cur) { run++; continue; }
            if (cur != null) parts.Add(Run(cur, run));
            cur = code;
            run = 1;
        }
        if (cur != null) parts.Add(Run(cur, run));
        return string.Join(",", parts);
    }

    private static string Run(string code, int run) => run > 1 ? $"{code}*{run}" : code;

    public static string Code(int id, int damage) =>
        (id < 256 ? Blocks.Get(id).Name : Items.Get(id).Name) + (damage != 0 && Items.Get(id).MaxDamage == 0 ? ":" + damage : "");
}

/// <summary>Remembered per-chest layouts ("tidy up" restores them). The Bot layer backs this with memory.</summary>
public interface ILayoutStore
{
    string? Get(BlockPos chest);
    void Save(BlockPos chest, string template);
}

public sealed class InMemoryLayoutStore : ILayoutStore
{
    private readonly ConcurrentDictionary<BlockPos, string> _d = new();
    public string? Get(BlockPos chest) => _d.GetValueOrDefault(chest);
    public void Save(BlockPos chest, string template) => _d[chest] = template;
}

/// <summary>
/// Finds containers by name: "chest@x,y,z", a sign label ("Iron storage"), or a category target ("storage:ores").
/// The default resolves through sign text next to chests and categories assigned by organize_storage; the Bot layer
/// adds the WorldIndex (labels, snapshots) on top.
/// </summary>
public interface IContainerDirectory
{
    BlockPos? Resolve(SkillContext ctx, string name);
    /// <summary>Category assigned to a chest (by organize_storage, a label, or the index).</summary>
    string? CategoryOf(SkillContext ctx, BlockPos chest);
    void Assign(BlockPos chest, string category);
    /// <summary>Fresh contents after a run (WorldIndex container snapshot).</summary>
    void Snapshot(BlockPos chest, IReadOnlyList<ItemStack> contents);
}

public class ContainerDirectory : IContainerDirectory
{
    private readonly ConcurrentDictionary<BlockPos, string> _assigned = new();
    public ConcurrentDictionary<BlockPos, ItemStack[]> Snapshots { get; } = new();
    /// <summary>Extra resolver (WorldIndex) tried first.</summary>
    public Func<string, BlockPos?>? External { get; init; }
    public Action<BlockPos, IReadOnlyList<ItemStack>>? OnSnapshot { get; init; }

    public static bool IsContainer(int id) => id is Blocks.Chest or StorageOps.TrappedChest or Blocks.Furnace or Blocks.LitFurnace or Blocks.Dispenser;

    public virtual BlockPos? Resolve(SkillContext ctx, string name)
    {
        var n = name.Trim();
        var at = n.IndexOf('@');
        if (at >= 0) n = n[(at + 1)..];
        if (SkillJson.Parse($"{{\"p\":\"{n.Replace("\"", "")}\"}}").Pos("p") is { } p) return p;
        if (External?.Invoke(name) is { } ext) return ext;
        if (n.StartsWith("storage:", StringComparison.OrdinalIgnoreCase) || StorageCategory.Parse(n) is not null && !LabelMatch(ctx, n).Any())
        {
            var cat = StorageCategory.Parse(n);
            if (cat is null) return null;
            return ctx.Find((id, _) => StorageOps.IsChest(id), ctx.Options.SearchRadius, 64).Select(f => f.Pos)
                .Where(c => CategoryOf(ctx, c) == cat).Cast<BlockPos?>().FirstOrDefault();
        }
        return LabelMatch(ctx, n).Cast<BlockPos?>().FirstOrDefault();
    }

    /// <summary>Chests next to a sign whose text contains <paramref name="label"/>.</summary>
    private static IEnumerable<BlockPos> LabelMatch(SkillContext ctx, string label)
    {
        foreach (var (pos, lines) in ctx.Game.Signs)
        {
            var text = string.Join(" ", lines);
            if (!text.Contains(label, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var c in Around(pos)) if (StorageOps.IsChest(ctx.Block(c))) { yield return c; break; }
        }
    }

    private static IEnumerable<BlockPos> Around(BlockPos p) =>
        new[] { p.Down(), p.Offset(1, 0, 0), p.Offset(-1, 0, 0), p.Offset(0, 0, 1), p.Offset(0, 0, -1), p.Offset(1, -1, 0), p.Offset(-1, -1, 0), p.Offset(0, -1, 1), p.Offset(0, -1, -1) };

    public virtual string? CategoryOf(SkillContext ctx, BlockPos chest)
    {
        if (_assigned.TryGetValue(chest, out var c)) return c;
        foreach (var (pos, lines) in ctx.Game.Signs)
            if (Around(pos).Contains(chest) && StorageCategory.Parse(string.Join(" ", lines)) is { } cat) return cat;
        return null;
    }

    public void Assign(BlockPos chest, string category) => _assigned[chest] = category;

    public void Snapshot(BlockPos chest, IReadOnlyList<ItemStack> contents)
    {
        Snapshots[chest] = contents.ToArray();
        OnSnapshot?.Invoke(chest, contents);
    }
}

/// <summary>Chest presets: by_category, by_id, compact, columns.</summary>
public static class LayoutPresets
{
    public static readonly string[] Names = ["by_category", "by_id", "compact", "columns"];

    /// <summary>Target content per slot as indexes into <paramref name="stacks"/> (−1 empty), for a preset.</summary>
    public static int[] Target(string preset, IReadOnlyList<ItemStack> stacks)
    {
        int n = stacks.Count;
        var present = Enumerable.Range(0, n).Where(i => !stacks[i].IsEmpty).ToList();
        if (preset == "columns") return Stabilize(Columns(present, stacks, n), stacks);
        var target = Enumerable.Repeat(-1, n).ToArray();
        int at = 0;
        foreach (var i in Ordered(preset, present, stacks)) target[at++] = i;
        return Stabilize(target, stacks);
    }

    private static IEnumerable<int> Ordered(string preset, List<int> present, IReadOnlyList<ItemStack> stacks) => preset switch
    {
        "by_id" => present.OrderBy(i => stacks[i].Id).ThenBy(i => stacks[i].Damage).ThenByDescending(i => stacks[i].Count).ThenBy(i => i),
        "compact" => present.OrderBy(i => i),
        _ => present.OrderBy(i => StorageCategory.Rank(StorageCategory.Of(stacks[i].Id, stacks[i].Damage)))
            .ThenBy(i => stacks[i].Id).ThenBy(i => stacks[i].Damage).ThenByDescending(i => stacks[i].Count).ThenBy(i => i),
    };

    /// <summary>One item type per column (top to bottom), types in id order; types beyond 9 columns fill leftovers.</summary>
    private static int[] Columns(List<int> present, IReadOnlyList<ItemStack> stacks, int n)
    {
        var target = Enumerable.Repeat(-1, n).ToArray();
        int rows = Math.Max(1, n / 9);
        var groups = present.GroupBy(i => (stacks[i].Id, stacks[i].Damage)).OrderBy(g => g.Key.Id).ThenBy(g => g.Key.Damage).ToList();
        var leftovers = new List<int>();
        int col = 0;
        foreach (var g in groups)
        {
            var list = g.OrderBy(i => i).ToList();
            if (col >= 9) { leftovers.AddRange(list); continue; }
            for (int k = 0; k < list.Count; k++)
            {
                if (k < rows) target[k * 9 + col] = list[k];
                else leftovers.Add(list[k]);
            }
            col++;
        }
        FillFirstFree(target, leftovers);
        return target;
    }

    /// <summary>Each leftover into the next slot still free (in slot order); none left: dropped.</summary>
    private static void FillFirstFree(int[] target, List<int> leftovers)
    {
        int slot = 0, n = target.Length;
        foreach (var i in leftovers)
        {
            while (slot < n && target[slot] >= 0) slot++;
            if (slot < n) target[slot] = i;
        }
    }

    /// <summary>Target from an explicit layout: each layout slot takes a stack of its item (keeping stacks that are
    /// already in a matching slot), leftovers go to undesignated slots. Returns the target and the leftover count.</summary>
    public static (int[] Target, int Unplaced) Target(SlotLayout layout, IReadOnlyList<ItemStack> stacks)
    {
        int n = stacks.Count;
        var target = Enumerable.Repeat(-1, n).ToArray();
        var free = Enumerable.Range(0, n).Where(i => !stacks[i].IsEmpty).ToHashSet();
        KeepMatching(layout, stacks, target, free);
        FillDesignated(layout, stacks, target, free);
        KeepUndesignated(layout, target, free);
        int unplaced = PlaceRest(layout, target, free);
        return (Stabilize(target, stacks), unplaced);
    }

    /// <summary>Already right: keep.</summary>
    private static void KeepMatching(SlotLayout layout, IReadOnlyList<ItemStack> stacks, int[] target, HashSet<int> free)
    {
        for (int i = 0; i < target.Length && i < layout.Slots.Count; i++)
            if (layout.Slots[i] is { } want && free.Contains(i) && stacks[i].Id == want.Id && stacks[i].Damage == want.Damage)
            {
                target[i] = i;
                free.Remove(i);
            }
    }

    /// <summary>Each designated slot still open takes the biggest free stack of its item.</summary>
    private static void FillDesignated(SlotLayout layout, IReadOnlyList<ItemStack> stacks, int[] target, HashSet<int> free)
    {
        for (int i = 0; i < target.Length && i < layout.Slots.Count; i++)
        {
            if (target[i] >= 0 || layout.Slots[i] is not { } want) continue;
            var src = free.Where(j => stacks[j].Id == want.Id && stacks[j].Damage == want.Damage).OrderByDescending(j => stacks[j].Count).Cast<int?>().FirstOrDefault();
            if (src is { } s) { target[i] = s; free.Remove(s); }
        }
    }

    /// <summary>Leftovers stay in place when that slot isn't designated.</summary>
    private static void KeepUndesignated(SlotLayout layout, int[] target, HashSet<int> free)
    {
        foreach (var j in free.OrderBy(j => j).ToList())
        {
            bool designated = j < layout.Slots.Count && layout.Slots[j] is not null;
            if (!designated && target[j] < 0) { target[j] = j; free.Remove(j); }
        }
    }

    /// <summary>The rest go to the first undesignated empty target, else any empty one (counted unplaced); returns that count.</summary>
    private static int PlaceRest(SlotLayout layout, int[] target, HashSet<int> free)
    {
        int n = target.Length, unplaced = 0;
        foreach (var j in free.OrderBy(j => j))
        {
            int slot = Enumerable.Range(0, n).FirstOrDefault(i => target[i] < 0 && (i >= layout.Slots.Count || layout.Slots[i] is null), -1);
            if (slot < 0) { unplaced++; slot = Enumerable.Range(0, n).FirstOrDefault(i => target[i] < 0, -1); }
            if (slot >= 0) target[slot] = j;
        }
        return unplaced;
    }

    /// <summary>Same-item stacks are interchangeable: re-match them so a stack already in a slot that wants its item stays
    /// (maximizes fixed points → fewer clicks, and no same-item swaps that would merge instead).</summary>
    public static int[] Stabilize(int[] target, IReadOnlyList<ItemStack> stacks)
    {
        var t = (int[])target.Clone();
        foreach (var g in Enumerable.Range(0, t.Length).Where(i => t[i] >= 0).GroupBy(i => (stacks[t[i]].Id, stacks[t[i]].Damage)).ToList())
        {
            var slots = g.ToList();
            var srcs = slots.Select(i => t[i]).ToList();
            var assigned = new Dictionary<int, int>();
            foreach (var i in slots) if (srcs.Contains(i)) { assigned[i] = i; srcs.Remove(i); }
            // Prefer full stacks first in the remaining slots.
            srcs = srcs.OrderByDescending(s => stacks[s].Count).ThenBy(s => s).ToList();
            foreach (var i in slots.Where(i => !assigned.ContainsKey(i)).OrderBy(i => i)) { assigned[i] = srcs[0]; srcs.RemoveAt(0); }
            foreach (var (slot, src) in assigned) t[slot] = src;
        }
        return t;
    }
}

/// <summary>Short slot summary for results ("27/27 full").</summary>
internal static class Fill
{
    public static string Of(IReadOnlyList<ItemStack> container) => $"{container.Count(s => !s.IsEmpty)}/{container.Count}";
    public static bool Full(IReadOnlyList<ItemStack> container) => container.All(s => !s.IsEmpty && s.Count >= WindowModel.Max(s.Id));

    public static string Join(IEnumerable<string> parts) => new StringBuilder().AppendJoin("; ", parts).ToString();
}
