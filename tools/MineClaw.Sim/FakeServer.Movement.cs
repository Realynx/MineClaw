using System.Collections.Concurrent;
using System.Diagnostics;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using MineClaw.World.Release1122;
using MineClaw.World.Versioning;

namespace MineClaw.Sim;

/// <summary>
/// The vanilla server's movement checks (opt-in: <see cref="MovementChecks"/>), so a test of a faster body means something.
/// <list type="bullet">
/// <item><b>b1.8.1</b> (mc-dev <c>NetServerHandler.a(Packet10Flying)</c>): a packet more than 10 blocks (distance² &gt; 100) from the
/// last accepted position is "moved too quickly" and a kick; otherwise the server replays the delta with <c>Entity.move</c> (ySize
/// zeroed, the last packet's onGround, the sneak flag) and, more than 0.25 off horizontally (the vertical part is always dropped:
/// <c>d6 &gt; -0.5D || d6 &lt; 0.5D</c>), "moved wrongly"; then, if the player was clear of blocks before and is moved wrongly or now
/// inside one, a teleport back to the last accepted position, after which packets are ignored until one comes from exactly there.</item>
/// <item><b>1.12.2</b> (<c>NetHandlerPlayServer.processPlayer</c>): "moved too quickly" against the position at the start of the
/// server tick, distance² &gt; 100 × the packets this tick (a <see cref="ServerTickMs"/> window here), is a teleport back; "moved wrongly" the same replay
/// (step 0.6) and rule.</item>
/// </list>
/// Every accepted position goes to <see cref="Accepted"/> with its time (the tests measure speed from it).
/// </summary>
public partial class FakeServer
{
    /// <summary>Enforce the vanilla movement checks (default off: the older fixtures teleport the player around freely).</summary>
    public bool MovementChecks { get; set; }
    /// <summary>"moved too quickly" (b1.8.1: also a kick, <see cref="Kicked"/>).</summary>
    public int MovedTooQuickly => _tooQuickly;
    /// <summary>"moved wrongly!" warnings (the replay disagreed by more than 0.25).</summary>
    public int MovedWrongly => _wrongly;
    /// <summary>Teleports back to the last accepted position.</summary>
    public int Rollbacks => _rollbacks;
    public bool Kicked { get; private set; }
    /// <summary>1.12.2: the server tick the "moved too quickly" check measures from (vanilla 50 ms; a test harness that ticks its
    /// client faster sets its own interval).</summary>
    public int ServerTickMs { get; set; } = 50;
    /// <summary>What the last rejection was (for a test's message).</summary>
    public string? LastRejection { get; private set; }
    /// <summary>b1.8.1 "Illegal stance" (stance − y outside 0.1..1.65): a kick.</summary>
    public int IllegalStance => _illegalStance;
    /// <summary>The largest horizontal step one position packet carried.</summary>
    public double LargestStep { get; private set; }
    /// <summary>Accepted positions with the time they arrived.</summary>
    public ConcurrentQueue<(DateTime At, double X, double Y, double Z)> Accepted { get; } = new();

    private int _tooQuickly, _wrongly, _rollbacks, _illegalStance;
    private (double X, double Y, double Z)? _good;
    private (double X, double Y, double Z) _tickStart;
    private long _tickStartMs = -1;
    private int _tickPackets;
    private bool _checkMovement = true, _lastOnGround, _srvSneaking;
    private readonly Stopwatch _moveClock = Stopwatch.StartNew();
    private ServerBlocks? _serverBlocks;

