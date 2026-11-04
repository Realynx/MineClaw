using MineClaw.Bot.Game;
using MineClaw.Data;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Live 10-27 15:42: clone 67 fell 6 blocks off a tree crown with "no placeable block in my inventory to catch myself".
/// Jobs at height (chop_tree, a build-mode goto, pillar/bridge/edge skills) first make sure a few cheap placeable blocks are
/// carried for the void/fall clutch: cobble or its substitutes, never dirt (unrenewable on Skyblock). Acquired through the
/// acquisition planner (chests, a hive handoff, crafting planks from logs, the generator); best effort, bounded, zero tokens.
/// </summary>
public static class ClutchBlocks
{
    /// <summary>Blocks that count as clutch blocks, cheapest first (never dirt/grass, sand/gravel fall).</summary>
    public static readonly int[] Ids =
        [Blocks.Cobblestone, Blocks.Netherrack, Blocks.Stone, Blocks.Planks, Blocks.Sandstone, Blocks.StoneBricks, Blocks.Log];

    public const int Want = 4;

    public static int Held(IGameClient c) =>
        Ids.Where(id => !c.ReservedItems.Contains(id)).Sum(id => c.World.Inventory.CountItem(id))
        // A normal world has dirt to spare: it catches a fall and builds a pillar as well as cobble (skyblock never spends it).
        + (MineClaw.Data.Versioning.WorldMode.Current.ScarceTerrain || c.ReservedItems.Contains(Blocks.Dirt) ? 0 : c.World.Inventory.CountItem(Blocks.Dirt));

    /// <summary>
    /// Ensures at least <paramref name="want"/> clutch blocks: had them → null (no note); otherwise tries cobble/planks from
    /// chests, the hive, crafting (logs → planks) and the generator. Returns a short note ("carrying 4 cobblestone to
    /// clutch with" / "no clutch blocks: ...") or null when nothing needed doing.
    /// </summary>
    public static async Task<string?> EnsureAsync(IGameClient c, CancellationToken ct, int want = Want)
    {
        if (Held(c) >= want) return null;
        var sources = Acquisition.Of(c);
        if (sources == null) return null; // bare test hosts: nothing to draw on
        var planner = Acquisition.Planner(c);
        int missing = want - Held(c);
        try
        {
            foreach (var (name, depth) in new[] { ("cobblestone", AcquireDepth.Cheap), ("planks", AcquireDepth.Cheap) })
            {
                var r = await planner.AcquireAsync(new Need(name, c.World.Inventory.CountItem(Items.ByName(name)!.Id) + missing, 0, depth), ct)
                    .ConfigureAwait(false);
                if (Held(c) >= want) return $"carrying {Held(c)} clutch blocks ({r.Text})";
                missing = want - Held(c);
            }
            // The generator makes them for free (no asking players: a clutch stock is never worth a question).
            if (sources.GeneratorCells?.Invoke() is { Count: > 0 })
            {
                var r = await planner.AcquireAsync(new Need("cobblestone", c.World.Inventory.CountItem(Blocks.Cobblestone) + missing, 0,
                    AcquireDepth.Full), ct).ConfigureAwait(false);
                if (Held(c) >= want) return $"carrying {Held(c)} clutch blocks ({r.Text})";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* best effort */ }
        return Held(c) > 0 ? $"only {Held(c)} clutch block(s) on me" : "no clutch blocks on me (no cobble/planks to be had): careful up high";
    }
}
