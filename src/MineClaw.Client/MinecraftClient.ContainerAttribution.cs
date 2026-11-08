using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    private sealed record ContainerAttribution(int WindowId, int Dimension, BlockPos At);
    private volatile ContainerAttribution? _openedContainerAttribution;

    /// <summary>The activated block for the latest server-opened window. Published before Inventory.WindowOpened, so an
    /// observer never associates a new window with an older action or guesses from a later look direction. Unsolicited
    /// windows and dimension changes have no attribution.</summary>
    public BlockPos? OpenedContainerPosition(int windowId) =>
        _openedContainerAttribution is { } a && a.WindowId == windowId && a.Dimension == World.Dimension ? a.At : null;
}
