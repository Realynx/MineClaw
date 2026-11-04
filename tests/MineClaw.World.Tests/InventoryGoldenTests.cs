using System.Text;
using MineClaw.Data;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// Click prediction golden (complexity refactor WP04), taken before <c>SlotClick</c> / <c>SlotClick1122</c> and the transfers were
/// reshaped: seeded click sequences of every mode and button (the 1.12.2 swap, throw, pickup-all and three-stage drags; b1.8.1 left,
/// right and shift) over the player window, a chest, a workbench, a furnace and a dispenser filled with stackables, armor, a shield,
/// tools, smeltables, fuel and tagged items; after each click the predicted item, the changed slots, the cursor and every slot.
/// </summary>
public class InventoryGoldenTests
{
    private static readonly ItemStack[] Palette =
    [
        new(1, 64, 0), new(4, 10, 0), new(4, 64, 0), new(17, 5, 2), new(263, 3, 0), new(15, 7, 0), new(14, 1, 0), new(265, 12, 0),
        new(298, 1, 0), new(299, 1, 3), new(300, 1, 0), new(301, 1, 0), new(310, 1, 0), new(442, 1, 0), new(276, 1, 12), new(257, 1, 0),
        new(344, 16, 0), new(332, 16, 0), new(50, 63, 0), new(58, 1, 0), new(280, 33, 0), new(5, 2, 0), new(5, 2, 1), new(326, 1, 0),
        new(2256, 1, 0), new(373, 1, 8197), new(4, 1, 0) { Tag = null },
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Clicks_match_the_golden(bool modern) => Check($"inventory-{(modern ? "1122" : "b181")}", modern ? "0B457FBD52C5925DF631E6F74E9B202845215F9456D177BC9FF91F0CF248B9D9" : "661E278F4852C469A28CAF62B01A00827BCAF0C6F7F2376987412737C9DFB46F", sb =>
    {
        using var _ = GoldenWorlds.Data(modern);
        for (int seed = 1; seed <= 6; seed++)
        {
            var rng = new Random(seed * 101 + (modern ? 7 : 0));
            var inv = new InventoryManager(model: modern ? InventoryModel.Release1122 : InventoryModel.Beta181)
            {
                CraftResult = (grid, width) => Recipes.Match(grid, width)?.Output ?? ItemStack.Empty,
            };
            var window = OpenFor(inv, seed);
            Fill(inv, window, rng);
            sb.Append("== ").Append(seed).Append(' ').Append(window.Type).Append(' ').Append(window.SlotCount).Append('\n');
            for (int step = 0; step < 300; step++) Click(sb, inv, window, rng, modern);
        }
    });

    private static Window OpenFor(InventoryManager inv, int seed) => (seed % 5) switch
    {
        0 => inv.PlayerWindow,
        1 => inv.Open(1, WindowType.Chest, "Chest", 27),
        2 => inv.Open(2, WindowType.Workbench, "Crafting", 10),
        3 => inv.Open(3, WindowType.Furnace, "Furnace", 3),
        _ => inv.Open(4, WindowType.Dispenser, "Trap", 9),
    };

    private static void Fill(InventoryManager inv, Window window, Random rng)
    {
        var player = new ItemStack[inv.PlayerWindow.SlotCount];
        for (int i = 0; i < player.Length; i++) player[i] = rng.Next(3) == 0 ? ItemStack.Empty : Pick(rng);
        inv.SetWindowItems(0, player);
        if (window.Type == WindowType.Player) return;
        var items = new ItemStack[window.SlotCount];
        for (int i = 0; i < items.Length; i++) items[i] = rng.Next(2) == 0 ? ItemStack.Empty : Pick(rng);
        inv.SetWindowItems(window.Id, items);
    }

    private static ItemStack Pick(Random rng)
    {
        var s = Palette[rng.Next(Palette.Length)];
        if (rng.Next(9) == 0) s = s with { Tag = new ItemTag([10, 0, 0, 1, 0, 1, (byte)'a', (byte)rng.Next(3), 0]) };
        return s with { Count = (byte)Math.Max(1, Math.Min(s.Count, rng.Next(1, 65))) };
    }

    private static void Click(StringBuilder sb, InventoryManager inv, Window window, Random rng, bool modern)
    {
        int slot = rng.Next(12) switch
        {
            0 => InventoryManager.OutsideSlot,
            1 => window.SlotCount + rng.Next(3),
            2 => -1 - rng.Next(2),
            _ => rng.Next(window.SlotCount),
        };
        var mode = modern ? (ClickMode)rng.Next(7) : (ClickMode)rng.Next(2);
        int button = mode switch
        {
            ClickMode.Swap => rng.Next(-1, 10),
            ClickMode.QuickCraft => rng.Next(16),
            _ => rng.Next(3),
        };
        sb.Append(slot).Append('/').Append(mode).Append('/').Append(button).Append(" -> ");
        try
        {
            var r = inv.ApplyClick(slot, mode, button);
            sb.Append(r.ExpectedItem).Append(Tag(r.ExpectedItem)).Append(" changed=").Append(string.Join(",", r.ChangedSlots))
                .Append(" cursor=").Append(r.CursorChanged).Append(' ').Append(r.RightClick).Append(r.Shift).Append(r.ActionNumber);
        }
        catch (Exception ex) { sb.Append("throws ").Append(ex.GetType().Name); }
        sb.Append("\n c=").Append(inv.Cursor).Append(Tag(inv.Cursor)).Append(" w=");
        for (int i = 0; i < window.SlotCount; i++) sb.Append(window.Get(i)).Append(Tag(window.Get(i))).Append(';');
        sb.Append(" p=");
        foreach (var s in inv.PlayerSlots()) sb.Append(s).Append(Tag(s)).Append(';');
        sb.Append('\n');
    }

    private static string Tag(ItemStack s) => s.Tag is { } t ? "#" + Convert.ToHexString(t.Bytes) : "";
}
