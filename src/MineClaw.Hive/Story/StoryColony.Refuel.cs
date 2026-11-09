using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MineClaw.Hive.Story;

/// <summary>
/// A furnace load left without fuel (docs/web/story-mode.md "Fuel"). Live 11-04 story-1 (release 20261104-181056): "collect_furnace
/// 218,64,399 failed: nothing to collect; furnace (3 slots, 2 free) progress 0%; in=oak wood x1 fuel=- out=-" ×108 in a few hours, a
/// collect every 20 s that rested pawns and the bench and never smelted anything. A collect that finds nothing to collect is no failure:
/// what the furnace showed says what it is. Its input with no fuel: a refuel job brings the furnace's fuel (its choice) for that input
/// (the smelt primitive with count 0 only tops up the fuel). Empty: the load is gone. Else it is still smelting.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>The furnace slots in the collect's "nothing to collect; furnace (…) progress 0%[ burning]; in=oak wood x1 fuel=- out=-".</summary>
    private static readonly Regex FurnaceSlots = new(@"in=(?:-|(?<in>.+?) x(?<n>\d+)) fuel=(?<fuel>\S+)", RegexOptions.Compiled);

    /// <summary>A job that brings fuel to a loaded furnace (no input).</summary>
    private static bool IsRefuel(ActiveJob j) => j is { Source: "furnace", Kind: "smelt" };

    /// <summary>A loaded furnace's job: fuel for a load left without it, collecting one that is done; null while it smelts.</summary>
    private ActiveJob? LoadedFurnaceJob(IStoryWorld w, PawnLive live, string work, WorkbenchState bench, string lb, string key)
    {
        if (bench.RefuelCount > 0) return RefuelJob(w, live, work, bench, lb, key);
        return bench.ReadyAt <= Now ? CollectJob(w, bench, lb, work, key) : null;
    }

    /// <summary>
    /// Fuel for the input that waits in the furnace: the bill's (or the furnace's) choice for that many items, the pawn's own first, else a
    /// chest. No fuel: the idle audit says so ("the furnace at … holds 1 oak wood and no fuel: needs fuel: …").
    /// </summary>
    private ActiveJob? RefuelJob(IStoryWorld w, PawnLive live, string work, WorkbenchState bench, string lb, string key)
    {
        var bill = BillById(bench, lb);
        var lr = (bench.LoadedRecipe ?? bill?.Recipe) is { } rid ? StoryRecipes.Get(w.Data, rid) : null;
        if ((lr?.WorkType ?? WorkTypes.Craft) != work) return null;
        int n = bench.RefuelCount;
        string what = $"{n} {bench.RefuelItem ?? "items"}";
        var (fuel, units, src, noFuel) = PickFuel(bench, bill ?? new BillState(), n, live);
        if (noFuel != null)
        {
            if (bill?.Mode == "forever" && lr != null) SuspendBill(bench, bill, lr, noFuel, expectedShortage: true);
            else Blocked($"the furnace at {bench.Pos} holds {what} and no fuel: {noFuel}");
            return null;
        }
        if (bill?.WaitingReason != null) { bill.WaitingReason = null; bench.Rev = Touch(); }
        // The input only names a smeltable item for the primitive; with count 0 it loads no input, it fuels what the furnace holds.
        var input = lr?.Inputs.FirstOrDefault()?.Options.FirstOrDefault() is { } o ? ItemName(o.Id, Math.Max(0, o.Meta)) : bench.RefuelItem ?? "log";
        var job = Job(work, "smelt", $"fuelling the furnace ({what} waiting)", bench.Pos, key, "furnace", lb, "working", f =>
        {
            f["item"] = input;
            f["n"] = 0;
            f["refuel"] = n;
            f["fetch"] = new JsonArray();
            f["recipe"] = lr?.Id;
        }, xp: 5);
        AddFuel(job, fuel, units, src);
        return job;
    }

    /// <summary>Fuelled: the waiting input smelts from now (an item every 10 s), on the fuel it brought.</summary>
    private void Refuelled(ActiveJob j)
    {
        if (Benches.FirstOrDefault(b => b.Pos == j.At && b.Kind == "furnace") is not { } bench) return;
        int n = j.Json["refuel"]?.GetValue<int>() ?? bench.RefuelCount;
        FuelLoaded(bench, j);
        bench.RefuelCount = 0;
        bench.RefuelItem = null;
        bench.LoadedAt = Now;
        bench.ReadyAt = Now + TimeSpan.FromSeconds(StoryRecipes.SmeltSeconds * Math.Max(1, n));
        bench.Rev = Touch();
        _log.LogInformation("story {Colony}: the furnace at {At} is fuelled for its {N} waiting item(s)", Id, bench.Pos, n);
    }

    /// <summary>A refuel that failed (no fuel came, short at the furnace): what the colony thought the furnace held isn't there.</summary>
    private void RefuelFailed(ActiveJob j, string why)
    {
        if (Benches.FirstOrDefault(b => b.Pos == j.At && b.Kind == "furnace") is not { } bench) return;
        if (why.Contains("fuel", StringComparison.OrdinalIgnoreCase)) NoFuelLeft(bench);
        bench.Rev = Touch();
    }

    /// <summary>
    /// A collect that found nothing to collect, or a refuel that found nothing to fuel, at a loaded furnace: no failure (no rest for the
    /// pawn or the bench). Empty: the load is gone (someone took it). Its input, no fuel and not burning: it waits for a refuel. Else it is
    /// still smelting: looked at again when its input should be done. True = handled.
    /// </summary>
    private bool FurnaceNotReady(ActiveJob j, string why)
    {
        if (j.Source != "furnace" || !(why.StartsWith("nothing to collect", StringComparison.Ordinal) || why.StartsWith("nothing to fuel", StringComparison.Ordinal)))
            return false;
        var bench = Benches.FirstOrDefault(b => b.Kind == "furnace" && b.Pos == j.At && b.LoadedBill == j.SourceId);
        var m = FurnaceSlots.Match(why);
        if (bench == null || !m.Success) return false;
        if (!m.Groups["in"].Success) FurnaceEmptied(bench);
        else if (m.Groups["fuel"].Value == "-" && !why.Contains(" burning;", StringComparison.Ordinal)) NeedsRefuel(bench, m.Groups["in"].Value, int.Parse(m.Groups["n"].Value));
        else bench.ReadyAt = Now + TimeSpan.FromSeconds(StoryRecipes.SmeltSeconds * Math.Max(1, int.Parse(m.Groups["n"].Value)));
        bench.Rev = Touch();
        return true;
    }

    /// <summary>The furnace holds nothing of its load any more: the load is forgotten (nothing failed).</summary>
    private void FurnaceEmptied(WorkbenchState bench)
    {
        _log.LogInformation("story {Colony}: the furnace at {At} is empty: its load ({Bill}) is gone", Id, bench.Pos, bench.LoadedBill);
        bench.LoadedBill = null; bench.LoadedCount = 0; bench.ReadyAt = null; bench.LoadedRecipe = null; bench.LoadedAt = null;
        bench.RefuelCount = 0; bench.RefuelItem = null;
        MaterialsChanged();
    }

    /// <summary>Its input stands there with no fuel: a refuel job brings fuel for it (what the colony thought it had burnt out).</summary>
    private void NeedsRefuel(WorkbenchState bench, string item, int n)
    {
        if (bench.RefuelCount == 0)
            _log.LogInformation("story {Colony}: the furnace at {At} holds {N} {Item} and no fuel: a pawn brings fuel", Id, bench.Pos, n, item);
        bench.RefuelCount = Math.Max(1, n);
        bench.RefuelItem = item;
        bench.LoadedCount = Math.Max(1, n);
        NoFuelLeft(bench);
    }
}
