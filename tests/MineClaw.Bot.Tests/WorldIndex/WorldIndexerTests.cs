using System.Diagnostics;
using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

public class WorldIndexerTests(ITestOutputHelper output)
{
    private static string K(int x, int y, int z) => ObservationStore.KeyFor(x, y, z);

    [Fact]
    public void Detects_stations_and_merges_double_chests()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(1, 64, 1, Blocks.Chest);
        f.Set(2, 64, 1, Blocks.Chest);           // double chest with 1,64,1
        f.Set(-2, 64, -2, Blocks.Chest);         // single
        f.Set(3, 64, -1, Blocks.LitFurnace);
        f.Set(-3, 64, 2, Blocks.Workbench);
        f.Set(0, 64, 3, Blocks.Dispenser);
        f.Set(-1, 64, 0, Blocks.Bed, 8 | 0);     // head, facing +z → foot at -1,64,-1
        f.Set(-1, 64, -1, Blocks.Bed, 0);
        f.Set(2, 64, 3, Blocks.Jukebox);
        f.Set(3, 64, 3, Blocks.NoteBlock);
        f.Pump();

        var chest = f.Row("chest", K(1, 64, 1));
        Assert.NotNull(chest);
        Assert.Contains("(double)", chest!.Text);
        Assert.Null(f.Row("chest", K(2, 64, 1)));
        Assert.NotNull(f.Row("chest", K(-2, 64, -2)));
        var furnace = f.Row("furnace", K(3, 64, -1))!;
        Assert.Contains("(lit)", furnace.Text);
        Assert.Contains("on main island", furnace.Text);
        Assert.NotNull(f.Row("crafting_table", K(-3, 64, 2)));
        Assert.NotNull(f.Row("dispenser", K(0, 64, 3)));
        Assert.NotNull(f.Row("bed", K(-1, 64, 0)));
        Assert.Null(f.Row("bed", K(-1, 64, -1)));
        Assert.NotNull(f.Row("jukebox", K(2, 64, 3)));
        Assert.NotNull(f.Row("note_block", K(3, 64, 3)));
        foreach (var r in new[] { chest, furnace }) output.WriteLine(r.Text);
    }

    [Fact]
    public void Container_snapshot_feeds_where_is_end_to_end()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(2, 64, 0, Blocks.Chest);
        f.Set(3, 64, 0, Blocks.Furnace);
        f.Pump();

        // Bot at 0.5,65,0.5 looking at the chest.
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(new Vec3d(0.5, 65 + PlayerPhysics.EyeHeight, 0.5), new Vec3d(2.5, 64.5, 0.5));
        f.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, yaw, pitch, true);
        var inv = f.World.Inventory;
        inv.Open(1, WindowType.Chest, "Chest", 27);
        var items = Enumerable.Repeat(ItemStack.Empty, 27 + 36).ToArray();
        items[0] = new ItemStack(Items.IronIngot, 12, 0);
        items[1] = new ItemStack(Blocks.Cobblestone, 64, 0);
        items[2] = new ItemStack(Blocks.Cobblestone, 64, 0);
        inv.SetWindowItems(1, items);
        inv.Close(1);
        f.Pump();

        var hit = MineClaw.Agent.Rag.WorldTools.WhereIs(f.Store, "iron_ingot", null, new MineClaw.Agent.Memory.BlockPos(0, 65, 0), f.Clock.GetUtcNow());
        output.WriteLine(hit.Text);
        Assert.Contains("x12", hit.Text);
        Assert.Contains("chest@2,64,0", hit.Text);
        var row = f.Row("chest", K(2, 64, 0))!;
        Assert.Contains("cobblestone x128", row.Text);
        output.WriteLine(row.Text);

        // Furnace: lit state + "last: N input -> output".
        var (fy, fp) = PlayerPhysics.YawPitchTo(new Vec3d(0.5, 65 + PlayerPhysics.EyeHeight, 0.5), new Vec3d(3.5, 64.5, 0.5));
        f.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, fy, fp, true);
        inv.Open(2, WindowType.Furnace, "Furnace", 3);
        var fs = Enumerable.Repeat(ItemStack.Empty, 3 + 36).ToArray();
        fs[0] = new ItemStack(Blocks.Cobblestone, 3, 0);
        fs[1] = new ItemStack(Items.Coal, 5, 0);
        inv.SetWindowItems(2, fs);
        inv.Close(2);
        f.Set(3, 64, 0, Blocks.LitFurnace);
        f.Pump();
        var furnace = f.Row("furnace", K(3, 64, 0))!;
        output.WriteLine(furnace.Text);
        Assert.Contains("(lit)", furnace.Text);
        Assert.Contains("last: 3 cobblestone -> stone", furnace.Text);
        Assert.Contains("near chest", furnace.Text);
    }

    [Fact]
    public void Generator_detected_then_confirmed_by_regen()
    {
        using var f = new IndexFixture();
        f.Island();
        var dig = f.Generator(-2, 64, -2);
        f.Pump();
        var key = K(dig.X, dig.Y, dig.Z);
        var row = f.Row("cobble_generator", key);
        Assert.NotNull(row);
        Assert.Contains("unconfirmed", row!.Text);
        Assert.Contains($"dig {dig.X},{dig.Y},{dig.Z}", row.Text);
        Assert.Contains("stand", row.Text);
        Assert.True(row.Confidence < 0.9);
        Assert.True(f.Indexer.IsProtected(new BlockPos(-2, 64, -2), Blocks.Lava));       // lava
        Assert.True(f.Indexer.IsProtected(new BlockPos(0, 64, -2), Blocks.Water));       // water
        Assert.True(f.Indexer.IsProtected(new BlockPos(-3, 64, -2), Blocks.Cobblestone)); // wall holding the lava
        Assert.False(f.Indexer.IsProtected(dig, Blocks.Cobblestone));                    // the dig cell is minable

        f.Set(dig.X, dig.Y, dig.Z, 0);           // dug
        f.Clock.Seconds(1);
        f.Set(dig.X, dig.Y, dig.Z, Blocks.FlowingLava, 2);
        f.Clock.Seconds(0.5);
        f.Set(dig.X, dig.Y, dig.Z, Blocks.Cobblestone); // refilled
        f.Pump();
        row = f.Row("cobble_generator", key)!;
        output.WriteLine(row.Text);
        Assert.Contains("confirmed (1 regens)", row.Text);
        Assert.True(row.Confidence > 0.9);
        Assert.Single(f.Store.Nearest("cobble_generator", dig.X, dig.Y, dig.Z, 5, 10));
    }

    [Fact]
    public void Generator_not_confirmed_when_refill_is_too_late()
    {
        using var f = new IndexFixture();
        f.Island();
        var dig = f.Generator(-2, 64, -2);
        f.Pump();
        f.Set(dig.X, dig.Y, dig.Z, 0);
        f.Clock.Seconds(60);
        f.Set(dig.X, dig.Y, dig.Z, Blocks.Cobblestone); // a player re-placed it a minute later
        f.Pump();
        Assert.Contains("unconfirmed", f.Row("cobble_generator", K(dig.X, dig.Y, dig.Z))!.Text);
    }

    [Fact]
    public void Farms_trees_and_sugar_cane()
    {
        using var f = new IndexFixture();
        f.Island(0, 0, 6);
        for (int x = 1; x <= 3; x++)
        for (int z = 1; z <= 2; z++) { f.Set(x, 63, z, Blocks.Farmland); f.Set(x, 64, z, Blocks.Crops, x == 1 ? 7 : 3); }
        f.Set(4, 63, 1, Blocks.Water);
        f.Set(-4, 63, -4, Blocks.Sand);
        f.Set(-4, 64, -4, Blocks.SugarCane);
        f.Set(-4, 65, -4, Blocks.SugarCane);
        f.Set(-5, 63, -4, Blocks.Water);
        f.Fill(-4, 64, 4, -4, 67, 4, Blocks.Log);
        f.Set(-2, 64, 5, Blocks.Sapling);
        f.Pump();

        var farms = f.Store.Nearest("farm", 0, 64, 0, 5, 30).Select(t => t.Row).ToList();
        foreach (var r in farms) output.WriteLine(r.Text);
        var wheat = farms.Single(r => r.Key.StartsWith("wheat@"));
        Assert.Contains("crops 6 (2 ripe)", wheat.Text);
        Assert.Contains("by water", wheat.Text);
        var cane = farms.Single(r => r.Key.StartsWith("sugar_cane@"));
        Assert.Contains("sugar cane 2 (max h2)", cane.Text);
        Assert.Contains("on sand", cane.Text);
        Assert.True(f.Indexer.IsProtected(new BlockPos(1, 63, 1), Blocks.Farmland));
        Assert.True(f.Indexer.IsProtected(new BlockPos(-4, 63, -4), Blocks.Sand));   // cane soil
        Assert.True(f.Indexer.IsProtected(new BlockPos(4, 63, 1), Blocks.Water));    // irrigation
        Assert.False(f.Indexer.IsProtected(new BlockPos(1, 64, 1), Blocks.Crops));   // harvestable
        var tree = f.Store.Nearest("tree", -4, 64, 4, 1, 10).Single().Row;
        output.WriteLine(tree.Text);
        Assert.Contains("1 trees, 4 logs, 1 saplings", tree.Text);
    }

    [Fact]
    public void Islands_segment_and_update_incrementally()
    {
        using var f = new IndexFixture(3);
        f.Island(0, 0, 3);
        f.Island(30, 0, 1); // small second island, 3x3
        f.Pump();
        var islands = f.Store.Nearest("island", 0, 60, 0, 10, 200).Select(t => t.Row).ToList();
        foreach (var r in islands) output.WriteLine(r.Text);
        Assert.Equal(2, islands.Count);
        var main = islands.Single(r => r.Text.StartsWith("main island"));
        Assert.Contains("(49 cols)", main.Text);
        Assert.Contains("void edges N7 E7 S7 W7", main.Text);
        var mainKey = main.Key;

        // Grow the main island by one column: same key, new size. Only that column is reprocessed.
        int scanned = (int)f.Indexer.Stats.ColumnsScanned;
        f.Set(4, 63, 0, Blocks.Cobblestone);
        f.Clock.Seconds(1);
        f.Pump();
        Assert.Equal(scanned, (int)f.Indexer.Stats.ColumnsScanned); // single block change, no column rescan
        Assert.Contains("(50 cols)", f.Row("island", mainKey)!.Text);

        // Bridge them: one island (the main key survives), the small one is tombstoned.
        f.Fill(5, 63, 0, 28, 63, 0, Blocks.Cobblestone);
        f.Pump();
        var after = f.Store.Nearest("island", 0, 60, 0, 10, 200).Select(t => t.Row).ToList();
        Assert.Single(after);
        Assert.Equal(mainKey, after[0].Key);

        // Break the bridge: they split again; the bigger part keeps the key.
        f.Set(15, 63, 0, 0);
        f.Pump();
        after = f.Store.Nearest("island", 0, 60, 0, 10, 200).Select(t => t.Row).ToList();
        Assert.Equal(2, after.Count);
        Assert.Contains(after, r => r.Key == mainKey);
    }

    [Fact]
    public void Signs_label_the_nearest_thing_and_go_away_with_the_block()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(1, 64, 1, Blocks.Chest);
        f.Set(1, 64, 0, Blocks.WallSign, 2); // hangs on the chest's north face (block at z+1)
        f.Feed.SignTexts[new BlockPos(1, 64, 0)] = ["§1Iron", "storage", "", ""];
        f.Pump();
        var chest = f.Row("chest", K(1, 64, 1))!;
        output.WriteLine(chest.Text);
        Assert.Contains("\"Iron storage\"", chest.Text);
        var sign = f.Row("sign", K(1, 64, 0))!;
        output.WriteLine(sign.Text);
        Assert.Contains("on chest@1,64,1", sign.Text);

        f.Set(1, 64, 0, 0);
        f.Pump();
        Assert.Null(f.Row("sign", K(1, 64, 0)));
        Assert.DoesNotContain("Iron", f.Row("chest", K(1, 64, 1))!.Text);
    }

    [Fact]
    public void Tombstones_when_block_disappears_and_revives_on_return()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(2, 64, 2, Blocks.Workbench);
        f.Pump();
        Assert.NotNull(f.Row("crafting_table", K(2, 64, 2)));
        f.Set(2, 64, 2, 0);
        f.Pump();
        Assert.Null(f.Row("crafting_table", K(2, 64, 2)));
        Assert.NotNull(f.Store.Get("crafting_table", K(2, 64, 2), includeRemoved: true)!.Removed);
        f.Set(2, 64, 2, Blocks.Workbench);
        f.Pump();
        Assert.NotNull(f.Row("crafting_table", K(2, 64, 2)));
    }

    [Fact]
    public void Persisted_rows_for_vanished_blocks_are_tombstoned_on_first_scan()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Store.Upsert(new Observation("chest", K(3, 64, 3), "chest at 3,64,3") { Bounds = Box.Point(3, 64, 3) }); // from an earlier session
        f.Pump();
        Assert.Null(f.Row("chest", K(3, 64, 3)));
    }

    [Fact]
    public void Taught_entries_are_annotated_never_overwritten()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(1, 64, 1, Blocks.Chest);
        f.Store.Upsert(new Observation("chest", K(1, 64, 1), "diamond vault (don't touch)") { Bounds = Box.Point(1, 64, 1), Source = ObservationSource.Taught });
        var dig = f.Generator(-2, 64, -2);
        f.Store.Upsert(new Observation("cobble_generator", "main_gen", "the main cobble gen, mine from the west")
            { Bounds = Box.Of(-3, 64, -3, 1, 64, -1), Source = ObservationSource.Llm });
        f.Pump();

        var chest = f.Row("chest", K(1, 64, 1))!;
        Assert.Equal("diamond vault (don't touch)", chest.Text);
        Assert.Equal(ObservationSource.Taught, chest.Source);
        Assert.Contains("chest at 1,64,1", chest.Annotation);
        // The indexer adopted the llm generator row (overlap) instead of creating a duplicate; it only annotates it.
        var gen = f.Row("cobble_generator", "main_gen")!;
        Assert.Equal("the main cobble gen, mine from the west", gen.Text);
        Assert.Contains("cobble_generator at", gen.Annotation);
        Assert.Null(f.Row("cobble_generator", K(dig.X, dig.Y, dig.Z)));

        // Block gone: taught row is annotated "not seen", never tombstoned.
        f.Set(1, 64, 1, 0);
        f.Pump();
        chest = f.Row("chest", K(1, 64, 1))!;
        Assert.Contains("not seen", chest.Annotation);
    }

    [Fact]
    public void Unknown_structures_are_flagged_for_the_brain()
    {
        using var f = new IndexFixture();
        f.Island(0, 0, 5);
        var found = new List<UnknownStructure>();
        f.Indexer.UnknownStructureFound += found.Add;
        // A little glass-and-plank hut with cobble walls.
        f.Fill(2, 64, 2, 4, 66, 2, Blocks.Cobblestone);
        f.Fill(2, 64, 4, 4, 66, 4, Blocks.Planks);
        f.Set(3, 65, 3, Blocks.Glass);
        f.Set(2, 65, 3, Blocks.Glass);
        f.Set(4, 65, 3, Blocks.Torch);
        // A one-thick cobble floor patch elsewhere is not a structure.
        f.Fill(-5, 63, -5, -3, 63, -3, Blocks.Cobblestone);
        f.Pump();
        var rows = f.Store.Nearest("unknown_structure", 0, 64, 0, 5, 20).Select(t => t.Row).ToList();
        foreach (var r in rows) output.WriteLine(r.Text);
        var hut = Assert.Single(rows);
        Assert.Contains("ask players", hut.Text);
        Assert.Contains("\"ask\":true", hut.FieldsJson);
        Assert.Single(found);
        Assert.Single(f.Indexer.PendingUnknownStructures);
        Assert.True(f.Indexer.IsProtected(new BlockPos(2, 65, 2), Blocks.Cobblestone)); // their build is not ours to mine
        f.Indexer.MarkAsked(hut.Key);
        Assert.Empty(f.Indexer.PendingUnknownStructures);
    }

    [Fact]
    public void Chunk_bursts_are_batched_and_unchanged_rescans_dont_write()
    {
        using var f = new IndexFixture(4);
        f.Island();
        for (int i = 0; i < 30; i++) f.Set(-30 + i * 2, 64, 20, Blocks.Chest);
        f.Indexer.PumpAll();
        int calls = f.Sink.UpsertCalls;
        output.WriteLine($"{f.Indexer.Stats} sink calls {calls} upserted {f.Sink.Upserted}");
        Assert.True(f.Sink.Upserted >= 31);
        Assert.True(calls <= 3, $"{calls} transactions for a burst");

        // A full re-send of the same chunks: rescanned, but nothing changed → no writes.
        f.World.ApplyChunkRegion(-64, 0, -64, 1, 1, 1, [0], null, null, null); // touches one column
        foreach (var c in f.World.Columns) f.World.ApplyChunkRegion(c.ChunkX * 16, 127, c.ChunkZ * 16, 16, 1, 16, new byte[256], null, null, null);
        f.Clock.Seconds(5);
        f.Indexer.PumpAll();
        Assert.Equal(calls, f.Sink.UpsertCalls);

        // After the refresh interval the same rescan touches last_seen once (one batched write).
        f.Clock.Advance(TimeSpan.FromMinutes(11));
        foreach (var c in f.World.Columns) f.World.ApplyChunkRegion(c.ChunkX * 16, 127, c.ChunkZ * 16, 16, 1, 16, new byte[256], null, null, null);
        f.Indexer.PumpAll();
        Assert.True(f.Sink.UpsertCalls - calls <= 2);
        Assert.True(f.Row("chest", K(-30, 64, 20))!.SeenCount >= 2);
    }

    [Fact]
    public void Column_scan_cost_is_small()
    {
        using var f = new IndexFixture(4);
        var rnd = new Random(1);
        var ids = new byte[128 * 128 * 128];
        // Dense synthetic terrain: every column 0..70 filled with mixed blocks (worst case vs skyblock's mostly air).
        for (int x = 0; x < 128; x++)
        for (int z = 0; z < 128; z++)
        for (int y = 0; y < 70; y++)
            ids[(x * 128 + z) * 128 + y] = (byte)(new[] { Blocks.Stone, Blocks.Dirt, Blocks.Cobblestone, Blocks.Sand, Blocks.Log })[rnd.Next(5)];
        f.World.ApplyChunkRegion(-64, 0, -64, 128, 128, 128, ids, null, null, null);
        var sw = Stopwatch.StartNew();
        f.Indexer.PumpAll(1000);
        output.WriteLine($"{f.Indexer.Stats}; total {sw.ElapsedMilliseconds} ms");
        Assert.True(f.Indexer.Stats.ColumnsScanned >= 64);
        Assert.True(f.Indexer.Stats.MsPerColumn < 25, $"{f.Indexer.Stats.MsPerColumn} ms/column");
    }

    [Fact]
    public void Protection_predicate_covers_stations_and_generator_fluids_only()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(1, 64, 1, Blocks.Chest);
        var dig = f.Generator(-2, 64, -2);
        f.Pump();
        Assert.True(f.Indexer.IsProtected(new BlockPos(1, 64, 1), Blocks.Chest));
        Assert.False(f.Indexer.IsProtected(new BlockPos(0, 63, 0), Blocks.Grass));
        Assert.False(f.Indexer.IsProtected(dig, Blocks.Cobblestone));
        f.Set(1, 64, 1, 0);
        f.Pump();
        Assert.False(f.Indexer.IsProtected(new BlockPos(1, 64, 1), 0));
    }
}
