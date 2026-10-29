using System.Collections.Concurrent;
using MineClaw.Client.Versioning;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Release1122;
using MineClaw.Protocol.Versioning;
using MineClaw.World;
using MineClaw.World.Chunks;
using Xunit.Abstractions;

namespace MineClaw.Client.Tests;

[CollectionDefinition(nameof(BodyMemoryCollection), DisableParallelization = true)]
public sealed class BodyMemoryCollection;

/// <summary>
/// Managed memory per in-process body: K 1.12.2 clients, each on its own in-memory connection to its own raw 1.12.2 server
/// that streams a (2r+1)² square of realistic columns around spawn (bedrock, stone with ore and granite/diorite patches up to
/// y ≈ 62..66, dirt, grass, air above; server-style light: sky 15 above the ground and 0 below, no block light), so 5 of the
/// 16 sections of every 256-high column are non-empty. Measures <see cref="GC.GetTotalMemory"/> after 1 body and after K
/// and reports MB per extra body, with private columns or one <see cref="SharedChunkStore"/> for all the bodies (all near the
/// same spot). Runs alone (no parallel tests allocating beside it).
/// <para>Measured with 4 bodies (MB per extra body): eager 256 KiB columns (before) r 8 = 72.9, r 4 = 20.6 (262 656 B per
/// column); lazy sections r 8 = 16.6, r 4 = 4.8 (≈ 59 200 B per column: 14 of 64 sections); lazy + shared store r 4 = 0.25,
/// r 3 = 0.24. The guard below fails if sharing or lazy sections regress (lazy alone at r 4 is already above it).</para>
/// </summary>
[Collection(nameof(BodyMemoryCollection))]
public class BodyMemoryBenchmarkTests(ITestOutputHelper output)
{
    private const int Bodies = 4;

    /// <summary>Regression guard: managed MB per extra body at view distance 4 with lazy sections and the shared store.</summary>
    public const double TargetPerExtraBodyMbR4Shared = 3;

    [Theory]
    [InlineData(8, false)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(3, true)]
    public async Task Managed_memory_per_extra_body(int radius, bool shared)
    {
        var r = await MeasureAsync(radius, shared);
        output.WriteLine(r.ToString());
        if (radius == 4 && shared)
            Assert.True(r.PerExtraBodyMb <= TargetPerExtraBodyMbR4Shared,
                $"{r.PerExtraBodyMb:F2} MB per extra body at r 4 + shared store (target {TargetPerExtraBodyMbR4Shared} MB)");
    }

    private sealed record Result(int Radius, bool Shared, int Columns, double FirstBodyMb, double PerExtraBodyMb, long BytesPerColumn,
        string Raw, string? Store)
    {
        public override string ToString() =>
            $"r={Radius} {(Shared ? "lazy+shared" : "lazy")} ({Columns} columns/body): first body {FirstBodyMb:F1} MB, " +
            $"per extra body {PerExtraBodyMb:F2} MB, ~{BytesPerColumn} B per column; {Raw}{(Store is null ? "" : "; store " + Store)}";
    }

    private async Task<Result> MeasureAsync(int radius, bool shared)
    {
        int columns = (2 * radius + 1) * (2 * radius + 1);
        var bodies = Terrain.Bodies(radius);
        // Warm up the statics (lang table, game data, light tables, codecs) with a throwaway body.
        await using (var warm = await Body.StartAsync("warmup", bodies, radius, null)) { }
        await Task.Delay(1000); // the warm-up body's threads wind down

        var store = shared ? new SharedChunkStore(ChunkColumn.MaxHeight) : null;
        var all = new List<Body>();
        try
        {
            long m0 = Measure();
            all.Add(await Body.StartAsync("body0", bodies, radius, store));
            long m1 = Measure();
            for (int i = 1; i < Bodies; i++) all.Add(await Body.StartAsync("body" + i, bodies, radius, store));
            long mk = Measure();
            foreach (var b in all)
            {
                Assert.Equal(columns, b.Client.World.LoadedColumnCount);
                Assert.Equal(radius, b.ViewDistanceSent); // ClientOptions.ViewDistance went out in Client Settings
            }
            if (store is not null)
            {
                Assert.Equal(columns, store.ColumnCount); // one copy for all the bodies
                Assert.Same(all[0].Client.World.GetColumn(0, 0), all[^1].Client.World.GetColumn(0, 0));
            }
            var w = all[0].Client.World;
            long perColumn = w.Columns.Sum(c => c.ApproximateBytes) / w.LoadedColumnCount;
            string raw = $"m0={m0 / 1048576.0:F1} m1={m1 / 1048576.0:F1} m{Bodies}={mk / 1048576.0:F1} MB";
            return new Result(radius, shared, columns, (m1 - m0) / 1048576.0, (mk - m1) / (double)(Bodies - 1) / 1048576.0, perColumn,
                raw, store?.Stats().ToString());
        }
        finally
        {
            foreach (var b in all) await b.DisposeAsync();
            if (store is not null) Assert.Equal(0, store.ColumnCount); // every body gave its columns back
        }
    }

