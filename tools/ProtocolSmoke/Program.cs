// ProtocolSmoke: connects to a b1.8.1 server, logs in (offline mode), stays for N seconds answering keep-alives and
// position corrections like the vanilla client, optionally says one chat line, then prints a packet histogram.
//
// Usage: ProtocolSmoke <host> <port> <username> [seconds=20] [chat line...] [--verbose]

using System.Collections.Concurrent;
using System.Diagnostics;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

var verbose = args.Contains("--verbose");
var positional = args.Where(a => a != "--verbose").ToArray();
if (positional.Length < 3)
{
    Console.Error.WriteLine("usage: ProtocolSmoke <host> <port> <username> [seconds=20] [chat line...] [--verbose]");
    return 2;
}

string host = positional[0];
int port = int.Parse(positional[1]);
string username = positional[2];
int seconds = positional.Length > 3 ? int.Parse(positional[3]) : 20;
string? chat = positional.Length > 4 ? string.Join(' ', positional.Skip(4)) : null;

var counts = new ConcurrentDictionary<byte, (int Count, long Bytes)>();
var outCounts = new ConcurrentDictionary<byte, int>();
int chunksDecoded = 0, chunkFailures = 0;
string? parseFailure = null, kickReason = null, chunkFailure = null;
var loggedIn = new ManualResetEventSlim();
var positionConfirmed = new ManualResetEventSlim();
var sw = Stopwatch.StartNew();

Console.WriteLine($"Connecting to {host}:{port} as {username} for {seconds}s ...");
using var conn = PacketConnection.Connect(host, port);
conn.TrafficLog = t =>
{
    if (t.Direction == PacketDirection.ClientBound)
        counts.AddOrUpdate(t.Packet.Id, _ => (1, t.Raw.Length), (_, v) => (v.Count + 1, v.Bytes + t.Raw.Length));
    else
        outCounts.AddOrUpdate(t.Packet.Id, 1, (_, v) => v + 1);
    if (verbose)
        Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F2}] {t.Direction.Arrow()} 0x{t.Packet.Id:X2} {t.Packet.GetType().Name} ({t.Raw.Length} B)");
};

conn.Send(new HandshakePacket(username));

