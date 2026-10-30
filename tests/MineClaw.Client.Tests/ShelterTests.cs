using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Live 10-28 16:20 (alice): a restart woke main on the drop tower's platform at night, about seven zombies piled on, "disengaging
/// inward" and "retreating inward to eat" had nowhere to go (no path from up there back to the island) and it died at -26,71,14.
/// The no-path danger reflex boxes the body in where it stands instead: 4 sides at feet and head, overhead if open, from blocks
/// on hand (never saplings), never digging its floor or stepping anywhere; then it eats and waits it out.
/// </summary>
public class ShelterTests
{
    private static WorldState Platform(int half = 1, int y = 63)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -half; x <= half; x++) for (int z = -half; z <= half; z++) w.SetBlock(x, y, z, Blocks.Cobblestone, 0);
        return w;
    }

    /// <summary>Applies the plan in order, checking each click: a solid block to click, never into the body's column, in reach.</summary>
    private static void Apply(WorldState w, BlockPos feet, List<ShelterPlace> plan)
    {
        var eye = new Vec3d(feet.X + 0.5, feet.Y + 1.62, feet.Z + 0.5);
        foreach (var p in plan)
        {
            Assert.NotEqual(feet, p.Target);
            Assert.NotEqual(feet.Up(), p.Target);
            Assert.Equal(0, w.GetBlock(p.Target));
            Assert.True(BlockShapes.HasCollision(w, p.Against.X, p.Against.Y, p.Against.Z), $"{p.Target}: nothing at {p.Against} to click");
            Assert.True(eye.DistanceTo(p.Target.Center) <= 4.5, $"{p.Target} out of reach");
            w.SetBlock(p.Target.X, p.Target.Y, p.Target.Z, Blocks.Cobblestone, 0);
        }
    }

    [Fact]
    public void On_a_platform_tower_top_the_box_closes_all_four_sides_at_feet_and_head_and_overhead()
    {
        var w = Platform(half: 3);
        var feet = new BlockPos(0, 64, 0);
        Assert.Equal(9, Shelter.Open(w, feet).Count);
        var plan = Shelter.Plan(w, feet);
        Assert.Equal(10, plan.Count); // 4 + 4 + a cap beside the roof + the roof
        Apply(w, feet, plan);
        Assert.True(Shelter.Enclosed(w, feet));
        Assert.Equal(Blocks.Cobblestone, w.GetBlock(0, 63, 0)); // the floor stays
    }

    [Fact]
    public void At_a_one_block_post_over_the_void_each_side_gets_a_support_from_my_floor_first()
    {
        var w = Platform(half: 0); // a 1×1 column top: the void all around
        var feet = new BlockPos(0, 64, 0);
        var plan = Shelter.Plan(w, feet);
        Apply(w, feet, plan);
        Assert.True(Shelter.Enclosed(w, feet));
        Assert.Equal(4, plan.Count(p => p.Target.Y == 63)); // the supports beside my floor block, clicked on its sides
        Assert.All(plan.Where(p => p.Target.Y == 63), p => Assert.Equal(new BlockPos(0, 63, 0), p.Against));
    }

    [Fact]
    public void Walls_already_there_are_used_and_only_the_gaps_are_filled()
    {
        var w = Platform(half: 3);
        var feet = new BlockPos(0, 64, 0);
        for (int y = 64; y <= 65; y++) { w.SetBlock(1, y, 0, Blocks.Stone, 0); w.SetBlock(-1, y, 0, Blocks.Stone, 0); }
        w.SetBlock(0, 66, 0, Blocks.Planks, 0);
        var plan = Shelter.Plan(w, feet);
        Assert.Equal(4, plan.Count);
        Apply(w, feet, plan);
        Assert.True(Shelter.Enclosed(w, feet));
    }

    [Theory]
    [InlineData(Blocks.Cobblestone, 0)]
    [InlineData(Blocks.Planks, 1)]
    [InlineData(Blocks.Dirt, 9)]
    [InlineData(Blocks.Sapling, -1)]
    [InlineData(Blocks.Sand, -1)]
    [InlineData(Blocks.Chest, -1)]
    [InlineData(Blocks.Torch, -1)]
    [InlineData(Blocks.Leaves, -1)]
    public void Cobble_first_dirt_last_never_saplings_or_falling_blocks(int id, int rank) => Assert.Equal(rank, Shelter.BlockRank(id));

    [Fact]
    public async Task Stranded_on_a_platform_at_low_hp_with_zombies_it_boxes_itself_in_and_says_so()
    {
        // The fake server's 5×5 platform at y63 hangs over the void; home (the island) is far below it.
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32),
            o => o with { Survival = o.Survival with { ShelterMaxMinutes = 0.05 } });
        await using var _ = c;
        using var __ = s;
        var events = new List<CombatEvent>();
        c.CombatEventRaised += e => { lock (events) events.Add(e); };
        c.World.Spawn = new BlockPos(8, 40, 8);
        s.SetVitals(6, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 6));
        Assert.True(c.StrandedNow());
        c.World.Entities.SpawnMob(901, MobTypes.Zombie, new Vec3d(6.5, 64, 6.5), 0, 0, null);
        c.World.Entities.SpawnMob(902, MobTypes.Zombie, new Vec3d(10.5, 64, 10.5), 0, 0, null);
        c.World.Entities.SpawnMob(903, MobTypes.Zombie, new Vec3d(10.5, 64, 6.5), 0, 0, null);
        var feet = new BlockPos(8, 64, 8);
        Assert.True(await Harness.WaitAsync(() => { lock (events) return events.Any(e => e.Message.StartsWith("boxed myself in at ")); }, 8000), "boxed in");
        CombatEvent line;
        lock (events) line = events.First(e => e.Message.StartsWith("boxed myself in at "));
        Assert.Equal(CombatEventKind.Critical, line.Kind);
        Assert.Matches(@"^boxed myself in at 8,64,8: 3 zombies, hp 6; waiting it out$", line.Message);
        // The 4 sides at feet and head and the roof stood (the server has them), the floor was never dug.
        foreach (var d in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            for (int y = 64; y <= 65; y++) Assert.True(s.Blocks.ContainsKey((8 + d.Item1, y, 8 + d.Item2)), $"side {d} at y{y}");
        Assert.True(s.Blocks.ContainsKey((8, 66, 8)), "roof");
        Assert.DoesNotContain(s.Dug, d => d.Y < 64);
        Assert.Equal(Blocks.Stone, s.Blocks[(8, 63, 8)]);
        Assert.Equal(feet, Pathfinder.StartCellFor(c.World, c.World.Player.Position));
    }

    [Fact]
    public async Task With_no_blocks_it_does_not_box_in()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Sapling, 13));
        await using var _ = c;
        using var __ = s;
        c.World.Spawn = new BlockPos(8, 40, 8);
        s.SetVitals(6, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 6));
        c.World.Entities.SpawnMob(901, MobTypes.Zombie, new Vec3d(6.5, 64, 6.5), 0, 0, null);
        c.World.Entities.SpawnMob(902, MobTypes.Zombie, new Vec3d(10.5, 64, 10.5), 0, 0, null);
        await Task.Delay(600);
        Assert.False(s.Blocks.ContainsKey((9, 64, 8)));
        Assert.False(s.Blocks.ContainsKey((8, 66, 8)));
        Assert.Equal(13, c.World.Inventory.CountItem(Blocks.Sapling)); // never saplings
    }

    [Fact]
    public async Task With_too_few_blocks_to_close_a_box_it_places_none()
    {
        // Live 10-30: bodies with a few planks started boxes they couldn't close and left the stubs about.
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 3));
        await using var _ = c;
        using var __ = s;
        c.World.Spawn = new BlockPos(8, 40, 8);
        s.SetVitals(6, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 6));
        c.World.Entities.SpawnMob(901, MobTypes.Zombie, new Vec3d(6.5, 64, 6.5), 0, 0, null);
        c.World.Entities.SpawnMob(902, MobTypes.Zombie, new Vec3d(10.5, 64, 10.5), 0, 0, null);
        c.World.Entities.SpawnMob(903, MobTypes.Zombie, new Vec3d(10.5, 64, 6.5), 0, 0, null);
        await Task.Delay(800);
        Assert.Equal(3, c.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.False(s.Blocks.ContainsKey((8, 66, 8)));
        var r = await c.ShelterAsync("test");
        Assert.False(r.Ok);
        Assert.Contains("not starting one", r.Message);
        Assert.Equal(3, c.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Fact]
    public async Task Once_it_is_safe_the_whole_box_comes_down_and_the_blocks_come_back()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32),
            o => o with { Survival = o.Survival with { ShelterMaxMinutes = 0.05 } });
        await using var _ = c;
        using var __ = s;
        c.World.Spawn = new BlockPos(8, 40, 8);
        s.SetVitals(6, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 6));
        foreach (var (id, x, z) in new[] { (901, 6.5, 6.5), (902, 10.5, 10.5), (903, 10.5, 6.5) })
            c.World.Entities.SpawnMob(id, MobTypes.Zombie, new Vec3d(x, 64, z), 0, 0, null);
        Assert.True(await Harness.WaitAsync(() => s.Blocks.ContainsKey((8, 66, 8)), 8000), "roof");
        // The mobs go (they burn, the night ends): the box is taken down when the wait ends.
        foreach (var id in new[] { 901, 902, 903 }) c.World.Entities.Destroy(id);
        var box = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.SelectMany(d => new[] { (8 + d.Item1, 64, 8 + d.Item2), (8 + d.Item1, 65, 8 + d.Item2) })
            .Append((8, 66, 8)).ToList();
        Assert.True(await Harness.WaitAsync(() => box.All(b => !s.Blocks.ContainsKey(b)), 20000),
            "left standing: " + string.Join(" ", box.Where(b => s.Blocks.ContainsKey(b))));
        Assert.Equal(Blocks.Stone, s.Blocks[(8, 63, 8)]);
        Assert.DoesNotContain(s.Dug, d => d.Y < 64);
    }

    [Fact]
    public async Task A_box_that_timed_out_with_mobs_about_is_cleared_later_once_nothing_can_get_at_the_body()
    {
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32),
            o => o with { Survival = o.Survival with { ShelterMaxMinutes = 0.05 } });
        await using var _ = c;
        using var __ = s;
        c.World.Spawn = new BlockPos(8, 40, 8);
        s.SetVitals(6, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 6));
        foreach (var (id, x, z) in new[] { (901, 6.5, 6.5), (902, 10.5, 10.5), (903, 10.5, 6.5) })
            c.World.Entities.SpawnMob(id, MobTypes.Zombie, new Vec3d(x, 64, z), 0, 0, null);
        Assert.True(await Harness.WaitAsync(() => s.Blocks.ContainsKey((8, 66, 8)), 8000), "roof");
        // Still there when the wait times out: one side opens, the roof stays.
        Assert.True(await Harness.WaitAsync(() => s.Dug.Any(d => d.Y >= 64), 15000), "the way out opened");
        await Task.Delay(500);
        Assert.True(s.Blocks.ContainsKey((8, 66, 8)));
        s.SetVitals(20, 20);
        foreach (var id in new[] { 901, 902, 903 }) c.World.Entities.Destroy(id);
        Assert.True(await Harness.WaitAsync(() => !s.Blocks.ContainsKey((8, 66, 8)), 15000), "the old box was cleared");
    }

    [Fact]
    public async Task Brave_keeps_the_body_out_of_a_box_until_it_takes_damage()
    {
        // 10-30: "stop filling it in, you're not being attacked". The brain's defend brave holds the box-in reflex off; a hit ends it.
        var (c, s) = await Harness.StartAsync(srv => srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 32),
            o => o with { Survival = o.Survival with { ShelterMaxMinutes = 0.05 } });
        await using var _ = c;
        using var __ = s;
        c.World.Spawn = new BlockPos(8, 40, 8);
        Assert.True(c.SetBrave(true).Ok);
        s.SetVitals(6, 20);
        Assert.True(await Harness.WaitAsync(() => c.World.Player.Health == 6));
        Assert.True(await Harness.WaitAsync(() => !c.Brave, 3000), "that was damage: brave is off again");
        Assert.True(c.SetBrave(true).Ok);
        foreach (var (id, x, z) in new[] { (901, 6.5, 6.5), (902, 10.5, 10.5), (903, 10.5, 6.5) })
            c.World.Entities.SpawnMob(id, MobTypes.Zombie, new Vec3d(x, 64, z), 0, 0, null);
        await Task.Delay(1200);
        Assert.False(s.Blocks.ContainsKey((8, 66, 8)), "boxed in while brave");
        s.SetVitals(5, 20);
        Assert.True(await Harness.WaitAsync(() => !c.Brave, 3000), "a hit ends brave");
        Assert.True(await Harness.WaitAsync(() => s.Blocks.ContainsKey((8, 66, 8)), 8000), "the reflex boxed in again after the hit");
    }
}
