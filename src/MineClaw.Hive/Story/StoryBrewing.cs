using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Brewing stand bills (1.12.2): "Brew Potion of Swiftness" and the rest, one recipe per potion, splash and lingering potion a chain from
/// water bottles makes (<see cref="Brewing.Chain"/>: water + nether wart → awkward, + sugar → swiftness, + redstone → extended,
/// + glowstone → II, + fermented spider eye → corrupted, + gunpowder → splash, + dragon's breath → lingering). Cook work.
/// </summary>
public static class StoryBrewing
{
    public static string FormName(int form) => form switch
    {
        Brewing.SplashPotion => "splash_potion", Brewing.LingeringPotion => "lingering_potion", _ => "potion",
    };

    public static string RecipeId(int form, string type) =>
        "brew:" + (form == Brewing.SplashPotion ? "splash_" : form == Brewing.LingeringPotion ? "lingering_" : "") + type;

    public static IEnumerable<StoryRecipe> Recipes(IGameData d)
    {
        foreach (var type in Brewing.Brewable)
            foreach (var form in new[] { Brewing.Potion, Brewing.SplashPotion, Brewing.LingeringPotion })
            {
                if (type == "awkward" && form != Brewing.Potion || !d.ItemExists(form)) continue;
                if (Brewing.Chain(form, type) is not { Count: > 0 } chain) continue;
                var inputs = new List<RecipeInput>
                {
                    new([new ItemKey(Brewing.GlassBottle, 0), new ItemKey(Brewing.Potion, Brewing.TypeId("water"))], Brewing.Bottles),
                };
                foreach (var g in chain.GroupBy(s => s.With)) inputs.Add(new RecipeInput([new ItemKey(g.Key.Id, g.Key.Meta)], g.Count()));
                yield return new StoryRecipe(RecipeId(form, type), "Brew " + Brewing.Label(form, type), "brewing_stand", WorkTypes.Cook,
                    new Stack(form, Brewing.TypeId(type), Brewing.Bottles), inputs, true)
                {
                    ToolItem = type,
                    Station = new StationSpec("brew", FormName(form), type, FuelItem: "blaze_powder", Water: true,
                        Chain: chain.Select(s => $"{Brewing.Label(s.FromForm, s.FromType)} + {StoryRecipes.Label(d, s.With.Id, s.With.Meta)} → {Brewing.Label(s.Form, s.Type)}").ToList(),
                        Note: $"{chain.Count} brew{(chain.Count == 1 ? "" : "s")} of 20 s for up to 3 bottles; 1 blaze powder fuels 20 brews"),
                };
            }
    }
}

public sealed partial class StoryColony
{
    private readonly Dictionary<P3, (DateTimeOffset At, P3? Water)> _water = new();

    /// <summary>
    /// The water a brew fills its glass bottles at: the nearest filled cauldron or water source block (open above) within 16 blocks
    /// (4 up or down) of the stand, looked up at most once a minute.
    /// </summary>
    internal P3? WaterNear(P3 stand)
    {
        if (World is not { } w) return null;
        if (_water.TryGetValue(stand, out var c) && Now - c.At < TimeSpan.FromMinutes(1) && (c.Water is not { } cw || BrewWater(w, cw))) return c.Water;
        var best = NearestBrewWater(w, stand);
        _water[stand] = (Now, best);
        return best;
    }

    /// <summary>A filled cauldron, or a water source block open above.</summary>
    private static bool BrewWater(IStoryWorld w, P3 p)
    {
        var (id, meta) = w.Block(p.X, p.Y, p.Z);
        return id == Brewing.CauldronBlock && meta > 0
               || id is Brewing.WaterSource or Brewing.FlowingWater && meta == 0 && w.Block(p.X, p.Y + 1, p.Z).Id == 0;
    }

    private static P3? NearestBrewWater(IStoryWorld w, P3 stand)
    {
        P3? best = null;
        double bd = double.MaxValue;
        for (int dx = -16; dx <= 16; dx++)
            for (int dz = -16; dz <= 16; dz++)
            {
                if (!w.Loaded(stand.X + dx, stand.Z + dz)) continue;
                for (int dy = -4; dy <= 4; dy++)
                {
                    var p = stand.Add(dx, dy, dz);
                    double dist = p.Dist(stand);
                    if (dist < bd && BrewWater(w, p)) { best = p; bd = dist; }
                }
            }
        return best;
    }