    private static long Measure()
    {
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        return GC.GetTotalMemory(true);
    }

    /// <summary>The test terrain: Chunk Data bodies (after the packet id) around chunk (0, 0).</summary>
    private static class Terrain
    {
        private static readonly ConcurrentDictionary<int, Dictionary<(int, int), byte[]>> Cache = new();

        public static Dictionary<(int, int), byte[]> Bodies(int radius) => Cache.GetOrAdd(radius, r =>
        {
            var d = new Dictionary<(int, int), byte[]>();
            for (int cx = -r; cx <= r; cx++)
            for (int cz = -r; cz <= r; cz++)
                d[(cx, cz)] = Column(cx, cz);
            return d;
        });

        private static uint Hash(int x, int y, int z)
        {
            uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        public static int Ground(int x, int z) => 62 + (int)(Hash(x, 0, z) % 5);

        private static int State(int x, int y, int z, int ground)
        {
            if (y == 0) return Blocks.Bedrock << 4;
            if (y > ground) return 0;
            if (y == ground) return Blocks.Grass << 4;
            if (y >= ground - 3) return Blocks.Dirt << 4;
            uint h = Hash(x, y, z) % 1000;
            if (h < 8) return Blocks.CoalOre << 4;
            if (h < 12) return Blocks.IronOre << 4;
            if (h < 60) return Blocks.Stone << 4 | 1; // granite
            if (h < 100) return Blocks.Stone << 4 | 3; // diorite
            return Blocks.Stone << 4;
        }

        private static byte[] Column(int cx, int cz)
        {
            var data = new WireWriter(65536);
            int mask = 0;
            var states = new int[ChunkData.SectionCells];
            for (int s = 0; s < 5; s++)
            {
                mask |= 1 << s;
                var block = new byte[ChunkData.SectionLightBytes];
                var sky = new byte[ChunkData.SectionLightBytes];
                for (int i = 0; i < ChunkData.SectionCells; i++)
                {
                    int lx = i & 15, lz = i >> 4 & 15, y = s * 16 + (i >> 8);
                    int x = cx * 16 + lx, z = cz * 16 + lz;
                    int g = Ground(x, z);
                    states[i] = State(x, y, z, g);
                    int sl = y > g ? 15 : 0;
                    sky[i >> 1] |= (byte)((i & 1) == 0 ? sl : sl << 4);
                }
                ChunkData.WriteStates(data, states, 4);
                data.WriteBytes(block);
                data.WriteBytes(sky);
            }
            var biomes = new byte[256];
            Array.Fill(biomes, (byte)1);
            data.WriteBytes(biomes);
            var w = new WireWriter(data.Length + 32);
            w.WriteInt(cx).WriteInt(cz).WriteBool(true).WriteVarInt(mask).WriteVarInt(data.Length).WriteBytes(data.Span);
            w.WriteVarInt(0);
            return w.ToArray();
        }
    }

    /// <summary>One body: a 1.12.2 client and its raw server (login, the chunk square, the spawn teleport; then it drains).</summary>
    private sealed class Body : IAsyncDisposable
    {
        public required MinecraftClient Client { get; init; }
        public required SegmentPipe ToClient { get; init; }
        public required SegmentPipe ToServer { get; init; }
        public required Thread Server { get; init; }
        public int? ViewDistanceSent;

        public static async Task<Body> StartAsync(string name, Dictionary<(int, int), byte[]> columns, int radius, SharedChunkStore? store)
        {
            var toClient = new SegmentPipe();
            var toServer = new SegmentPipe();
            var clientEnd = new PipeStream(toClient, toServer);
            var serverEnd = new PipeStream(toServer, toClient);
            // The 1.12.2 profile with an adapter that "connects" to the pipe: the client goes through Connect, so
            // ClientOptions.ViewDistance reaches the wire as it does over TCP.
            var j = GameVersionProfiles.Java1122;
            var adapter = new PipeAdapter(new Release1122ProtocolAdapter { Lang = () => LangTable.Builtin }, clientEnd);
            var profile = new GameVersionProfile(j.Version, adapter, j.Physics, j.Combat, j.Hunger, j.Data, j.Inventory, j.ItemRules, j.Entities);
            var client = new MinecraftClient(new ClientOptions
            {
                Username = name, Profile = profile, ViewDistance = radius, ChunkStore = store,
                TickInterval = TimeSpan.FromMilliseconds(50), ChatInterval = TimeSpan.Zero, HostileAlertRadius = 0,
                LoginTimeout = TimeSpan.FromSeconds(60),
            });
            Body? body = null;
            var thread = new Thread(() => Serve(serverEnd, name, columns, v => body!.ViewDistanceSent = v)) { IsBackground = true, Name = "bench-server-" + name };
            body = new Body { Client = client, ToClient = toClient, ToServer = toServer, Server = thread };
            thread.Start();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60))) await client.ConnectAsync(cts.Token);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (client.World.LoadedColumnCount < columns.Count)
            {
                if (sw.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException($"{name}: {client.World.LoadedColumnCount}/{columns.Count} columns");
                await Task.Delay(20);
            }
            return body;
        }

