using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// The 1.12.2 work stations beyond the crafting table and furnace (b1.8.1 has none of them): the brewing stand (5 slots: bottles 0–2,
/// ingredient 3, blaze powder 4; properties 0 brew time, 1 fuel), the enchanting table (2 slots: item 0, lapis 1; properties 0–2 the
/// offers' levels, 4–6 their hint enchantment, 7–9 the hint level; the offer is taken with Enchant Item) and the anvil (2 inputs and
/// the output 2; property 0 the level cost), plus filling glass bottles at a water source or a cauldron. Items go in by hand (pick up,
/// place): the server routes shift-clicks into these windows by item, which the chest-like window model can't predict.
/// </summary>
public sealed partial class MinecraftClient
{
    private static readonly HashSet<int> StationBlocks = [Brewing.StandBlock, Enchanting.TableBlock, Anvil.Block];

    // ------------------------------------------------------------------ water bottles

    /// <summary>Fills up to <paramref name="count"/> glass bottles at a water source block (looking at it, use) or a cauldron (use on
    /// it; a level per bottle).</summary>
    public Task<ActionResult> FillBottlesAsync(BlockPos water, int count, CancellationToken ct = default) =>
        RunActionAsync("fill_bottles", ct, c => FillBottlesCoreAsync(water, count, c));

    private int WaterBottles() => World.Inventory.PlayerSlots().Skip(9)
        .Where(s => !s.IsEmpty && s.Id == Brewing.Potion && s.Tag?.Potion == "water").Sum(s => s.Count);