    /// <summary>
    /// A brew: up to three bottles through the recipe's chain, from the furthest stage the pawn already holds (awkward potions skip the
    /// wart), else from water bottles it holds or glass bottles it fills at the stand's water. It brings each step's ingredient and blaze
    /// powder when the stand's fuel is short or unknown, waits out every brew at the stand and stores the potions.
    /// </summary>
    private (ActiveJob? Job, string? Missing) BrewJob(PawnState p, PawnLive live, string work, WorkbenchState bench, BillState bill, StoryRecipe r, string key)
    {
        var spec = r.Station!;
        int form = r.Output.Id;
        if (Brewing.Chain(form, spec.Potion!) is not { Count: > 0 } chain) return (null, "no brewing chain makes it");
        int batch = Math.Clamp(Left(bill, r, Brewing.Bottles), 1, Brewing.Bottles);
        int start = BrewStart(live, chain);
        var steps = chain.Skip(start).ToList();
        var g = new StationGathering(this, bill, live, bench.Pos);
        (batch, var fill, var missing) = start > 0
            ? (Math.Min(batch, HeldPotions(live, steps[0].FromForm, steps[0].FromType)), null, null)
            : BrewBottles(live, bench, g, batch);
        if (missing != null) return (null, missing);
        if (batch <= 0) return (null, null);
        if (BrewIngredients(g, steps, bench) is { } lacking) return (null, lacking);
        return (BrewJobOf(work, bench, bill, key, steps, batch, fill, g), null);
    }

    private static int HeldPotions(PawnLive live, int form, string type) =>
        live.Inventory.Where(s => s.Id == form && s.Meta == Brewing.TypeId(type)).Sum(s => s.Count);

    /// <summary>The furthest stage of the chain the pawn already holds the potions for (0: from water).</summary>
    private static int BrewStart(PawnLive live, IReadOnlyList<Brewing.Step> chain)
    {
        for (int i = chain.Count - 1; i > 0; i--)
            if (HeldPotions(live, chain[i].FromForm, chain[i].FromType) > 0) return i;
        return 0;
    }

    /// <summary>A brew from water: the water bottles held, topped up with glass bottles filled at the stand's water.</summary>
    private (int Batch, JsonObject? Fill, string? Missing) BrewBottles(PawnLive live, WorkbenchState bench, StationGathering g, int batch)
    {
        int water = HeldPotions(live, Brewing.Potion, "water");
        if (water >= batch) return (batch, null, null);
        if (WaterNear(bench.Pos) is not { } wat)
            return water == 0 ? (batch, null, "no water (a source block or a filled cauldron) within 16 blocks of the brewing stand") : (water, null, null);
        int bottles = g.Get(s => s.Id == Brewing.GlassBottle, batch - water, "glass bottles (or water bottles)", partial: true);
        if (g.Missing != null)
        {
            if (water == 0) return (batch, null, g.Missing);
            g.Missing = null;
            bottles = 0;
        }
        return (water + bottles, bottles > 0 ? new JsonObject { ["at"] = wat.ToString(), ["n"] = bottles } : null, null);
    }

    /// <summary>Each step's ingredient, and blaze powder when the stand's fuel is short or unknown; what is missing, else null.</summary>
    private string? BrewIngredients(StationGathering g, List<Brewing.Step> steps, WorkbenchState bench)
    {
        foreach (var ing in steps.GroupBy(s => s.With))
            g.Get(s => s.Id == ing.Key.Id && (ing.Key.Meta <= 0 || s.Meta == ing.Key.Meta), ing.Count(),
                StoryRecipes.Label(World!.Data, ing.Key.Id, ing.Key.Meta).ToLowerInvariant());
        if (g.Missing != null) return g.Missing;
        if (bench.Fuel is { } fuel && fuel >= steps.Count) return null;
        g.Get(s => s.Id == Brewing.BlazePowder, 1, "blaze powder (the brewing stand's fuel)");
        if (g.Missing == null) return null;
        if (bench.Fuel is not null) return g.Missing; // known to be short
        g.Missing = null; // unknown: the stand may have some (the pawn says so when it hasn't)
        return null;
    }

    private ActiveJob BrewJobOf(string work, WorkbenchState bench, BillState bill, string key, List<Brewing.Step> steps, int batch, JsonObject? fill, StationGathering g)
    {
        var last = steps[^1];
        var fetch = new JsonArray();
        var job = Job(work, "brew", $"Brew {Brewing.Label(last.Form, last.Type)}" + (bill.Mode == "count" ? $" ({bill.Done}/{bill.Target})" : ""), bench.Pos, key,
            "bill", bill.Id, "working", f =>
            {
                f["potion"] = last.Type;
                f["form"] = StoryBrewing.FormName(last.Form);
                f["n"] = batch;
                f["steps"] = steps.Count;
                f["fetch"] = fetch;
                if (fill != null) f["fill"] = fill;
            }, xp: 20 * steps.Count, count: batch);
        g.Attach(job, fetch);
        var dst = bill.Store is JsonObject so && P3.Parse(so["chest"]) is { } c ? StorageAt(c) : BestChest(last.Form, Brewing.TypeId(last.Type), bench.Pos);
        if (dst != null)
        {
            job.Json["stash"] = new JsonArray(new JsonObject { ["to"] = dst.Chest.ToString(), ["items"] = new JsonArray(StoryBrewing.FormName(last.Form)) });
            job.Puts.Add((dst.Chest, [new Stack(last.Form, Brewing.TypeId(last.Type), batch)]));
        }
        return job;
    }
}
