using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Render;
using MineClaw.World;
using Xunit.Abstractions;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Live Hive's CPU-side drawing byte for byte (WP08 complexity refactor): the free camera's overlays (plans, claims,
/// places, chests and gens, dangers, bots, players, drops, the staleness tint) painted into seeded frames from a few cameras
/// with every flag combination that matters, hashed as pixels + labels. Set EXPLORER_GOLDEN_OUT to write the dumps out.
/// </summary>
public sealed class LiveHiveGoldenTests(ITestOutputHelper output)
{
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];
    private static string Hash(uint[] px) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(px.AsSpan())))[..20];

    private void Compare(List<(string Name, string Dump)> rows, string expected)
    {
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        var actual = new StringBuilder();
        foreach (var (n, dump) in rows)
        {
            if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, n + ".txt"), dump);
            actual.Append(n).Append(' ').Append(Hash(dump)).Append('\n');
        }
        var want = string.Join("\n", expected.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0)) + "\n";
        if (want != actual.ToString()) output.WriteLine(actual.ToString());
        Assert.Equal(want, actual.ToString());
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    private static HiveWorldCache World()
    {
        var cache = new HiveWorldCache();
        var entries = new List<ColumnPayloadEntry>();
        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var blocks = new List<(int, int, int, byte)>();
                for (int x = 0; x < 16; x++)
                    for (int z = 0; z < 16; z++)
                        blocks.Add((x, 63, z, (byte)((x + z) % 5 == 0 ? 4 : 2)));
                if (cx == 0 && cz == 0) { blocks.Add((5, 64, 5, 4)); blocks.Add((6, 64, 5, 1)); }
                long seen = Now.ToUnixTimeMilliseconds() - (long)(Math.Abs(cx * 7 + cz * 3) * 900_000L);
                entries.Add(Entry(cx, cz, Snap([.. blocks]), seen));
            }
        cache.Apply(ColumnPayload.Encode(0, entries));
        return cache;
    }

    private static EyeRenderer.Frame Frame(int w, int h, int seed)
    {
        var px = new uint[w * h];
        var depth = new float[w * h];
        var r = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                px[i] = (uint)(0xff000000 | (uint)(x * 255 / w) << 16 | (uint)(y * 255 / h) << 8 | (uint)r.Next(256));
                // sky in the top third, a floor below getting nearer, a wall block in the middle
                depth[i] = y < h / 3 ? float.PositiveInfinity : (float)(4 + 60.0 * (h - y) / h);
                if (x > w * 2 / 5 && x < w * 3 / 5 && y > h / 3 && y < h * 2 / 3) depth[i] = 3.5f;
            }
        return new EyeRenderer.Frame(px, depth, w, h);
    }

    private static int[] B(int x, int y, int z, int id, int meta, int done) => [x, y, z, id, meta, done];

    private static OverlaysDto Overlays(int dim) => new(Now, dim,
        [new MarkDto("claim", "home base", [0, 63, 0], [8, 66, 8], "maid_chan_67"), new MarkDto("claim", "far", [400, 63, 400], [410, 66, 410], null),
         new MarkDto("claim", "x", [2, 64, 2], [1, 63, 1], "fox_"), new MarkDto("claim", "bad", [1, 2], [3, 4, 5], "a")],
        [new MarkDto("waypoint", "spawn", [3, 64, -4], [3, 64, -4], null), new MarkDto("structure", "the tower with a very long name that gets cut", [-6, 63, 2], [-4, 70, 4], null),
         new MarkDto("structure", "", [9, 64, 9], [9, 64, 9], null)],
        [new MarkDto("chest", "c", [5, 64, 6], [5, 64, 6], null), new MarkDto("cobble_generator", "g", [7, 64, 3], [8, 64, 3], null),
         new MarkDto("farm", "f", [-3, 63, -3], [0, 63, 0], null), new MarkDto("portal", "p", [10, 64, -2], [10, 66, -1], null),
         new MarkDto("key", "the key spot", [1, 64, 8], [1, 64, 8], null), new MarkDto("island", "main island", [-10, 60, -10], [10, 64, 10], null),
         new MarkDto("other_thing", "o", [4, 65, 4], [4, 65, 4], null)],
        [new PlanDto(1, "wall", "building", 0, [B(2, 64, 2, 4, 0, 0), B(3, 64, 2, 4, 0, 1), B(5, 64, 5, 4, 0, 1), B(6, 64, 5, 4, 0, 1), [1, 2, 3], B(300, 64, 0, 4, 0, 0)]),
         new PlanDto(2, "empty", "done", 0, [])],
        [new MarkDto("lava", "lava pool", [-2, 62, 6], [-1, 63, 7], null)], ["note"]);

    private static string OverlayDump()
    {
        var cache = World();
        var vox = new HiveVoxels(cache);
        vox.Prepare(0, 0, 0, 3, Now);
        List<BotDto> bots =
        [
            new("maid_chan", "main", true, 0, 2.5, 64, 2.5, 0, 0, 0, 20, 20, null, null, null, null, true, 0),
            new("maid_chan_67", "67", false, 0, -3.5, 64, 6.5, 90, 0, 0, 20, 20, null, null, null, null, true, 0),
            new("nether_bot", "68", false, -1, 0, 64, 0, 0, 0, 0, 20, 20, null, null, null, null, true, 0),
        ];
        List<EntityDto> ents =
        [
            new("player", "Fox", 0, 6.5, 64, -2.5), new("player", null, 0, 1, 64, 1), new("mob", "zombie", 0, 3, 64, 3),
            new("item", "cobblestone", 0, 4.5, 64, 4.5, Count: 12), new("item", null, 0, 5.5, 64, 2.5, Count: 1), new("item", "dirt", 0, 1.5, 64, 6.5),
            new("item", "far", -1, 1.5, 64, 6.5), new("player", "Robin", -1, 0, 64, 0),
        ];
        var cams = new[]
        {
            new CamPose(0.5, 70, -8, 0, 25, 0), new CamPose(12, 66, 12, 135, 10, 0), new CamPose(2, 64.6, 2, 270, -30, 0), new CamPose(0, 120, 0, 0, 89, 0),
        };
        var flagSets = new[] { Overlay.Default, Overlay.None, Overlay.Plans, Overlay.Claims | Overlay.Places, Overlay.Chests, Overlay.Dangers,
            Overlay.Bots, Overlay.Drops, Overlay.Staleness, Overlay.Default & ~Overlay.Staleness };
        var sb = new StringBuilder();
        int seed = 0;
        foreach (var cam in cams)
            foreach (var flags in flagSets)
                foreach (var ov in new[] { Overlays(0), null, Overlays(-1) })
                    foreach (double maxDist in new[] { 48.0, 12.0 })
                    {
                        var fr = Frame(160, 120, seed++);
                        var proj = new ViewProjector(cam, fr.Width, fr.Height, 70f);
                        var labels = OverlayPainter.Paint(fr, proj, flags, ov, bots, ents, vox, Now, maxDist);
                        sb.Append(cam).Append(' ').Append(flags).Append(' ').Append(ov?.Dim.ToString() ?? "null").Append(' ').Append(maxDist).Append(' ')
                            .Append(Hash(fr.Px)).Append('\n');
                        foreach (var l in labels) sb.Append("  ").Append(l).Append('\n');
                    }
        foreach (long age in new long[] { -5, 0, 59_999, 60_000, 600_000, 3_600_000, 86_400_000, 999_999_999 })
            sb.Append(age).Append(' ').Append(OverlayPainter.AgeAlpha(age).ToString("R")).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void Live_hive_drawing_matches_the_golden_hashes()
    {
        var rows = new List<(string, string)> { ("overlays", OverlayDump()) };
        Compare(rows, Golden);
    }

    private const string Golden = """
        pending
        """;
}
