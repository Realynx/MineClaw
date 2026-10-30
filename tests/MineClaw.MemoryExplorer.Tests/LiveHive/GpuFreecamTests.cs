using System.IO.Compression;
using System.Net;
using System.Text;
using MineClaw.Data;
using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.MemoryExplorer.LiveHive.Gpu;
using MineClaw.MemoryExplorer.Services;
using MineClaw.Render;
using MineClaw.World;
using static MineClaw.MemoryExplorer.Tests.LiveHive.LiveHiveDecoderTests;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

[Collection("avalonia")]
public class GpuFreecamTests
{
    // ------------------------------------------------------------------ mesher

    private static int SolidQuads(IVoxelWorld w, int cx = 0, int sy = 4, int cz = 0)
    {
        var m = new SectionMesher();
        m.Build(w, cx, sy, cz);
        return m.Solid.Quads;
    }

    [Fact]
    public void Mesher_culls_faces_between_solid_neighbours()
    {
        // One stone block: 6 faces. Two touching: 10 (the shared pair is hidden). A 3×3×3 cube: only its 54 outer faces.
        Assert.Equal(6, SolidQuads(new SparseVoxels().Set(4, 68, 4, Blocks.Stone)));
        Assert.Equal(10, SolidQuads(new SparseVoxels().Set(4, 68, 4, Blocks.Stone).Set(5, 68, 4, Blocks.Cobblestone)));
        var cube = new SparseVoxels();
        for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++) for (int z = 0; z < 3; z++) cube.Set(5 + x, 66 + y, 5 + z, Blocks.Stone);
        Assert.Equal(54, SolidQuads(cube));
        // Glass doesn't hide the stone's face (not an opaque cube); the glass face against the stone is hidden: 6 + 5.
        Assert.Equal(11, SolidQuads(new SparseVoxels().Set(4, 68, 4, Blocks.Stone).Set(5, 68, 4, Blocks.Glass)));
        // Cubes on a section's edge are culled against the next section's blocks too.
        var edge = new SparseVoxels().Set(15, 68, 4, Blocks.Stone).Set(16, 68, 4, Blocks.Stone);
        Assert.Equal(5, SolidQuads(edge));
        Assert.Equal(5, SolidQuads(edge, cx: 1));
    }

    [Fact]
    public void Water_goes_to_the_translucent_layer_and_uses_the_shared_fluid_texture()
    {
        var m = new SectionMesher { Textured = true };
        m.Build(new SparseVoxels().Set(3, 70, 3, Blocks.Water).Set(3, 69, 3, Blocks.Stone), 0, 4, 0);
        Assert.Equal(5, m.Translucent.Quads); // top + 4 sides; the bottom rests on stone
        Assert.Equal(6, m.Solid.Quads);       // water is no opaque cube: the stone keeps its top face
        var top = m.Translucent.V[0];
        Assert.Equal(GpuAtlas.ExtraRow, top.Row);
        Assert.True((top.Flags & GpuVertex.Wrap) != 0);
    }

    // ------------------------------------------------------------------ delta rebuilds

    private static ChunkSnapshot Column(params (int X, int Y, int Z, byte Id)[] extra)
    {
        var blocks = new List<(int, int, int, byte)>();
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) blocks.Add((x, 20, z, (byte)Blocks.Stone)); // a floor in section 1
        blocks.Add((5, 40, 5, (byte)Blocks.Stone));
        blocks.Add((15, 40, 5, (byte)Blocks.Stone));
        blocks.Add((5, 47, 5, (byte)Blocks.Stone));
        blocks.Add((5, 60, 5, (byte)Blocks.Stone));
        blocks.AddRange(extra);
        return Snap([.. blocks]);
    }

    private static (HiveWorldCache, SectionStore) World()
    {
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, Column()), Entry(1, 0, Column()), Entry(-1, 0, Column()), Entry(0, 1, Column()), Entry(0, -1, Column())]));
        var store = new SectionStore(cache) { Log = [] };
        while (store.Update(0, 0, 0, 2) > 0) { }
        store.Log!.Clear();
        return (cache, store);
    }

    private static List<SectionKey> Rebuild(HiveWorldCache cache, SectionStore store, ChunkSnapshot col)
    {
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, col)]));
        while (store.Update(0, 0, 0, 2) > 0) { }
        var log = store.Log!.OrderBy(k => (k.Cx, k.Sy, k.Cz)).ToList();
        store.Log.Clear();
        return log;
    }

    [Fact]
    public void A_block_change_rebuilds_only_the_sections_it_touches()
    {
        var (cache, store) = World();
        Assert.Empty(Rebuild(cache, store, Column())); // same blocks, a new object: nothing to rebuild

        // Inside a section, same opaque top: that section only.
        var a = Snap([.. ColumnBlocks(), (5, 40, 5, (byte)Blocks.Cobblestone)]);
        Assert.Equal([new SectionKey(0, 2, 0)], Rebuild(cache, store, a));

        // On the section's top layer: also the section above.
        var b = Snap([.. ColumnBlocks(), (5, 40, 5, (byte)Blocks.Cobblestone), (5, 47, 5, (byte)Blocks.Cobblestone)]);
        Assert.Equal([new SectionKey(0, 2, 0), new SectionKey(0, 3, 0)], Rebuild(cache, store, b));

        // On the column's east edge: also the east neighbour's facing section.
        var c = Snap([.. ColumnBlocks(), (5, 40, 5, (byte)Blocks.Cobblestone), (5, 47, 5, (byte)Blocks.Cobblestone), (15, 40, 5, (byte)Blocks.Cobblestone)]);
        Assert.Equal([new SectionKey(0, 2, 0), new SectionKey(1, 2, 0)], Rebuild(cache, store, c));

        // Unrelated columns and sections were never touched.
        Assert.True(store.Built > 0);
    }

    private static List<(int, int, int, byte)> ColumnBlocks()
    {
        var blocks = new List<(int, int, int, byte)>();
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) blocks.Add((x, 20, z, (byte)Blocks.Stone));
        blocks.Add((5, 40, 5, (byte)Blocks.Stone));
        blocks.Add((15, 40, 5, (byte)Blocks.Stone));
        blocks.Add((5, 47, 5, (byte)Blocks.Stone));
        blocks.Add((5, 60, 5, (byte)Blocks.Stone));
        return blocks;
    }

    [Fact]
    public void A_neighbour_column_appearing_rebuilds_the_bordering_sections()
    {
        var cache = new HiveWorldCache();
        cache.Apply(ColumnPayload.Encode(0, [Entry(0, 0, Column())]));
        var store = new SectionStore(cache) { Log = [] };
        while (store.Update(0, 0, 0, 2) > 0) { }
        store.Log!.Clear();
        cache.Apply(ColumnPayload.Encode(0, [Entry(1, 0, Column())]));
        while (store.Update(0, 0, 0, 2) > 0) { }
        // The new column's own sections, and the old column's non-empty ones (their east faces are now culled).
        Assert.Contains(new SectionKey(1, 1, 0), store.Log);
        Assert.Contains(new SectionKey(0, 1, 0), store.Log);
        Assert.DoesNotContain(store.Log, k => k.Cx == 0 && k.Sy is 0 or 4 or 5 or 6 or 7); // empty sections stay empty
    }

    // ------------------------------------------------------------------ per-frame allocations

    [Fact]
    public void Preparing_a_frame_does_not_allocate()
    {
        var (cache, store) = World();
        var scene = new FrameScene(cache) { Textured = false, Flags = Overlay.Default };
        while (store.Ready.TryDequeue(out var up))
        {
            if (up.SolidCount + up.TranslucentCount > 0)
                scene.Sections[up.Key] = new GpuSection { Key = up.Key, SolidVbo = 1, SolidCount = up.SolidCount, TransVbo = 2, TransCount = up.TranslucentCount };
            up.Release();
        }
        Assert.NotEmpty(scene.Sections);
        var ents = new EntityStore();
        ents.Apply(new EntitiesDto(0, [], [
            new EntityDto("item", "cobblestone", 0, 4.5, 21.1, 4.5, Id: 1, Item: 4, Count: 12, Age: 3),
            new EntityDto("item", "stick", 0, 6.5, 21.1, 3.5, Id: 2, Item: 280, Count: 1, Age: 9),
            new EntityDto("mob", "zombie", 0, 8, 21, 8, Id: 3, Type: 54, Yaw: 30, Flags: 1),
            new EntityDto("player", "PoofImaFox", 0, 2, 21, 9, Id: 4, Yaw: 200),
        ]));
        scene.Entities = ents;
        scene.ModelRefreshMs = 1e9; // posing (the shared geometry) is throttled; this measures the per-frame path
        BotDto[] bots = [new("maid_chan", "main", true, 0, 3, 21, 3, 10, 0, null, 20, 20, null, null, null, [], true, 0)];
        scene.Bots = () => bots;
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 20; i++) scene.Prepare(new CamPose(8 + i * 0.01, 30, -6, i, 30, 0), 1280, 720, now); // warm-up: lists grow
        Assert.True(scene.EntitiesDrawn > 0);
        Assert.NotEmpty(scene.SolidDraw);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        const int frames = 300;
        for (int i = 0; i < frames; i++) scene.Prepare(new CamPose(8 + i * 0.01, 30, -6, 20 + i * 0.3f, 30, 0), 1280, 720, now);
        long perFrame = (GC.GetAllocatedBytesForCurrentThread() - a0) / frames;
        Assert.True(perFrame < 256, $"{perFrame} bytes allocated per frame");
    }

    // ------------------------------------------------------------------ weather and x-ray (live 10-29)

    private static FrameScene Scene(HiveWorldCache cache, params BotDto[] bots)
    {
        var scene = new FrameScene(cache) { Textured = false, Flags = Overlay.Default };
        scene.Bots = () => bots;
        return scene;
    }

    private static BotDto Bot(double x, double y, double z) => new("maid_chan", "main", true, 0, x, y, z, 10, 0, null, 20, 20, null, null, null, [], true, 0);

    [Fact]
    public void Rain_streaks_fall_above_the_ground_around_the_camera_and_cost_no_allocations()
    {
        var (cache, _) = World();
        cache.SetWeather(0, 1f, 0f);
        Assert.Equal(new Weather(1, 0), cache.WeatherIn(0));
        Assert.Equal(Weather.Clear, cache.WeatherIn(-1)); // another dimension: clear
        var scene = Scene(cache);
        scene.Weather = cache.WeatherIn(0);
        var pose = new CamPose(8, 24, 8, 0, 30, 0);
        scene.Prepare(pose, 640, 360, DateTimeOffset.UtcNow);
        Assert.True(scene.Precipitation.Count >= 40, $"{scene.Precipitation.Count / 2} streaks");
        Assert.Equal(0, scene.Precipitation.Count % 2);
        for (int i = 0; i < scene.Precipitation.Count; i++)
        {
            var v = scene.Precipitation.V[i];
            Assert.True(v.Y + pose.Y > 20.0, $"a streak under the stone floor at y {v.Y + pose.Y:0.0}");
            Assert.InRange(Math.Abs(v.X), 0, 12);
        }
        // The sky greys with the weather; clear weather draws nothing.
        Assert.NotEqual(EyeRenderer.SkyColours(0, false).Top, scene.Sky.Top);
        scene.Weather = Weather.Clear;
        scene.Prepare(pose, 640, 360, DateTimeOffset.UtcNow);
        Assert.Equal(0, scene.Precipitation.Count);
        // Per-frame cost while it rains stays allocation-free (the column tops are cached per camera cell).
        scene.Weather = new Weather(1, 1);
        for (int i = 0; i < 5; i++) scene.Prepare(pose, 640, 360, DateTimeOffset.UtcNow);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) scene.Prepare(pose, 640, 360, DateTimeOffset.UtcNow);
        Assert.True((GC.GetAllocatedBytesForCurrentThread() - a0) / 50 < 256);
    }

    [Fact]
    public void The_freecam_x_ray_is_off_unless_toggled_and_cuts_only_toward_bodies_in_front()
    {
        var (cache, _) = World();
        var scene = Scene(cache, Bot(8.5, 21, 18.5));
        var looking = new CamPose(8.5, 30, 2, 0, 30, 0);  // yaw 0 = facing south, toward the bot
        scene.Prepare(looking, 320, 240, DateTimeOffset.UtcNow);
        Assert.Empty(scene.XRaySubjects); // exact by default
        scene.XRay = true;
        scene.Prepare(looking, 320, 240, DateTimeOffset.UtcNow);
        var s = Assert.Single(scene.XRaySubjects);
        Assert.Equal(new System.Numerics.Vector3(0, 21.9f - 30, 16.5f), s);
        Assert.Equal(21 - 30, scene.XRayFloor, 3);
        scene.Prepare(looking with { Yaw = 180 }, 320, 240, DateTimeOffset.UtcNow); // facing north: the bot is behind
        Assert.Empty(scene.XRaySubjects);
        // The CPU freecam's cut is the same cone.
        var cut = FreecamXRay.Cut(looking, [Bot(8.5, 21, 18.5)])!;
        Assert.True(cut.Hides(8, 26, 10));   // on the line from the camera to the bot
        Assert.False(cut.Hides(8, 20, 18));  // the floor under the bot
        Assert.Null(FreecamXRay.Cut(looking with { Yaw = 180 }, [Bot(8.5, 21, 18.5)]));
    }

    // ------------------------------------------------------------------ the sync worker

    private sealed class FakeSupervisor(HiveWorldCache cache) : HttpMessageHandler
    {
        public readonly List<int> Threads = [];
        public volatile int Chunks;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
            var path = r.RequestUri!.AbsolutePath;
            byte[] body;
            string type = "application/json";
            if (path.EndsWith("/entities")) body = Encoding.UTF8.GetBytes("""{"at":1,"rev":3,"full":true,"bots":[{"name":"maid_chan","short":"main","main":true,"dim":0,"x":8,"y":21,"z":8,"hp":20,"food":20,"claims":[],"live":true,"age_seconds":0}],"entities":[{"kind":"item","name":"cobblestone","dim":0,"x":1,"y":21,"z":1,"id":7,"item":4,"count":2,"flags":1}],"removed":[]}""");
            else if (path.EndsWith("/index"))
            {
                var s = Column();
                body = Encoding.UTF8.GetBytes($$"""{"dim":0,"at":1,"columns":[{"cx":0,"cz":0,"hash":{{s.Hash}},"seen":1,"by":"maid_chan","live":true}]}""");
            }
            else if (path.EndsWith("/chunks")) { body = ColumnPayload.Encode(0, [Entry(0, 0, Column())]); type = "application/octet-stream"; Chunks++; }
            else body = Encoding.UTF8.GetBytes("""{"at":"2026-01-01T00:00:00Z","dim":0,"claims":[],"places":[],"points":[],"plans":[],"dangers":[],"notes":[]}""");
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task The_sync_worker_never_runs_on_the_ui_thread()
    {
        var cache = new HiveWorldCache();
        var fake = new FakeSupervisor(cache);
        var http = new HttpClient(fake) { BaseAddress = new Uri("http://hive.test/") };
        using var api = new HiveApi(new SupervisorEndpoint("http://hive.test/"), http);
        long uiBefore = Interlocked.Read(ref FreecamStats.Shared.SyncOnUiThread);
        using var sync = new HiveSync(api, cache) { Interests = () => [new Interest(0, 8, 8, 32, 50)] };
        // Started from Avalonia's UI thread (the headless session's dispatcher), as the app does.
        int ui = await HeadlessUi.Run(() => { FreecamStats.Shared.UiThreadId = Environment.CurrentManagedThreadId; sync.Start(); return Environment.CurrentManagedThreadId; });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((cache.Count == 0 || sync.Entities == null) && DateTime.UtcNow < deadline) await Task.Delay(20).ConfigureAwait(false);
        sync.Stop();
        Assert.True(cache.Count > 0, "no column applied");
        Assert.NotNull(sync.Entities);
        lock (fake.Threads) Assert.DoesNotContain(ui, fake.Threads);
        Assert.Equal(uiBefore, Interlocked.Read(ref FreecamStats.Shared.SyncOnUiThread));
        Assert.Equal(1, sync.Store.Count);                 // the delta stream fed the entity store
        var views = new List<EntityView>();
        sync.Store.Interpolated(0, views);
        Assert.Equal(EntityDto.Burning, views[0].Flags);    // the flags byte made it through
    }

    // ------------------------------------------------------------------ textures

    /// <summary>A minimal RGBA (colour type 6) PNG, so alpha is part of what's compared.</summary>
    private static byte[] RgbaPng(int w, int h, Func<int, int, (byte R, byte G, byte B, byte A)> px)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < w; x++) { var p = px(x, y); raw.Write([p.R, p.G, p.B, p.A]); }
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, leaveOpen: true)) raw.WriteTo(zs);
        var o = new MemoryStream();
        o.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string t, byte[] d)
        {
            o.Write([(byte)(d.Length >> 24), (byte)(d.Length >> 16), (byte)(d.Length >> 8), (byte)d.Length]);
            var td = Encoding.ASCII.GetBytes(t).Concat(d).ToArray();
            o.Write(td);
            uint c = Crc(td);
            o.Write([(byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c]);
        }
        Chunk("IHDR", [(byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 8, 6, 0, 0, 0]);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", []);
        return o.ToArray();
    }

    private static uint Crc(byte[] d)
    {
        uint c = 0xffffffff;
        foreach (byte b in d)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    [Fact]
    public void An_uploaded_skin_texel_is_the_source_pngs_rgba()
    {
        static (byte, byte, byte, byte) Skin(int x, int y) => ((byte)(x * 4), (byte)(y * 8), (byte)(255 - x), (byte)(x < 8 && y < 8 ? 0 : 200 + y % 50));
        var skinPng = RgbaPng(64, 32, Skin);
        var terrain = new uint[256 * 256];
        Array.Fill(terrain, 0xff808080);
        var terrainPng = Png.Encode(terrain, 256, 256);
        var atlas = TextureAtlas.FromPng(terrainPng, null, skinPng);
        var (w, _, rgba) = GpuAtlas.Build(atlas);
        var (col, row) = GpuAtlas.EntityCell(0); // char.png
        int t = atlas.Tile;
        foreach (var (x, y) in new[] { (0, 0), (9, 9), (20, 22), (44, 20), (63, 31), (40, 8) })
        {
            var (r, g, b, a) = Skin(x, y);
            int i = ((row * t + y) * w + col * t + x) * 4;
            Assert.Equal((r, g, b, a), (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3])); // GL_RGBA bytes, no BGRA swap
            Assert.Equal(atlas.Skin!.Value.Px[y * 64 + x], GpuAtlas.Texel(rgba, w, col * t + x, row * t + y));
        }
    }

    [Fact]
    public void Entity_flags_reach_the_shared_model_burning_and_sneaking()
    {
        var dto = HiveJson.Parse<EntitiesDto>("""{"at":1,"bots":[],"entities":[{"kind":"mob","name":"zombie","dim":0,"x":1,"y":64,"z":1,"id":9,"flags":3,"variant":2}]}"""u8.ToArray())!;
        Assert.Equal(3, dto.Entities[0].Flags);
        var v = FrameScene.Vision(dto.Entities[0]);
        Assert.True(v.Burning);
        Assert.True(v.Sneaking);
        Assert.Equal(2, v.Variant);
        var q = GeoQuad.Make(default, default, default, default, default, default, default, default);
        q.Tex = QuadTex.Fire; q.Tile = 1;
        var list = new VertexList();
        QuadEmit.Emit(list, q, default, 1, true);
        Assert.Equal((GpuAtlas.FireB, GpuAtlas.ExtraRow), (list.V[0].Col, list.V[0].Row));
    }

    [Fact]
    public void Entity_quads_map_onto_the_entity_texture_block()
    {
        var q = GeoQuad.Make(default, default, default, default, new(0.5f, 0.25f), new(1, 1), default, default);
        q.Tex = QuadTex.Entity; q.Tile = 0;
        var list = new VertexList();
        QuadEmit.Emit(list, q, default, 1, true);
        var v = list.V[0];
        Assert.Equal(GpuAtlas.EntityCell(0), (v.Col, v.Row));
        Assert.Equal((2f, 0.5f), (v.U, v.V)); // normalised image UV × the 4×2-cell block
        Assert.True((v.Flags & GpuVertex.Span) != 0);
    }
}
