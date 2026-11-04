using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using MineClaw.World;

namespace MineClaw.Sim;

/// <summary>
/// 1.12.2 brewing stands and enchanting tables, minimal (Story Mode's station bills): a stand window (bottles 0–2, ingredient 3, blaze
/// powder 4; properties 0 brew time, 1 fuel) that brews the game's mixes in <see cref="BrewMs"/>, and a table window (item 0, lapis 1)
/// that shows <see cref="EnchantOffers"/> once an unenchanted item is in and takes one with Enchant Item (the levels and lapis paid).
/// </summary>
public partial class FakeServer
{
    /// <summary>Brewing stands by position: their five slots.</summary>
    public Dictionary<(int, int, int), ItemStack[]> BrewingStands { get; } = new();
    public HashSet<(int, int, int)> EnchantingTables { get; } = new();
    /// <summary>The player's XP level (sent with <see cref="SendXp"/>; the table takes its offers' costs off it).</summary>
    public int XpLevel { get; set; }
    /// <summary>The table's three offers for any item: (level needed, hint enchantment id, hint level); the hint is what it gives.</summary>
    public (int Level, int ClueId, int ClueLevel)[] EnchantOffers { get; set; } = [(3, 34, 1), (9, 32, 2), (18, 35, 2)];
    /// <summary>A brew's time here (the game's 400 ticks, sped up).</summary>
    public int BrewMs { get; set; } = 80;
    public ConcurrentQueue<int> Enchanted { get; } = new();
    public ConcurrentQueue<string> Brewed { get; } = new();

    private readonly Dictionary<(int, int, int), int> _standFuel = new();
    private (int, int, int)? _openStand, _openTable;
    private bool _brewing;

    /// <summary>A 1.12.2 potion item of <paramref name="type"/> ("water", "awkward"...).</summary>
    public static ItemStack Potion(string type, int form = Brewing.Potion) =>
        new((short)form, 1, 0) { Tag = Codec1122.TagOf(new NbtCompound { ["Potion"] = "minecraft:" + type }) };

    public void SendXp() => Send(new ExperienceMessage { Bar = 0, Level = XpLevel, Total = XpLevel * 17 });

    private bool HandleStations(IPacket p)
    {
        var srv = _srv!;
        switch (p)
        {
            case PlayerBlockPlacementPacket place when place.Direction != PlayerBlockPlacementPacket.NoDirection
                                                        && BrewingStands.TryGetValue((place.X, place.Y, place.Z), out var slots):
            {
                SyncIn();
                sbyte id = (sbyte)++_windowId;
                var w = srv.Open(id, WindowType.Chest, "Brewing Stand", 5, "brewing_stand");
                srv.SetWindowItems(id, slots.Concat(Enumerable.Range(9, 36).Select(i => Inventory[i])).ToArray());
                _openStand = (place.X, place.Y, place.Z);
                Send(new OpenWindowPacket { WindowId = id, InventoryType = 0, Title = "Brewing Stand", SlotCount = 5, Kind = "brewing_stand" });
                SendWindow(w);
                Send(new UpdateProgressBarPacket { WindowId = id, ProgressBar = 1, Value = (short)_standFuel.GetValueOrDefault(_openStand.Value) });
                return true;
            }
            case PlayerBlockPlacementPacket place when place.Direction != PlayerBlockPlacementPacket.NoDirection
                                                        && EnchantingTables.Contains((place.X, place.Y, place.Z)):
            {
                SyncIn();
                sbyte id = (sbyte)++_windowId;
                var w = srv.Open(id, WindowType.Chest, "Enchant", 2, "enchanting_table");
                srv.SetWindowItems(id, new[] { ItemStack.Empty, ItemStack.Empty }.Concat(Enumerable.Range(9, 36).Select(i => Inventory[i])).ToArray());
                _openTable = (place.X, place.Y, place.Z);
                Send(new OpenWindowPacket { WindowId = id, InventoryType = 0, Title = "Enchant", SlotCount = 2, Kind = "enchanting_table" });
                SendWindow(w);
                return true;
            }
            case WindowClickPacket click when (_openStand != null || _openTable != null) && srv.OpenWindow is { } sw && sw.Id == click.WindowId:
                HandleModern(p);
                if (_openStand != null) StandTick(); else TableOffers();
                SyncStations();
                return true;
            case EnchantItemPacket ei when _openTable != null && srv.OpenWindow is { } tw && tw.Id == ei.WindowId:
                Enchant(tw, ei.Enchantment);
                return true;
            case CloseWindowPacket when _openStand != null || _openTable != null:
                SyncStations();
                // The table gives back what was left in it (ContainerEnchantment.onContainerClosed).
                if (_openTable != null && srv.OpenWindow is { } cw)
                    for (int i = 0; i < 2; i++)
                        if (!cw.Get(i).IsEmpty) { GiveBack(cw.Get(i)); cw.Set(i, ItemStack.Empty); }
                _openStand = _openTable = null;
                return false; // HandleModern closes the window
        }
        return false;
    }

