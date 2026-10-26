using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Read-only block access used by physics, raycasting and pathfinding (implemented by <see cref="WorldState"/>;
/// tests can implement it directly).</summary>
public interface IBlockAccess
{
    /// <summary>Block id at a position; 0 (air) when out of range or not loaded.</summary>
    int GetBlock(int x, int y, int z);

    int GetMeta(int x, int y, int z);

    /// <summary>Whether the chunk column containing block (x, z) has been loaded with data.</summary>
    bool IsLoaded(int x, int z);

    /// <summary>Static properties for a block id.</summary>
    BlockInfo Info(int id);
}
