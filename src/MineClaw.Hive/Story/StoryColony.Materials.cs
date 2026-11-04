using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Builds craft what they need (docs/web/story-mode.md "Builds craft what they need"). The materials pass walks the queue in order:
/// each build reserves what the colony has of its items (storage plus what pawns carry, minus what jobs in flight already took), and
/// its shortfall goes through the version's recipe data (<see cref="RecipePlanner"/>: logs → planks → sticks → fence, sand → glass
/// with fuel) against what is still unreserved. What can be made becomes auto bills ("for build #N", count = exactly what is still
/// to make, ingredients before products) on a designated crafting table or furnace; 2×2 steps run in a pawn's own grid when the
/// colony has no table. A build that needs a table (or furnace) the colony hasn't got has a pawn craft one and place it near home
/// (<see cref="ColonyMeta.AutoPlaceBenches"/>), else an alert asks for one. Raw materials that are short are said once per build
/// ("needs 6 more logs"); nothing is designated by itself. Reservations are what keep two builds (or a manual bill) from taking the
/// same planks.
/// </summary>
public sealed partial class StoryColony
{
    private bool _materialsDirty = true;
    private DateTimeOffset _materialsAt = DateTimeOffset.MinValue;
    private long _materialsSeq;
    private long _materialsRev = -1;
    /// <summary>Items reserved for builds (directly, or as ingredients of their auto crafts): manual bills leave them.</summary>
    private Dictionary<(int Id, int Meta), int> _reserved = new();
    /// <summary>What is left after every build's reservation (the Architect's "craftable" counts).</summary>
    private Dictionary<(int Id, int Meta), int> _freePool = new();
    /// <summary>Bench-less crafts (no crafting table yet: 2×2 recipes in a pawn's own grid).</summary>
    private List<HandCraft> _handCrafts = new();
    /// <summary>A workbench the colony is crafting and placing for a build, or null.</summary>
    private StationWant? _station;
    private readonly Dictionary<string, string> _buildAlerted = new(StringComparer.Ordinal);

    internal sealed record HandCraft(string Build, string ForItem, StoryRecipe Recipe, int Outputs, int Step);
    internal sealed record StationWant(string Kind, string Build, P3 Spot, IReadOnlyList<MaterialNeed> Raw, P3? Table);

    /// <summary>Seconds between materials passes while nothing in storage or the queue changed (pawns' pockets still move).</summary>
    public static readonly TimeSpan MaterialsEvery = TimeSpan.FromSeconds(3);

    /// <summary>Something a build counts on changed (storage, the queue, benches, bills): the next pick plans again.</summary>
    internal void MaterialsChanged() => _materialsDirty = true;

    /// <summary>Runs the materials pass when it is due (callers hold the lock).</summary>
    internal void EnsureMaterials()
    {
        if (!_materialsDirty && (Now - _materialsAt < MaterialsEvery || _materialsRev == _rev)) return;
        RefreshMaterials();
    }

    private static bool Builds_Live(BuildState b) => b.Status is not ("paused" or "done");

    /// <summary>The colony's stock by exact item: storage, plus pawns' pockets (tools, weapons, armour excluded), minus what jobs in
    /// flight already took from a chest (it is in a pocket and still listed in the chest until the job ends), plus what a furnace is
    /// smelting.</summary>
    private Dictionary<(int Id, int Meta), int> Pool(IStoryWorld w)
    {
        var pool = new Dictionary<(int, int), int>();
        void Add(int id, int meta, int n) { if (n != 0) pool[(id, meta)] = pool.GetValueOrDefault((id, meta)) + n; }
        foreach (var s in Storages) foreach (var c in s.Contents) Add(c.Id, c.Meta, c.Count);
        var cats = StoryCategories.For(w.Data);
        foreach (var p in Pawns.Values)
        {
            var live = Host.Live(p.Name);
            if (!live.Online) continue;
            foreach (var st in live.Inventory)
                if (cats.LeafOf(st.Id, st.Meta) is not ("tools" or "weapons" or "armour")) Add(st.Id, st.Meta, st.Count);
            // A job's takes are still listed in the chest until it ends, and once fetched they are in the pocket too: that copy never
            // counts twice. A craft (bill, hand craft, bench, eating) consumes them, so they don't count at all; a builder's blocks
            // or a haul only move them.
            if (_current.TryGetValue(p.Name, out var cur))
                foreach (var (_, t) in cur.Takes)
                {
                    bool consumed = cur.Source is "bill" or "hand" or "station" or "need";
                    int pocket = live.Inventory.Where(s => s.Id == t.Id && s.Meta == t.Meta).Sum(s => s.Count);
                    Add(t.Id, t.Meta, -((consumed ? t.Count : 0) + Math.Min(t.Count, pocket)));
                }
        }
        foreach (var bench in Benches)
            if (bench.LoadedBill is { } lb && bench.Bills.FirstOrDefault(x => x.Id == lb) is { } bill && StoryRecipes.Get(w.Data, bill.Recipe) is { } r)
                Add(r.Output.Id, r.Output.Meta, bench.LoadedCount);
        foreach (var k in pool.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList()) pool.Remove(k);
        return pool;
    }

