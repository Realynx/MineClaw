using System.Globalization;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>One ore's share of a dig: blocks of it mined (in a dug cell) and exposed (in a wall touching one, not dug).</summary>
public sealed record OreTally(OreBand Ore, int Mined, int Exposed)
{
    /// <summary>Every block of it the miner sees: mined, or in sight in a wall (and mined on the way).</summary>
    public int Seen => Mined + Exposed;
}

/// <summary>
/// What a mine design digs on its generated terrain (estimates: the real world differs): the blocks to mine by material, each
/// ore mined and exposed in the walls, and the dug y range. A line-for-line port of the browser's <c>mineStats.ts</c> (both are
/// checked against the same numbers), so the studio's panel, the AI's <c>mine_stats</c> tool and the layout study agree.
/// </summary>
public sealed record MineStats(int ToMine, IReadOnlyList<(string Name, int Count)> Materials, IReadOnlyList<OreTally> Ores, (int Min, int Max)? YRange)
{
    private static readonly (int Dx, int Dy, int Dz)[] N6 = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>Every ore block seen (mined or exposed).</summary>
    public int Seen => Ores.Sum(o => o.Seen);

    /// <summary>Ore blocks seen per 100 blocks mined, of the given ores (null = all): the layout's yield for its digging.</summary>
    public double SeenPer100(Func<OreBand, bool>? which = null) =>
        ToMine == 0 ? 0 : 100.0 * Ores.Where(o => which?.Invoke(o.Ore) ?? true).Sum(o => o.Seen) / ToMine;

    /// <summary>The estimate of digging <paramref name="dug"/> (bedrock and open cells are never mined).</summary>
    public static MineStats Of(MineTerrain terrain, IEnumerable<BlockPos> dug)
    {
        var cells = dug.ToList();
        var set = cells.ToHashSet();
        var materials = new Dictionary<string, int>();
        var mined = new Dictionary<string, int>();
        var exposedBy = new Dictionary<string, int>();
        var exposed = new HashSet<BlockPos>();
        int toMine = 0, lo = int.MaxValue, hi = int.MinValue;
        foreach (var p in cells)
        {
            lo = Math.Min(lo, p.Y);
            hi = Math.Max(hi, p.Y);
            var (id, meta) = terrain.Block(p.X, p.Y, p.Z);
            if (id == MineTerrain.Bedrock) continue;
            if (id != 0)
            {
                toMine++;
                Bump(materials, NameOf(terrain, id, meta));
                if (OreTable.OreOf(terrain.Version, id, meta) is { } ore) Bump(mined, ore.Id);
            }
            // Authored excavation reveals walls even where it crosses a naturally empty seed cave.
            foreach (var (dx, dy, dz) in N6)
            {
                var n = new BlockPos(p.X + dx, p.Y + dy, p.Z + dz);
                if (set.Contains(n) || !exposed.Add(n)) continue;
                var (nid, nmeta) = terrain.Block(n.X, n.Y, n.Z);
                if (nid != 0 && OreTable.OreOf(terrain.Version, nid, nmeta) is { } o) Bump(exposedBy, o.Id);
                else exposed.Remove(n);
            }
        }
        return new MineStats(toMine,
            materials.Select(kv => (kv.Key, kv.Value)).OrderByDescending(m => m.Value).ToList(),
            terrain.Table.Ores.Where(o => o.Veins > 0).Select(o => new OreTally(o, mined.GetValueOrDefault(o.Id), exposedBy.GetValueOrDefault(o.Id))).ToList(),
            cells.Count > 0 ? (lo, hi) : null);
    }

    /// <summary>A terrain block's name for the stats: the ore or pocket's, else the plain block's.</summary>
    public static string NameOf(MineTerrain terrain, int id, int meta)
    {
        if (OreTable.OreOf(terrain.Version, id, meta) is { } ore) return ore.Name;
        if (terrain.Table.Pockets.FirstOrDefault(p => p.Block == id && p.Meta == meta) is { } pocket) return pocket.Name;
        return id switch
        {
            MineTerrain.Stone => "Stone",
            MineTerrain.Grass => "Grass",
            MineTerrain.Dirt => "Dirt",
            MineTerrain.Bedrock => "Bedrock",
            13 => "Gravel",
            _ => string.Create(CultureInfo.InvariantCulture, $"#{id}:{meta}"),
        };
    }

    /// <summary>
    /// "mine y11..72: 934 blocks to mine (848 stone, 45 dirt, …); ores seen (mined+walls): diamond 3+4, redstone 5+20, …; 9.0 ore
    /// per 100 blocks": the one line the AI designer reads.
    /// </summary>
    public string Text(int maxMaterials = 4)
    {
        if (YRange is not { } y) return "nothing dug yet";
        var mats = string.Join(", ", Materials.Take(maxMaterials).Select(m => $"{m.Count} {m.Name.ToLowerInvariant()}")) + (Materials.Count > maxMaterials ? ", …" : "");
        var ores = Ores.Where(o => o.Seen > 0).OrderBy(o => o.Ore.Veins).Select(o => $"{o.Ore.Id} {o.Mined}+{o.Exposed}").ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"dug y{y.Min}..{y.Max}: {ToMine} blocks to mine ({mats}); ores seen (mined+in the walls): {(ores.Count > 0 ? string.Join(", ", ores) : "none")}; {SeenPer100():0.0} ore per 100 blocks mined");
    }

    private static void Bump(Dictionary<string, int> m, string k) => m[k] = m.GetValueOrDefault(k) + 1;
}
