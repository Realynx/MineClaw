using MineClaw.Data;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>Automatic smelting belongs to the colony. Split its uncommitted shortfall across free furnaces;
    /// a manual bill remains local and cannot reserve an otherwise idle station just by existing.</summary>
    private void ReconcileFurnaceTasks(List<Want> wants, P3 near, HashSet<(string, string)> placed)
    {
        var furnaces = Benches.Where(b => b.Kind == "furnace").OrderBy(b => b.Pos.Dist(near)).ToList();
        bool Busy(WorkbenchState b) => b.LoadedBill != null || _current.Values.Any(j => j.At == b.Pos && j.Source is "bill" or "furnace");
        var free = furnaces.Where(b => BenchAvailable(b) && !Busy(b) && !Skipped("bench:" + b.Pos) && FurnaceCanFuel(b)).ToList();
        var assignments = new Dictionary<(WorkbenchState Bench, string Build, string Recipe), Want>();
        foreach (var want in wants.Where(w => w.Bench == "furnace"))
        {
            placed.Add((want.Build, want.Recipe.Id));
            int left = want.Outputs;
            for (int i = 0; i < free.Count && left > 0; i++)
            {
                int n = (int)Math.Ceiling(left / (double)(free.Count - i));
                assignments[(free[i], want.Build, want.Recipe.Id)] = want with { Outputs = n };
                left -= n;
            }
        }
        foreach (var bench in furnaces)
        {
            bool changed = false;
            foreach (var bill in bench.Tasks.Where(t => t.ForBuild != NeedTag).ToList())
            {
                // Never move a real or promised load. Collection and cancellation keep their original bill identity.
                if (bench.LoadedBill == bill.Id || _current.Values.Any(j => j.SourceId == bill.Id)) continue;
                if (assignments.Remove((bench, bill.ForBuild!, bill.Recipe), out var want)) changed |= RetargetBill(bill, want);
                else { bench.Tasks.Remove(bill); changed = true; }
            }
            foreach (var (key, want) in assignments.Where(a => a.Key.Bench == bench))
            {
                bench.Tasks.Add(new BillState
                {
                    Id = NextId("bill"), Recipe = want.Recipe.Id, Mode = "count", Target = want.Outputs,
                    ForBuild = want.Build, ForItem = want.ForItem, Step = want.Step, Radius = 256, Reason = TaskReason(want),
                });
                changed = true;
            }
            if (changed) bench.Rev = Touch();
        }
    }

    private bool FurnaceCanFuel(WorkbenchState bench)
    {
        if (FuelCreditNow(bench) >= 1) return true;
        foreach (var fuel in StoryFuels.For(World!.Data, FuelChoice(bench, null)))
        {
            int units = fuel.Units(1);
            bool Matches(Stack s) => fuel.Item.Matches(s.Id, s.Meta);
            if (Storages.Where(s => ChestOpen(s.Chest) && s.Chest.Dist(bench.Pos) <= 256).SelectMany(s => FurnaceStock(s)).Where(Matches).Sum(s => s.Count) >= units
                || Pawns.Keys.Select(Host.Live).Where(p => p.Online).Any(p => p.Inventory.Where(Matches).Sum(s => s.Count) >= units)) return true;
        }
        return false;
    }

    private Dictionary<(int Id, int Meta), int>? NamedFurnaceFuelAllowance(Dictionary<(int Id, int Meta), int> hidden)
    {
        var allowed = Benches.Where(b => b.Kind == "furnace" && BenchAvailable(b) && FuelChoice(b, null) is not (StoryFuels.Auto or "any"))
            .SelectMany(b => StoryFuels.For(World!.Data, FuelChoice(b, null))).Select(f => f.Item).ToList();
        return allowed.Count == 0 ? null : hidden.Where(p => allowed.Any(f => f.Matches(p.Key.Id, p.Key.Meta))).ToDictionary();
    }

    /// <summary>The storage ledger may still contain items another pawn is fetching. Furnace inputs and fuel
    /// reserve exact variants, including another half of the same chest, before choosing the next load.</summary>
    private IEnumerable<Stack> FurnaceStock(StorageState store, IReadOnlyList<(P3 Chest, Stack Stack)>? pending = null)
    {
        foreach (var group in store.Contents.GroupBy(s => (s.Id, s.Meta)))
        {
            int reserved = InFlightFrom(store, group.Key).InFlight
                + (pending?.Where(t => (t.Chest == store.Chest || t.Chest == store.DoubleWith) && t.Stack.Id == group.Key.Id && t.Stack.Meta == group.Key.Meta)
                    .Sum(t => t.Stack.Count) ?? 0);
            int n = group.Sum(s => s.Count) - reserved;
            if (n > 0) yield return group.First() with { Count = n };
        }
    }

    private IEnumerable<(P3? Chest, Stack Stack)> FurnaceInputs(BillState bill, RecipeInput input, WorkbenchState bench, PawnLive live,
        IReadOnlyList<(P3 Chest, Stack Stack)>? pending = null)
    {
        bool Fits(Stack stack) => input.Options.Any(o => o.Matches(stack.Id, stack.Meta)) && Cats!.Accepts(bill.Ingredients, stack.Id, stack.Meta);
        foreach (var stack in live.Inventory.Where(Fits)) yield return (null, stack);
        foreach (var store in Storages.Where(s => s.Chest.Dist(bench.Pos) <= bill.Radius && ChestOpen(s.Chest)))
            foreach (var stack in FurnaceStock(store, pending).Where(Fits)) yield return (store.Chest, stack);
    }

    private int FurnaceBatchSize(BillState bill, StoryRecipe recipe, WorkbenchState bench, PawnLive live, int wanted,
        IReadOnlyList<(P3 Chest, Stack Stack)>? pending = null)
    {
        var wood = WoodBudget(bill, recipe);
        foreach (var input in recipe.Inputs)
        {
            // A furnace slot holds one exact input variant. Different logs cannot jointly fill one load.
            int available = FurnaceInputs(bill, input, bench, live, pending).GroupBy(s => (s.Stack.Id, s.Stack.Meta))
                .Select(g => WoodAvailable(wood, g.Key.Id, g.Key.Meta, g.Sum(s => s.Stack.Count))).DefaultIfEmpty().Max();
            wanted = Math.Min(wanted, available / Math.Max(1, input.Count));
        }
        return Math.Max(1, wanted);
    }

    private (List<(P3? Chest, Stack Stack)> Got, string? Missing) FurnaceIngredients(BillState bill, StoryRecipe recipe, int crafts, WorkbenchState bench, PawnLive live,
        IReadOnlyList<(P3 Chest, Stack Stack)>? pending = null)
    {
        var got = new List<(P3? Chest, Stack Stack)>();
        var wood = WoodBudget(bill, recipe);
        foreach (var input in recipe.Inputs)
        {
            int need = input.Count * crafts;
            var variants = FurnaceInputs(bill, input, bench, live, pending).GroupBy(s => (s.Stack.Id, s.Stack.Meta));
            var chosen = variants.OrderByDescending(g => WoodAvailable(wood, g.Key.Id, g.Key.Meta, g.Sum(s => s.Stack.Count))).FirstOrDefault();
            if (chosen == null || WoodAvailable(wood, chosen.Key.Id, chosen.Key.Meta, chosen.Sum(s => s.Stack.Count)) < need)
                return (got, chosen != null && wood != null ? ReservedText(input, recipe, 0) : MissingText(input, bill.Radius, bench));
            foreach (var (chest, stack) in chosen.OrderBy(s => s.Chest?.Dist(bench.Pos) ?? -1))
            {
                int n = Math.Min(need, stack.Count);
                if (n > 0) { got.Add((chest, stack with { Count = n })); WoodTaken(wood, stack with { Count = n }); need -= n; }
                if (need == 0) break;
            }
        }
        return (got, null);
    }
}
