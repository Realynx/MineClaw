using MineClaw.Data;
using MineClaw.World.Claims;

namespace MineClaw.World.Tests;

/// <summary>
/// User report 10-29: "chests placed next to each other don't combine into a double chest, so the bot can't tell". Vanilla's
/// rule (b1.8.1 BlockChest.interact, 1.12.2 BlockChest.getContainer): same kind, horizontally touching = one 54-slot window,
/// the west/north half first; placement never lets a third chest touch a pair.
/// </summary>
public class ChestPairsTests
{
    private const int Trapped = ChestPairs.TrappedChest, Ender = 130, Normal = ChestPairs.Chest;

    private sealed class Grid(params (int X, int Y, int Z, int Id)[] cells) : IBlockAccess
    {
        private readonly Dictionary<BlockPos, int> _ids = cells.ToDictionary(c => new BlockPos(c.X, c.Y, c.Z), c => c.Id);
        public int GetBlock(int x, int y, int z) => _ids.GetValueOrDefault(new BlockPos(x, y, z));
        public int GetMeta(int x, int y, int z) => 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => TestBlocks.Get(id);
    }

    private static BlockPos P(int x, int y, int z) => new(x, y, z);

    [Fact]
    public void Touching_chests_are_one_container_named_by_the_west_or_north_half_from_either_block()
    {
        var ew = new Grid((1, 64, 1, Normal), (2, 64, 1, Normal));
        Assert.Equal((P(1, 64, 1), (BlockPos?)P(2, 64, 1)), ChestPairs.Halves(ew, P(2, 64, 1)));
        Assert.Equal((P(1, 64, 1), (BlockPos?)P(2, 64, 1)), ChestPairs.Halves(ew, P(1, 64, 1)));
        var ns = new Grid((5, 64, 3, Normal), (5, 64, 2, Normal));
        Assert.Equal(P(5, 64, 2), ChestPairs.Canonical(ns, P(5, 64, 3)));   // north (-Z) half first: slots 0-26
        Assert.Equal([P(5, 64, 2), P(5, 64, 3)], ChestPairs.Cells(ns, P(5, 64, 3)));
        Assert.Equal([P(1, 64, 1)], ChestPairs.Distinct(ew, [P(2, 64, 1), P(1, 64, 1), P(2, 64, 1)]));
    }

    [Fact]
    public void Only_the_same_kind_pairs_and_never_stacked_diagonal_or_a_third_in_a_row()
    {
        var trappedPair = new Grid((0, 64, 0, Trapped), (1, 64, 0, Trapped));
        Assert.Equal(P(1, 64, 0), ChestPairs.Partner(trappedPair, P(0, 64, 0)));
        var mixed = new Grid((0, 64, 0, Normal), (1, 64, 0, Trapped));
        Assert.Null(ChestPairs.Partner(mixed, P(0, 64, 0)));
        Assert.Null(ChestPairs.Partner(mixed, P(1, 64, 0)));
        var ender = new Grid((0, 64, 0, Ender), (1, 64, 0, Ender));
        Assert.Null(ChestPairs.Partner(ender, P(0, 64, 0)));
        var stacked = new Grid((0, 64, 0, Normal), (0, 65, 0, Normal));
        Assert.Null(ChestPairs.Partner(stacked, P(0, 64, 0)));
        var diagonal = new Grid((0, 64, 0, Normal), (1, 64, 1, Normal));
        Assert.Null(ChestPairs.Partner(diagonal, P(0, 64, 0)));
        // A layout vanilla placement can't build: every block reads as a single, the same answer from each.
        var row = new Grid((0, 64, 0, Normal), (1, 64, 0, Normal), (2, 64, 0, Normal));
        foreach (var x in new[] { 0, 1, 2 }) Assert.Null(ChestPairs.Partner(row, P(x, 64, 0)));
        // A trapped chest beside a normal pair doesn't break the pair.
        var pairAndTrapped = new Grid((0, 64, 0, Normal), (1, 64, 0, Normal), (2, 64, 0, Trapped));
        Assert.Equal(P(1, 64, 0), ChestPairs.Partner(pairAndTrapped, P(0, 64, 0)));
        Assert.Null(ChestPairs.Partner(pairAndTrapped, P(2, 64, 0)));
    }

