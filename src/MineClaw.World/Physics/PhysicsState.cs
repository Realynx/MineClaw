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

    /// <summary>Hitbox width (b1.8.1 0.6; 1.12.2 0.6F).</summary>
    public double Width { get; set; } = PlayerPhysics.Width;
    /// <summary>Hitbox height: 1.8 (b1.8.1 always); 1.12.2 1.8F, or 1.65F while sneaking when it fits (<c>EntityPlayer.updateSize</c>).</summary>
    public double Height { get; set; } = PlayerPhysics.Height;
    /// <summary>Eye height above the feet: 1.62 (b1.8.1 always: sneaking lowers the camera, not the eye); 1.12.2 1.62F, 1.54F while
    /// sneaking (<c>EntityPlayer.getEyeHeight</c>).</summary>
    public double EyeHeight { get; set; } = PlayerPhysics.EyeHeight;

    // ---- 1.12.2 state (multi-version P3; the b1.8.1 tick never reads or writes these) ----
    /// <summary><c>EntityLivingBase.jumpTicks</c>: 10 after a jump, counting down; a held jump re-jumps only at 0.</summary>
    public int JumpTicks { get; set; }
    /// <summary><c>landMovementFactor</c> (the AI move speed) as the end of the previous tick left it; NaN = not yet ticked (walk speed).
    /// The travel of a tick uses last tick's value, so the first sprinting tick still accelerates at walking speed.</summary>
    public float LandSpeed { get; set; } = float.NaN;
    /// <summary><c>jumpMovementFactor</c> as the previous tick left it; NaN = not yet ticked (0.02F).</summary>
    public float AirSpeed { get; set; } = float.NaN;
    /// <summary>Potion amplifiers the 1.12.2 tick reads (−1 = none; the client sets them from the player's effects each tick):
    /// speed/slowness scale the movement attribute (<c>Potion.registerPotionAttributeModifier</c>: +0.2 / −0.15 per level,
    /// operation 2), jump boost adds 0.1 per level to the jump (<c>EntityLivingBase.jump</c>).</summary>
    public int SpeedAmplifier { get; set; } = -1;
    public int SlownessAmplifier { get; set; } = -1;
    public int JumpBoostAmplifier { get; set; } = -1;
    /// <summary>Fall damage (half-hearts) the landing of the last tick dealt (<c>EntityLivingBase.fall</c>); 0 when none.</summary>
    public int FallDamage { get; set; }

    public AABB BoundingBox => AABB.ForEntity(Position, Width, Height);

    /// <summary>Eye position used for raycasts (feet + <see cref="EyeHeight"/>).</summary>
    public Vec3d EyePosition => new(Position.X, Position.Y + EyeHeight, Position.Z);

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