    private async Task<ActionResult> FillBottlesCoreAsync(BlockPos pos, int count, CancellationToken ct)
    {
        int id = World.GetBlock(pos), meta = World.GetMeta(pos);
        bool cauldron = id == Brewing.CauldronBlock;
        if (FillRefusal(pos, id, meta, cauldron) is { } no) return ActionResult.Fail(no);
        int bottles = World.Inventory.CountItem(Brewing.GlassBottle);
        if (bottles == 0) return ActionResult.Fail("no glass bottles");
        int n = Math.Min(Math.Max(1, count), bottles);
        if (cauldron) n = Math.Min(n, meta);
        await EnsureNoWindowAsync(ct).ConfigureAwait(false);
        if (!cauldron && World.Player.EyePosition.DistanceTo(new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5)) > Options.Reach - 0.8)
        {
            var g = await GotoCoreAsync(new GoalWithinReach(pos, Options.Reach - 1.2), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, ct)
                .ConfigureAwait(false);
            if (!g.Ok) return ActionResult.Fail($"can't reach the water at {Fmt(pos)}: {g.Message}");
        }
        int filled = 0;
        for (int i = 0; i < n; i++)
        {
            if (await SelectItemAsync(Brewing.GlassBottle, null, ct).ConfigureAwait(false) < 0) break;
            if (await FillOneBottleAsync(pos, cauldron, filled, ct).ConfigureAwait(false) is { } stop) return stop;
            filled++;
        }
        return ActionResult.Success($"filled {filled} water bottles at {Fmt(pos)}", filled);
    }

    private string? FillRefusal(BlockPos pos, int id, int meta, bool cauldron)
    {
        if (!cauldron && !(id is Brewing.WaterSource or Brewing.FlowingWater && meta == 0))
            return $"no water at {Fmt(pos)} ({BlockName(id, meta)}: a water source block or a filled cauldron)";
        return cauldron && meta <= 0 ? $"the cauldron at {Fmt(pos)} is empty" : null;
    }

    /// <summary>Fills the held bottle: used on the cauldron, or looking at the water source (ItemGlassBottle: the look ray, liquids
    /// included, must end on a water source). Null = it filled; else the result to stop with.</summary>
    private async Task<ActionResult?> FillOneBottleAsync(BlockPos pos, bool cauldron, int filled, CancellationToken ct)
    {
        int before = WaterBottles();
        if (cauldron)
        {
            var r = await ClickBlockAsync(pos, ct).ConfigureAwait(false);
            if (!r.Ok) return filled > 0 ? ActionResult.Success($"filled {filled} water bottles ({r.Message})", filled) : r;
        }
        else
        {
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, new Vec3d(pos.X + 0.5, pos.Y + 0.7, pos.Z + 0.5));
            await LookCoreAsync(yaw, pitch, ct).ConfigureAwait(false);
            Send(PlayerBlockPlacementPacket.UseItem(HeldStack()));
        }
        if (await WaitUntilAsync(() => WaterBottles() > before, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)) return null;
        return filled > 0
            ? ActionResult.Success($"filled {filled} water bottles (then the water at {Fmt(pos)} gave no more)", filled)
            : ActionResult.Fail($"the bottle didn't fill at {Fmt(pos)} (can't see the water from here?)");
    }

    // ------------------------------------------------------------------ brewing stand

    /// <summary>
    /// Brews at the stand: loads up to three bottles of <paramref name="baseForm"/>/<paramref name="baseType"/> (a water bottle, an
    /// awkward potion...), tops the fuel up with blaze powder, then runs each step (the ingredient in, 20 s, the bottles checked) and
    /// takes the potions out. Every step's ingredient (and blaze powder when the stand's fuel is short) must be in the inventory.
    /// </summary>
    public Task<ActionResult> BrewAsync(BlockPos stand, string baseType, int baseForm, IReadOnlyList<Brewing.Step> steps, int count,
        CancellationToken ct = default) =>
        RunActionAsync("brew", ct, c => BrewCoreAsync(stand, baseType, baseForm, steps, count, c));

    private static bool IsPotion(ItemStack s, int form, string type) => !s.IsEmpty && s.Id == form && (s.Tag?.Potion ?? "empty") == type;

    private int InvSlot(Window w, Func<ItemStack, bool> match)
    {
        for (int i = w.MainStart; i < w.SlotCount; i++)
            if (w.Get(i) is { IsEmpty: false } s && match(s)) return i;
        return -1;
    }

    /// <summary>Shift-clicks the station's own slots back into the inventory (left by someone, or what's left of a job).</summary>
    private async Task ClearStationAsync(Window w, IEnumerable<int> slots, CancellationToken ct)
    {
        foreach (int s in slots)
            if (!w.Get(s).IsEmpty) await ClickCoreAsync(s, false, true, ct).ConfigureAwait(false);
    }

    private async Task<ActionResult> BrewCoreAsync(BlockPos pos, string baseType, int baseForm, IReadOnlyList<Brewing.Step> steps, int count, CancellationToken ct)
    {
        if (World.GetBlock(pos) != Brewing.StandBlock) return ActionResult.Fail($"no brewing stand at {Fmt(pos)}");
        if (steps.Count == 0) return ActionResult.Fail("nothing to brew");
        var inv = World.Inventory.PlayerSlots().Skip(9).ToList();
        int bases = inv.Where(s => IsPotion(s, baseForm, baseType)).Sum(s => s.Count);
        if (bases == 0) return ActionResult.Fail($"no {Brewing.Label(baseForm, baseType).ToLowerInvariant()} to brew from");
        if (MissingIngredient(inv, steps) is { } missing) return ActionResult.Fail(missing);
        int n = Math.Min(Math.Min(count, Brewing.Bottles), bases);

        var o = await OpenCoreAsync(pos, ct).ConfigureAwait(false);
        if (!o.Ok) return o;
        var w = World.Inventory.OpenWindow!;
        try
        {
            return await BrewInWindowAsync(w, baseType, baseForm, steps, n, ct).ConfigureAwait(false);
        }
        finally
        {
            await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>"no sugar (needs 2)" for the first step ingredient the inventory holds too few of, else null.</summary>
    private static string? MissingIngredient(List<ItemStack> inv, IReadOnlyList<Brewing.Step> steps)
    {
        foreach (var g in steps.GroupBy(s => (s.With.Id, s.With.Meta)))
            if (inv.Where(s => !s.IsEmpty && s.Id == g.Key.Id && (g.Key.Meta <= 0 || s.Damage == g.Key.Meta)).Sum(s => s.Count) < g.Count())
                return $"no {g.First().With.Name.Replace('_', ' ')} (needs {g.Count()})";
        return null;
    }

    /// <summary>In the stand's window: cleared, fuelled, the bottles in, each step brewed, the potions taken out.</summary>
    private async Task<ActionResult> BrewInWindowAsync(Window w, string baseType, int baseForm, IReadOnlyList<Brewing.Step> steps, int n, CancellationToken ct)
    {
        if (w.ContainerSlots != 5) return ActionResult.Fail($"that isn't a brewing stand window ({w.Title}, {w.ContainerSlots} slots)");
        await WaitUntilAsync(() => w.GetProperty(1) > 0 || !w.Get(4).IsEmpty, TimeSpan.FromMilliseconds(400), ct).ConfigureAwait(false);
        if (w.GetProperty(0) > 0 && !w.Get(3).IsEmpty) return ActionResult.Fail("the brewing stand is busy brewing");
        await ClearStationAsync(w, [0, 1, 2, 3], ct).ConfigureAwait(false);
        if (await FuelStandAsync(w, steps.Count, ct).ConfigureAwait(false) is { } unfuelled) return ActionResult.Fail(unfuelled);
        n = await LoadBottlesAsync(w, baseForm, baseType, n, ct).ConfigureAwait(false);
        if (n == 0) return ActionResult.Fail("couldn't load the bottles");
        for (int i = 0; i < steps.Count; i++)
            if (await BrewStepAsync(w, steps[i], i, n, ct).ConfigureAwait(false) is { } failed) return failed;
        var last = steps[^1];
        await ClearStationAsync(w, Enumerable.Range(0, n), ct).ConfigureAwait(false);
        return ActionResult.Success($"brewed {n} {Brewing.Label(last.Form, last.Type)}", n);
    }

    /// <summary>Fuel: one blaze powder is 20 brews; what's in the stand (fuel left, powder in slot 4) counts. Null = fuelled.</summary>
    private async Task<string?> FuelStandAsync(Window w, int brews, CancellationToken ct)
    {
        int fuel = w.GetProperty(1) + (w.Get(4) is { IsEmpty: false } f && f.Id == Brewing.BlazePowder ? f.Count * Brewing.BrewsPerPowder : 0);
        if (fuel >= brews) return null;
        if (!w.Get(4).IsEmpty && w.Get(4).Id != Brewing.BlazePowder) await ClearStationAsync(w, [4], ct).ConfigureAwait(false);
        int powders = (brews - fuel + Brewing.BrewsPerPowder - 1) / Brewing.BrewsPerPowder;
        int src = InvSlot(w, s => s.Id == Brewing.BlazePowder);
        if (src < 0) return "no blaze powder (the brewing stand's fuel)";
        return await MoveManualAsync(w, src, [4], powders, ct).ConfigureAwait(false) <= 0 ? "couldn't fuel the brewing stand" : null;
    }

    /// <summary>One base bottle into each of the first <paramref name="n"/> slots; how many went in.</summary>
    private async Task<int> LoadBottlesAsync(Window w, int baseForm, string baseType, int n, CancellationToken ct)
    {
        for (int slot = 0; slot < n; slot++)
        {
            int src = InvSlot(w, s => IsPotion(s, baseForm, baseType));
            if (src < 0 || await MoveManualAsync(w, src, [slot], 1, ct).ConfigureAwait(false) <= 0) return slot;
        }
        return n;
    }

    /// <summary>One step: the ingredient in, brewing within a moment (else the stand takes no such mix, or has no fuel), every bottle
    /// turned. Null = done; else the failure (the stand's slots given back).</summary>
    private async Task<ActionResult?> BrewStepAsync(Window w, Brewing.Step step, int i, int n, CancellationToken ct)
    {
        string with = step.With.Name.Replace('_', ' ');
        int src = InvSlot(w, s => s.Id == step.With.Id && (step.With.Meta <= 0 || s.Damage == step.With.Meta));
        if (src < 0 || await MoveManualAsync(w, src, [3], 1, ct).ConfigureAwait(false) <= 0)
            return await BrewAbortAsync(w, n, $"couldn't put the {with} in", ct).ConfigureAwait(false);
        bool started = await WaitUntilAsync(() => w.GetProperty(0) > 0 || w.Get(3).IsEmpty, TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
        if (!started)
            return await BrewAbortAsync(w, n, $"the stand won't brew {with} into {Brewing.Label(step.FromForm, step.FromType).ToLowerInvariant()}", ct)
                .ConfigureAwait(false);
        bool done = await WaitUntilAsync(() => World.Inventory.OpenWindow != w
                || w.Get(3).IsEmpty && Enumerable.Range(0, n).All(b => IsPotion(w.Get(b), step.Form, step.Type)),
            TimeSpan.FromSeconds(Brewing.BrewSeconds + 15), ct).ConfigureAwait(false);
        if (World.Inventory.OpenWindow != w) return ActionResult.Fail("the brewing stand window closed mid-brew");
        return done ? null : await BrewAbortAsync(w, n, $"step {i + 1} ({with}) didn't finish in time", ct).ConfigureAwait(false);
    }

    private async Task<ActionResult> BrewAbortAsync(Window w, int bottles, string why, CancellationToken ct)
    {
        await ClearStationAsync(w, Enumerable.Range(0, bottles).Append(3), ct).ConfigureAwait(false);
        return ActionResult.Fail(why);
    }

    // ------------------------------------------------------------------ enchanting table

    /// <summary>
    /// Enchants one plain <paramref name="itemId"/> at the table: the item and lapis go in, the three offers are read, the bill's rule
    /// picks one (<see cref="Enchanting.Choose"/>: up to offer <paramref name="maxOption"/>, the one hinting <paramref name="preferId"/>
    /// first, else the highest payable unless <paramref name="preferOnly"/>), Enchant Item takes it and the enchanted item comes back.
    /// </summary>
    public Task<ActionResult> EnchantAsync(BlockPos table, int itemId, int maxOption = 3, int? preferId = null, bool preferOnly = false,
        CancellationToken ct = default) =>
        RunActionAsync("enchant", ct, c => EnchantCoreAsync(table, itemId, maxOption, preferId, preferOnly, c));

    private static bool Plain(ItemStack s) => s.Tag is null || s.Tag.Enchantments.Count == 0 && s.Tag.StoredEnchantments.Count == 0;
    private static bool IsLapis(ItemStack s) => !s.IsEmpty && s.Id == Enchanting.Lapis && s.Damage == Enchanting.LapisMeta;

    private async Task<ActionResult> EnchantCoreAsync(BlockPos pos, int itemId, int maxOption, int? preferId, bool preferOnly, CancellationToken ct)
    {
        if (World.GetBlock(pos) != Enchanting.TableBlock) return ActionResult.Fail($"no enchanting table at {Fmt(pos)}");
        string name = ItemName(itemId);
        var inv = World.Inventory.PlayerSlots().Skip(9).ToList();
        if (!inv.Any(s => !s.IsEmpty && s.Id == itemId && Plain(s))) return ActionResult.Fail($"no plain {name} to enchant");
        int lapis = inv.Where(IsLapis).Sum(s => s.Count);
        if (lapis == 0) return ActionResult.Fail("no lapis lazuli");
        int level = World.Player.Snapshot.XpLevel;
        if (level < 1) return ActionResult.Fail("needs 1 XP levels (has 0)");

        var o = await OpenCoreAsync(pos, ct).ConfigureAwait(false);
        if (!o.Ok) return o;
        var w = World.Inventory.OpenWindow!;
        try
        {
            if (w.ContainerSlots != 2) return ActionResult.Fail($"that isn't an enchanting table window ({w.Title}, {w.ContainerSlots} slots)");
            if (await LoadTableAsync(w, itemId, name, maxOption, ct).ConfigureAwait(false) is { } unloaded) return unloaded;
            return await TakeOfferAsync(w, name, maxOption, preferId, preferOnly, ct).ConfigureAwait(false);
        }
        finally
        {
            await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The plain item and up to <paramref name="maxOption"/> lapis on the table. Null = loaded; else the failure.</summary>
    private async Task<ActionResult?> LoadTableAsync(Window w, int itemId, string name, int maxOption, CancellationToken ct)
    {
        await ClearStationAsync(w, [0, 1], ct).ConfigureAwait(false);
        int src = InvSlot(w, s => s.Id == itemId && Plain(s));
        if (src < 0 || await MoveManualAsync(w, src, [0], 1, ct).ConfigureAwait(false) <= 0) return ActionResult.Fail($"couldn't put the {name} on the table");
        int lsrc = InvSlot(w, IsLapis);
        int put = lsrc < 0 ? 0 : await MoveManualAsync(w, lsrc, [1], Math.Min(3, Math.Clamp(maxOption, 1, 3)), ct).ConfigureAwait(false);
        return put <= 0 ? await TakeBackAsync(w, [0, 1], "couldn't put the lapis in", ct).ConfigureAwait(false) : null;
    }

    /// <summary>Reads the three offers, picks one by the bill's rule, takes it with Enchant Item and the enchanted item back out.</summary>
    private async Task<ActionResult> TakeOfferAsync(Window w, string name, int maxOption, int? preferId, bool preferOnly, CancellationToken ct)
    {
        // ContainerEnchantment sends the offers once the item is in: 0..2 levels, 4..6 hint ids, 7..9 hint levels.
        bool offered = await WaitUntilAsync(() => w.GetProperty(0) > 0 || w.GetProperty(1) > 0 || w.GetProperty(2) > 0, TimeSpan.FromSeconds(3), ct)
            .ConfigureAwait(false);
        if (!offered) return await TakeBackAsync(w, [0, 1], $"the table offers nothing for the {name}", ct).ConfigureAwait(false);
        await Task.Delay(150, ct).ConfigureAwait(false); // the hints follow the levels
        var offers = Enumerable.Range(0, 3).Select(i => new Enchanting.Offer(i, w.GetProperty(i), w.GetProperty(4 + i), w.GetProperty(7 + i))).ToList();
        var (pick, why) = Enchanting.Choose(offers, World.Player.Snapshot.XpLevel, w.Get(1).IsEmpty ? 0 : w.Get(1).Count, maxOption, preferId, preferOnly);
        if (pick is null) return await TakeBackAsync(w, [0, 1], why ?? "no offer to take", ct).ConfigureAwait(false);

        Send(new EnchantItemPacket { WindowId = (sbyte)w.Id, Enchantment = (sbyte)pick.Slot });
        bool done = await WaitUntilAsync(() => w.Get(0) is { IsEmpty: false } s && !Plain(s), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        var result = w.Get(0);
        if (!done) return await TakeBackAsync(w, [0, 1], $"the table didn't enchant it (offer #{pick.Slot + 1}, level {pick.Level})", ct).ConfigureAwait(false);
        var ench = result.Tag!.Enchantments.Count > 0 ? result.Tag.Enchantments : result.Tag.StoredEnchantments;
        await ClearStationAsync(w, [0, 1], ct).ConfigureAwait(false);
        return ActionResult.Success($"enchanted {name}: {Enchanting.Describe(ench)} (offer #{pick.Slot + 1}, level {pick.Level})", 1);
    }

    private async Task<ActionResult> TakeBackAsync(Window w, int[] slots, string why, CancellationToken ct)
    {
        await ClearStationAsync(w, slots, ct).ConfigureAwait(false);
        return ActionResult.Fail(why);
    }

    // ------------------------------------------------------------------ anvil

    /// <summary>
    /// One anvil job on <paramref name="itemId"/>: <c>repair</c> (the most damaged one with its material, as many units as mend it),
    /// <c>combine</c> (two of them, the most damaged left) or <c>book</c> (an enchanted book that fits onto it). The cost is checked
    /// against the XP levels before and after the anvil prices it (40+ is "too expensive"); the result is taken out (levels paid).
    /// </summary>
    public Task<ActionResult> AnvilAsync(BlockPos anvil, string mode, int itemId, CancellationToken ct = default) =>
        RunActionAsync("anvil", ct, c => AnvilCoreAsync(anvil, mode, itemId, c));

    private static IReadOnlyDictionary<int, int> Ench(ItemStack s) => s.Tag?.Enchantments ?? new Dictionary<int, int>();

    private async Task<ActionResult> AnvilCoreAsync(BlockPos pos, string mode, int itemId, CancellationToken ct)
    {
        if (World.GetBlock(pos) != Anvil.Block) return ActionResult.Fail($"no anvil at {Fmt(pos)}");
        string label = ItemName(itemId);
        var mine = World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty && s.Id == itemId).OrderByDescending(s => s.Damage).ToList();
        if (mine.Count == 0) return ActionResult.Fail($"no {label}");
        var (job, refused) = PlanAnvil(mode, itemId, label, mine);
        if (refused is not null) return ActionResult.Fail(refused);
        if (job!.Cost >= Anvil.TooExpensive) return ActionResult.Fail($"too expensive ({job.Cost} levels; the anvil refuses 40 or more)");
        int level = World.Player.Snapshot.XpLevel;
        if (level < job.Cost) return ActionResult.Fail($"needs {job.Cost} XP levels (has {level})");

        int metaBefore = World.GetMeta(pos);
        var o = await OpenCoreAsync(pos, ct).ConfigureAwait(false);
        if (!o.Ok) return o;
        var w = World.Inventory.OpenWindow!;
        try
        {
            return await AnvilInWindowAsync(w, pos, mode, label, job, metaBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            await EnsureNoWindowAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>One anvil job as planned from the inventory: the item that goes left, what goes right (and how many), the expected
    /// level cost, and the repair material's name.</summary>
    private sealed record AnvilJob(ItemStack Left, Func<ItemStack, bool> RightMatch, int RightCount, int Cost, string Material);

    /// <summary>Plans the job for <paramref name="mode"/> on the most damaged of <paramref name="mine"/>; refused = why it can't be done.</summary>
    private (AnvilJob? Job, string? Refused) PlanAnvil(string mode, int itemId, string label, List<ItemStack> mine) => mode switch
    {
        "repair" => PlanAnvilRepair(Items.Get(itemId), label, mine[0]),
        "combine" => PlanAnvilCombine(Items.Get(itemId), itemId, label, mine),
        "book" => PlanAnvilBook(Items.Get(itemId).Name, label, mine[0]),
        _ => (null, "anvil mode is repair|combine|book"),
    };

    private (AnvilJob? Job, string? Refused) PlanAnvilRepair(ItemInfo info, string label, ItemStack left)
    {
        if (Anvil.RepairMaterial(info.Name) is not { } mat) return (null, $"nothing repairs a {label} at an anvil");
        if (left.Damage <= 0) return (null, $"the {label} isn't damaged");
        Func<ItemStack, bool> match = s => s.Id == mat.Id && (mat.Meta < 0 || s.Damage == mat.Meta) && s.Tag is null;
        int have = World.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty && match(s)).Sum(s => s.Count);
        if (have == 0) return (null, $"no {mat.Name.Replace('_', ' ')} to repair the {label} with");
        int units = Anvil.Repair(info.MaxDamage, left.Damage, have).Units;
        return (new AnvilJob(left, match, units, Anvil.RepairCost(units, left.Tag?.RepairCost ?? 0), mat.Name.Replace('_', ' ')), null);
    }

    private static (AnvilJob? Job, string? Refused) PlanAnvilCombine(ItemInfo info, int itemId, string label, List<ItemStack> mine)
    {
        if (mine.Count < 2) return (null, $"needs two {label}s to combine");
        ItemStack left = mine[0], right = mine[1];
        int cost = Anvil.CombineCost(info.Name, info.MaxDamage, left.Damage, right.Damage, Ench(left), Ench(right), left.Tag?.RepairCost ?? 0,
            right.Tag?.RepairCost ?? 0);
        return (new AnvilJob(left, s => s.Id == itemId, 1, cost, ""), null); // the left one is on the anvil by then
    }

    private (AnvilJob? Job, string? Refused) PlanAnvilBook(string name, string label, ItemStack left)
    {
        var book = World.Inventory.PlayerSlots().Skip(9).FirstOrDefault(s => !s.IsEmpty && s.Id == Enchanting.EnchantedBook && s.Tag is { } t
            && Anvil.EnchantCost(name, Ench(left), t.StoredEnchantments, true).Any);
        if (book.IsEmpty) return (null, $"no enchanted book that fits a {label}");
        int cost = Anvil.BookCost(name, Ench(left), book.Tag!.StoredEnchantments, left.Tag?.RepairCost ?? 0, book.Tag.RepairCost);
        return (new AnvilJob(left, s => s.Equals(book), 1, cost, ""), null);
    }

    /// <summary>In the anvil window: both inputs in, the price read and checked, the result taken out (levels paid).</summary>
    private async Task<ActionResult> AnvilInWindowAsync(Window w, BlockPos pos, string mode, string label, AnvilJob job, int metaBefore,
        CancellationToken ct)
    {
        if (w.ContainerSlots != 3) return ActionResult.Fail($"that isn't an anvil window ({w.Title}, {w.ContainerSlots} slots)");
        await ClearStationAsync(w, [0, 1], ct).ConfigureAwait(false);
        int ls = InvSlot(w, s => s.Equals(job.Left));
        if (ls < 0 || await MoveManualAsync(w, ls, [0], 1, ct).ConfigureAwait(false) <= 0) return ActionResult.Fail($"couldn't put the {label} on the anvil");
        int placed = await LoadAnvilRightAsync(w, job, ct).ConfigureAwait(false);
        if (placed == 0) return await TakeBackAsync(w, [0, 1], "couldn't put the second item on the anvil", ct).ConfigureAwait(false);
        var (price, unpriced) = await AnvilPriceAsync(w, ct).ConfigureAwait(false);
        if (unpriced is not null) return await TakeBackAsync(w, [0, 1], unpriced, ct).ConfigureAwait(false);
        var output = w.Get(2);
        await ClickCoreAsync(2, false, true, ct).ConfigureAwait(false);
        bool taken = await WaitUntilAsync(() => w.Get(2).IsEmpty || World.Inventory.OpenWindow != w, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (World.Inventory.OpenWindow == w) await ClearStationAsync(w, [0, 1], ct).ConfigureAwait(false);
        if (!taken) return ActionResult.Fail($"the anvil wouldn't give the {label} ({price} levels)");
        await WaitTicksAsync(2, ct).ConfigureAwait(false);
        return ActionResult.Success($"{AnvilDone(mode, label, placed, job.Material, output)} for {price} levels{AnvilWear(pos, metaBefore)}", 1);
    }

    /// <summary>Puts the right-hand input in (up to <see cref="AnvilJob.RightCount"/>, from as many stacks as needed); how many went in.</summary>
    private async Task<int> LoadAnvilRightAsync(Window w, AnvilJob job, CancellationToken ct)
    {
        int placed = 0;
        for (int guard = 0; guard < 8 && placed < job.RightCount; guard++)
        {
            int rs = InvSlot(w, job.RightMatch);
            if (rs < 0) break;
            int k = await MoveManualAsync(w, rs, [1], job.RightCount - placed, ct).ConfigureAwait(false);
            if (k <= 0) break;
            placed += k;
        }
        return placed;
    }

    /// <summary>The anvil's price once it shows a result; unpriced = why it can't be taken (nothing made, too expensive, too few levels).</summary>
    private async Task<(int Price, string? Unpriced)> AnvilPriceAsync(Window w, CancellationToken ct)
    {
        bool priced = await WaitUntilAsync(() => !w.Get(2).IsEmpty, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        int price = w.GetProperty(0);
        if (!priced) return (price, price >= Anvil.TooExpensive ? $"too expensive ({price} levels)" : "the anvil makes nothing of these");
        int level = World.Player.Snapshot.XpLevel;
        return price > 0 && level < price ? (price, $"needs {price} XP levels (has {level})") : (price, null);
    }

    private static string AnvilDone(string mode, string label, int placed, string material, ItemStack output) => mode switch
    {
        "repair" => $"repaired the {label} with {placed} {material}",
        "combine" => $"combined two {label}s",
        _ => $"put {Enchanting.Describe(Ench(output))} on the {label}",
    };

    /// <summary>"; the anvil broke", "; the anvil is chipped now", or "" when it kept its condition.</summary>
    private string AnvilWear(BlockPos pos, int metaBefore)
    {
        int idAfter = World.GetBlock(pos), metaAfter = World.GetMeta(pos);
        if (idAfter != Anvil.Block) return "; the anvil broke";
        return Anvil.Condition(metaAfter) != Anvil.Condition(metaBefore) ? $"; the anvil is {Anvil.Condition(metaAfter)} now" : "";
    }
}
