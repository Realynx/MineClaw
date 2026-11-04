using System.Security.Cryptography;
using System.Text;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Pins <see cref="PitScan"/> and <see cref="ReachScan"/> (the eyes of <c>story.escape</c> / <c>story.dig</c>) over seeded rough
/// worlds: ledges, 1×1 holes, water and lava, sand and gravel overhead, cave roofs and build roofs, obsidian and chests, and three
/// inventories (nothing, a little dirt, cobble and a pickaxe). Every scan's JSON for every probe: the first world readable, all of them
/// by hash. A refactor must leave <see cref="StoryScanGoldenData"/> unchanged. Regenerate (only for an intended change) by setting
/// WP09_GOLDEN_OUT.
/// </summary>
public class StoryScanGoldenTests
{
    private const int Span = 14;

    private static (WorldState World, int[,] Top, List<(int X, int Z)> Features) Build(int seed)
    {
        var rnd = new Random(seed);
        var w = new WorldState();
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        var top = new int[2 * Span + 1, 2 * Span + 1];
        for (int x = -Span; x <= Span; x++)
            for (int z = -Span; z <= Span; z++)
            {
                int h = 64 + rnd.Next(0, 3) + (rnd.Next(6) == 0 ? rnd.Next(1, 5) : 0);
                for (int y = 56; y <= h; y++) w.SetBlock(x, y, z, y == h ? Surface(rnd) : (rnd.Next(4) == 0 ? Blocks.Dirt : Blocks.Stone), 0);
                top[x + Span, z + Span] = h;
            }
        int features = 6 + rnd.Next(6);
        var spots = new List<(int X, int Z)>();
        for (int k = 0; k < features; k++)
        {
            int x = rnd.Next(-Span + 2, Span - 1), z = rnd.Next(-Span + 2, Span - 1), h = top[x + Span, z + Span];
            spots.Add((x, z));
            switch (rnd.Next(9))
            {
                case 0: for (int y = h; y > h - 2 - rnd.Next(3); y--) w.SetBlock(x, y, z, 0, 0); break;            // a hole
                case 1: w.SetBlock(x, h, z, Blocks.Water, 0); w.SetBlock(x, h + 1, z, Blocks.Water, 0); break;     // a pool
                case 2: w.SetBlock(x + 1, h, z, Blocks.Lava, 0); break;
                case 3: w.SetBlock(x, h + 3, z, rnd.Next(2) == 0 ? Blocks.Sand : Blocks.Gravel, 0); break;
                case 4: Slab(w, x, z, h + 4, 3, Blocks.Stone); break;                                                  // a cave roof
                case 5: Slab(w, x, z, h + 4, 2, Blocks.Planks); break;                                                 // a build's roof
                case 6: w.SetBlock(x, h + 1, z, Blocks.Obsidian, 0); w.SetBlock(x, h + 2, z, Blocks.Chest, 0); break;
                case 7: for (int y = h + 1; y <= h + 4; y++) w.SetBlock(x, y, z, 17, 0); Slab(w, x, z, h + 5, 1, 18); break; // a tree
                default: for (int d = 0; d < 3; d++) w.SetBlock(x + d, h + 1 + d, z, Blocks.Dirt, 0); break;           // a ledge stair
            }
        }
        switch (seed % 3)
        {
            case 1: w.Inventory.SetSlot(0, 36, new ItemStack((short)Blocks.Dirt, 5, 0)); break;
            case 2:
                w.Inventory.SetSlot(0, 36, new ItemStack((short)Blocks.Cobblestone, 20, 0));
                w.Inventory.SetSlot(0, 37, new ItemStack(274, 1, 0));
                break;
        }
        return (w, top, spots);
    }

    private static int Surface(Random rnd) => rnd.Next(10) switch
    {
        0 => Blocks.Sand,
        1 => Blocks.Gravel,
        2 => Blocks.Stone,
        3 => Blocks.Dirt,
        _ => Blocks.Grass,
    };

    private static void Slab(WorldState w, int cx, int cz, int y, int r, int id)
    {
        for (int x = cx - r; x <= cx + r; x++)
            for (int z = cz - r; z <= cz + r; z++)
                w.SetBlock(x, y, z, id, 0);
    }

    private static IEnumerable<(int X, int Z)> Spots(List<(int X, int Z)> features)
    {
        for (int x = -6; x <= 6; x += 2)
            for (int z = -6; z <= 6; z += 2)
                yield return (x, z);
        foreach (var (x, z) in features)
        {
            yield return (x, z);
            yield return (x + 1, z);
            yield return (x - 1, z + 1);
        }
    }

    private static void Probe(StringBuilder sb, WorldState w, int[,] top, List<(int X, int Z)> features, IGameData data)
    {
        var home = new BlockPos(9, 67, 0);
        foreach (var (x, z) in Spots(features))
            {
                int y = top[x + Span, z + Span] + 1;
                // The column's lowest free cell from the top down (inside a hole, a pool).
                while (y > 57 && !BlockShapes.HasCollision(w, x, y - 1, z)) y--;
                var pos = new Vec3d(x + 0.5, y, z + 0.5);
                sb.Append("@ ").Append(x).Append(',').Append(y).Append(',').Append(z).Append('\n');
                sb.Append("  pit  ").Append(PitScan.Scan(w, data, pos, null).ToJsonString()).Append('\n');
                sb.Append("  home ").Append(PitScan.Scan(w, data, pos, home).ToJsonString()).Append('\n');
                sb.Append("  clmb ").Append(PitScan.Scan(w, data, pos, home, climb: true).ToJsonString()).Append('\n');
                foreach (var (dx, dy, dz) in new[] { (0, 4, 0), (1, 7, 0), (0, 12, 1), (2, 30, -1), (0, 2, 0), (3, 6, 0), (-2, 9, 2) })
                    sb.Append("  reach ").Append(dx).Append(',').Append(dy).Append(',').Append(dz).Append(' ')
                        .Append(ReachScan.Scan(w, data, pos, new BlockPos(x + dx, y + dy, z + dz)).ToJsonString()).Append('\n');
            }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    internal static string Dump()
    {
        var data = Beta181GameData.Instance;
        var sb = new StringBuilder();
        var all = new StringBuilder();
        for (int seed = 0; seed < 12; seed++)
        {
            var (w, top, features) = Build(seed);
            var part = new StringBuilder();
            Probe(part, w, top, features, data);
            if (seed == 0) sb.Append(part);
            all.Append("== seed ").Append(seed).Append('\n').Append(part);
        }
        sb.Append("all ").Append(Hash(all.ToString())).Append('\n');
        // The whole dump, to diff a changed hash against a run of the old code.
        if (Environment.GetEnvironmentVariable("WP09_GOLDEN_ALL") is { Length: > 0 } path) File.WriteAllText(path, all.ToString());
        return sb.ToString();
    }

    [Fact]
    public void Story_scans_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP09_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(StoryScanGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n'), actual.TrimEnd('\n'));
    }
}