    private void SendWindow(Window w)
    {
        var items = new ItemStack[w.SlotCount];
        for (int i = 0; i < items.Length; i++) items[i] = w.Get(i);
        Send(new WindowItemsPacket { WindowId = (sbyte)w.Id, Items = items });
    }

    private void GiveBack(ItemStack s)
    {
        int slot = Array.FindIndex(Inventory, 9, 36, x => x.IsEmpty);
        if (slot < 0) return;
        Inventory[slot] = s;
        Send(new SetSlotPacket { WindowId = 0, Slot = (short)slot, Item = s });
    }

    private void SyncStations()
    {
        if (_openStand is { } at && _srv!.OpenWindow is { } w && BrewingStands.TryGetValue(at, out var slots))
            for (int i = 0; i < 5; i++) slots[i] = w.Get(i);
    }

    private static ItemStack Less(ItemStack s, int n) => s.Count <= n ? ItemStack.Empty : new ItemStack(s.Id, (byte)(s.Count - n), s.Damage) { Tag = s.Tag };

    /// <summary>The bottle after a brew with <paramref name="ing"/>, or null when the mix doesn't take it.</summary>
    private static ItemStack? BrewOf(ItemStack bottle, ItemStack ing)
    {
        if (bottle.IsEmpty || ing.IsEmpty || !Brewing.IsPotionItem(bottle.Id)) return null;
        var type = bottle.Tag?.Potion ?? "empty";
        if (ing.Id == Brewing.Gunpowder.Id) return bottle.Id == Brewing.Potion ? Potion(type, Brewing.SplashPotion) : null;
        if (ing.Id == Brewing.DragonBreath.Id) return bottle.Id == Brewing.SplashPotion ? Potion(type, Brewing.LingeringPotion) : null;
        var mix = Brewing.TypeMixes.FirstOrDefault(m => m.From == type && m.With.Id == ing.Id && (m.With.Meta <= 0 || m.With.Meta == ing.Damage));
        return mix.To is null ? null : Potion(mix.To, bottle.Id);
    }

