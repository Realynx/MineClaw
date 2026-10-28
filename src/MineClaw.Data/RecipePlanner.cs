namespace MineClaw.Data;

/// <summary>An amount of an item; Damage -1 = any variant.</summary>
public readonly record struct MaterialNeed(int Id, int Damage, int Count)
{
    public override string ToString() =>
        $"{Count} {(Damage < 0 ? Items.Get(Id).DisplayName : Items.DisplayName(Id, Damage))}";
}

/// <summary>One step of a plan: either craft <see cref="Recipe"/> <see cref="Times"/> times, or smelt <see cref="Times"/> items.</summary>
public sealed record CraftStep(Recipe? Recipe, SmeltingRecipe? Smelt, int Times)
{
    public bool IsSmelt => Smelt is not null;
    public int OutputId => Recipe?.OutputId ?? Smelt!.OutputId;
    public int OutputDamage => Recipe?.OutputDamage ?? Smelt!.OutputDamage;
    public int OutputCount => Times * (Recipe?.OutputCount ?? Smelt!.OutputCount);
    public bool RequiresCraftingTable => Recipe?.RequiresCraftingTable ?? false;
    public bool RequiresFurnace => IsSmelt;

    public override string ToString() => IsSmelt
        ? $"smelt {Times} {Items.Get(Smelt!.InputId).DisplayName} -> {OutputCount} {Items.DisplayName(OutputId, OutputDamage)}"
        : $"craft {OutputCount} {Items.DisplayName(OutputId, OutputDamage)} ({Times}x{(RequiresCraftingTable ? ", table" : "")})";
}

/// <summary>Result of <see cref="RecipePlanner.Plan"/>.</summary>
public sealed record CraftPlan(
    int TargetId, int TargetDamage, int Count,
    IReadOnlyList<CraftStep> Steps,
    IReadOnlyList<MaterialNeed> Missing,
    bool NeedsCraftingTable,
    bool NeedsFurnace,
    int ItemsToSmelt)
{
    /// <summary>True when everything can be made from the given inventory.</summary>
    public bool Complete => Missing.Count == 0;

    /// <summary>What the plan takes out of the given inventory (exact id:damage keys; intermediates it crafts don't count):
    /// the amounts a caller withdraws from storage when the inventory it planned on included chest contents.</summary>
    public IReadOnlyList<MaterialNeed> Used { get; init; } = [];

    public override string ToString()
    {
        var steps = Steps.Count == 0 ? "already have it" : string.Join("; ", Steps);
        var miss = Missing.Count == 0 ? "" : $" | missing: {string.Join(", ", Missing)}";
        var smelt = ItemsToSmelt > 0 ? $" | fuel for {ItemsToSmelt} smelts (coal {Smelting.FuelNeeded(ItemsToSmelt, Items.Coal)} or planks {Smelting.FuelNeeded(ItemsToSmelt, Blocks.Planks)})" : "";
        return steps + miss + smelt;
    }
}

/// <summary>Options for <see cref="RecipePlanner"/>.</summary>
public sealed record PlannerOptions
{
    /// <summary>Allow furnace steps (cobblestone → stone, sand → glass, log → charcoal, ores → ingots...).</summary>
    public bool AllowSmelting { get; init; } = true;
    /// <summary>A crafting table is placed/reachable. When false and a 3x3 recipe is needed, the plan includes crafting one.</summary>
    public bool HasCraftingTable { get; init; } = true;
    /// <summary>Maximum recursion depth.</summary>
    public int MaxDepth { get; init; } = 12;

    public static readonly PlannerOptions Default = new();
}

/// <summary>
/// Resolves a crafting tree against an inventory: e.g. wooden pickaxe ← (planks ← log) + (stick ← planks ← log).
/// Returns ordered steps (ingredients before products) plus the raw materials still missing; when something is missing the
/// steps still describe the full tree, assuming the missing materials get gathered first.
/// Simulates the inventory, so leftovers from one step (e.g. 4 planks per log) are reused by later steps.
/// </summary>
public static class RecipePlanner
{
    public static CraftPlan Plan(int itemId, int count, IEnumerable<ItemStack> inventory, int damage = -1, PlannerOptions? options = null)
    {
        options ??= PlannerOptions.Default;
        var inv = new SimInventory(inventory);
        var ctx = new Ctx(options);
        var target = new Ingredient(itemId, damage);

        var trial = ctx.Clone();
        var trialInv = inv.Clone();
        Resolve(trial, trialInv, target, count, 0, []);

        if (!options.HasCraftingTable && trial.Steps.Any(s => s.RequiresCraftingTable)
            && itemId != Blocks.CraftingTable && inv.Count(new Ingredient(Blocks.CraftingTable)) == 0)
        {
            // Make the table first (2x2 recipe; it gets placed so it leaves the inventory), then the target.
            Resolve(ctx, inv, new Ingredient(Blocks.CraftingTable, -1), 1, 0, []);
            Resolve(ctx, inv, target, count, 0, []);
            return ctx.ToPlan(itemId, damage, count, needsTable: true) with { Used = inv.Used() };
        }

        return trial.ToPlan(itemId, damage, count, needsTable: trial.Steps.Any(s => s.RequiresCraftingTable)) with { Used = trialInv.Used() };
    }

