using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// The colony's world view as an <see cref="IBlockAccess"/> with its version's collision rules (b1.8.1 or 1.12.2), so the colony asks
/// the World package's own rules (<see cref="Safety.StandFeet"/>, <see cref="ItemEntityRules.PickupPoint"/>) the pawns' clients ask
/// instead of a copy of them. An unloaded column reads as air (<see cref="IStoryWorld.Loaded"/>).
/// </summary>
internal sealed class StoryBlockAccess(IStoryWorld w) : IBlockAccess
{
    public IStoryWorld World => w;
    public int GetBlock(int x, int y, int z) => w.Block(x, y, z).Id;
    public int GetMeta(int x, int y, int z) => w.Block(x, y, z).Meta;
    public bool IsLoaded(int x, int z) => w.Loaded(x, z);
    public BlockInfo Info(int id) => w.Data.Block(id);
    public int WorldHeight => w.Height;
    public IPhysicsRules Physics { get; } = RulesOf(w);

    /// <summary>The version's collision rules: 1.12.2's for a 1.12.2 world, else b1.8.1's.</summary>
    internal static IPhysicsRules RulesOf(IStoryWorld w) =>
        w.Version == "1.12.2" ? Release1122PhysicsRules.Instance : Beta181PhysicsRules.Instance;
}
