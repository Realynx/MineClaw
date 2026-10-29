using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Client.Trees;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>Multi-version P6: a default 1.12.2 world (acacia/dark oak trees, natural stone, no void) and the 1.9 weapon ranking.</summary>
public class NormalWorldClientTests
{
    private static WorldState Flat()
    {
        var w = new WorldState(height: 256);
        w.ApplyChunkRegion(-16, 0, -16, 32, 256, 32, new byte[32 * 256 * 32], null, null, null);
        for (int x = -8; x <= 8; x++)
        for (int z = -8; z <= 8; z++)
            w.SetBlock(x, 63, z, Blocks.Grass, 0);
        return w;
    }

    private static void Crown(WorldState w, int cx, int top, int cz, int leaves, int meta)
    {
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        for (int y = top - 1; y <= top + 1; y++)
            if (w.GetBlock(cx + dx, y, cz + dz) == 0) w.SetBlock(cx + dx, y, cz + dz, leaves, meta);
    }

    [Fact]
    public void Acacia_log2_tree_is_seen_with_its_leaves2_crown()
    {
        using var _ = GameData.Override(Release1122GameData.Instance);
        var w = Flat();
        for (int y = 64; y <= 68; y++) w.SetBlock(0, y, 0, Wood.Log2, 0); // log2 meta 0 = acacia
        Crown(w, 0, 68, 0, Wood.Leaves2, 0);
        var t = TreeScan.Analyze(w, new BlockPos(0, 66, 0))!;
        Assert.Equal(Wood.Acacia, t.Wood);
        Assert.Equal("acacia", t.WoodName);
        Assert.Equal(5, t.Logs.Count);
        Assert.True(t.Leaves.Count > 10);
        Assert.Single(TreeScan.FindTrees(w, new Vec3d(0.5, 64, 0.5), 8, wood: Wood.Acacia));
        Assert.Empty(TreeScan.FindTrees(w, new Vec3d(0.5, 64, 0.5), 8, wood: Wood.Oak));
        Assert.Equal(new BlockPos(0, 64, 0), TreeScan.LogNear(w, new BlockPos(1, 63, 1)));
        Assert.Equal(Wood.Log2, Wood.LogId(t.Wood));
        Assert.Equal(0, Wood.LogDamage(t.Wood));
    }

    [Fact]
    public void Dark_oak_2x2_trunk_is_one_tree()
    {
        using var _ = GameData.Override(Release1122GameData.Instance);
        var w = Flat();
        foreach (var (x, z) in new[] { (0, 0), (1, 0), (0, 1), (1, 1) })
            for (int y = 64; y <= 69; y++) w.SetBlock(x, y, z, Wood.Log2, 1 | 4 * (y % 2)); // log2 meta 1 = dark oak (+axis bits)
        Crown(w, 0, 69, 0, Wood.Leaves2, 1);
        Crown(w, 1, 69, 1, Wood.Leaves2, 1);
        var trees = TreeScan.FindTrees(w, new Vec3d(0.5, 64, 0.5), 8);
        var t = Assert.Single(trees);
        Assert.Equal(Wood.DarkOak, t.Wood);
        Assert.Equal("dark oak", t.WoodName);
        Assert.Equal(24, t.Logs.Count);
        Assert.Equal(4, t.Trunk.Count);
        Assert.True(Wood.NeedsFourSaplings(t.Wood));
    }

    [Fact]
    public void Wood_names_parse_every_species()
    {
        Assert.Equal(3, TreeScan.ParseWood("jungle"));
        Assert.Equal(4, TreeScan.ParseWood("acacia"));
        Assert.Equal(5, TreeScan.ParseWood("dark oak"));
        Assert.Equal(1, TreeScan.ParseWood("pine"));
        Assert.Equal("jungle", TreeScan.WoodName(3));
        Assert.Equal(Wood.DarkOak, Wood.Type(Wood.Leaves2, 1 | 8));
        Assert.Equal(Wood.Birch, Wood.Type(Blocks.Log, 2 | 4)); // axis bits ignored
    }

    [Fact]
    public void Stewardship_guards_only_where_trees_are_scarce()
    {
        var t = new TreeInfo(new BlockPos(0, 64, 0), new BlockPos(0, 63, 0), Blocks.Grass, 0,
            new HashSet<BlockPos> { new(0, 64, 0) }, new HashSet<BlockPos> { new(1, 65, 0), new(-1, 65, 0), new(0, 66, 0) });
        var census = new TreeCensus([t], []);
        Assert.NotNull(TreeStewardship.Refusal(census, t, 0, null, 2));
        Assert.NotEmpty(TreeStewardship.GuardedLogs(census, 2));
        using var _ = WorldMode.Override(WorldMode.Normal);
        Assert.Null(TreeStewardship.Refusal(census, t, 0, null, 2));
        Assert.Empty(TreeStewardship.GuardedLogs(census, 2));
    }

