using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private BlockPos? _lastFurnacePos;

    /// <summary>Search radius for crafting tables / furnaces.</summary>
    public double StationSearchRadius { get; set; } = 24;

    // ------------------------------------------------------------------ crafting

    /// <summary>
    /// Crafts <paramref name="count"/> of an item, resolving the recipe tree (planks → sticks → tool...) with
    /// <see cref="RecipePlanner"/>. 2×2 recipes use the inventory grid; 3×3 ones walk to the nearest crafting table.
    /// Leftover grid items are returned to the inventory before any window closes.
    /// </summary>
    public Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default) =>
        RunActionAsync("craft", ct, c => CraftCoreAsync(itemId, damage, count, c));

    /// <summary>Name overload ("wooden pickaxe", "torch", "oak planks"...).</summary>
    public Task<ActionResult> CraftAsync(string itemName, int count = 1, CancellationToken ct = default)
    {
        if (Options.Profile.Data.ItemByNameWithDamage(itemName) is not { } r) return Task.FromResult(ActionResult.Fail($"unknown item '{itemName}'"));
        return CraftAsync(r.Item.Id, r.Damage, count, ct);
    }

    /// <summary>Crafting steps restarted from the server's fresh state after a desync, at most (then an honest failure).</summary>
    internal const int MaxCraftResyncs = 2;

    private async Task<ActionResult> CraftCoreAsync(int itemId, int damage, int count, CancellationToken ct)
    {
        if (count <= 0) return ActionResult.Fail("count must be positive");
        string target = ItemName(itemId, Math.Max(damage, 0));
        if (Recipes.ForOutput(itemId, damage).Count == 0) return ActionResult.Fail($"{target} has no crafting recipe");

        var (plan, unplanned) = await PlanCraftAsync(itemId, damage, count, target, ct).ConfigureAwait(false);
        if (unplanned is not null) return unplanned;
        int? variant = damage < 0 ? null : damage;
        if (plan.Steps.Count == 0) return ActionResult.Success($"already have {World.Inventory.CountItem(itemId, variant)} {target}");
        if (CraftTableRefusal(plan, target, out var table) is { } noTable) return ActionResult.Fail(noTable);

        int resyncsBefore = Volatile.Read(ref _windowResyncs);
        int before = World.Inventory.CountItem(itemId, variant);
        if (await RunCraftStepsAsync(plan, table, ct).ConfigureAwait(false) is { } stopped) return stopped;
        string synced = await ConfirmCraftAfterResyncAsync(resyncsBefore, ct).ConfigureAwait(false);
        int have = World.Inventory.CountItem(itemId, variant);
        int made = have - before;
        if (made <= 0) return ActionResult.Fail($"crafting {target} produced nothing (have {have}{synced})");
        return ActionResult.Success($"crafted {made} {target} (have {World.Inventory.CountItem(itemId, variant)}{synced})", made);
    }

    /// <summary>The craft's plan (planned again on the server's contents when a probe finds a desync), or the failure that ends it:
    /// smelting needed first, or what is missing.</summary>
    private async Task<(CraftPlan Plan, ActionResult? Fail)> PlanCraftAsync(int itemId, int damage, int count, string target, CancellationToken ct)
    {
        var opts = new PlannerOptions { AllowSmelting = false, HasCraftingTable = true };
        // Crafting makes new ones: the carried target never stands in for the craft (live 10-28: "already have 1 stone
        // pickaxe" for a pickaxe at 1/131, so no replacement was ever made).
        var plan = RecipePlanner.Plan(itemId, count, PlannerStacks(itemId, damage), damage, opts);
        if (!plan.Complete && await ProbeCoreAsync(ct).ConfigureAwait(false) == ProbeOutcome.Resynced)
        {
            // "Missing" may be a stale local copy (the server holds stacks a desync hid): plan again on the server's contents.
            Log("craft: inventory resynced before giving up on missing materials");
            plan = RecipePlanner.Plan(itemId, count, PlannerStacks(itemId, damage), damage, opts);
        }
        if (plan.Complete) return (plan, null);
        var withSmelt = RecipePlanner.Plan(itemId, count, PlannerStacks(itemId, damage), damage, opts with { AllowSmelting = true });
        if (withSmelt.Complete)
            return (plan, ActionResult.Fail($"need smelting first: {string.Join("; ", withSmelt.Steps.Where(s => s.IsSmelt))}"));
        return (plan, ActionResult.Fail($"missing {string.Join(", ", plan.Missing)} for {count} {target}"));
    }

    /// <summary>The crafting table a plan needs (null when none of its steps does), or why there is none in reach.</summary>
    private string? CraftTableRefusal(CraftPlan plan, string target, out BlockPos? table)
    {
        table = null;
        if (!plan.Steps.Any(s => s.RequiresCraftingTable)) return null;
        table = World.FindNearestBlock(World.Player.Position, StationSearchRadius, Blocks.Workbench)?.Pos;
        if (table is not null) return null;
        return World.Inventory.CountItem(Blocks.Workbench) > 0
            ? $"{target} needs a crafting table: place the one I carry first"
            : $"{target} needs a crafting table and none is within {StationSearchRadius:0} blocks";
    }

    /// <summary>Crafts every step of the plan in its window (the table opened for 3×3 steps, a foreign window closed for 2×2 ones),
    /// then closes the window while the session lives: null when all went through, else the failure.</summary>
    private async Task<ActionResult?> RunCraftStepsAsync(CraftPlan plan, BlockPos? table, CancellationToken ct)
    {
        try
        {
            foreach (var step in plan.Steps)
            {
                var recipe = step.Recipe!;
                if (await PrepareCraftWindowAsync(recipe, table, ct).ConfigureAwait(false) is { } noWindow) return noWindow;
                var w = World.Inventory.ActiveWindow;
                var r = await CraftRecipeAsync(w, recipe, step.Times, ct).ConfigureAwait(false);
                if (!r.Ok) return r;
            }
            return null;
        }
        finally
        {
            if (_session is { Ended: false })
            {
                try { await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log($"cleanup after craft failed: {ex.Message}"); }
            }
        }
    }

    /// <summary>Opens the crafting table for a 3×3 recipe (unless it is open), or closes a foreign window for a 2×2 one: null, or
    /// the failure to open the table.</summary>
    private async Task<ActionResult?> PrepareCraftWindowAsync(Recipe recipe, BlockPos? table, CancellationToken ct)
    {
        var open = World.Inventory.OpenWindow;
        if (recipe.RequiresCraftingTable && open?.Type != WindowType.Workbench)
        {
            var o = await OpenCoreAsync(table!.Value, ct).ConfigureAwait(false);
            if (!o.Ok) return ActionResult.Fail($"couldn't open crafting table: {o.Message}");
        }
        else if (!recipe.RequiresCraftingTable && open is not null && open.Type != WindowType.Workbench)
        {
            await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>After a craft during which the model was replaced by the server's, the end state is probed too, so the count
    /// reported is the server's (no phantom "+N" from a prediction the server never made): the note for the result, or "".</summary>
    private async Task<string> ConfirmCraftAfterResyncAsync(int resyncsBefore, CancellationToken ct)
    {
        if (Volatile.Read(ref _windowResyncs) == resyncsBefore) return "";
        if (_session is { Ended: false }) await ProbeCoreAsync(ct).ConfigureAwait(false);
        return ", server-confirmed after a resync";
    }

    private List<ItemStack> PlannerStacks() => World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).ToList();

    /// <summary>The planner's inventory without the craft's own target (exact variant; any variant for -1).</summary>
    private List<ItemStack> PlannerStacks(int targetId, int targetDamage) =>
        PlannerStacks().Where(s => !(s.Id == targetId && (targetDamage < 0 || s.Damage == targetDamage))).ToList();

    /// <summary>
    /// Crafts one recipe <paramref name="times"/> times in the given (player or workbench) window. Progress is counted from
    /// the output that actually reached the inventory, so a click the server rejected (or a slot update that raced one)
    /// restarts the batch from the server's fresh contents instead of trusting a broken prediction; after
    /// <see cref="MaxCraftResyncs"/> restarts it fails with what it really made and has.
    /// </summary>
    private async Task<ActionResult> CraftRecipeAsync(Window w, Recipe recipe, int times, CancellationToken ct)
    {
        int gridSize = w.Type == WindowType.Workbench ? 3 : 2;
        var layout = recipe.LayoutFor(gridSize);
        if (layout is null) return ActionResult.Fail($"{recipe.Output} doesn't fit a {gridSize}x{gridSize} grid");
        var run = new CraftRun(this, w, recipe, times, gridSize, layout);
        for (int guard = 0; run.Crafted() < times && guard < 64; guard++)
            if (await CraftBatchAsync(run, ct).ConfigureAwait(false) is { } stopped) return stopped;
        int made = run.Crafted();
        return made >= times ? ActionResult.Success($"crafted {run.Name} x{made}") : ActionResult.Fail($"crafted only {made}/{times} {run.Name}");
    }

    /// <summary>One crafting run: the window, the recipe's layout grouped by ingredient, the output count it started from, and the
    /// window resyncs seen (a resync since <see cref="Snap"/> = the server rejected a click and replaced the window's contents).</summary>
    private sealed class CraftRun
    {
        private readonly MinecraftClient _c;
        public readonly Window W;
        public readonly Recipe Recipe;
        public readonly int Times, GridSize, PerCraft, OutStart, Cells;
        public readonly string Name;
        public readonly ItemRules Rules;
        public readonly List<IGrouping<Ingredient, (Ingredient? ing, int i)>> Groups;
        public int Resyncs, Snap;

        public CraftRun(MinecraftClient c, Window w, Recipe recipe, int times, int gridSize, Ingredient?[] layout)
        {
            _c = c;
            W = w;
            Recipe = recipe;
            Times = times;
            GridSize = gridSize;
            Name = c.ItemName(recipe.OutputId, recipe.OutputDamage);
            Rules = c.World.Inventory.Rules;
            Groups = layout.Select((ing, i) => (ing, i)).Where(t => t.ing is not null).GroupBy(t => t.ing!.Value).ToList();
            PerCraft = Math.Max(1, recipe.OutputCount);
            OutStart = CountInWindowInventory(w, recipe.OutputId);
            Cells = layout.Count(i => i != null);
        }

        public int Crafted() => Math.Max(0, (CountInWindowInventory(W, Recipe.OutputId) - OutStart) / PerCraft);
        public string Have() => string.Join(", ", Groups.Select(g => $"{CountMatching(W, g.Key)} {g.Key}"));
        public bool Desynced() => Volatile.Read(ref _c._windowResyncs) != Snap;
    }

    /// <summary>One batch: lay the ingredients out, take the output, put the rest back. Null = go on (crafted, or the window resynced
    /// and the next batch starts from the server's contents); else the run's result.</summary>
    private async Task<ActionResult?> CraftBatchAsync(CraftRun run, CancellationToken ct)
    {
        if (!ReferenceEquals(World.Inventory.ActiveWindow, run.W))
            return ActionResult.Fail($"the crafting window closed; crafted {run.Crafted()}/{run.Times} {run.Name}");
        run.Snap = Volatile.Read(ref _windowResyncs);
        int batch = CraftBatchLimit(run, run.Times - run.Crafted());
        if (batch <= 0)
            return ActionResult.Fail($"ran out of ingredients for {run.Name} (crafted {run.Crafted()}/{run.Times}; have {run.Have()})");
        var concrete = new Dictionary<int, Ingredient>();
        batch = ConcreteLayout(run, batch, concrete);
        if (batch <= 0) return ActionResult.Fail($"couldn't lay out ingredients for {run.Name}");
        string? fail = await LayOutIngredientsAsync(run, concrete, batch, ct).ConfigureAwait(false);
        if (!run.Desynced() && !World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
        if (fail == null && !run.Desynced()) fail = await TakeCraftOutputAsync(run, ct).ConfigureAwait(false);

        // Anything left on the cursor or in the grid (uneven counts, an aborted layout) goes back to the inventory.
        await ReturnGridAsync(run.W, run.GridSize, ct).ConfigureAwait(false);
        if (fail != null) return ActionResult.Fail(fail + $" (crafted {run.Crafted()}/{run.Times})");
        return run.Desynced() ? CraftResynced(run) : null;
    }

    /// <summary>Batch size: limited by stack size of every ingredient and by what we have.</summary>
    private static int CraftBatchLimit(CraftRun run, int remaining)
    {
        int batch = remaining;
        foreach (var group in run.Groups)
        {
            batch = Math.Min(batch, CountMatching(run.W, group.Key) / group.Count());
            batch = Math.Min(batch, run.Rules.MaxStack(group.Key.Id));
        }
        return batch;
    }

    /// <summary>A wildcard accepts different variants in different cells, never in one stack. Chooses the largest batch whose cells can
    /// each be filled with ONE concrete variant (3 oak + 5 spruce => batches of 3, then 1); 0 = none.</summary>
    private static int ConcreteLayout(CraftRun run, int batch, Dictionary<int, Ingredient> concrete)
    {
        for (; batch > 0; batch--)
        {
            concrete.Clear();
            foreach (var group in run.Groups)
                if (!FillGroupCells(run.W, group, batch, concrete)) break;
            if (concrete.Count == run.Cells) break;
        }
        return batch;
    }

    /// <summary>The group's cells, each given a variant there is <paramref name="batch"/> of; false = not all of them.</summary>
    private static bool FillGroupCells(Window w, IGrouping<Ingredient, (Ingredient? ing, int i)> group, int batch, Dictionary<int, Ingredient> concrete)
    {
        var cells = new Queue<int>(group.Select(t => t.i + 1));
        var stock = Enumerable.Range(w.MainStart, w.SlotCount - w.MainStart).Select(w.Get)
            .Where(s => group.Key.Matches(s)).GroupBy(s => (s.Id, s.Damage));
        foreach (var variant in stock)
            for (int n = variant.Sum(s => (int)s.Count) / batch; n > 0 && cells.Count > 0; n--)
                concrete[cells.Dequeue()] = new Ingredient(variant.Key.Id, variant.Key.Damage);
        return cells.Count == 0;
    }

    /// <summary>Puts <paramref name="batch"/> of each cell's ingredient into the grid; the failure, or null (also when the window
    /// resynced: the caller sees that).</summary>
    private async Task<string?> LayOutIngredientsAsync(CraftRun run, Dictionary<int, Ingredient> concrete, int batch, CancellationToken ct)
    {
        foreach (var (cell, ing) in concrete)
        {
            var (need, fail) = await LayOutCellAsync(run, cell, ing, batch, ct).ConfigureAwait(false);
            if (fail != null || run.Desynced()) return fail;
            if (need > 0) return $"couldn't lay out {ing} for {run.Name}";
        }
        return null;
    }

    /// <summary>Fills one grid cell: picks the ingredient up (a stray cursor stack stashed first) and drops it in, all at once or one
    /// by one. Returns what is still missing, and a failure when the ingredient was lost track of.</summary>
    private async Task<(int Need, string? Fail)> LayOutCellAsync(CraftRun run, int cell, Ingredient ing, int batch, CancellationToken ct)
    {
        int need = batch;
        for (int g2 = 0; g2 < 64 && need > 0 && !run.Desynced(); g2++)
        {
            var (held, lost) = await IngredientOnCursorAsync(run, ing, ct).ConfigureAwait(false);
            if (lost != null) return (need, lost);
            if (!held) break;
            var cur = World.Inventory.Cursor;
            int k = Math.Min(need, cur.Count);
            if (k == cur.Count) await ClickCoreAsync(cell, false, false, ct).ConfigureAwait(false);
            else for (int i = 0; i < k && !run.Desynced(); i++) await ClickCoreAsync(cell, true, false, ct).ConfigureAwait(false);
            need -= cur.Count - World.Inventory.Cursor.Count;
        }
        return (need, null);
    }

    /// <summary>The ingredient on the cursor: kept when it already is, else picked up from the window. False = the pick-up click
    /// failed; a failure text when no slot holds it any more.</summary>
    private async Task<(bool Held, string? Lost)> IngredientOnCursorAsync(CraftRun run, Ingredient ing, CancellationToken ct)
    {
        if (!World.Inventory.Cursor.IsEmpty && !ing.Matches(World.Inventory.Cursor))
            await StashCursorAsync(ct).ConfigureAwait(false);
        if (!World.Inventory.Cursor.IsEmpty) return (true, null);
        int src = FindIngredientSlot(run.W, ing);
        if (src < 0) return (false, $"lost track of {ing} while crafting {run.Name}");
        return (await ClickCoreAsync(src, false, false, ct).ConfigureAwait(false), null);
    }

    /// <summary>The output is predicted locally (b1.8.1 never sends 0x67 for a crafting-result slot): waits for our recipe evaluator to
    /// show it, then shift-clicks it into the inventory (or, the inventory full for that, takes it on the cursor and stashes it).
    /// A layout click rejected while we waited (live 10-27 23:07: "click 370 in window 14 rejected" then "grid didn't produce
    /// chest"): the grid model is the server's again, so the resync lays it out afresh (null here).</summary>
    private async Task<string?> TakeCraftOutputAsync(CraftRun run, CancellationToken ct)
    {
        var w = run.W;
        int outputId = run.Recipe.OutputId;
        bool shown = await WaitUntilAsync(() => run.Desynced() || w.Get(0) is { IsEmpty: false } o && o.Id == outputId,
            TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false);
        if (run.Desynced()) return null;
        if (!shown) return $"grid didn't produce {run.Name} (recipe mismatch?)";
        int outBefore = CountInWindowInventory(w, outputId);
        await ClickCoreAsync(0, false, true, ct).ConfigureAwait(false); // shift-click: craft all, straight into the inventory
        if (run.Desynced() || CountInWindowInventory(w, outputId) > outBefore) return null;
        await ClickCoreAsync(0, false, false, ct).ConfigureAwait(false);
        if (!run.Desynced() && !await StashCursorAsync(ct).ConfigureAwait(false)) return "inventory full while crafting " + run.Name;
        return null;
    }

    /// <summary>The window resynced during the batch: retry from the server's contents, up to <see cref="MaxCraftResyncs"/> times.</summary>
    private ActionResult? CraftResynced(CraftRun run)
    {
        if (++run.Resyncs > MaxCraftResyncs)
            return ActionResult.Fail($"crafting {run.Name}: the server kept rejecting my window clicks ({run.Resyncs} resyncs); " +
                $"crafted {run.Crafted()}/{run.Times}, server-confirmed inventory has {run.Have()}");
        Log($"craft {run.Name}: inventory resynced ({run.Resyncs}/{MaxCraftResyncs}); retrying from the server's contents");
        return null;
    }

    /// <summary>Cursor and crafting-grid contents back into the inventory part of the window.</summary>
    private async Task ReturnGridAsync(Window w, int gridSize, CancellationToken ct)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            if (!World.Inventory.Cursor.IsEmpty) await StashCursorAsync(ct).ConfigureAwait(false);
            bool any = false;
            for (int cell = 1; cell <= gridSize * gridSize; cell++)
                if (!w.Get(cell).IsEmpty) { any = true; await ClickCoreAsync(cell, false, true, ct).ConfigureAwait(false); }
            if (!any && World.Inventory.Cursor.IsEmpty) return;
        }
    }

    private static int CountMatching(Window w, Ingredient ing)
    {
        int n = 0;
        for (int i = w.MainStart; i < w.SlotCount; i++)
        {
            var s = w.Get(i);
            if (ing.Matches(s)) n += s.Count;
        }
        return n;
    }

    /// <summary>Window slot of a matching stack in the inventory part; main inventory first (keeps the hotbar tidy), smallest stack first.</summary>
    private static int FindIngredientSlot(Window w, Ingredient ing)
    {
        int best = -1, bestCount = int.MaxValue;
        for (int i = w.MainStart; i < w.SlotCount; i++)
        {
            var s = w.Get(i);
            if (!ing.Matches(s)) continue;
            int score = s.Count + (i >= w.HotbarStart ? 1000 : 0);
            if (score < bestCount) { bestCount = score; best = i; }
        }
        return best;
    }

    private static int CountInWindowInventory(Window w, int itemId)
    {
        int n = 0;
        for (int i = w.MainStart; i < w.SlotCount; i++)
        {
            var s = w.Get(i);
            if (!s.IsEmpty && s.Id == itemId) n += s.Count;
        }
        return n;
    }

    // ------------------------------------------------------------------ smelting

    /// <summary>
    /// Walks to a furnace (given or nearest), loads <paramref name="count"/> items and enough fuel
    /// (<see cref="Smelting.FuelNeeded"/>; fuel = <paramref name="fuelItemId"/> or the best non-reserved fuel).
    /// With <paramref name="wait"/> it stays and collects the output; otherwise it returns immediately — use
    /// <see cref="CollectFurnaceAsync"/> later.
    /// </summary>
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false,
        BlockPos? furnace = null, CancellationToken ct = default) =>
        RunActionAsync("smelt", ct, c => SmeltCoreAsync(inputItemId, count, fuelItemId, wait, furnace, c));

    /// <summary>Takes finished items out of a furnace (given, last used, or nearest).</summary>
    public Task<ActionResult> CollectFurnaceAsync(BlockPos? furnace = null, CancellationToken ct = default) =>
        RunActionAsync("collect_furnace", ct, async c =>
        {
            var pos = furnace ?? _lastFurnacePos ?? FindFurnace();
            if (pos is null) return ActionResult.Fail($"no furnace within {StationSearchRadius:0} blocks");
            var o = await OpenCoreAsync(pos.Value, c).ConfigureAwait(false);
            if (!o.Ok) return o;
            try
            {
                var w = World.Inventory.OpenWindow!;
                var outStack = w.Get(2);
                string status = DescribeOpenWindow(300);
                if (outStack.IsEmpty) return ActionResult.Fail("nothing to collect; " + status);
                await ClickCoreAsync(2, false, true, c).ConfigureAwait(false);
                int left = w.Get(2).IsEmpty ? 0 : w.Get(2).Count;
                int got = outStack.Count - left;
                return got > 0
                    ? ActionResult.Success($"collected {got} {ItemName(outStack.Id, outStack.Damage)}; still smelting {StackCount(w.Get(0))}", got)
                    : ActionResult.Fail("inventory full");
            }
            finally
            {
                await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false);
            }
        });

    private BlockPos? FindFurnace() =>
        World.FindNearestBlock(World.Player.Position, StationSearchRadius, Blocks.Furnace, Blocks.LitFurnace)?.Pos;

    private async Task<ActionResult> SmeltCoreAsync(int inputId, int count, int? fuelId, bool wait, BlockPos? furnace, CancellationToken ct)
    {
        // 1.12.2 keys some inputs by variant (wet sponge, stone bricks, cod/salmon): the carried stack's variant decides.
        var carried = World.Inventory.PlayerSlots().Skip(9).FirstOrDefault(st => !st.IsEmpty && st.Id == inputId);
        if (Smelting.ResultFor(inputId, carried.IsEmpty ? -1 : carried.Damage) is not { } recipe)
            return ActionResult.Fail($"{ItemName(inputId)} can't be smelted");
        int have = World.Inventory.CountItem(inputId);
        if (have == 0) return ActionResult.Fail($"no {ItemName(inputId)} to smelt");
        count = Math.Min(count, have);
        var pos = furnace ?? FindFurnace();
        if (pos is null) return ActionResult.Fail($"no furnace within {StationSearchRadius:0} blocks");

        var o = await OpenCoreAsync(pos.Value, ct).ConfigureAwait(false);
        if (!o.Ok) return o;
        _lastFurnacePos = pos;
        var w = World.Inventory.OpenWindow!;
        try
        {
            return await SmeltInWindowAsync(w, recipe, inputId, count, fuelId, wait, pos.Value, ct).ConfigureAwait(false);
        }
        finally
        {
            await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Loads the open furnace with the input and the fuel it needs, then (with <paramref name="wait"/>) stays and collects.</summary>
    private async Task<ActionResult> SmeltInWindowAsync(Window w, SmeltingRecipe recipe, int inputId, int count, int? fuelId, bool wait, BlockPos pos,
        CancellationToken ct)
    {
        var input = w.Get(0);
        if (!input.IsEmpty && input.Id != inputId)
            return ActionResult.Fail($"furnace is busy with {ItemName(input.Id, input.Damage)} x{input.Count}");
        int already = input.IsEmpty ? 0 : input.Count;
        count = Math.Min(count, 64 - already);
        if (count <= 0) return ActionResult.Fail("furnace input slot is full");

        // Fuel: what's in the slot already counts; top it up with the chosen fuel.
        var fuelSlot = await ClearOtherFuelAsync(w, fuelId, ct).ConfigureAwait(false);
        int fuel = ChooseFuel(fuelId, fuelSlot, inputId);
        if (PlanFuel(w, already, fuelSlot, fuel, ref count, out int fuelUnits) is { } noFuel) return ActionResult.Fail(noFuel);

        int putIn = await MoveIntoSlotAsync(w, s => s.Id == inputId, 0, count, ct).ConfigureAwait(false);
        if (putIn <= 0) return ActionResult.Fail("couldn't load the furnace");
        if (fuelUnits > 0)
        {
            int f = await MoveIntoSlotAsync(w, s => s.Id == fuel, 1, fuelUnits, ct).ConfigureAwait(false);
            if (f < fuelUnits) Log($"smelt: only loaded {f}/{fuelUnits} fuel");
        }
        int queued = putIn + already;
        string outName = ItemName(recipe.OutputId, recipe.OutputDamage);
        if (!wait)
            return ActionResult.Success($"smelting {queued} {ItemName(inputId)} -> {outName} at {Fmt(pos)}, ready in ~{queued * 10}s", putIn);
        return await CollectSmeltedAsync(w, queued, outName, ct).ConfigureAwait(false);
    }

    /// <summary>A named fuel is the only one burnt (Story Mode's coal-only furnace never burns planks): another fuel left in the slot
    /// comes out first, and nothing else stands in when the named one runs short. Returns the fuel slot after.</summary>
    private async Task<ItemStack> ClearOtherFuelAsync(Window w, int? fuelId, CancellationToken ct)
    {
        var fuelSlot = w.Get(1);
        if (fuelId is not { } only || fuelSlot.IsEmpty || fuelSlot.Id == only) return fuelSlot;
        await ClickCoreAsync(1, false, true, ct).ConfigureAwait(false);
        return w.Get(1);
    }

    /// <summary>The named fuel, else the one in the slot while there's more of it, else the best one carried (0 = none).</summary>
    private int ChooseFuel(int? fuelId, ItemStack fuelSlot, int inputId)
    {
        int fuel = fuelId ?? (fuelSlot.IsEmpty ? 0 : fuelSlot.Id);
        if (fuelId is null && (fuel == 0 || World.Inventory.CountItem(fuel) == 0 && fuelSlot.IsEmpty))
        {
            var candidates = World.Inventory.PlayerSlots().Skip(9)
                .Where(s => !s.IsEmpty && s.Id != inputId && !ReservedItems.Contains(s.Id) && s.Id != Items.LavaBucket);
            fuel = Smelting.BestFuel(candidates) ?? 0;
        }
        return fuel;
    }

    /// <summary>The fuel units to add so the whole load cooks; when the fuel carried covers less, <paramref name="count"/> shrinks to
    /// what it does cover. Returns why it can't smelt at all, or null.</summary>
    private string? PlanFuel(Window w, int already, ItemStack fuelSlot, int fuel, ref int count, out int fuelUnits)
    {
        int totalItems = count + already;
        int burnTicksAvailable = w.BurnTimeLeft + (fuelSlot.IsEmpty ? 0 : fuelSlot.Count * Smelting.FuelTicks(fuelSlot.Id));
        int ticksNeeded = totalItems * Smelting.CookTicks - (int)(w.SmeltProgress * Smelting.CookTicks);
        fuelUnits = 0;
        if (ticksNeeded <= burnTicksAvailable) return null;
        if (fuel == 0) return "no fuel (coal, charcoal, planks, logs, sticks)";
        if (!fuelSlot.IsEmpty && fuelSlot.Id != fuel) return $"fuel slot holds {ItemName(fuelSlot.Id)}; not enough to finish and can't mix fuels";
        fuelUnits = (int)Math.Ceiling((ticksNeeded - burnTicksAvailable) / (double)Smelting.FuelTicks(fuel));
        int fuelHave = World.Inventory.CountItem(fuel);
        if (fuelHave >= fuelUnits) return null;
        // Smelt what the fuel covers.
        int coverable = (burnTicksAvailable + fuelHave * Smelting.FuelTicks(fuel)) / Smelting.CookTicks - already;
        if (coverable <= 0) return $"not enough fuel ({fuelHave} {ItemName(fuel)})";
        count = Math.Min(count, coverable);
        fuelUnits = fuelHave;
        return null;
    }

    /// <summary>Stays by the furnace and shift-clicks the output out until the input and output slots are empty (or the time is up).</summary>
    private async Task<ActionResult> CollectSmeltedAsync(Window w, int queued, string outName, CancellationToken ct)
    {
        int collected = 0;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(queued * 10.5 + 10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            var outStack = w.Get(2);
            if (!outStack.IsEmpty)
            {
                await ClickCoreAsync(2, false, true, ct).ConfigureAwait(false);
                collected += outStack.Count - (w.Get(2).IsEmpty ? 0 : w.Get(2).Count);
            }
            if (w.Get(0).IsEmpty && w.Get(2).IsEmpty) break;
            if (World.Inventory.OpenWindow != w) return ActionResult.Fail($"furnace window closed; collected {collected} {outName}", collected);
        }
        return collected > 0
            ? ActionResult.Success($"smelted {collected} {outName}", collected)
            : ActionResult.Fail($"nothing came out of the furnace ({DescribeOpenWindow(200)})");
    }

    /// <summary>Moves up to <paramref name="count"/> matching items from the inventory part into one container slot.</summary>
    private async Task<int> MoveIntoSlotAsync(Window w, Func<ItemStack, bool> match, int dest, int count, CancellationToken ct)
    {
        int moved = 0;
        for (int guard = 0; guard < 16 && moved < count; guard++)
        {
            int src = -1;
            for (int i = w.MainStart; i < w.SlotCount; i++)
                if (w.Get(i) is { IsEmpty: false } s && match(s)) { src = i; break; }
            if (src < 0) break;
            int n = await MoveManualAsync(w, src, [dest], count - moved, ct).ConfigureAwait(false);
            if (n <= 0) break;
            moved += n;
        }
        return moved;
    }

    private string StackCount(ItemStack s) => s.IsEmpty ? "nothing" : $"{s.Count} {ItemName(s.Id, s.Damage)}";
}
