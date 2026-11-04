using MineClaw.Data.Versioning;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.Hive.Tests;

/// <summary>Live 11-04: chop/cut boxes near a home ~160 blocks from main designated 0 (main didn't have those columns; pawns did),
/// and re-marking failed designations answered 0 without trying them again.</summary>
public class StoryMergedWorldTests
{
    private static WorldState With(int x, int y, int z, int id)
    {
        var w = new WorldState();
        int cx = (x >> 4) << 4, cz = (z >> 4) << 4;
        w.ApplyChunkRegion(cx, 0, cz, 16, 128, 16, new byte[16 * 128 * 16], null, null, null); // a live column with data
        w.SetBlock(x, y, z, id, 0);
        return w;
    }

    [Fact]
    public void A_column_only_a_pawn_has_is_seen_through_the_pawn()
    {
        var main = With(0, 64, 0, 1);       // main sees chunk 0,0 only
        var pawn = With(200, 70, 400, 17);  // a pawn sees the far home's chunk (a log)
        var view = new MergedStoryWorld(() => main, () => [pawn], Beta181GameData.Instance);
        Assert.True(view.Loaded(200, 400));
        Assert.Equal(17, view.Block(200, 70, 400).Id);
        Assert.Equal(1, view.Block(0, 64, 0).Id);   // main first where it has the column
        Assert.False(view.Loaded(5000, 5000));
        Assert.Equal(0, view.Block(5000, 64, 5000).Id);
    }

    [Fact]
    public void Re_marking_a_failed_designation_tries_it_again()
    {
        var (c, _, w) = StoryRig.Make();
        w.Tree(10, 0);
        Assert.Equal(1, c.Designate("add", "chop", null, [new P3(10, 66, 0)], null).Result!["count"]!.GetValue<int>());
        var d = c.Designations.Single();
        d.Status = "failed";
        var again = c.Designate("add", "chop", null, [new P3(10, 66, 0)], null).Result!;
        Assert.Equal(1, again["count"]!.GetValue<int>());
        Assert.Equal(1, again["revived"]!.GetValue<int>());
        Assert.Equal("open", c.Designations.Single().Status);
        // An open one isn't counted twice.
        Assert.Equal(0, c.Designate("add", "chop", null, [new P3(10, 66, 0)], null).Result!["count"]!.GetValue<int>());
    }
}