    [Fact]
    public void Placement_refuses_a_chest_beside_a_double_chest_or_between_two_and_names_them()
    {
        // Beside a pair, in line or round the corner: refused, naming the half it would touch.
        var pair = new Grid((0, 64, 0, Normal), (1, 64, 0, Normal));
        Assert.False(ChestPairs.CanPlace(pair, P(2, 64, 0), Normal, out var inLine));
        Assert.Equal(new ChestPairs.Clash(P(1, 64, 0), null), inLine);
        Assert.Equal("it would touch the double chest at 1,64,0", inLine.Words);
        Assert.False(ChestPairs.CanPlace(pair, P(0, 64, 1), Normal, out var corner));
        Assert.Equal(P(0, 64, 0), corner.At);
        // Between two singles: refused, naming both.
        var gap = new Grid((0, 64, 0, Normal), (2, 64, 0, Normal));
        Assert.False(ChestPairs.CanPlace(gap, P(1, 64, 0), Normal, out var between));
        Assert.Equal("it would sit between the chests at 0,64,0 and 2,64,0", between.Words);
        Assert.Equal("can't place a chest at 1,64,0: it would sit between the chests at 0,64,0 and 2,64,0", ChestPairs.Refusal(P(1, 64, 0), between));
        // Beside one single, a gap from a pair, stacked or diagonal: allowed.
        Assert.True(ChestPairs.CanPlace(new Grid((0, 64, 0, Normal)), P(1, 64, 0), Normal, out _));
        Assert.True(ChestPairs.CanPlace(pair, P(3, 64, 0), Normal, out _));
        Assert.True(ChestPairs.CanPlace(pair, P(1, 65, 0), Normal, out _));
        Assert.True(ChestPairs.CanPlace(pair, P(2, 64, 1), Normal, out _));
        // Only the same kind counts: a trapped chest beside a normal pair (1.12.2), an ender chest anywhere.
        Assert.True(ChestPairs.CanPlace(pair, P(2, 64, 0), Trapped, out _));
        Assert.True(ChestPairs.CanPlace(pair, P(2, 64, 0), Ender, out _));
        var trappedPair = new Grid((0, 64, 0, Trapped), (1, 64, 0, Trapped));
        Assert.False(ChestPairs.CanPlace(trappedPair, P(2, 64, 0), Trapped, out _));
        Assert.True(ChestPairs.CanPlace(trappedPair, P(2, 64, 0), Normal, out _));
    }

    [Fact]
    public void An_unknown_neighbour_is_no_chest()
    {
        int pairBeyond(BlockPos q) => q == P(1, 64, 0) ? -1 : q == P(2, 64, 0) ? Normal : 0;
        // The pair's near half is unloaded: nothing known touches the cell.
        Assert.True(ChestPairs.CanPlace(pairBeyond, P(0, 64, 0), Normal));
        int pair(BlockPos q) => q == P(1, 64, 0) || q == P(2, 64, 0) ? Normal : q == P(3, 64, 0) ? -1 : 0;
        Assert.False(ChestPairs.CanPlace(pair, P(0, 64, 0), Normal));
    }

    [Fact]
    public void A_chest_lease_through_one_half_blocks_the_other_half()
    {
        var west = ClaimKey.Chest(P(1, 64, 1));
        Assert.True(west.ConflictsWith(ClaimKey.Chest(P(2, 64, 1))));
        Assert.True(ClaimKey.Chest(P(1, 64, 2)).ConflictsWith(west));
        Assert.False(west.ConflictsWith(ClaimKey.Chest(P(2, 64, 2))));      // diagonal
        Assert.False(west.ConflictsWith(ClaimKey.Chest(P(1, 65, 1))));      // stacked
        Assert.False(west.ConflictsWith(ClaimKey.Chest(P(2, 64, 1), -1)));  // other dimension
        Assert.False(ClaimKey.Furnace(P(1, 64, 1)).ConflictsWith(ClaimKey.Furnace(P(2, 64, 1))));
    }
}
