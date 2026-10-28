using MineClaw.Agent;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.World;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Item 11, live 10-27 21:35: clone 69's wheat job "skipped 22 protected: wheat at 4,65,-10 is protected (part of the main
/// house build)": the guessed "main house" box 0,63,-16..15,71,-1 covers the wheat field. Crops are farm plants wherever they
/// grow (ripe = harvestable, unripe = "not ripe (5/7)"); guessed boxes protect only the blocks a build is made of.
/// </summary>
public class FarmProtectionTests
{
    private sealed class HouseBox : IProtectedRegionSource
    {
        public IReadOnlyList<(BlockPos Min, BlockPos Max)> ProtectedRegions() => [(new BlockPos(0, 63, -16), new BlockPos(15, 71, -1))];
        public IReadOnlyList<(BlockPos Min, BlockPos Max, string? Label)> LabeledRegions() =>
            [(new BlockPos(0, 63, -16), new BlockPos(15, 71, -1), "main house")];
    }

    private static readonly BlockPos Wheat = new(4, 65, -10), Soil = new(4, 64, -10), Wall = new(9, 64, -6), Dirt = new(2, 64, -12);

    private static Protection Policy(int wheatMeta, BotOptions? o = null) =>
        new(o ?? new BotOptions(), new HouseBox(), new NoMemory()) { BlockMeta = p => p == Wheat ? wheatMeta : 0 };

    [Fact]
    public void Ripe_wheat_inside_the_house_box_is_harvestable()
    {
        var p = Policy(7);
        Assert.False(p.IsProtected(Wheat, Blocks.Crops));
        Assert.False(p.Check(Wheat, Blocks.Crops).Protected);
    }

    [Fact]
    public void Unripe_wheat_is_left_alone_and_the_refusal_says_what_it_is()
    {
        var p = Policy(5);
        Assert.True(p.IsProtected(Wheat, Blocks.Crops));
        var v = p.Check(Wheat, Blocks.Crops);
        Assert.Equal("farm", v.Reason);
        Assert.Contains("wheat is a farm crop (ripe? no: stage 5/7)", v.Why);
        Assert.Contains("not ripe (5/7)", v.Why);
        Assert.DoesNotContain("main house", v.Why);
    }

    [Fact]
    public void Farmland_in_the_box_stays_soft_protected_as_a_farm_not_a_build()
    {
        var withConfig = Policy(7).Check(Soil, Blocks.Farmland); // default config protects farmland
        Assert.True(withConfig.Protected);
        Assert.Equal("farm", withConfig.Reason);
        var boxOnly = Policy(7, new BotOptions { ProtectedBlocks = [] }).Check(Soil, Blocks.Farmland);
        Assert.True(boxOnly.Protected);
        Assert.Equal("farm", boxOnly.Reason);
        Assert.Contains("farmland", boxOnly.Why);
        Assert.DoesNotContain("main house", boxOnly.Why);
    }

    [Fact]
    public void A_real_house_wall_in_the_box_is_still_protected_but_ground_is_not()
    {
        var p = Policy(7);
        var wall = p.Check(Wall, Blocks.Planks);
        Assert.True(wall.Protected);
        Assert.Contains("part of the main house build", wall.Why);
        Assert.False(p.IsProtected(Dirt, Blocks.Dirt));      // natural fill inside a guessed box isn't a part of it
        Assert.False(p.IsProtected(Dirt, Blocks.TallGrass));
        Assert.False(p.IsProtected(new BlockPos(5, 65, -10), Blocks.SugarCane));
    }

    private static async Task<bool> Until(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    [Fact]
    public async Task Live_replay_the_client_digs_ripe_wheat_in_the_house_box_and_refuses_unripe()
    {
        var server = new FakeServer();
        server.Blocks.Clear();
        for (int x = 1; x <= 7; x++)
        for (int z = -12; z <= -8; z++)
            server.Blocks[(x, 64, z)] = Blocks.Planks;
        server.Blocks[(4, 64, -10)] = Blocks.Farmland;
        server.Blocks[(5, 64, -10)] = Blocks.Farmland;
        server.Blocks[(4, 65, -10)] = Blocks.Crops;
        server.Metas[(4, 65, -10)] = 7;
        server.Blocks[(5, 65, -10)] = Blocks.Crops;
        server.Metas[(5, 65, -10)] = 5;
        server.SpawnAt = (2.5, 65, -9.5);
        var c = new MinecraftClient(new ClientOptions
        {
            Username = "maid_chan_69", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), ConnectionFactory = server.CreateClientConnection, HostileAlertRadius = 0,
        });
        await using var _ = c;
        using var __ = server;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10))) await c.ConnectAsync(cts.Token);
        await Until(() => c.World.Player.OnGround, 3000);
        var o = TestBot.Options();
        using var h = BotHost.Create(new MinecraftGameClient(c), o, null, new NoLlm(), persist: false);
        h.Runtime.Memory.UpsertStructure("main house", s =>
        {
            s.Kind = "house";
            s.Min = new MPos(0, 63, -16);
            s.Max = new MPos(15, 71, -1);
            s.Note = "guessed house, awaiting player confirmation. Don't break.";
            s.Confidence = 0.4;
        });
        h.Protection.Invalidate();

        var unripe = await c.DigBlockAsync(5, 65, -10);
        Assert.False(unripe.Ok);
        Assert.Contains("not ripe (5/7)", unripe.Message);
        Assert.DoesNotContain("main house", unripe.Message);
        // Ripe: no refusal, the dig goes to the server (the fake server doesn't model instant breaks, so it "undoes" it).
        var ripe = await c.DigBlockAsync(4, 65, -10);
        Assert.DoesNotContain("is protected", ripe.Message);
        Assert.Contains(server.Got<MineClaw.Protocol.Packets.PlayerDiggingPacket>(), d => d.X == 4 && d.Y == 65 && d.Z == -10);
        Assert.DoesNotContain(server.Got<MineClaw.Protocol.Packets.PlayerDiggingPacket>(), d => d.X == 5 && d.Y == 65 && d.Z == -10);
        var wall = await c.DigBlockAsync(6, 64, -12);
        Assert.False(wall.Ok);
        Assert.Contains("part of the main house build", wall.Message);
    }
}
