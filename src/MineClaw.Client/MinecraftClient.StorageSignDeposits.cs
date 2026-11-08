using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

public sealed partial class MinecraftClient
{
    /// <summary>Lock-free lookup of published physical sign rules, including ordinary label fingerprints. Null disables the guard.</summary>
    public Func<int, BlockPos, StorageSignPolicy?>? StorageSignRules { get; set; }

    private int _storageDragWindow = -1;
    private bool _storageDragIntoChest;

    private static readonly BlockPos[] LabelOffsets = [new(0, 1, 0), new(-1, 0, 0), new(1, 0, 0), new(0, 0, -1), new(0, 0, 1)];

    /// <summary>Every path that sends inventory clicks passes here, including skills that don't use DepositAll/Transfer.</summary>
    private void GuardStorageSignClick(int slot, ClickMode mode, int button)
    {
        var w = World.Inventory.ActiveWindow;
        if (mode != ClickMode.QuickCraft || _storageDragWindow != w.Id)
        {
            _storageDragWindow = w.Id;
            _storageDragIntoChest = false;
        }
        if (w.Type != WindowType.Chest || StorageSignRules == null) return;
        bool containerSlot = slot >= 0 && slot < w.ContainerSlots;
        var incoming = ItemStack.Empty;
        switch (mode)
        {
            case ClickMode.QuickMove when slot >= w.MainStart && slot < w.SlotCount:
                incoming = w.Get(slot);
                break;
            case ClickMode.Pickup when containerSlot:
                incoming = World.Inventory.Cursor;
                break;
            case ClickMode.Swap when containerSlot && button is >= 0 and < 9:
                incoming = w.Get(w.HotbarStart + button);
                break;
            case ClickMode.QuickCraft:
                int stage = button & 3;
                if (stage == 0) _storageDragIntoChest = false;
                if (stage == 1 && containerSlot || stage == 2 && _storageDragIntoChest)
                    incoming = World.Inventory.Cursor;
                if (!incoming.IsEmpty && !StorageSignAccepts(incoming))
                    throw new ActionAbortedException("storage sign rejects this deposit (label changed or item is not allowed)");
                if (stage == 1 && containerSlot) _storageDragIntoChest = true;
                if (stage == 2) _storageDragIntoChest = false;
                return;
        }
        if (!incoming.IsEmpty && !StorageSignAccepts(incoming))
            throw new ActionAbortedException("storage sign rejects this deposit (label changed or item is not allowed)");
    }

    private bool StorageSignAccepts(ItemStack stack)
    {
        if (StorageSignRules is not { } lookup || _lastContainerPos is not { } at
            || World.Inventory.OpenWindow?.Type != WindowType.Chest) return true;
        if (World.IsLoaded(at.X, at.Z) && !ChestPairs.IsPairable(World.GetBlock(at))) return true;
        var policy = lookup(World.Dimension, at);
        var cells = ChestPairs.Cells(World, at).ToHashSet();
        if (policy != null) cells.UnionWith(policy.Chests);
        // A new sign/half can arrive before the next director tick. Never use an older unrestricted snapshot for it.
        foreach (var cell in cells)
            foreach (var offset in LabelOffsets)
            {
                var sign = cell + offset;
                if (!World.IsLoaded(sign.X, sign.Z) || AttachedStorageChest(sign) != cell) continue;
                if (policy == null || !policy.Signs.TryGetValue(sign, out var expected)) return false;
                if (World.BlockEntities.Get(sign)?.Lines is { } lines && !lines.SequenceEqual(expected)) return false;
            }
        if (policy == null) return true;
        foreach (var (sign, expected) in policy.Signs)
        {
            if (!World.IsLoaded(sign.X, sign.Z)) continue; // persisted signs still constrain an unseen half
            if (AttachedStorageChest(sign) is not { } chest || !policy.Chests.Contains(chest)) return false;
            if (World.BlockEntities.Get(sign)?.Lines is { } lines && !lines.SequenceEqual(expected)) return false;
        }
        return policy.Accepts(stack.Id, stack.Damage);
    }

    private BlockPos? AttachedStorageChest(BlockPos sign) => World.GetBlock(sign) switch
    {
        Blocks.SignPost => sign.Down(),
        Blocks.WallSign => World.GetMeta(sign) switch
        {
            2 => sign.Offset(0, 0, 1), 3 => sign.Offset(0, 0, -1),
            4 => sign.Offset(1, 0, 0), 5 => sign.Offset(-1, 0, 0), _ => null,
        },
        _ => null,
    };
}
