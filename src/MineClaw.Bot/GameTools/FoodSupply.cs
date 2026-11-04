using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Food through the shared resolver (live 10-28 13:25: main starved at food 1, "no bread in my inventory or any known chest",
/// while 420 had stored 30 wheat in the house chest that morning). Zero tokens, in order: (1) ready food in the inventory;
/// (2) ready food in known chests (withdrawn); (3) crafted from inventory + chest ingredients: bread from 3 wheat, mushroom
/// stew from a bowl + both mushrooms (<see cref="Obtainer"/> → <see cref="Crafter"/>); (4) raw meat/fish cooked when a
/// furnace is in reach (carried fuel or chest fuel), else safe raw meat eaten raw. Reserved items are never fetched, crafted
/// or eaten; food held for a hand-off isn't counted as ready (an extra copy fetched may be eaten: the held ones stay). One
/// line says what's available ("no ready food; 30 wheat in chest 11,68,-5 → can craft 10 bread (eat will do it)") or the
/// real shortfall, so nothing says "no food anywhere" while ingredients sit in a chest.
/// </summary>
public sealed class FoodSupply(IGameClient c, AcquisitionSources? sources = null)
{
    private readonly AcquisitionSources? _s = sources ?? Acquisition.Of(c);

    /// <summary>Budget hook (the acquisition planner counts each withdraw/craft/smelt as a step).</summary>
    public Action<string>? OnStep { get; init; }

    private static readonly int[] BaseReady =
        [Items.CookedPorkchop, Items.Steak, Items.CookedChicken, Items.Bread, Items.CookedFish, Items.MushroomStew, Items.Apple, Items.Cookie, Items.MelonSlice];
    private static readonly int[] BaseRaw = [Items.Porkchop, Items.RawBeef, Items.RawFish, Items.RawChicken];
    private static readonly int[] BaseFuels = [Items.Coal, Blocks.Planks, Blocks.Log];

    /// <summary>Never fetched as everyday food: golden apples (saved for critical hp), chorus fruit (teleports).</summary>
    private static readonly HashSet<int> Kept = [Items.GoldenApple, 432];

    private sealed record Lists(int[] Ready, int[] Raw, int[] Fuels);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IGameData, Lists> ByVersion = new();

    /// <summary>The version's food and fuel (<see cref="GameData.Current"/>): b1.8.1's lists first, then whatever else the
    /// version's tables call food (1.12.2: baked potato, carrot, cooked mutton/rabbit, beetroot, pumpkin pie...) and fuel (log2,
    /// coal block).</summary>
    private static Lists Current => ByVersion.GetValue(GameData.Current, BuildLists);

    private static Lists BuildLists(IGameData d)
    {
        var ready = BaseReady.Where(d.ItemExists).ToList();
        var raw = BaseRaw.Where(d.ItemExists).ToList();
        var fuels = BaseFuels.Where(d.ItemExists).ToList();
        var extraReady = new List<(int Id, int Hunger)>();
        var extraRaw = new List<(int Id, int Hunger)>();
        for (int id = 256; id < 512; id++)
        {
            if (!IsExtraFood(d, id, ready, raw, out int hunger)) continue;
            var cooked = d.SmeltingFor(id);
            if (cooked != null && d.Food(cooked.OutputId) != null) extraRaw.Add((id, hunger));
            else if (d.FoodPoisonChance(id) <= 0) extraReady.Add((id, hunger));
        }
        ready.AddRange(extraReady.OrderByDescending(x => x.Hunger).Select(x => x.Id));
        raw.AddRange(extraRaw.OrderByDescending(x => x.Hunger).Select(x => x.Id));
        foreach (var id in new[] { Ids1122.Log2, Ids1122.CoalBlock })
            if (!fuels.Contains(id) && d.ItemExists(id) && d.FuelTicks(id) > 0) fuels.Add(id);
        return new Lists(ready.ToArray(), raw.ToArray(), fuels.ToArray());
    }

    /// <summary>A version food item not already listed and not kept back; <paramref name="hunger"/> = what it restores.</summary>
    private static bool IsExtraFood(IGameData d, int id, List<int> ready, List<int> raw, out int hunger)
    {
        hunger = 0;
        if (!d.ItemExists(id) || ready.Contains(id) || raw.Contains(id) || Kept.Contains(id) || d.Food(id) is not { } f) return false;
        hunger = f.Hunger;
        return true;
    }

    /// <summary>Prepared food fetched as it is, best first (golden apples stay: they're saved for critical hp).</summary>
    public static int[] Ready => Current.Ready;

    /// <summary>Raw food: cooked when a furnace is in reach; the safe ones are eaten raw otherwise.</summary>
    public static int[] Raw => Current.Raw;

    private static int[] Fuels => Current.Fuels;

    public enum Way { Carried, Chest, Craft, Cook, EatRaw }

    /// <summary>One way to get food: <paramref name="Count"/> meals of <paramref name="ItemId"/>; <paramref name="Line"/> says it.</summary>
    public sealed record Option(Way Way, int ItemId, int Count, string Line);

    /// <summary>What is reachable now, in resolver order; <see cref="Text"/> = the first option's line, or the shortfall.</summary>
    public sealed record Outlook(IReadOnlyList<Option> Options, string Text)
    {
        public bool CanObtain => Options.Count > 0;
        public bool Carried => Options.Count > 0 && Options[0].Way == Way.Carried;
    }

    private WorldState W => c.World;
    private int Inv(int id) => W.Inventory.CountItem(id);
    private string Name(int id) => Names.Item(id);

    /// <summary>Safe (non-poisoning) food in the inventory that isn't reserved or held for a hand-off.</summary>
    public static bool HasReady(IGameClient c) => SurvivalReflex.HasSafeFood(c.World.Inventory.PlayerSlots(), c.EatReservedItems);

    private ChestStock Chests() => ChestStock.Read(c, _s?.Observations, _s?.SkipChest);

    // ------------------------------------------------------------------ survey (no actions)

    /// <summary>What food the bot can get without asking anyone (no game actions; one chest-memory read).</summary>
    public Outlook Survey() => Survey(Chests());

    private Outlook Survey(ChestStock chests)
    {
        var opts = new List<Option>();
        if (HasReady(c)) opts.Add(new Option(Way.Carried, 0, 1, "food in inventory"));
        AddChestOptions(opts, chests);
        AddBreadOption(opts, chests);
        AddStewOption(opts, chests);
        var furnace = FurnaceInReach();
        bool fuel = FuelAvailable(chests);
        foreach (var id in Raw)
            if (RawOption(id, chests, furnace, fuel) is { } o) opts.Add(o);
        return new Outlook(opts, opts.Count > 0 ? opts[0].Line : Shortfall(chests, furnace, fuel));
    }

    private void AddChestOptions(List<Option> opts, ChestStock chests)
    {
        foreach (var id in Ready)
        {
            if (c.ReservedItems.Contains(id)) continue;
            int n = chests.Count(id);
            if (n > 0) opts.Add(new Option(Way.Chest, id, n, $"{n} {Name(id)} in {Where(id, chests, carried: false)} (eat will fetch it)"));
        }
    }

    private void AddBreadOption(List<Option> opts, ChestStock chests)
    {
        if (c.ReservedItems.Contains(Items.Bread)) return;
        int wheat = Spare(Items.Wheat) + chests.Count(Items.Wheat);
        if (wheat >= 3)
            opts.Add(new Option(Way.Craft, Items.Bread, wheat / 3,
                $"no ready food; {wheat} wheat in {Where(Items.Wheat, chests)} → can craft {wheat / 3} bread (eat will do it)"));
    }

    private void AddStewOption(List<Option> opts, ChestStock chests)
    {
        if (c.ReservedItems.Contains(Items.MushroomStew)) return;
        int bowls = Spare(Items.Bowl) + chests.Count(Items.Bowl);
        int brown = Spare(Blocks.BrownMushroom) + chests.Count(Blocks.BrownMushroom), red = Spare(Blocks.RedMushroom) + chests.Count(Blocks.RedMushroom);
        int n = Math.Min(bowls, Math.Min(brown, red));
        if (n > 0)
            opts.Add(new Option(Way.Craft, Items.MushroomStew, n,
                $"no ready food; {n} bowl + mushrooms in {Where(Items.Bowl, chests)} → can craft {n} mushroom stew (eat will do it)"));
    }

    /// <summary>Raw meat/fish to cook (furnace + fuel in reach) or, when safe and stored, to eat raw; null when neither.</summary>
    private Option? RawOption(int id, ChestStock chests, string? furnace, bool fuel)
    {
        var reserved = c.ReservedItems;
        if (reserved.Contains(id) || Smelting.ResultFor(id) is not { } cooked || reserved.Contains(cooked.OutputId)) return null;
        int n = Spare(id) + chests.Count(id);
        if (n <= 0) return null;
        string where = Where(id, chests);
        if (furnace != null && fuel)
            return new Option(Way.Cook, id, n, $"no ready food; {n} {Name(id)} in {where} → can cook {n} {Name(cooked.OutputId)} at the furnace {furnace} (eat will do it)");
        if (HungerRules.IsRisky(id) || chests.Count(id) <= 0) return null;
        return new Option(Way.EatRaw, id, chests.Count(id),
            $"no ready food; {n} {Name(id)} in {where}, {(furnace == null ? "no furnace in reach" : "no fuel")} to cook it: eat will take it raw");
    }

    /// <summary>"no food anywhere: none in inventory or the 3 known chests; only 2 wheat in chest 3,65,2 (bread needs 3)".</summary>
    private string Shortfall(ChestStock chests, string? furnace, bool fuel)
    {
        string head = chests.Known == 0 ? "no food anywhere: none in inventory and no known chests"
            : $"no food anywhere: none in inventory or the {chests.Known} known chest{(chests.Known == 1 ? "" : "s")}";
        var notes = new List<string>();
        AddCarriedNotes(notes);
        AddCraftNotes(notes, chests);
        AddRawNotes(notes, chests, furnace, fuel);
        return notes.Count == 0 ? head : head + "; " + string.Join("; ", notes);
    }

    private void AddCarriedNotes(List<string> notes)
    {
        var slots = W.Inventory.PlayerSlots();
        var held = slots.Skip(9).Where(s => !s.IsEmpty && Items.Food(s.Id, s.Damage) != null && c.EatReservedItems.Contains(s.Id))
            .GroupBy(s => (int)s.Id).Select(g => $"{Name(g.Key)} x{g.Sum(s => s.Count)}").ToList();
        if (held.Count > 0) notes.Add($"carried {string.Join(", ", held)} is reserved");
        if (SurvivalReflex.HasAnyFood(slots, c.EatReservedItems)) notes.Add("only rotten flesh/raw chicken carried (use_item to eat one anyway)");
    }

    private void AddCraftNotes(List<string> notes, ChestStock chests)
    {
        int wheat = Spare(Items.Wheat) + chests.Count(Items.Wheat);
        if (c.ReservedItems.Contains(Items.Bread)) { if (wheat >= 3) notes.Add($"{wheat} wheat, but bread is reserved"); }
        else if (wheat > 0) notes.Add($"only {wheat} wheat in {Where(Items.Wheat, chests)} (bread needs 3)");
        int seeds = Spare(Items.Seeds) + chests.Count(Items.Seeds);
        if (wheat < 3 && seeds > 0) notes.Add($"{seeds} seeds to plant");
        int mush = Spare(Blocks.BrownMushroom) + chests.Count(Blocks.BrownMushroom) + Spare(Blocks.RedMushroom) + chests.Count(Blocks.RedMushroom);
        if (mush > 0) notes.Add("mushrooms, but stew needs a bowl + 1 brown + 1 red");
    }

    private void AddRawNotes(List<string> notes, ChestStock chests, string? furnace, bool fuel)
    {
        foreach (var id in Raw)
        {
            int n = Spare(id) + chests.Count(id);
            if (n > 0 && !c.ReservedItems.Contains(id))
                notes.Add($"{n} {Name(id)} in {Where(id, chests)}, but {(furnace == null ? "no furnace in reach" : fuel ? "it's reserved" : "no fuel")} to cook it");
        }
    }

    /// <summary>Carried copies a craft may use (reserved ones stay).</summary>
    private int Spare(int id) => c.ReservedItems.Contains(id) ? 0 : Inv(id);

    /// <summary>"chest 11,68,-5", "inventory + chest 11,68,-5", "chests 11,68,-5, 14,64,-5".</summary>
    private string Where(int id, ChestStock chests, bool carried = true)
    {
        var at = chests.Chests.Where(ch => ch.Items.Any(i => i.ItemId == id && i.Count > 0)).Select(ch => $"{ch.Pos.X},{ch.Pos.Y},{ch.Pos.Z}").Take(3).ToList();
        string ch = at.Count == 0 ? "" : (at.Count == 1 ? "chest " : "chests ") + string.Join(", ", at);
        bool inv = carried && Spare(id) > 0;
        return inv && ch.Length > 0 ? "inventory + " + ch : inv ? "inventory" : ch.Length > 0 ? ch : "storage";
    }

    /// <summary>"at 3,64,2" for a furnace in station range, "carried" for one in the inventory, null = none in reach.</summary>
    private string? FurnaceInReach()
    {
        if (W.FindNearestBlock(W.Player.Position, c.StationSearchRadius, Blocks.Furnace, Blocks.LitFurnace) is { } f)
            return $"{f.Pos.X},{f.Pos.Y},{f.Pos.Z}";
        return Inv(Blocks.Furnace) > 0 ? "(carried)" : null;
    }

    private bool FuelAvailable(ChestStock chests) =>
        Smelting.BestFuel(W.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty && !c.ReservedItems.Contains(s.Id))) != null
        || Fuels.Any(f => chests.Count(f) > 0);

    // ------------------------------------------------------------------ obtain

    /// <summary>How many of <paramref name="id"/> to fetch/make: enough to refill the bar plus one spare meal (2..8).</summary>
    private int Want(int id)
    {
        int hunger = Items.Food(id)?.Hunger ?? Items.Food(Smelting.ResultFor(id)?.OutputId ?? 0)?.Hunger ?? 4;
        int deficit = Math.Max(0, 20 - W.Player.Food);
        int n = (deficit + hunger - 1) / Math.Max(1, hunger) + 1;
        if (Items.Exists(id) && Items.Get(id).MaxStack <= 1) return 1;
        return Math.Clamp(n, 2, 8);
    }

    /// <summary>
    /// Gets edible food into the inventory through the resolver order (see the class remarks); Ok once safe, unreserved food is
    /// carried. The text is the chain ("took 15 wheat from 11,68,-5; crafted 5 bread") or the exact shortfall.
    /// </summary>
    public async Task<ObtainResult> ObtainAsync(CancellationToken ct)
    {
        if (HasReady(c)) return new(true, "had food");
        var chests = Chests();
        var look = Survey(chests);
        if (!look.CanObtain) return new(false, look.Text);
        var obtainer = new Obtainer(c, _s) { OnStep = OnStep };
        var tried = new List<string>();
        foreach (var o in look.Options)
        {
            ct.ThrowIfCancellationRequested();
            ObtainResult r;
            switch (o.Way)
            {
                case Way.Chest:
                case Way.EatRaw:
                    r = await obtainer.ObtainAsync(o.ItemId, -1, Inv(o.ItemId) + Math.Min(o.Count, Want(o.ItemId)), ct).ConfigureAwait(false);
                    break;
                case Way.Craft:
                    r = await obtainer.ObtainAsync(o.ItemId, 0, Inv(o.ItemId) + Math.Min(o.Count, Want(o.ItemId)), ct).ConfigureAwait(false);
                    break;
                case Way.Cook:
                    r = await CookAsync(o.ItemId, Math.Min(o.Count, Want(o.ItemId)), obtainer, ct).ConfigureAwait(false);
                    break;
                default:
                    continue;
            }
            if (HasReady(c)) return new(true, r.Text);
            tried.Add(r.Text);
        }
        return new(false, $"{look.Text}; but: {string.Join("; ", tried.Distinct())}");
    }

    /// <summary>Raw food (carried or from a chest) + fuel (carried or from a chest) → the furnace in reach (a carried one is
    /// placed), waiting for the cooked output.</summary>
    private async Task<ObtainResult> CookAsync(int raw, int n, Obtainer obtainer, CancellationToken ct)
    {
        var notes = new List<string>();
        if (Inv(raw) < n)
        {
            var got = await obtainer.ObtainAsync(raw, -1, n, ct).ConfigureAwait(false);
            if (Inv(raw) == 0) return new(false, got.Text);
            if (!got.Text.StartsWith("had ", StringComparison.Ordinal)) notes.Add(got.Text);
        }
        n = Math.Min(n, Inv(raw));
        if (!CarriesFuelFor(raw, n)) await FetchFuelAsync(n, obtainer, notes, ct).ConfigureAwait(false);
        var station = await new Crafter(c) { Observations = _s?.Observations }.EnsureStationAsync(Blocks.Furnace, ct).ConfigureAwait(false);
        if (!station.Ok) return new(false, Join(notes, $"can't cook {Name(raw)}: {station.Message}"));
        if (station.Note != null) notes.Add(station.Note);
        OnStep?.Invoke("smelt");
        var r = await c.SmeltAsync(raw, n, null, wait: true, furnace: station.Pos, ct: ct).ConfigureAwait(false);
        return new(r.Ok && HasReady(c), Join(notes, r.Ok ? $"cooked {n} {Name(Smelting.ResultFor(raw)!.OutputId)}" : $"cooking {Name(raw)} failed: {r.Message}"));
    }

    /// <summary>Enough spare carried fuel (not the raw food itself) to smelt <paramref name="n"/>.</summary>
    private bool CarriesFuelFor(int raw, int n)
    {
        var carriedFuel = W.Inventory.PlayerSlots().Skip(9).Where(s => !s.IsEmpty && !c.ReservedItems.Contains(s.Id) && s.Id != raw);
        return Smelting.BestFuel(carriedFuel) is { } fuel && carriedFuel.Where(s => s.Id == fuel).Sum(s => s.Count) >= Smelting.FuelNeeded(n, fuel);
    }

    /// <summary>Fetches the first stored fuel there is enough of (in fuel order), noting each attempt.</summary>
    private async Task FetchFuelAsync(int n, Obtainer obtainer, List<string> notes, CancellationToken ct)
    {
        var chests = Chests();
        foreach (var f in Fuels)
        {
            int need = Smelting.FuelNeeded(n, f);
            if (chests.Count(f) < need) continue;
            var got = await obtainer.ObtainAsync(f, -1, Inv(f) + need, ct).ConfigureAwait(false);
            notes.Add(got.Text);
            if (got.Ok) break;
        }
    }

    private static string Join(List<string> notes, string last) => notes.Count == 0 ? last : string.Join("; ", notes) + "; " + last;

    // ------------------------------------------------------------------ eat

    /// <summary>
    /// The one meal path (the eat tool, scripts' eat and keepFed, the hunger reflex): with safe food carried, eat it; else get
    /// some through <see cref="ObtainAsync"/> and eat. With none to be had, risky food is still tried (the client eats it only
    /// when starving); otherwise the failure names the shortfall.
    /// </summary>
    public static async Task<ActionResult> EatAsync(IGameClient c, CancellationToken ct, AcquisitionSources? sources = null)
    {
        if (c.World.Player.Food >= 20 || HasReady(c)) return await c.EatAsync(ct).ConfigureAwait(false);
        var got = await new FoodSupply(c, sources).ObtainAsync(ct).ConfigureAwait(false);
        if (got.Ok)
        {
            var r = await c.EatAsync(ct).ConfigureAwait(false);
            return r.Ok ? ActionResult.Success($"{got.Text}; {r.Message}") : ActionResult.Fail($"{got.Text}; then {r.Message}");
        }
        if (SurvivalReflex.HasAnyFood(c.World.Inventory.PlayerSlots(), c.EatReservedItems))
        {
            var r = await c.EatAsync(ct).ConfigureAwait(false);
            if (r.Ok) return r;
            return ActionResult.Fail($"{r.Message}; {got.Text}");
        }
        return ActionResult.Fail(got.Text);
    }
}
