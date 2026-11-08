using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Who reserved what (docs/web/story-mode.md "Reservations"): the materials pass records each build's claim on the stock, either its own
/// item (planks it will place) or an input of the chain that crafts its item (the iron ore a bucket's ingots are smelted from, with the
/// chain's step outputs). An owner's bill that makes one of a chain's steps may use that chain's reserved input: it does what the chain
/// needs (its ingots then count for the build, whose own auto bill shrinks). A bill waits only when every usable input is reserved, a
/// partial batch runs on what is free, and the reason names the builds and the amounts. The owner can release a claim.
/// </summary>
public sealed partial class StoryColony
{
    /// <param name="Key">The item reserved.</param>
    /// <param name="Build">The build (or the tools tag) that holds it.</param>
    /// <param name="ForItem">The build's item it is for ("bucket").</param>
    /// <param name="Chain">The item ids the chain's steps make (null: the build's own item, not a chain input).</param>
    internal sealed record StockClaim((int Id, int Meta) Key, int Count, string Build, string ForItem, IReadOnlySet<int>? Chain);

    private List<StockClaim> _claims = new();

    /// <summary>What is free of <paramref name="pick"/> for a bill making <paramref name="output"/>: the unreserved stock plus what is
    /// reserved only as an input of a chain that makes the same item (that bill does the chain's step).</summary>
    internal int UnreservedFor(Func<int, int, bool> pick, int output)
    {
        int free = Unreserved(pick);
        int chain = _claims.Where(c => c.Chain != null && c.Chain.Contains(output) && pick(c.Key.Id, c.Key.Meta)).Sum(c => c.Count);
        return free + chain;
    }

    /// <summary>An owner's live bill (not paused or waiting, still wanted) makes <paramref name="output"/>: a build's crafting chain
    /// leaves that step to it. Furnace chains instead account for committed loads and use every available station.</summary>
    private bool OwnerMakes(IStoryWorld w, int output) =>
        Benches.Any(b => BenchAvailable(b) && b.Bills.Any(x => x.ForBuild == null && !x.Paused && x.SuspendedReason == null
            && StoryRecipes.Get(w.Data, x.Recipe) is { } r && r.Output.Id == output && Wanted(x, r)));

    /// <summary>"3 iron ore reserved for build b12 (bucket); 2 for build b14 (iron ore) — 0 free".</summary>
    private string ReservedText(RecipeInput input, StoryRecipe r, int free)
    {
        var d = World!.Data;
        bool M(int id, int meta) => input.Options.Any(o => o.Matches(id, meta));
        var name = d.DisplayName(input.Options[0].Id, Math.Max(0, input.Options[0].Meta)).ToLowerInvariant();
        var parts = _claims.Where(c => M(c.Key.Id, c.Key.Meta) && !(c.Chain?.Contains(r.Output.Id) ?? false))
            .GroupBy(c => (c.Build, c.ForItem))
            .Select(g => $"{g.Sum(c => c.Count)} {name} reserved for {(g.Key.Build == ToolsTag ? "the colony's tools" : "build " + g.Key.Build)} ({g.Key.ForItem.Replace('_', ' ')})")
            .ToList();
        if (parts.Count == 0) parts.Add($"the {name} in storage is reserved (a bench or tools the colony is making)");
        return string.Join("; ", parts) + $" — {Math.Max(0, free)} free";
    }

    /// <summary>The claims in the state: <c>[{item, count, build, for_item, chain}]</c>, largest first.</summary>
    private JsonArray ClaimsJson() => new(_claims.GroupBy(c => (c.Key, c.Build, c.ForItem, Chain: c.Chain != null))
        .Select(g => (JsonNode)new JsonObject
        {
            ["item"] = new JsonArray(g.Key.Key.Id, g.Key.Key.Meta), ["count"] = g.Sum(c => c.Count), ["build"] = g.Key.Build,
            ["for_item"] = g.Key.ForItem, ["chain"] = g.Key.Chain,
        }).ToArray());

    /// <summary>
    /// <c>POST …/bills {op:"release_claim", build, item}</c>: the owner frees what a build holds for one of its items. A chain claim stops
    /// the build crafting that item (it waits for it, as cancelling its auto bill does); the build's own item stays reserved only while
    /// the build is live, so that one pauses the build.
    /// </summary>
    private (JsonNode? Result, string? Error) ReleaseClaim(string? build, string? forItem)
    {
        if (build == null || forItem == null) return (null, "build and item required");
        if (Builds.FirstOrDefault(b => b.Id == build) is not { } b) return (null, $"no build {build}");
        var mine = _claims.Where(c => c.Build == build && c.ForItem == forItem).ToList();
        if (mine.Count == 0) return (null, $"build {build} holds nothing for {forItem}");
        string done;
        if (mine.Any(c => c.Chain != null))
        {
            if (!b.NoCraft.Contains(forItem)) b.NoCraft.Add(forItem);
            foreach (var bench in Benches)
                if (bench.Tasks.RemoveAll(x => x.ForBuild == build && x.ForItem == forItem && bench.LoadedBill != x.Id) > 0) bench.Rev = Touch();
            done = "crafting cancelled";
        }
        else
        {
            b.Status = "paused";
            done = "build paused";
        }
        b.Rev = Touch();
        MaterialsChanged();
        RefreshMaterials();
        RetrySuspended();
        WakeIdle();
        return (new JsonObject { ["build"] = build, ["item"] = forItem, ["done"] = done }, null);
    }
}
