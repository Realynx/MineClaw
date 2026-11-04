using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>One fuel a furnace may burn: its items and how many items one unit smelts (the version's burn time ÷ 200 ticks).</summary>
public sealed record StoryFuel(ItemKey Item, string Name, double ItemsPerUnit)
{
    /// <summary>Units for <paramref name="items"/> smelted items (at least 1).</summary>
    public int Units(int items) => Math.Max(1, (int)Math.Ceiling(items / ItemsPerUnit - 1e-9));
}

/// <summary>
/// The furnace's fuel choice (docs/web/story-mode.md "Fuel"): a bench setting and an optional per-bill override. <c>auto</c> burns coal
/// or charcoal, then planks, then logs, and never one the builds or bills have reserved unless nothing else is there; a named choice
/// burns only that (coal, charcoal, coal_or_charcoal, planks, logs, lava_bucket, blaze_rod — what the version has); <c>any</c> is auto
/// plus blaze rods and lava buckets. A brewing stand's fuel is always blaze powder (one fuels 20 brews).
/// </summary>
public static class StoryFuels
{
    public const string Auto = "auto";
    public static readonly IReadOnlyList<string> Choices = [Auto, "coal", "charcoal", "coal_or_charcoal", "planks", "logs", "lava_bucket", "blaze_rod", "any"];

    private static StoryFuel? Fuel(IGameData d, int id, int meta, string name)
    {
        if (!d.ItemExists(id) || d.FuelTicks(id) <= 0) return null;
        return new StoryFuel(new ItemKey(id, meta), name, d.FuelTicks(id) / (double)Math.Max(1, d.CookTicks));
    }

    /// <summary>The fuels a choice allows, in the order a pawn tries them; empty when the version has none of it.</summary>
    public static IReadOnlyList<StoryFuel> For(IGameData d, string? choice)
    {
        StoryFuel?[] list = (choice ?? Auto) switch
        {
            "coal" => [Fuel(d, 263, 0, "coal")],
            "charcoal" => [Fuel(d, 263, 1, "charcoal")],
            "coal_or_charcoal" => [Fuel(d, 263, -1, "coal")],
            "planks" => [Fuel(d, 5, -1, "planks")],
            "logs" => [Fuel(d, 17, -1, "log"), Fuel(d, 162, -1, "log2")],
            "lava_bucket" => [Fuel(d, 327, 0, "lava_bucket")],
            "blaze_rod" => [Fuel(d, 369, 0, "blaze_rod")],
            "any" => [.. For(d, Auto), Fuel(d, 369, 0, "blaze_rod"), Fuel(d, 327, 0, "lava_bucket")],
            _ => [Fuel(d, 263, -1, "coal"), Fuel(d, 5, -1, "planks"), Fuel(d, 17, -1, "log"), Fuel(d, 162, -1, "log2")],
        };
        return list.Where(f => f != null).Select(f => f!).ToList();
    }

    /// <summary>The choices this version can burn (auto and any always).</summary>
    public static IReadOnlyList<string> ValidFor(IGameData d) => Choices.Where(c => c is Auto or "any" || For(d, c).Count > 0).ToList();

    public static bool Valid(IGameData d, string? choice) => choice != null && ValidFor(d).Contains(choice);

    /// <summary>"coal, charcoal, planks or logs" (the reason a bill waits for fuel).</summary>
    public static string Describe(string? choice) => (choice ?? Auto) switch
    {
        "coal" => "coal",
        "charcoal" => "charcoal",
        "coal_or_charcoal" => "coal or charcoal",
        "planks" => "planks",
        "logs" => "logs",
        "lava_bucket" => "lava buckets",
        "blaze_rod" => "blaze rods",
        "any" => "coal, charcoal, planks, logs, blaze rods or lava buckets",
        _ => "coal, charcoal, planks or logs",
    };

    /// <summary>The fuel table the recipe picker shows: each allowed fuel's item and how many items one smelts.</summary>
    public static JsonArray Json(IGameData d, string? choice) => new(For(d, choice).Select(f => (JsonNode)new JsonObject
    {
        ["item"] = f.Item.Json(), ["name"] = f.Name, ["items_per_unit"] = Math.Round(f.ItemsPerUnit, 3),
    }).ToArray());

    /// <summary>The brewing stand's fuel: blaze powder, 20 brews each.</summary>
    public const int BlazePowder = 377;
}