    [Fact]
    public async Task Normal_world_mine_takes_natural_stone_skyblock_does_not()
    {
        static void Setup(FakeServer srv)
        {
            srv.Blocks[(9, 64, 9)] = Blocks.Stone;
            srv.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
            srv.DropOnDig = true;
        }
        {
            var (c, s) = await Harness.StartAsync(Setup);
            await using var _ = c;
            using var __ = s;
            var r = await c.MineAsync("stone", 1, 6);
            Assert.Equal(0, r.Count); // skyblock: someone placed that stone
            Assert.Contains("generator", r.Message);
        }
        using var mode = WorldMode.Override(WorldMode.Normal);
        {
            var (c, s) = await Harness.StartAsync(Setup);
            await using var _ = c;
            using var __ = s;
            var r = await c.MineAsync("stone", 1, 6);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(1, r.Count);
        }
    }

    [Fact]
    public void Natural_terrain_and_1122_ores_are_resources()
    {
        Assert.True(MinecraftClient.NaturalResource(129, 0));  // emerald ore
        Assert.True(MinecraftClient.NaturalResource(153, -1)); // nether quartz ore
        Assert.True(MinecraftClient.NaturalResource(Wood.Leaves2, 0));
        Assert.True(MinecraftClient.NaturalTerrain(Blocks.Dirt) && MinecraftClient.NaturalTerrain(Blocks.Stone));
    }

    // ------------------------------------------------------------------ weapons / spar on 1.9+ rules

    private static ItemStack[] Slots(params (int Slot, int Id)[] items)
    {
        var s = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
        foreach (var (slot, id) in items) s[slot] = new ItemStack((short)id, 1, 0);
        return s;
    }

    [Fact]
    public void On_1122_a_sword_beats_an_axe_on_dps_though_the_axe_hits_harder()
    {
        var r = Release1122CombatRules.Instance;
        var slots = Slots((36, Items.StoneAxe), (37, Items.StoneSword));
        Assert.True(r.AttackDamage(Items.StoneAxe) > r.AttackDamage(Items.StoneSword));
        Assert.True(WeaponChoice.Dps(Items.StoneSword, r) > WeaponChoice.Dps(Items.StoneAxe, r));
        Assert.Equal(Items.StoneSword, WeaponChoice.Best(slots, new HashSet<int>(), new HashSet<int>(), r).ItemId);
        // Diamond axe (9 per hit, 1.0/s) vs iron sword (6 per hit, 1.6/s): the sword still wins on DPS.
        slots = Slots((36, Items.DiamondAxe), (37, Items.IronSword));
        Assert.Equal(Items.IronSword, WeaponChoice.Best(slots, new HashSet<int>(), new HashSet<int>(), r).ItemId);
    }

    [Fact]
    public void B181_ranking_is_unchanged()
    {
        var slots = Slots((36, Items.StoneAxe), (37, Items.WoodenSword));
        var none = new HashSet<int>();
        Assert.Equal(WeaponChoice.Best(slots, none, none), WeaponChoice.Best(slots, none, none, MineClaw.Client.Versioning.Beta181CombatRules.Instance));
        Assert.Null(WeaponChoice.Dps(Items.StoneSword, MineClaw.Client.Versioning.Beta181CombatRules.Instance));
    }

    [Fact]
    public void Spar_estimate_uses_the_1122_charged_damage_and_crit()
    {
        var o = new CombatOptions { SparDamageMargin = 0, SparArmorTrust = 1 };
        var b = new OpponentHpModel(o);
        var beta = new OpponentHpModel(o, rules: MineClaw.Client.Versioning.Beta181CombatRules.Instance);
        Assert.Equal(b.WorstHit(Items.IronSword, true, []), beta.WorstHit(Items.IronSword, true, []));
        var m = new OpponentHpModel(o, rules: Release1122CombatRules.Instance);
        // 1.12.2 iron sword: 6 per full hit, a crit ×1.5 = 9.
        Assert.Equal(6, m.WorstHit(Items.IronSword, false, []));
        Assert.Equal(9, m.WorstHit(Items.IronSword, true, []));
    }

    [Fact]
    public void Pufferfish_is_risky_food_raw_fish_is_not()
    {
        using var _ = GameData.Override(Release1122GameData.Instance);
        const int fish = 349; // raw fish: damage 3 = pufferfish
        Assert.True(HungerRules.IsRisky(fish, 3));
        Assert.False(HungerRules.IsRisky(fish, 0));
        var slots = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
        slots[36] = new ItemStack((short)fish, 4, 3);
        Assert.False(SurvivalReflex.HasSafeFood(slots, new HashSet<int>()));
        slots[36] = new ItemStack((short)fish, 4, 0);
        Assert.True(SurvivalReflex.HasSafeFood(slots, new HashSet<int>()));
    }
}
