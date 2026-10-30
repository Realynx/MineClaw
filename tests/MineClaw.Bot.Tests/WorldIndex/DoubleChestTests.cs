using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// User report 10-29: "chests placed next to each other don't combine into a double chest, so the bot can't tell". Memory
/// must hold a double chest as ONE 54-slot row, keyed on its west/north half and reached from either block: opening either
/// half updates it, a broken half splits it back to the survivor's own 27 slots, a chest placed beside a remembered one
/// joins it, and rows from an earlier session never leave a phantom second chest or 54 slots' items in a single.
/// </summary>
public class DoubleChestTests(ITestOutputHelper output)
{
    private static string K(int x, int y, int z) => ObservationStore.KeyFor(x, y, z);
    private static ItemCount I(int id, int n) => new(id, Items.Exists(id) ? Items.Get(id).Name : Blocks.Get(id).Name, n);
    private static readonly BlockPos West = new(2, 64, 0), East = new(3, 64, 0);
    private int _window = 1;

    /// <summary>The bot opens the chest at <paramref name="at"/> (the window the server sends: <paramref name="slots"/> container slots).</summary>
    private void Open(IndexFixture f, BlockPos at, int slots, params (int Slot, int Id, int Count)[] items)
    {
        int id = _window++;
        f.Feed.LastOpenedContainer = at;
        var inv = f.World.Inventory;
        inv.Open(id, WindowType.Chest, slots == 54 ? "Large chest" : "Chest", slots);
        var all = Enumerable.Repeat(ItemStack.Empty, slots + 36).ToArray();
        foreach (var (s, item, n) in items) all[s] = new ItemStack((short)item, (byte)n, 0);
        inv.SetWindowItems(id, all);
        inv.Close(id);
        f.Pump();
    }

    private static Dictionary<string, int> Contents(IndexFixture f, ObservationRow row) =>
        f.Store.Contents(row.Id).ToDictionary(c => c.Name, c => c.Count);

    private static IndexFixture Pair()
    {
        var f = new IndexFixture();
        f.Island();
        f.Set(West.X, West.Y, West.Z, Blocks.Chest);
        f.Set(East.X, East.Y, East.Z, Blocks.Chest);
        f.Pump();
        return f;
    }

