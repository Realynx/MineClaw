using MineClaw.World;

namespace MineClaw.Hive.Story;

internal static class StorySignObservations
{
    public static IEnumerable<(P3 At, IReadOnlyList<string> Lines, long Revision)> Read(WorldState world)
    {
        foreach (var (pos, lines, revision) in world.BlockEntities.SignObservations())
            if (world.IsLoaded(pos.X, pos.Z) && world.GetBlock(pos) is 63 or 68)
                yield return (new P3(pos.X, pos.Y, pos.Z), lines, revision);
    }
}
