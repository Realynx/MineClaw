namespace MineClaw.World;

/// <summary>
/// One tick of player control input, equivalent to the vanilla MovementInput (keys) plus look direction.
/// Forward/Strafe are in [-1, 1] (W = +1 forward, A = +1 strafe left). Sneak scales both by 0.3 inside the physics.
/// </summary>
public record struct MoveInput(
    float Forward = 0,
    float Strafe = 0,
    bool Jump = false,
    bool Sneak = false,
    bool Sprint = false,
    float Yaw = 0,
    float Pitch = 0)
{
    public static MoveInput Idle(float yaw, float pitch) => new(Yaw: yaw, Pitch: pitch);
}
