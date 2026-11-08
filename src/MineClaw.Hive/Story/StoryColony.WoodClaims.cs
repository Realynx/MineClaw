using MineClaw.Data;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private sealed record WoodClaim((int Id, int Meta) Key, int Count, string Build, string Item,
        IReadOnlySet<(int Id, int Meta)> Outputs);

    private List<WoodClaim> _woodClaims = new();
    [ThreadStatic] private static Dictionary<(int Id, int Meta), int>? _plannerFuelAllowance;

    private static bool ConstructionWood(int id) => id is 5 or 17 or 162;

    /// <summary>Exact wood construction gets its scarce variants before wildcard chest recipes or automatic fuel. This is a
    /// bounded lookahead over placed wood and the final recipe's exact wood inputs, not a second general recipe planner.</summary>
    private sealed class ConstructionWoodHolds(Dictionary<(int Id, int Meta), int> stock)
    {
        private readonly Dictionary<(int Id, int Meta), int> _shadow = new(stock);
        private readonly Dictionary<(string Build, string Item), Dictionary<(int Id, int Meta), int>> _left = new();
        public readonly List<WoodClaim> Claims = new();

        public void Add(string build, string item, (int Id, int Meta) key, int need, bool noCraft)
        {
            var held = new Dictionary<(int Id, int Meta), int>();
            var outputs = new HashSet<(int Id, int Meta)> { key };
            int have = Take(_shadow, key, need);
            if (ConstructionWood(key.Id)) Reserve(held, key, have);
            int missing = need - have;
            if (!noCraft && missing > 0)
            {
                var plan = PlanFor(key, missing, _shadow);
                foreach (var s in plan.Steps) outputs.Add((s.OutputId, Math.Max(0, s.OutputDamage)));
                if (ConstructionWood(key.Id)) Keep(plan, held);
                else if (plan.Steps.LastOrDefault(s => s.OutputId == key.Id && Math.Max(0, s.OutputDamage) == key.Meta)?.Recipe is { } final
                    && final.Totals.Any(t => ConstructionWood(t.Ingredient.Id) && t.Ingredient.Damage >= 0))
                    // Keep the complete item's wood inputs, including its sticks. Reserving only the fence's four exact oak
                    // planks would let a later floor take the other two needed for sticks and strand the earlier fence.
                    Keep(plan, held);
            }
            if (held.Count == 0) return;
            _left[(build, item)] = new(held);
            Claims.AddRange(held.Select(kv => new WoodClaim(kv.Key, kv.Value, build, item, outputs)));
        }

        private void Keep(CraftPlan plan, Dictionary<(int Id, int Meta), int> held)
        {
            foreach (var u in plan.Used.Where(u => ConstructionWood(u.Id)))
                Reserve(held, (u.Id, u.Damage), Take(_shadow, (u.Id, u.Damage), u.Count));
        }

        public Dictionary<(int Id, int Meta), int> Hide(Dictionary<(int Id, int Meta), int> pool, string build, string item)
        {
            var hidden = new Dictionary<(int Id, int Meta), int>();
            foreach (var (owner, wants) in _left)
                if (owner != (build, item))
                    foreach (var (key, n) in wants) Reserve(hidden, key, Take(pool, key, n));
            return hidden;
        }

        public void Used(string build, string item, Dictionary<(int Id, int Meta), int> before, Dictionary<(int Id, int Meta), int> after)
        {
            if (!_left.TryGetValue((build, item), out var left)) return;
            foreach (var key in left.Keys.ToList())
                left[key] = Math.Max(0, left[key] - Math.Max(0, before.GetValueOrDefault(key) - after.GetValueOrDefault(key)));
        }

        public void Finish(Dictionary<(int Id, int Meta), int> pool, Dictionary<(int Id, int Meta), int> reserved, List<StockClaim> claims)
        {
            foreach (var (owner, wants) in _left)
            {
                var outputs = Claims.First(c => c.Build == owner.Build && c.Item == owner.Item).Outputs.Select(k => k.Id).ToHashSet();
                foreach (var (key, n) in wants)
                {
                    int held = Take(pool, key, n);
                    if (held <= 0) continue;
                    Reserve(reserved, key, held);
                    claims.Add(new StockClaim(key, held, owner.Build, owner.Item, outputs));
                }
            }
        }
    }

    /// <summary>Per exact stack limits used by both ingredient gathering and auto fuel, including the pawn's pockets.
    /// Auto crafts may use their own construction hold. A manual bill may do a matching chain step; fuel never does.</summary>
    private Dictionary<(int Id, int Meta), int>? WoodBudget(BillState bill, StoryRecipe? recipe, bool fuel = false)
    {
        if (_woodClaims.Count == 0 || World is not { } world) return null;
        bool Own(WoodClaim claim) => !fuel && (bill.ForBuild is { } build
            ? claim.Build == build && claim.Item == bill.ForItem
            : recipe != null && claim.Outputs.Any(k => k.Id == recipe.Output.Id && (recipe.AnyOutput || k.Meta == recipe.Output.Meta)));
        var held = _woodClaims.Where(c => !Own(c)).GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        if (held.Count == 0) return null;
        var actual = Pool(world);
        return held.ToDictionary(kv => kv.Key, kv => Math.Max(0, actual.GetValueOrDefault(kv.Key) - kv.Value));
    }

    private static int WoodAvailable(Dictionary<(int Id, int Meta), int>? budget, int id, int meta, int count) =>
        Math.Min(count, budget?.GetValueOrDefault((id, meta), int.MaxValue) ?? int.MaxValue);

    private static void WoodTaken(Dictionary<(int Id, int Meta), int>? budget, Stack stack)
    {
        if (budget?.ContainsKey((stack.Id, stack.Meta)) == true)
            budget[(stack.Id, stack.Meta)] = Math.Max(0, budget[(stack.Id, stack.Meta)] - stack.Count);
    }

    /// <summary>The client replans wildcard recipes from the entire pocket. Do not let a generic task burn through a held
    /// variant already carried by this pawn; another pawn or the matching exact construction can do that work.</summary>
    private bool ProtectedCarriedWood(BillState bill, StoryRecipe recipe, PawnLive live)
    {
        var budget = WoodBudget(bill, recipe);
        if (budget == null) return false;
        return recipe.Inputs.Any(input => (input.Options.Any(o => o.Meta < 0) || input.Options.Count > 1)
            && live.Inventory.Where(s => input.Options.Any(o => o.Matches(s.Id, s.Meta)))
                .GroupBy(s => (s.Id, s.Meta)).Any(g => WoodAvailable(budget, g.Key.Id, g.Key.Meta, g.Sum(s => s.Count)) < g.Sum(s => s.Count)));
    }
}
