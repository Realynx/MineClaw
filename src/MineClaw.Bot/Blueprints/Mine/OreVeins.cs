using System.Runtime.CompilerServices;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints.Mine;

/// <summary>What a dig yields of one ore: the veins it touches (a block in a dug cell or a wall), their blocks, and the plot's.</summary>
public sealed record OreYield(OreBand Ore, int Veins, int Blocks, int PlotBlocks)
{
    /// <summary>The share of the plot's blocks of this ore the dig finds (0..1).</summary>
    public double Coverage => PlotBlocks == 0 ? 0 : (double)Blocks / PlotBlocks;
}

/// <summary>
/// The ore veins of a mine terrain's plot (6-connected cells of one ore, x and z 0..63) and what a dig finds of them: a miner who
/// sees one block of a vein mines the whole vein, so a layout's real yield is the blocks of every vein its tunnels touch, not just
/// the blocks in their walls. The layout study (docs/design/mine-blueprints.md) and the AI's <c>mine_stats</c> tool read it.
/// </summary>
public sealed class OreVeins
{
    private static readonly ConditionalWeakTable<MineTerrain, OreVeins> Cache = new();

    private readonly MineTerrain _terrain;
    private readonly Dictionary<BlockPos, int> _veinOf = new();
    private readonly List<(OreBand Ore, int Size)> _veins = [];

    private OreVeins(MineTerrain terrain)
    {
        _terrain = terrain;
        for (int y = 0; y < MineTerrain.Height; y++)
            for (int z = 0; z < DesignerCanvas.Size; z++)
                for (int x = 0; x < DesignerCanvas.Size; x++)
                    if (!_veinOf.ContainsKey(new BlockPos(x, y, z)) && OreAt(x, y, z) is { } ore) Fill(new BlockPos(x, y, z), ore);
    }

    /// <summary>The veins of a terrain's plot (worked out once per terrain).</summary>
    public static OreVeins For(MineTerrain terrain) => Cache.GetValue(terrain, t => new OreVeins(t));

    /// <summary>Per ore (rarest first): what digging <paramref name="dug"/> finds by following every vein it touches.</summary>
    public IReadOnlyList<OreYield> Yield(IEnumerable<BlockPos> dug)
    {
        var touched = new HashSet<int>();
        foreach (var p in dug)
        {
            if (_veinOf.TryGetValue(p, out var v)) touched.Add(v);
            foreach (var (dx, dy, dz) in OreIdentity.Neighbors)
                if (_veinOf.TryGetValue(new BlockPos(p.X + dx, p.Y + dy, p.Z + dz), out var n)) touched.Add(n);
        }
        return _terrain.Table.Ores.Where(o => o.Veins > 0).OrderBy(o => o.Veins).ThenBy(o => o.Size).Select(o =>
        {
            var found = touched.Where(v => _veins[v].Ore.Id == o.Id).ToList();
            return new OreYield(o, found.Count, found.Sum(v => _veins[v].Size), _veins.Where(v => v.Ore.Id == o.Id).Sum(v => v.Size));
        }).ToList();
    }

    private OreBand? OreAt(int x, int y, int z)
    {
        var (id, meta) = _terrain.Block(x, y, z);
        return id == 0 ? null : OreTable.OreOf(_terrain.Version, id, meta);
    }

    /// <summary>One vein from <paramref name="start"/>: every 6-connected cell of the same ore on the plot.</summary>
    private void Fill(BlockPos start, OreBand ore)
    {
        int index = _veins.Count, size = 0;
        var queue = new Queue<BlockPos>([start]);
        _veinOf[start] = index;
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            size++;
            foreach (var (dx, dy, dz) in OreIdentity.Neighbors)
            {
                var n = new BlockPos(c.X + dx, c.Y + dy, c.Z + dz);
                if (n.X is < 0 or >= DesignerCanvas.Size || n.Z is < 0 or >= DesignerCanvas.Size || n.Y is < 0 or >= MineTerrain.Height
                    || _veinOf.ContainsKey(n) || OreAt(n.X, n.Y, n.Z)?.Id != ore.Id) continue;
                _veinOf[n] = index;
                queue.Enqueue(n);
            }
        }
        _veins.Add((ore, size));
    }
}
