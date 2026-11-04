using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Window types: <see cref="Player"/> is the always-open window 0; others match 0x64 Open Window inventory types.</summary>
public enum WindowType
{
    Player = -1,
    Chest = 0,
    Workbench = 1,
    Furnace = 2,
    Dispenser = 3,
}

/// <summary>What a window slot is for (drives click semantics).</summary>
public enum SlotKind
{
    Container,
    /// <summary>Crafting output (player 0, workbench 0): take-only, takes the whole stack, consumes the grid.</summary>
    CraftOutput,
    CraftGrid,
    /// <summary>Furnace output (2): take-only.</summary>
    FurnaceOutput,
    FurnaceInput,
    FurnaceFuel,
    /// <summary>Player armor slots 5–8: limit 1, only the matching armor piece.</summary>
    Armor,
    /// <summary>Player main inventory (27 slots above the hotbar).</summary>
    Main,
    Hotbar,
    /// <summary>1.12.2 player window slot 45: the off hand (any item, limit 64).</summary>
    Offhand,
}

/// <summary>
/// A window's slot layout. Player-inventory slots (main + hotbar) are shared with window 0, so an update through
/// any window is visible in all. b1.8.1 layouts:
/// <list type="bullet">
/// <item>Player (0): 0 craft output, 1–4 craft grid, 5–8 armor, 9–35 main, 36–44 hotbar; 1.12.2 adds 45, the off hand.</item>
/// <item>Chest (type 0): 0..N-1 chest (N = 27 or 54 for a large chest), then 27 main, then 9 hotbar.</item>
/// <item>Workbench (1): 0 output, 1–9 grid, 10–36 main, 37–45 hotbar.</item>
/// <item>Furnace (2): 0 input, 1 fuel, 2 output, 3–29 main, 30–38 hotbar.</item>
/// <item>Dispenser (3): 0–8 dispenser, 9–35 main, 36–44 hotbar.</item>
/// </list>
/// </summary>
public sealed class Window
{
    internal readonly ItemStack[] Container;
    private readonly ItemStack[] _player; // window-0 array: 45 slots (b1.8.1) or 46 (1.12.2: + off hand)
    // 1.12.2's enchanting table sends ten (three offer levels, the seed, three hints and their levels).
    private readonly short[] _properties = new short[16];

    public int Id { get; }
    public WindowType Type { get; }
    public string Title { get; }
    /// <summary>1.12.2: the window's own kind (brewing_stand, enchanting_table, anvil, hopper, beacon...) when the canonical
    /// <see cref="Type"/> folds it into a chest-like window; null otherwise (b1.8.1, chests, the four canonical types).</summary>
    public string? Kind { get; init; }
    /// <summary>Number of non-player slots (the 0x64 slot count; for window 0 this is 9: output, grid, armor).</summary>
    public int ContainerSlots { get; }
    public int SlotCount => Type == WindowType.Player ? _player.Length : ContainerSlots + 36;
    /// <summary>Window slot index where the player's main inventory (27) starts; hotbar follows at +27.</summary>
    public int MainStart => Type == WindowType.Player ? 9 : ContainerSlots;
    public int HotbarStart => MainStart + 27;

    internal Window(int id, WindowType type, string title, int containerSlots, ItemStack[] playerSlots)
    {
        Id = id;
        Type = type;
        Title = title;
        _player = playerSlots;
        if (type == WindowType.Player)
        {
            ContainerSlots = 9;
            Container = playerSlots;
        }
        else
        {
            // b1.8.1 EntityPlayer.b(x,y,z) opens the crafting table with Packet100OpenWindow(id, 1, "Crafting", 9): the 9 counts
            // the grid only, but ContainerWorkbench has 10 own slots (output + 3x3). Taking the 9 shifted the whole player part
            // by one (grid cell 9 seen as the first inventory slot, the last hotbar slot invisible): a leftover stashed into
            // "slot 9" landed in the grid and broke the recipe (live 10-27 22:50, bread rejected 3x per craft).
            ContainerSlots = type == WindowType.Workbench ? DefaultSize(type)
                : containerSlots > 0 ? containerSlots : DefaultSize(type);
            Container = new ItemStack[ContainerSlots];
            Array.Fill(Container, ItemStack.Empty);
        }
    }

