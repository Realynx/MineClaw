using MineClaw.Agent.Rag;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// Task loadouts (a pickaxe for mining, an axe for trees, food for long jobs): what the bot already carries, else withdrawn
/// from the nearest chest the WorldIndexer saw holding one (container snapshots in the shared db), else — for a pickaxe or
/// an axe — crafted when the materials are on hand. Zero tokens. Whatever is still missing is reported honestly, never
/// papered over ("no pickaxe in my inventory or storage").
/// </summary>
public static class Loadout
{
    public sealed record Kind(string Name, int[] Ids, int Want, int[] Craftable);

    /// <summary>Best first.</summary>
    public static readonly IReadOnlyDictionary<string, Kind> Kinds = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
    {
        ["pickaxe"] = new("pickaxe", [Items.DiamondPickaxe, Items.IronPickaxe, Items.StonePickaxe, Items.WoodenPickaxe], 1, [Items.StonePickaxe, Items.WoodenPickaxe]),
        ["axe"] = new("axe", [Items.DiamondAxe, Items.IronAxe, Items.StoneAxe, Items.WoodenAxe], 1, [Items.StoneAxe, Items.WoodenAxe]),
        ["shovel"] = new("shovel", [Items.DiamondShovel, Items.IronShovel, Items.StoneShovel, Items.WoodenShovel], 1, [Items.StoneShovel, Items.WoodenShovel]),
        // Farming (Fox, live 10-27: "it needs to replant, and sometimes that means hoeing the soil").
        ["hoe"] = new("hoe", [Items.DiamondHoe, Items.IronHoe, Items.StoneHoe, Items.GoldenHoe, Items.WoodenHoe], 1, [Items.StoneHoe, Items.WoodenHoe]),
        ["sword"] = new("sword", [Items.DiamondSword, Items.IronSword, Items.StoneSword, Items.WoodenSword], 1, [Items.StoneSword, Items.WoodenSword]),
        ["food"] = new("food", [Items.CookedPorkchop, Items.Steak, Items.CookedChicken, Items.Bread, Items.CookedFish, Items.MushroomStew,
            Items.Apple, Items.GoldenApple, Items.Cookie, Items.MelonSlice, Items.Porkchop, Items.RawBeef, Items.RawFish], 8, []),
    };

    public static int Have(WorldState w, Kind k) => k.Ids.Sum(id => w.Inventory.CountItem(id));

    /// <summary>Like <see cref="Have"/>, without tools about to break (<see cref="AcquisitionPlanner.WornUses"/> uses or fewer left).</summary>
    public static int HaveUsable(WorldState w, Kind k) => w.Inventory.PlayerSlots().Where(s => !s.IsEmpty && k.Ids.Contains(s.Id))
        .Where(s => !Items.Exists(s.Id) || Items.Get(s.Id).MaxDamage <= 0 || Items.Get(s.Id).MaxDamage - s.Damage > AcquisitionPlanner.WornUses)
        .Sum(s => (int)s.Count);

    /// <summary>Kinds from <paramref name="wants"/> the inventory lacks right now.</summary>
    public static List<Kind> Lacking(WorldState w, IEnumerable<string> wants) =>
        wants.Select(n => Kinds.GetValueOrDefault(n.Trim())).OfType<Kind>().DistinctBy(k => k.Name).Where(k => Have(w, k) == 0).ToList();

    public sealed record Report(string Text, IReadOnlyList<string> Missing, IReadOnlyList<string> Got);

