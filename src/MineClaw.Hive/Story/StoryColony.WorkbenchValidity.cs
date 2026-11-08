namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>An unseen registered bench keeps its last known availability; only a loaded view changes that evidence.</summary>
    private bool BenchAvailable(WorkbenchState bench) => !bench.Missing
        && (World is not { } w || !w.Loaded(bench.Pos.X, bench.Pos.Z)
            || StoryRecipes.BenchOf(w.Block(bench.Pos.X, bench.Pos.Y, bench.Pos.Z).Id) == bench.Kind);

    private static string MissingBenchReason(WorkbenchState bench) => $"the {Pretty(bench.Kind)} at {bench.Pos} is gone";

    /// <summary>Keep registrations and manual bills, but never schedule against a missing table or furnace. Automatic tasks
    /// follow an existing replacement bench; a furnace's real or unseen load stays where it was put.</summary>
    private void ReconcileWorkbenches()
    {
        if (World is not { } w) return;
        foreach (var bench in Benches.Where(b => b.Kind is "crafting_table" or "furnace"))
        {
            if (!w.Loaded(bench.Pos.X, bench.Pos.Z)) continue;
            if (StoryRecipes.BenchOf(w.Block(bench.Pos.X, bench.Pos.Y, bench.Pos.Z).Id) != bench.Kind)
            {
                if (!bench.Missing) MissingWorkbench(bench);
            }
            else RestoreWorkbench(bench);
        }
        foreach (var bench in Benches.Where(b => b.Missing))
            if (BenchFor(bench.Kind, bench.Pos) is { } replacement) MoveWorkbenchTasks(bench, replacement);
    }

    private void MissingWorkbench(WorkbenchState bench)
    {
        bench.Missing = true;
        foreach (var (name, job) in _current.ToList())
            if (job.At == bench.Pos && job.Source is ("bill" or "furnace") && Pawns.TryGetValue(name, out var pawn))
                Supersede(pawn, "workbench removed");
        if (bench.LoadedBill != null) FurnaceEmptied(bench); // confirmed absent: its old output is no longer inside a furnace
        NoFuelLeft(bench);
        foreach (var bill in bench.Bills)
            if (bill.SuspendedReason == null || bill.SuspendedReason.Contains($"at {bench.Pos} is not a container", StringComparison.OrdinalIgnoreCase))
                bill.SuspendedReason = MissingBenchReason(bench);
        bench.Rev = Touch();
        MaterialsChanged();
        WakeIdle();
    }

    private void RestoreWorkbench(WorkbenchState bench)
    {
        bool changed = bench.Missing;
        bench.Missing = false;
        foreach (var bill in bench.Bills.Where(b => b.SuspendedReason == MissingBenchReason(bench)))
        {
            ResetWorkbenchTask(bill);
            changed = true;
        }
        if (!changed) return;
        _skip.Remove("bench:" + bench.Pos);
        bench.Rev = Touch();
        MaterialsChanged();
        WakeIdle();
    }

    private void MoveWorkbenchTasks(WorkbenchState from, WorkbenchState to)
    {
        if (from.Tasks.Count == 0) return;
        foreach (var task in from.Tasks)
        {
            ResetWorkbenchTask(task);
            to.Tasks.Add(task);
        }
        from.Tasks.Clear();
        from.Rev = to.Rev = Touch();
        MaterialsChanged();
        WakeIdle();
    }

    private void ResetWorkbenchTask(BillState task)
    {
        task.SuspendedReason = null;
        _billFails.Remove(task.Id);
        _billAlerted.Remove(task.Id);
        _taskBlocked.Remove(task.Id);
    }
}
