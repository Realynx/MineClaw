using System.Text;
using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using static MineClaw.Hive.Tests.StoryGoldenTests;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Behaviour pins for the Story director's small lookups (docs/dev/complexity.md WP01), every input on both versions: what a block
/// needs to dig, an item's tier and armour rank, tree bases, crops and soil. Goldens written by the code before it was reshaped.
/// </summary>
public sealed class StoryLookupGoldenTests
{
    private static readonly IGameData[] Versions = [Beta181GameData.Instance, Release1122GameData.Instance];

    [Fact]
    public void Tool_needs_tiers_and_ranks()
    {
        var sb = new StringBuilder();
        foreach (var d in Versions)
        {
            for (int id = -1; id <= 260; id++)
            {
                var n = StoryTools.For(d, id);
                sb.Append(d.VersionId).Append(" need ").Append(id).Append(' ').Append(n.Kind).Append(' ').Append(n.Floor).Append(' ')
                    .Append(n.Fast).Append(' ').Append(n.Keep).Append(' ').Append(n.Text).Append('\n');
            }
            for (int id = -1; id <= 2300; id++)
                if (StoryTools.ItemTier(d, id) is { } t) sb.Append(d.VersionId).Append(" tier ").Append(id).Append(' ').Append(t).Append('\n');
        }
        for (int id = -1; id <= 4096; id++)
            if (StoryTools.ArmorRank(id) is var r and not 0) sb.Append("rank ").Append(id).Append(' ').Append(r).Append('\n');
        GoldenHash("tool-needs", sb.ToString());
    }
}
