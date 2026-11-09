using MineClaw.Data;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private sealed class PriorityFuel
    {
        public List<Stack> Own { get; } = [];
        public List<(P3 Chest, Stack Stack)> Stored { get; } = [];
    }

    /// <summary>
    /// A cook can be scanned before a crafter because of pawn work settings. Keep the next runnable higher-priority
    /// furnace batch's fuel available anyway. These are prospective allocations only; actual jobs already reserve Takes.
    /// A paused, finished, unavailable or ingredient-starved bill cannot hold fuel hostage.
    /// </summary>
    private PriorityFuel HigherPriorityFuel(WorkbenchState requestingBench, BillState requestingBill, PawnLive? live)
    {
        var held = new PriorityFuel();
        var inputClaims = new PriorityFuel();
        int priority = BillWorkPriority(requestingBill);
        if (priority == 0 || World is not { } w) return held;
        var candidates = Benches.Where(b => b.Kind == "furnace" && BenchAvailable(b) && !Skipped("bench:" + b.Pos))
            .SelectMany(bench => BenchQueue(bench).Select(bill => (Bench: bench, Bill: bill)))
            .Where(x => x.Bill != requestingBill && BillWorkPriority(x.Bill) < priority)
            .OrderBy(x => BillWorkPriority(x.Bill)).ThenBy(x => x.Bench.Pos.Dist(requestingBench.Pos));
        var allocated = new HashSet<WorkbenchState>();
        foreach (var (bench, bill) in candidates)
        {
            if (allocated.Contains(bench) || bill.Paused || _billFails.ContainsKey(bill.Id)
                || bill.SuspendedReason != null && !ExpectedBillShortage(bill.SuspendedReason)
                || _current.Values.Any(j => j.At == bench.Pos && j.Source is "bill" or "furnace")
                || bench.LoadedBill != null && (bench.LoadedBill != bill.Id || bench.RefuelCount <= 0)
                || StoryRecipes.Get(w.Data, bill.Recipe) is not { Fuel: true } recipe
                || !HasBillWorker(bill, recipe.WorkType, w.Dimension)) continue;
            var claims = held.Stored.Concat(inputClaims.Stored).ToArray();
            // Another eligible pawn cannot spend this searcher's pockets. A restricted bill may
            // reserve shared stock, but uses these carried items only if this pawn can execute it.
            bool usePockets = live != null && _pickPawn is { } name && Pawns.TryGetValue(name, out var pawn)
                && CanWorkBill(pawn, bill, recipe.WorkType, w.Dimension);
            var pockets = (live ?? PawnLive.Offline) with { Inventory = usePockets
                ? WithoutFuelClaims(live!.Inventory, held.Own.Concat(inputClaims.Own).ToArray()) : [] };
            var batch = new BillBatch();
            if (bench.RefuelCount > 0) batch = new BillBatch { Crafts = bench.RefuelCount, Got = [] };
            else
            {
                if (!Wanted(bill, recipe)) continue;
                batch.Crafts = FurnaceBatchSize(bill, recipe, bench, pockets, BatchSize(bill, recipe, bench), claims);
                (batch.Got, batch.Missing) = FurnaceIngredients(bill, recipe, batch.Crafts, bench, pockets, claims);
                if (batch.Missing == null && bill.ForBuild == null && _reserved.Count > 0) FitReserved(bill, recipe, bench, pockets, ref batch);
                if (batch.Missing != null) continue;
                (batch.Got, batch.Missing) = FurnaceIngredients(bill, recipe, batch.Crafts, bench, pockets, claims);
                if (batch.Missing != null) continue;
            }
            var inputs = batch.Got.Where(g => g.Item1 != null).Select(g => (g.Item1!.Value, g.Item2)).Concat(claims).ToArray();
            var (fuel, units, from, missing) = PickFuel(bench, bill, batch.Crafts, pockets,
                batch.Got.Where(g => g.Item1 == null).Select(g => g.Item2).ToArray(), inputs, protectPriority: false);
            if (missing != null) continue;
            allocated.Add(bench);
            inputClaims.Stored.AddRange(batch.Got.Where(g => g.Chest != null).Select(g => (g.Chest!.Value, g.Stack)));
            inputClaims.Own.AddRange(batch.Got.Where(g => g.Chest == null).Select(g => g.Stack));
            if (fuel == null) continue; // Existing burn credit still commits this batch's ingredients.
            if (from is { } stored) held.Stored.Add(stored);
            int own = units - (from?.Stack.Count ?? 0);
            foreach (var stack in pockets.Inventory.Where(s => fuel.Item.Matches(s.Id, s.Meta)))
            {
                int n = Math.Min(own, stack.Count);
                if (n > 0) held.Own.Add(stack with { Count = n });
                own -= n;
                if (own <= 0) break;
            }
        }
        held.Own.AddRange(inputClaims.Own);
        held.Stored.AddRange(inputClaims.Stored);
        return held;
    }

    private bool HasBillWorker(BillState bill, string work, int dimension) => Pawns.Values.Any(p => CanWorkBill(p, bill, work, dimension));

    private bool CanWorkBill(PawnState p, BillState bill, string work, int dimension) =>
        !p.Drafted && !_leaving.ContainsKey(p.Name) && p.Priorities.GetValueOrDefault(work) is >= 1 and <= 4
        && MayTake(bill, p, work) && ScheduleBlockAt(p, HourNow) is ScheduleBlocks.Work or ScheduleBlocks.Anything
        && Host.Live(p.Name) is { Online: true } live && live.Dim == dimension;

    private static IReadOnlyList<Stack> WithoutFuelClaims(IReadOnlyList<Stack> inventory, IReadOnlyList<Stack> claimed) =>
        inventory.GroupBy(s => (s.Id, s.Meta)).Select(g => g.First() with
        {
            Count = Math.Max(0, g.Sum(s => s.Count) - claimed.Where(s => s.Id == g.Key.Id && s.Meta == g.Key.Meta).Sum(s => s.Count)),
        }).Where(s => s.Count > 0).ToArray();
}
