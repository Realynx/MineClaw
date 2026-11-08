using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

public sealed class MineOreWorkTests
{
    private sealed class Body : IBuildBody, IDisposable
    {
        public string Name => "miner";
        public WorldState World { get; } = new();
        public HashSet<BlockPos> Protected { get; } = [];
        public List<BlockPos> Ores { get; } = [];
        public int Walks, Pillars, Recoveries;
        public Action? AfterOre;
        public Action? AfterPillar;
        public Body()
        {
            World.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
            for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) World.SetBlock(x, 64, z, 1, 0);
            World.Inventory.SetSlot(0, 36, new ItemStack(3, 32, 0));
            Move(new BlockPos(0, 65, 0));
        }
        private void Move(BlockPos p) => World.Player.ServerTeleport(new Vec3d(p.X + .5, p.Y, p.Z + .5), p.Y + 1.62, 0, 0, true);
        public bool IsProtected(BlockPos p, int id) => Protected.Contains(p);
        public int Count(int id, int damage) => World.Inventory.CountItem(id);
        public Task<ActionResult> GotoAsync(BlockPos stand, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Walks++; Move(stand); return Task.FromResult(ActionResult.Success("walk")); }
        public Task<ActionResult> PlaceAsync(BuildStep step, int itemId, int damage, CancellationToken ct) => throw new InvalidOperationException("unexpected ordinary placement");
        public Task<ActionResult> DigAsync(BlockPos pos, CancellationToken ct) { ct.ThrowIfCancellationRequested(); World.SetBlock(pos.X, pos.Y, pos.Z, 0, 0); return Task.FromResult(ActionResult.Success("dug")); }
        public Task<ActionResult> DigOreHereAsync(BlockPos pos, int ore, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(ore, OreIdentity.Canonical(World.GetBlock(pos), false));
            Assert.True(ReachScan.Within(World, World.Player.Position, pos));
            Assert.DoesNotContain(pos, Protected);
            World.SetBlock(pos.X, pos.Y, pos.Z, 0, 0);
            Ores.Add(pos);
            AfterOre?.Invoke();
            return Task.FromResult(ActionResult.Success("ore"));
        }
        public Task<ActionResult> PillarAsync(BlockPos feet, int itemId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(0, World.GetBlock(feet));
            World.SetBlock(feet.X, feet.Y, feet.Z, itemId, 0);
            Move(feet.Up()); Pillars++;
            AfterPillar?.Invoke();
            return Task.FromResult(ActionResult.Success("up"));
        }
        public Task<ActionResult> RecoverOreScaffoldAsync(BlockPos pos, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(pos.Y + 1, World.Player.Position.Y);
            World.SetBlock(pos.X, pos.Y, pos.Z, 0, 0);
            Move(pos); Recoveries++;
            return Task.FromResult(ActionResult.Success("down"));
        }
        public Task<string?> EnsureAsync(int itemId, int damage, int count, CancellationToken ct) => throw new InvalidOperationException("veins must not fetch supplies");
        public Task<string?> ClutchAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public void Dispose() => World.Dispose();
    }

    private static VoxelPlan Design(BlockPos seed) => new() { Blueprint = new Blueprint { Kind = "mine", W = 1, H = 1, D = 1 }, Origin = seed, Air = { seed } };
    private static void Ore(Body b, int x, int y, int z, int id = 56) => b.World.SetBlock(x, y, z, id, 0);

    [Fact]
    public async Task An_exposed_vein_extends_incrementally_and_does_not_hop_to_a_different_ore()
    {
        using var b = new Body();
        var seed = new BlockPos(1, 65, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 2, 65, 0); Ore(b, 3, 65, 0); Ore(b, 3, 66, 0, 14);
        work.Expose(seed, 0);
        Assert.Single(bp.OreFrontier!);
        Assert.Equal(2, await work.DrainAsync(default));
        Assert.Equal([new BlockPos(2, 65, 0), new BlockPos(3, 65, 0)], b.Ores);
        Assert.Equal(14, b.World.GetBlock(3, 66, 0));
        Assert.Equal(0, b.Walks);
        Assert.Empty(bp.OreFrontier!);
    }

    [Fact]
    public async Task Planned_floors_protected_blocks_and_hazardous_ore_are_left_intact()
    {
        using var b = new Body();
        var seed = new BlockPos(1, 65, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 1, 64, 0); Ore(b, 2, 65, 0); Ore(b, 1, 65, 1);
        b.Protected.Add(new BlockPos(2, 65, 0));
        Ore(b, 1, 65, 2, 11);
        work.Expose(seed, 0);
        Assert.Equal(0, await work.DrainAsync(default));
        Assert.Equal(56, b.World.GetBlock(1, 64, 0));
        Assert.Equal(56, b.World.GetBlock(2, 65, 0));
        Assert.Equal(56, b.World.GetBlock(1, 65, 1));
    }

    [Fact]
    public async Task Cancellation_saves_the_next_exposed_cell_and_resume_mines_it()
    {
        using var b = new Body();
        using var stop = new CancellationTokenSource();
        var seed = new BlockPos(1, 65, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 2, 65, 0); Ore(b, 3, 65, 0);
        b.AfterOre = stop.Cancel;
        work.Expose(seed, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.DrainAsync(stop.Token));
        Assert.Single(b.Ores);
        Assert.Equal(new BlockPos(3, 65, 0), Assert.Single(bp.OreFrontier!).Pos);
        b.AfterOre = null;
        Assert.Equal(1, await work.DrainAsync(default));
    }

    [Fact]
    public async Task High_neighbors_share_one_pillar_and_all_temporary_blocks_are_recovered()
    {
        using var b = new Body();
        var seed = new BlockPos(1, 71, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 2, 71, 0); Ore(b, 3, 71, 0);
        work.Expose(seed, 0);
        Assert.Equal(2, await work.DrainAsync(default));
        Assert.Equal(1, b.Walks);
        Assert.True(b.Pillars > 0);
        Assert.Equal(b.Pillars, b.Recoveries);
        Assert.Empty(bp.ScaffoldUp);
        Assert.Equal(65, b.World.Player.Position.Y);
    }

    [Fact]
    public async Task Full_inventory_keeps_the_frontier_for_the_next_run()
    {
        using var b = new Body();
        var seed = new BlockPos(1, 65, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 2, 65, 0);
        work.Expose(seed, 0);
        for (int i = 9; i < 45; i++) b.World.Inventory.SetSlot(0, i, new ItemStack(4, 64, 0));
        Assert.Equal(0, await work.DrainAsync(default));
        Assert.Single(bp.OreFrontier!);
        b.World.Inventory.SetSlot(0, 10, ItemStack.Empty);
        Assert.Equal(1, await work.DrainAsync(default));
    }

    [Fact]
    public async Task A_known_vein_waits_for_a_hazard_to_clear_instead_of_forgetting_its_continuation()
    {
        using var b = new Body();
        var seed = new BlockPos(1, 65, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 2, 65, 0); Ore(b, 3, 65, 0, 9);
        work.Expose(seed, 0);
        Assert.Equal(0, await work.DrainAsync(default));
        Assert.Single(bp.OreFrontier!);
        b.World.SetBlock(3, 65, 0, 0, 0);
        Assert.Equal(1, await work.DrainAsync(default));
    }

    [Fact]
    public async Task Cancellation_after_a_server_accepted_pillar_still_records_and_recovers_that_block()
    {
        using var b = new Body();
        using var stop = new CancellationTokenSource();
        var seed = new BlockPos(1, 71, 0);
        var bp = new BuildPlan();
        var work = new MineOreWork(Design(seed), bp, b, () => null, () => { });
        Ore(b, 2, 71, 0);
        work.Expose(seed, 0);
        b.AfterPillar = () => { stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.DrainAsync(stop.Token));
        Assert.Equal(1, b.Pillars);
        Assert.Equal(1, b.Recoveries);
        Assert.Empty(bp.ScaffoldUp);
        Assert.Empty(b.Ores);
        Assert.Equal(65, b.World.Player.Position.Y);
        Assert.Single(bp.OreFrontier!);
    }

    [Fact]
    public async Task An_unloaded_continuation_keeps_its_family_and_resolves_after_reload()
    {
        using var b = new Body();
        var seed = new BlockPos(15, 65, 0);
        var bp = new BuildPlan();
        var design = Design(new BlockPos(14, 65, 0));
        var work = new MineOreWork(design, bp, b, () => null, () => { });
        work.Expose(seed, 56);
        var unknown = Assert.Single(bp.OreFrontier!);
        Assert.Equal(new BlockPos(16, 65, 0), unknown.Pos);
        Assert.True(unknown.Unknown);
        Assert.Equal(0, await work.DrainAsync(default));
        b.World.ApplyChunkRegion(16, 0, -16, 16, 128, 32, new byte[16 * 128 * 32], null, null, null);
        Ore(b, 16, 65, 0);
        b.World.Player.ServerTeleport(new Vec3d(15.5, 65, .5), 66.62, 0, 0, true);
        Assert.Equal(1, await work.DrainAsync(default));
        Assert.DoesNotContain(bp.OreFrontier!, c => c.Pos == new BlockPos(16, 65, 0));
    }
}
