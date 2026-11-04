using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Data;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// The acquisition planner's doors into the tools: a hidden <c>acquire {what, count?}</c> (no tier sees its definition, so
/// no prompt bytes; scripts' <c>ensure</c> and <c>acquire{}</c> call it) and the <c>mine</c> pre-step that gets the tool a
/// block needs before mining it (so <c>do mine</c>, worker and script mines don't fail for a missing pickaxe).
/// </summary>
public static class AcquireTool
{
    public const string ToolName = "acquire";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Get a tool kind (pickaxe/axe/shovel/sword/food) or N of an item: inventory, chests, hive, craft, bootstrap, ask.",
            ToolSchema.Create().Str("what").Int("count", required: false).Int("tier", "min harvest level 0 wood 1 stone 2 iron", required: false).Build(),
            async (i, ct) =>
            {
                var what = i.ReqStr("what").Trim();
                var (n, name) = Items.ParseAmount(what);
                if (n != null && !Loadout.Kinds.ContainsKey(what) && Items.ByNameWithDamage(what) == null) what = name;
                var r = await Acquisition.Planner(c).AcquireAsync(new Need(what, Math.Clamp(i.Int("count") ?? n ?? 1, 1, 576),
                    Math.Clamp(i.Int("tier") ?? 0, 0, 3)), ct).ConfigureAwait(false);
                return r.Ok ? ToolResult.Success(r.Text) : ToolResult.Error(r.Text);
            }, longRunning: true, tiers: ToolTiers.None);
    }

    /// <summary>
    /// Before mining <paramref name="blockName"/>: when no carried tool can harvest it (stone, cobble, ores need a pickaxe of
    /// the right tier), acquire one. Null = nothing needed; else (ok, note) — the note leads the mine result.
    /// </summary>
    public static async Task<(bool Ok, string Note)?> ToolForBlockAsync(IGameClient c, string blockName, CancellationToken ct)
    {
        // The version's harvest rule (CanHarvest is a GameData facade): bare hands get nothing from it = it needs a tool.
        if (Blocks.ByName(blockName) is not { } b || Tools.CanHarvest(b.Id, -1) || CarriesToolFor(c, b.Id)) return null;
        if (ToolKindFor(b) is not { } kind) return null;
        var r = await Acquisition.Planner(c).AcquireAsync(new Need(kind, 1, ToolTierFor(b)), ct).ConfigureAwait(false);
        return (r.Ok, r.Ok ? r.Text : $"no {kind} for {Names.Block(b.Id)}: {r.Text}");
    }

    private static bool CarriesToolFor(IGameClient c, int blockId)
    {
        foreach (var s in c.World.Inventory.PlayerSlots())
            if (!s.IsEmpty && Items.Exists(s.Id) && Items.Get(s.Id).Tool != ToolType.None && Tools.CanHarvest(blockId, s.Id)) return true;
        return false;
    }

    /// <summary>The tool kind a block needs ("pickaxe", "shovel", "axe"), or null.</summary>
    internal static string? ToolKindFor(BlockInfo b) =>
        Tools.PickaxeEffective.Contains(b.Id) || b.Id is Blocks.Obsidian or Blocks.RedstoneOre or Blocks.LitRedstoneOre ? "pickaxe"
        : b.Id is Blocks.SnowLayer or Blocks.SnowBlock ? "shovel"
        // Blocks past b1.8.1's ids (1.12.2): the version's own best tool.
        : b.Id > Blocks.MaxId ? KindOf(b.BestTool)
        : null;

    private static string? KindOf(ToolType t) => t switch { ToolType.Pickaxe => "pickaxe", ToolType.Shovel => "shovel", ToolType.Axe => "axe", _ => null };

    /// <summary>The pickaxe tier a b1.8.1 ore/block needs (1.12.2 blocks: their own harvest level).</summary>
    private static readonly Dictionary<int, int> PickaxeTiers = new()
    {
        [Blocks.Obsidian] = 3,
        [Blocks.DiamondOre] = 2, [Blocks.DiamondBlock] = 2, [Blocks.GoldOre] = 2, [Blocks.GoldBlock] = 2, [Blocks.RedstoneOre] = 2, [Blocks.LitRedstoneOre] = 2,
        [Blocks.IronOre] = 1, [Blocks.IronBlock] = 1, [Blocks.LapisOre] = 1, [Blocks.LapisBlock] = 1,
    };

    internal static int ToolTierFor(BlockInfo b) =>
        PickaxeTiers.TryGetValue(b.Id, out var tier) ? tier : b.Id > Blocks.MaxId ? Math.Max(0, b.MinHarvestLevel) : 0;
}
