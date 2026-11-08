using System.Text.Json.Nodes;
using MineClaw.Data;

namespace MineClaw.Hive.Story;

/// <summary>
/// Craft tasks (docs/web/story-mode.md "Craft tasks"). Fox, 11-05: "Separate having bills and having personal crafting tasks. Bills are
/// something I configure for crafting tables, but it's over-inundating having each personal crafting task as a bill." The colony's
/// own crafts (tools pawns wait on, spares, a build's chain, a bucket, a farm's hoe, a fishing rod) live in each bench's
/// <see cref="WorkbenchState.Tasks"/>, never in its bill list: one task per item and need with a count and a reason, taken by pawns
/// with the recipe's work, gone when done or no longer needed. "Immediate requirements like that should always be at the top of the
/// list for crafting": a bench's queue is immediate tasks (work waits on them right now), then the owner's bills in their order, then
/// the other tasks (build ingredients); and a crafter takes an immediate task before any other work.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>A craft task the colony made for a single need (a farm's hoe, a fishing zone's rod): kept until its count is made.</summary>
    public const string NeedTag = "need";

    /// <summary>What held a craft task up at its last try ("no sticks: checked 2 chests …"), cleared when a pawn takes it.</summary>
    private readonly Dictionary<string, string> _taskBlocked = new(StringComparer.Ordinal);

    /// <summary>A bill or task by id on a bench (owner bills and craft tasks share ids: both are "bill…").</summary>
    internal static BillState? BillById(WorkbenchState b, string? id) =>
        id == null ? null : b.Bills.FirstOrDefault(x => x.Id == id) ?? b.Tasks.FirstOrDefault(x => x.Id == id);

    /// <summary>Everything a bench may craft: its tasks and the owner's bills.</summary>
    internal static IEnumerable<BillState> BillsAndTasks(WorkbenchState b) => b.Tasks.Concat(b.Bills);

    /// <summary>The order a bench's work is taken in: immediate tasks, the owner's bills, then the other tasks (only those
    /// <paramref name="only"/> passes, when given).</summary>
    internal IEnumerable<BillState> BenchQueue(WorkbenchState b, Func<BillState, bool>? only = null)
    {
        var all = b.Tasks.Count == 0 ? b.Bills : b.Tasks.Where(Immediate).Concat(b.Bills).Concat(b.Tasks.Where(t => !Immediate(t)));
        return only == null ? all : all.Where(only);
    }

    /// <summary>Work waits on it right now: a tool pawns are waiting on, a farm's hoe or a fishing rod, the bucket of a water build.</summary>
    internal bool Immediate(BillState t) =>
        t.ForBuild == NeedTag || t.ForItem == "bucket" && t.ForBuild != null
        || t.ForBuild == ToolsTag && t.ForItem is { } fi && _urgentTools.Contains(fi)
        || t.ForItem == "chest" && t.ForBuild is { } fb && IsChestBuild(fb);

    /// <summary>
    /// A build of the Architect's chest (storage the owner placed): its chest is crafted before other work. Live 11-06: storage was full,
    /// Fox placed 14 chests, and their crafts queued behind a "forever" bread bill and every build at the crafters' priority.
    /// </summary>
    private bool IsChestBuild(string id) => Builds.Any(b => b.Id == id && b.Kind == "chest" && b.Status != "done");

    /// <summary>
    /// An immediate craft task this pawn can do (the recipe's work enabled), taken before its other work: a missing tool holds the
    /// colony's work up (live 11-05: the pickaxe bill sat "crafting one" for 30 minutes while every crafter hauled at priority 2).
    /// </summary>
    private ActiveJob? ImmediateCraftJob(PawnState p, PawnLive live)
    {
        if (!Benches.Any(b => b.Tasks.Any(Immediate))) return null;
        foreach (var work in new[] { WorkTypes.Craft, WorkTypes.Cook })
            if (p.Priorities.GetValueOrDefault(work) is >= 1 and <= 4 && BillJob(p, live, work, Immediate) is { } job) return job;
        return null;
    }

    /// <summary>
    /// Older saves kept the colony's crafts as bills: auto bills move to the tasks, and the finished one-off bills the colony added for
    /// a farm's hoe or a fishing rod (live 11-05: 20 "wooden hoe 1/1" on one table) go. True when anything moved.
    /// </summary>
    internal bool MigrateCraftTasks()
    {
        bool changed = false;
        foreach (var bench in Benches)
        {
            foreach (var bill in bench.Bills.Where(x => x.ForBuild != null).ToList())
            {
                bench.Bills.Remove(bill);
                bench.Tasks.Add(bill);
                changed = true;
            }
            changed |= bench.Bills.RemoveAll(x => FinishedNeedBill(bench, x)) > 0;
            changed |= OpenNeedBillsToTasks(bench);
        }
        return changed;
    }

    /// <summary>A one-off bill the colony added for a need that isn't made yet becomes that need's (immediate) task.</summary>
    private static bool OpenNeedBillsToTasks(WorkbenchState bench)
    {
        var open = bench.Bills.Where(x => x.Mode == "count" && x.Done < x.Target && x.ForBuild == null && bench.LoadedBill != x.Id && ColonyMade(x)).ToList();
        foreach (var bill in open)
        {
            bench.Bills.Remove(bill);
            bill.ForBuild = NeedTag;
            bill.ForItem ??= bill.Recipe;
            bench.Tasks.Add(bill);
        }
        return open.Count > 0;
    }

    /// <summary>A one-off bill the colony added itself (a hoe, a fishing rod: count, the defaults it was made with) that is done.</summary>
    private static bool FinishedNeedBill(WorkbenchState bench, BillState x) =>
        x.Mode == "count" && x.Done >= x.Target && x.ForBuild == null && bench.LoadedBill != x.Id && ColonyMade(x);

    /// <summary>Made the way the colony made its one-off bills (radius 32, any pawn, any ingredients) for a hoe, a rod or seeds.</summary>
    private static bool ColonyMade(BillState x) =>
        x.Radius == 32 && x.Pawn == "any" && x.Ingredients.Allow is ["*"] && x.Ingredients.Deny.Count == 0 && NeedRecipe(x.Recipe);

    private static bool NeedRecipe(string recipe) => recipe.EndsWith("_hoe", StringComparison.Ordinal) || recipe.EndsWith("fishing_rod", StringComparison.Ordinal) || recipe.EndsWith("_seeds", StringComparison.Ordinal);

    /// <summary>A one-off craft task for a need (a farm's hoe, a fishing rod) at the colony's crafting table, unless a task is still open.
    /// False when there is no crafting table or no such recipe in this version.</summary>
    private bool NeedTask(IStoryWorld w, int outputId, int target, string why)
    {
        var recipe = StoryRecipes.For(w.Data, "crafting_table").FirstOrDefault(r => r.Output.Id == outputId);
        var bench = BenchFor("crafting_table", Home() ?? default);
        if (recipe == null || bench == null) return false;
        // Only an open task counts: an owner's bill for it waits its turn in the owner's queue (live 11-05: a "wooden hoe 1/1" bill and
        // the owner's "stone hoe until 1" kept the farm's hoe from ever becoming an immediate task, and no hoe was made for 9 hours).
        if (Benches.Any(b => b.Tasks.Any(x => x.Recipe == recipe.Id && !x.Paused && (x.Mode != "count" || x.Done < x.Target)))) return true;
        bench.Tasks.Add(new BillState { Id = NextId("bill"), Recipe = recipe.Id, Mode = "count", Target = target, ForBuild = NeedTag, ForItem = recipe.Id, Reason = why });
        bench.Rev = Touch();
        return true;
    }

    /// <summary>Need tasks that are made go (a furnace still smelting for one, or a pawn on it, keeps it until then).</summary>
    private bool DropFinishedNeeds(WorkbenchState bench) =>
        bench.Tasks.RemoveAll(x => x.ForBuild == NeedTag && x.Done >= x.Target && bench.LoadedBill != x.Id && !_current.Values.Any(c => c.SourceId == x.Id)) > 0;

    /// <summary>Why the colony crafts it, in words ("for the stone bricks of build b3", the tool's reason, a need's).</summary>
    private string TaskReason(Want want)
    {
        if (want.Reason != null) return want.Reason;
        var made = World is { } w && w.Data.ItemExists(want.Recipe.Output.Id) ? w.Data.Item(want.Recipe.Output.Id).Name : null;
        return made == want.ForItem ? $"for build {want.Build}" : $"for the {want.ForItem.Replace('_', ' ')} of build {want.Build}";
    }

    /// <summary>A tool want's reason: who waits on it for which work, and the spare ("for mining: Cleo Z., Dov U. (+1 spare)").</summary>
    private string ToolReason(ToolAsk ask, int made)
    {
        var who = _toolWants.TryGetValue((ask.Kind, ask.Level), out var want) ? want.Pawns.Keys.Select(n => Pawns.TryGetValue(n, out var p) ? p.Display : n).ToList() : [];
        string work = ToolWork(ask.Kind);
        if (who.Count == 0) return $"a spare for {work}";
        int spare = Math.Max(0, made - who.Count);
        var names = who.Count <= 3 ? string.Join(", ", who) : $"{string.Join(", ", who.Take(3))} and {who.Count - 3} more";
        return $"for {work}: {names}" + (spare > 0 ? $" (+{spare} spare)" : "");
    }

    private static string ToolWork(ToolType kind) => kind switch
    {
        ToolType.Pickaxe => "mining", ToolType.Axe => "chopping", ToolType.Shovel => "digging", ToolType.Hoe => "farming", ToolType.Sword => "cobwebs",
        _ => "work",
    };

    /// <summary>
    /// The colony's craft tasks for the browser (<c>craft_tasks</c>): each bench's tasks and the hand crafts (2×2, no bench), with what,
    /// how many are left, why, for what, its priority, the bench, its status and what holds it up.
    /// </summary>
    private JsonArray CraftTasksJson(IStoryWorld? w)
    {
        var arr = new JsonArray();
        foreach (var bench in Benches)
            foreach (var t in bench.Tasks)
                arr.Add(TaskJson(w, bench, t));
        foreach (var h in _handCrafts)
            arr.Add(new JsonObject
            {
                ["id"] = $"hand:{h.Build}:{h.Recipe.Id}", ["item"] = h.Recipe.Id, ["label"] = h.Recipe.Label, ["count"] = h.Outputs,
                ["reason"] = h.Build == ToolsTag ? "for tools" : $"for build {h.Build}", ["for"] = h.Build, ["priority"] = "normal", ["bench"] = null,
                ["status"] = "queued", ["blocked"] = null,
            });
        return arr;
    }

    private JsonObject TaskJson(IStoryWorld? w, WorkbenchState bench, BillState t)
    {
        var label = w != null && StoryRecipes.Get(w.Data, t.Recipe) is { } r ? r.Label : t.Recipe;
        var blocked = t.SuspendedReason ?? _taskBlocked.GetValueOrDefault(t.Id);
        var workingAt = _current.Values.FirstOrDefault(j => j.Source == "bill" && j.SourceId == t.Id)?.At ?? bench.Pos;
        return new JsonObject
        {
            ["id"] = t.Id, ["item"] = t.Recipe, ["label"] = label, ["count"] = Math.Max(0, t.Target - t.Done),
            ["reason"] = t.Reason ?? (t.ForBuild == ToolsTag ? "for tools" : $"for build {t.ForBuild}"), ["for"] = t.ForBuild, ["for_item"] = t.ForItem,
            ["priority"] = Immediate(t) ? "immediate" : "normal", ["bench"] = workingAt.Json(), ["status"] = TaskStatus(bench, t, blocked), ["blocked"] = blocked,
        };
    }

    /// <summary>crafting (a pawn on it, or its furnace smelting) | paused | waiting (held up: <c>blocked</c> says why) | queued.</summary>
    private string TaskStatus(WorkbenchState bench, BillState t, string? blocked) =>
        bench.LoadedBill == t.Id || _current.Values.Any(c => c.SourceId == t.Id) ? "crafting" : t.Paused ? "paused" : blocked != null ? "waiting" : "queued";
}
