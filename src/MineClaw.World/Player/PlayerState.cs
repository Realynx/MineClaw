namespace MineClaw.World;

/// <summary>Immutable snapshot of the local player's state.</summary>
public sealed record PlayerSnapshot(
    int EntityId,
    Vec3d Position,
    double Stance,
    float Yaw,
    float Pitch,
    bool OnGround,
    int Health,
    int Food,
    float Saturation,
    float XpBar,
    int XpLevel,
    int XpTotal,
    int GameMode,
    int Dimension,
    int HeldSlot,
    bool IsDead);

/// <summary>
/// The local player's state. <see cref="Physics"/> is owned by the Client's tick thread (it is the simulated body);
/// the tick thread calls <see cref="SyncFromPhysics"/> after each tick so other threads can read a consistent
/// <see cref="Snapshot"/>. Network-driven values (health, food, xp, gamemode) are set by the packet thread.
/// </summary>
public sealed class PlayerState
{
    private readonly object _sync = new();
    private PlayerSnapshot _snap = new(0, Vec3d.Zero, PlayerPhysics.EyeHeight, 0, 0, false, 20, 20, 5, 0, 0, 0, 0, 0, 0, false);

    /// <summary>Simulated body — touch only from the tick thread.</summary>
    public PhysicsState Physics { get; } = new();

    /// <summary>True once the server has sent the first 0x0D position (the client must not move before that).</summary>
    public bool PositionKnown { get; private set; }

    public PlayerSnapshot Snapshot { get { lock (_sync) return _snap; } }

    private void Set(Func<PlayerSnapshot, PlayerSnapshot> f) { lock (_sync) _snap = f(_snap); }

    public int EntityId { get => Snapshot.EntityId; set => Set(s => s with { EntityId = value }); }
    public Vec3d Position => Snapshot.Position;
    public Vec3d EyePosition => Snapshot.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
    public float Yaw => Snapshot.Yaw;
    public float Pitch => Snapshot.Pitch;
    public bool OnGround => Snapshot.OnGround;
    public int Health => Snapshot.Health;
    public int Food => Snapshot.Food;
    public float Saturation => Snapshot.Saturation;
    public int GameMode { get => Snapshot.GameMode; set => Set(s => s with { GameMode = value }); }
    public int Dimension { get => Snapshot.Dimension; set => Set(s => s with { Dimension = value }); }
    /// <summary>Selected hotbar slot 0..8 (0x10 Holding Change).</summary>
    public int HeldSlot { get => Snapshot.HeldSlot; set => Set(s => s with { HeldSlot = Math.Clamp(value, 0, 8) }); }
    public bool IsDead => Snapshot.IsDead;

    /// <summary>0x08 Update Health (health ≤ 0 marks dead).</summary>
    public void SetHealth(int health, int food, float saturation) =>
        Set(s => s with { Health = health, Food = food, Saturation = saturation, IsDead = health <= 0 });

    /// <summary>0x2B Experience.</summary>
    public void SetExperience(float bar, int level, int total) =>
        Set(s => s with { XpBar = bar, XpLevel = level, XpTotal = total });

    /// <summary>After 0x09 Respawn.</summary>
    public void Respawned(int dimension) => Set(s => s with { IsDead = false, Dimension = dimension });

    /// <summary>Server-authoritative position (0x0D from server). Resets the simulated body — call from the tick thread
    /// or while the tick loop is paused.</summary>
    public void ServerTeleport(Vec3d feet, double stance, float yaw, float pitch, bool onGround)
    {
        Physics.Teleport(feet, yaw, pitch);
        Physics.OnGround = onGround;
        PositionKnown = true;
        Set(s => s with { Position = feet, Stance = stance, Yaw = yaw, Pitch = pitch, OnGround = onGround });
    }

    /// <summary>Publishes the physics body after a tick.</summary>
    public void SyncFromPhysics() =>
        Set(s => s with
        {
            Position = Physics.Position, Stance = Physics.Stance, Yaw = Physics.Yaw, Pitch = Physics.Pitch,
            OnGround = Physics.OnGround,
        });
}
