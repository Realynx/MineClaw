using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>A slot's durability (damageable items only): <c>left</c> = <c>max</c> − damage.</summary>
public sealed record InventoryDurability(int Max, int Left);

/// <summary>
/// One non-empty inventory slot as the viewer reads it. <paramref name="Slot"/> is vanilla's <c>InventoryPlayer</c> index, the same in
/// b1.8.1 and 1.12.2: 0–8 hotbar, 9–35 main, 36 feet, 37 legs, 38 chest, 39 head, 40 off hand (1.12.2).
/// </summary>
/// <param name="Name">Snake-case item name keeping the variant ("red_wool", "diamond_pickaxe").</param>
/// <param name="Display">What the game calls it (a renamed item's own name).</param>
/// <param name="Enchanted">Its NBT makes vanilla draw the enchantment glint (enchantments, a potion with effects); b1.8.1 never.</param>
public sealed record InventorySlot(int Slot, int Id, int Meta, int Count, string Name, string Display, InventoryDurability? Durability, bool Enchanted);

/// <summary>
/// A body's inventory (<c>GET /api/viewer/bodies/{name}/inventory</c>). <paramref name="Slots"/> lists the non-empty main slots in
/// slot order (hotbar 0–8 first, then main 9–35); <paramref name="Armor"/> is always [head, chest, legs, feet] (null = empty);
/// <paramref name="Offhand"/> is null on b1.8.1. <paramref name="At"/> = unix ms of the snapshot; <paramref name="Online"/> false =
/// the body is not in game and this is the last snapshot taken while it was.
/// </summary>
public sealed record BodyInventory(string Name, string? Version, int HeldSlot, IReadOnlyList<InventorySlot> Slots, IReadOnlyList<InventorySlot?> Armor,
    InventorySlot? Offhand, long At, bool Online);

/// <summary>
/// Every in-process body's inventory for the web viewer: main, in-process hives, every subhive body and Story pawn registers its
/// client here when it is built (<see cref="Attach"/>, next to <see cref="BlockDeltaLog.Attach"/>). Entries hold the world weakly
/// (a released body's chunks are never kept alive), remember the last snapshot taken while online (and one on disconnect), and a
/// read is one copy of the player window under the inventory's own short lock: nothing runs on, or waits for, the game tick.
/// </summary>
public sealed class BodyInventories
{
    /// <summary>This process's registry (in a clone process it fills but nobody reads it).</summary>
    public static BodyInventories Process { get; } = new();

    private sealed class Entry(WeakReference<WorldState> world, Func<bool> online)
    {
        public readonly WeakReference<WorldState> World = world;
        public readonly Func<bool> Online = online;
        public BodyInventory? Last;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeProvider _time;

