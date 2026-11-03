using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Sim;

/// <summary>A fixed, tiny Skyblock start; no terrain generation or downloaded assets.</summary>
public static class SkyblockStart
{
    public const string PlayerName = "tester";
    public const double PlayerX = 11.5, PlayerY = 64, PlayerZ = 8.5;
    public static readonly (int X, int Y, int Z) GenCell = (10, 64, 6);

    public static void Configure(FakeServer session)
    {
        session.Blocks.Clear();
        for (int x = 4; x <= 13; x++)
        for (int z = 4; z <= 13; z++)
        {
            if (x > 8 && z > 9) continue;
            for (int y = 61; y <= 63; y++)
                session.Blocks[(x, y, z)] = y == 63 ? Blocks.Grass : Blocks.Dirt;
        }
        for (int y = 64; y <= 68; y++) session.Blocks[(5, y, 5)] = Blocks.Log;
        for (int x = 3; x <= 7; x++)
        for (int z = 3; z <= 7; z++)
        for (int y = 67; y <= 69; y++)
            if (!session.Blocks.ContainsKey((x, y, z))) session.Blocks[(x, y, z)] = Blocks.Leaves;
        session.Blocks[(6, 64, 11)] = Blocks.Chest;
        var chest = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
        chest[0] = new ItemStack(Items.LavaBucket, 1, 0);
        chest[1] = new ItemStack(Blocks.Ice, 2, 0);
        chest[2] = new ItemStack(Blocks.Sapling, 1, 0);
        session.Chests[(6, 64, 11)] = chest;
        // Raised, contained trench away from the tree and island edge:
        // water -> output <- flowing lava <- still lava source. Water has a
        // one-block sink so it falls rather than washing across the output.
        session.Blocks[(8, 64, 6)] = Blocks.Water;
        session.Blocks[(9, 64, 6)] = Blocks.FlowingWater;
        session.Metas[(9, 64, 6)] = 1;
        session.Blocks.Remove((9, 63, 6));
        session.Blocks[GenCell] = Blocks.Cobblestone;
        session.Blocks[(11, 64, 6)] = Blocks.FlowingLava;
        session.Metas[(11, 64, 6)] = 1;
        session.Blocks[(12, 64, 6)] = Blocks.Lava;
        foreach (var wall in new[] { (7, 64, 6), (8, 64, 5), (8, 64, 7), (9, 64, 5), (9, 64, 7),
                     (11, 64, 5), (11, 64, 7), (12, 64, 5), (12, 64, 7), (13, 64, 6) })
            session.Blocks[wall] = Blocks.Stone;
        // A one-row bed, reachable from the grass path without trampling.
        // Wheat block 59 metadata is the Beta growth stage (7 = ripe).
        for (int x = 5; x <= 8; x++)
        {
            session.Blocks[(x, 63, 9)] = 60; // farmland
            session.Metas[(x, 63, 9)] = 7; // hydrated
            session.Blocks[(x, 64, 9)] = 59;
            session.Metas[(x, 64, 9)] = x == 6 ? 3 : 7;
        }
        session.Regenerate[GenCell] = Blocks.Cobblestone;
        session.RegenerateDelayMs = 1000;
        session.DropOnDig = true;
        session.StationaryPickup = true;
        session.ConsumeOnPlace = true;
    }

    public static void SpawnPlayer(FakeServer session) => session.Send(new NamedEntitySpawnPacket
    {
        EntityId = 43, PlayerName = PlayerName,
        X = FixedPoint.ToAbsolute(PlayerX), Y = FixedPoint.ToAbsolute(PlayerY), Z = FixedPoint.ToAbsolute(PlayerZ),
    });

    public static void Say(FakeServer session, string text) => session.Send(new ChatPacket($"<{PlayerName}> {text}"));
}
