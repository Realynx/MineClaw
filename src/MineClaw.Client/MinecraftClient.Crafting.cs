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
        if (Items.ByNameWithDamage(itemName) is not { } r) return Task.FromResult(ActionResult.Fail($"unknown item '{itemName}'"));
        return CraftAsync(r.Item.Id, r.Damage, count, ct);
    }

    private async Task<ActionResult> CraftCoreAsync(int itemId, int damage, int count, CancellationToken ct)
    {
        if (count <= 0) return ActionResult.Fail("count must be positive");
        string target = ItemName(itemId, Math.Max(damage, 0));
        if (Recipes.ForOutput(itemId, damage).Count == 0) return ActionResult.Fail($"{target} has no crafting recipe");

        var invStacks = World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty).ToList();
        var plan = RecipePlanner.Plan(itemId, count, invStacks, damage, new PlannerOptions { AllowSmelting = false, HasCraftingTable = true });
        if (!plan.Complete)
        {
            var withSmelt = RecipePlanner.Plan(itemId, count, invStacks, damage, new PlannerOptions { AllowSmelting = true, HasCraftingTable = true });
            if (withSmelt.Complete)
                return ActionResult.Fail($"need smelting first: {string.Join("; ", withSmelt.Steps.Where(s => s.IsSmelt))}");
            return ActionResult.Fail($"missing {string.Join(", ", plan.Missing)} for {count} {target}");
        }
        if (plan.Steps.Count == 0) return ActionResult.Success($"already have {World.Inventory.CountItem(itemId)} {target}");

        BlockPos? table = null;
        if (plan.Steps.Any(s => s.RequiresCraftingTable))
        {
            table = World.FindNearestBlock(World.Player.Position, StationSearchRadius, Blocks.Workbench)?.Pos;
            if (table is null)
                return ActionResult.Fail(World.Inventory.CountItem(Blocks.Workbench) > 0
                    ? $"{target} needs a crafting table: place the one I carry first"
                    : $"{target} needs a crafting table and none is within {StationSearchRadius:0} blocks");
        }

        int before = World.Inventory.CountItem(itemId, damage < 0 ? null : damage);
        try
        {
            foreach (var step in plan.Steps)
            {
                var recipe = step.Recipe!;
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
                var w = World.Inventory.ActiveWindow;
                var r = await CraftRecipeAsync(w, recipe, step.Times, ct).ConfigureAwait(false);
                if (!r.Ok) return r;
            }
        }
        finally
        {
            if (_session is { Ended: false })
            {
                try { await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { Log($"cleanup after craft failed: {ex.Message}"); }
            }
        }
        int made = World.Inventory.CountItem(itemId, damage < 0 ? null : damage) - before;
        if (made <= 0) return ActionResult.Fail($"crafting {target} produced nothing");
        return ActionResult.Success($"crafted {made} {target} (have {World.Inventory.CountItem(itemId)})", made);
    }

    /// <summary>Crafts one recipe <paramref name="times"/> times in the given (player or workbench) window.</summary>
    private async Task<ActionResult> CraftRecipeAsync(Window w, Recipe recipe, int times, CancellationToken ct)
    {
        int gridSize = w.Type == WindowType.Workbench ? 3 : 2;
        var layout = recipe.LayoutFor(gridSize);
        if (layout is null) return ActionResult.Fail($"{recipe.Output} doesn't fit a {gridSize}x{gridSize} grid");
        string name = ItemName(recipe.OutputId, recipe.OutputDamage);
        var rules = World.Inventory.Rules;

        int remaining = times;
        for (int guard = 0; remaining > 0 && guard < 64; guard++)
        {
            // Batch size: limited by stack size of every ingredient and by what we have.
            int batch = remaining;
            foreach (var group in layout.Select((ing, i) => (ing, i)).Where(t => t.ing is not null).GroupBy(t => t.ing!.Value))
            {
                int have = CountMatching(w, group.Key);
                batch = Math.Min(batch, have / group.Count());
                batch = Math.Min(batch, rules.MaxStack(group.Key.Id));
            }
            if (batch <= 0) return ActionResult.Fail($"ran out of ingredients for {name}");

            // Lay out the pattern: for each distinct ingredient, distribute `batch` items to each of its cells.
            foreach (var group in layout.Select((ing, i) => (ing, i)).Where(t => t.ing is not null).GroupBy(t => t.ing!.Value))
            {
                var cells = group.Select(t => t.i + 1).ToList();
                var need = cells.ToDictionary(c => c, _ => batch);
                for (int g2 = 0; g2 < 32 && need.Values.Any(v => v > 0); g2++)
                {
                    if (World.Inventory.Cursor.IsEmpty)
                    {
                        int src = FindIngredientSlot(w, group.Key);
                        if (src < 0) return ActionResult.Fail($"lost track of {group.Key} while crafting {name}");
                        await ClickCoreAsync(src, false, false, ct).ConfigureAwait(false);
                    }
                    foreach (int cell in cells)
                    {
                        var cur = World.Inventory.Cursor;
                        if (cur.IsEmpty || need[cell] <= 0) continue;
                        int k = Math.Min(need[cell], cur.Count);
                        if (k == cur.Count) await ClickCoreAsync(cell, false, false, ct).ConfigureAwait(false);
                        else for (int i = 0; i < k; i++) await ClickCoreAsync(cell, true, false, ct).ConfigureAwait(false);
                        need[cell] -= cur.Count - World.Inventory.Cursor.Count;
                    }
                    if (need.Values.All(v => v <= 0) && !World.Inventory.Cursor.IsEmpty)
                        await StashCursorAsync(ct).ConfigureAwait(false);
                }
                if (need.Values.Any(v => v > 0)) return ActionResult.Fail($"couldn't lay out {group.Key} for {name}");
            }

            // Wait for the output (predicted locally; the server confirms with 0x67 on slot 0).
            if (!await WaitUntilAsync(() => w.Get(0) is { IsEmpty: false } o && o.Id == recipe.OutputId, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false))
                return ActionResult.Fail($"grid didn't produce {name} (recipe mismatch?)");

            int outBefore = CountInWindowInventory(w, recipe.OutputId);
            await ClickCoreAsync(0, false, true, ct).ConfigureAwait(false); // shift-click: craft all, straight into the inventory
            int got = CountInWindowInventory(w, recipe.OutputId) - outBefore;
            if (got <= 0)
            {
                // Inventory full for shift-click: take it on the cursor and stash.
                await ClickCoreAsync(0, false, false, ct).ConfigureAwait(false);
                if (!await StashCursorAsync(ct).ConfigureAwait(false)) return ActionResult.Fail("inventory full while crafting " + name);
                got = recipe.OutputCount;
            }
            int crafted = Math.Max(1, got / Math.Max(1, recipe.OutputCount));
            remaining -= crafted;
            // Anything left in the grid (uneven counts) goes back to the inventory.
            for (int cell = 1; cell <= gridSize * gridSize; cell++)
                if (!w.Get(cell).IsEmpty) await ClickCoreAsync(cell, false, true, ct).ConfigureAwait(false);
        }
        return remaining <= 0 ? ActionResult.Success($"crafted {name} x{times}") : ActionResult.Fail($"crafted only {times - remaining}/{times} {name}");
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
        if (Smelting.ResultFor(inputId) is not { } recipe) return ActionResult.Fail($"{ItemName(inputId)} can't be smelted");
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
            var input = w.Get(0);
            if (!input.IsEmpty && input.Id != inputId)
                return ActionResult.Fail($"furnace is busy with {ItemName(input.Id, input.Damage)} x{input.Count}");
            count = Math.Min(count, 64 - (input.IsEmpty ? 0 : input.Count));
            if (count <= 0) return ActionResult.Fail("furnace input slot is full");

            // Fuel: what's in the slot already counts; top it up with the chosen fuel.
            var fuelSlot = w.Get(1);
            int fuel = fuelId ?? (fuelSlot.IsEmpty ? 0 : fuelSlot.Id);
            if (fuel == 0 || World.Inventory.CountItem(fuel) == 0 && fuelSlot.IsEmpty)
            {
                var candidates = World.Inventory.PlayerSlots().Skip(9)
                    .Where(s => !s.IsEmpty && s.Id != inputId && !ReservedItems.Contains(s.Id) && s.Id != Items.LavaBucket);
                fuel = Smelting.BestFuel(candidates) ?? 0;
            }
            int totalItems = count + (input.IsEmpty ? 0 : input.Count);
            int burnTicksAvailable = w.BurnTimeLeft + (fuelSlot.IsEmpty ? 0 : fuelSlot.Count * Smelting.FuelTicks(fuelSlot.Id));
            int ticksNeeded = totalItems * Smelting.CookTicks - (int)(w.SmeltProgress * Smelting.CookTicks);
            int fuelUnits = 0;
            if (ticksNeeded > burnTicksAvailable)
            {
                if (fuel == 0) return ActionResult.Fail("no fuel (coal, charcoal, planks, logs, sticks)");
                if (!fuelSlot.IsEmpty && fuelSlot.Id != fuel)
                    return ActionResult.Fail($"fuel slot holds {ItemName(fuelSlot.Id)}; not enough to finish and can't mix fuels");
                fuelUnits = (int)Math.Ceiling((ticksNeeded - burnTicksAvailable) / (double)Smelting.FuelTicks(fuel));
                int fuelHave = World.Inventory.CountItem(fuel);
                if (fuelHave < fuelUnits)
                {
                    // Smelt what the fuel covers.
                    int coverable = (burnTicksAvailable + fuelHave * Smelting.FuelTicks(fuel)) / Smelting.CookTicks - (input.IsEmpty ? 0 : input.Count);
                    if (coverable <= 0) return ActionResult.Fail($"not enough fuel ({fuelHave} {ItemName(fuel)})");
                    count = Math.Min(count, coverable);
                    fuelUnits = fuelHave;
                }
            }

            int putIn = await MoveIntoSlotAsync(w, s => s.Id == inputId, 0, count, ct).ConfigureAwait(false);
            if (putIn <= 0) return ActionResult.Fail("couldn't load the furnace");
            if (fuelUnits > 0)
            {
                int f = await MoveIntoSlotAsync(w, s => s.Id == fuel, 1, fuelUnits, ct).ConfigureAwait(false);
                if (f < fuelUnits) Log($"smelt: only loaded {f}/{fuelUnits} fuel");
            }
            int queued = putIn + (input.IsEmpty ? 0 : input.Count);
            string outName = ItemName(recipe.OutputId, recipe.OutputDamage);
            if (!wait)
                return ActionResult.Success($"smelting {queued} {ItemName(inputId)} -> {outName} at {Fmt(pos.Value)}, ready in ~{queued * 10}s", putIn);

            // Stay and collect.
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
        finally
        {
            await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false);
        }
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

    private static string StackCount(ItemStack s) => s.IsEmpty ? "nothing" : $"{s.Count} {ItemName(s.Id, s.Damage)}";
}