    public BodyInventories(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Several viewers asking within this window share one snapshot.</summary>
    public TimeSpan ShareWindow { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The game version the snapshots name (the process's one version; tests override it).</summary>
    public Func<string?> Version { get; init; } = () => MineClaw.Data.Versioning.GameData.Current.VersionId;

    /// <summary>A body's client (main, a hive, a subhive body, a pawn): online while it plays; a snapshot is kept when it disconnects.</summary>
    public static void Attach(string name, MinecraftClient client)
    {
        var weak = new WeakReference<MinecraftClient>(client);
        Process.Register(name, client.World, () => weak.TryGetTarget(out var c) && c.State == ClientState.Playing);
        client.Disconnected += _ => Process.Remember(name);
    }

    /// <summary>Registers (or replaces: a new client for the same account) a body's world.</summary>
    public void Register(string name, WorldState world, Func<bool> online)
    {
        lock (_lock)
        {
            var last = _entries.TryGetValue(name, out var old) ? old.Last : null;
            _entries[name] = new Entry(new WeakReference<WorldState>(world), online) { Last = last };
        }
    }

    public bool Knows(string name) { lock (_lock) return _entries.ContainsKey(name); }

    /// <summary>Takes a snapshot now (the body is about to go: its disconnect).</summary>
    public void Remember(string name)
    {
        Entry? e;
        lock (_lock) _entries.TryGetValue(name, out e);
        if (e == null || !e.World.TryGetTarget(out var w)) return;
        var snap = Snapshot(name, w, online: false);
        if (snap.Slots.Count > 0 || snap.Armor.Any(a => a != null) || snap.Offhand != null || e.Last == null)
            lock (_lock) e.Last = snap;
    }

    /// <summary>The body's inventory: live when it plays, else its last known one; null = no such body here or nothing known.</summary>
    public BodyInventory? Get(string name)
    {
        Entry? e;
        lock (_lock) _entries.TryGetValue(name, out e);
        if (e == null) return null;
        bool online;
        try { online = e.Online(); } catch (Exception) { online = false; }
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        if (online && e.World.TryGetTarget(out var w))
        {
            var last = e.Last;
            if (last is { Online: true } && now - last.At < ShareWindow.TotalMilliseconds) return last;
            var snap = Snapshot(name, w, online: true);
            lock (_lock) e.Last = snap;
            return snap;
        }
        if (e.Last is { } known) return known with { Online = false };
        // Never seen online by a viewer and no disconnect snapshot: what the world still holds, if it is still around.
        return e.World.TryGetTarget(out var gone) ? Snapshot(name, gone, online: false) : null;
    }

    /// <summary>One copy of the player window (b1.8.1: 45 slots; 1.12.2: 46 with the off hand at 45) mapped to vanilla's
    /// <c>InventoryPlayer</c> indices: window 36–44 → 0–8, 9–35 → 9–35, 5..8 (head..feet) → 39..36, 45 → 40.</summary>
    public BodyInventory Snapshot(string name, WorldState w, bool online)
    {
        var window = w.Inventory.PlayerSlots();
        int held = Math.Clamp(w.Player.HeldSlot, 0, 8);
        InventorySlot? At(int windowSlot, int index) => windowSlot < window.Length ? Slot(index, window[windowSlot]) : null;
        var slots = new List<InventorySlot>(36);
        for (int i = 0; i < 9; i++)
            if (At(36 + i, i) is { } s) slots.Add(s);
        for (int i = 9; i < 36; i++)
            if (At(i, i) is { } s) slots.Add(s);
        var armor = new InventorySlot?[] { At(5, 39), At(6, 38), At(7, 37), At(8, 36) };
        var offhand = w.Inventory.Model.HasOffhand ? At(InventoryModel.OffhandSlot, 40) : null;
        return new BodyInventory(name, Version(), held, slots, armor, offhand, _time.GetUtcNow().ToUnixTimeMilliseconds(), online);
    }

    /// <summary>A slot's viewer form (null = empty). Names come from the process's version tables; unknown ids still show.</summary>
    public static InventorySlot? Slot(int index, ItemStack st)
    {
        if (st.IsEmpty) return null;
        int id = st.Id, meta = st.Damage;
        ItemInfo? info = null;
        try { info = Items.Get(id); } catch (Exception) { }
        bool damageable = info is { MaxDamage: > 0 };
        string name, display;
        try { name = damageable ? info!.Name : Items.VariantName(id, meta); }
        catch (Exception) { name = info?.Name ?? $"unknown_{id}"; }
        try { display = damageable ? info!.DisplayName : Items.DisplayName(id, meta); }
        catch (Exception) { display = info?.DisplayName ?? $"Unknown ({id})"; }
        if (st.Tag?.DisplayName is { Length: > 0 } custom) display = custom;
        var durability = damageable ? new InventoryDurability(info!.MaxDamage, Math.Clamp(info.MaxDamage - meta, 0, info.MaxDamage)) : null;
        return new InventorySlot(index, id, meta, st.Count, name, display, durability, EntityLooks.Glint(st));
    }
}
