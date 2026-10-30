using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using Xunit;

namespace MineClaw.Skills.Tests;

/// <summary>
/// The excavator on the real client over the in-process fake server, b1.8.1 and 1.12.2: real dig packets (start/finish with the
/// version's dig time), real walking inside the dug space, the server's block changes. A room in a rock mass dug from the 1×2
/// pocket the body stands in, and a stair pattern extended a few repeats from inside the shaft.
/// </summary>
public class ExcavationFakeServerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    /// <summary>Stone x 2..14, y 54..63, z 2..14 (the fake server's chunks are 128 high on both versions) with a pocket at 8,60..61,8.</summary>
    private static FakeServer Rock(bool release1122)
    {
        var s = new FakeServer(release1122);
        s.Blocks.Clear();
        for (int x = 2; x <= 14; x++)
        for (int y = 54; y <= 63; y++)
        for (int z = 2; z <= 14; z++)
            s.Blocks[(x, y, z)] = Blocks.Stone;
        s.Blocks.Remove((8, 60, 8));
        s.Blocks.Remove((8, 61, 8));
        s.SpawnAt = (8.5, 60, 8.5);
        s.Inventory[36] = new ItemStack((short)Items.ByName("iron_pickaxe")!.Id, 1, 0);
        s.Inventory[37] = new ItemStack(Blocks.Cobblestone, 32, 0);
        return s;
    }

    private static async Task<MinecraftClient> Connect(FakeServer s, bool release1122)
    {
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), HostileAlertRadius = 0,
            ConnectionFactory = release1122 ? s.CreateConnection : s.CreateClientConnection,
        };
        if (release1122) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        var c = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await c.ConnectAsync(cts.Token);
        Assert.True(await Until(() => c.World.Player.OnGround && c.World.Inventory.CountItem(Blocks.Cobblestone) == 32, 5000));
        return c;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_room_is_dug_from_the_pocket_with_real_dig_packets(bool release1122)
    {
        using var data = release1122 ? MineClaw.Data.Versioning.GameData.Override(MineClaw.Data.Versioning.Release1122GameData.Instance) : null;
        using var s = Rock(release1122);
        await using var c = await Connect(s, release1122);
        var game = new ClientGameActions(c);
        var reg = SkillRegistry.CreateDefault();
        var ctx = new SkillContext(game, reg);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // A 3×2×3 room one block east of the pocket: rock between (the access is tunnelled).
        var r = await reg.RunAsync(ctx, "excavate", FakeGame.J(new { box = "10,60,7,12,61,9", torches = false }), cts.Token);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        for (int x = 10; x <= 12; x++)
        for (int y = 60; y <= 61; y++)
        for (int z = 7; z <= 9; z++)
            Assert.Equal(0, s.Blocks.GetValueOrDefault((x, y, z)));
        Assert.Contains("access", r.Text);
        Assert.Equal(Blocks.Stone, s.Blocks.GetValueOrDefault((8, 59, 8))); // never the floor I stand on
        Assert.NotEmpty(s.Got<PlayerDiggingPacket>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stair_pattern_is_extended_from_inside_the_shaft(bool release1122)
    {
        using var data = release1122 ? MineClaw.Data.Versioning.GameData.Override(MineClaw.Data.Versioning.Release1122GameData.Instance) : null;
        using var s = Rock(release1122);
        var p = DigPattern.Parse("column 3 step 0,-1,0 turn -90 pivot 0.5,0.5", new BlockPos(8, 62, 8), out _)!;
        foreach (var cell in p.Cells(0, 2)) s.Blocks.Remove((cell.X, cell.Y, cell.Z));
        var feet = p.Repeat(1).MinBy(q => q.Y);
        s.SpawnAt = (feet.X + 0.5, feet.Y, feet.Z + 0.5);
        await using var c = await Connect(s, release1122);
        var reg = SkillRegistry.CreateDefault();
        var ctx = new SkillContext(new ClientGameActions(c), reg);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var learn = await reg.RunAsync(ctx, "excavate", FakeGame.J(new { op = "learn", name = "stair", rule = p.Rule, at = "8,62,8" }), cts.Token);
        Assert.True(learn.Ok, learn.Text);
        var r = await reg.RunAsync(ctx, "excavate", FakeGame.J(new { site = "stair", repeats = 4, torches = false }), cts.Token);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        foreach (var cell in p.Cells(0, 6)) Assert.Equal(0, s.Blocks.GetValueOrDefault((cell.X, cell.Y, cell.Z)));
        foreach (var tread in DigPattern.Floors(p.Cells(0, 6)).Where(t => t.Y >= 54))
            Assert.Equal(Blocks.Stone, s.Blocks.GetValueOrDefault((tread.X, tread.Y, tread.Z)));
        Assert.Equal(6, reg.Sites.Find("stair")!.Repeats);
        // It walked down its own steps (never outside the 2×2 core: no tunnel, no drop).
        var at = c.World.Player.Position;
        Assert.True(at.X is >= 8 and < 10 && at.Z is >= 8 and < 10 && at.Y < feet.Y, $"in the shaft below where it started (at {at})");
    }
}
