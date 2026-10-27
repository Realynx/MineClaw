using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>craft</c> on top of the client's planner-driven <see cref="IGameClient.CraftAsync"/>, filling its gaps:
/// smelting prerequisites run first (placing a carried furnace if none is near), and a crafting table is placed
/// from the inventory (crafted from planks if needed) when a 3x3 recipe needs one and none is in range.
/// </summary>
public sealed class Crafter(IGameClient client)
{
    public async Task<ActionResult> CraftAsync(string itemName, int count, CancellationToken ct)
    {
        if (Items.ByNameWithDamage(itemName) is not { } r) return ActionResult.Fail($"unknown item '{itemName}'");
        int id = r.Item.Id, dmg = r.Damage;
        string name = Names.Item(id, Math.Max(dmg, 0));
        if (Recipes.ForOutput(id, dmg).Count == 0) return ActionResult.Fail($"{name} has no crafting recipe");

        var plan = Plan(id, dmg, count, smelting: false);
        var notes = new List<string>();
        if (!plan.Complete)
        {
            var withSmelt = Plan(id, dmg, count, smelting: true);
            if (!withSmelt.Complete) return ActionResult.Fail($"missing {string.Join(", ", plan.Missing)} for {count} {name}");
            var furnace = await EnsureStationAsync(Blocks.Furnace, ct).ConfigureAwait(false);
            if (!furnace.Ok) return ActionResult.Fail($"need smelting ({string.Join("; ", withSmelt.Steps.Where(s => s.IsSmelt))}) but {furnace.Message}");
            if (furnace.Note != null) notes.Add(furnace.Note);
            foreach (var step in withSmelt.Steps.Where(s => s.IsSmelt))
            {
                var s = step.Smelt!;
                var res = await client.SmeltAsync(s.InputId, step.Times, null, wait: true, furnace: furnace.Pos, ct: ct).ConfigureAwait(false);
                if (!res.Ok) return ActionResult.Fail($"smelting {Names.Item(s.InputId)} failed: {res.Message}");
                notes.Add($"smelted {Names.Item(s.OutputId, s.OutputDamage)} x{step.OutputCount}");
            }
            plan = Plan(id, dmg, count, smelting: false);
            if (!plan.Complete) return ActionResult.Fail($"{Join(notes)}still missing {string.Join(", ", plan.Missing)}");
        }

        if (plan.Steps.Any(s => s.RequiresCraftingTable))
        {
            var table = await EnsureStationAsync(Blocks.Workbench, ct).ConfigureAwait(false);
            if (!table.Ok) return ActionResult.Fail($"{name} needs a crafting table: {table.Message}");
            if (table.Note != null) notes.Add(table.Note);
        }

        var result = await client.CraftAsync(id, dmg, count, ct).ConfigureAwait(false);
        return notes.Count == 0 ? result : result with { Message = Join(notes) + result.Message };
    }

    private CraftPlan Plan(int id, int dmg, int count, bool smelting) =>
        RecipePlanner.Plan(id, count, client.World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).ToList(), dmg,
            new PlannerOptions { AllowSmelting = smelting, HasCraftingTable = true });

    private static string Join(List<string> notes) => notes.Count == 0 ? "" : string.Join("; ", notes) + "; ";

    public sealed record Station(bool Ok, BlockPos? Pos, string Message, string? Note = null);

    /// <summary>Finds a station block in range, else places one from the inventory (crafting a table from planks if needed).</summary>
    public async Task<Station> EnsureStationAsync(int blockId, CancellationToken ct)
    {
        var w = client.World;
        int[] ids = blockId == Blocks.Furnace ? [Blocks.Furnace, Blocks.LitFurnace] : [blockId];
        if (w.FindNearestBlock(w.Player.Position, client.StationSearchRadius, ids) is { } found) return new(true, found.Pos, "ok");

        string what = Names.Block(blockId);
        string? crafted = null;
        if (w.Inventory.CountItem(blockId) == 0)
        {
            if (blockId != Blocks.Workbench) return new(false, null, $"no {what} within {client.StationSearchRadius:0} blocks and none carried");
            var c = await client.CraftAsync(Blocks.Workbench, -1, 1, ct).ConfigureAwait(false);
            if (!c.Ok) return new(false, null, $"none near or carried, and can't craft one: {c.Message}");
            crafted = "crafted a crafting table";
        }

        if (FindPlacementSpot() is not { } spot) return new(false, null, $"no safe spot to place a {what}");
        var p = await client.PlaceBlockAsync(spot.X, spot.Y, spot.Z, blockId, ct).ConfigureAwait(false);
        if (!p.Ok) return new(false, null, $"couldn't place {what}: {p.Message}");
        var note = $"placed {what} at {Locations.Fmt(spot)}";
        return new(true, spot, "ok", crafted != null ? crafted + ", " + note : note);
    }

    /// <summary>An air cell next to the bot (not its own column) with solid ground under it and air above, nearest first.</summary>
    public BlockPos? FindPlacementSpot()
    {
        var w = client.World;
        var feet = w.Player.Position.ToBlockPos();
        var candidates = new List<BlockPos>();
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        {
            if (dx == 0 && dz == 0) continue;
            for (int dy = 0; dy >= -1; dy--)
                candidates.Add(new BlockPos(feet.X + dx, feet.Y + dy, feet.Z + dz));
        }
        foreach (var c in candidates.OrderBy(c => c.DistanceSquaredTo(feet)))
        {
            if (w.GetBlock(c) != 0 || w.GetBlock(c.Up()) != 0) continue;
            if (!w.Info(w.GetBlock(c.Down())).Solid) continue;
            return c;
        }
        return null;
    }
}

/// <summary>Short lower-case names for results.</summary>
public static class Names
{
    public static string Item(int id, int damage = 0) => Items.DisplayName(id, damage).ToLowerInvariant();
    public static string Block(int id) => Blocks.Exists(id) ? Blocks.Get(id).DisplayName.ToLowerInvariant() : "block " + id;
}
