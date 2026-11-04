using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Fox 11-04: "The pawns need to crouch-walk through growing zones, so they don't crush plants or tilled ground." The fake server turns
/// farmland to dirt the way the version's server does (b1.8.1: a walker on it that isn't sneaking; 1.12.2: a landing from more than
/// half a block), and sees the sneak entity actions: a pawn crossing a sown field (the only way, a walled corridor) must leave every
/// farmland block as it was, sneaking on the field and walking again once off it.
/// </summary>
public class GrowZoneSneakTests
{
    public static TheoryData<bool> Versions => new() { false, true };

    private static void Corridor(FakeServer srv)
    {
        for (int x = -4; x <= 20; x++)
        {
            for (int y = 60; y <= 63; y++) srv.Blocks[(x, y, 8)] = Blocks.Dirt;
            srv.Blocks[(x, 64, 7)] = srv.Blocks[(x, 65, 7)] = Blocks.Cobblestone;
            srv.Blocks[(x, 64, 9)] = srv.Blocks[(x, 65, 9)] = Blocks.Cobblestone;
        }
        for (int x = 4; x <= 9; x++)
        {
            srv.Blocks[(x, 63, 8)] = Blocks.Farmland;
            srv.Blocks[(x, 64, 8)] = Blocks.Crops;
        }
        srv.SpawnAt = (-2.5, 64, 8.5);
        srv.TrampleFarmland = true;
        srv.MovementChecks = true;
        srv.ServerTickMs = 5;
    }

    [Theory, MemberData(nameof(Versions))]
    public async Task A_pawn_crosses_a_sown_field_sneaking_and_leaves_every_farmland_block(bool release1122)
    {
        var (c, s) = await Harness.StartAsync(Corridor, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        c.GrowZone = (x, y, z) => x is >= 2 and <= 12 && z is >= 6 and <= 10;
        c.SpeedMultiplier = 3; // Faster never boosts a sneaking body
        var r = await c.GotoAsync(18, 64, 8);
        Assert.True(r.Ok, r.Message);
        Assert.Empty(s.Trampled);
        for (int x = 4; x <= 9; x++) Assert.Equal(Blocks.Farmland, s.Blocks.GetValueOrDefault((x, 63, 8)));
        var actions = s.Received.OfType<EntityActionPacket>().Select(a => (int)a.Action).ToList();
        int start = actions.IndexOf(1);
        Assert.True(start >= 0, "never started sneaking");
        Assert.Contains(2, actions.Skip(start)); // and stood up again past the field
        Assert.False(c.World.Player.Physics.Sneaking, "still sneaking at the far end");
        Assert.False(s.Kicked);
    }

    [Fact]
    public async Task The_fake_server_tramples_under_a_jump_on_1122()
    {
        // The rule the test above relies on: a jump in place on the field lands from ~1.2 and turns the farmland to dirt.
        var (c, s) = await Harness.StartAsync(srv => { Corridor(srv); srv.SpawnAt = (6.5, 64, 8.5); }, release1122: true);
        await using var _ = c;
        using var __ = s;
        var r = await c.JumpAsync();
        Assert.True(r.Ok, r.Message);
        Assert.True(await Harness.WaitAsync(() => !s.Trampled.IsEmpty, 2000), "a jump on farmland didn't trample it");
        Assert.Equal(Blocks.Dirt, s.Blocks.GetValueOrDefault((6, 63, 8)));
    }
}
