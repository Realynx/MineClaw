using System.Text.Json.Nodes;
using MineClaw.Data;

namespace MineClaw.Hive.Story;

/// <summary>
/// Furnace fuel (docs/web/story-mode.md "Fuel"): the bench's choice and a bill's override (<see cref="StoryFuels"/>), the fuel a job
/// brings (the pawn's own first, then a chest within the bill's radius; auto and any leave what the builds and bills reserved unless
/// nothing else is there), what the furnace still holds (<see cref="WorkbenchState.FuelCredit"/>: a job tops up only what the batch
/// needs), and the fuel need each bill shows (need / have / short).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>The fuel choice a bill burns: its override, else the furnace's, else auto.</summary>
    internal static string FuelChoice(WorkbenchState bench, BillState? bill) => bill?.Fuel ?? bench.FuelSetting ?? StoryFuels.Auto;

    /// <summary>
    /// The fuel for <paramref name="items"/> smelts at <paramref name="bench"/>: the fuel, the units the job loads (0 = the furnace still
    /// holds enough), where the rest comes from (a chest, or null = the pawn's own), or why there is none.
    /// </summary>
    internal (StoryFuel? Fuel, int Units, (P3 Chest, Stack Stack)? From, string? Missing) PickFuel(WorkbenchState bench, BillState bill, int items, PawnLive? live)
    {
        var d = World!.Data;
        var choice = FuelChoice(bench, bill);
        var fuels = StoryFuels.For(d, choice);
        if (fuels.Count == 0) return (null, 0, null, $"needs fuel: {d.VersionId} has no {StoryFuels.Describe(choice)}");
        int net = items - (int)Math.Floor(bench.FuelCredit + 1e-9);
        if (net <= 0) return (fuels[0], 0, null, null);
        // Auto (and any) leave the builds' and bills' reserved stock alone unless nothing else is there; a named fuel is what was asked.
        bool guard = bill.ForBuild == null && choice is StoryFuels.Auto or "any" && _reserved.Count > 0;
        for (int pass = guard ? 0 : 1; pass < 2; pass++)
            if (FuelPass(fuels, net, bench, bill, live, unreservedOnly: pass == 0) is { } got) return got;
        return (null, 0, null, $"needs fuel: no {StoryFuels.Describe(choice)} within {bill.Radius} blocks");
    }

    /// <summary>The first fuel the pawn carries enough of, or a chest within the bill's radius holds the rest of (with
    /// <paramref name="unreservedOnly"/>: only from what the builds and bills haven't reserved).</summary>
    private (StoryFuel? Fuel, int Units, (P3 Chest, Stack Stack)? From, string? Missing)? FuelPass(IReadOnlyList<StoryFuel> fuels, int net, WorkbenchState bench,
        BillState bill, PawnLive? live, bool unreservedOnly)
    {
        foreach (var f in fuels)
        {
            int units = f.Units(net);
            bool Ok(Stack s) => f.Item.Matches(s.Id, s.Meta);
            int own = live?.Inventory.Where(Ok).Sum(s => s.Count) ?? 0;
            if (own >= units) return (f, units, null, null);
            if (FindStored(s => Ok(s) && s.Count >= units - own, bench.Pos, bill.Radius) is not { } fs) continue;
            if (unreservedOnly && Unreserved((id, meta) => f.Item.Matches(id, meta)) < units - own) continue;
            return (f, units, (fs.Store.Chest, fs.Stack with { Count = units - own }), null);
        }
        return null;
    }

    /// <summary>A furnace load done: what the fuel it brought leaves over for the next batch.</summary>
    private static void FuelLoaded(WorkbenchState bench, ActiveJob j)
    {
        double per = j.Json["fuel_per"]?.GetValue<double>() ?? 0;
        int units = j.Json["fuel_units"]?.GetValue<int>() ?? 0, items = j.Json["n"]?.GetValue<int>() ?? 0;
        bench.FuelCredit = Math.Max(0, bench.FuelCredit + units * per - items);
    }

    /// <summary>
    /// A bill's fuel for its next batch, as the bills inspector shows it: <c>{choice, item, name, need, have, short, per_item}</c> (the
    /// fuel it would burn, the units, the colony's stock of it, short when no allowed fuel covers it); a brewing stand's blaze powder
    /// (one per 20 brews). Null for other benches.
    /// </summary>
    internal JsonObject? FuelNeedJson(WorkbenchState bench, BillState bill)
    {
        if (World is not { } w || StoryRecipes.Get(w.Data, bill.Recipe) is not { } r) return null;
        if (bench.Kind == "brewing_stand" && r.Station?.Kind == "brew") return BrewFuelJson(bench, r);
        if (bench.Kind != "furnace" || !r.Fuel) return null;
        return FurnaceFuelJson(w, bench, bill, r);
    }

    private JsonObject BrewFuelJson(WorkbenchState bench, StoryRecipe r)
    {
        int brews = Brewing.Chain(r.Output.Id, r.Station!.Potion!)?.Count ?? 1;
        int haveP = CountItem(StoryFuels.BlazePowder, -1);
        bool known = bench.Fuel is { } f && f >= brews;
        return new JsonObject
        {
            ["choice"] = "blaze_powder", ["item"] = new JsonArray(StoryFuels.BlazePowder, 0), ["name"] = "blaze powder",
            ["need"] = known ? 0 : 1, ["have"] = haveP, ["short"] = !known && haveP == 0, ["per_item"] = 1.0 / Brewing.BrewsPerPowder,
        };
    }

    private JsonObject FurnaceFuelJson(IStoryWorld w, WorkbenchState bench, BillState bill, StoryRecipe r)
    {
        int left = bill.Mode == "count" ? bill.Target - bill.Done : bill.Mode == "until" ? bill.Target - StockOf(r) : 8;
        int items = Math.Clamp(left, 1, 8);
        var choice = FuelChoice(bench, bill);
        var fuels = StoryFuels.For(w.Data, choice);
        var (fuel, units, _, missing) = PickFuel(bench, bill, items, null);
        var shown = fuel ?? fuels.FirstOrDefault();
        int net = Math.Max(0, items - (int)Math.Floor(bench.FuelCredit + 1e-9));
        return new JsonObject
        {
            ["choice"] = choice, ["item"] = shown?.Item.Json(), ["name"] = shown?.Name,
            ["need"] = fuel != null ? units : shown?.Units(net) ?? 0,
            ["have"] = shown != null ? CountItem(shown.Item.Id, shown.Item.Meta) : 0,
            ["short"] = missing != null, ["per_item"] = shown != null ? Math.Round(1 / shown.ItemsPerUnit, 3) : null,
        };
    }

    /// <summary>
    /// A loaded furnace as the colony expects it (nobody watches it smelt): <c>{recipe, input:[id,meta,n], output:[id,meta,n], loaded_at,
    /// ready_at, estimated: true}</c>, an item every 10 s since it was loaded; null when it isn't loaded.
    /// </summary>
    private JsonObject? SmeltingJson(WorkbenchState bench)
    {
        if (bench.Kind != "furnace" || bench.LoadedBill == null || World is not { } w) return null;
        var r = (bench.LoadedRecipe ?? bench.Bills.FirstOrDefault(b => b.Id == bench.LoadedBill)?.Recipe) is { } rid ? StoryRecipes.Get(w.Data, rid) : null;
        int done = bench.LoadedAt is { } at ? Math.Clamp((int)((Now - at).TotalSeconds / StoryRecipes.SmeltSeconds), 0, bench.LoadedCount) : 0;
        var input = r?.Inputs.FirstOrDefault()?.Options.FirstOrDefault();
        return new JsonObject
        {
            ["recipe"] = r?.Id,
            ["input"] = input is { } i ? new JsonArray(i.Id, Math.Max(0, i.Meta), bench.LoadedCount - done) : null,
            ["output"] = r != null ? new JsonArray(r.Output.Id, r.Output.Meta, done) : null,
            ["loaded_at"] = bench.LoadedAt, ["ready_at"] = bench.ReadyAt, ["estimated"] = true,
        };
    }

    /// <summary><c>POST …/workbenches {op:"fuel", pos, fuel}</c>: a furnace's fuel choice (auto, coal, charcoal, coal_or_charcoal, planks,
    /// logs, lava_bucket, blaze_rod, any — what the version has).</summary>
    private (JsonNode? Result, string? Error) SetBenchFuel(WorkbenchState bench, string? fuel)
    {
        var d = World!.Data;
        if (bench.Kind != "furnace") return (null, $"only a furnace takes a fuel choice (a {Pretty(bench.Kind)}{(bench.Kind == "brewing_stand" ? " burns blaze powder" : " burns nothing")})");
        if (!StoryFuels.Valid(d, fuel)) return (null, $"fuel must be {string.Join("|", StoryFuels.ValidFor(d))}");
        bench.FuelSetting = fuel == StoryFuels.Auto ? null : fuel;
        bench.Rev = Touch();
        foreach (var b in bench.Bills.Where(b => b.SuspendedReason?.StartsWith("needs fuel", StringComparison.Ordinal) == true)) b.SuspendedReason = null;
        MaterialsChanged();
        WakeIdle();
        return (new JsonObject { ["pos"] = bench.Pos.Json(), ["kind"] = bench.Kind, ["designated"] = true, ["fuel"] = fuel }, null);
    }
}
