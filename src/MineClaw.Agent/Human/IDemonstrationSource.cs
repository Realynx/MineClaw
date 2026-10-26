using MineClaw.Agent.Memory;

namespace MineClaw.Agent.Human;

public enum DemoActionKind
{
    Move,
    BlockPlaced,
    BlockBroken,
    /// <summary>Right-click on a block (lever, button, furnace, crafting table...).</summary>
    UseBlock,
    OpenContainer,
    /// <summary>Slot click in an open window; Detail describes it ("took 12 cobblestone").</summary>
    ClickSlot,
    Chat,
}

/// <summary>One observed thing the teaching player did. Coordinates are absolute block coords.</summary>
public sealed record DemoAction(DateTimeOffset At, DemoActionKind Kind, int X, int Y, int Z, string? Block = null, string? Detail = null);

/// <summary>
/// Captures what a player does near a structure while teaching the bot. Implemented by the client layer
/// (it sees entity movement, block changes and — on servers that echo them — window activity).
/// </summary>
public interface IDemonstrationSource
{
    /// <summary>Starts recording <paramref name="player"/>; <paramref name="area"/> (min,max) optionally limits block events.</summary>
    Task<IDemonstrationSession> StartAsync(string player, (BlockPos Min, BlockPos Max)? area, CancellationToken ct);
}

public interface IDemonstrationSession : IAsyncDisposable
{
    /// <summary>Stops recording and returns everything captured, oldest first.</summary>
    Task<IReadOnlyList<DemoAction>> StopAsync(CancellationToken ct);
}