        private static void Serve(Stream s, string name, Dictionary<(int, int), byte[]> columns, Action<int> viewDistance)
        {
            var framing = new Framing();
            void Send(int id, Action<WireWriter> b)
            {
                var w = WireWriter.ForPacket(id);
                b(w);
                s.Write(framing.Encode(w.Span));
            }
            (int Id, byte[] Body) Read()
            {
                var payload = framing.Read(s) ?? throw new EndOfStreamException();
                var r = new WireReader(payload);
                int id = r.ReadVarInt();
                return (id, payload[r.Position..]);
            }
            try
            {
                Read(); // handshake
                Read(); // login start
                Send(0x03, w => w.WriteVarInt(256));
                framing.Threshold = 256;
                Send(0x02, w => w.WriteString(Wire.OfflineUuid(name).ToString()).WriteString(name));
                Send(0x23, w => w.WriteInt(77).WriteUByte(0).WriteInt(0).WriteUByte(2).WriteUByte(20).WriteString("default").WriteBool(false));
                var settings = Read();
                if (settings.Id == 0x04)
                {
                    var r = new WireReader(settings.Body);
                    r.ReadString(16);
                    viewDistance(r.ReadSByte());
                }
                Read(); // MC|Brand
                int ground = Terrain.Ground(8, 8);
                Send(0x46, w => w.WritePosition(8, ground + 1, 8));
                Send(0x47, w => w.WriteLong(1000).WriteLong(6000));
                Send(0x41, w => w.WriteFloat(20).WriteVarInt(20).WriteFloat(5));
                foreach (var body in columns.Values) Send(0x20, w => w.WriteBytes(body));
                Send(0x2F, w => w.WriteDouble(8.5).WriteDouble(ground + 1).WriteDouble(8.5).WriteFloat(0).WriteFloat(0).WriteUByte(0).WriteVarInt(1));
                while (true) Read(); // drain the client's movement until the pipe closes
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException or TimeoutException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            ToClient.Close();
            ToServer.Close();
            Server.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>A protocol adapter whose Connect opens the in-memory pipe (Open, LoginStart and the view distance copy pass through).</summary>
    private sealed class PipeAdapter(IProtocolAdapter inner, Stream clientEnd) : IProtocolAdapter
    {
        public GameVersion Version => inner.Version;
        public int MaxChatLength => inner.MaxChatLength;
        public IGameConnection Connect(string host, int port, TimeSpan? connectTimeout = null) => inner.Open(clientEnd);
        public IGameConnection Open(Stream stream, PacketDirection inbound = PacketDirection.ClientBound, bool ownsStream = true) =>
            inner.Open(stream, inbound, ownsStream);
        public IReadOnlyList<IPacket> LoginStart(string username) => inner.LoginStart(username);
        public IProtocolAdapter WithViewDistance(int chunks) => new PipeAdapter(inner.WithViewDistance(chunks), clientEnd);
    }

    /// <summary>An in-memory byte pipe that frees what was read (the test's <see cref="BytePipe"/> keeps a queue whose buffer
    /// stays at its peak size, which would count as a body's memory).</summary>
    internal sealed class SegmentPipe
    {
        private readonly Queue<byte[]> _segments = new();
        private byte[]? _current;
        private int _offset;
        private bool _closed;

        public void Write(ReadOnlySpan<byte> data)
        {
            lock (_segments)
            {
                if (_closed) throw new IOException("pipe closed");
                _segments.Enqueue(data.ToArray());
                Monitor.PulseAll(_segments);
            }
        }

        public int Read(Span<byte> buffer)
        {
            lock (_segments)
            {
                while ((_current is null || _offset >= _current.Length) && _segments.Count == 0)
                {
                    if (_closed) return 0;
                    if (!Monitor.Wait(_segments, TimeSpan.FromSeconds(120))) throw new TimeoutException("pipe read timed out");
                }
                if (_current is null || _offset >= _current.Length) { _current = _segments.Dequeue(); _offset = 0; }
                int n = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsSpan(_offset, n).CopyTo(buffer);
                _offset += n;
                if (_offset >= _current.Length) _current = null;
                return n;
            }
        }

        public void Close()
        {
            lock (_segments)
            {
                _closed = true;
                _segments.Clear();
                Monitor.PulseAll(_segments);
            }
        }
    }

    internal sealed class PipeStream(SegmentPipe readFrom, SegmentPipe writeTo) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer) => readFrom.Read(buffer);
        public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => writeTo.Write(buffer);
        public override void Flush() { }
        protected override void Dispose(bool disposing)
        {
            readFrom.Close();
            writeTo.Close();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
