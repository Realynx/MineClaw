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
    /// <summary>Veto for placing a scaffold block at a cell (e.g. inside a spawn-protected area where the server undoes
    /// placements). Null = allowed.</summary>
    public Func<BlockPos, bool>? CanPlace { get; init; }
    /// <summary>Block id assumed for placed scaffolding when evaluating later moves (any full cube; default cobblestone).</summary>
    public int ScaffoldBlockId { get; init; } = 4;

    /// <summary>Cost multiplier for moves into a cell next to a void / deadly drop (the follower sneaks there).</summary>
    public double EdgeCostMultiplier { get; init; } = 3.0;
    /// <summary>Penalty for cells adjacent to lava, fire or cactus.</summary>
    public double HazardAdjacentPenalty { get; init; } = 40;

    /// <summary>Plan through closed wooden doors, fence gates and head-height trapdoors (and iron doors with a reachable
    /// button/lever/plate on the near side) as <see cref="MoveKind.OpenDoor"/>/<see cref="MoveKind.OpenGate"/>/
    /// <see cref="MoveKind.OpenTrapdoor"/> steps. Off here (raw planners and executors without
    /// <see cref="FollowerActionKind.Activate"/>); the Client's goto turns it on by default via GotoOptions.AllowOpenDoors.
    /// Closed doors are obstacles either way (never walked into).</summary>
    public bool AllowOpenDoors { get; init; }
    /// <summary>Extra cost (ticks) of stopping, right-clicking and waiting for the server's block change.</summary>
    public double OpenCost { get; init; } = 20;
    /// <summary>Extra cost when an iron door needs its button/lever pressed first.</summary>
    public double ActuatorCost { get; init; } = 30;
    /// <summary>Veto for toggling an openable (pos, id): e.g. a door that ignored our clicks. Null = allowed. Build protection
    /// (never break) is NOT a reason: the doors of a protected house must still open.</summary>
    public Func<BlockPos, int, bool>? CanOpen { get; init; }

    /// <summary>Learned mechanisms of this dimension (<see cref="MechanismGraph.View"/>): a passage blocked by a known
    /// link's effect becomes passable through a <see cref="MoveKind.Trigger"/> step (press its button, flip its lever,
    /// walk over its plate). Iron doors with a known link are only planned that way (timing-aware).</summary>
    public MechanismView? Mechanisms { get; init; }
    /// <summary>Planning cost (ticks) of a Trigger step (stop, face, click, wait for the server).</summary>
    public double TriggerCost { get; init; } = 15;
    /// <summary>Estimated ticks from starting a click to the trigger reacting (the pulse starts then).</summary>
    public double TriggerTicks { get; init; } = 4;
    /// <summary>Safety margin (ticks) between a pulse door's planned crossing and its closing, plus
    /// <see cref="PulseMarginFraction"/> of the time elapsed since the activation (move-time estimates drift).</summary>
    public double PulseMargin { get; init; } = 4;
    public double PulseMarginFraction { get; init; } = 0.15;
    /// <summary>Links below this confidence are not used.</summary>
    public double MinMechanismConfidence { get; init; } = 0.3;
    /// <summary>Use triggers linked to TNT/dispensers (only when explicitly asked).</summary>
    public bool AllowDangerousTriggers { get; init; }
    /// <summary>Flip levers back once past their effects (default; skipped when that would leave no way back out).</summary>
    public bool RestoreLevers { get; init; } = true;
    /// <summary>Allow routing through a mechanism door (iron door / triggered passage) into a space with no known way back
    /// out. Off: such routes are rejected unless a trigger on the far side, another exit, or a lever left on gets back.</summary>
    public bool AllowOneWay { get; init; }
    /// <summary>Node budget of the way-back-out check.</summary>
    public int ReturnCheckNodes { get; init; } = 6000;
    /// <summary>Extra cost of stepping on a pressure plate whose effect is unknown (it fires whatever it's wired to).
    /// Plates with explosives near, or linked to dangerous effects, are never stepped on.</summary>
    public double UnknownPlatePenalty { get; init; } = 40;
    /// <summary>Flip levers of unknown effect (iron-door heuristic) even inside players' builds (<see cref="IsBuild"/>).</summary>
    public bool AllowUnknownLevers { get; init; }
    /// <summary>Is this block part of a player's build (protection)? Unknown levers there are not flipped.</summary>
    public Func<BlockPos, int, bool>? IsBuild { get; init; }
    /// <summary>Never stand in a retracted piston's push path (unless the piston belongs to a known mechanism).</summary>
    public bool AvoidPistonPaths { get; init; } = true;

    /// <summary>Cells the path must not enter.</summary>
    public IReadOnlySet<BlockPos>? Avoid { get; init; }
    /// <summary>Jump and gap-jump moves (from foot cell, to foot cell) not to plan again: the edge guard refused to verify their landing
    /// (see <see cref="PathFollower.BlockedJump"/>), so re-plans route around them instead of retrying forever.</summary>
    public IReadOnlySet<(BlockPos From, BlockPos To)>? AvoidJumps { get; init; }

    /// <summary>Plan gap jumps (<see cref="MoveKind.SprintJump"/>/<see cref="MoveKind.JumpGap"/>) validated by forward physics
    /// simulation. Gaps where a miss is a fall within <see cref="MaxDrop"/> are allowed by default.</summary>
    public bool AllowParkour { get; init; } = true;
    /// <summary>Sprinting is possible (b1.8.1: food &gt; 6). False = only non-sprint gap jumps.</summary>
    public bool CanSprint { get; init; } = true;
    /// <summary>Allow gap jumps where a miss means the void (or lava / a deadly fall). Also requires
    /// <see cref="HasClutchBlock"/> and passes the stricter margin and perturbation set.</summary>
    public bool AllowVoidParkour { get; init; }
    /// <summary>A placeable clutch block is in the hotbar (void parkour precondition).</summary>
    public bool HasClutchBlock { get; init; }
    /// <summary>Largest landing fall of a gap jump, measured from the jump apex (≈1.25 above the takeoff floor). Default
    /// 4.3 = a jump down 3 blocks (fall damage ceil(fall − 3) ≤ 2 hp); 3.0 = never any damage (jumps down ≤ 1.75).</summary>
    public double ParkourMaxFall { get; init; } = 4.3;
    /// <summary>Risk penalty (ticks) of any gap jump: walking ~6 blocks around is preferred.</summary>
    public double ParkourPenalty { get; init; } = 30;
    /// <summary>Extra penalty for a void gap jump (bridging is preferred when allowed).</summary>
    public double VoidParkourPenalty { get; init; } = 80;
    /// <summary>Validated jumps shared across plans (null = a per-search cache only).</summary>
    public ParkourCache? ParkourCache { get; init; }
    /// <summary>Candidate gap jumps simulated per expanded node (closest first).</summary>
    public int MaxParkourCandidates { get; init; } = 16;

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
