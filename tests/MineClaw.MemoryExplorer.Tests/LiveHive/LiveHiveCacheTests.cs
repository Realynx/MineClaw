using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Render;
using MineClaw.World;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

public class LiveHiveCacheTests
{
    private static ColumnInfoDto Info(int cx, int cz, long hash, long seen = 1000, string? by = "maid_chan", bool live = false) => new(cx, cz, hash, seen, by, live);

    [Fact]
    public void Only_new_or_changed_columns_are_fetched()
    {
        var cache = new HiveWorldCache();
        var a = Snap((1, 64, 1, 4));
        var b = Snap((2, 64, 2, 1));

        var want = cache.Reconcile(0, -1, -1, 1, 1, [Info(0, 0, a.Hash), Info(1, 0, b.Hash)]);
        Assert.Equal([(0, 0), (1, 0)], want);
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, a, 1000), Entry(1, 0, b, 1000)]));
        Assert.Equal(2, cache.Count);

        // Same hashes, newer seen: nothing to fetch, seen/by refreshed in place.
        long v = cache.Version;
        Assert.Empty(cache.Reconcile(0, -1, -1, 1, 1, [Info(0, 0, a.Hash, 5000, "maid_chan_67"), Info(1, 0, b.Hash, 1000)]));
        Assert.Equal(5000, cache.Get(0, 0, 0)!.SeenMs);
        Assert.Equal("maid_chan_67", cache.Get(0, 0, 0)!.By);
        Assert.True(cache.Version > v);

        // A changed column is fetched again; an unchanged index changes nothing.
        var a2 = Snap((1, 64, 1, 4), (1, 65, 1, 4));
        Assert.Equal([(0, 0)], cache.Reconcile(0, -1, -1, 1, 1, [Info(0, 0, a2.Hash, 6000), Info(1, 0, b.Hash)]));
        v = cache.Version;
        Assert.Equal([(0, 0)], cache.Reconcile(0, -1, -1, 1, 1, [Info(0, 0, a2.Hash, 6000), Info(1, 0, b.Hash)]));
        Assert.Equal(v, cache.Version);
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, a2, 6000)]));
        Assert.Equal(a2.Hash, cache.Get(0, 0, 0)!.Hash);
        Assert.Empty(cache.Reconcile(0, -1, -1, 1, 1, [Info(0, 0, a2.Hash, 6000), Info(1, 0, b.Hash)]));
    }

    [Fact]
    public void Columns_the_index_drops_are_forgotten_inside_its_rectangle_only()
    {
        var cache = new HiveWorldCache();
        var s = Snap((0, 1, 0, 1));
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, s), Entry(1, 1, s), Entry(9, 9, s)]));
        cache.Apply(ColumnPayload.Encode(-1, [Entry(0, 0, s)]));
        Assert.Empty(cache.Reconcile(0, -1, -1, 2, 2, [Info(0, 0, s.Hash)])); // (1,1) evicted on the server
        Assert.Null(cache.Get(0, 1, 1));
        Assert.NotNull(cache.Get(0, 9, 9));   // outside the rectangle: untouched
        Assert.NotNull(cache.Get(-1, 0, 0));  // other dimension: untouched
        // A payload's "missing" entry drops the column too.
        cache.Apply(ColumnPayload.Encode(0, [new ColumnPayloadEntry(0, 0, 0, 0, null, false, [])]));
        Assert.Null(cache.Get(0, 0, 0));
    }

    [Fact]
    public void Trim_keeps_the_nearest_columns()
    {
        var cache = new HiveWorldCache();
        var s = Snap((0, 1, 0, 1));
        cache.Apply(ColumnPayload.Encode(0, Enumerable.Range(0, 10).Select(i => Entry(i, 0, s)).ToList()));
        Assert.Equal(6, cache.Trim(0, 0, 0, 4));
        Assert.Equal(4, cache.CountIn(0));
        Assert.NotNull(cache.Get(0, 3, 0));
        Assert.Null(cache.Get(0, 4, 0));
    }

    [Fact]
    public void Voxels_read_blocks_light_and_staleness()
    {
        var cache = new HiveWorldCache();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(10_000_000);
        var fresh = Snap((3, 64, 4, 2), (3, 60, 4, 1));
        var old = Snap((0, 70, 0, 4));
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, fresh, now.ToUnixTimeMilliseconds() - 1000), Entry(-1, 0, old, now.ToUnixTimeMilliseconds() - 3_600_000)]));
        var v = new HiveVoxels(cache);
        v.Prepare(0, 8, 8, 2, now);
        Assert.Equal(2, v.Block(3, 64, 4));
        Assert.Equal(3, v.Meta(3, 64, 4));
        Assert.Equal(4, v.Block(-16, 70, 0));
        Assert.Equal(0, v.Block(100, 64, 100)); // unknown: air
        Assert.Equal(ColumnState.Live, v.State(3, 4));
        Assert.Equal(ColumnState.Remembered, v.State(-16, 0));
        Assert.Equal(ColumnState.Unknown, v.State(100, 100));
        Assert.Equal(15, v.SkyLight(3, 65, 4));  // open sky above the top
        Assert.Equal(-1, v.SkyLight(3, 62, 4));  // under it: unknown (even shade)
        Assert.Equal(-1, v.BlockLight(3, 62, 4));
        // Out of the prepared square still works (dictionary path).
        Assert.Equal(4, new HiveVoxels(cache).Block(-16, 70, 0));
    }

    [Fact]
    public void Camera_moves_along_its_heading()
    {
        var cam = new FlyCamera { Speed = 10 };
        cam.Set(new CamPose(0, 64, 0, 0, 0, 0)); // facing south (+z)
        cam.Move(1, 0, 0, 1, false);
        Assert.Equal(10, cam.Pose.Z, 6);
        Assert.Equal(0, cam.Pose.X, 6);
        cam.Move(0, 1, 0, 1, false); // strafe right while facing south = west (−x)
        Assert.Equal(-10, cam.Pose.X, 6);
        cam.Move(0, 0, 1, 0.5, true); // up, fast: ×4
        Assert.Equal(84, cam.Pose.Y, 6);
        cam.Set(new CamPose(0, 64, 0, 90, 0, 0)); // facing west
        cam.Move(1, 0, 0, 1, false);
        Assert.Equal(-10, cam.Pose.X, 6);
        long r = cam.Revision;
        cam.Look(100, 1000);
        Assert.True(cam.Revision > r);
        Assert.Equal(89.9f, cam.Pose.Pitch, 3);
        Assert.Equal((90 + 100 * cam.Sensitivity) % 360, cam.Pose.Yaw, 3);
        Assert.False(cam.Move(0, 0, 0, 1, false));
    }

    [Fact]
    public void Projector_matches_the_raycaster()
    {
        var p = new ViewProjector(new CamPose(0.5, 65, 0.5, 0, 0, 0), 320, 240);
        Assert.True(p.Project(0.5, 65, 10.5, out var sx, out var sy, out var d));
        Assert.Equal(160, sx, 1);
        Assert.Equal(120, sy, 1);
        Assert.Equal(10, d, 6);
        Assert.False(p.Project(0.5, 65, -5, out _, out _, out _)); // behind
        var (dx, dy, dz) = p.Ray(160, 120);
        Assert.True(dz > 0.99 && Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01);
        Assert.True(p.Project(5.5, 65, 10.5, out var rx, out _, out _)); // +x is to the left when facing south
        Assert.True(rx < 160);
    }

    [Fact]
    public void Engine_renders_the_cached_world_with_overlays()
    {
        var cache = new HiveWorldCache();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var floor = Enumerable.Range(0, 16).SelectMany(x => Enumerable.Range(0, 16).Select(z => (x, 63, z, (byte)1))).ToArray();
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, Snap(floor), now)]));
        var cam = new FlyCamera();
        cam.Set(new CamPose(8, 70, 2, 0, 30, 0));
        var bots = new[] { new BotDto("maid_chan", "main", true, 0, 8, 64, 9, 0, 0, 0, 20, 20, null, null, null, [], true, 0) };
        var ov = new OverlaysDto(DateTimeOffset.UtcNow, 0, [new MarkDto("block", "block 8,63,10", [8, 63, 10], [8, 63, 10], "maid_chan_67")], [], [], [], [], []);
        using var eng = new FreecamEngine(cache, cam, () => null) { Bots = () => bots, Overlays = () => ov, Threads = 2 };
        var f = eng.RenderOnce(160, 90);
        Assert.Equal((160, 90), (f.W, f.H));
        Assert.True(f.Px.Length >= 160 * 90); // grow-only buffer: the moving and still sizes share it
        Assert.All(f.Px.Take(100), px => Assert.Equal(0xFF, (px >> 24) & 0xFF)); // opaque BGRA
        Assert.Contains(f.Labels, l => l.Text == "main");
        Assert.Contains(f.Labels, l => l.Text.StartsWith("67:", StringComparison.Ordinal));
        Assert.Contains(f.Px, px => (px & 0xFFFFFF) == OverlayPainter.ClaimColour); // the claim box edge is drawn
    }

    [Fact]
    public void Staleness_alpha_ramps_with_age()
    {
        Assert.Equal(0, OverlayPainter.AgeAlpha(30_000));
        float min = OverlayPainter.AgeAlpha(120_000), hour = OverlayPainter.AgeAlpha(3_600_000), day = OverlayPainter.AgeAlpha(86_400_000 * 3L);
        Assert.True(min > 0 && min < hour && hour < day);
        Assert.Equal(0.42f, day, 3);
    }
}