    /// <summary>Convenience overload taking a name ("wooden pickaxe", "torch", "charcoal"...). Returns null if unknown.</summary>
    public static CraftPlan? Plan(string itemName, int count, IEnumerable<ItemStack> inventory, PlannerOptions? options = null)
    {
        if (Items.ByNameWithDamage(itemName) is not { } r) return null;
        return Plan(r.Item.Id, count, inventory, r.Damage, options);
    }

    // ------------------------------------------------------------------------------------------------------

    private sealed class Ctx(PlannerOptions options)
    {
        public PlannerOptions Options { get; } = options;
        public List<CraftStep> Steps { get; private init; } = [];
        public Dictionary<(int, int), int> Missing { get; private init; } = [];

        public Ctx Clone() => new(Options) { Steps = [.. Steps], Missing = new(Missing) };

        public int MissingTotal => Missing.Values.Sum();

        public void AddMissing(Ingredient ing, int n)
        {
            var k = (ing.Id, ing.Damage);
            Missing[k] = Missing.GetValueOrDefault(k) + n;
        }

        public void AddStep(CraftStep step)
        {
            if (Steps.Count > 0 && Steps[^1].Recipe == step.Recipe && Steps[^1].Smelt == step.Smelt)
                Steps[^1] = step with { Times = Steps[^1].Times + step.Times };
            else
                Steps.Add(step);
        }

