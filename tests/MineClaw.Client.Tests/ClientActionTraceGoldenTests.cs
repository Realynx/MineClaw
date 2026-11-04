using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Pins the packets the client sends for a run of representative actions (dig, place, turned placement, chest deposit and
/// withdraw, crafting, holding, jumping, swinging) on the FakeServer, both versions, before the complexity refactor (WP05) reshapes
/// the action and dispatch code: every non-movement packet with all its fields, in order. Runs of identical lines (a per-tick
/// swing while waiting on the server, keep-alives) collapse to one, so tick timing never decides the trace.
/// </summary>
public class ClientActionTraceGoldenTests
{
    private static string Value(object? v) => v switch
    {
        null => "null",
        string s => s,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Value)) + "]",
        _ => v.ToString() ?? "",
    };

    internal static string Line(IPacket p)
    {
        var sb = new StringBuilder(p.GetType().Name);
        foreach (var prop in p.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name, StringComparer.Ordinal))
            if (prop.GetIndexParameters().Length == 0)
                sb.Append(' ').Append(prop.Name).Append('=').Append(Value(prop.GetValue(p)));
        return sb.ToString();
    }

    internal static string Trace(IEnumerable<IPacket> received)
    {
        var lines = new List<string>();
        foreach (var p in received)
        {
            if (p is PlayerOnGroundPacket or PlayerPositionPacket or PlayerLookPacket or PlayerPositionLookPacket or KeepAlivePacket) continue;
            var line = Line(p);
            if (lines.Count == 0 || lines[^1] != line) lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    private static void World(FakeServer s)
    {
        s.OrientBlocks = true;
        s.CraftingEnabled = true;
        s.Blocks[(9, 64, 9)] = Blocks.Cobblestone;
        s.Blocks[(10, 64, 6)] = Blocks.Chest;
        s.Chests[(10, 64, 6)] = Enumerable.Range(0, 27).Select(i => i == 3 ? Harness.Stack(Blocks.Sand, 5) : ItemStack.Empty).ToArray();
        s.Inventory[36] = Harness.Stack(Items.StonePickaxe, 1);
        s.Inventory[37] = Harness.Stack(Blocks.Dirt, 8);
        s.Inventory[38] = Harness.Stack(Blocks.Furnace, 2);
        s.Inventory[39] = Harness.Stack(Blocks.Log, 3);
        s.Inventory[12] = Harness.Stack(Blocks.Cobblestone, 7);
    }

    private static async Task<string> RunAsync(bool modern)
    {
        var (c, s) = await Harness.StartAsync(World, release1122: modern);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Harness.WaitAsync(() => c.World.GetBlock(10, 64, 6) == Blocks.Chest));
        var results = new List<string>
        {
            (await c.DigBlockAsync(9, 64, 9)).ToString(),
            (await c.PlaceBlockAsync(9, 64, 9, Blocks.Dirt)).ToString(),
            (await c.PlaceTurnedAsync(new BlockPos(7, 64, 7), Blocks.Furnace, null, 5)).ToString(),
            (await c.DepositAllAsync(new BlockPos(10, 64, 6), st => st.Id == Blocks.Cobblestone)).ToString(),
            (await c.WithdrawAsync(Blocks.Sand, 2, new BlockPos(10, 64, 6))).ToString(),
            (await c.CraftAsync(Blocks.Planks, 0, 4)).ToString(),
            (await c.HoldAsync(st => st.Id == Blocks.Dirt, "dirt")).ToString(),
            (await c.JumpAsync()).ToString(),
            (await c.SwingArmAsync(2)).ToString(),
        };
        await Task.Delay(100);
        return string.Join("\n", results) + "\n----\n" + Trace(s.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Representative_actions_send_the_pinned_packets(bool modern)
    {
        var trace = await RunAsync(modern);
        var expected = modern ? ClientActionTraceGoldenData.Modern : ClientActionTraceGoldenData.Beta;
        if (expected.ReplaceLineEndings("\n") != trace)
        {
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-trace-{(modern ? "1122" : "beta")}.txt");
            File.WriteAllText(path, trace);
            Assert.Fail($"action trace drifted (actual: {path})");
        }
    }
}
