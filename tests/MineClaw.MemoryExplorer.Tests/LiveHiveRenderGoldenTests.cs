using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Input;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.Tests.LiveHive;
using MineClaw.World;
using Xunit.Abstractions;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins the Live Hive's CPU frames and tile view models byte for byte (WP08 complexity refactor, checkpoint 3): the free camera's
/// rendered frames (pixels + labels; the staleness tint is off, it reads the wall clock), the eye tiles' frames, the bot tiles'
/// text after updates and the feed layouts over growing and shrinking bot sets. The movement-key helper is checked against the
/// old key list over every key. Set EXPLORER_GOLDEN_OUT to a directory to write the dumps out.
/// </summary>
[Collection("avalonia")]
public sealed class LiveHiveRenderGoldenTests(ITestOutputHelper output)
{
    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..20];
    private static string Hash(int[] px, int n) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(px.AsSpan(0, n))))[..20];

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

    private static HiveWorldCache World()
    {
        var cache = new HiveWorldCache();
        long seen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        var entries = new List<ColumnPayloadEntry>();
        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var blocks = new List<(int, int, int, byte)>();
                for (int x = 0; x < 16; x++)
                    for (int z = 0; z < 16; z++)
                        blocks.Add((x, 63, z, (byte)((x * 3 + z) % 7 == 0 ? 4 : 2)));
                if (cx == 0 && cz == 0) { blocks.Add((5, 64, 5, 4)); blocks.Add((6, 64, 5, 1)); blocks.Add((6, 65, 5, 17)); }
                entries.Add(Entry(cx, cz, Snap([.. blocks]), seen));
            }
        cache.Apply(ColumnPayload.Encode(0, entries));
        return cache;
    }

    private static readonly BotDto[] Bots =
    [
        new("maid_chan", "main", true, 0, 2.5, 64, 2.5, 30, 10, 0, 20, 20, "mining", "opus", "cobblestone", ["home"], true, 0),
        new("maid_chan_67", "67", false, 0, -3.5, 64, 6.5, 90, null, 0, 20, 20, null, null, null, null, true, 0),
        new("nether_bot", "68", false, -1, 0, 64, 0, 0, 0, 0, 20, 20, null, null, null, null, true, 0),
    ];

    private static readonly EntityDto[] Ents =
    [
        new("player", "Fox", 0, 6.5, 64, -2.5, Yaw: 45), new("mob", "zombie", 0, 3, 64, 3, Type: 54), new("item", "cobblestone", 0, 4.5, 64, 4.5, Item: 4, Count: 12),
        new("player", "Robin", -1, 0, 64, 0), new("arrow", "a", 0, 2, 64, 2),
    ];

    private static OverlaysDto Overlays() => new(DateTimeOffset.UnixEpoch, 0,
        [new MarkDto("claim", "home base", [0, 63, 0], [8, 66, 8], "maid_chan_67")],
        [new MarkDto("waypoint", "spawn", [3, 64, -4], [3, 64, -4], null)],
        [new MarkDto("chest", "c", [5, 64, 6], [5, 64, 6], null)],
        [new PlanDto(1, "wall", "building", 0, [[2, 64, 2, 4, 0, 0], [3, 64, 2, 4, 0, 1]])],
        [new MarkDto("lava", "lava pool", [-2, 62, 6], [-1, 63, 7], null)], []);

    private static string Labels(List<OverlayLabel> labels) =>
        string.Join(";", labels.Select(l => $"{l.Text}@{l.X:R},{l.Y:R},{l.Dist:R},{l.Colour},{l.Occluded}"));

    private static string FreecamDump()
    {
        var cache = World();
        var cam = new FlyCamera();
        var sb = new StringBuilder();
        var ov = Overlays();
        using var eng = new FreecamEngine(cache, cam, () => null) { Bots = () => Bots, Entities = () => Ents, Overlays = () => ov, Threads = 2 };
        var poses = new[] { new CamPose(0.5, 70, -8, 0, 25, 0), new CamPose(12, 66, 12, 135, 10, 0), new CamPose(2, 64.6, 2, 270, -30, 0), new CamPose(0, 70, 0, 0, 20, -1) };
        var flags = new[] { Overlay.Default & ~Overlay.Staleness, Overlay.None, Overlay.Bots, Overlay.Bots | Overlay.Drops };
        foreach (var pose in poses)
            foreach (var f in flags)
                foreach (bool xray in new[] { false, true })
                {
                    cam.Set(pose);
                    eng.Flags = f;
                    eng.XRay = xray;
                    var fr = eng.RenderOnce(160, 90);
                    sb.Append(pose).Append(' ').Append(f).Append(' ').Append(xray).Append(" => ").Append(fr.W).Append('x').Append(fr.H).Append(' ')
                        .Append(Hash(fr.Px, fr.W * fr.H)).Append(' ').Append(fr.Pose).Append('\n').Append(Labels(fr.Labels)).Append('\n');
                }
        return sb.ToString();
    }

    private static string TileDump()
    {
        var cache = World();
        var sb = new StringBuilder();
        using var tiles = new TileRenderer(cache, () => null) { Bots = () => Bots, Entities = () => Ents, ThreadsPerTile = 2, MaxTilePixels = 200 * 150 };
        foreach (var b in Bots)
        {
            var t = new EyeTile(b.Key, cache);
            foreach (var (w, h, now) in new[] { (160, 120, 1000L), (160, 120, 1500L), (160, 120, 2600L), (400, 300, 2700L), (10, 10, 2800L) })
            {
                t.Frames.SetTarget(w, h);
                tiles.Draw(t, b, now);
                var fr = t.Frames.TakeFrame();
                sb.Append(b.Key).Append(' ').Append(w).Append('x').Append(h).Append(' ').Append(now).Append(" => ");
                if (fr == null) { sb.Append("no frame\n"); continue; }
                sb.Append(fr.W).Append('x').Append(fr.H).Append(' ').Append(Hash(fr.Px, fr.W * fr.H)).Append(' ').Append(fr.Pose).Append('\n')
                    .Append(Labels(fr.Labels)).Append('\n');
            }
            tiles.Draw(t, b with { Yaw = 100 }, 2900);
            sb.Append("turned ").Append(t.Frames.TakeFrame() is { } f2 ? Hash(f2.Px, f2.W * f2.H) : "none").Append('\n');
        }
        return sb.ToString();
    }

    private static string BotTileDump()
    {
        var sb = new StringBuilder();
        var cache = new HiveWorldCache();
        var tile = new BotTileViewModel(Bots[0], cache, TileMode.All[0]);
        var updates = new[]
        {
            Bots[0], Bots[1], Bots[2],
            Bots[0] with { Hp = 6, Brain = "", Live = false, Job = "  ", Held = "", Claims = ["a", "b", "c", "d", "e"], Yaw = 10, PoseAge = 3.5 },
            Bots[0] with { Hp = 0, Brain = null, Live = true, Claims = ["a", "b", "c"], PoseAge = 3 },
            Bots[1] with { Hp = 1, Dim = -1, X = -100.5, Y = 70.49, Z = 3.5, Claims = [], PoseAge = null, Yaw = 0 },
            Bots[1] with { Hp = 7, Main = true, Short = "", Pitch = 10 },
        };
        foreach (var b in updates)
        {
            tile.Update(b);
            sb.Append(StateDump.Of(tile, 1)).Append('\n');
        }
        tile.Dispose();
        return sb.ToString();
    }

    private static readonly MethodInfo ApplyEntitiesM = typeof(LiveHiveViewModel).GetMethod("ApplyEntities", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string LayoutDump() => HeadlessUi.Run(() =>
    {
        var sb = new StringBuilder();
        using var vm = new LiveHiveViewModel();
        void Dump(string name) => sb.Append(name).Append(": ").Append(vm.SelectedLayout.Key).Append(' ').Append(vm.GridColumns).Append('x').Append(vm.GridRows)
            .Append(" grid [").Append(string.Join(",", vm.GridTiles.Select(t => t.Key))).Append("] strip [").Append(string.Join(",", vm.StripTiles.Select(t => t.Key)))
            .Append("] focus ").Append(vm.FocusedTile?.Key ?? "null").Append(" focused [").Append(string.Join(",", vm.AllTiles.Where(t => t.IsFocused).Select(t => t.Key)))
            .Append("] hidden '").Append(vm.HiddenTiles).Append("'\n");
        BotDto Bot(int i) => new($"bot_{i}", i == 0 ? "main" : i.ToString(), i == 0, 0, i, 64, i, 0, 0, 0, 20, 20, null, null, null, null, true, 0);
        foreach (int n in new[] { 0, 1, 2, 3, 5, 8, 2, 1, 0 })
        {
            ApplyEntitiesM.Invoke(vm, [new EntitiesDto(1, Enumerable.Range(0, n).Select(Bot).ToList(), [])]);
            foreach (var layout in LiveHiveViewModel.Layouts)
            {
                vm.SelectedLayout = layout;
                Dump($"{n} bots");
            }
            if (vm.AllTiles.Count > 1)
            {
                vm.Focus(vm.AllTiles[^1]);
                Dump($"{n} bots, focus last");
                vm.SelectedLayout = LiveHiveViewModel.Layouts[1];
                Dump($"{n} bots, back to 2");
            }
        }
        return sb.ToString();
    }).GetAwaiter().GetResult();

    [Fact]
    public void Live_hive_frames_and_tiles_match_the_golden_hashes()
    {
        var rows = new List<(string, string)>
        {
            ("freecam-frames", FreecamDump()),
            ("tile-frames", TileDump()),
            ("bot-tile", BotTileDump()),
            ("layouts", LayoutDump()),
        };
        Compare(rows, Golden);
    }

    [Fact]
    public void Movement_keys_are_the_old_list()
    {
        static bool Old(Key k) => k is Key.W or Key.A or Key.S or Key.D or Key.Q or Key.E or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Space or Key.LeftShift or Key.RightShift or Key.PageUp or Key.PageDown;
        foreach (var k in Enum.GetValues<Key>()) Assert.Equal(Old(k), FreecamKeys.IsMovement(k));
    }

    private const string Golden = """
        freecam-frames 0
        tile-frames 0
        bot-tile 0
        layouts 0
        """;
}
