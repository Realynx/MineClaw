using MineClaw.Data;
using MineClaw.Skills.Inventory;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class InventoryTests
{
    private static ItemStack S(int id, int n, int d = 0) => new((short)id, (byte)n, (short)d);

    private static WindowModel Chest(params (int Slot, ItemStack Stack)[] items)
    {
        var slots = Enumerable.Repeat(ItemStack.Empty, 27 + 36).ToArray();
        foreach (var (s, st) in items) slots[s] = st;
        return WindowModel.Create(WindowType.Chest, 27, slots);
    }

    private static IReadOnlyCollection<int> Player(WindowModel m) => m.SlotsOf(Region.Player).ToArray();
    private static IReadOnlyCollection<int> Box(WindowModel m) => m.SlotsOf(Region.Container).ToArray();

    [Fact]
    public void Whole_stack_is_one_shift_click()
    {
        var m = Chest((0, S(Blocks.Cobblestone, 64)));
        var plan = ClickPlanner.Transfer(m, Blocks.Cobblestone, 0, 64, Box(m), Player(m));
        Assert.Equal([new Click(0, Shift: true)], plan);
    }

    [Fact]
    public void Half_stack_is_right_pickup_plus_place()
    {
        var m = Chest((0, S(Blocks.Cobblestone, 64)));
        var plan = ClickPlanner.Transfer(m, Blocks.Cobblestone, 0, 32, Box(m), Player(m));
        Assert.Equal(2, plan.Count);
        Assert.True(plan[0].Right);
        m.Apply(plan);
        Assert.Equal(32, m.Count(Blocks.Cobblestone, 0, Player(m)));
        Assert.True(m.Cursor.IsEmpty);
    }

    [Fact]
    public void Exact_small_count_uses_singles_and_leaves_cursor_empty()
    {
        var m = Chest((0, S(Blocks.Cobblestone, 64)));
        var plan = ClickPlanner.Transfer(m, Blocks.Cobblestone, 0, 5, Box(m), Player(m));
        Assert.Equal(7, plan.Count); // pick all, 5 singles, put the rest back
        m.Apply(plan);
        Assert.Equal(5, m.Count(Blocks.Cobblestone, 0, Player(m)));
        Assert.Equal(59, m.Count(Blocks.Cobblestone, 0, Box(m)));
        Assert.True(m.Cursor.IsEmpty);
        var big = Chest((0, S(Blocks.Cobblestone, 64)));
        var p60 = ClickPlanner.Transfer(big, Blocks.Cobblestone, 0, 60, Box(big), Player(big));
        Assert.Equal(6, p60.Count); // pick all, put 4 back, place 60
    }

    [Fact]
    public void Deposits_merge_into_partial_stacks_first()
    {
        var m = Chest((3, S(Blocks.Dirt, 60)), (27, S(Blocks.Dirt, 10)));
        var plan = ClickPlanner.Transfer(m, Blocks.Dirt, 0, 10, Player(m), Box(m));
        m.Apply(plan);
        Assert.Equal(64, m[3].Count);
        Assert.Equal(6, m.Count(Blocks.Dirt, 0, Box(m).Where(s => s != 3).ToArray()));
        Assert.True(m.Cursor.IsEmpty);
    }

    [Theory]
    [InlineData(new[] { 1, 0, -1 }, 3)]          // swap two stacks: L0 L1 L0
    [InlineData(new[] { 2, 0, 1 }, 4)]           // 3-cycle: L+1
    [InlineData(new[] { -1, 0, -1 }, 2)]         // move into an empty slot
    [InlineData(new[] { 0, 1, 2 }, 0)]           // already there
    [InlineData(new[] { 1, 0, 3, 2 }, 6)]        // two swaps
    public void Permutation_costs_moves_plus_cycles(int[] target, int expected)
    {
        var plan = ClickPlanner.Permute(target);
        Assert.Equal(expected, plan.Count);
        // Verify on a model with distinct items.
        var ids = new[] { Blocks.Dirt, Blocks.Cobblestone, Blocks.Sand, Blocks.Log };
        var slots = Enumerable.Repeat(ItemStack.Empty, 27 + 36).ToArray();
        for (int i = 0; i < target.Length; i++) if (target.Contains(i)) slots[i] = S(ids[i], 10 + i);
        var m = WindowModel.Create(WindowType.Chest, 27, slots);
        m.Apply(plan);
        for (int i = 0; i < target.Length; i++)
            Assert.Equal(target[i] < 0 ? -1 : ids[target[i]], m[i].IsEmpty ? -1 : m[i].Id);
        Assert.True(m.Cursor.IsEmpty);
    }

    [Fact]
    public void Template_parsing_and_round_trip()
    {
        var l = SlotLayout.Parse("cobblestone*3,dirt*2,_*4 / log*9");
        Assert.Equal(27, l.Slots.Count);
        Assert.Equal(Blocks.Cobblestone, l.Slots[2]!.Value.Id);
        Assert.Equal(Blocks.Dirt, l.Slots[4]!.Value.Id);
        Assert.Null(l.Slots[5]);
        Assert.Equal(Blocks.Log, l.Slots[17]!.Value.Id);
        Assert.Null(l.Slots[18]);
        Assert.Throws<FormatException>(() => SlotLayout.Parse("unobtainium*2"));
        Assert.Throws<FormatException>(() => SlotLayout.Parse("dirt*10"));
        Assert.Throws<FormatException>(() => SlotLayout.Parse("dirt/dirt/dirt/dirt"));
        var slots = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        slots[0] = slots[1] = S(Blocks.Cobblestone, 64);
        slots[9] = S(Blocks.Log, 3);
        var text = SlotLayout.Describe(slots);
        Assert.Equal("cobblestone*2,_*7 / log,_*8", text);
        var back = SlotLayout.Parse(text);
        Assert.Equal(Blocks.Log, back.Slots[9]!.Value.Id);
    }

    internal static FakeGame Storage()
    {
        var g = LiquidTests.Island(6);
        foreach (var p in new[] { new BlockPos(3, 64, 0), new BlockPos(-3, 64, 0), new BlockPos(0, 64, 4) })
        {
            g.Set(p, Blocks.Chest);
            g.Chests[p] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        }
        return g;
    }

    [Fact]
    public async Task Move_items_plans_one_visit_per_chest()
    {
        var g = Storage();
        g.Chests[new BlockPos(3, 64, 0)][0] = S(Items.IronIngot, 20);
        g.Chests[new BlockPos(3, 64, 0)][1] = S(Blocks.Log, 64);
        g.Give(Blocks.Cobblestone, 70);
        var r = await g.Run("move_items", new
        {
            moves = new object[]
            {
                new { item = "iron ingot", count = "all", from = "chest@3,64,0", to = "inv" },
                new { item = "cobblestone", count = 64, from = "inv", to = "chest@-3,64,0" },
                new { item = "log", count = 10, from = "chest@3,64,0", to = "chest@0,64,4" },
            },
        });
        Assert.True(r.Ok, r.Text);
        Assert.Matches(@"3 moves, 3 chests, \d+ clicks", r.Text);
        Assert.All(g.Opens.Values, n => Assert.Equal(1, n));
        Assert.Equal(20, g.World.Inventory.CountItem(Items.IronIngot));
        Assert.Equal(6, g.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal(64, g.Chests[new BlockPos(-3, 64, 0)].Where(s => !s.IsEmpty).Sum(s => s.Count));
        Assert.Equal(10, g.Chests[new BlockPos(0, 64, 4)].Where(s => !s.IsEmpty && s.Id == Blocks.Log).Sum(s => s.Count));
        Assert.Equal(54, g.Chests[new BlockPos(3, 64, 0)].Where(s => !s.IsEmpty && s.Id == Blocks.Log).Sum(s => s.Count));
        Assert.Empty(g.Violations); // never closed with items on the cursor
    }

    [Fact]
    public async Task Move_items_replans_after_a_rejected_click()
    {
        var g = Storage();
        g.Chests[new BlockPos(3, 64, 0)][0] = S(Blocks.Sand, 64);
        g.RejectClick = 2;
        var r = await g.Run("move_items", new { moves = new object[] { new { item = "sand", count = 5, from = "chest@3,64,0", to = "inv" } } });
        Assert.True(r.Ok, r.Text);
        Assert.Contains("rejected+replanned", r.Text);
        Assert.Equal(5, g.World.Inventory.CountItem(Blocks.Sand));
        Assert.Equal(59, g.Chests[new BlockPos(3, 64, 0)].Where(s => !s.IsEmpty).Sum(s => s.Count));
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Move_items_refuses_reserved_and_unknown_targets()
    {
        var g = Storage();
        g.Give(Items.DiamondPickaxe, 1);
        g.ReservedItems = new HashSet<int> { Items.DiamondPickaxe };
        Assert.Contains("reserved", (await g.Run("move_items", new { moves = new object[] { new { item = "diamond pickaxe", from = "inv", to = "chest@3,64,0" } } })).Text);
        Assert.Contains("unknown destination", (await g.Run("move_items", new { moves = new object[] { new { item = "dirt", from = "inv", to = "Nowhere storage" } } })).Text);
    }

    [Fact]
    public async Task Move_items_to_a_labelled_chest_and_a_hotbar_slot()
    {
        var g = Storage();
        g.SignText[new BlockPos(3, 65, 0)] = ["Iron", "storage", "", ""];
        g.Give(Items.IronIngot, 5);
        g.SetSlot(20, Items.Bread, 8);
        var r = await g.Run("move_items", new
        {
            moves = new object[]
            {
                new { item = "iron ingot", count = "all", from = "inv", to = "Iron storage" },
                new { item = "bread", count = "all", from = "inv", to = "hotbar", slot = 4 },
            },
        });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(5, g.Chests[new BlockPos(3, 64, 0)].Where(s => !s.IsEmpty).Sum(s => s.Count));
        Assert.Equal(Items.Bread, g.World.Inventory.GetPlayerSlot(40).Id);
    }

    [Fact]
    public async Task Arrange_chest_from_template_and_saves_layout()
    {
        var g = Storage();
        var c = g.Chests[new BlockPos(3, 64, 0)];
        c[0] = S(Blocks.Dirt, 64); c[5] = S(Blocks.Cobblestone, 64); c[6] = S(Blocks.Cobblestone, 20); c[7] = S(Blocks.Cobblestone, 30);
        var ctx = g.Context();
        var reg = SkillRegistry.CreateDefault();
        var r = await reg.RunAsync(ctx, "arrange_chest", FakeGame.J(new { chest = "3,64,0", layout = "cobblestone*2,dirt" }), CancellationToken.None);
        Assert.True(r.Ok, r.Text);
        var after = g.Chests[new BlockPos(3, 64, 0)];
        Assert.Equal(Blocks.Cobblestone, after[0].Id);
        Assert.Equal(Blocks.Cobblestone, after[1].Id);
        Assert.Equal(Blocks.Dirt, after[2].Id);
        Assert.Equal(114, after.Where(s => !s.IsEmpty && s.Id == Blocks.Cobblestone).Sum(s => s.Count));
        Assert.Equal(2, after.Count(s => !s.IsEmpty && s.Id == Blocks.Cobblestone)); // consolidated 64+50
        Assert.NotNull(ctx.Layouts.Get(new BlockPos(3, 64, 0)));
        Assert.Empty(g.Violations);
        // "tidy up" restores the saved layout after someone messes it up.
        var mess = g.Chests[new BlockPos(3, 64, 0)];
        (mess[0], mess[20]) = (mess[20], mess[0]);
        var t = await reg.RunAsync(ctx, "arrange_chest", FakeGame.J(new { chest = "3,64,0", layout = "saved" }), CancellationToken.None);
        Assert.True(t.Ok, t.Text);
        Assert.Equal(Blocks.Cobblestone, g.Chests[new BlockPos(3, 64, 0)][0].Id);
    }

    [Fact]
    public async Task Arrange_chest_presets_and_bad_template()
    {
        var g = Storage();
        var c = g.Chests[new BlockPos(3, 64, 0)];
        c[10] = S(Items.IronIngot, 3); c[2] = S(Blocks.Log, 5); c[20] = S(Items.Bread, 4);
        var r = await g.Run("arrange_chest", new { chest = "3,64,0", layout = "by_category" });
        Assert.True(r.Ok, r.Text);
        var after = g.Chests[new BlockPos(3, 64, 0)];
        Assert.Equal(Items.IronIngot, after[0].Id); // ores first
        Assert.Equal(Blocks.Log, after[1].Id);
        Assert.Equal(Items.Bread, after[2].Id);
        var bad = await g.Run("arrange_chest", new { chest = "3,64,0", layout = "dirt*12" });
        Assert.False(bad.Ok);
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Organize_storage_assigns_categories_and_moves_strays()
    {
        var g = Storage();
        g.Chests[new BlockPos(3, 64, 0)][0] = S(Items.IronIngot, 10);
        g.Chests[new BlockPos(3, 64, 0)][1] = S(Blocks.Log, 8);
        g.Chests[new BlockPos(-3, 64, 0)][0] = S(Blocks.Log, 30);
        g.Chests[new BlockPos(-3, 64, 0)][1] = S(Blocks.Log, 20);
        g.Chests[new BlockPos(0, 64, 4)][4] = S(Items.Bread, 6);
        var r = await g.Run("organize_storage");
        Assert.True(r.Ok, r.Text);
        var wood = g.Chests[new BlockPos(-3, 64, 0)];
        Assert.Equal(58, wood.Where(s => !s.IsEmpty && s.Id == Blocks.Log).Sum(s => s.Count));
        Assert.DoesNotContain(g.Chests[new BlockPos(3, 64, 0)], s => !s.IsEmpty && s.Id == Blocks.Log);
        Assert.Contains("ores@", r.Text);
        Assert.Empty(g.Violations);
    }

    [Fact]
    public async Task Deposit_everything_and_restock()
    {
        var g = Storage();
        g.Give(Blocks.Cobblestone, 30);
        g.Give(Items.Wheat, 5);
        var d = await g.Run("deposit_everything");
        Assert.True(d.Ok, d.Text);
        Assert.Equal(0, g.World.Inventory.CountItem(Blocks.Cobblestone));
        g.Chests[new BlockPos(0, 64, 4)][0] = S(Items.StonePickaxe, 1);
        g.Chests[new BlockPos(0, 64, 4)][1] = S(Blocks.Torch, 64);
        var r = await g.Run("restock_loadout", new { items = "pickaxe torches" });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(1, g.World.Inventory.CountItem(Items.StonePickaxe));
        Assert.Equal(16, g.World.Inventory.CountItem(Blocks.Torch));
        Assert.Empty(g.Violations);
    }
}
