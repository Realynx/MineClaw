namespace MineClaw.World;

/// <summary>Mutable kinematic state of the local player as simulated by <see cref="PlayerPhysics"/>.</summary>
public sealed class PhysicsState
{
    /// <summary>Feet position (bounding box bottom center).</summary>
    public Vec3d Position { get; set; }
    public Vec3d Velocity { get; set; }
    public float Yaw { get; set; }
    public float Pitch { get; set; }
    public bool OnGround { get; set; }
    public bool CollidedHorizontally { get; set; }
    public bool CollidedVertically { get; set; }
    public bool InWater { get; set; }
    public bool InLava { get; set; }
    public bool InWeb { get; set; }
    public bool OnLadder { get; set; }
    public bool Sneaking { get; set; }
    /// <summary>Current sprint state. The Client must send 0x13 Entity Action (4 start / 5 stop sprinting) when it changes.</summary>
    public bool Sprinting { get; set; }
    public double FallDistance { get; set; }
    /// <summary>Camera smoothing offset (Entity.ySize): rises after step-ups / while sneaking, decays ×0.4 per tick.</summary>
    public double YSize { get; set; }
    /// <summary>Set when the last tick could not run because the chunk under the player isn't loaded.</summary>
    public bool Frozen { get; set; }

    public AABB BoundingBox => AABB.ForPlayer(Position);

    /// <summary>Eye position used for raycasts (feet + 1.62; sneaking lowers the camera, not the hitbox).</summary>
    public Vec3d EyePosition => new(Position.X, Position.Y + PlayerPhysics.EyeHeight, Position.Z);

    /// <summary>Stance to send in 0x0B/0x0D (vanilla sends posY = minY + 1.62 - ySize).</summary>
    public double Stance => Position.Y + PlayerPhysics.EyeHeight - YSize;

    public PhysicsState Clone() => (PhysicsState)MemberwiseClone();

    /// <summary>Resets motion after a server teleport (0x0D) or respawn.</summary>
    public void Teleport(Vec3d feet, float yaw, float pitch)
    {
        Position = feet;
        Yaw = yaw;
        Pitch = pitch;
        Velocity = Vec3d.Zero;
        FallDistance = 0;
        YSize = 0;
        OnGround = false;
    }
}
