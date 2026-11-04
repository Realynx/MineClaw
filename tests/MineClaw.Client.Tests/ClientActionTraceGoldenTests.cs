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
        DropSneakBlips(lines);
        SlotChangesBeforeSneaks(lines);
        return string.Join("\n", lines);
    }

    /// <summary>The tick loop sends a sneak press/release (it follows the action's sneak flag), while the action sends its hotbar
    /// change itself: under load the two land in either order (a full Client.Tests run on main). In a run of nothing but slot
    /// changes and sneak toggles the slot changes go first (stable), so the order between the two threads never decides the trace;
    /// each one's place against every other packet (the sneak before the placement it covers) is still pinned.</summary>
    private static void SlotChangesBeforeSneaks(List<string> lines)
    {
        static bool Slot(string l) => l.StartsWith("HoldingChangePacket ", StringComparison.Ordinal);
        static bool Sneak(string l) => l.StartsWith("EntityActionPacket Action=1 ", StringComparison.Ordinal)
                                       || l.StartsWith("EntityActionPacket Action=2 ", StringComparison.Ordinal);
        for (int i = 0; i < lines.Count; i++)
        {
            int j = i;
            while (j < lines.Count && (Slot(lines[j]) || Sneak(lines[j]))) j++;
            if (j - i < 2) continue;
            var run = lines.GetRange(i, j - i);
            lines.RemoveRange(i, j - i);
            lines.InsertRange(i, run.Where(Slot).Concat(run.Where(Sneak)));
            i = j - 1;
        }
    }

    /// <summary>A sneak released and pressed again with only a hotbar change in between (two sneaking actions back to back, a tick
    /// between them or not) is the same as holding it: the pair is dropped, so tick timing never decides the trace.</summary>
    private static void DropSneakBlips(List<string> lines)
    {
        const string release = "EntityActionPacket Action=2 ", press = "EntityActionPacket Action=1 ";
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith(release, StringComparison.Ordinal)) continue;
            int j = i + 1;
            while (j < lines.Count && lines[j].StartsWith("HoldingChangePacket ", StringComparison.Ordinal)) j++;
            if (j >= lines.Count || !lines[j].StartsWith(press, StringComparison.Ordinal)) continue;
            lines.RemoveAt(j);
            lines.RemoveAt(i);
            i--;
        }
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
        // Two ticks after each action: the tick that releases an action's sneak lands before the next action's packets. Without it
        // a tick already under way when the action ended (it read the sneak still held) could count as the next action's look tick,
        // and a deposit done within one tick then went out before the release (a load race: 1 in ~20 runs).
        async Task<string> Settled(Task<ActionResult> action)
        {
            var r = await action;
            long t0 = c.TickCount;
            await Harness.WaitAsync(() => c.TickCount >= t0 + 2);
            return r.ToString();
        }
        var results = new List<string>
        {
            await Settled(c.DigBlockAsync(9, 64, 9)),
            await Settled(c.PlaceBlockAsync(9, 64, 9, Blocks.Dirt)),
            await Settled(c.PlaceTurnedAsync(new BlockPos(7, 64, 7), Blocks.Furnace, null, 5)),
            await Settled(c.DepositAllAsync(new BlockPos(10, 64, 6), st => st.Id == Blocks.Cobblestone)),
            await Settled(c.WithdrawAsync(Blocks.Sand, 2, new BlockPos(10, 64, 6))),
            await Settled(c.CraftAsync(Blocks.Planks, 0, 4)),
            await Settled(c.HoldAsync(st => st.Id == Blocks.Dirt, "dirt")),
            await Settled(c.JumpAsync()),
            await Settled(c.SwingArmAsync(2)),
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
            var path = Path.Combine(Path.GetTempPath(), $"mineclaw-trace-{(modern ? "1122" : "beta")}-{DateTime.UtcNow:HHmmssfff}.txt");
            File.WriteAllText(path, trace);
            Assert.Fail($"action trace drifted (actual: {path})");
        }
    }
}
