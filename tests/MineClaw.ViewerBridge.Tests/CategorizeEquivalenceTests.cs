using MineClaw.ViewerBridge.Graph;

namespace MineClaw.ViewerBridge.Tests;

/// <summary>Pins <see cref="GraphModel.Categorize"/> (WP07: the tuple switch became a table): every kind × source it knows, casing, nulls, unknowns.</summary>
public class CategorizeEquivalenceTests
{
    private static MemoryCategory Reference(string? kind, string? source) => (kind?.ToLowerInvariant(), source?.ToLowerInvariant()) switch
    {
        ("episode", "activity") => MemoryCategory.Activity,
        ("episode", "journal") => MemoryCategory.Self,
        ("episode", _) => MemoryCategory.Episode,
        ("gamedata", _) or ("recipe", _) or ("skill", _) or ("routine", _) => MemoryCategory.Recipe,
        ("player", _) or ("chat", _) or ("claim", _) => MemoryCategory.Chat,
        ("structure", _) or ("world", _) or ("waypoint", _) => MemoryCategory.World,
        ("selfnote", _) or ("self", _) or ("identity", _) or ("journal", _) => MemoryCategory.Self,
        _ => MemoryCategory.Fact,
    };

    [Fact]
    public void Every_kind_and_source_matches_the_switch()
    {
        string?[] kinds =
        [
            null, "", "episode", "EPISODE", "gamedata", "recipe", "skill", "routine", "player", "chat", "claim", "structure", "world", "waypoint",
            "selfnote", "self", "identity", "journal", "Journal", "fact", "knowledge", "note", "lore", "episodes",
        ];
        string?[] sources = [null, "", "activity", "ACTIVITY", "journal", "Journal", "chat", "minute", "seed", "x"];
        foreach (var k in kinds)
            foreach (var s in sources)
                foreach (var tag in new string?[] { null, "tag" })
                    Assert.True(Reference(k, s) == GraphModel.Categorize(k, tag, s), $"{k ?? "null"}/{s ?? "null"}");
    }
}
