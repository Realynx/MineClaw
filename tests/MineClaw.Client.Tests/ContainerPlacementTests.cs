using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class ContainerPlacementTests
{
    private static readonly BlockPos Support = new(10, 64, 8);
    private static readonly BlockPos Target = Support.Up();

    private static void Setup(FakeServer s, int support)
    {
        s.OrientBlocks = true;
        s.ConsumeOnPlace = true;
        s.Blocks[(Support.X, Support.Y, Support.Z)] = support;
        if (support == Blocks.Chest) s.Chests[(Support.X, Support.Y, Support.Z)] = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        if (support == Blocks.Furnace) s.Furnaces[(Support.X, Support.Y, Support.Z)] = Enumerable.Repeat(ItemStack.Empty, 3).ToArray();
        if (support == Blocks.CraftingTable) s.Workbenches.Add((Support.X, Support.Y, Support.Z));
        s.Inventory[36] = Harness.Stack(Blocks.Chest, 4);
    }

    private static Task<ActionResult> Place(MinecraftClient c, string path, CancellationToken ct = default) => path switch
    {
        "plain" => c.PlaceBlockAsync(Target.X, Target.Y, Target.Z, Blocks.Chest, ct),
        "face" => c.PlaceBlockAsync(Target.X, Target.Y, Target.Z, Blocks.Chest, BlockFace.Top, ct),
        "against" => c.PlaceAgainstAsync(Target, BlockFace.Top, Blocks.Chest, null, null, false, ct),
        "turned" => c.PlaceTurnedAsync(Target, Blocks.Chest, null, 5, ct),
        _ => c.PlaceHereAsync(Target, Blocks.Chest, meta: 5, ct: ct),
    };

    private static void ClickWasSneaking(FakeServer s)
    {
        bool sneak = false;
        int clicks = 0;
        foreach (var packet in s.Received)
        {
            if (packet is EntityActionPacket { Action: 1 or 2 } a) sneak = a.Action == 1;
            if (packet is not PlayerBlockPlacementPacket { Direction: not PlayerBlockPlacementPacket.NoDirection } p) continue;
            Assert.Equal((Support.X, Support.Y, Support.Z), (p.X, p.Y, p.Z));
            Assert.True(sneak, "The placement reached the server before start-sneak, or after stop-sneak.");
            clicks++;
        }
        Assert.Equal(1, clicks);
    }

    private static Task<bool> SneakReleased(FakeServer s) => Harness.WaitAsync(() =>
        s.Received.OfType<EntityActionPacket>().LastOrDefault(a => a.Action is 1 or 2)?.Action == 2);

    [Theory]
    [InlineData("plain", Blocks.Chest)]
    [InlineData("face", Blocks.Chest)]
    [InlineData("against", Blocks.Chest)]
    [InlineData("turned", Blocks.Chest)]
    [InlineData("stationary", Blocks.Chest)]
    [InlineData("stationary", Blocks.CraftingTable)]
    [InlineData("stationary", Blocks.Furnace)]
    public async Task Modern_build_paths_sneak_place_on_containers_without_opening_them(string path, int support)
    {
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, support), o => o with { SneakAtEdgesWhenIdle = false }, release1122: true);
        await using var _ = c;
        using var __ = s;
        int opened = 0;
        c.World.Inventory.WindowOpened += _ => Interlocked.Increment(ref opened);
        var start = c.World.Player.Position;
        var result = await Place(c, path);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(Blocks.Chest, c.World.GetBlock(Target));
        Assert.Equal(support, c.World.GetBlock(Support));
        Assert.Equal(3, c.World.Inventory.CountItem(Blocks.Chest));
        Assert.Equal(0, opened);
        Assert.Null(c.World.Inventory.OpenWindow);
        if (path is "turned" or "stationary") Assert.Equal(5, c.World.GetMeta(Target));
        Assert.InRange(c.World.Player.Position.DistanceTo(start), 0, 0.01);
        ClickWasSneaking(s);
        Assert.True(await SneakReleased(s));
        // The same chest remains a normal usable chest after placement. The simulator must not simply stop opening it.
        if (support == Blocks.Chest)
        {
            var use = await c.ActivateBlockAsync(Support.X, Support.Y, Support.Z);
            Assert.True(use.Ok, use.Message);
            Assert.True(await Harness.WaitAsync(() => opened == 1));
        }
    }

    [Theory]
    [InlineData("plain", false)]
    [InlineData("against", false)]
    [InlineData("stationary", true)]
    public async Task Server_refusal_or_cancellation_restores_sneak_without_opening_the_support(string path, bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Setup(srv, Blocks.Chest);
            srv.Hook = p =>
            {
                if (p is not PlayerBlockPlacementPacket { Direction: not PlayerBlockPlacementPacket.NoDirection }) return false;
                if (cancel) cancellation.Cancel();
                return true; // server rejects the edit
            };
        }, o => o with { SneakAtEdgesWhenIdle = false }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var result = await Place(c, path, cancellation.Token);
        Assert.False(result.Ok);
        if (cancel) Assert.Contains("cancelled", result.Message);
        else Assert.True(result.Message.Contains("didn't appear", StringComparison.Ordinal)
            || result.Message.Contains("server undid my placements", StringComparison.Ordinal), result.Message);
        Assert.Equal(0, c.World.GetBlock(Target));
        Assert.Equal(4, c.World.Inventory.CountItem(Blocks.Chest));
        Assert.Null(c.World.Inventory.OpenWindow);
        ClickWasSneaking(s);
        Assert.True(await SneakReleased(s));
    }

    [Fact]
    public async Task Placement_preserves_a_deliberately_held_crouch()
    {
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, Blocks.Chest), o => o with { SneakAtEdgesWhenIdle = false }, release1122: true);
        await using var _ = c;
        using var __ = s;
        Assert.True((await c.CrouchAsync(true)).Ok);
        var result = await Place(c, "stationary");
        Assert.True(result.Ok, result.Message);
        Assert.True(c.IsCrouching);
        Assert.DoesNotContain(s.Received, p => p is EntityActionPacket { Action: 2 });
        ClickWasSneaking(s);
        Assert.True((await c.CrouchAsync(false)).Ok);
        Assert.True(await SneakReleased(s));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("against")]
    [InlineData("stationary")]
    public async Task Beta_keeps_refusing_container_supports_without_clicking_them(string path)
    {
        var (c, s) = await Harness.StartAsync(srv => Setup(srv, Blocks.Chest), o => o with { SneakAtEdgesWhenIdle = false });
        await using var _ = c;
        using var __ = s;
        var result = await Place(c, path);
        Assert.False(result.Ok);
        Assert.DoesNotContain(s.Received, p => p is PlayerBlockPlacementPacket);
        Assert.Equal(0, c.World.GetBlock(Target));
        Assert.Null(c.World.Inventory.OpenWindow);
    }
}
