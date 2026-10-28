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

    /// <summary>Blocks per column (b1.8.1 128, 1.12.2 256). Implementations that predate multi-version are 128 high.</summary>
    int WorldHeight => ChunkColumn.SizeY;

    /// <summary>The movement and collision rules of the game version this world belongs to (b1.8.1 by default; a 1.12.2 client's
    /// <see cref="WorldState"/> carries the profile's). The static <see cref="BlockShapes"/> / <see cref="PlayerPhysics"/> entry points
    /// dispatch through it, so the pathfinder, simulators and safety checks get the version's shapes without a parameter.
    /// Wrappers (overlays, snapshots) forward their inner world's.</summary>
    Versioning.IPhysicsRules Physics => Versioning.Beta181PhysicsRules.Instance;
}
