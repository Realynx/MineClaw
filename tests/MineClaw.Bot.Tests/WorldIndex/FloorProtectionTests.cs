using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Live 2026-10-27 15:55: "mine cobble" at the dual gen dug the island floor next to it, because cobble only counted as a
/// player build in walls 2+ high. Any placed floor (cobble, planks, slabs), one-thick included, is a build now (soft,
/// kind "floor", never announced as an unknown structure); a generator's dig cell never is.
/// </summary>
public class FloorProtectionTests
{
    [Fact]
    public void One_thick_placed_floors_are_protected_but_generator_cells_are_not()
    {
        using var f = new IndexFixture();
        f.Island(0, 0, 5);
        f.Fill(-5, 63, -5, -3, 63, -3, Blocks.Cobblestone); // a one-thick cobble floor patch
        f.Fill(4, 63, -5, 5, 63, -4, Blocks.Planks);
        f.Set(4, 63, -3, Blocks.Slab);
        var cell = f.Generator(2, 64, 2);
        f.Pump();
        Assert.Equal(WorldIndexer.FloorKind, f.Indexer.ProtectionKind(new BlockPos(-4, 63, -4)));
        Assert.NotNull(f.Indexer.ProtectionKind(new BlockPos(4, 63, -5)));
        Assert.NotNull(f.Indexer.ProtectionKind(new BlockPos(4, 63, -3)));
        Assert.Null(f.Indexer.ProtectionKind(cell));             // the dig cell
        Assert.Null(f.Indexer.ProtectionKind(new BlockPos(1, 63, 1))); // natural island ground (grass/dirt) isn't a placed floor
        // A floor is protected, not an "unknown structure" to ask about.
        Assert.Empty(f.Store.Nearest("unknown_structure", -4, 63, -4, 5, 4));
        // The cobble the bot digs out of the floor stops being protected; putting it back makes it a floor again.
        f.Set(-4, 63, -4, 0);
        f.Pump();
        Assert.Null(f.Indexer.ProtectionKind(new BlockPos(-4, 63, -4)));
        f.Set(-4, 63, -4, Blocks.Cobblestone);
        f.Pump();
        Assert.Equal(WorldIndexer.FloorKind, f.Indexer.ProtectionKind(new BlockPos(-4, 63, -4)));
    }
}
