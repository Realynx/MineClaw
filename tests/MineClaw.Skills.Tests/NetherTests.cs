using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Skills.Tests;

public class NetherTests
{
    [Fact]
    public async Task Build_and_light_a_portal()
    {
        var g = LiquidTests.Island(6);
        g.Give(Blocks.Obsidian, 10);
        g.Give(Blocks.Cobblestone, 8);
        g.Give(Items.FlintAndSteel, 1);
        var b = await g.Run("build_portal", new { pos = "2,64,-1" });
        Assert.True(b.Ok, b.Text);
        Assert.Equal(0, g.World.Inventory.CountItem(Blocks.Obsidian));
        var l = await g.Run("light_portal");
        Assert.True(l.Ok, l.Text);
        Assert.Equal(Blocks.Portal, g.World.GetBlock(3, 65, -1));
        Assert.Equal(6, g.World.FindBlocks(new Vec3d(3, 65, -1), 4, Blocks.Portal).Count);
    }

    [Fact]
    public async Task Portal_needs_obsidian_and_a_frame()
    {
        var g = LiquidTests.Island(6);
        g.Give(Blocks.Obsidian, 6);
        Assert.Contains("needs 10 obsidian", (await g.Run("build_portal")).Text);
        g.Give(Items.FlintAndSteel, 1);
        Assert.Contains("no complete obsidian frame", (await g.Run("light_portal")).Text);
    }

    [Fact]
    public async Task Enter_portal_records_the_pair_and_scans_nether_hazards()
    {
        var g = LiquidTests.Island(6);
        g.Set(3, 64, 0, Blocks.Portal);
        var task = g.Run("enter_portal");
        // The fake doesn't model travel: switch the dimension the way a 0x09 respawn would.
        Assert.False((await task).Ok, "nothing happens without a dimension change");
        var ctx = g.Context();
        g.World.Dimension = -1;
        var r = await NetherHazardsProbe(g, ctx);
        Assert.Contains("ghast", r);
        Assert.Contains("never hit", r);
        Assert.Equal(new BlockPos(1, 64, -2), SkillState.Scale(new BlockPos(8, 64, -16), toNether: true));
        Assert.Equal(new BlockPos(8, 64, -16), SkillState.Scale(new BlockPos(1, 64, -2), toNether: false));
    }

    private static Task<string> NetherHazardsProbe(FakeGame g, SkillContext ctx)
    {
        g.World.Entities.SpawnMob(20, MobTypes.Ghast, new Vec3d(10, 70, 10), 0, 0);
        g.World.Entities.SpawnMob(21, MobTypes.PigZombie, new Vec3d(3, 64, 3), 0, 0);
        g.Set(-2, 64, -2, Blocks.Lava);
        return Task.FromResult(NetherHazards.Scan(ctx));
    }

    [Fact]
    public async Task Nether_rules_ghasts_block_edge_work_beds_explode_pigmen_never_targeted()
    {
        var g = LiquidTests.Island(4);
        g.World.Dimension = -1;
        g.Give(Blocks.Netherrack, 32);
        g.World.Entities.SpawnMob(20, MobTypes.Ghast, new Vec3d(10, 70, 10), 0, 0);
        var r = await g.Run("expand_island");
        Assert.False(r.Ok);
        Assert.Contains("ghast", r.Text);
        g.World.Entities.Destroy(20);
        g.World.Time = 14000;
        g.Set(2, 64, 2, Blocks.Bed, 8);
        Assert.Contains("overworld", (await g.Run("sleep_in_bed")).Text);
        g.World.Entities.SpawnMob(21, MobTypes.PigZombie, new Vec3d(3, 64, 3), 0, 0);
        g.Give(Items.Snowball, 4);
        Assert.Contains("no hostile", (await g.Run("throw_projectile")).Text);
        Assert.True(NetherHazards.NeverAttack(g.World.Entities.Get(21)!));
        var gl = await g.Run("gather_glowstone");
        Assert.Contains("pickaxe", gl.Text);
        g.Give(Items.IronPickaxe, 1);
        Assert.True((await g.Run("gather_glowstone", new { count = 4 })).Ok);
        Assert.False((await LiquidTests.Island().Run("gather_glowstone")).Ok, "overworld: nether-only skill");
    }

    /// <summary>
    /// Client audit: a 0x09 Respawn to the nether clears the world copy and entities, adopts the new dimension, freezes
    /// movement until the chunks under the player arrive, and re-arms the edge guard on the following 0x0D.
    /// </summary>
    [Fact]
    public async Task Dimension_change_clears_world_freezes_until_chunks_and_rearms()
    {
        var server = new FakeServer();
        var client = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        });
        await using var _c = client;
        using var _s = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await client.ConnectAsync(cts.Token);
        Assert.True(await Wait(() => client.World.Player.OnGround));
        Assert.Equal(Blocks.Stone, client.World.GetBlock(8, 63, 8));
        int respawned = 0;
        client.Respawned += () => respawned++;

        server.Send(new RespawnPacket { Dimension = -1, Difficulty = 1, GameMode = 0, WorldHeight = 128, MapSeed = 1234 });
        Assert.True(await Wait(() => client.World.Dimension == -1));
        Assert.Equal(-1, client.World.Player.Dimension);
        Assert.False(client.World.IsLoaded(8, 8), "old dimension's chunks are gone");
        Assert.Equal(0, client.World.Entities.Count);
        server.Send(new PlayerPositionLookPacket { X = 8.5, Y = 64, Stance = 65.62, Z = 8.5, OnGround = false });
        Assert.True(await Wait(() => client.IsFrozenForSafety), "movement frozen until ground is verified");
        await Task.Delay(100);
        Assert.Equal(64, client.World.Player.Position.Y, 3); // didn't fall while chunks were missing
        for (int cx = -1; cx <= 1; cx++)
        for (int cz = -1; cz <= 1; cz++)
            server.SendChunk(cx, cz);
        Assert.True(await Wait(() => !client.IsFrozenForSafety && client.World.Player.OnGround));
        Assert.Equal(1, respawned);
        Assert.Contains("nether", client.DescribeStatus());
    }

    private static async Task<bool> Wait(Func<bool> cond, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(5); }
        return cond();
    }
}
