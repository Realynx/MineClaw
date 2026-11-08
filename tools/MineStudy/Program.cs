using System.Globalization;
using MineClaw.Bot.Blueprints.Mine;
using MineClaw.World;

// The mine layout study (docs/design/mine-blueprints.md): which blueprint finds the most ore per block dug on the Design Studio's
// terrain. A layout's yield is the ore of every vein its tunnels touch (OreVeins: a miner follows a vein once a block of it is in
// sight), per 100 blocks mined (MineStats). Run: dotnet run -c Release --project tools/MineStudy [-- seeds]
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
int seeds = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 40;
const int Plot = 64, Corridor = 31;
string[] targets = ["diamond", "redstone", "gold", "lapis", "iron"];

foreach (var version in new[] { "1.12.2", "b1.8.1" })
{
    var terrains = Enumerable.Range(1, seeds).Select(i => MineTerrain.For((7919 * i).ToString(CultureInfo.InvariantCulture), version)).ToList();
    Console.WriteLine($"\n## {version} ({seeds} seeds, the 64x64 plot): ore found by following the veins, per 100 blocks mined (and % of the plot's)");
    Console.WriteLine($"{"layout",-30} {"mined",6}  " + string.Join("  ", targets.Select(t => $"{t,-18}")));

    void Row(string name, Func<HashSet<BlockPos>> layout)
    {
        var dug = layout();
        double mined = 0;
        var found = targets.ToDictionary(t => t, _ => 0.0);
        var cover = targets.ToDictionary(t => t, _ => 0.0);
        foreach (var terrain in terrains)
        {
            mined += MineStats.Of(terrain, dug).ToMine;
            foreach (var y in OreVeins.For(terrain).Yield(dug).Where(y => targets.Contains(y.Ore.Id)))
            {
                found[y.Ore.Id] += y.Blocks;
                cover[y.Ore.Id] += y.Coverage;
            }
        }
        Console.WriteLine($"{name,-30} {mined / seeds,6:0}  " + string.Join("  ", targets.Select(t => $"{100 * found[t] / mined,5:0.00} ({100 * cover[t] / seeds,3:0}%)      ")));
    }

    Console.WriteLine("level: 1x2 branches, 4 solid apart");
    foreach (var y in new[] { 5, 7, 9, 11, 12, 13, 14, 16, 20 }) Row($"  feet y{y}", () => Strip(y, 4));
    Console.WriteLine("spacing (solid blocks between branches) at feet y 11");
    foreach (var sp in new[] { 1, 2, 3, 4, 5, 6, 7 }) Row($"  {sp} apart", () => Strip(11, sp));
    Console.WriteLine("main corridor at feet y 11");
    foreach (var sp in new[] { 3, 4 })
    {
        Row($"  1x2 corridor, {sp} apart", () => Strip(11, sp));
        Row($"  2x3 corridor, {sp} apart", () => Strip(11, sp, width: 2, height: 3));
    }
    Console.WriteLine("tunnel shape at feet y 11, 3 solid apart");
    foreach (var (w, h) in new[] { (1, 2), (1, 3), (2, 2) }) Row($"  {w}x{h} tunnels", () => Tunnels(11, 3, w, h));
    Console.WriteLine("poke holes (1x1 side pokes at head height every 4) and second levels");
    foreach (var (sp, depth) in new[] { (5, 1), (7, 2), (9, 3) }) Row($"  {sp} apart, pokes {depth} deep", () => Pokes(Tunnels(11, sp, 1, 2), 11, sp, depth));
    Row("  y11 + y14 staggered, 3 apart", () => Union(Tunnels(11, 3, 1, 2), Tunnels(14, 3, 1, 2, offset: 2)));
    Row("  y6 + y11 staggered, 3 apart", () => Union(Tunnels(11, 3, 1, 2), Tunnels(6, 3, 1, 2, offset: 2)));
}

// The studio's own strip template across the plot: a corridor along x at z 31, branches both sides to the plot's edges.
static HashSet<BlockPos> Strip(int y, int spacing, int width = 1, int height = 2) =>
    MineTemplates.Strip(StripSpec.Default with { Y = y, Z = Corridor, Length = Plot, Width = width, Height = height, Spacing = spacing, BranchLength = Plot / 2 })
        .Cells.Where(c => c.Id == 0 && c.Z is >= 0 and < Plot).Select(c => new BlockPos(c.X, c.Y, c.Z)).ToHashSet();

// Parallel w x h tunnels along z, spacing solid blocks apart, plus a 1x2 corridor along x at z 31 (shapes the template doesn't draw).
static HashSet<BlockPos> Tunnels(int y, int spacing, int w, int h, int offset = 0)
{
    var set = new HashSet<BlockPos>();
    for (int x = 0; x < Plot; x++)
        for (int dy = 0; dy < 2; dy++) set.Add(new BlockPos(x, y + dy, Corridor));
    for (int x0 = offset; x0 < Plot; x0 += spacing + w)
        for (int dx = 0; dx < w && x0 + dx < Plot; dx++)
            for (int z = 0; z < Plot; z++)
                for (int dy = 0; dy < h; dy++) set.Add(new BlockPos(x0 + dx, y + dy, z));
    return set;
}

static HashSet<BlockPos> Pokes(HashSet<BlockPos> tunnels, int y, int spacing, int depth)
{
    for (int x0 = 0; x0 < Plot; x0 += spacing + 1)
        for (int z = 2; z < Plot; z += 4)
            for (int d = 1; d <= depth; d++)
            {
                tunnels.Add(new BlockPos(x0 - d, y + 1, z));
                tunnels.Add(new BlockPos(x0 + d, y + 1, z));
            }
    return tunnels;
}

static HashSet<BlockPos> Union(HashSet<BlockPos> a, HashSet<BlockPos> b) => [.. a, .. b];