    /// <summary>TileEntityBrewingStand.update, sped up: with an ingredient that takes and fuel (a blaze powder is 20 brews), brew.</summary>
    private void StandTick()
    {
        var srv = _srv!;
        if (_brewing || _openStand is not { } at || srv.OpenWindow is not { } w) return;
        var ing = w.Get(3);
        if (!Enumerable.Range(0, 3).Any(i => BrewOf(w.Get(i), ing) != null)) return;
        int fuel = _standFuel.GetValueOrDefault(at);
        if (fuel <= 0)
        {
            var f = w.Get(4);
            if (f.IsEmpty || f.Id != Brewing.BlazePowder) return;
            fuel = Brewing.BrewsPerPowder;
            w.Set(4, Less(f, 1));
            Send(new SetSlotPacket { WindowId = (sbyte)w.Id, Slot = 4, Item = w.Get(4) });
        }
        _standFuel[at] = fuel;
        _brewing = true;
        int wid = w.Id;
        Send(new UpdateProgressBarPacket { WindowId = (sbyte)wid, ProgressBar = 0, Value = 400 });
        Send(new UpdateProgressBarPacket { WindowId = (sbyte)wid, ProgressBar = 1, Value = (short)fuel });
        _ = Task.Delay(BrewMs).ContinueWith(_ =>
        {
            lock (BlockLock)
            {
                _brewing = false;
                if (srv.OpenWindow is not { } ow || ow.Id != wid) return;
                var ing2 = ow.Get(3);
                for (int i = 0; i < 3; i++)
                    if (BrewOf(ow.Get(i), ing2) is { } r)
                    {
                        ow.Set(i, r);
                        Send(new SetSlotPacket { WindowId = (sbyte)wid, Slot = (short)i, Item = r });
                        Brewed.Enqueue(r.Tag?.Potion ?? "?");
                    }
                ow.Set(3, Less(ing2, 1));
                Send(new SetSlotPacket { WindowId = (sbyte)wid, Slot = 3, Item = ow.Get(3) });
                _standFuel[at] = _standFuel.GetValueOrDefault(at) - 1;
                Send(new UpdateProgressBarPacket { WindowId = (sbyte)wid, ProgressBar = 0, Value = 0 });
                Send(new UpdateProgressBarPacket { WindowId = (sbyte)wid, ProgressBar = 1, Value = (short)_standFuel[at] });
                SyncStations();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>ContainerEnchantment.onCraftMatrixChanged: an unenchanted item shows the offers (0–2 levels, 4–6 hints, 7–9 hint levels).</summary>
    private void TableOffers()
    {
        if (_srv!.OpenWindow is not { } w) return;
        bool ready = !w.Get(0).IsEmpty && (w.Get(0).Tag?.Enchantments.Count ?? 0) == 0;
        for (int i = 0; i < 3; i++)
        {
            var o = EnchantOffers[i];
            Send(new UpdateProgressBarPacket { WindowId = (sbyte)w.Id, ProgressBar = (short)i, Value = (short)(ready ? o.Level : 0) });
            Send(new UpdateProgressBarPacket { WindowId = (sbyte)w.Id, ProgressBar = (short)(4 + i), Value = (short)(ready ? o.ClueId : -1) });
            Send(new UpdateProgressBarPacket { WindowId = (sbyte)w.Id, ProgressBar = (short)(7 + i), Value = (short)(ready ? o.ClueLevel : -1) });
        }
    }

    /// <summary>ContainerEnchantment.enchantItem: the level and lapis checked, the hinted enchantment put on, slot + 1 levels and lapis taken.</summary>
    private void Enchant(Window w, int slot)
    {
        if (slot is < 0 or > 2) return;
        var o = EnchantOffers[slot];
        var item = w.Get(0);
        var lapis = w.Get(1);
        if (item.IsEmpty || o.Level <= 0 || XpLevel < o.Level || lapis.IsEmpty || lapis.Count < slot + 1) return;
        var ench = new NbtList(NbtType.Compound, [new NbtCompound { ["id"] = (short)o.ClueId, ["lvl"] = (short)o.ClueLevel }]);
        var done = new ItemStack(item.Id == Enchanting.Book ? (short)Enchanting.EnchantedBook : item.Id, item.Count, item.Damage)
        {
            Tag = Codec1122.TagOf(item.Id == Enchanting.Book ? new NbtCompound { ["StoredEnchantments"] = ench } : new NbtCompound { ["ench"] = ench }),
        };
        w.Set(0, done);
        w.Set(1, Less(lapis, slot + 1));
        XpLevel -= slot + 1;
        Send(new SetSlotPacket { WindowId = (sbyte)w.Id, Slot = 0, Item = done });
        Send(new SetSlotPacket { WindowId = (sbyte)w.Id, Slot = 1, Item = w.Get(1) });
        SendXp();
        Enchanted.Enqueue(slot);
        TableOffers();
    }
}