    private static IEnumerable<ItemStack> Stacks(Dictionary<(int Id, int Meta), int> pool)
    {
        foreach (var ((id, meta), n) in pool)
            for (int left = n; left > 0; left -= 64)
                yield return new ItemStack((short)id, (byte)Math.Min(64, left), (short)meta);
    }

    private static int Take(Dictionary<(int Id, int Meta), int> pool, (int Id, int Meta) key, int n)
    {
        int have = pool.GetValueOrDefault(key), t = Math.Min(have, n);
        if (t <= 0) return 0;
        if (have - t == 0) pool.Remove(key); else pool[key] = have - t;
        return t;
    }

    /// <summary>Takes n of any meta of an id (fuel); false (and nothing taken) when there aren't n.</summary>
    private static bool TakeAny(Dictionary<(int Id, int Meta), int> pool, int id, int n)
    {
        if (pool.Where(kv => kv.Key.Id == id).Sum(kv => kv.Value) < n) return false;
        foreach (var k in pool.Keys.Where(k => k.Id == id).ToList())
        {
            if (n <= 0) break;
            n -= Take(pool, k, n);
        }
        return true;
    }

    private static void Reserve(Dictionary<(int Id, int Meta), int> into, (int Id, int Meta) key, int n)
    {
        if (n > 0) into[key] = into.GetValueOrDefault(key) + n;
    }

    private static CraftPlan PlanFor((int Id, int Meta) key, int n, Dictionary<(int Id, int Meta), int> pool) =>
        RecipePlanner.Plan(key.Id, n, Stacks(pool), key.Meta, new PlannerOptions { AllowSmelting = true, HasCraftingTable = true });

    /// <summary>The fuels the materials pass plans with: the colony's furnace's choice (StoryColony.Fuel.cs), set by each pass on its
    /// thread (the pass runs under the colony's lock); null = auto (coal, planks, logs).</summary>
    [ThreadStatic] private static IReadOnlyList<(int Id, int Meta, double Per)>? _plannerFuels;

    /// <summary>Fuel for <paramref name="smelts"/> smelts out of the pool by preference (the furnace's choice; auto = coal, planks,
    /// logs); the fuel taken, or null.</summary>
    private static (int Id, int Count)? TakeFuel(Dictionary<(int Id, int Meta), int> pool, int smelts)
    {
        if (smelts <= 0) return (0, 0);
        foreach (var (fid, fmeta, per) in _plannerFuels ?? StoryRecipes.Fuels.Select(f => (f.Id, -1, f.PerItem)).ToList())
        {
            int need = Math.Max(1, (int)Math.Ceiling(smelts * per - 1e-9));
            if (fmeta < 0 ? TakeAny(pool, fid, need) : pool.GetValueOrDefault((fid, fmeta)) >= need && Take(pool, (fid, fmeta), need) == need) return (fid, need);
        }
        return null;
    }

    /// <summary>A plan for n that the pool covers completely, fuel included, and the pool after it; null when it can't.</summary>
    private static (CraftPlan Plan, Dictionary<(int Id, int Meta), int> After, (int Id, int Count) Fuel)? Feasible((int Id, int Meta) key, int n,
        Dictionary<(int Id, int Meta), int> pool)
    {
        var plan = PlanFor(key, n, pool);
        if (!plan.Complete || plan.Steps.Count == 0) return null;
        var after = new Dictionary<(int, int), int>(pool);
        foreach (var u in plan.Used) Take(after, (u.Id, u.Damage), u.Count);
        if (TakeFuel(after, plan.ItemsToSmelt) is not { } fuel) return null;
        return (plan, after, fuel);
    }