        public CraftPlan ToPlan(int id, int dmg, int count, bool needsTable)
        {
            var missing = Missing.Where(kv => kv.Value > 0).Select(kv => new MaterialNeed(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToArray();
            int smelts = Steps.Where(s => s.IsSmelt).Sum(s => s.Times);
            return new CraftPlan(id, dmg, count, Steps.ToArray(), missing, needsTable, smelts > 0, smelts);
        }
    }

    /// <summary>Simulated inventory; the given stacks (<see cref="_orig"/>) are kept apart from what the plan crafted, so
    /// <see cref="Used"/> can tell what came out of the inventory. Crafted leftovers are always used first.</summary>
    private sealed class SimInventory
    {
        private readonly Dictionary<(int Id, int Dmg), int> _c = [];
        private readonly Dictionary<(int Id, int Dmg), int> _orig = [];
        private readonly Dictionary<(int Id, int Dmg), int> _start = [];
        public SimInventory() { }
        public SimInventory(IEnumerable<ItemStack> stacks)
        {
            foreach (var s in stacks)
                if (!s.IsEmpty)
                {
                    var k = (s.Id, Items.Get(s.Id).MaxDamage > 0 ? 0 : (int)s.Damage);
                    Add(k.Item1, k.Item2, s.Count);
                    _orig[k] = _orig.GetValueOrDefault(k) + s.Count;
                    _start[k] = _start.GetValueOrDefault(k) + s.Count;
                }
        }
        public SimInventory Clone()
        {
            var c = new SimInventory();
            foreach (var kv in _c) c._c[kv.Key] = kv.Value;
            foreach (var kv in _orig) c._orig[kv.Key] = kv.Value;
            foreach (var kv in _start) c._start[kv.Key] = kv.Value;
            return c;
        }
        public void Add(int id, int dmg, int n) => _c[(id, dmg)] = _c.GetValueOrDefault((id, dmg)) + n;
        public int Count(Ingredient ing) => _c.Where(kv => ing.Matches(kv.Key.Id, kv.Key.Dmg)).Sum(kv => kv.Value);

        /// <summary>Removes up to n matching items (crafted ones first, then the given stacks), returns how many were taken.</summary>
        public int Take(Ingredient ing, int n)
        {
            int taken = 0;
            var keys = _c.Keys.Where(k => ing.Matches(k.Id, k.Dmg)).OrderByDescending(k => _c[k]).ToList();
            foreach (var key in keys)                       // crafted leftovers (4 planks per log, 2 used: the other 2)
            {
                if (taken == n) break;
                int t = Math.Min(n - taken, _c[key] - _orig.GetValueOrDefault(key));
                if (t <= 0) continue;
                _c[key] -= t;
                if (_c[key] == 0) _c.Remove(key);
                taken += t;
            }
            foreach (var key in keys)
            {
                if (taken == n) break;
                if (!_c.TryGetValue(key, out var have)) continue;
                int t = Math.Min(n - taken, have);
                _c[key] -= t;
                _orig[key] = _orig.GetValueOrDefault(key) - t;
                if (_c[key] == 0) _c.Remove(key);
                taken += t;
            }
            return taken;
        }

        public MaterialNeed[] Used() => _start.Select(kv => new MaterialNeed(kv.Key.Id, kv.Key.Dmg, kv.Value - _orig.GetValueOrDefault(kv.Key)))
            .Where(m => m.Count > 0).ToArray();
    }

    private abstract record Candidate;
    private sealed record CraftCandidate(Recipe Recipe) : Candidate;
    private sealed record SmeltCandidate(SmeltingRecipe Smelt) : Candidate;

    private static void Resolve(Ctx ctx, SimInventory inv, Ingredient need, int n, int depth, HashSet<Ingredient> stack)
    {
        if (n <= 0) return;
        n -= inv.Take(need, n);
        if (n == 0) return;

        if (depth >= ctx.Options.MaxDepth || Conflicts(need, stack)) { ctx.AddMissing(need, n); return; }

        var candidates = Candidates(need, ctx.Options).Where(c => !Cyclic(c, stack, need)).ToList();
        if (candidates.Count == 0) { ctx.AddMissing(need, n); return; }

        Candidate chosen = candidates[0];
        if (candidates.All(IsUnpack))
        {
            // Only an unpack (iron ingot ← iron block, no smelting): the item itself is what's missing unless the block is there.
            var tctx = ctx.Clone();
            Apply(tctx, inv.Clone(), chosen, need, n, depth, stack);
            if (tctx.MissingTotal > ctx.MissingTotal) { ctx.AddMissing(need, n); return; }
        }
        else if (candidates.Count > 1)
        {
            int bestMissing = int.MaxValue;
            foreach (var c in candidates)
            {
                var tctx = ctx.Clone();
                var tinv = inv.Clone();
                Apply(tctx, tinv, c, need, n, depth, stack);
                int m = tctx.MissingTotal;
                // Unpacking (iron block → 9 ingots) only when the block is really there: a missing block is never the plan
                // ("need 3 more iron ingots", not "1 more block of iron").
                if (IsUnpack(c) && m > ctx.MissingTotal) m = int.MaxValue - 1;
                if (m < bestMissing) { bestMissing = m; chosen = c; }
                if (m == ctx.MissingTotal) break; // nothing new missing: good enough
            }
        }
        Apply(ctx, inv, chosen, need, n, depth, stack);
    }

    private static void Apply(Ctx ctx, SimInventory inv, Candidate c, Ingredient need, int n, int depth, HashSet<Ingredient> stack)
    {
        var inner = new HashSet<Ingredient>(stack) { need };
        switch (c)
        {
            case CraftCandidate { Recipe: var r }:
            {
                int times = (n + r.OutputCount - 1) / r.OutputCount;
                foreach (var (ing, per) in r.Totals)
                    Resolve(ctx, inv, ing, per * times, depth + 1, inner);
                ctx.AddStep(new CraftStep(r, null, times));
                inv.Add(r.OutputId, r.OutputDamage, r.OutputCount * times);
                break;
            }
            case SmeltCandidate { Smelt: var s }:
            {
                int times = (n + s.OutputCount - 1) / s.OutputCount;
                Resolve(ctx, inv, new Ingredient(s.InputId, -1), times, depth + 1, inner);
                ctx.AddStep(new CraftStep(null, s, times));
                inv.Add(s.OutputId, s.OutputDamage, s.OutputCount * times);
                break;
            }
        }
        inv.Take(need, n);
    }

    private static bool IsUnpack(Candidate c) => c is CraftCandidate { Recipe: var r } && Recipes.IsUnpack(r);

    private static IEnumerable<Candidate> Candidates(Ingredient need, PlannerOptions o)
    {
        var recipes = Recipes.ForOutput(need.Id, need.Damage);
        foreach (var r in recipes.Where(r => !Recipes.IsUnpack(r))) yield return new CraftCandidate(r);
        if (o.AllowSmelting)
            foreach (var s in Smelting.ForOutput(need.Id, need.Damage)) yield return new SmeltCandidate(s);
        foreach (var r in recipes.Where(Recipes.IsUnpack)) yield return new CraftCandidate(r);
    }

    /// <summary>Same id and overlapping metadata (wildcards overlap everything): red wool may use white wool, but not itself.</summary>
    private static bool Overlaps(Ingredient a, Ingredient b) =>
        a.Id == b.Id && (a.Damage < 0 || b.Damage < 0 || a.Damage == b.Damage);

    private static bool Conflicts(Ingredient i, HashSet<Ingredient> stack) => stack.Any(s => Overlaps(s, i));

    private static bool Cyclic(Candidate c, HashSet<Ingredient> stack, Ingredient self) => c switch
    {
        CraftCandidate { Recipe: var r } => r.Ingredients.Any(i => Overlaps(i, self) || Conflicts(i, stack)),
        SmeltCandidate { Smelt: var s } => s.InputId == self.Id || stack.Any(x => x.Id == s.InputId),
        _ => false,
    };
}
