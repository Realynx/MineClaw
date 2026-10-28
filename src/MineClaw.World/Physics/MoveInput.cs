namespace MineClaw.World;

/// <summary>
/// One tick of player control input, equivalent to the vanilla MovementInput (keys) plus look direction.
/// Forward/Strafe are in [-1, 1] (W = +1 forward, A = +1 strafe left). Sneak scales both by 0.3 inside the physics.
/// <para><see cref="Sprint"/> = the sprint key held: sprinting starts when forward ≥ 0.8 and stops on release (b1.8.1), and in
/// 1.12.2 also on a horizontal collision or at food ≤ 6 (<see cref="Hungry"/>). <see cref="UsingItem"/> (eating, drawing a bow,
/// a raised shield) slows the input ×0.2 and blocks sprinting in 1.12.2 (<c>EntityPlayerSP.onLivingUpdate</c>); the b1.8.1 tick
/// ignores both.</para>
/// </summary>
public record struct MoveInput(
    float Forward = 0,
    float Strafe = 0,
    bool Jump = false,
    bool Sneak = false,
    bool Sprint = false,
    float Yaw = 0,
    float Pitch = 0,
    bool UsingItem = false,
    bool Hungry = false)
{
    public static MoveInput Idle(float yaw, float pitch) => new(Yaw: yaw, Pitch: pitch);
}