    /// <summary>The most of an item (up to <paramref name="max"/>) the pool can make, with that plan.</summary>
    private static (int N, CraftPlan? Plan, Dictionary<(int Id, int Meta), int>? After, (int Id, int Count) Fuel) MostCraftable((int Id, int Meta) key, int max,
        Dictionary<(int Id, int Meta), int> pool)
    {
        if (max <= 0) return (0, null, null, (0, 0));
        if (Feasible(key, max, pool) is { } all) return (max, all.Plan, all.After, all.Fuel);
        int lo = 0, hi = max - 1;
        (CraftPlan, Dictionary<(int, int), int>, (int, int))? best = null;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Feasible(key, mid, pool) is { } f) { lo = mid; best = (f.Plan, f.After, f.Fuel); }
            else hi = mid - 1;
        }
        if (lo > 0 && best == null && Feasible(key, lo, pool) is { } g) best = (g.Plan, g.After, g.Fuel);
        return lo > 0 && best is { } b ? (lo, b.Item1, b.Item2, b.Item3) : (0, null, null, (0, 0));
    }

    /// <summary>The bench-kind recipe a planner step is (the bill's recipe), or null.</summary>
    /// <param name="intermediate">The step makes an ingredient of a later step (planks for sticks), not the item wanted itself: a family
    /// recipe that takes any variant ("Planks (from any logs)") is used, so the chain never stalls on one wood (StoryRecipeFamilies).</param>
    internal static StoryRecipe? RecipeOf(IGameData d, CraftStep s, bool intermediate = false)
    {
        if (s.Smelt is { } sm)
            return StoryRecipes.For(d, "furnace").FirstOrDefault(r => r.Inputs.Count > 0 && r.Inputs[0].Options.Any(o => o.Id == sm.InputId
                && (o.Meta < 0 || sm.InputDamage < 0 || o.Meta == sm.InputDamage)));
        var rc = s.Recipe!;
        var table = StoryRecipes.For(d, "crafting_table");
        if (intermediate && table.FirstOrDefault(r => r.AnyOutput && r.Output.Id == rc.OutputId) is { } any) return any;
        return table.FirstOrDefault(r => !r.AnyOutput && r.Output.Id == rc.OutputId && r.Output.Meta == Math.Max(0, rc.OutputDamage));
    }

    private (int Id, int Meta) KeyOfItem(IStoryWorld w, string item, int fallbackId) =>
        w.Data.ItemByNameWithDamage(item) is { } hit ? (hit.Item.Id, Math.Max(0, hit.Damage)) : (fallbackId, 0);

    private string Display(IStoryWorld w, int id, int meta) => w.Data.DisplayName(id, meta).ToLowerInvariant();

    /// <summary>The designated bench of a kind nearest home (or the build), or null.</summary>
    private WorkbenchState? BenchFor(string kind, P3 near) =>
        Benches.Where(b => b.Kind == kind).OrderBy(b => b.Pos.Dist(near)).FirstOrDefault();

    private sealed record Want(string Build, int Order, string ForItem, StoryRecipe Recipe, int Outputs, int Step, string Bench);

    /// <summary>The materials pass (see the type's summary). Callers hold the lock.</summary>
    internal void RefreshMaterials()
    {
        _materialsDirty = false;
        _materialsAt = Now;
        _materialsSeq++;
        _materialsRev = _rev;
        if (World is not { } w) return;
        using var scope = ReferenceEquals(GameData.Current, w.Data) ? null : GameData.Override(w.Data);
        var pool = Pool(w);
        var reserved = new Dictionary<(int, int), int>();
        var wants = new List<Want>();
        var hand = new List<HandCraft>();
        StationWant? station = null;
        var near = Home() ?? Builds.SelectMany(b => b.Ghosts).Select(g => g.At).FirstOrDefault();
        _plannerFuels = BenchFor("furnace", near) is { } fb
            ? StoryFuels.For(w.Data, FuelChoice(fb, null)).Select(f => (f.Item.Id, f.Item.Meta, 1 / f.ItemsPerUnit)).ToList()
            : null;
        bool hasTable = Benches.Any(b => b.Kind == "crafting_table"), hasFurnace = Benches.Any(b => b.Kind == "furnace");
        List<MaterialNeed> Used(IReadOnlyList<MaterialNeed> used, Dictionary<(int Id, int Meta), int> after) { pool = after; return used.ToList(); }
        pool = ToolCrafts(w, pool, reserved, wants, hasTable); // tools a waiting job needs, spare tools: auto bills "for tools" (StoryColony.Gear.cs)
        foreach (var b in Builds.OrderBy(b => b.Order))
        {
            if (!Builds_Live(b)) { if (b.Materials.Count > 0) { b.Materials = new(); b.Rev = Bump(); } continue; }
            var mats = new List<BuildMaterial>();
            var notes = new List<string>();
            int step = 0;
            var groups = b.Ghosts.Where(g => g.Job && !g.Remove && g.State is "todo" or "leased" or "blocked")
                .GroupBy(g => g.Item ?? (w.Data.ItemExists(g.Id) ? w.Data.Item(g.Id).Name : g.Id.ToString()));
            foreach (var grp in groups)
            {
                var item = grp.Key;
                var key = KeyOfItem(w, item, grp.First().Id);
                int need = grp.Count();
                int have = Take(pool, key, need);
                Reserve(reserved, key, have);
                int shortN = need - have, crafting = 0;
                string via = "none";
                var chain = new List<string>();
                var raw = new List<string>();
                if (shortN > 0 && !b.NoCraft.Contains(item))
                {
                    var full = PlanFor(key, shortN, pool);
                    if (full.Steps.Count > 0)
                    {
                        via = full.Steps.Any(s => s.IsSmelt) ? "smelt" : "craft";
                        chain.AddRange(full.Steps.Select(s => s.ToString()));
                        // The bench the chain needs comes first (a furnace is made at a table, so no table → the table first).
                        bool needsTable = full.Steps.Any(s => s.RequiresCraftingTable), needsFurnace = full.Steps.Any(s => s.IsSmelt);
                        string? bench = !hasTable && (needsTable || needsFurnace && !hasFurnace) ? "crafting_table" : needsFurnace && !hasFurnace ? "furnace" : null;
                        if (bench != null)
                        {
                            var benchKey = bench == "furnace" ? (61, 0) : (58, 0);
                            if (Meta.AutoPlaceBenches && station == null && StationSpot(w) is { } spot
                                && (Take(pool, benchKey, 1) == 1 ? new List<MaterialNeed> { new(benchKey.Item1, 0, 1) } : Feasible(benchKey, 1, pool) is { } sf
                                    ? Used(sf.Plan.Used, sf.After) : null) is { } stationRaw)
                            {
                                foreach (var u in stationRaw) Reserve(reserved, (u.Id, u.Damage), u.Count);
                                station = new StationWant(bench, b.Id, spot, stationRaw, bench == "furnace" ? BenchFor("crafting_table", spot)?.Pos : null);
                            }
                            else if (station?.Kind != bench) notes.Add(bench);
                        }
                        var (can, plan, after, fuel) = MostCraftable(key, shortN, pool);
                        if (can > 0 && plan != null && after != null)
                        {
                            foreach (var u in plan.Used) Reserve(reserved, (u.Id, u.Damage), u.Count);
                            if (fuel.Count > 0) Reserve(reserved, (fuel.Id, 0), fuel.Count);
                            pool = after;
                            crafting = can;
                            foreach (var s in plan.Steps)
                            {
                                if (RecipeOf(w.Data, s, intermediate: (s.Recipe?.OutputId ?? -1) != key.Id) is not { } r) continue;
                                int outputs = s.OutputCount;
                                // A step whose bench the colony hasn't got yet waits (the bench is on its way, or asked for).
                                if (s.IsSmelt) { if (hasFurnace) wants.Add(new Want(b.Id, b.Order, item, r, outputs, step++, "furnace")); }
                                else if (hasTable) wants.Add(new Want(b.Id, b.Order, item, r, outputs, step++, "crafting_table"));
                                else if (!s.RequiresCraftingTable) hand.Add(new HandCraft(b.Id, item, r, outputs, step++));
                            }
                        }
                        int left = shortN - can;
                        if (left > 0)
                        {
                            var rest = PlanFor(key, left, pool);
                            raw.AddRange(rest.Missing.Select(m => m.ToString()));
                            if (rest.Complete && rest.ItemsToSmelt > 0)
                                raw.Add($"fuel for {rest.ItemsToSmelt} smelt{(rest.ItemsToSmelt == 1 ? "" : "s")} (coal, planks or logs)");
                        }
                    }
                    else raw.Add($"{shortN} {Display(w, key.Id, key.Meta)}");
                }
                else if (shortN > 0) raw.Add($"{shortN} {Display(w, key.Id, key.Meta)}");
                mats.Add(new BuildMaterial(item, Display(w, key.Id, key.Meta), need, have, crafting, shortN - crafting, via, chain, raw));
            }
            // A bench this build's chain needs and the colony hasn't got: the colony makes one (one at a time), or the owner is asked.
            string? alert = null;
            foreach (var kind in notes.Distinct())
                alert ??= Meta.AutoPlaceBenches
                    ? $"Build {b.Id} needs a {(kind == "furnace" ? "furnace" : "crafting table")} and the colony can't make one yet: place or designate one"
                    : $"Build {b.Id} needs a {(kind == "furnace" ? "furnace" : "crafting table")}: place one or designate an existing one (or allow auto-placing benches)";
            var shortRaw = mats.Where(m => m.Short > 0 && m.Raw.Count > 0).ToList();
            if (alert == null && shortRaw.Count > 0)
                alert = $"Build {b.Id} ({b.Kind}) is short of materials: " + string.Join("; ",
                    shortRaw.Select(m => $"need {m.Short} {m.Label} (" + (m.Via == "none" ? (b.NoCraft.Contains(m.Item) ? "none in storage; its crafting was cancelled" : "none in storage, and no recipe makes it") :$"short of raw materials: {string.Join(", ", m.Raw)}") + ")"));
            if (alert == null) _buildAlerted.Remove(b.Id);
            else if (_buildAlerted.GetValueOrDefault(b.Id) != alert) { _buildAlerted[b.Id] = alert; Alert("warn", alert); }
            if (!mats.Select(m => m.Key).SequenceEqual(b.Materials.Select(m => m.Key))) { b.Materials = mats; b.Rev = Bump(); }
        }
        _reserved = reserved;
        _freePool = pool;
        _handCrafts = hand;
        if (station?.Kind != _station?.Kind || station?.Spot != _station?.Spot || station?.Build != _station?.Build)
            foreach (var b in Builds.Where(b => b.Id == station?.Build || b.Id == _station?.Build)) b.Rev = Bump();
        _station = station;
        ReconcileAutoBills(wants, near);
    }

    /// <summary>The auto bills on the benches match <paramref name="wants"/>: targets follow the shortfall, unwanted ones go (never a
    /// furnace's loaded one), new ones are added; each bench lists its auto bills first, by build order then step.</summary>
    private void ReconcileAutoBills(List<Want> wants, P3 near)
    {
        var merged = wants.GroupBy(x => (x.Build, x.Recipe.Id))
            .Select(g => g.First() with { Outputs = g.Sum(x => x.Outputs), Step = g.Min(x => x.Step) }).ToList();
        var placed = new HashSet<(string, string)>();
        foreach (var bench in Benches)
        {
            bool changed = false;
            foreach (var bill in bench.Bills.Where(x => x.ForBuild != null).ToList())
            {
                var want = merged.FirstOrDefault(x => x.Build == bill.ForBuild && x.Recipe.Id.Equals(bill.Recipe, StringComparison.OrdinalIgnoreCase) && x.Bench == bench.Kind);
                if (want != null && !placed.Contains((want.Build, want.Recipe.Id)))
                {
                    placed.Add((want.Build, want.Recipe.Id));
                    int target = bill.Done + want.Outputs;
                    if (bill.Target != target || bill.Step != want.Step) { bill.Target = target; bill.Step = want.Step; changed = true; }
                    continue;
                }
                if (bench.LoadedBill == bill.Id) continue; // smelting: collected first
                bench.Bills.Remove(bill);
                changed = true;
            }
            if (changed) bench.Rev = Touch();
        }
        foreach (var want in merged.Where(x => !placed.Contains((x.Build, x.Recipe.Id))))
        {
            if (BenchFor(want.Bench, near) is not { } bench) continue;
            bench.Bills.Add(new BillState
            {
                Id = NextId("bill"), Recipe = want.Recipe.Id, Mode = "count", Target = want.Outputs, ForBuild = want.Build, ForItem = want.ForItem,
                Step = want.Step, Radius = 256,
            });
            bench.Rev = Touch();
        }
        var order = Builds.ToDictionary(b => b.Id, b => b.Order);
        order[ToolsTag] = int.MinValue; // a missing tool holds work up: its bills first
        foreach (var bench in Benches.Where(b => b.Bills.Any(x => x.ForBuild != null)))
        {
            var sorted = bench.Bills.Where(x => x.ForBuild != null).OrderBy(x => order.GetValueOrDefault(x.ForBuild!, int.MaxValue)).ThenBy(x => x.Step)
                .Concat(bench.Bills.Where(x => x.ForBuild == null)).ToList();
            if (!sorted.SequenceEqual(bench.Bills)) { bench.Bills = sorted; bench.Rev = Touch(); }
        }
    }

    /// <summary>A free cell near home for a bench the colony places: air with solid ground under it and air above, not a build's cell,
    /// not the home cell, nobody standing there. Null when there's none (or no home).</summary>
    private P3? StationSpot(IStoryWorld w)
    {
        if (Home() is not { } home) return null;
        var ghosts = Builds.Where(Builds_Live).SelectMany(b => b.Ghosts).Select(g => g.At).ToHashSet();
        bool Free(int x, int y, int z) => StoryBlocks.Replaceable.Contains(w.Block(x, y, z).Id) && !StoryBlocks.Liquids.Contains(w.Block(x, y, z).Id);
        for (int r = 2; r <= 8; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
                    for (int dy = 0; dy <= 2; dy = dy <= 0 ? 1 - dy : -dy)
                    {
                        var c = home.Add(dx, dy, dz);
                        if (!w.Loaded(c.X, c.Z) || ghosts.Contains(c) || Benches.Any(b => b.Pos == c)) continue;
                        if (w.Block(c.X, c.Y, c.Z).Id != 0 || !Free(c.X, c.Y + 1, c.Z)) continue;
                        var below = w.Block(c.X, c.Y - 1, c.Z).Id;
                        if (StoryBlocks.Replaceable.Contains(below) || StoryBlocks.Liquids.Contains(below)) continue;
                        if (Occupied(c)) continue;
                        return c;
                    }
                }
        return null;
    }

    // ---------------------------------------------------------------- the jobs: a bench for a build, a craft in the hand grid

    /// <summary>Craft work for the builds that isn't a bill: placing a bench the colony needs, or a 2×2 craft in a pawn's own grid.</summary>
    internal ActiveJob? SupplyJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        EnsureMaterials();
        if (_station is { } st)
        {
            var key = "station:" + st.Kind;
            if (Open(key, p.Name, st.Spot))
            {
                var name = ItemName(st.Kind == "furnace" ? 61 : 58, 0);
                var fetch = new JsonArray();
                var takes = new List<(P3, Stack)>();
                foreach (var u in st.Raw)
                {
                    int own = live.Inventory.Where(s => s.Id == u.Id && (u.Damage < 0 || s.Meta == u.Damage)).Sum(s => s.Count);
                    if (own >= u.Count) continue;
                    if (FindStored(s => s.Id == u.Id && (u.Damage < 0 || s.Meta == u.Damage), live.Block) is not { } got) { fetch = null; break; }
                    int n = Math.Min(u.Count - own, got.Stack.Count);
                    fetch.Add(new JsonObject { ["from"] = got.Store.Chest.ToString(), ["item"] = ItemName(got.Stack.Id, got.Stack.Meta), ["n"] = n });
                    takes.Add((got.Store.Chest, got.Stack with { Count = n }));
                }
                if (fetch != null)
                {
                    var j = Job(WorkTypes.Craft, "place", $"making a {(st.Kind == "furnace" ? "furnace" : "crafting table")} for build {st.Build}", st.Spot, key,
                        "station", st.Kind, "working", f =>
                        {
                            f["block"] = name;
                            var craft = new JsonObject { ["item"] = name, ["n"] = 1 };
                            if (st.Table is { } t) craft["at"] = t.ToString();
                            f["craft"] = new JsonArray(craft);
                            if (fetch.Count > 0) f["fetch"] = fetch;
                        }, xp: 20);
                    j.Takes.AddRange(takes);
                    return j;
                }
            }
        }
        foreach (var h in _handCrafts.OrderBy(x => Builds.FirstOrDefault(b => b.Id == x.Build)?.Order ?? int.MaxValue).ThenBy(x => x.Step))
        {
            var key = $"hand:{h.Build}:{h.Recipe.Id}";
            if (!Open(key, p.Name, null)) continue;
            var r = h.Recipe;
            int crafts = Math.Clamp((int)Math.Ceiling(h.Outputs / (double)Math.Max(1, r.Output.Count)), 1, 16);
            var fetch = new JsonArray();
            var takes = new List<(P3, Stack)>();
            bool ok = true;
            foreach (var input in r.Inputs)
            {
                int need = input.Count * crafts;
                bool Ok(Stack s) => input.Options.Any(o => o.Matches(s.Id, s.Meta));
                if (live.Inventory.Where(Ok).Sum(s => s.Count) >= need) continue;
                if ((FindStored(s => Ok(s) && s.Count >= need, live.Block) ?? FindStored(Ok, live.Block)) is not { } src) { ok = false; break; }
                int n = Math.Min(need, src.Stack.Count);
                fetch.Add(new JsonObject { ["from"] = src.Store.Chest.ToString(), ["item"] = ItemName(src.Stack.Id, src.Stack.Meta), ["n"] = n });
                takes.Add((src.Store.Chest, src.Stack with { Count = n }));
            }
            if (!ok) continue; // an earlier step makes the ingredient
            int outputs = crafts * r.Output.Count;
            var j = Job(WorkTypes.Craft, "craft", $"{r.Label} for build {h.Build}", null, key, "hand", h.Build, "working", f =>
            {
                f["item"] = r.ToolItem;
                f["n"] = outputs;
                if (fetch.Count > 0) f["fetch"] = fetch;
            }, xp: 15, count: outputs);
            j.Takes.AddRange(takes);
            if (BestChest(r.Output.Id, r.Output.Meta, live.Block) is { } dst)
            {
                j.Json["stash"] = new JsonArray(new JsonObject { ["to"] = dst.Chest.ToString(), ["items"] = new JsonArray(ItemName(r.Output.Id, r.Output.Meta)) });
                j.Puts.Add((dst.Chest, [r.Output with { Count = outputs }]));
            }
            return j;
        }
        return null;
    }

    /// <summary>A station or hand craft finished: a placed bench becomes the colony's (designated); the next pass plans again.</summary>
    internal void SupplyDone(PawnState p, ActiveJob j)
    {
        MaterialsChanged();
        if (j.Source != "station" || j.At is not { } at || World is not { } w) return;
        if (StoryRecipes.BenchOf(w.Block(at.X, at.Y, at.Z).Id) is { } kind && Benches.All(b => b.Pos != at))
        {
            Benches.Add(new WorkbenchState { Pos = at, Kind = kind, Rev = Touch() });
            Alert("info", $"{p.Display} placed a {(kind == "furnace" ? "furnace" : "crafting table")} at {at} for the builds", p.Name);
        }
    }

    /// <summary>Of the colony's stock matching <paramref name="pick"/>, what isn't reserved for a build.</summary>
    internal int Unreserved(Func<int, int, bool> pick)
    {
        int stored = Storages.Sum(s => s.Contents.Where(c => pick(c.Id, c.Meta)).Sum(c => c.Count));
        int held = _reserved.Where(kv => pick(kv.Key.Id, kv.Key.Meta)).Sum(kv => kv.Value);
        return stored - held;
    }

    // ---------------------------------------------------------------- the Architect

    /// <summary>
    /// <c>GET …/architect</c>: every placeable the version has, by tab, with its tier (what it takes to make: gathered, hand = a pawn's
    /// 2×2 grid, workbench = a crafting table, furnace), whether the colony has unlocked it (a designated crafting table / furnace),
    /// and what the colony has of it, could make now from unreserved stock, or is short of.
    /// </summary>
    public JsonObject Architect()
    {
        lock (_lock)
        {
            if (World is not { } w) return new JsonObject { ["entries"] = new JsonArray(), ["benches"] = new JsonObject() };
            EnsureMaterials();
            if (_architect is { } c && c.Seq == _materialsSeq && Now - c.At < TimeSpan.FromSeconds(5)) return (JsonObject)c.Json.DeepClone();
            var json = ArchitectJson(w);
            _architect = (Now, _materialsSeq, json);
            return (JsonObject)json.DeepClone();
        }
    }

    private (DateTimeOffset At, long Seq, JsonObject Json)? _architect;

    private JsonObject ArchitectJson(IStoryWorld w)
    {
        {
            using var scope = ReferenceEquals(GameData.Current, w.Data) ? null : GameData.Override(w.Data);
            bool hasTable = Benches.Any(b => b.Kind == "crafting_table"), hasFurnace = Benches.Any(b => b.Kind == "furnace");
            var entries = new JsonArray();
            foreach (var e in StoryArchitect.Entries(w.Data))
            {
                var key = (e.ItemId, e.ItemMeta);
                var plan = PlanFor(key, 1, new Dictionary<(int, int), int>());
                string tier = plan.Steps.Count == 0 ? "gathered" : plan.Steps.Any(s => s.IsSmelt) ? "furnace" : plan.Steps.Any(s => s.RequiresCraftingTable) ? "workbench" : "hand";
                bool needsTable = plan.Steps.Any(s => s.RequiresCraftingTable);
                string? reason = null;
                if (needsTable && !hasTable) reason = "Needs a crafting table — place one or designate an existing one";
                else if (tier == "furnace" && !hasFurnace) reason = "Needs a furnace — place one or designate an existing one";
                int have = _freePool.GetValueOrDefault(key);
                int can = have > 0 || plan.Steps.Count == 0 ? 0 : MostCraftable(key, 64, _freePool).N;
                var withStock = PlanFor(key, 1, _freePool);
                var shortList = have > 0 || can > 0 ? new List<string>() : withStock.Missing.Select(m => m.ToString()).ToList();
                entries.Add(new JsonObject
                {
                    ["id"] = e.Id, ["label"] = e.Label, ["tab"] = e.Tab, ["tier"] = tier, ["unlocked"] = reason == null, ["reason"] = reason,
                    ["item"] = new JsonArray(e.ItemId, e.ItemMeta), ["material"] = new JsonArray(e.BlockId, e.BlockMeta), ["kind"] = e.Kinds[0],
                    ["kinds"] = new JsonArray(e.Kinds.Select(k => (JsonNode)k!).ToArray()),
                    ["chain"] = new JsonArray(plan.Steps.Select(s => (JsonNode)s.ToString()!).ToArray()),
                    ["have"] = have, ["craftable"] = can,
                    ["from"] = new JsonArray(withStock.Used.Select(u => (JsonNode)u.ToString()!).ToArray()),
                    ["short"] = new JsonArray(shortList.Select(s => (JsonNode)s!).ToArray()),
                });
            }
            return new JsonObject
            {
                ["entries"] = entries,
                ["benches"] = new JsonObject(StoryRecipes.Benches.Where(k => StoryRecipes.SupportedIn(w.Data, k))
                    .Select(k => new KeyValuePair<string, JsonNode?>(k, Benches.Count(b => b.Kind == k)))),
                ["tabs"] = new JsonArray(StoryArchitect.Tabs.Select(t => (JsonNode)t!).ToArray()),
            };
        }
    }

    /// <summary>The colony options (<c>POST …/options</c>): auto-placing benches, detecting benches near home.</summary>
    public JsonObject SetOptions(bool? autoPlaceBenches, bool? detectBenches)
    {
        lock (_lock)
        {
            if (autoPlaceBenches is { } a) Meta.AutoPlaceBenches = a;
            if (detectBenches is { } d) Meta.DetectBenches = d;
            Touch();
            MaterialsChanged();
            if (detectBenches == true) ScanBenches();
        }
        Save(force: true);
        lock (_lock) return OptionsJson();
    }

    internal JsonObject OptionsJson()
    {
        var o = new JsonObject { ["auto_place_benches"] = Meta.AutoPlaceBenches, ["detect_benches"] = Meta.DetectBenches, ["idle"] = Meta.Idle };
        HaulOptionsJson(o); // rescue teleport, unfiltered storage, keeping disallowed items (StoryColony.Hauling.cs)
        SpeedOptionsJson(o); // speed, speed_effect (StoryColony.Speed.cs)
        foreach (var (k, v) in GearOptionsJson().ToList()) o[k] = v?.DeepClone(); // auto_craft_tools, spare_tools (StoryColony.Gear.cs)
        return o;
    }
}