    /// <summary>
    /// Gets each wanted kind the bot doesn't carry through the <see cref="AcquisitionPlanner"/>: storage (nearest chest that
    /// holds one), a hive spare, crafting from what's reachable; a pickaxe (the job can't run without one) also through the
    /// from-scratch bootstrap (tree → wood pick → gen cobble → stone pick) and, last, a player.
    /// </summary>
    public static async Task<Report> FetchAsync(IGameClient c, ObservationStore? observations, IEnumerable<string> wants, CancellationToken ct)
    {
        var w = c.World;
        var all = wants.Select(n => Kinds.GetValueOrDefault(n.Trim())).OfType<Kind>().DistinctBy(k => k.Name).ToList();
        var got = new List<string>();
        var have = new List<string>();
        var missing = new List<string>();
        var planned = new List<string>(); // bootstraps: "no pick, making one: ... | steps; got stone pickaxe"
        var planner = Acquisition.Planner(c, observations);
        foreach (var k in all)
        {
            ct.ThrowIfCancellationRequested();
            if (HaveUsable(w, k) > 0) { have.Add(k.Name); continue; } // a worn one is replaced (the planner keeps it as the fallback)
            var depth = k.Name == "pickaxe" ? AcquireDepth.Full : AcquireDepth.Cheap;
            var r = await planner.AcquireAsync(new Need(k.Name, 1, 0, depth), ct).ConfigureAwait(false);
            if (r.Ok && Have(w, k) > 0) { (r.Plan != null ? planned : got).Add(r.Text); continue; }
            missing.Add(k.Name);
        }
        return new Report(FetchText(got, planned, have, missing), missing, [.. got, .. planned]);
    }

    /// <summary>"took …; <bootstrap lines>; already had …; no … in my inventory or storage", or "nothing needed".</summary>
    private static string FetchText(List<string> got, List<string> planned, List<string> have, List<string> missing)
    {
        var parts = new List<string>();
        if (got.Count > 0) parts.Add("took " + string.Join(", ", got));
        parts.AddRange(planned);
        if (have.Count > 0) parts.Add("already had " + string.Join(", ", have));
        if (missing.Count > 0) parts.Add("no " + string.Join(", no ", missing) + " in my inventory or storage");
        return parts.Count == 0 ? "nothing needed" : string.Join("; ", parts);
    }

    /// <summary>Withdraws one of the kind from the nearest known container holding it; "stone pickaxe from 14,64,-5" or null.</summary>
    public static async Task<string?> WithdrawAsync(IGameClient c, ObservationStore observations, Kind k, CancellationToken ct)
    {
        var me = c.World.Player.Position;
        var spots = new List<(BlockPos Pos, int Id, int Count, double Dist)>();
        foreach (var id in k.Ids)
        {
            List<ItemLocation> found;
            try { found = observations.FindItemInContainers(id, limit: 6); }
            catch (Exception) { continue; } // db busy: try the next kind
            foreach (var f in found)
            {
                if (f.Container.Bounds is not { } b) continue;
                var p = new BlockPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ));
                spots.Add((p, id, f.Count, p.Center.DistanceTo(me)));
            }
        }
        // Best tier first, then the nearest chest; a few tries (stale snapshots: the chest may be empty by now).
        foreach (var s in spots.OrderBy(s => Array.IndexOf(k.Ids, s.Id)).ThenBy(s => s.Dist).Take(4))
        {
            int n = Math.Max(1, Math.Min(k.Want, s.Count));
            var r = await c.WithdrawAsync(s.Id, n, s.Pos, null, ct).ConfigureAwait(false);
            if (r.Ok && Have(c.World, k) > 0) return $"{Names.Item(s.Id)} from {s.Pos.X},{s.Pos.Y},{s.Pos.Z}";
        }
        return null;
    }

    /// <summary>Crafts the best craftable tier when the materials are on hand (stone first, then wood); "crafted stone pickaxe" or null.</summary>
    public static async Task<string?> CraftAsync(IGameClient c, Kind k, CancellationToken ct, ObservationStore? observations = null)
    {
        if (k.Craftable.Length == 0) return null;
        var crafter = new Crafter(c) { Observations = observations }; // sticks/planks/cobble from chests when short
        foreach (var id in k.Craftable)
        {
            var r = await crafter.CraftAsync(Items.Get(id).Name, 1, ct).ConfigureAwait(false);
            if (r.Ok && Have(c.World, k) > 0)
                return "crafted " + Names.Item(id) + (r.Message.StartsWith("took ", StringComparison.Ordinal) ? " (" + r.Message.Split(';')[0] + ")" : "");
        }
        return null;
    }
}
