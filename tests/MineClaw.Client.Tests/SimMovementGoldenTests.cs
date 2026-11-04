using System.Globalization;
using System.Reflection;
using System.Text;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;

namespace MineClaw.Client.Tests;

/// <summary>
/// Pins the FakeServer's vanilla movement checks and farmland trampling (complexity refactor WP05) before they were reshaped:
/// seeded random packet streams (walks, jumps, falls, too-quick leaps, illegal stances, moves into a wall, sneaks, look and
/// on-ground packets, the answer to a rollback, server teleports) go straight into <c>CheckMovement</c> and <c>TrackTrample</c>
/// in the server's order, on both versions and with the 1.12.2 tick window at both extremes, and every verdict, counter, the
/// accepted position and the trampled cells are dumped and hashed. The whole suite moves through these checks.
/// </summary>
public class SimMovementGoldenTests
{
    private static readonly MethodInfo Check = typeof(MineClaw.Sim.FakeServer).GetMethod("CheckMovement", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo Trample = typeof(MineClaw.Sim.FakeServer).GetMethod("TrackTrample", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo Good = typeof(MineClaw.Sim.FakeServer).GetField("_good", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo Checking = typeof(MineClaw.Sim.FakeServer).GetField("_checkMovement", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static void World(FakeServer s)
    {
        for (int x = 0; x <= 16; x++)
        for (int z = 0; z <= 16; z++)
            s.Blocks[(x, 63, z)] = Blocks.Stone;
        for (int z = 0; z <= 16; z++)
        {
            s.Blocks[(12, 64, z)] = Blocks.Cobblestone; // a wall
            s.Blocks[(12, 65, z)] = Blocks.Cobblestone;
        }
        s.Blocks[(5, 64, 8)] = Blocks.Dirt; // a step
        s.Blocks[(6, 64, 5)] = 44; // a slab
        for (int x = 2; x <= 10; x++)
        {
            s.Blocks[(x, 63, 3)] = Blocks.Farmland;
            if (x % 2 == 0) s.Blocks[(x, 64, 3)] = Blocks.Crops;
        }
    }

    private static void Run(StringBuilder sb, bool modern, int tickMs, int seed)
    {
        var s = new FakeServer(modern);
        try
        {
            World(s);
            s.MovementChecks = true;
            s.TrampleFarmland = true;
            s.ServerTickMs = tickMs;
            var r = new Random(seed);
            double x = 8.5, y = 64, z = 8.5;
            sb.Append($"== {(modern ? "1.12.2" : "b1.8.1")} tick {tickMs} seed {seed}\n");
            for (int i = 0; i < 400; i++)
            {
                var (p, what) = Next(s, r, ref x, ref y, ref z);
                if (s.TrampleFarmland) Trample.Invoke(s, [p]);
                string verdict;
                try { verdict = (bool)Check.Invoke(s, [p])! ? "drop" : "pass"; }
                catch (TargetInvocationException e) { verdict = "!" + e.InnerException!.GetType().Name; }
                var g = ((double X, double Y, double Z)?)Good.GetValue(s);
                sb.Append(i).Append(' ').Append(what).Append(' ').Append(verdict)
                    .Append(" q").Append(s.MovedTooQuickly).Append(" w").Append(s.MovedWrongly).Append(" r").Append(s.Rollbacks)
                    .Append(" st").Append(s.IllegalStance).Append(s.Kicked ? " K" : "").Append(" big ").Append(F(s.LargestStep))
                    .Append(" good ").Append(g is { } v ? $"{F(v.X)},{F(v.Y)},{F(v.Z)}" : "-").Append((bool)Checking.GetValue(s)! ? " on" : " off")
                    .Append(" acc").Append(s.Accepted.Count).Append(" tr").Append(s.Trampled.Count)
                    .Append(" | ").Append(s.LastRejection).Append('\n');
            }
            foreach (var t in s.Trampled) sb.Append($"trampled {t.X},{t.Y},{t.Z} {t.How}\n");
            foreach (var a in s.Accepted) sb.Append($"accepted {F(a.X)},{F(a.Y)},{F(a.Z)}\n");
        }
        finally { s.Dispose(); }
    }

    private static (IPacket Packet, string What) Next(FakeServer s, Random r, ref double x, ref double y, ref double z)
    {
        int k = r.Next(100);
        bool ground = r.Next(3) > 0;
        if (k < 45)
        {
            x += r.NextDouble() * 0.8 - 0.4;
            z += r.NextDouble() * 0.8 - 0.4;
            y += new[] { 0, 0, 0, 0.42, -0.08, -0.5, 1.0, 0.5 }[r.Next(8)];
            return (Pos(r, x, y, z, y + 1.62, ground), "move");
        }
        if (k < 55)
        {
            if (((double X, double Y, double Z)?)Good.GetValue(s) is { } g) (x, y, z) = (g.X, g.Y, g.Z);
            return (Pos(r, x, y, z, y + 1.62, ground), "answer");
        }
        if (k < 61)
        {
            x += 6 + r.NextDouble() * 6;
            return (Pos(r, x, y, z, y + 1.62, ground), "leap");
        }
        if (k < 66) return (Pos(r, x, y, z, y + (r.Next(2) == 0 ? 2.0 : 0.05), ground), "stance");
        if (k < 70) return (new PlayerOnGroundPacket { OnGround = ground }, "ground");
        if (k < 74) return (new PlayerLookPacket { Yaw = r.Next(360), Pitch = 10, OnGround = ground }, "look");
        if (k < 82) return (new EntityActionPacket { EntityId = 42, Action = (sbyte)(r.Next(2) + 1) }, "sneak");
        if (k < 88)
        {
            x = 11.9 + r.NextDouble() * 0.4;
            return (Pos(r, x, y, z, y + 1.62, ground), "wall");
        }
        if (k < 92)
        {
            (x, y, z) = (1.5 + r.Next(14), 64, 1.5 + r.Next(14));
            s.MovedBy(x, y, z);
            return (new PlayerOnGroundPacket { OnGround = true }, "teleport");
        }
        if (k < 96)
        {
            (x, y, z) = (2.5 + r.Next(9), 64, 3.5 + r.NextDouble() * 0.2 - 0.1);
            return (Pos(r, x, y, z, y + 1.62, true), "onfarm");
        }
        y += 1.5;
        return (Pos(r, x, y, z, y + 1.62, false), "rise");
    }

    private static IPacket Pos(Random r, double x, double y, double z, double stance, bool ground) => r.Next(2) == 0
        ? new PlayerPositionPacket { X = x, Y = y, Z = z, Stance = stance, OnGround = ground }
        : new PlayerPositionLookPacket { X = x, Y = y, Z = z, Stance = stance, Yaw = 0, Pitch = 0, OnGround = ground };

    [Theory]
    [InlineData(false, "54C726266D9908283A1B576B2D833C767D2B9CFF46755E7D85739AD84EF99C3E")]
    [InlineData(true, "2B061ABB0BE045AF8030B6DF071FB397826B137053AA44F0A3A58B3DF488C980")]
    public void Movement_checks_match_the_golden(bool modern, string digest) =>
        SimRulesGoldenTests.Check($"sim-movement-{(modern ? "1122" : "beta")}", digest, sb =>
        {
            foreach (int seed in new[] { 1, 2, 3, 4 })
            {
                Run(sb, modern, 1_000_000, seed);
                if (modern) Run(sb, modern, 0, seed);
            }
        });
}
