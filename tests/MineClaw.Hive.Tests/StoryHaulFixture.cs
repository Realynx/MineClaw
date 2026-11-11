using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The strip-mine colony (<see cref="StoryStripMineFixture"/>) with its storage and its hauling in the live shape of 11-10, when the haul
/// search cost about 0.9 s a call (work:haul 56-60 s a minute of 64-71 calls, the longest 4.5 s) and the logs said "can't reach chest"
/// about 300 times in ten minutes: 374 storages (336 double chests, 38 single ones), most taking anything, a few filtered (food, tools,
/// ores, stone, wood) and nine of them important; what a strip mine leaves (cobblestone, coal, iron ore, gravel, dirt, a few spare
/// pickaxes) in about one chest in eight, some chests full; drops on the ground by the shaft, by home and down the tunnels; miners with
/// full pockets and generalists with mixed ones; and each pawn with its own set of chests it failed to reach ("no walkable spot that
/// sees it"), as the live colony's pawns had. Every value is generated here, at the strip-mine fixture's made-up coordinates: only the
/// counts and the shape follow the live colony. Deterministic.
/// </summary>
internal static class StoryHaulFixture
{
    /// <summary>The single chests' row (the double chests stand in the strip-mine fixture's chest cells).</summary>
    public const int SingleRowZ = 1240;

    /// <summary>The colony. <paramref name="drops"/>: drops on the ground; <paramref name="filled"/>: every n-th chest holds the mine's
    /// output (8: the live snapshot's share); <paramref name="skipShare"/>: of each pawn's chests, the share it failed to reach (per
    /// thousand); <paramref name="knowsChests"/>: the host remembers chests (the live host does).</summary>
    public static StoryStripMineFixture.Rig Make(int drops = 600, int filled = 8, int skipShare = 250, bool knowsChests = true)
    {
        var r = StoryStripMineFixture.Make();
        // The live colony's deconstruct mark was the colony's own (auto), not the owner's.
        foreach (var d in r.C.Designations.Where(d => d.Kind == "deconstruct")) d.Auto = true;
        Storages(r, filled);
        Drops(r, drops);
        Pockets(r);
        r.H.KnowsChests = knowsChests;
        r.H.FeedsLooks = knowsChests;
        Skips(r, skipShare);
        return r;
    }

    private static readonly int[] Loot = [4, 4, 4, 4, 3, 263, 13, 15, 4, 1];

    /// <summary>374 storages: 336 doubles over the strip-mine fixture's chest pairs, 38 singles in a row of their own.</summary>
    private static void Storages(StoryStripMineFixture.Rig r, int filled)
    {
        var c = r.C;
        c.Storages.Clear();
        r.H.Chests.Clear();
        var cells = StoryStripMineFixture.ChestCells(); // 333 pairs
        var chests = new List<(P3 At, P3? Half)>();
        for (int i = 0; i + 1 < cells.Count; i += 2) chests.Add((cells[i], cells[i + 1]));
        for (int k = 0; chests.Count < 336; k++) // three more pairs, then the singles
        {
            var a = new P3(1790 + 3 * k, 64, SingleRowZ - 2);
            r.W.Set(a.X, a.Y, a.Z, 54);
            r.W.Set(a.X + 1, a.Y, a.Z, 54);
            chests.Add((a, a.Add(1, 0, 0)));
        }
        for (int k = 0; chests.Count < 374; k++)
        {
            var a = new P3(1790 + 2 * k, 64, SingleRowZ);
            r.W.Set(a.X, a.Y, a.Z, 54);
            chests.Add((a, null));
        }
        for (int i = 0; i < chests.Count; i++)
        {
            var (at, half) = chests[i];
            var s = new StorageState
            {
                Chest = at, DoubleWith = half, Priority = i % 41 == 7 ? "important" : "normal", Filter = FilterOf(i),
                KnownAt = r.Clock.Now - TimeSpan.FromMinutes(1), SeenAt = r.Clock.Now - TimeSpan.FromMinutes(3), Rev = 900 + i,
            };
            s.Contents = ContentsOf(i, s.Slots, filled);
            int used = s.Contents.Sum(x => (x.Count + 63) / 64);
            s.FreeSlots = Math.Max(0, s.Slots - used);
            c.Storages.Add(s);
            r.H.Chests[s.Chest] = s.Contents.ToList();
            r.H.Seen[s.Chest] = (s.Contents.ToList(), s.SeenAt!.Value);
        }
    }

