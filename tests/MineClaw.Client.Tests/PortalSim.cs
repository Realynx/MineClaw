using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

/// <summary>
/// Nether portals on the <see cref="FakeServer"/>, as the b1.8.1 server does them (mc-dev <c>NetServerHandler</c> flying
/// packet → <c>EntityPlayer.a(true)</c> tick + <c>Entity.move</c> block collisions → <c>BlockPortal</c> → <c>Entity.Y()</c>):
/// each movement packet whose player box (shrunk by 0.001) overlaps a portal block marks "in portal" unless the cooldown
/// runs, which it then resets to 10; each "in portal" tick adds 0.0125 to the timer (80 ticks), otherwise the timer decays
/// 0.05 a tick; at 1.0 the cooldown is set to 10 and the player switches: 0x09 Respawn with the other dimension, that
/// dimension's chunks, and a 0x0D INTO the exit portal. Moves are ignored until the client echoes the teleport.
/// </summary>
internal sealed class PortalSim
{
    private readonly FakeServer _s;
    private readonly Dictionary<int, Dictionary<(int, int, int), (int Id, int Meta)>> _worlds = new();
    private readonly Dictionary<int, (double X, double Y, double Z)> _arrival = new();
    private float _timer;
    private int _cooldown;
    private bool _awaitingAck;
    private (double X, double Y, double Z) _pos;

    public int Dimension { get; private set; }
    public int Switches { get; private set; }
    /// <summary>Most ticks spent in a portal while the cooldown kept refreshing (what stranded the live bot).</summary>
    public int CooldownRefreshes { get; private set; }

    public PortalSim(FakeServer s, int startDim = 0)
    {
        _s = s;
        Dimension = startDim;
        s.Hook = OnPacket;
    }

    /// <summary>State right after a switch (the player stands in the exit portal with the cooldown running).</summary>
    public void JustArrived()
    {
        _cooldown = 10;
        _timer = 1f;
    }

    /// <summary>Blocks of a dimension and where the server drops a player arriving there (inside its portal).</summary>
    public void World(int dim, IEnumerable<(int X, int Y, int Z, int Id, int Meta)> blocks, (double X, double Y, double Z) arrival)
    {
        _worlds[dim] = blocks.ToDictionary(b => (b.X, b.Y, b.Z), b => (b.Id, b.Meta));
        _arrival[dim] = arrival;
        if (dim == Dimension) Load(dim);
    }

    /// <summary>"x y z id meta" lines (# comments) from tests/fixtures.</summary>
    public static IEnumerable<(int, int, int, int, int)> Fixture(params string[] path)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine([d.FullName, "tests", "fixtures", .. path]);
            if (!File.Exists(p)) continue;
            return File.ReadLines(p).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Select(f => (int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]))).ToList();
        }
        throw new FileNotFoundException("tests/fixtures/" + string.Join('/', path));
    }

    private void Load(int dim)
    {
        _s.Blocks.Clear();
        _s.Metas.Clear();
        foreach (var ((x, y, z), (id, meta)) in _worlds[dim])
        {
            _s.Blocks[(x, y, z)] = id;
            if (meta != 0) _s.Metas[(x, y, z)] = meta;
        }
    }

    /// <summary>Every chunk with blocks in the current dimension, plus a ring of (empty) neighbours.</summary>
    public void SendChunks()
    {
        var cols = _s.Blocks.Keys.Select(k => (k.Item1 >> 4, k.Item3 >> 4)).ToHashSet();
        var all = cols.SelectMany(c => from dx in new[] { -1, 0, 1 } from dz in new[] { -1, 0, 1 } select (c.Item1 + dx, c.Item2 + dz)).ToHashSet();
        foreach (var (cx, cz) in all) _s.SendChunk(cx, cz);
    }

    private bool BoxInPortal()
    {
        const double h = 0.3 - 0.001;
        for (int x = (int)Math.Floor(_pos.X - h); x <= (int)Math.Floor(_pos.X + h); x++)
        for (int y = (int)Math.Floor(_pos.Y + 0.001); y <= (int)Math.Floor(_pos.Y + 1.8 - 0.001); y++)
        for (int z = (int)Math.Floor(_pos.Z - h); z <= (int)Math.Floor(_pos.Z + h); z++)
            if (_s.Blocks.TryGetValue((x, y, z), out int id) && id == MineClaw.Data.Blocks.Portal) return true;
        return false;
    }

    private bool OnPacket(IPacket p)
    {
        switch (p)
        {
            case PlayerPositionLookPacket pl:
                if (_awaitingAck)
                {
                    var a = _arrival[Dimension];
                    if (Math.Abs(pl.X - a.X) < 0.01 && Math.Abs(pl.Z - a.Z) < 0.01) _awaitingAck = false;
                    return false;
                }
                _pos = (pl.X, pl.Y, pl.Z);
                break;
            case PlayerPositionPacket pp:
                if (_awaitingAck) return false;
                _pos = (pp.X, pp.Y, pp.Z);
                break;
            case PlayerLookPacket or PlayerOnGroundPacket:
                if (_awaitingAck) return false;
                break;
            default:
                return false;
        }
        Tick();
        return false;
    }

    private void Tick()
    {
        bool inPortal = false;
        if (BoxInPortal())
        {
            if (_cooldown > 0) { _cooldown = 10; CooldownRefreshes++; }
            else inPortal = true;
        }
        if (inPortal)
        {
            _timer += 0.0125f;
            if (_timer >= 1f)
            {
                _timer = 1f;
                _cooldown = 10;
                Switch(Dimension == 0 ? -1 : 0);
            }
        }
        else _timer = Math.Max(0, _timer - 0.05f);
        if (_cooldown > 0) _cooldown--;
    }

    private void Switch(int to)
    {
        Dimension = to;
        Switches++;
        _s.Send(new RespawnPacket { Dimension = (sbyte)to, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        Load(to);
        SendChunks();
        var a = _arrival[to];
        _pos = a;
        _awaitingAck = true;
        _s.Send(new PlayerPositionLookPacket { X = a.X, Y = a.Y, Stance = a.Y + 1.62, Z = a.Z, OnGround = false });
    }
}
