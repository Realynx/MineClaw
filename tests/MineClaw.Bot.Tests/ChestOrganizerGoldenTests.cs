using System.Security.Cryptography;
using System.Text;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins <see cref="ChestOrganizer.OrganizeAsync"/> (WP03 refactor) on fake chests that keep their contents between opens:
/// the exact open/click/close sequence, the result text and every chest's final contents, for one chest, three mixed chests
/// with a nearly full inventory (several passes), a full target chest, and an open that fails midway.
/// </summary>
public class ChestOrganizerGoldenTests
{
    private sealed class Chests
    {
        public readonly Dictionary<BlockPos, ItemStack[]> Contents = new();
        public void Fill(BlockPos p, params (int Slot, int Id, int Count, int Damage)[] items)
        {
            var a = Enumerable.Repeat(ItemStack.Empty, 27).ToArray();
            foreach (var (slot, id, n, d) in items) a[slot] = new ItemStack((short)id, (byte)n, (short)d);
            Contents[p] = a;
        }
    }

    private static FakeGameClient Client(Chests chests, int playerFill)
    {
        var c = new FakeGameClient();
        BlockPos last = default;
        c.OnOpen = p =>
        {
            c.World.Inventory.Open(1, WindowType.Chest, "Chest", 27);
            var a = chests.Contents[p];
            for (int i = 0; i < 27; i++) if (!a[i].IsEmpty) c.World.Inventory.SetSlot(1, i, a[i]);
            last = p;
        };
        c.OnClose = w =>
        {
            var a = new ItemStack[27];
            for (int i = 0; i < 27; i++) a[i] = w.Get(i);
            chests.Contents[last] = a;
        };
        // The player's main inventory: the first playerFill slots hold cobblestone (never moved: not taken from a chest).
        for (int i = 0; i < playerFill; i++) c.World.Inventory.SetSlot(0, 9 + i, new ItemStack((short)Blocks.Cobblestone, 64, 0));
        return c;
    }

    private static string Dump(FakeGameClient c, Chests chests, ActionResult r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Ok).Append(' ').Append(r.Message).Append('\n');
        sb.Append(string.Join(";", c.CallList)).Append('\n');
        foreach (var (p, a) in chests.Contents.OrderBy(kv => kv.Key.X))
            sb.Append($"{p}: ").Append(string.Join(",", a.Select(s => s.IsEmpty ? "-" : $"{s.Id}:{s.Damage}x{s.Count}"))).Append('\n');
        var inv = c.World.Inventory.PlayerSlots();
        sb.Append("inv: ").Append(string.Join(",", inv.Select(s => s.IsEmpty ? "-" : $"{s.Id}:{s.Damage}x{s.Count}"))).Append('\n');
        sb.Append("cursor: ").Append(c.World.Inventory.Cursor.IsEmpty ? "-" : $"{c.World.Inventory.Cursor.Id}").Append('\n');
        return sb.ToString();
    }

    private static readonly BlockPos A = new(10, 64, 0), B = new(20, 64, 0), C = new(30, 64, 0);

    private static void Mixed(Chests ch)
    {
        ch.Fill(A, (0, Blocks.Dirt, 10, 0), (1, Items.Coal, 5, 0), (2, Blocks.Dirt, 60, 0), (3, Items.StonePickaxe, 1, 3), (4, Blocks.Log, 4, 0),
            (5, Items.IronIngot, 7, 0), (6, Blocks.Sapling, 2, 0), (7, Items.Coal, 40, 0), (9, Items.Wheat, 9, 0), (10, Items.Dye, 3, Items.BoneMeal));
        ch.Fill(B, (0, Blocks.Log, 30, 0), (1, Blocks.Log, 64, 1), (2, Blocks.Planks, 20, 0), (4, Items.Coal, 8, 0), (5, Blocks.Dirt, 33, 0),
            (8, Items.Stick, 12, 0), (11, Items.IronIngot, 2, 0), (12, Blocks.Sapling, 5, 1));
        ch.Fill(C, (0, Items.Coal, 64, 0), (1, Items.IronIngot, 30, 0), (2, Items.GoldIngot, 3, 0), (3, Blocks.Cobblestone, 64, 0), (5, Items.Seeds, 10, 0),
            (6, Blocks.Log, 3, 0), (7, Items.WoodenPickaxe, 1, 0), (20, Blocks.Dirt, 1, 0));
    }

    private static async Task<string> Run(Chests ch, int playerFill, IReadOnlyList<BlockPos> order, Action<FakeGameClient>? setup = null)
    {
        var c = Client(ch, playerFill);
        setup?.Invoke(c);
        var r = await new ChestOrganizer(c).OrganizeAsync(order, CancellationToken.None);
        return Dump(c, ch, r);
    }

    [Fact]
    public async Task Organize_sequence_and_results_are_unchanged()
    {
        var sb = new StringBuilder();
        var one = new Chests();
        one.Fill(A, (0, Blocks.Dirt, 10, 0), (2, Items.Coal, 5, 0), (5, Blocks.Dirt, 60, 0), (7, Items.StonePickaxe, 1, 0), (9, Items.Coal, 3, 0), (12, Blocks.Log, 4, 0));
        sb.Append(await Run(one, 0, [A]));
        foreach (int fill in new[] { 0, 30, 34, 36 })
        {
            var ch = new Chests();
            Mixed(ch);
            sb.Append($"fill {fill}\n").Append(await Run(ch, fill, [A, B, C]));
        }
        // A target chest that's full: the put loop stops at the first slot that takes nothing.
        var full = new Chests();
        Mixed(full);
        var packed = full.Contents[C];
        for (int i = 0; i < 27; i++) if (packed[i].IsEmpty) packed[i] = new ItemStack((short)Items.Coal, 64, 0);
        sb.Append("full\n").Append(await Run(full, 20, [C, A, B]));
        // An open that fails on the n-th call.
        foreach (int failAt in new[] { 1, 2, 4, 7, 10 })
        {
            var ch = new Chests();
            Mixed(ch);
            int opens = 0;
            sb.Append($"fail {failAt}\n").Append(await Run(ch, 10, [A, B, C], c =>
                c.Results["open"] = () => ++opens == failAt ? ActionResult.Fail("can't reach") : ActionResult.Success("open")));
        }
        sb.Append(await Run(new Chests(), 0, []));
        Check(sb.ToString());
    }

    private static void Check(string dump)
    {
        dump = dump.Replace("\r\n", "\n");
        var outDir = Environment.GetEnvironmentVariable("WP03_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "chest-organizer.txt"), dump);
        Assert.Equal(Expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))[..24]);
    }

    /// <summary>Generated from the pre-refactor code (WP03).</summary>
    private const string Expected = "08654B58F80D58FACEF305FE";
}
