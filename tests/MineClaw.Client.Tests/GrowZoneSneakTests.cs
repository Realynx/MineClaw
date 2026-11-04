using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Fox 11-04: "The pawns need to crouch-walk through growing zones, so they don't crush plants or tilled ground." The fake server turns
/// farmland to dirt the way the version's server does (b1.8.1: a walker on it that isn't sneaking; 1.12.2: a landing from more than
/// half a block), and sees the sneak and sprint entity actions: a pawn crossing a sown field (the only way, a walled corridor) must
/// leave every farmland block as it was.
/// </summary>
public class GrowZoneSneakTests
{
    public static TheoryData<bool> Versions => new() { false, true };

    private static void Corridor(FakeServer srv)
    {
        for (int x = -4; x <= 20; x++)
        {
            // Solid walls down to the ground: on 1.12.2 farmland (15/16 high) a wall hanging from y 64 leaves a sliver of "drop" beside the
            // feet, and the edge guard would sneak there.
            for (int y = 60; y <= 63; y++) srv.Blocks[(x, y, 8)] = Blocks.Dirt;
            for (int y = 60; y <= 65; y++) srv.Blocks[(x, y, 7)] = srv.Blocks[(x, y, 9)] = Blocks.Cobblestone;
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
    public async Task A_pawn_crosses_a_sown_field_the_versions_way_and_leaves_every_farmland_block(bool release1122)
    {
        // Fox 11-04: "In the version we're using now (1.12.2) we can walk on wheat and not crush it. We can't sprint on it, but we don't
        // have to crouch-walk either, like the beta version." b1.8.1 sneaks over the field; 1.12.2 walks it, never sprinting there.
        var flagged = new List<(int Action, double X)>();
        double lastX = double.NaN;
        bool sprinting = false;
        int sprintOnField = 0;
        var (c, s) = await Harness.StartAsync(srv =>
        {
            Corridor(srv);
            srv.Hook = p =>
            {
                lock (flagged)
                {
                    switch (p)
                    {
                        case PlayerPositionPacket pp: lastX = pp.X; break;
                        case PlayerPositionLookPacket pl: lastX = pl.X; break;
                        case EntityActionPacket a: flagged.Add((a.Action, lastX)); if (a.Action == 4) sprinting = true; else if (a.Action == 5) sprinting = false; break;
                    }
                    if (sprinting && lastX is > 4.3 and < 9.7) sprintOnField++;
                }
                return false;
            };
        }, release1122: release1122);
        await using var _ = c;
        using var __ = s;
        c.GrowZone = (x, y, z) => x is >= 2 and <= 12 && z is >= 6 and <= 10;
        c.SpeedMultiplier = 3;
        var r = await c.GotoAsync(new GoalBlock(18, 64, 8), new GotoOptions { AllowSprint = true });
        Assert.True(r.Ok, r.Message);
        Assert.Empty(s.Trampled);
        for (int x = 4; x <= 9; x++) Assert.Equal(Blocks.Farmland, s.Blocks.GetValueOrDefault((x, 63, 8)));
        List<(int Action, double X)> actions;
        lock (flagged) actions = flagged.ToList();
        Assert.Equal(0, sprintOnField);
        if (release1122)
            Assert.True(!actions.Any(a => a.Action == 1), "sneaked: " + string.Join(" ", actions.Select(a => $"{a.Action}@{a.X:0.00}"))); // no sneaking at all
        else
        {
            var start = actions.FindIndex(a => a.Action == 1);
            Assert.True(start >= 0, "never started sneaking");
            Assert.InRange(actions[start].X, 1.5, 4.5); // at the field (beside its first crop)
            var stop = actions.FindIndex(start, a => a.Action == 2);
            Assert.True(stop > start, "never stood up again");
            Assert.InRange(actions[stop].X, 9, 12.5);
            Assert.DoesNotContain(actions.Skip(stop + 1), a => a.Action == 1);
        }
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
