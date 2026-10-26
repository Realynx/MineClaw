namespace MineClaw.World;

public enum MoveKind
{
    /// <summary>Walk to an adjacent cell at (about) the same height; step-ups ≤ 0.5 are handled by physics.</summary>
    Walk,
    /// <summary>Diagonal walk (both orthogonal neighbours are clear and safe — no corner cutting).</summary>
    Diagonal,
    /// <summary>Jump up onto an adjacent cell one block higher.</summary>
    Jump,
    /// <summary>Walk off an edge and fall 1..MaxDrop blocks (or into water).</summary>
    Drop,
    /// <summary>Climb a ladder/vine up one block.</summary>
    ClimbUp,
    /// <summary>Climb a ladder/vine down one block.</summary>
    ClimbDown,
    /// <summary>Move through water (horizontal or vertical).</summary>
    Swim,
    /// <summary>Sneak to the edge, place a block at <see cref="PathStep.Place"/> (the new floor), then walk onto it.</summary>
    Bridge,
    /// <summary>Jump and place a block at <see cref="PathStep.Place"/> under yourself (the previous foot cell).</summary>
    Pillar,
}

/// <summary>
/// One step of a path: the destination foot cell and how to get there. <see cref="Break"/> lists blocks that must be
/// mined first (in order); <see cref="Place"/> is a cell to fill with a scaffold block. <see cref="Sneak"/> is set when
/// the move starts or ends next to a void/deadly drop — the follower sneaks there so vanilla edge protection applies.
/// </summary>
public sealed record PathStep(
    int X,
    int Y,
    int Z,
    MoveKind Kind,
    double Floor,
    double Cost,
    IReadOnlyList<BlockPos> Break,
    BlockPos? Place = null,
    bool Sneak = false)
{
    public BlockPos Pos => new(X, Y, Z);

    /// <summary>Feet position to aim for: cell center at floor height.</summary>
    public Vec3d Target => new(X + 0.5, Floor, Z + 0.5);

    public bool HasActions => Break.Count > 0 || Place is not null;

    public override string ToString() =>
        $"{Kind} -> {Pos}" + (Break.Count > 0 ? $" break[{string.Join(",", Break)}]" : "") +
        (Place is { } p ? $" place{p}" : "") + (Sneak ? " sneak" : "");
}

public enum PathStatus
{
    /// <summary>A complete path to the goal (possibly empty if already there).</summary>
    Found,
    /// <summary>Budget exhausted or no route; <see cref="PathResult.Steps"/> leads to the closest node found (may be empty).</summary>
    Partial,
    /// <summary>No route and no closer node than the start.</summary>
    NoPath,
    /// <summary>Start position isn't a valid standing position (e.g. mid-air, chunk not loaded).</summary>
    InvalidStart,
}

public sealed record PathResult(
    PathStatus Status,
    IReadOnlyList<PathStep> Steps,
    double Cost,
    int NodesExplored,
    TimeSpan Elapsed,
    int ScaffoldUsed,
    string? Reason = null)
{
    public bool Success => Status == PathStatus.Found;

    public override string ToString() =>
        $"{Status}: {Steps.Count} steps, cost {Cost:0.#}, {NodesExplored} nodes, {Elapsed.TotalMilliseconds:0} ms" +
        (Reason is null ? "" : $" ({Reason})");
}
