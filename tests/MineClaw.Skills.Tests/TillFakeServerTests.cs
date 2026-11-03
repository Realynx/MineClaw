using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using Xunit;

namespace MineClaw.Skills.Tests;

/// <summary>
/// till_soil on the real client over the in-process fake server, b1.8.1 and 1.12.2:
/// a protected water source names the center of the patch to till.
/// </summary>
public class TillFakeServerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tilling_around_protected_water_hoes_the_ring_with_right_clicks_only(bool release1122)
    {
        using var data = release1122 ? MineClaw.Data.Versioning.GameData.Override(MineClaw.Data.Versioning.Release1122GameData.Instance) : null;
        using var s = new FakeServer(release1122);
        s.Blocks.Clear();
        for (int x = 2; x <= 14; x++)
        for (int z = 2; z <= 14; z++)
        {
            s.Blocks[(x, 62, z)] = Blocks.Dirt;
            s.Blocks[(x, 63, z)] = Blocks.Grass;
        }
        s.Blocks[(8, 63, 8)] = Blocks.Water;
        s.SpawnAt = (5.5, 64, 8.5);
        s.Inventory[36] = new ItemStack((short)Items.StoneHoe, 1, 0);
        // ItemHoe.onItemUse: the top face of grass/dirt with air above turns to farmland (1.12.2 names no held item: hotbar).
        s.Hook = p =>
        {
            if (p is PlayerBlockPlacementPacket { Direction: 1 } u && (u.HeldItem.Id == Items.StoneHoe || release1122 && u.HeldItem.IsEmpty)
                && s.Blocks.GetValueOrDefault((u.X, u.Y, u.Z)) is Blocks.Grass or Blocks.Dirt && !s.Blocks.ContainsKey((u.X, u.Y + 1, u.Z)))
                s.SetBlock(u.X, u.Y, u.Z, Blocks.Farmland);
            return p is PlayerBlockPlacementPacket;
        };
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), HostileAlertRadius = 0,
            ConnectionFactory = release1122 ? s.CreateConnection : s.CreateClientConnection,
        };
        if (release1122) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        await using var c = new MinecraftClient(o);
        using (var cts0 = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts0.Token);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!(c.World.Player.OnGround && c.World.Inventory.CountItem(Items.StoneHoe) == 1) && DateTime.UtcNow < until) await Task.Delay(5);
        c.IsProtected = (_, id) => BlockShapes.IsWater(id); // the farm's irrigation (Protection: "farmland or irrigation of a farm")

        var reg = SkillRegistry.CreateDefault();
        var ctx = new SkillContext(new ClientGameActions(c), reg);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await reg.RunAsync(ctx, "till_soil", FakeGame.J(new { pos = "8,63,8", count = 4 }), cts.Token);
        output.WriteLine(r.ToString());
        Assert.True(r.Ok, r.Text);
        Assert.Contains("tilled 4", r.Text);
        foreach (var (x, z) in new[] { (7, 8), (9, 8), (8, 7), (8, 9) })
            Assert.Equal(Blocks.Farmland, s.Blocks.GetValueOrDefault((x, 63, z)));
        Assert.Equal(Blocks.Water, s.Blocks.GetValueOrDefault((8, 63, 8)));
        // Tilling is a right-click with the hoe on the top face: never a dig, never a click on the water.
        Assert.Empty(s.Got<PlayerDiggingPacket>().Where(d => d.Status is PlayerDiggingPacket.StatusStarted or PlayerDiggingPacket.StatusFinished));
        var clicks = s.Got<PlayerBlockPlacementPacket>().Where(u => u.Direction != PlayerBlockPlacementPacket.NoDirection).ToList();
        Assert.Equal(4, clicks.Count);
        Assert.All(clicks, u => Assert.True(u.Direction == 1 && u.Y == 63 && (u.X, u.Z) != (8, 8), $"click {u.X},{u.Y},{u.Z} face {u.Direction}"));
    }
}