var reader = new Thread(() =>
{
    try
    {
        while (conn.ReadPacket() is { } p)
            if (!Handle(p)) return;
        Console.WriteLine("Server closed the connection.");
    }
    catch (ProtocolException ex)
    {
        parseFailure = ex.Message;
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
    {
        if (sw.Elapsed.TotalSeconds < seconds) parseFailure ??= "I/O: " + ex.Message;
    }
}) { IsBackground = true, Name = "packet-reader" };
reader.Start();

// Movement heartbeat: an idle vanilla client sends a flying packet every tick once its position is confirmed.
var deadline = DateTime.UtcNow.AddSeconds(seconds);
bool chatSent = Heartbeat();

if (reader.IsAlive)
{
    try { conn.Send(new KickPacket("Quitting")); } catch { /* ignore */ }
}
conn.Dispose();
reader.Join(TimeSpan.FromSeconds(2));

Report();
return parseFailure != null || chunkFailures > 0 ? 1 : 0;

// One packet from the server; false after a kick (the reader stops).
bool Handle(IPacket p)
{
    switch (p)
    {
        case HandshakePacket hs: OnHandshake(hs); break;
        case LoginRequestPacket login:
            Console.WriteLine($"Logged in: entity {login.EntityId}, seed {login.MapSeed}, mode {login.ServerMode}, dim {login.Dimension}, difficulty {login.Difficulty}, height {login.WorldHeight}, max players {login.MaxPlayers}");
            loggedIn.Set();
            break;
        case KeepAlivePacket ka:
            conn.Send(new KeepAlivePacket(ka.KeepAliveId));
            break;
        case PlayerPositionLookPacket pos: OnPosition(pos); break;
        case ChatPacket c:
            Console.WriteLine($"Chat: {c.Message}");
            break;
        case SpawnPositionPacket sp:
            Console.WriteLine($"Spawn: {sp.X},{sp.Y},{sp.Z}");
            break;
        case UpdateHealthPacket h:
            Console.WriteLine($"Health {h.Health} food {h.Food} saturation {h.FoodSaturation}");
            break;
        case MapChunkPacket mc: DecodeChunk(mc); break;
        case KickPacket k:
            kickReason = k.Reason;
            Console.WriteLine($"Kicked: {k.Reason}");
            return false;
    }
    return true;
}

void OnHandshake(HandshakePacket hs)
{
    Console.WriteLine($"Handshake reply: '{hs.ConnectionHash}'{(hs.IsOfflineMode ? " (offline mode)" : " (ONLINE MODE: login will probably be rejected)")}");
    conn.Send(LoginRequestPacket.ForClient(username));
}

void OnPosition(PlayerPositionLookPacket pos)
{
    Console.WriteLine($"Server position: x={pos.X:F3} y(feet)={pos.Y:F3} stance={pos.Stance:F3} z={pos.Z:F3} yaw={pos.Yaw:F1} pitch={pos.Pitch:F1}");
    if (Math.Abs(pos.Stance - pos.Y - ProtocolInfo.PlayerEyeHeight) > 0.01)
        Console.WriteLine("  WARNING: stance - y != 1.62; 0x0D S->C field order may be wrong!");
    conn.Send(pos.EchoForServer());
    positionConfirmed.Set();
}

void DecodeChunk(MapChunkPacket mc)
{
    try
    {
        ChunkDataDecoder.Decode(mc);
        chunksDecoded++;
    }
    catch (Exception ex)
    {
        chunkFailures++;
        chunkFailure ??= $"{mc.X},{mc.Y},{mc.Z} {mc.SizeX}x{mc.SizeY}x{mc.SizeZ}: {ex.Message}";
    }
}

// Flying packets every 50 ms once the position is confirmed, and the chat line after 3 s; returns whether the chat was sent.
bool Heartbeat()
{
    bool sent = false;
    while (DateTime.UtcNow < deadline && reader.IsAlive)
    {
        Thread.Sleep(50);
        if (!positionConfirmed.IsSet) continue;
        try
        {
            conn.Send(new PlayerOnGroundPacket { OnGround = true });
            if (chat != null && !sent && sw.Elapsed.TotalSeconds > 3)
            {
                conn.Send(new ChatPacket(chat.Length > ChatPacket.MaxServerBoundLength ? chat[..ChatPacket.MaxServerBoundLength] : chat));
                sent = true;
                Console.WriteLine($"Sent chat: {chat}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Send failed: {ex.Message}");
            break;
        }
    }
    return sent;
}

void Report()
{
    Console.WriteLine();
    Console.WriteLine($"=== Received packets ({sw.Elapsed.TotalSeconds:F1}s) ===");
    foreach (var (id, (count, bytes)) in counts.OrderBy(kv => kv.Key))
        Console.WriteLine($"  0x{id:X2} {PacketRegistry.NameOf(id),-32} {count,7}  {bytes,10:N0} B");
    Console.WriteLine($"  total {counts.Values.Sum(v => v.Count)} packets, {counts.Values.Sum(v => v.Bytes):N0} bytes");
    Console.WriteLine("=== Sent packets ===");
    foreach (var (id, count) in outCounts.OrderBy(kv => kv.Key))
        Console.WriteLine($"  0x{id:X2} {PacketRegistry.NameOf(id),-32} {count,7}");
    Console.WriteLine($"Logged in: {loggedIn.IsSet}; position confirmed: {positionConfirmed.IsSet}; chat sent: {chatSent}");
    Console.WriteLine($"Chunks decoded: {chunksDecoded}, chunk decode failures: {chunkFailures}{(chunkFailure != null ? " (first: " + chunkFailure + ")" : "")}");
    if (kickReason != null) Console.WriteLine($"Kick reason: {kickReason}");
    Console.WriteLine(parseFailure != null ? $"PARSE FAILURE: {parseFailure}" : "No parse failures.");
}
