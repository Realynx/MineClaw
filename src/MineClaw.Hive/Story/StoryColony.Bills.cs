using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Workbenches and bills (docs/web/story-mode.md "Workbenches &amp; bills"): crafting tables and furnaces in the home zone (found by
/// themselves, or built), each with an ordered list of bills. A pawn with the bill's work type on (and allowed by the bill) takes the
/// first runnable bill on the nearest free bench: one pawn per bench. It fetches the ingredients from storages within the bill's
/// radius, crafts or loads the furnace (and goes on with other work while it smelts; a later job collects), and the product goes to
/// storage per the bill. Missing ingredients suspend the bill with the reason and an alert.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>With <see cref="ColonyMeta.DetectBenches"/>: benches near home and in houses join the colony by themselves. Off (the
    /// default): only designated benches count (<see cref="WorkbenchOp"/>, a bill added at one, one the colony placed).</summary>
    private void ScanBenches()
    {
        if (!Meta.DetectBenches || World is not { } w || Home() is not { } home) return;
        // Every house (home zone) and the primary home's surroundings.
        var areas = Zones.Where(z => z.Kind == "home").Select(z => (Area: z.Area, Cells: (ZoneCells?)z.Cells)).ToList();
        areas.Add((Area.Of(home.X - 12, home.Y - 4, home.Z - 12, home.X + 12, home.Y + 6, home.Z + 12), null));
        foreach (var (a, cells) in areas)
        {
            if (a.Volume > 100_000) continue;
            foreach (var p in a.Blocks())
                if ((cells == null || cells.Contains(p.X, p.Z)) && StoryRecipes.BenchOf(w.Block(p.X, p.Y, p.Z).Id) is { } kind && Benches.All(b => b.Pos != p))
                {
                    Benches.Add(new WorkbenchState { Pos = p, Kind = kind, Rev = Touch() });
                    MaterialsChanged();
                }
        }
    }

    private WorkbenchState? Bench(P3 at) => Benches.FirstOrDefault(b => b.Pos == at);

    /// <summary>
    /// <c>POST …/workbenches</c> <c>{op: designate|release, pos}</c>: the owner marks a placed crafting table / furnace (any bench kind)
    /// as the colony's, or gives it back. Designated benches are the ones that take bills and unlock the Architect's tiers; a released
    /// bench's bills go with it (the builds' auto bills move to another bench, or wait).
    /// </summary>
    public (JsonNode? Result, string? Error) WorkbenchOp(string op, P3 pos, string? fuel = null)
    {
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            switch (op)
            {
                case "designate":
                {
                    if (Bench(pos) is { } have) return (new JsonObject { ["pos"] = pos.Json(), ["kind"] = have.Kind, ["designated"] = true }, null);
                    if (StoryRecipes.BenchOf(w.Block(pos.X, pos.Y, pos.Z).Id) is not { } kind) return (null, "no workbench there (crafting table, furnace, brewing stand, anvil, enchanting table)");
                    Benches.Add(new WorkbenchState { Pos = pos, Kind = kind, Rev = Touch() });
                    MaterialsChanged();
                    WakeIdle();
                    return (new JsonObject { ["pos"] = pos.Json(), ["kind"] = kind, ["designated"] = true }, null);
                }
                case "release":
                {
                    if (Bench(pos) is not { } bench) return (null, "that workbench isn't the colony's");
                    Benches.Remove(bench);
                    Removed("workbench", pos.ToString());
                    foreach (var k in _leases.Keys.Where(k => k == "bench:" + pos).ToList()) _leases.Remove(k);
                    MaterialsChanged();
                    return (new JsonObject { ["pos"] = pos.Json(), ["kind"] = bench.Kind, ["designated"] = false }, null);
                }
                case "fuel":
                {
                    // A furnace's fuel choice (StoryColony.Fuel.cs); designating it if it wasn't the colony's yet.
                    var bench = Bench(pos);
                    if (bench == null)
                    {
                        if (StoryRecipes.BenchOf(w.Block(pos.X, pos.Y, pos.Z).Id) is not { } kind) return (null, "no workbench there");
                        if (kind != "furnace" || !StoryFuels.Valid(w.Data, fuel)) return SetBenchFuel(new WorkbenchState { Pos = pos, Kind = kind }, fuel);
                        Benches.Add(bench = new WorkbenchState { Pos = pos, Kind = kind, Rev = Touch() });
                    }
                    return SetBenchFuel(bench, fuel);
                }
                default:
                    return (null, "op must be designate|release|fuel");
            }
        }
    }

    private static readonly JsonSerializerOptions J = StoryJson.Options;

    /// <summary>add {bench, bill} | update {id, …} | remove|pause|resume {id} | reorder {bench, ids}.</summary>
    public (JsonNode? Result, string? Error) BillsOp(string op, JsonObject body)
    {
        if (World is not { } w) return (null, "no world view yet");
        lock (_lock)
        {
            var id = body["id"]?.GetValue<string>();
            BillState? Find(out WorkbenchState? bench)
            {
                foreach (var b in Benches)
                    if (b.Bills.FirstOrDefault(x => x.Id == id) is { } bill) { bench = b; return bill; }
                bench = null;
                return null;
            }
            switch (op)
            {
                case "add":
                {
                    if (P3.Parse(body["bench"]) is not { } at) return (null, "bench [x,y,z] required");
                    var bench = Bench(at);
                    if (bench == null)
                    {
                        if (StoryRecipes.BenchOf(w.Block(at.X, at.Y, at.Z).Id) is not { } kind) return (null, "no workbench there");
                        Benches.Add(bench = new WorkbenchState { Pos = at, Kind = kind, Rev = Touch() });
                    }
                    if (!StoryRecipes.SupportedIn(w.Data, bench.Kind))
                        return (null, $"{w.Data.VersionId} has no {Pretty(bench.Kind)} bills (b1.8.1 has no brewing stands, anvils or enchanting tables)");
                    if (body["bill"] is not JsonObject spec) return (null, "bill required");
                    var (bill, err) = ParseBill(w, spec, bench, null);
                    if (bill == null) return (null, err);
                    bill.Id = NextId("bill");
                    bench.Bills.Add(bill);
                    bench.Rev = Touch();
                    MaterialsChanged(); // an owner bill making a build chain's step takes that step over (StoryColony.Claims.cs)
                    WakeIdle();
                    return (new JsonObject { ["id"] = bill.Id }, null);
                }
                case "update":
                {
                    if (Find(out var bench) is not { } bill) return (null, "no such bill");
                    if (bill.ForBuild != null) return (null, $"an auto bill (for build {bill.ForBuild}) can only be cancelled");
                    var (upd, err) = ParseBill(w, body, bench!, bill);
                    if (upd == null) return (null, err);
                    bench!.Rev = Touch();
                    return (new JsonObject { ["id"] = bill.Id }, null);
                }
                case "remove":
                case "pause":
                case "resume":
                {
                    if (Find(out var bench) is not { } bill) return (null, "no such bill");
                    if (bill.ForBuild != null && op != "remove") return (null, $"an auto bill (for build {bill.ForBuild}) can only be cancelled");
                    if (op == "remove")
                    {
                        bench!.Bills.Remove(bill);
                        // Cancelling an auto bill: that build waits for the item instead of crafting it (its other auto bills for it go too).
                        if (bill.ForBuild != null && Builds.FirstOrDefault(b => b.Id == bill.ForBuild) is { } fb && bill.ForItem is { } fi && !fb.NoCraft.Contains(fi))
                        {
                            fb.NoCraft.Add(fi);
                            fb.Rev = Touch();
                            foreach (var other in Benches) other.Bills.RemoveAll(x => x.ForBuild == fb.Id && x.ForItem == fi && other.LoadedBill != x.Id);
                        }
                        else if (bill.ForBuild == ToolsTag && bill.ForItem is { } ti) ToolCraftCancelled(ti); // that tool isn't made by itself again
                        MaterialsChanged();
                    }
                    else { bill.Paused = op == "pause"; MaterialsChanged(); if (op == "resume") { bill.SuspendedReason = null; _billFails.Remove(bill.Id); _noOffer.Remove(bill.Id); } }
                    bench!.Rev = Touch();
                    return (new JsonObject { ["id"] = bill.Id }, null);
                }
                case "reorder":
                {
                    if (P3.Parse(body["bench"]) is not { } at || Bench(at) is not { } bench) return (null, "no such bench");
                    var ids = body["ids"] is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : [];
                    if (ids.Count == 0 || ids.Any(i => bench.Bills.All(b => b.Id != i))) return (null, "ids must name this bench's bills");
                    if (bench.Bills.Any(b => b.ForBuild != null && ids.Contains(b.Id))) return (null, "auto bills follow their build's queue order: reorder the builds instead");
                    var rest = bench.Bills.Where(b => !ids.Contains(b.Id)).ToList();
                    bench.Bills = ids.Distinct().Select(i => bench.Bills.First(b => b.Id == i)).Concat(rest).ToList();
                    bench.Rev = Touch();
                    return (new JsonObject { ["ids"] = new JsonArray(bench.Bills.Select(b => (JsonNode)b.Id!).ToArray()) }, null);
                }
                case "release_claim":
                    // The owner frees what a build holds for one of its items (StoryColony.Claims.cs).
                    return ReleaseClaim(body["build"]?.GetValue<string>(), body["item"]?.GetValue<string>());
                case "enchant_tools":
                {
                    // The tools system's hook (StoryEnchanting.cs EnchantTools): {prefer, items?, count?, prefer_only?}.
                    var prefer = body["prefer"]?.GetValue<string>() ?? (body["material"]?.GetValue<string>() is { } mat ? StoryEnchanting.PreferFor(mat) : null);
                    if (prefer == null) return (null, "prefer (an enchantment) or material required");
                    var items = body["items"] is JsonArray ia ? ia.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() : null;
                    var (ids, err) = EnchantTools(prefer, items, body["count"]?.GetValue<int>() ?? 1, body["prefer_only"]?.GetValue<bool>() ?? false);
                    if (err != null && ids.Count == 0) return (null, err);
                    return (new JsonObject { ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i!).ToArray()), ["note"] = err }, null);
                }
                default:
                    return (null, "op must be add|update|remove|pause|resume|reorder|enchant_tools|release_claim");
            }
        }
    }

    private (BillState? Bill, string? Error) ParseBill(IStoryWorld w, JsonObject spec, WorkbenchState bench, BillState? into)
    {
        var bill = into ?? new BillState();
        if (spec["recipe"]?.GetValue<string>() is { } rid)
        {
            if (StoryRecipes.Get(w.Data, rid) is not { } r || r.Workbench != bench.Kind) return (null, $"no recipe '{rid}' at a {bench.Kind}");
            bill.Recipe = r.Id;
        }
        else if (into == null) return (null, "recipe required");
        if (spec["mode"]?.GetValue<string>() is { } mode)
        {
            if (mode is not ("count" or "until" or "forever")) return (null, "mode must be count|until|forever");
            bill.Mode = mode;
        }
        if (spec["target"] is { } t) bill.Target = Math.Clamp(t.GetValue<int>(), 1, 100_000);
        if (spec["radius"] is { } rad) bill.Radius = Math.Clamp(rad.GetValue<int>(), 1, 256);
        if (spec["pawn"]?.GetValue<string>() is { } pw)
        {
            if (pw != "any" && Pawn(pw) == null) return (null, $"no pawn '{pw}'");
            bill.Pawn = pw == "any" ? "any" : Pawn(pw)!.Name;
        }
        if (spec.ContainsKey("min_skill")) bill.MinSkill = spec["min_skill"] is { } ms ? Math.Clamp(ms.GetValue<int>(), 0, 20) : null;
        if (spec["ingredients"] is JsonObject ing)
        {
            var f = ing.Deserialize<StorageFilter>(J) ?? new StorageFilter();
            if (f.Allow.Count == 0) f.Allow = ["*"];
            if (Cats is { } c && f.Allow.Concat(f.Deny).FirstOrDefault(e => !c.Valid(e)) is { } bad) return (null, $"unknown ingredient filter '{bad}'");
            bill.Ingredients = f;
        }
        if (spec["store"] is { } st)
        {
            if (st is JsonValue v && v.TryGetValue<string>(out var s) && s == "best") bill.Store = JsonValue.Create("best");
            else if (st is JsonObject so && P3.Parse(so["chest"]) is { } chest) bill.Store = new JsonObject { ["chest"] = chest.Json() };
            else return (null, "store must be \"best\" or {chest:[x,y,z]}");
        }
        if (spec["paused"] is { } pz) bill.Paused = pz.GetValue<bool>();
        if (spec.ContainsKey("fuel"))
        {
            // A furnace bill's fuel override (StoryColony.Fuel.cs); null, "" or "inherit" = the furnace's setting.
            var fu = spec["fuel"]?.GetValue<string>();
            if (fu is null or "" or "inherit") bill.Fuel = null;
            else if (bench.Kind != "furnace") return (null, "fuel is for furnace bills (a brewing stand burns blaze powder)");
            else if (!StoryFuels.Valid(w.Data, fu)) return (null, $"fuel must be inherit|{string.Join("|", StoryFuels.ValidFor(w.Data))}");
            else bill.Fuel = fu;
            if (bill.SuspendedReason?.StartsWith("needs fuel", StringComparison.Ordinal) == true) bill.SuspendedReason = null;
        }
        if (StoryRecipes.Get(w.Data, bill.Recipe)?.Station is { } station)
        {
            // Station bills (StoryColony.Stations.cs): the modes the product allows, an enchant's option and preference.
            if (!station.Modes.Contains(bill.Mode))
                return (null, $"mode must be {string.Join("|", station.Modes)} for this recipe (an enchanted or repaired item can't be told apart in storage)");
            if (spec.ContainsKey("option")) bill.Option = spec["option"] is { } opt ? Math.Clamp(opt.GetValue<int>(), 1, 3) : null;
            if (spec.ContainsKey("prefer"))
            {
                var pref = spec["prefer"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(pref)) bill.Prefer = null;
                else if (station.Kind != "enchant") return (null, "prefer is for enchanting bills");
                else if (MineClaw.Data.Enchanting.ByName(pref) is not { } e) return (null, $"unknown enchantment '{pref}'");
                else if (!MineClaw.Data.Enchanting.Fits(e, station.Item)) return (null, $"{e.DisplayName} doesn't go on {Pretty(station.Item)} at a table");
                else bill.Prefer = e.Name;
            }
            if (spec["prefer_only"] is { } po) bill.PreferOnly = po.GetValue<bool>();
            if (spec.ContainsKey("prefer") || spec.ContainsKey("option") || spec.ContainsKey("prefer_only")) _noOffer.Remove(bill.Id);
        }
        return (bill, null);
    }

    private readonly Dictionary<string, int> _billFails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _billAlerted = new(StringComparer.Ordinal);

    /// <summary>Is the bill still wanted (count not reached, until-amount not in stock, forever)?</summary>
    internal bool Wanted(BillState b, StoryRecipe r) => b.Mode switch
    {
        "count" => b.Done < b.Target,
        "until" => StockOf(r) < b.Target,
        _ => true,
    };

    /// <summary>
    /// How much of a bill's product the colony has, every chest and every pocket: a family product (planks from any logs) counts every
    /// variant, a tool or armour piece every wear state (a used pickaxe is still a pickaxe: live 11-04, "stone_pickaxe until 10" made 11).
    /// </summary>
    internal int StockOf(StoryRecipe r) =>
        CountItem(r.Output.Id, r.AnyOutput || World?.Data is { } d && d.ItemExists(r.Output.Id) && d.Item(r.Output.Id).MaxDamage > 0 ? -1 : r.Output.Meta);

    private bool MayTake(BillState b, PawnState p, string work) =>
        (b.Pawn == "any" || b.Pawn.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) && (b.MinSkill is not { } m || (p.Skills.GetValueOrDefault(work)?.Level ?? 0) >= m);

    /// <summary>Each ingredient's source (a pawn's own stack, else a chest within the radius), or what's missing.</summary>
    private (List<(P3? Chest, Stack Stack)> Got, string? Missing) Ingredients(BillState b, StoryRecipe r, int crafts, WorkbenchState bench, PawnLive live)
    {
        var got = new List<(P3?, Stack)>();
        foreach (var input in r.Inputs)
        {
            int need = input.Count * crafts;
            bool Ok(Stack s) => input.Options.Any(o => o.Matches(s.Id, s.Meta)) && Cats!.Accepts(b.Ingredients, s.Id, s.Meta);
            if (live.Inventory.Where(Ok).Sum(s => s.Count) >= need) { got.Add((null, live.Inventory.First(Ok) with { Count = need })); continue; }
            // Whatever variants are stocked, several stacks if need be (vanilla's wildcard recipes take mixed planks; a family bill
            // takes any log): the most plentiful first, then the nearest. Live 11-04: "no oak wood planks" with birch in the chest.
            int left = need;
            foreach (var (store, stack) in Storages.Where(s => s.Chest.Dist(bench.Pos) <= b.Radius)
                         .SelectMany(s => s.Contents.Where(Ok).Select(c => (Store: s, Stack: c)))
                         .OrderByDescending(x => x.Stack.Count).ThenBy(x => x.Store.Chest.Dist(bench.Pos)))
            {
                if (left <= 0) break;
                if (r.Fuel && got.Any(g => g.Item1 != null && (g.Item2.Id != stack.Id || g.Item2.Meta != stack.Meta))) continue; // a furnace load is one item
                int n = Math.Min(left, stack.Count);
                got.Add((store.Chest, stack with { Count = n }));
                left -= n;
            }
            if (left == need) return (got, MissingText(input, b.Radius, bench)); // some is a smaller batch; none is missing
        }
        return (got, null);
    }

    /// <summary>"no planks within 32 blocks — needs logs (chop trees)": an any-variant input says its family, and where its raw
    /// material comes from when the colony has none of it.</summary>
    private string MissingText(RecipeInput input, int radius, WorkbenchState? bench = null)
    {
        var d = World!.Data;
        var first = input.Options[0];
        var fam = input.Options.All(o => o.Meta < 0) || input.Options.Select(o => o.Id).Distinct().Count() > 1 ? StoryRecipeFamilies.FamilyOf(first.Id) : null;
        var name = fam ?? d.DisplayName(first.Id, Math.Max(0, first.Meta)).ToLowerInvariant();
        // What it looked at (StoryColony.StockCheck.cs): "no planks: checked 6 chests within 32 of the bench (2 not looked into lately)".
        var text = bench != null ? $"no {name}: {CheckedText(bench, radius)}" : $"no {name} within {radius} blocks";
        if (PocketText(input, null) is { } pockets) text += $"; {pockets} (stored soon)";
        if (StoryRecipeFamilies.Source(first.Id) is not { } from) return text;
        // Planks or sticks with logs in stock: they can be made (the "any" bills); with no logs either: chop trees.
        bool logs = Storages.Any(s => s.Contents.Any(c => c.Id is 17 or 162));
        if (first.Id is 5 or 280 && logs) return text + " — make them from the stored logs (a \"from any logs\" bill)";
        return text + $" — needs {(first.Id is 5 or 280 or 17 or 162 ? "logs" : name)} ({from})";
    }

    /// <summary>A bill job (craft or cook), or collecting a furnace that is done, for this pawn; null = none runnable.</summary>
    private ActiveJob? BillJob(PawnState p, PawnLive live, string work)
    {
        if (World is not { } w) return null;
        EnsureMaterials(); // the builds' auto bills are on the benches
        RetryOnStockChange(); // a deposit or a look since: the waiting bills try again now (StoryColony.StockCheck.cs)
        var from = live.Block;
        foreach (var bench in Benches.OrderBy(b => b.Pos.Dist(from)))
        {
            var key = "bench:" + bench.Pos;
            if (!Open(key, p.Name, null)) continue;
            // A loaded furnace that is done: collect (the bill's work type).
            if (bench.LoadedBill is { } lb && bench.ReadyAt <= Now)
            {
                // Its bill may be gone (removed, finished, an auto bill the build no longer wants): the output is collected all the same.
                var loaded = bench.Bills.FirstOrDefault(b => b.Id == lb);
                var lr = (bench.LoadedRecipe ?? loaded?.Recipe) is { } lrid ? StoryRecipes.Get(w.Data, lrid) : null;
                if ((lr?.WorkType ?? WorkTypes.Craft) != work) continue;
                return Job(work, "collect_furnace", $"collecting {bench.LoadedCount} {(lr != null ? w.Data.DisplayName(lr.Output.Id, lr.Output.Meta).ToLowerInvariant() : "items")} from the furnace",
                    bench.Pos, key, "furnace", lb, xp: 10, count: bench.LoadedCount);
            }
            if (bench.LoadedBill != null) continue; // still smelting
            if (StoryRecipes.Stations.Contains(bench.Kind) && (!StoryRecipes.SupportedIn(w.Data, bench.Kind) || StationGone(bench))) continue;
            foreach (var bill in bench.Bills)
            {
                if (bill.Paused || bill.SuspendedReason != null) continue;
                if (StoryRecipes.Get(w.Data, bill.Recipe) is not { } r || r.WorkType != work || !MayTake(bill, p, work)) continue;
                if (!Wanted(bill, r)) continue;
                if (r.Station != null)
                {
                    // Brewing stand, enchanting table, anvil (StoryColony.Stations.cs): their own jobs, XP levels and reasons.
                    var (sj, why) = StationJob(p, live, work, bench, bill, r, key);
                    if (sj != null) return sj;
                    if (why == null || bill.ForBuild != null) continue;
                    bill.SuspendedReason = why;
                    bench.Rev = Touch();
                    if (_billAlerted.GetValueOrDefault(bill.Id) != why) { _billAlerted[bill.Id] = why; Alert("warn", $"Bill {r.Label} suspended: {why}"); }
                    continue;
                }
                int left = bill.Mode == "count" ? bill.Target - bill.Done : bill.Mode == "until" ? bill.Target - StockOf(r) : 64;
                int crafts = Math.Clamp((int)Math.Ceiling(Math.Max(1, left) / (double)Math.Max(1, r.Output.Count)), 1, bench.Kind == "furnace" ? 8 : 16);
                var (got, missing) = Ingredients(bill, r, crafts, bench, live);
                bool auto = bill.ForBuild != null;
                // An auto bill whose ingredient isn't there yet waits quietly (an earlier step makes it; a raw shortfall is the build's alert).
                if (missing != null && auto) continue;
                // The owner's bill leaves what the builds reserved (their items, their chains' inputs), except an input of a chain that
                // makes what this bill makes (it does that step for the build: StoryColony.Claims.cs). What is free runs as a partial
                // batch; the bill waits only when nothing is, and says who holds it.
                if (missing == null && !auto && _reserved.Count > 0)
                {
                    int fit = crafts, fitFree = 0;
                    RecipeInput? held = null;
                    foreach (var i in r.Inputs)
                    {
                        bool M(int id, int meta) => i.Options.Any(o => o.Matches(id, meta));
                        // Only an input something holds limits the batch (live 11-04: "the wheat in storage is reserved for builds"
                        // with no wheat reserved at all: any reservation compared the whole batch with the stock).
                        if (!_reserved.Any(kv => kv.Value > 0 && M(kv.Key.Id, kv.Key.Meta))) continue;
                        int own = live.Inventory.Where(s => M(s.Id, s.Meta)).Sum(s => s.Count);
                        int free = UnreservedFor(M, r.Output.Id);
                        int can = (own + Math.Max(0, free)) / Math.Max(1, i.Count);
                        if (can < fit) { fit = can; held = i; fitFree = free; }
                    }
                    if (fit <= 0) missing = ReservedText(held!, r, fitFree);
                    else if (fit < crafts)
                    {
                        crafts = fit;
                        (got, missing) = Ingredients(bill, r, crafts, bench, live);
                    }
                }
                if (missing != null && missing.StartsWith("no ", StringComparison.Ordinal))
                {
                    // Before waiting: the bodies' latest looks into the chests around the bench, then a pawn looks into the ones nobody
                    // has lately (a player's deposit is invisible until a body opens the chest: StoryColony.StockCheck.cs).
                    if (RefreshSeen(Storages.Where(s => s.Chest.Dist(bench.Pos) <= bill.Radius)))
                    {
                        RetryOnStockChange();
                        (got, missing) = Ingredients(bill, r, crafts, bench, live);
                    }
                    if (missing != null && LookJob(p, work, bench, bill, missing[3..].Split(':')[0]) is { } look) return look;
                }
                if (missing != null)
                {
                    bill.SuspendedReason = missing;
                    bench.Rev = Touch();
                    if (_billAlerted.GetValueOrDefault(bill.Id) != missing) { _billAlerted[bill.Id] = missing; Alert("warn", $"Bill {r.Label} suspended: {missing}"); }
                    continue;
                }
                var fetch = new JsonArray();
                var job = Job(work, bench.Kind == "furnace" ? "smelt" : "craft", r.Label + (bill.Mode == "count" ? $" ({bill.Done}/{bill.Target})" : ""), bench.Pos, key,
                    "bill", bill.Id, "working", f =>
                    {
                        // A family smelt (charcoal from any logs) smelts the log it fetched (one item id per furnace load).
                        f["item"] = bench.Kind == "furnace" && r.Any && got.FirstOrDefault().Chest != null ? ItemName(got[0].Stack.Id, got[0].Stack.Meta) : r.ToolItem;
                        f["n"] = bench.Kind == "furnace" ? crafts : crafts * r.Output.Count;
                        f["fetch"] = fetch;
                        f["recipe"] = r.Id;
                    }, xp: 25, count: crafts * r.Output.Count);
                foreach (var (chest, st) in got.Where(g => g.Chest != null))
                {
                    fetch.Add(new JsonObject { ["from"] = chest!.Value.ToString(), ["item"] = ItemName(st.Id, st.Meta), ["n"] = st.Count });
                    job.Takes.Add((chest.Value, st));
                }
                if (bench.Kind == "furnace")
                {
                    // Fuel (StoryColony.Fuel.cs): the bill's or the furnace's choice, topped up to what this batch needs beyond what the
                    // furnace still holds; the pawn's own first, else a chest.
                    var (fuel, units, src, noFuel) = PickFuel(bench, bill, crafts, live);
                    if (noFuel != null && auto) continue; // the build's materials say it (fuel short)
                    if (noFuel != null)
                    {
                        bill.SuspendedReason = noFuel;
                        bench.Rev = Touch();
                        if (_billAlerted.GetValueOrDefault(bill.Id) != noFuel) { _billAlerted[bill.Id] = noFuel; Alert("warn", $"Bill {r.Label} suspended: {noFuel}"); }
                        continue;
                    }
                    if (src is { } fs)
                    {
                        fetch.Add(new JsonObject { ["from"] = fs.Chest.ToString(), ["item"] = TakeName(fs.Stack), ["n"] = fs.Stack.Count });
                        job.Takes.Add((fs.Chest, fs.Stack));
                    }
                    job.Json["fuel"] = fuel != null ? ItemName(fuel.Item.Id, 0) : null; // the only fuel the pawn loads
                    job.Json["fuel_units"] = units;
                    job.Json["fuel_per"] = fuel?.ItemsPerUnit;
                }
                else
                {
                    // The product goes to storage right after crafting: the bill's chest, else the best one.
                    var dst = bill.Store is JsonObject so && P3.Parse(so["chest"]) is { } c ? StorageAt(c) : BestChest(r.Output.Id, r.Output.Meta, bench.Pos);
                    if (dst != null)
                    {
                        job.Json["stash"] = new JsonArray(new JsonObject { ["to"] = dst.Chest.ToString(), ["items"] = new JsonArray(ItemName(r.Output.Id, r.Output.Meta)) });
                        job.Puts.Add((dst.Chest, [r.Output with { Count = crafts * r.Output.Count }]));
                    }
                }
                return job;
            }
        }
        return null;
    }

    private void BillDone(PawnState p, ActiveJob j, string? note = null)
    {
        // A furnace loaded: it smelts on whether or not its bill is still there (an auto bill can go while the pawn walks).
        if (j.Source == "bill" && j.Kind == "smelt" && Benches.FirstOrDefault(b => b.Pos == j.At && b.Kind == "furnace") is { } fur)
        {
            if (fur.Bills.FirstOrDefault(b => b.Id == j.SourceId) is { } lb) _billFails.Remove(lb.Id);
            MaterialsChanged();
            int items = Math.Max(1, j.Json["n"]?.GetValue<int>() ?? 1);
            fur.LoadedBill = j.SourceId;
            fur.LoadedCount = j.Count;
            fur.LoadedRecipe = j.Json["recipe"]?.GetValue<string>();
            fur.LoadedAt = Now;
            fur.ReadyAt = Now + TimeSpan.FromSeconds(StoryRecipes.SmeltSeconds * items);
            FuelLoaded(fur, j);
            fur.Rev = Touch();
            return;
        }
        foreach (var bench in Benches)
        {
            if (j.Source == "furnace" && bench.LoadedBill == j.SourceId && j.At == bench.Pos)
            {
                // Collected: the bill counts what came out (done on collection, never on loading); what still smelts stays loaded.
                MaterialsChanged();
                var m = note == null ? null : System.Text.RegularExpressions.Regex.Match(note, @"collected (\d+)");
                int got = m is { Success: true } ? int.Parse(m.Groups[1].Value) : bench.LoadedCount;
                var still = note == null ? null : System.Text.RegularExpressions.Regex.Match(note, @"still smelting (\d+)");
                if (bench.Bills.FirstOrDefault(b => b.Id == j.SourceId) is { } fb) fb.Done += Math.Min(got, bench.LoadedCount);
                int left = bench.LoadedCount - got;
                if (left > 0 && still is { Success: true } && int.Parse(still.Groups[1].Value) > 0)
                {
                    bench.LoadedCount = left;
                    bench.ReadyAt = Now + TimeSpan.FromSeconds(StoryRecipes.SmeltSeconds * int.Parse(still.Groups[1].Value));
                }
                else { bench.LoadedBill = null; bench.LoadedCount = 0; bench.ReadyAt = null; bench.LoadedRecipe = null; bench.LoadedAt = null; }
                bench.Rev = Touch();
                _lastSource[p.Name] = "furnace"; // what it took out is stored next (deliveries)
                return;
            }
            if (bench.Bills.FirstOrDefault(b => b.Id == j.SourceId) is not { } bill) continue;
            _billFails.Remove(bill.Id);
            MaterialsChanged();
            if (bench.Kind == "furnace")
            {
                // Loaded and left to smelt: another job collects when it's done.
                bench.LoadedBill = bill.Id;
                bench.LoadedCount = j.Count;
                FuelLoaded(bench, j);
                bench.ReadyAt = Now + TimeSpan.FromSeconds(StoryRecipes.SmeltSeconds * Math.Max(1, j.Json["n"]?.GetValue<int>() ?? 1));
            }
            else bill.Done += j.Count;
            if (StoryRecipes.Stations.Contains(bench.Kind)) StationDone(bench, bill, j, note);
            bench.Rev = Touch();
            return;
        }
    }

    private void BillFailed(PawnState p, ActiveJob j, string why)
    {
        foreach (var bench in Benches)
        {
            if (j.Source == "furnace" && bench.LoadedBill == j.SourceId && j.At == bench.Pos)
            {
                bench.ReadyAt = Now + TimeSpan.FromSeconds(20); // not done yet: look again later
                bench.Rev = Touch();
                return;
            }
            if (bench.Bills.FirstOrDefault(b => b.Id == j.SourceId) is not { } bill) continue;
            if (StoryRecipes.Stations.Contains(bench.Kind) && StationFailed(p, bench, bill, why))
            {
                bench.Rev = Touch();
                return;
            }
            // Short of fuel at the furnace: what the colony thought it still held isn't there (emptied, or another fuel taken out).
            if (bench.Kind == "furnace" && why.Contains("fuel", StringComparison.OrdinalIgnoreCase)) bench.FuelCredit = 0;
            if ((_billFails[bill.Id] = _billFails.GetValueOrDefault(bill.Id) + 1) >= 3)
            {
                bill.SuspendedReason = why;
                Alert("warn", $"Bill {bill.Recipe} suspended: {why}", p.Name);
            }
            bench.Rev = Touch();
            return;
        }
    }

    /// <summary>Suspended bills whose ingredients may be back are retried (every storage change wakes them).</summary>
    internal void RetrySuspended()
    {
        foreach (var bench in Benches)
            foreach (var b in bench.Bills.Where(b => b.SuspendedReason != null && !_billFails.ContainsKey(b.Id)))
            {
                b.SuspendedReason = null;
                bench.Rev = Touch();
            }
    }
}