    /// <summary>Runs before the default handling; true = the packet is dropped (ignored after a rollback, or rejected).</summary>
    private bool CheckMovement(IPacket p)
    {
        if (p is EntityActionPacket ea) { if (ea.Action == 1) _srvSneaking = true; else if (ea.Action == 2) _srvSneaking = false; return false; }
        double x, y, z, stance;
        bool onGround;
        switch (p)
        {
            case PlayerPositionPacket pp: (x, y, z, stance, onGround) = (pp.X, pp.Y, pp.Z, pp.Stance, pp.OnGround); break;
            case PlayerPositionLookPacket pl: (x, y, z, stance, onGround) = (pl.X, pl.Y, pl.Z, pl.Stance, pl.OnGround); break;
            case PlayerOnGroundPacket og: _lastOnGround = og.OnGround; return false;
            case PlayerLookPacket lk: _lastOnGround = lk.OnGround; return false;
            default: return false;
        }
        if (!Modern && (stance - y > 1.65 || stance - y < 0.1))
        {
            // b1.8.1: "Illegal stance", a kick.
            Interlocked.Increment(ref _illegalStance);
            Kicked = true;
            Send(new KickPacket("Illegal stance"));
            return true;
        }
        if (_good is null)
        {
            // The login teleport: nothing counts until the client answers from the spawn.
            _good = SpawnAt;
            _checkMovement = false;
        }
        var good = _good.Value;
        if (!_checkMovement)
        {
            // After a teleport (the login's, a rollback): only a packet from exactly there turns the checks on again (b1.8.1's
            // checkMovement; 1.12.2 ignores moves until the teleport is confirmed, which the echo is).
            double ddy = y - good.Y;
            if (Math.Abs(x - good.X) < 1e-6 && ddy * ddy < 0.01 && Math.Abs(z - good.Z) < 1e-6)
            {
                _checkMovement = true;
                Accept(x, y, z, onGround);
                return false;
            }
            return true;
        }
        double dx = x - good.X, dy = y - good.Y, dz = z - good.Z;
        LargestStep = Math.Max(LargestStep, Math.Sqrt(dx * dx + dz * dz));
        if (Modern)
        {
            long now = _moveClock.ElapsedMilliseconds;
            if (_tickStartMs < 0 || now - _tickStartMs >= ServerTickMs) { _tickStartMs = now; _tickStart = good; _tickPackets = 0; }
            int i = ++_tickPackets;
            if (i > 5) i = 1;
            double fx = x - _tickStart.X, fy = y - _tickStart.Y, fz = z - _tickStart.Z;
            if (fx * fx + fy * fy + fz * fz > 100.0 * i)
            {
                Interlocked.Increment(ref _tooQuickly);
                LastRejection = $"moved too quickly: {x:0.###},{y:0.###},{z:0.###} from {_tickStart.X:0.###},{_tickStart.Y:0.###},{_tickStart.Z:0.###} ({i} packets this tick)";
                RollBack(good);
                return true;
            }
        }
        else if (dx * dx + dy * dy + dz * dz > 100.0)
        {
            Interlocked.Increment(ref _tooQuickly);
            LastRejection = $"moved too quickly: {x:0.###},{y:0.###},{z:0.###} from {good.X:0.###},{good.Y:0.###},{good.Z:0.###}";
            Kicked = true;
            Send(new KickPacket("You moved too quickly :( (Hacking?)"));
            return true;
        }

        // The replay: the server moves its player by the packet's delta with its own collision.
        var w = _serverBlocks ??= new ServerBlocks(this);
        var srv = new PhysicsState
        {
            Position = new Vec3d(good.X, good.Y, good.Z), OnGround = _lastOnGround, Sneaking = _srvSneaking,
            Width = w.Physics.Width, Height = w.Physics.Height, YSize = 0,
        };
        bool clearBefore = w.Physics.GetCollidingBoxes(w, srv.BoundingBox.Grow(-0.0625, -0.0625, -0.0625), new List<AABB>()).Count == 0;
        if (Modern) Physics1122.Move(w, srv, dx, dy, dz);
        else PlayerPhysics.MoveEntity(w, srv, dx, dy, dz, PlayerPhysics.StepHeight);
        double rx = x - srv.Position.X, rz = z - srv.Position.Z;
        bool wrongly = rx * rx + rz * rz > 0.0625;
        if (wrongly)
        {
            Interlocked.Increment(ref _wrongly);
            LastRejection = $"moved wrongly: got {x:0.###},{y:0.###},{z:0.###}, expected {srv.Position.X:0.###},{srv.Position.Y:0.###},{srv.Position.Z:0.###} from {good.X:0.###},{good.Y:0.###},{good.Z:0.###}";
        }
        var at = AABB.ForEntity(new Vec3d(x, y, z), w.Physics.Width, w.Physics.Height);
        bool clearAfter = w.Physics.GetCollidingBoxes(w, at.Grow(-0.0625, -0.0625, -0.0625), new List<AABB>()).Count == 0;
        if (clearBefore && (wrongly || !clearAfter))
        {
            RollBack(good);
            return true;
        }
        Accept(x, y, z, onGround);
        return false;
    }

