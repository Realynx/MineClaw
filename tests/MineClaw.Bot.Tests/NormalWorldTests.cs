using MineClaw.Bot.Brainstem;
using MineClaw.Bot.Building;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Memory;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Tests.WorldIndex;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Multi-version P6: a DEFAULT generated world (ground under everything, natural stone, caves with lava by water, deserts,
/// plentiful trees). Skyblock's assumptions (every stone block was placed, lava beside water is a cobble generator, unusual
/// blocks are player builds, dirt under trees is unrenewable, the edge is the void) must not fire there: natural terrain
/// indexes no floors, islands, generators or unknown structures and protects nothing; stone is mined like any resource; the
/// bootstrap (wood → table → wooden pickaxe → cobble → stone pickaxe/furnace) mines stone instead of asking for a gen.
/// </summary>
public class NormalWorldTests
{
    private const int Top = 54; // grass

    /// <summary>One 16x128x16 column of natural terrain at chunk (0,0): bedrock, stone, a cave lava pool beside water, a dungeon
    /// cobble/mossy wall, a sandstone desert patch, dirt and grass, an oak and an acacia (log2) tree.</summary>
    private static byte[] Terrain(Action<Func<int, int, int, int>, Action<int, int, int, int>>? edit = null)
    {
        const int h = 128;
        var ids = new byte[16 * h * 16];
        int I(int x, int y, int z) => (x * 16 + z) * h + y;
        void Set(int x, int y, int z, int id) => ids[I(x, y, z)] = (byte)id;
        int Get(int x, int y, int z) => ids[I(x, y, z)];
        for (int x = 0; x < 16; x++)
        for (int z = 0; z < 16; z++)
        {
            Set(x, 0, z, Blocks.Bedrock);
            for (int y = 1; y <= 50; y++) Set(x, y, z, Blocks.Stone);
            bool desert = x >= 10 && z >= 10;
            for (int y = 51; y < Top; y++) Set(x, y, z, desert ? Blocks.Sandstone : Blocks.Dirt);
            Set(x, Top, z, desert ? Blocks.Sand : Blocks.Grass);
        }
        // A cave pool: lava | stone | water (the stone in between is a "gen cell" by geometry).
        Set(5, 30, 5, Blocks.Lava);
        Set(7, 30, 5, Blocks.Water);
        Set(5, 31, 5, 0); Set(6, 31, 5, 0); Set(7, 31, 5, 0);
        // A dungeon wall: a cobble/mossy pillar (skyblock: a player's wall).
        for (int y = 20; y <= 23; y++) { Set(2, y, 12, Blocks.Cobblestone); Set(3, y, 12, Blocks.MossyCobblestone); }
        // An oak and an acacia (1.12.2 log2).
        for (int y = Top + 1; y <= Top + 4; y++) { Set(8, y, 2, Blocks.Log); Set(13, y, 3, Ids1122.Log2); }
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            Set(8 + dx, Top + 4, 2 + dz, Blocks.Leaves);
            Set(13 + dx, Top + 4, 3 + dz, Ids1122.Leaves2);
        }
        edit?.Invoke(Get, Set);
        return ids;
    }

    private static void Load(IndexFixture f, byte[] ids) => f.World.ApplyChunkRegion(0, 0, 0, 16, 128, 16, ids, null, null, null);

    [Fact]
    public void Natural_terrain_indexes_no_floors_islands_generators_or_unknown_structures_and_protects_nothing()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        using var data = GameData.Override(Release1122GameData.Instance);
        using var f = new IndexFixture(chunkRadius: 1);
        _ = f.Indexer;
        Load(f, Terrain());
        f.Pump();
        var census = f.Indexer.Census();
        Assert.False(census.ContainsKey(IndexKinds.Island), string.Join(", ", census));
        Assert.False(census.ContainsKey(IndexKinds.Generator), string.Join(", ", census));
        Assert.False(census.ContainsKey(IndexKinds.UnknownStructure), string.Join(", ", census));
        Assert.Equal(0, f.Indexer.ProtectedCount);
        Assert.Null(f.Indexer.ProtectionKind(new BlockPos(6, 30, 5)));  // the cave "gen cell"
        Assert.Null(f.Indexer.ProtectionKind(new BlockPos(8, Top, 2)));  // the soil under the oak
        Assert.Null(f.Indexer.ProtectionKind(new BlockPos(12, 52, 12))); // desert sandstone
        // Both trees are known (acacia = log2), as resources.
        Assert.True(census.GetValueOrDefault(IndexKinds.Tree) >= 2, string.Join(", ", census));

        // A block someone is SEEN placing is a floor (soft); lava hardening into cobble in a cave is not.
        f.Set(3, Top + 1, 3, Blocks.Cobblestone);
        f.Set(9, 40, 9, Blocks.Lava);
        f.Set(9, 40, 9, Blocks.Cobblestone);
        f.Pump();
        Assert.Equal(WorldIndexer.FloorKind, f.Indexer.ProtectionKind(new BlockPos(3, Top + 1, 3)));
        Assert.Null(f.Indexer.ProtectionKind(new BlockPos(9, 40, 9)));

        // The server resends the chunk (with the placed block in it): the seen placement stays a floor, terrain stays terrain.
        Load(f, Terrain((_, set) => { set(3, Top + 1, 3, Blocks.Cobblestone); set(9, 40, 9, Blocks.Cobblestone); }));
        f.Pump();
        Assert.Equal(WorldIndexer.FloorKind, f.Indexer.ProtectionKind(new BlockPos(3, Top + 1, 3)));
        Assert.Equal(1, f.Indexer.ProtectedCount);
    }

    [Fact]
    public void The_same_terrain_on_skyblock_is_still_read_as_placed_blocks()
    {
        // Control: skyblock (the default) keeps its rules (every stone/build block was placed, lava beside water is a gen).
        using var f = new IndexFixture(chunkRadius: 1);
        _ = f.Indexer;
        Load(f, Terrain());
        f.Pump();
        Assert.True(f.Indexer.ProtectedCount > 0);
        Assert.True(f.Indexer.Census().ContainsKey(IndexKinds.Generator));
    }

    [Fact]
    public void A_normal_world_floor_refusal_says_floor_not_station()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var p = new Protection(new BotOptions(), new NoMemory(), new NoMemory()) { IndexerKind = _ => WorldIndexer.FloorKind };
        var v = p.Check(new BlockPos(1, 64, 1), Blocks.Stone);
        Assert.True(v.Protected);
        Assert.Equal("floor", v.Reason);
    }

    [Fact]
    public void Protection_knows_the_1_12_2_crops_and_containers()
    {
        using var data = GameData.Override(Release1122GameData.Instance);
        Assert.True(Protection.IsFarmPlant(Ids1122.Carrots));
        Assert.True(Protection.IsFarmPlant(Ids1122.Beetroots));
        Assert.True(Protection.IsNaturalFill(Ids1122.Leaves2));
        Assert.True(Protection.IsContainer(Ids1122.TrappedChest));
        Assert.True(Protection.IsContainer(Ids1122.Hopper));
        Assert.True(Protection.IsContainer(Ids1122.ShulkerFirst + 3));
        var meta = 2;
        var p = new Protection(new BotOptions(), new NoMemory(), new NoMemory()) { BlockMeta = _ => meta };
        Assert.Contains("beetroot", p.UnripeReason(new BlockPos(0, 64, 0), Ids1122.Beetroots));
        meta = 3;
        Assert.Null(p.UnripeReason(new BlockPos(0, 64, 0), Ids1122.Beetroots));
        Assert.Equal(IndexKinds.Chest, IndexKinds.StationKind(Ids1122.TrappedChest));
        Assert.Equal(IndexKinds.Hopper, IndexKinds.StationKind(Ids1122.Hopper));
    }

    // ---------------------------------------------------------------- mining and the bootstrap

    private static void Wire(FakeGameClient c)
    {
        int Last(string verb, int field) => int.Parse(c.CallList.Last(x => x.StartsWith(verb + " ", StringComparison.Ordinal)).Split(' ')[field]);
        c.Effects["craft"] = () => AcquisitionTests.Add(c, Last("craft", 1), Math.Max(1, Last("craft", 2)));
        c.Effects["chop"] = () => AcquisitionTests.Add(c, Blocks.Log, 5);
        c.Effects["mine"] = () => { if (c.CallList.Last(x => x.StartsWith("mine ")).StartsWith("mine stone ")) AcquisitionTests.Add(c, Blocks.Cobblestone, Last("mine", 2)); };
        c.Effects["place"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("place ")).Split(' ');
            var p = parts[1].Split(',').Select(int.Parse).ToArray();
            c.World.SetBlock(p[0], p[1], p[2], int.Parse(parts[2]), 0);
        };
    }

    private static (BotHost H, FakeGameClient C) Ground()
    {
        var (h, c) = TestBot.Create();
        c.Floor(64, 12, Blocks.Grass); // grass on stone: trees grow on it, stone is "mined" by the fake client
        // A cave pool right here: lava | stone | water. Skyblock would call the stone a generator cell.
        c.World.SetBlock(5, 64, 0, Blocks.Lava, 0);
        c.World.SetBlock(5, 64, 1, Blocks.Stone, 0);
        c.World.SetBlock(5, 64, 2, Blocks.Water, 0);
        foreach (var t in new BlockPos[] { new(-6, 65, -6), new(-6, 65, 6), new(6, 65, -7) })
        {
            for (int y = 0; y < 4; y++) c.World.SetBlock(t.X, t.Y + y, t.Z, Blocks.Log, 0);
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) c.World.SetBlock(t.X + dx, t.Y + 3, t.Z + dz, Blocks.Leaves, 0);
            c.World.SetBlock(t.X, t.Y + 4, t.Z, Blocks.Leaves, 0);
        }
        Wire(c);
        return (h, c);
    }

    [Fact]
    public async Task Mine_stone_in_a_normal_world_digs_natural_stone_with_a_pickaxe()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (h, c) = Ground();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        var r = await h.Run("mine", new { block = "cobblestone", count = 4 });
        Assert.True(r.Ok, r.Text);
        Assert.Contains(c.CallList, x => x.StartsWith("mine stone 4 ", StringComparison.Ordinal));
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("generator", StringComparison.Ordinal));
        Assert.DoesNotContain("needs_input", r.Text);
    }

    [Fact]
    public async Task On_skyblock_mine_stone_still_goes_to_the_generator()
    {
        var (h, c) = Ground();
        using var _ = h;
        c.Give(36, Items.StonePickaxe, 1);
        await h.Run("mine", new { block = "stone", count = 4 });
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("mine stone 4 ", StringComparison.Ordinal));
        Assert.Contains(c.CallList, x => x.StartsWith("generator", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_pickaxe_bootstrap_in_a_normal_world_mines_stone_for_the_stone_pick()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (h, c) = Ground();
        using var _ = h;
        var plans = new List<string>();
        h.Acquisition!.Announce = plans.Add;
        var r = await Acquisition.Planner(c).AcquireAsync(new Need("pickaxe"), default);
        Assert.True(r.Ok, r.Text);
        Assert.True(c.World.Inventory.CountItem(Items.StonePickaxe) > 0, string.Join(" | ", c.CallList));
        var calls = c.CallList;
        int woodPick = calls.IndexOf($"craft {Items.WoodenPickaxe} 1"), stone = calls.FindIndex(x => x.StartsWith("mine stone 3 "));
        int stonePick = calls.IndexOf($"craft {Items.StonePickaxe} 1");
        Assert.True(woodPick >= 0 && woodPick < stone && stone < stonePick, string.Join(" | ", calls));
        Assert.DoesNotContain(calls, x => x.StartsWith("generator", StringComparison.Ordinal));
        Assert.Equal("no pick, making one: logs→planks→sticks→table→wood pick→3 cobble→stone pick", Assert.Single(plans));
    }

    [Fact]
    public async Task A_furnace_in_a_normal_world_is_crafted_from_mined_stone()
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (h, c) = Ground();
        using var _ = h;
        var asked = new List<string>();
        h.Acquisition!.AskPlayer = (q, _, _) => { asked.Add(q); return Task.FromResult<string?>(null); };
        var r = await Acquisition.Planner(c).AcquireAsync(new Need("furnace"), default);
        Assert.True(r.Ok, r.Text + " | " + string.Join(" | ", c.CallList));
        Assert.True(c.World.Inventory.CountItem(Blocks.Furnace) > 0);
        Assert.Contains(c.CallList, x => x.StartsWith("mine stone ", StringComparison.Ordinal));
        Assert.Contains($"craft {Blocks.Furnace} 1", c.CallList);
        Assert.Empty(asked);
    }

    // ---------------------------------------------------------------- food, build hints, vision, here

    [Fact]
    public void Food_and_fuel_lists_follow_the_version()
    {
        Assert.DoesNotContain(393, FoodSupply.Ready); // b1.8.1: no baked potato
        using var data = GameData.Override(Release1122GameData.Instance);
        foreach (var id in new[] { 393, 391, 424, 412, 434, 400 }) Assert.Contains(id, FoodSupply.Ready);
        foreach (var id in new[] { 423, 411, 392 }) Assert.Contains(id, FoodSupply.Raw);
        Assert.DoesNotContain(Items.GoldenApple, FoodSupply.Ready);
        Assert.DoesNotContain(367, FoodSupply.Ready); // rotten flesh
        Assert.Equal(Items.CookedPorkchop, FoodSupply.Ready[0]); // b1.8.1's order first
    }

    [Fact]
    public void A_build_past_the_scaffold_limit_suggests_a_foundation_not_an_island_in_a_normal_world()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        var cells = new List<BlockPos>();
        for (int x = 0; x < 3; x++) for (int z = 0; z < 3; z++) cells.Add(new BlockPos(x, 70, z));
        var items = cells.Select(p => (p, (string?)"no support within 6 scaffold blocks (nothing to build up from)")).ToList();
        var fp = BuildSkips.Footprint(cells);
        Assert.Equal(BuildSkips.OverVoid, BuildSkips.Category(items[0].Item2));
        Assert.StartsWith("lay a floor: expand_island", BuildSkips.FloorHint(items, fp, w));
        using var mode = WorldMode.Override(WorldMode.Normal);
        Assert.Equal(BuildSkips.NoSupport, BuildSkips.Category(items[0].Item2));
        var hint = BuildSkips.FloorHint(items, fp, w);
        Assert.StartsWith("lay a foundation: fill_floor from=0,69,0 to=2,69,2", hint);
        Assert.DoesNotContain("expand_island", hint);
    }

    [Fact]
    public void Vision_names_no_gen_cells_or_void_by_geometry_in_a_normal_world()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -3; x <= 3; x++) for (int z = -3; z <= 3; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        w.SetBlock(0, 64, 0, Blocks.Lava, 0);
        w.SetBlock(1, 64, 0, Blocks.Stone, 0);
        w.SetBlock(2, 64, 0, Blocks.Water, 0);
        var roles = RoleClassifier.For(w, null, _ => false, _ => null);
        Assert.Equal(BlockRole.GenCell, roles.RoleOf(1, 64, 0));   // skyblock: geometry is a gen
        Assert.Equal(BlockRole.Void, roles.RoleOf(10, 64, 10));
        using var mode = WorldMode.Override(WorldMode.Normal);
        Assert.NotEqual(BlockRole.GenCell, roles.RoleOf(1, 64, 0));
        Assert.NotEqual(BlockRole.Void, roles.RoleOf(10, 64, 10));
        Assert.Equal(BlockRole.CropGrowing, RoleClassifier.CropRole(Ids1122.Carrots, 3));
        Assert.Equal(BlockRole.CropRipe, RoleClassifier.CropRole(Ids1122.Beetroots, 3));
    }

    [Fact]
    public void The_here_block_says_no_void_edges_in_a_normal_world()
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) w.SetBlock(x, 63, z, Blocks.Stone, 0);
        var me = new Vec3d(0.5, 64, 0.5);
        Assert.StartsWith("void ", HereBlock.VoidEdges(w, me));
        using var mode = WorldMode.Override(WorldMode.Normal);
        Assert.Equal("", HereBlock.VoidEdges(w, me));
    }
}
