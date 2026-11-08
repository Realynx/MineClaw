using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Client.Tests;

public sealed class QuarryStairRepairTests(Xunit.Abstractions.ITestOutputHelper log)
{
    // Live 195,44,366 -> 195,43,367 -> 195,42,368, translated into the fake server's loaded chunks.
    private static readonly BlockPos Previous = new(8, 44, 8);
    private static readonly BlockPos Missing = new(8, 43, 9);
    private static readonly BlockPos Scaffold = new(9, 43, 9);
    private static readonly BlockPos Next = new(8, 42, 10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_floating_quarry_step_is_supported_repaired_and_cleaned_from_the_existing_stair(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal);
        var (c, s) = await Harness.StartAsync(srv =>
        {
            srv.Blocks.Clear();
            for (int x = -4; x <= 15; x++)
                for (int z = -4; z <= 15; z++) srv.Blocks[(x, 34, z)] = Blocks.Stone;
            for (int y = 35; y <= 42; y++)
                for (int z = 7; z <= 12; z++) srv.Blocks[(9, y, z)] = Blocks.Stone;
            srv.Blocks[(Previous.X, Previous.Y, Previous.Z)] = Blocks.Stone;
            srv.Blocks[(7, 45, 8)] = Blocks.Stone;
            srv.Blocks[(6, 46, 8)] = Blocks.Stone;
            srv.Blocks[(8, 41, 11)] = Blocks.Stone; // intact step after the two repairs: a safe continuation beyond their landing cells
            srv.SpawnAt = (8.5, 45, 8.5);
            srv.Inventory[36] = Harness.Stack(Blocks.Cobblestone, 8);
            srv.Inventory[37] = Harness.Stack(Items.StonePickaxe, 1);
            srv.ConsumeOnPlace = true;
        }, o => o with { Log = m => { try { log.WriteLine(m); } catch (InvalidOperationException) { } } }, release1122);
        await using var _ = c;
        using var __ = s;
        var expected = s.Blocks.ToDictionary(k => k.Key, v => v.Value);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var direct = await c.PlaceBlockAsync(Missing.X, Missing.Y, Missing.Z, Blocks.Cobblestone, timeout.Token);
        Assert.False(direct.Ok);
        Assert.Contains("nothing solid", direct.Message);

        var scaffold = await c.PlaceBlockAsync(Scaffold.X, Scaffold.Y, Scaffold.Z, Blocks.Cobblestone, timeout.Token);
        Assert.True(scaffold.Ok, "scaffold: " + scaffold.Message);
        Assert.True(c.World.Player.Position.Y >= 44.99, "scaffold placement left the preceding step");
        var repaired = await c.PlaceBlockAsync(Missing.X, Missing.Y, Missing.Z, Blocks.Cobblestone, timeout.Token);
        Assert.True(repaired.Ok, "repair: " + repaired.Message);
        var cleaned = await c.DigBlockAsync(Scaffold.X, Scaffold.Y, Scaffold.Z, timeout.Token);
        Assert.True(cleaned.Ok, "cleanup: " + cleaned.Message);
        Assert.Equal(0, c.World.GetBlock(Scaffold));
        Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(Missing));
        Assert.Equal(Blocks.Stone, c.World.GetBlock(Previous));
        Assert.True(c.World.Player.Position.Y >= 43.99, "cleanup dropped below the repaired stair");

        var onto = await c.GotoAsync(new GoalBlock(Missing.X, Missing.Y + 1, Missing.Z), ct: timeout.Token);
        Assert.True(onto.Ok, "approach: " + onto.Message);
        Assert.InRange(c.World.Player.Position.Y, 43.99, 44.01);
        Assert.Equal(0, c.World.GetBlock(Next));
        var next = await c.PlaceBlockAsync(Next.X, Next.Y, Next.Z, Blocks.Cobblestone, timeout.Token);
        Assert.True(next.Ok, "next step: " + next.Message);
        var down = await c.GotoAsync(new GoalBlock(Next.X, Next.Y + 1, Next.Z), ct: timeout.Token);
        Assert.True(down.Ok, "descent: " + down.Message);
        Assert.True(c.World.Player.OnGround);
        Assert.InRange(c.World.Player.Position.Y, 42.99, 43.01);
        expected[(Missing.X, Missing.Y, Missing.Z)] = Blocks.Cobblestone;
        expected[(Next.X, Next.Y, Next.Z)] = Blocks.Cobblestone;
        Assert.Equal(expected.OrderBy(k => k.Key), s.Blocks.OrderBy(k => k.Key));
    }
}
