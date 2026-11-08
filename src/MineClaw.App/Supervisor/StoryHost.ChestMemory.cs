using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

public sealed partial class StoryHost
{
    /// <summary>Only the actual double-chest partner can share contents. Neighboring unrelated containers must never
    /// donate their stock to this chest, and the freshest observation of either valid half wins.</summary>
    private ObservationRow? ChestMemory(P3 at)
    {
        if (_observations is not { } obs || World is not { } world) return null;
        var positions = ChestPairs.Cells(new ChestView(world), new BlockPos(at.X, at.Y, at.Z));
        return positions.Select(p => obs.Get("chest", ObservationStore.KeyFor(p.X, p.Y, p.Z), dimension: world.Dimension))
            .Where(row => row?.ContentsAt != null).OrderByDescending(row => row!.ContentsAt).FirstOrDefault();
    }

    private sealed class ChestView(IStoryWorld world) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => world.Block(x, y, z).Id;
        public int GetMeta(int x, int y, int z) => world.Block(x, y, z).Meta;
        public bool IsLoaded(int x, int z) => world.Loaded(x, z);
        public BlockInfo Info(int id) => world.Data.Block(id);
    }
}
