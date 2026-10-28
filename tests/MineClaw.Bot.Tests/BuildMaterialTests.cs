using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Build materials (live 10-28): "I need a 256 stone slab and can't get one" for a tower whose bill said cobblestone slabs
/// (the variant's damage was dropped: 44:3 sought as 44:0, which needs smelted stone), and "I need a 256 cobblestone" with
/// 500+ cobble in three chests (the withdraw stopped after the first chest). Now: the exact variant, every chest, then craft,
/// then the gen, only then a correctly worded question; a shortfall pauses the build instead of ending it.
/// </summary>
public class BuildMaterialTests
{
    private sealed record Island(BotHost H, FakeGameClient C, List<string> Asked);

    private static Island Make()
    {
        var (h, c) = TestBot.Create();
        c.Floor(64, 8, Blocks.Grass);
        c.World.SetBlock(2, 65, 0, Blocks.Workbench, 0);
        var asked = new List<string>();
        h.Acquisition!.AskPlayer = (q, _, _) => { asked.Add(q); return Task.FromResult<string?>(null); };
        // A withdraw moves what was asked for from the chest's memory into the inventory (the chest really had it).
        c.Effects["withdraw"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("withdraw ", StringComparison.Ordinal)).Split(' ');
            int id = int.Parse(parts[1]), n = int.Parse(parts[2]);
            var dmg = parts[4].StartsWith("dmg=", StringComparison.Ordinal) && int.TryParse(parts[4][4..], out var d) ? d : 0;
            var at = parts[3];
            var obs = h.Runtime.Observations.Nearest("chest", 0, 65, 0, limit: 16, maxDistance: 64).Select(r => r.Item1).First(r => r.Key == at);
            var have = h.Runtime.Observations.Contents(obs.Id).Where(i => i.ItemId == id).Sum(i => i.Count);
            n = Math.Min(n, have);
            Give(c, id, dmg, n);
            h.Runtime.Observations.Upsert(new Observation("chest", at, "chest") { Bounds = obs.Bounds, Contents = [new ItemCount(id, Items.Get(id).Name, have - n)] });
        };
        c.Effects["place"] = () =>
        {
            var parts = c.CallList.Last(x => x.StartsWith("place ", StringComparison.Ordinal)).Split(' ');
            var p = parts[1].Split(',').Select(int.Parse).ToArray();
            int item = int.Parse(parts[2]);
            Take(c, item, 1);
            c.World.SetBlock(p[0], p[1], p[2], item, 0);
        };
        return new Island(h, c, asked);
    }

    private static void Chest(Island i, int x, int z, int id, int count)
    {
        i.C.World.SetBlock(x, 65, z, Blocks.Chest, 0);
        i.H.Runtime.Observations.Upsert(new Observation("chest", $"{x},65,{z}", "chest") { Bounds = Box.Point(x, 65, z), Contents = [new ItemCount(id, Items.Get(id).Name, count)] });
    }

    private static void Give(FakeGameClient c, int id, int dmg, int n)
    {
        var slots = c.World.Inventory.PlayerSlots();
        foreach (int s in Enumerable.Range(36, 9).Concat(Enumerable.Range(9, 27)))
        {
            if (n <= 0) return;
            var st = c.World.Inventory.PlayerSlots()[s];
            if (st.IsEmpty) { int k = Math.Min(64, n); c.Give(s, id, k, dmg); n -= k; }
            else if (st.Id == id && st.Damage == dmg && st.Count < 64) { int k = Math.Min(64 - st.Count, n); c.Give(s, id, st.Count + k, dmg); n -= k; }
        }
    }

    private static void Take(FakeGameClient c, int id, int n)
    {
        foreach (int s in Enumerable.Range(9, 36))
        {
            var st = c.World.Inventory.PlayerSlots()[s];
            if (n <= 0 || st.IsEmpty || st.Id != id) continue;
            int k = Math.Min(st.Count, n);
            if (st.Count - k == 0) c.World.Inventory.SetSlot(0, s, ItemStack.Empty); else c.Give(s, id, st.Count - k, st.Damage);
            n -= k;
        }
    }

    [Fact]
    public async Task Cobblestone_slabs_are_crafted_from_storage_cobble_not_asked_for_as_stone_slabs()
    {
        var i = Make();
        using var _ = i.H;
        Chest(i, 3, 3, Blocks.Cobblestone, 300);
        // The table craft: 3 cobble → 3 cobblestone slabs (44:3), in one batch.
        i.C.Effects["craft"] = () =>
        {
            int n = int.Parse(i.C.CallList.Last(x => x.StartsWith("craft ", StringComparison.Ordinal)).Split(' ')[2]);
            int crafts = (n + 2) / 3;
            Take(i.C, Blocks.Cobblestone, crafts * 3);
            Give(i.C, Blocks.Slab, 3, crafts * 3);
        };
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("cobblestone_slab", 256), CancellationToken.None);
        Assert.True(r.Ok, r.Text + " | " + string.Join(" | ", i.C.CallList));
        Assert.True(i.C.World.Inventory.CountItem(Blocks.Slab, 3) >= 256);
        Assert.Equal(0, i.C.World.Inventory.CountItem(Blocks.Slab, 0)); // never stone slabs
        Assert.Contains(i.C.CallList, x => x.StartsWith($"withdraw {Blocks.Cobblestone} ", StringComparison.Ordinal));
        Assert.Contains(i.C.CallList, x => x.StartsWith($"craft {Blocks.Slab} 256", StringComparison.Ordinal)); // one batch of 86 crafts
        Assert.Empty(i.Asked);
    }

    [Fact]
    public async Task Cobble_spread_over_three_chests_is_all_withdrawn_before_anyone_is_asked()
    {
        var i = Make();
        using var _ = i.H;
        Chest(i, 3, 3, Blocks.Cobblestone, 100);
        Chest(i, -3, 3, Blocks.Cobblestone, 120);
        Chest(i, 3, -3, Blocks.Cobblestone, 150);
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("cobblestone", 256), CancellationToken.None);
        Assert.True(r.Ok, r.Text);
        Assert.True(i.C.World.Inventory.CountItem(Blocks.Cobblestone) >= 256);
        Assert.True(i.C.CallList.Count(x => x.StartsWith("withdraw ", StringComparison.Ordinal)) >= 3);
        Assert.Empty(i.Asked);
    }

    [Fact]
    public async Task A_build_withdraws_storage_cobble_and_proceeds()
    {
        var i = Make();
        using var _ = i.H;
        Chest(i, 6, 6, Blocks.Cobblestone, 64);
        var (plan, issues) = BlueprintNormalizer.FromText("size 4x1x1\npalette C=cobblestone\nlayer 0\nCCCC", new BlockPos(-2, 65, -2));
        Assert.NotNull(plan);
        var body = new GameBuildBody(() => i.C);
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan!, BuildPart.Whole(plan!), body, CancellationToken.None);
        Assert.True(report.Complete, report.Text("wall") + " | " + string.Join(" | ", i.C.CallList.TakeLast(8)));
        Assert.Contains(i.C.CallList, x => x.StartsWith($"withdraw {Blocks.Cobblestone} ", StringComparison.Ordinal));
        Assert.Empty(i.Asked);
    }

    [Fact]
    public async Task With_nothing_anywhere_the_question_names_the_real_item_and_count()
    {
        var i = Make();
        using var _ = i.H;
        var r = await Acquisition.Planner(i.C).AcquireAsync(new Need("cobblestone_slab", 256), CancellationToken.None);
        Assert.False(r.Ok);
        var q = Assert.Single(i.Asked);
        Assert.StartsWith("I need 256 cobblestone slabs and can't get them", q);
        Assert.DoesNotContain("a 256", q);
        Assert.DoesNotContain("stone slab", q.Replace("cobblestone slab", ""));
    }

    [Fact]
    public async Task A_shortfall_pauses_the_build_after_building_everything_else()
    {
        var w = BlueprintTests.Island();
        var (plan, _) = BlueprintNormalizer.FromText(BlueprintTests.Hut, new BlockPos(-2, 64, -3));
        var body = new SimBuildBody(w) { Inv = { [(Blocks.GlassPane, 0)] = 0 } };
        var report = await new ConstructionExecutor(new BlueprintOptions()).RunAsync(1, plan!, BuildPart.Whole(plan!), body, CancellationToken.None);
        Assert.StartsWith("missing glass", report.Stopped);
        Assert.Contains("paused", report.Stopped);
        Assert.All(plan!.Blocks.Values.Where(b => b.Id == Blocks.Cobblestone), b => Assert.Equal(Blocks.Cobblestone, w.GetBlock(b.Pos)));
        Assert.All(report.Missing, p => Assert.Equal(Blocks.GlassPane, plan.Blocks[p].Id));
        Assert.Equal("paused", report.Plan!.State); // resumable
    }
}
