using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

/// <summary>
/// The switch ladders the WP09 refactor turned into lookup tables, checked against a copy of the old switch over every name it
/// knew (plus case, padding and unknown names): direction names, excavate's op aliases and expand_island's side names.
/// </summary>
public class LookupTableEquivalenceTests
{
    private static readonly string[] Names =
        ["e", "east", "+x", "w", "west", "-x", "s", "south", "+z", "n", "north", "-z", " East ", "NORTH", "up", "", "x", "southeast"];

    private static BlockPos? OldParseDir(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "e" or "east" or "+x" => MinePlanner.Dirs[0], "w" or "west" or "-x" => MinePlanner.Dirs[1], "s" or "south" or "+z" => MinePlanner.Dirs[2],
        "n" or "north" or "-z" => MinePlanner.Dirs[3],
        _ => null,
    };

    [Fact]
    public void ParseDir_matches_the_old_switch_for_every_name()
    {
        Assert.Null(MinePlanner.ParseDir(null));
        foreach (var n in Names) Assert.Equal(OldParseDir(n), MinePlanner.ParseDir(n));
    }

    [Theory]
    [InlineData("dig", "box", "pattern", "extend", "DIG", " Extend ")]
    [InlineData("strip", "strip_mine", "branch")]
    [InlineData("learn", "remember")]
    [InlineData("light", "torch")]
    [InlineData("forget", "delete")]
    [InlineData("seal")]
    [InlineData("room")]
    [InlineData("list")]
    [InlineData("bogus", "Bogus")]
    public async Task Excavate_op_aliases_reach_the_same_op(params string[] ops)
    {
        var results = new List<string>();
        foreach (var op in ops)
        {
            var g = LiquidTests.Island(2);
            var reg = SkillRegistry.CreateDefault();
            var r = await ExcavationOps.RunAsync(g.Context(reg), op, SkillJson.Empty, CancellationToken.None);
            results.Add((r.Ok ? "ok: " : "failed: ") + r.Text.Replace($"'{op.Trim().ToLowerInvariant()}'", "'<op>'"));
        }
        Assert.All(results, r => Assert.Equal(results[0], r));
        var expected = ops[0] switch
        {
            "dig" => "failed: excavate needs box (x0,y0,z0,x1,y1,z1), site (a remembered dig) or rule+at",
            "learn" => "failed: learn needs a name",
            "list" => "ok: no remembered digs",
            "forget" => "failed: no remembered dig ''",
            "bogus" => "failed: excavate: unknown op '<op>' (dig|seal|room|strip|learn|light|list|forget)",
            "seal" or "light" => "failed: needs site (a remembered dig) or box",
            _ => "failed: which dig? say site:<name> (op=list shows them)",
        };
        Assert.Equal(expected, results[0]);
    }

    [Theory]
    [InlineData("east", "e", "+x", "EAST")]
    [InlineData("west", "w", "-x")]
    [InlineData("south", "s", "+z")]
    [InlineData("north", "n", "-z")]
    [InlineData("up", "sideways")]
    public async Task Expand_island_side_names_grow_the_same_side(params string[] sides)
    {
        var texts = new List<string>();
        foreach (var side in sides)
        {
            var g = LiquidTests.Island(2);
            g.Give(Blocks.Cobblestone, 64);
            var r = await g.Run("expand_island", new { side });
            texts.Add(r.ToString() + " " + string.Join(";", g.Log));
        }
        Assert.All(texts, t => Assert.Equal(texts[0], t));
    }

    private static Func<int, bool>? OldDoorKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "door" => id => Openables.IsDoor(id) && id != Openables.IronDoor, "iron_door" or "iron door" => id => id == Openables.IronDoor,
        "gate" or "fence_gate" => Openables.IsFenceGate, "trapdoor" or "hatch" => Openables.IsTrapdoor,
        "iron_trapdoor" or "iron trapdoor" => id => id == Openables.IronTrapdoor, _ => null,
    };

    [Fact]
    public void Door_kind_filter_matches_the_old_switch_for_every_kind_and_block()
    {
        string?[] kinds = [null, "door", "Door", "iron_door", "iron door", "IRON DOOR", "gate", "fence_gate", "trapdoor", "hatch", "iron_trapdoor",
            "iron trapdoor", " door", "doors", "", "lever"];
        foreach (var k in kinds)
        {
            var (old, now) = (OldDoorKind(k), DoorOps.KindFilter(k));
            Assert.Equal(old is null, now is null);
            if (old is null || now is null) continue;
            for (int id = 0; id < 4096; id++) Assert.True(old(id) == now(id), $"kind '{k}' id {id}");
        }
    }
}