    [Fact]
    public void Opening_either_half_updates_one_row_with_all_54_slots()
    {
        using var f = Pair();
        Open(f, East, 54, (0, Items.IronIngot, 12), (30, Blocks.Cobblestone, 64));
        var row = f.Row("chest", K(2, 64, 0))!;
        output.WriteLine(row.Text);
        Assert.Contains("(double)", row.Text);
        Assert.Null(f.Row("chest", K(3, 64, 0)));
        Assert.Equal(new Dictionary<string, int> { ["iron_ingot"] = 12, ["cobblestone"] = 64 }, Contents(f, row));
        Assert.Equal(54, ChestRoom.Capacity(row.Bounds!.Value));
        Assert.Equal(1, f.Store.Count("chest"));

        Open(f, West, 54, (40, Items.Bread, 3));
        row = f.Row("chest", K(2, 64, 0))!;
        Assert.Equal(new Dictionary<string, int> { ["bread"] = 3 }, Contents(f, row));
        Assert.Equal(1, f.Store.Count("chest"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Breaking_one_half_leaves_a_27_slot_chest_with_its_own_half(bool breakEast)
    {
        using var f = Pair();
        // Slots 0-26 are the west half's, 27-53 the east half's.
        Open(f, West, 54, (0, Items.IronIngot, 12), (30, Blocks.Cobblestone, 64));
        var gone = breakEast ? East : West;
        var kept = breakEast ? West : East;
        f.Set(gone.X, gone.Y, gone.Z, 0);
        f.Pump();
        var row = f.Row("chest", K(kept.X, kept.Y, kept.Z))!;
        output.WriteLine(row.Text);
        Assert.DoesNotContain("(double)", row.Text);
        Assert.True(row.Bounds!.Value.IsPoint);
        Assert.Equal(27, ChestRoom.Capacity(row.Bounds.Value));
        Assert.Equal(breakEast ? new Dictionary<string, int> { ["iron_ingot"] = 12 } : new() { ["cobblestone"] = 64 }, Contents(f, row));
        Assert.Null(f.Row("chest", K(gone.X, gone.Y, gone.Z)));
        Assert.Equal(1, f.Store.Count("chest"));
    }

    [Fact]
    public void A_broken_half_with_only_older_memory_leaves_the_survivor_unknown_not_holding_both_halves()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Store.Upsert(new Observation("chest", K(2, 64, 0), "chest at 2,64,0 (double)")
        {
            Bounds = Box.Of(2, 64, 0, 3, 64, 0), Contents = [I(Items.IronIngot, 12), I(Blocks.Cobblestone, 64)],
            ContentsAt = f.Clock.GetUtcNow(), ContentsBy = "maid_chan_67",
        });
        f.Set(West.X, West.Y, West.Z, Blocks.Chest);
        f.Set(East.X, East.Y, East.Z, Blocks.Chest);
        f.Pump();
        Assert.Equal(2, Contents(f, f.Row("chest", K(2, 64, 0))!).Count); // the pair keeps its row and memory

        f.Set(East.X, East.Y, East.Z, 0);
        f.Pump();
        var row = f.Row("chest", K(2, 64, 0))!;
        Assert.True(row.Bounds!.Value.IsPoint);
        Assert.Empty(f.Store.Contents(row.Id));
        Assert.Null(row.ContentsAt); // "contents unknown", never "opened 3m ago: empty"
    }

    [Fact]
    public void A_chest_placed_west_of_a_remembered_single_joins_it_with_its_memory()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Store.Upsert(new Observation("chest", K(3, 64, 0), "chest at 3,64,0")
        {
            Bounds = Box.Point(3, 64, 0), Contents = [I(Items.Bread, 5)], ContentsAt = f.Clock.GetUtcNow(), ContentsBy = "maid_chan_67",
        });
        f.Set(East.X, East.Y, East.Z, Blocks.Chest);
        f.Pump();
        f.Set(West.X, West.Y, West.Z, Blocks.Chest); // re-keys the chest onto its new west half
        f.Pump();
        var row = f.Row("chest", K(2, 64, 0))!;
        output.WriteLine(row.Text);
        Assert.Contains("(double)", row.Text);
        Assert.Null(f.Row("chest", K(3, 64, 0)));
        Assert.Equal(new Dictionary<string, int> { ["bread"] = 5 }, Contents(f, row));
        Assert.Equal("maid_chan_67", row.ContentsBy);
        Assert.Equal(1, f.Store.Count("chest"));

        // The new half goes again: the old chest is back with its bread.
        f.Set(West.X, West.Y, West.Z, 0);
        f.Pump();
        row = f.Row("chest", K(3, 64, 0))!;
        Assert.Equal(new Dictionary<string, int> { ["bread"] = 5 }, Contents(f, row));
        Assert.Null(f.Row("chest", K(2, 64, 0)));
    }

    [Fact]
    public void A_chest_placed_east_of_a_remembered_single_keeps_its_row()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(West.X, West.Y, West.Z, Blocks.Chest);
        f.Pump();
        Open(f, West, 27, (0, Items.Bread, 5));
        f.Set(East.X, East.Y, East.Z, Blocks.Chest);
        f.Pump();
        var row = f.Row("chest", K(2, 64, 0))!;
        Assert.Contains("(double)", row.Text);
        Assert.Equal(new Dictionary<string, int> { ["bread"] = 5 }, Contents(f, row));
        Assert.Equal(54, ChestRoom.Capacity(row.Bounds!.Value));
        Assert.Equal(1, f.Store.Count("chest"));
    }

