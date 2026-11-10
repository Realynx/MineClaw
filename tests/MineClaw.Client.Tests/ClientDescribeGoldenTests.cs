using MineClaw.Data;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

/// <summary>
/// Pins the model-facing description lines (complexity refactor WP05) before they were reshaped: <see
/// cref="MinecraftClient.DescribeSurroundings"/> (underfoot, void edges, the edge mark, census, crops, points of interest, players,
/// mobs, drops, online list; every radius/detail/truncation combination below), <see cref="MinecraftClient.DescribeStatus"/> and
/// <see cref="MinecraftClient.DescribeInventory"/>, on both versions, over a busy island, a walled-in one and a one-block pillar.
/// The scenes hold still (no mob AI, no reflexes, fixed time), so the text is the same on every run.
/// </summary>
public class ClientDescribeGoldenTests
{
    private static void Busy(FakeServer s)
    {
        for (int x = 2; x <= 14; x++)
        for (int z = 6; z <= 10; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        s.Blocks[(7, 64, 7)] = Blocks.Chest;
        s.Blocks[(3, 64, 7)] = Blocks.Chest;
        s.Blocks[(10, 64, 10)] = Blocks.Furnace;
        s.Blocks[(12, 64, 6)] = Blocks.Workbench;
        s.Blocks[(2, 64, 10)] = Blocks.Water;
        s.Blocks[(14, 64, 10)] = Blocks.Lava;
        s.Blocks[(13, 64, 8)] = Blocks.Log;
        s.Blocks[(13, 65, 8)] = Blocks.Log;
        s.Blocks[(13, 66, 8)] = Blocks.Leaves;
        s.Blocks[(4, 64, 9)] = Blocks.Sapling;
        s.Blocks[(5, 62, 8)] = Blocks.CoalOre;
        s.Blocks[(6, 62, 8)] = Blocks.IronOre;
        s.Blocks[(6, 62, 9)] = Blocks.IronOre;
        s.Blocks[(11, 64, 9)] = 44; // slabs: stone and cobblestone
        s.Blocks[(11, 64, 10)] = 44;
        s.Metas[(11, 64, 10)] = 3;
        s.Blocks[(12, 64, 9)] = 43;
        s.Metas[(12, 64, 9)] = 1;
        for (int x = 3; x <= 6; x++)
        {
            s.Blocks[(x, 63, 6)] = Blocks.Farmland;
            s.Metas[(x, 63, 6)] = 7;
            if (x == 6) continue;
            s.Blocks[(x, 64, 6)] = Blocks.Crops;
            s.Metas[(x, 64, 6)] = x == 4 ? 3 : 7;
        }
        s.Blocks[(9, 64, 6)] = Blocks.Cobblestone; // a wall north of the stand: E/W void edges stay
        s.Blocks[(9, 65, 6)] = Blocks.Cobblestone;
        s.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1, 17);
        s.Inventory[37] = Harness.Stack(Blocks.Dirt, 30);
        s.Inventory[12] = Harness.Stack(Blocks.Dirt, 64);
        s.Inventory[13] = Harness.Stack(Blocks.Cobblestone, 9);
        s.Inventory[14] = Harness.Stack(Items.StonePickaxe, 1, 3);
        s.Inventory[6] = Harness.Stack(Items.IronChestplate, 1);
    }

    private static void Walled(FakeServer s)
    {
        for (int x = 4; x <= 12; x++)
        for (int z = 4; z <= 12; z++)
        {
            s.Blocks[(x, 63, z)] = Blocks.Grass;
            if (x is 4 or 12 || z is 4 or 12)
            {
                s.Blocks[(x, 64, z)] = Blocks.Planks;
                s.Blocks[(x, 65, z)] = Blocks.Planks;
            }
        }
    }

    private static void Pillar(FakeServer s)
    {
        s.Blocks.Clear();
        s.Blocks[(8, 63, 8)] = Blocks.Dirt;
        s.Blocks[(8, 62, 8)] = Blocks.Dirt;
    }

    private static async Task<string> SceneAsync(string scene, bool modern)
    {
        Action<FakeServer> setup = scene switch { "busy" => Busy, "walled" => Walled, _ => Pillar };
        var (c, s) = await Harness.StartAsync(setup, o => o with
        {
            Survival = o.Survival with { Enabled = false, SecureOnSpawn = false, ShelterEnabled = false },
        }, release1122: modern);
        await using var _ = c;
        using var __ = s;
        c.Combat.DefendSelf = false; // the zombies stay put and so does the body
        int expectEntities = 0;
        if (scene == "busy")
        {
            s.Send(new NamedEntitySpawnPacket { EntityId = 7, PlayerName = "partner", X = 10 * 32 + 16, Y = 64 * 32, Z = 9 * 32, Yaw = 64 });
            s.Send(new NamedEntitySpawnPacket { EntityId = 6, PlayerName = "fox", X = 3 * 32, Y = 64 * 32, Z = 8 * 32 });
            s.SpawnDrop(12.5, 64, 9.5, Harness.Stack(Blocks.Cobblestone, 3)); // out of pickup reach
            s.SpawnDrop(4.5, 64, 9.5, Harness.Stack(Blocks.Cobblestone, 2));
            s.SpawnDrop(11.5, 64, 7.5, Harness.Stack(Blocks.Sapling, 1, 2));
            s.SpawnZombie(4, 64, 10);
            s.SpawnZombie(13, 64, 7);
            s.Send(new MobSpawnPacket { EntityId = 90, Type = 90, X = 2 * 32, Y = 64 * 32, Z = 9 * 32 }); // a pig
            s.Send(new PlayerListItemPacket { PlayerName = "fox", Online = true });
            s.Send(new PlayerListItemPacket { PlayerName = "maid_chan", Online = true });
            s.Send(new PlayerListItemPacket { PlayerName = "robin", Online = true });
            expectEntities = 8;
        }
        Assert.True(await Harness.WaitAsync(() => c.World.Entities.Count >= expectEntities
                                                && (scene != "busy" || c.World.Entities.OnlinePlayers.Count >= 3) && c.World.Player.OnGround, 5000));
        await Task.Delay(50);
        var lines = new List<string>
        {
            c.DescribeSurroundings(),
            c.DescribeSurroundings(12, detail: true),
            c.DescribeSurroundings(4),
            c.DescribeSurroundings(30, detail: true, maxChars: 5000),
            c.DescribeSurroundings(12, maxChars: 120),
            c.DescribeSurroundings(2, detail: true),
            c.DescribeStatus(),
            c.DescribeInventory(),
            c.DescribeInventory(detail: true),
        };
        return $"== {scene} {(modern ? "1.12.2" : "b1.8.1")}\n" + string.Join("\n", lines) + "\n";
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Descriptions_match_the_golden(bool modern)
    {
        var text = await SceneAsync("busy", modern) + await SceneAsync("walled", modern) + await SceneAsync("pillar", modern);
        SimRulesGoldenTests.Check($"describe-{(modern ? "1122" : "beta")}", modern ? "9B3EC0DD2911560338F68C7AB70671786196981B124BAFADC0AA756C33139664" : "D0125AC1DA95D0F8694660FCB135E9B16D0011AAFE38B2E4779ADAD412CAFC11", sb => sb.Append(text));
    }
}