    private void Accept(double x, double y, double z, bool onGround)
    {
        _good = (x, y, z);
        _lastOnGround = onGround;
        Accepted.Enqueue((DateTime.UtcNow, x, y, z));
    }

    private void RollBack((double X, double Y, double Z) to)
    {
        Interlocked.Increment(ref _rollbacks);
        _checkMovement = false;
        Send(new PlayerPositionLookPacket { X = to.X, Y = to.Y, Stance = to.Y + 1.6200000047683716, Z = to.Z, Yaw = 0, Pitch = 0, OnGround = false });
    }

    /// <summary>A position the server itself moved the player to (a test's teleport): the checks start from there.</summary>
    public void MovedBy(double x, double y, double z)
    {
        _good = (x, y, z);
        _checkMovement = false;
    }

    // ---------------------------------------------------------------- farmland trampling

    /// <summary>Turn farmland to dirt the way the version's server does (default off): b1.8.1 <c>BlockSoil.b</c>, called by
    /// <c>Entity.move</c> for a walker on the ground that isn't sneaking (1 in 4 per step there; here every such move, the worst
    /// case); 1.12.2 <c>BlockFarmland.onFallenUpon</c>, a landing with a fall of more than 0.5 (chance fall − 0.5; here always).
    /// The block under the feet is vanilla's <c>(x, y − 0.2, z)</c>; a crop on it goes with it.</summary>
    public bool TrampleFarmland { get; set; }
    /// <summary>Farmland the player turned to dirt: the cell and how (walked | landed from N).</summary>
    public ConcurrentQueue<(int X, int Y, int Z, string How)> Trampled { get; } = new();

    private bool _trSneak, _trGround = true;
    private double _trPeak;
    private (double X, double Z)? _trLast;

    private void TrackTrample(IPacket p)
    {
        if (p is EntityActionPacket ea) { if (ea.Action == 1) _trSneak = true; else if (ea.Action == 2) _trSneak = false; return; }
        double x, y, z;
        bool ground;
        switch (p)
        {
            case PlayerPositionPacket pp: (x, y, z, ground) = (pp.X, pp.Y, pp.Z, pp.OnGround); break;
            case PlayerPositionLookPacket pl: (x, y, z, ground) = (pl.X, pl.Y, pl.Z, pl.OnGround); break;
            default: return;
        }
        var under = ((int)Math.Floor(x), (int)Math.Floor(y - 0.2), (int)Math.Floor(z));
        bool farm = Blocks.GetValueOrDefault(under) == MineClaw.Data.Blocks.Farmland;
        double moved = _trLast is { } last ? Math.Sqrt((x - last.X) * (x - last.X) + (z - last.Z) * (z - last.Z)) : 0;
        _trLast = (x, z);
        string? how = null;
        if (Modern)
        {
            if (ground && !_trGround && _trPeak - y > 0.5 && farm) how = $"landed from {_trPeak - y:0.00}";
        }
        else if (ground && !_trSneak && moved > 1e-3 && farm) how = "walked";
        _trPeak = ground ? y : _trGround ? Math.Max(y, _trPeak) : Math.Max(_trPeak, y);
        _trGround = ground;
        if (how == null) return;
        lock (BlockLock)
        {
            SetBlock(under.Item1, under.Item2, under.Item3, MineClaw.Data.Blocks.Dirt);
            if (Blocks.GetValueOrDefault((under.Item1, under.Item2 + 1, under.Item3)) is not 0) SetBlock(under.Item1, under.Item2 + 1, under.Item3, 0);
        }
        Trampled.Enqueue((under.Item1, under.Item2, under.Item3, how));
    }

    /// <summary>The server's blocks as the physics reads them (everything loaded).</summary>
    private sealed class ServerBlocks(FakeServer s) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => s.Blocks.GetValueOrDefault((x, y, z));
        public int GetMeta(int x, int y, int z) => s.Metas.GetValueOrDefault((x, y, z));
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => MineClaw.Data.Blocks.Get(id);
        public int WorldHeight => s.Modern ? 256 : 128;
        public IPhysicsRules Physics => s.Modern ? Release1122PhysicsRules.Instance : Beta181PhysicsRules.Instance;
    }
}
