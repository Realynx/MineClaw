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
        if (!ReadMove(p, out var to, out double stance, out bool onGround)) return false;
        if (KickedForStance(to.Y, stance)) return true;
        var good = LastGood();
        // After a teleport (the login's, a rollback): only a packet from exactly there turns the checks on again (b1.8.1's
        // checkMovement; 1.12.2 ignores moves until the teleport is confirmed, which the echo is).
        if (!_checkMovement) return !AnswersTeleport(to, good, onGround);
        double dx = to.X - good.X, dz = to.Z - good.Z;
        LargestStep = Math.Max(LargestStep, Math.Sqrt(dx * dx + dz * dz));
        if (Modern ? TooQuickThisTick(to, good) : TooQuickBeta(to, good)) return true;
        return ReplayRejects(to, good, onGround);
    }

    /// <summary>The position a movement packet carries; the others only note the sneak or the on-ground flag (false = nothing to check).</summary>
    private bool ReadMove(IPacket p, out (double X, double Y, double Z) to, out double stance, out bool onGround)
    {
        (to, stance, onGround) = (default, 0, false);
        switch (p)
        {
            case EntityActionPacket ea: if (SneakChange(ea) is { } sneak) _srvSneaking = sneak; return false;
            case PlayerPositionPacket pp: (to, stance, onGround) = ((pp.X, pp.Y, pp.Z), pp.Stance, pp.OnGround); return true;
            case PlayerPositionLookPacket pl: (to, stance, onGround) = ((pl.X, pl.Y, pl.Z), pl.Stance, pl.OnGround); return true;
            case PlayerOnGroundPacket og: _lastOnGround = og.OnGround; return false;
            case PlayerLookPacket lk: _lastOnGround = lk.OnGround; return false;
            default: return false;
        }
    }

    /// <summary>Sneak pressed (true) or released (false); null for any other entity action.</summary>
    private static bool? SneakChange(EntityActionPacket ea) => ea.Action == 1 ? true : ea.Action == 2 ? false : null;

    /// <summary>b1.8.1: "Illegal stance" (stance − y outside 0.1..1.65), a kick.</summary>
    private bool KickedForStance(double y, double stance)
    {
        if (Modern || !(stance - y > 1.65 || stance - y < 0.1)) return false;
        Interlocked.Increment(ref _illegalStance);
        Kicked = true;
        Send(new KickPacket("Illegal stance"));
        return true;
    }

    /// <summary>The last accepted position; before any, the login teleport (nothing counts until the client answers from the spawn).</summary>
    private (double X, double Y, double Z) LastGood()
    {
        if (_good is null)
        {
            _good = SpawnAt;
            _checkMovement = false;
        }
        return _good.Value;
    }

    /// <summary>A packet from exactly the teleport's position turns the checks back on (and is accepted).</summary>
    private bool AnswersTeleport((double X, double Y, double Z) to, (double X, double Y, double Z) good, bool onGround)
    {
        double ddy = to.Y - good.Y;
        if (!(Math.Abs(to.X - good.X) < 1e-6 && ddy * ddy < 0.01 && Math.Abs(to.Z - good.Z) < 1e-6)) return false;
        _checkMovement = true;
        Accept(to.X, to.Y, to.Z, onGround);
        return true;
    }

    /// <summary>1.12.2: more than 10 blocks a packet from where the server tick started (100 × the packets this tick, distance²): a
    /// teleport back.</summary>
    private bool TooQuickThisTick((double X, double Y, double Z) to, (double X, double Y, double Z) good)
    {
        long now = _moveClock.ElapsedMilliseconds;
        if (_tickStartMs < 0 || now - _tickStartMs >= ServerTickMs) { _tickStartMs = now; _tickStart = good; _tickPackets = 0; }
        int i = ++_tickPackets;
        if (i > 5) i = 1;
        double fx = to.X - _tickStart.X, fy = to.Y - _tickStart.Y, fz = to.Z - _tickStart.Z;
        if (fx * fx + fy * fy + fz * fz <= 100.0 * i) return false;
        Interlocked.Increment(ref _tooQuickly);
        LastRejection = $"moved too quickly: {to.X:0.###},{to.Y:0.###},{to.Z:0.###} from {_tickStart.X:0.###},{_tickStart.Y:0.###},{_tickStart.Z:0.###} ({i} packets this tick)";
        RollBack(good);
        return true;
    }

    /// <summary>b1.8.1: more than 10 blocks from the last accepted position: a kick.</summary>
    private bool TooQuickBeta((double X, double Y, double Z) to, (double X, double Y, double Z) good)
    {
        double dx = to.X - good.X, dy = to.Y - good.Y, dz = to.Z - good.Z;
        if (dx * dx + dy * dy + dz * dz <= 100.0) return false;
        Interlocked.Increment(ref _tooQuickly);
        LastRejection = $"moved too quickly: {to.X:0.###},{to.Y:0.###},{to.Z:0.###} from {good.X:0.###},{good.Y:0.###},{good.Z:0.###}";
        Kicked = true;
        Send(new KickPacket("You moved too quickly :( (Hacking?)"));
        return true;
    }

    /// <summary>The replay: the server moves its player by the packet's delta with its own collision. Moved wrongly (or now inside a
    /// block) from a clear spot: a teleport back (true); otherwise the position is accepted.</summary>
    private bool ReplayRejects((double X, double Y, double Z) to, (double X, double Y, double Z) good, bool onGround)
    {
        var w = _serverBlocks ??= new ServerBlocks(this);
        var srv = new PhysicsState
        {
            Position = new Vec3d(good.X, good.Y, good.Z), OnGround = _lastOnGround, Sneaking = _srvSneaking,
            Width = w.Physics.Width, Height = w.Physics.Height, YSize = 0,
        };
        bool clearBefore = w.Physics.GetCollidingBoxes(w, srv.BoundingBox.Grow(-0.0625, -0.0625, -0.0625), new List<AABB>()).Count == 0;
        double dx = to.X - good.X, dy = to.Y - good.Y, dz = to.Z - good.Z;
        if (Modern) Physics1122.Move(w, srv, dx, dy, dz);
        else PlayerPhysics.MoveEntity(w, srv, dx, dy, dz, PlayerPhysics.StepHeight);
        bool wrongly = MovedWronglyFrom(to, srv.Position, good);
        var at = AABB.ForEntity(new Vec3d(to.X, to.Y, to.Z), w.Physics.Width, w.Physics.Height);
        bool clearAfter = w.Physics.GetCollidingBoxes(w, at.Grow(-0.0625, -0.0625, -0.0625), new List<AABB>()).Count == 0;
        if (clearBefore && (wrongly || !clearAfter))
        {
            RollBack(good);
            return true;
        }
        Accept(to.X, to.Y, to.Z, onGround);
        return false;
    }

    /// <summary>More than 0.25 off the replay horizontally: "moved wrongly" (counted and noted).</summary>
    private bool MovedWronglyFrom((double X, double Y, double Z) to, Vec3d replayed, (double X, double Y, double Z) good)
    {
        double rx = to.X - replayed.X, rz = to.Z - replayed.Z;
        if (rx * rx + rz * rz <= 0.0625) return false;
        Interlocked.Increment(ref _wrongly);
        LastRejection = $"moved wrongly: got {to.X:0.###},{to.Y:0.###},{to.Z:0.###}, expected {replayed.X:0.###},{replayed.Y:0.###},{replayed.Z:0.###} from {good.X:0.###},{good.Y:0.###},{good.Z:0.###}";
        return true;
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
        if (p is EntityActionPacket ea)
        {
            if (SneakChange(ea) is { } sneak) _trSneak = sneak;
            return;
        }
        if (!ReadPosition(p, out double x, out double y, out double z, out bool ground)) return;
        var under = ((int)Math.Floor(x), (int)Math.Floor(y - 0.2), (int)Math.Floor(z));
        bool farm = Blocks.GetValueOrDefault(under) == MineClaw.Data.Blocks.Farmland;
        double moved = _trLast is { } last ? Math.Sqrt((x - last.X) * (x - last.X) + (z - last.Z) * (z - last.Z)) : 0;
        _trLast = (x, z);
        string? how = !farm || !ground ? null : Modern ? LandedHow(y) : WalkedHow(moved);
        _trPeak = NextPeak(y, ground);
        _trGround = ground;
        if (how != null) TrampleCell(under, how);
    }

    private static bool ReadPosition(IPacket p, out double x, out double y, out double z, out bool ground)
    {
        (x, y, z, ground) = (0, 0, 0, false);
        switch (p)
        {
            case PlayerPositionPacket pp: (x, y, z, ground) = (pp.X, pp.Y, pp.Z, pp.OnGround); return true;
            case PlayerPositionLookPacket pl: (x, y, z, ground) = (pl.X, pl.Y, pl.Z, pl.OnGround); return true;
            default: return false;
        }
    }

    /// <summary>The highest feet height since the last ground contact (on the ground: here).</summary>
    private double NextPeak(double y, bool ground) => ground ? y : _trGround ? Math.Max(y, _trPeak) : Math.Max(_trPeak, y);

    /// <summary>1.12.2: a landing on farmland after a fall of more than 0.5.</summary>
    private string? LandedHow(double y) => !_trGround && _trPeak - y > 0.5 ? $"landed from {_trPeak - y:0.00}" : null;

    /// <summary>b1.8.1: a step on farmland that isn't sneaking.</summary>
    private string? WalkedHow(double moved) => !_trSneak && moved > 1e-3 ? "walked" : null;

    /// <summary>The farmland turns to dirt (a crop on it goes with it).</summary>
    private void TrampleCell((int X, int Y, int Z) under, string how)
    {
        lock (BlockLock)
        {
            SetBlock(under.X, under.Y, under.Z, MineClaw.Data.Blocks.Dirt);
            if (Blocks.GetValueOrDefault((under.X, under.Y + 1, under.Z)) is not 0) SetBlock(under.X, under.Y + 1, under.Z, 0);
        }
        Trampled.Enqueue((under.X, under.Y, under.Z, how));
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
