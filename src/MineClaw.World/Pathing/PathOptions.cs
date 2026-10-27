namespace MineClaw.World;

/// <summary>Pathfinder configuration. Costs are in ticks (one walked block ≈ 4.63 ticks).</summary>
public sealed record PathOptions
{
    /// <summary>Largest voluntary drop onto solid ground. 3 = never takes fall damage.</summary>
    public int MaxDrop { get; init; } = 3;
    /// <summary>Largest drop allowed when landing in water (water cancels fall damage).</summary>
    public int MaxWaterDrop { get; init; } = 12;
    public bool AllowDiagonal { get; init; } = true;
    public bool AllowSwim { get; init; } = true;
    public bool AllowClimb { get; init; } = true;

    /// <summary>Allow mining blocks in the way.</summary>
    public bool AllowBreak { get; init; }
    /// <summary>Extra veto for breaking (id, meta, pos). Interactables (chests...), liquids and unbreakables are always refused.</summary>
    public Func<int, int, BlockPos, bool>? CanBreak { get; init; }
    /// <summary>Dig time in ticks for (id, meta). Default: hardness × 30 + 5 (bare-hand harvest speed).</summary>
    public Func<int, int, double>? DigCost { get; init; }

    /// <summary>Allow placing scaffold blocks (bridging over gaps/void, pillaring up).</summary>
    public bool AllowPlace { get; init; }
    /// <summary>Maximum scaffold blocks a path may place.</summary>
    public int ScaffoldBudget { get; init; } = 0;
    public double PlaceCost { get; init; } = 12;
    /// <summary>Block id assumed for placed scaffolding when evaluating later moves (any full cube; default cobblestone).</summary>
    public int ScaffoldBlockId { get; init; } = 4;

    /// <summary>Cost multiplier for moves into a cell next to a void / deadly drop (the follower sneaks there).</summary>
    public double EdgeCostMultiplier { get; init; } = 3.0;
    /// <summary>Penalty for cells adjacent to lava, fire or cactus.</summary>
    public double HazardAdjacentPenalty { get; init; } = 40;

    /// <summary>Plan through closed wooden doors, fence gates and head-height trapdoors (and iron doors with a reachable
    /// button/lever/plate on the near side) as <see cref="MoveKind.OpenDoor"/>/<see cref="MoveKind.OpenGate"/>/
    /// <see cref="MoveKind.OpenTrapdoor"/> steps. Off by default: the executor must handle
    /// <see cref="FollowerActionKind.Activate"/>. Closed doors are obstacles either way (never walked into).</summary>
    public bool AllowOpenDoors { get; init; }
    /// <summary>Extra cost (ticks) of stopping, right-clicking and waiting for the server's block change.</summary>
    public double OpenCost { get; init; } = 20;
    /// <summary>Extra cost when an iron door needs its button/lever pressed first.</summary>
    public double ActuatorCost { get; init; } = 30;
    /// <summary>Veto for toggling an openable (pos, id): e.g. protected/claimed doors. Null = allowed.</summary>
    public Func<BlockPos, int, bool>? CanOpen { get; init; }

    /// <summary>Cells the path must not enter.</summary>
    public IReadOnlySet<BlockPos>? Avoid { get; init; }
    /// <summary>Jump moves (from foot cell, to foot cell) not to plan again: the edge guard refused to verify their landing
    /// (see <see cref="PathFollower.BlockedJump"/>), so re-plans route around them instead of retrying forever.</summary>
    public IReadOnlySet<(BlockPos From, BlockPos To)>? AvoidJumps { get; init; }

    public int MaxNodes { get; init; } = 30_000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Return a path to the closest explored node when the goal isn't reachable within budget.</summary>
    public bool AllowPartial { get; init; } = true;

    public static PathOptions Default { get; } = new();

    /// <summary>Reach used to decide whether an iron door's button/lever can be pressed from the near-side cell.</summary>
    public double ActuatorReach { get; init; } = 4.0;

    // Movement costs (ticks)
    public const double WalkCost = 1 / 0.21585; // ≈4.633 ticks per block at walking speed
    public const double SneakFactor = 1 / 0.3;
    public const double JumpExtra = 2.0;
    public const double ClimbUpCost = 8.5, ClimbDownCost = 6.7, SwimCost = 9.0;
}
