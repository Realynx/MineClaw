// ClientLive: conservative live check of MineClaw.Client against a real b1.8.1 server.
// Connects, prints world/inventory sync and the observation strings, optionally walks a few blocks on solid
// (non-edge) ground and back, optionally says one chat line, watches for corrections/kicks, disconnects cleanly.
// It never digs, places, opens containers or moves items.
//
// Usage: ClientLive <host> <port> <username> [--walk] [--chat "text"] [--seconds N] [--verbose]

using System.Collections.Concurrent;
using System.Diagnostics;
using MineClaw.Client;
using MineClaw.Protocol;
using MineClaw.World;

var positional = new List<string>();
bool walk = false, verbose = false;
string? chat = null;
int seconds = 20;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--walk": walk = true; break;
        case "--verbose": verbose = true; break;
        case "--chat": chat = args[++i]; break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        default: positional.Add(args[i]); break;
    }
}
if (positional.Count < 3)
{
    Console.Error.WriteLine("usage: ClientLive <host> <port> <username> [--walk] [--chat text] [--seconds N] [--verbose]");
    return 2;
}
string host = positional[0];
int port = int.Parse(positional[1]);
string user = positional[2];

var sw = Stopwatch.StartNew();
void Say(string s) => Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}] {s}");

var sentCounts = new ConcurrentDictionary<byte, int>();
var recvCounts = new ConcurrentDictionary<byte, int>();
var options = new ClientOptions
{
    Host = host, Port = port, Username = user,
    AutoReconnect = false, AutoRespawn = true,
    Log = m => Say("client: " + m),
    ConnectionFactory = _ =>
    {
        var c = PacketConnection.Connect(host, port);
        c.TrafficLog = t =>
        {
            var d = t.Direction == PacketDirection.ServerBound ? sentCounts : recvCounts;
            d.AddOrUpdate(t.Packet.Id, 1, (_, v) => v + 1);
            if (verbose && t.Packet.Id is not (0x0A or 0x0B or 0x0C or 0x0D or 0x1F or 0x20 or 0x21 or 0x1C or 0x22 or 0x00))
                Say($"{t.Direction.Arrow()} 0x{t.Packet.Id:X2} {t.Packet.GetType().Name}");
        };
        return c;
    },
};

await using var client = new MinecraftClient(options);
string? kicked = null;
client.Kicked += r => { kicked = r; Say("KICKED: " + r); };
client.Disconnected += r => Say("disconnected: " + r);
client.ChatReceived += m => Say("chat: " + ChatText.StripColours(m));
client.Danger += d => Say($"DANGER {d.Kind}: {d.Message}");
client.PlayerJoined += n => Say("joined: " + n);
client.PlayerLeft += n => Say("left: " + n);
client.HealthChanged += (h, f) => Say($"health {h} food {f}");

Say($"connecting to {host}:{port} as {user}");
try
{
    await client.ConnectAsync();
}
catch (Exception ex)
{
    Say("CONNECT FAILED: " + ex.Message);
    return 1;
}
Say("spawned: " + client.DescribeStatus());

// Give chunks/inventory a moment to arrive.
for (int i = 0; i < 60 && (!client.World.IsLoaded((int)client.World.Player.Position.X, (int)client.World.Player.Position.Z) || !client.World.Player.OnGround); i++)
    await Task.Delay(100);
await Task.Delay(2000);

var w = client.World;
var pos = w.Player.Position;
Say($"world spawn {w.Spawn}; seed {w.Seed}; gamemode {w.Player.GameMode}; difficulty {client.Difficulty}; loaded columns: {w.LoadedColumnCount}; block under feet: {w.GetBlock((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y) - 1, (int)Math.Floor(pos.Z))}; onGround {w.Player.OnGround}; entities {w.Entities.Count}; online: {string.Join(", ", w.Entities.OnlinePlayers)}");
Say("STATUS    " + client.DescribeStatus());
Say("INVENTORY " + client.DescribeInventory());
Say("INV+      " + client.DescribeInventory(detail: true));
Say("AROUND    " + client.DescribeSurroundings());
Say("AROUND+   " + client.DescribeSurroundings(16, detail: true));
Say($"lengths: status {client.DescribeStatus().Length}, inv {client.DescribeInventory().Length}, around {client.DescribeSurroundings().Length}");

if (chat is not null)
{
    var r = await client.SendChatAsync(chat);
    Say("chat sent: " + r);
}

if (walk)
{
    // Pick a target 2-3 blocks away reachable by plain walking over non-edge cells only.
    var start = Pathfinder.StartCellFor(w, w.Player.Position);
    if (start is null) Say("WALK SKIPPED: not on a valid start cell");
    else if (Safety.VoidNeighbourCount(w, start.Value.X, start.Value.Y, start.Value.Z) > 0) Say("WALK SKIPPED: standing on an edge");
    else
    {
        BlockPos? target = null;
        IReadOnlyList<PathStep>? path = null;
        foreach (int dist in new[] { 3, 2 })
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if (target is not null) break;
            var cand = start.Value.Offset(dx * dist, 0, dz * dist);
            var opts = PathOptions.Default with { AllowBreak = false, AllowPlace = false, AllowPartial = false, MaxDrop = 0, AllowSwim = false, AllowClimb = false };
            var pr = Pathfinder.FindPath(w, start.Value, new GoalBlock(cand), opts);
            if (pr.Status != PathStatus.Found) continue;
            bool safe = pr.Steps.All(s => s.Kind is MoveKind.Walk && !s.Sneak && s.Y == start.Value.Y
                                          && Safety.VoidNeighbourCount(w, s.X, s.Y, s.Z) == 0
                                          && !Safety.IsHazard(w, w.GetBlock(s.X, s.Y - 1, s.Z)));
            if (safe) { target = cand; path = pr.Steps; }
        }
        if (target is null) Say("WALK SKIPPED: no safe non-edge cell 2-3 blocks away");
        else
        {
            int corrBefore = client.PositionCorrections;
            Say($"walking {start} -> {target} ({path!.Count} steps: {string.Join(", ", path)})");
            var r1 = await client.GotoAsync(new GoalBlock(target.Value), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), MaxReplans = 1 });
            Say($"there: {r1} | pos {w.Player.Position} | corrections {client.PositionCorrections - corrBefore}");
            await Task.Delay(1500);
            Say($"after 1.5s: pos {w.Player.Position} | corrections {client.PositionCorrections - corrBefore}");
            var r2 = await client.GotoAsync(new GoalBlock(start.Value), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(20), MaxReplans = 1 });
            Say($"back: {r2} | pos {w.Player.Position} | corrections {client.PositionCorrections - corrBefore}");
        }
    }
}

var until = sw.Elapsed + TimeSpan.FromSeconds(seconds);
while (sw.Elapsed < until && client.State == ClientState.Playing) await Task.Delay(250);
Say("final: " + client.DescribeStatus());
Say($"position corrections total: {client.PositionCorrections}; kicked: {kicked ?? "no"}; state {client.State}");
await client.DisconnectAsync("Test finished");
Say("disconnected cleanly");
Console.WriteLine("sent:     " + string.Join(" ", sentCounts.OrderBy(k => k.Key).Select(k => $"{k.Key:X2}x{k.Value}")));
Console.WriteLine("received: " + string.Join(" ", recvCounts.OrderBy(k => k.Key).Select(k => $"{k.Key:X2}x{k.Value}")));
return kicked is null ? 0 : 1;