    public static int DefaultSize(WindowType t) => t switch
    {
        WindowType.Chest => 27,
        WindowType.Workbench => 10,
        WindowType.Furnace => 3,
        WindowType.Dispenser => 9,
        _ => 9,
    };

    public ItemStack this[int slot]
    {
        get => Get(slot);
        set => Set(slot, value);
    }

    public ItemStack Get(int slot)
    {
        if (slot < 0 || slot >= SlotCount) return ItemStack.Empty;
        if (Type == WindowType.Player) return _player[slot];
        return slot < ContainerSlots ? Container[slot] : _player[9 + slot - ContainerSlots];
    }

    public void Set(int slot, ItemStack stack)
    {
        if (slot < 0 || slot >= SlotCount) return;
        if (stack.IsEmpty) stack = ItemStack.Empty;
        if (Type == WindowType.Player) _player[slot] = stack;
        else if (slot < ContainerSlots) Container[slot] = stack;
        else _player[9 + slot - ContainerSlots] = stack;
    }

    /// <summary>Maps a window slot to the corresponding window-0 slot, or -1 for container slots.</summary>
    public int ToPlayerSlot(int slot)
    {
        if (Type == WindowType.Player) return slot;
        return slot >= ContainerSlots && slot < SlotCount ? 9 + slot - ContainerSlots : -1;
    }

    /// <summary>Maps a window-0 slot (9–44) to this window's slot index, or -1.</summary>
    public int FromPlayerSlot(int playerSlot)
    {
        if (Type == WindowType.Player) return playerSlot;
        return playerSlot is >= 9 and <= 44 ? ContainerSlots + playerSlot - 9 : -1;
    }

    public SlotKind KindOf(int slot)
    {
        if (Type == WindowType.Player && slot == InventoryModel.OffhandSlot) return SlotKind.Offhand;
        if (slot >= MainStart) return slot >= HotbarStart ? SlotKind.Hotbar : SlotKind.Main;
        return Type switch
        {
            WindowType.Player => slot == 0 ? SlotKind.CraftOutput : slot <= 4 ? SlotKind.CraftGrid : SlotKind.Armor,
            WindowType.Workbench => slot == 0 ? SlotKind.CraftOutput : SlotKind.CraftGrid,
            WindowType.Furnace => slot switch { 0 => SlotKind.FurnaceInput, 1 => SlotKind.FurnaceFuel, _ => SlotKind.FurnaceOutput },
            _ => SlotKind.Container,
        };
    }

    /// <summary>Container slots only (chest/dispenser contents, furnace slots...).</summary>
    public IEnumerable<(int Slot, ItemStack Item)> ContainerContents()
    {
        int end = Type == WindowType.Player ? 9 : ContainerSlots;
        for (int i = 0; i < end; i++) yield return (i, Get(i));
    }

    // ---- 0x69 window properties ----

    public short GetProperty(int index) => index >= 0 && index < _properties.Length ? _properties[index] : (short)0;

    public void SetProperty(int index, short value)
    {
        if (index >= 0 && index < _properties.Length) _properties[index] = value;
    }

    /// <summary>Furnace: smelt progress 0..1 (property 0 = cook time out of 200 ticks).</summary>
    public double SmeltProgress => Type == WindowType.Furnace ? Math.Clamp(GetProperty(0) / 200.0, 0, 1) : 0;
    /// <summary>Furnace: remaining fuel burn ticks (property 1).</summary>
    public int BurnTimeLeft => Type == WindowType.Furnace ? GetProperty(1) : 0;
    /// <summary>Furnace: fuel remaining as fraction of the current fuel item's burn time (1 / 2).</summary>
    public double FuelFraction => Type == WindowType.Furnace && GetProperty(2) > 0
        ? Math.Clamp(GetProperty(1) / (double)GetProperty(2), 0, 1) : 0;
    public bool IsBurning => BurnTimeLeft > 0;
}