    [Fact]
    public void Stacked_diagonal_and_a_third_in_a_row_are_not_paired()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(-2, 64, -2, Blocks.Chest); f.Set(-2, 65, -2, Blocks.Chest);   // stacked
        f.Set(2, 64, -2, Blocks.Chest); f.Set(3, 64, -1, Blocks.Chest);     // diagonal
        f.Set(-2, 64, 2, Blocks.Chest); f.Set(-1, 64, 2, Blocks.Chest); f.Set(0, 64, 2, Blocks.Chest); // three in a row (not buildable)
        f.Pump();
        var rows = f.Store.OfKind("chest");
        foreach (var r in rows) output.WriteLine(r.Text);
        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.DoesNotContain("(double)", r.Text));
    }

    [Fact]
    public void Release1122_pairs_trapped_with_trapped_only_and_never_ender_chests()
    {
        using var data = GameData.Override(Release1122GameData.Instance);
        using var f = new IndexFixture();
        f.Island();
        f.Set(-2, 64, -2, Ids1122.TrappedChest); f.Set(-1, 64, -2, Ids1122.TrappedChest); // a trapped double
        f.Set(2, 64, -2, Blocks.Chest); f.Set(3, 64, -2, Ids1122.TrappedChest);            // normal beside trapped: two chests
        f.Set(-2, 64, 2, Ids1122.EnderChest); f.Set(-1, 64, 2, Ids1122.EnderChest);        // ender chests never pair
        f.Pump();
        Assert.Contains("(double)", f.Row("chest", K(-2, 64, -2))!.Text);
        Assert.Null(f.Row("chest", K(-1, 64, -2)));
        Assert.DoesNotContain("(double)", f.Row("chest", K(2, 64, -2))!.Text);
        Assert.DoesNotContain("(double)", f.Row("chest", K(3, 64, -2))!.Text);
        Assert.NotNull(f.Row("ender_chest", K(-2, 64, 2)));
        Assert.NotNull(f.Row("ender_chest", K(-1, 64, 2)));
        Assert.Equal(3, f.Store.Count("chest"));
    }

    [Fact]
    public void Rejoin_drops_a_phantom_row_on_the_non_key_half_and_keeps_its_memory()
    {
        using var f = new IndexFixture();
        f.Island();
        // Last session: a single at 3,64,0. While away someone put a chest down west of it.
        f.Store.Upsert(new Observation("chest", K(3, 64, 0), "chest at 3,64,0")
        {
            Bounds = Box.Point(3, 64, 0), Contents = [I(Items.Bread, 5)], ContentsAt = f.Clock.GetUtcNow(),
        });
        f.Set(West.X, West.Y, West.Z, Blocks.Chest);
        f.Set(East.X, East.Y, East.Z, Blocks.Chest);
        f.Pump();
        Assert.Null(f.Row("chest", K(3, 64, 0)));
        var row = f.Row("chest", K(2, 64, 0))!;
        Assert.Contains("(double)", row.Text);
        Assert.Equal(new Dictionary<string, int> { ["bread"] = 5 }, Contents(f, row));
        Assert.Equal(1, f.Store.Count("chest"));
    }

    [Fact]
    public void Rejoin_with_a_half_broken_while_away_splits_the_remembered_double()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Store.Upsert(new Observation("chest", K(2, 64, 0), "chest at 2,64,0 (double)")
        {
            Bounds = Box.Of(2, 64, 0, 3, 64, 0), Contents = [I(Items.IronIngot, 12)], ContentsAt = f.Clock.GetUtcNow(),
        });
        f.Store.Upsert(new Observation("chest", K(6, 64, -6), "chest at 6,64,-6 (double)") { Bounds = Box.Of(6, 64, -6, 7, 64, -6) });
        f.Set(West.X, West.Y, West.Z, Blocks.Chest);  // east half gone
        f.Set(7, 64, -6, Blocks.Chest);               // west (key) half gone
        f.Pump();
        var row = f.Row("chest", K(2, 64, 0))!;
        Assert.True(row.Bounds!.Value.IsPoint);
        Assert.DoesNotContain("(double)", row.Text);
        Assert.Empty(f.Store.Contents(row.Id)); // the 54 slots' memory no longer claimed by 27
        Assert.Null(row.ContentsAt);
        Assert.Null(f.Row("chest", K(6, 64, -6))); // was: kept forever (a non-point row was never reconciled)
        Assert.NotNull(f.Row("chest", K(7, 64, -6)));
        Assert.Equal(2, f.Store.Count("chest"));
    }

    [Fact]
    public void A_pair_across_a_column_edge_stays_one_chest_while_the_other_column_is_unloaded()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(15, 64, 0, Blocks.Chest); // column 0
        f.Set(16, 64, 0, Blocks.Chest); // column 1
        f.Pump();
        Assert.Contains("(double)", f.Row("chest", K(15, 64, 0))!.Text);
        f.World.UnloadColumn(0, 0);
        f.World.ApplyChunkRegion(20, 70, 0, 1, 1, 1, new byte[1], null, null, null); // column 1 re-sent: re-scanned
        f.Pump();
        Assert.Null(f.Row("chest", K(16, 64, 0)));                     // was: a single at the east half...
        Assert.Contains("(double)", f.Row("chest", K(15, 64, 0))!.Text); // ...and the pair's row tombstoned
    }
}