    /// <summary>Most take anything; a few nothing; food, tools, ores and stone, wood chests as the live colony had.</summary>
    private static StorageFilter FilterOf(int i) => (i % 37) switch
    {
        3 => new StorageFilter { Allow = [] },
        11 when i < 150 => new StorageFilter { Allow = ["food_meals"] },
        11 => new StorageFilter { Allow = ["food"], Deny = ["food_produce", "food_raw_meat", "food_fish"] },
        19 when i < 80 => new StorageFilter { Allow = ["tools"] },
        19 when i < 160 => new StorageFilter { Allow = ["ores_metals"] },
        19 when i < 240 => new StorageFilter { Allow = ["stone_building", "ores_metals"] },
        19 => new StorageFilter { Allow = ["wood"] },
        _ => new StorageFilter { Allow = ["*"] },
    };

    /// <summary>Most empty; one in <paramref name="filled"/> holds the mine's output (up to 20 stacks), some a few spare pickaxes, a few full.</summary>
    private static List<Stack> ContentsOf(int i, int slots, int filled)
    {
        if (i % filled != 5 % filled) return [];
        var list = new List<Stack>();
        int stacks = i % 48 == 5 ? slots : 3 + i % 17;
        for (int k = 0; k < stacks; k++)
        {
            int id = Loot[(i + k) % Loot.Length];
            if (list.FindIndex(x => x.Id == id) is var at and >= 0) list[at] = list[at] with { Count = list[at].Count + 64 };
            else list.Add(new Stack(id, 0, 64));
        }
        if (i % 3 == 0) list.Add(new Stack(274, 0, 1 + i % 2)); // spare stone pickaxes
        return list;
    }

    /// <summary>Drops: a third by the shaft's top and home (in the haul area, on the ground), the rest down the tunnels.</summary>
    private static void Drops(StoryStripMineFixture.Rig r, int n)
    {
        var w = r.W;
        int id = 50_000;
        for (int k = 0; k < n; k++)
        {
            int item = Loot[k % Loot.Length];
            double x, y, z;
            if (k % 3 == 0) { x = 1750 + k * 7 % 60 + 0.5; y = 64; z = 1060 + k * 11 % 140 + 0.5; } // on the surface over the mine and by home
            else
            {
                var cells = StoryStripMineFixture.MineCells(k % 3, k % 4);
                var at = cells[k * 37 % cells.Count];
                w.Set(at.X, at.Y, at.Z, 0); // a dug cell
                x = at.X + 0.5; y = at.Y; z = at.Z + 0.5;
            }
            var seen = r.Clock.Now - TimeSpan.FromSeconds(k * 13 % 280);
            w.Ents.Add(new StoryEntity(id++, "item", "item", x, y, z, ItemId: item, ItemMeta: 0, Count: 1 + k % 5, SeenAt: r.Clock.Now, FirstSeen: seen));
        }
    }

    /// <summary>Miners' pockets full of what they dug; generalists' mixed (a few stacks and free slots).</summary>
    private static void Pockets(StoryStripMineFixture.Rig r)
    {
        for (int i = 0; i < r.Names.Count; i++)
        {
            var name = r.Names[i];
            var l = r.H.Lives[name];
            bool miner = r.C.Pawns[name].Role == "miner";
            int stacks = miner ? 33 + i % 2 : 2 + i % 6;
            var inv = Enumerable.Range(0, stacks).Select(k => new Stack(Loot[(i + k) % Loot.Length], 0, 64)).GroupBy(s => s.Id)
                .Select(g => new Stack(g.Key, 0, g.Sum(s => s.Count))).ToList();
            inv.Add(new Stack(274, 0, 1 + i % 3)); // its pickaxe (and spares)
            r.H.Lives[name] = l with { Inventory = inv, FreeSlots = 36 - stacks - 1 };
        }
    }

    /// <summary>Each pawn failed to reach its own share of the chests ("can't reach chest … no walkable spot that sees it").</summary>
    private static void Skips(StoryStripMineFixture.Rig r, int share)
    {
        var c = r.C;
        for (int i = 0; i < r.Names.Count; i++)
        {
            var p = c.Pawns[r.Names[i]];
            for (int k = 0; k < c.Storages.Count; k++)
            {
                if ((k * 7919 + i * 104729) % 1000 >= share) continue;
                var at = c.Storages[k].Chest;
                c.ContainerUnreachable(p, $"deposit into chest ({at.X}, {at.Y}, {at.Z}): can't reach chest at {at.X},{at.Y},{at.Z}: no walkable spot that sees it");
            }
        }
    }
}
