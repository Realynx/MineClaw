using System.Globalization;
using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.Hive.Story;
using MineClaw.World;
using Microsoft.Extensions.Logging;

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

    /// <summary>Every remembered chest cell with when its contents were read, in one query of the memory (the colony then reads only
    /// the chests looked into after its record: <see cref="SeenChest"/> is three queries a chest). Null when there is no memory.</summary>
    public IReadOnlyDictionary<P3, DateTimeOffset>? ChestLooks()
    {
        // Thread-safe: the store's queries take its own gate, the world's dimension is a plain read (ChestLooksOffLock).
        if (_observations is not { } obs || World is not { } world) return null;
        try
        {
            var looks = new Dictionary<P3, DateTimeOffset>();
            foreach (var (key, at) in obs.ContentsTimes("chest", world.Dimension))
                if (Cell(key) is { } p) looks[p] = at;
            return looks;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "story: chest looks");
            return null;
        }
    }

    /// <summary>The colony reads <see cref="ChestLooks"/> in the background, off its lock (live 11-10: "chest-looks 41x 1980 ms max 760
    /// ms" a minute under the colony lock, a whole read of the chest memory waiting on the store's gate).</summary>
    public bool ChestLooksOffLock => true;

    /// <summary>An observation key "x,y,z" as a cell, or null.</summary>
    private static P3? Cell(string key)
    {
        var parts = key.Split(',');
        return parts.Length == 3 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) ? new P3(x, y, z) : null;
    }

    private sealed class ChestView(IStoryWorld world) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => world.Block(x, y, z).Id;
        public int GetMeta(int x, int y, int z) => world.Block(x, y, z).Meta;
        public bool IsLoaded(int x, int z) => world.Loaded(x, z);
        public BlockInfo Info(int id) => world.Data.Block(id);
    }
}
