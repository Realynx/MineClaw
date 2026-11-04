using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

/// <summary>
/// One body's eye on the containers it opens, for the web viewer's container panel (<c>/api/world/containers</c>): when a container
/// window opens for a block the body right-clicked (<see cref="MinecraftClient.ContainerOpened"/>), its contents go to
/// <see cref="ContainerSightings"/> as soon as they arrive, again on slot changes (deposits, withdrawals; at most every
/// <see cref="Throttle"/>), and once more when the window closes. While the window is open the store can ask for a live copy.
/// Copies are taken under the inventory's own short lock, never on the game tick. Workbenches, enchanting tables, anvils and the
/// like are not containers and are ignored.
/// </summary>
public sealed class ContainerWatch
{
    private readonly string _name;
    private readonly WorldState _world;
    private readonly ContainerSightings _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private Window? _window;
    private Place? _at;
    private long _lastSnap;
    // a window that closed before the client said which block it was (the ContainerOpened event is raised on the event pump)
    private (int WindowId, ItemStack[] Slots)? _closedEarly;

    private sealed record Place(int Dim, BlockPos Pos, BlockPos[] Cells, int BlockId);

    private readonly Func<bool> _online;

    /// <param name="online">The body is in game (a live copy is only taken while it is); null = always.</param>
    public ContainerWatch(string name, WorldState world, ContainerSightings? store = null, TimeProvider? time = null, Func<bool>? online = null)
    {
        _online = online ?? (() => true);
        _name = name;
        _world = world;
        _store = store ?? ContainerSightings.Process;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Slot-change snapshots are at most this often (the close always takes one).</summary>
    public TimeSpan Throttle { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Watches a body's client (main, a hive, a subhive body, a Story pawn); called next to <see cref="BodyInventories.Attach"/>.</summary>
    public static ContainerWatch Attach(string name, MinecraftClient client, ContainerSightings? store = null)
    {
        var weak = new WeakReference<MinecraftClient>(client);
        var w = new ContainerWatch(name, client.World, store, online: () => weak.TryGetTarget(out var c) && c.State == ClientState.Playing);
        var inv = client.World.Inventory;
        inv.WindowOpened += w.Opened;
        inv.WindowClosed += w.Closed;
        inv.Changed += w.Changed;
        client.ContainerOpened += (wid, _, x, y, z) => w.Located(wid, new BlockPos(x, y, z));
        return w;
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>A window opened (0x64): which block it is comes with <see cref="Located"/>.</summary>
    public void Opened(Window win)
    {
        Window? prev;
        Place? at;
        lock (_lock)
        {
            prev = _window;
            at = _at;
            _window = win.Type is WindowType.Player or WindowType.Workbench ? null : win;
            _at = null;
            _closedEarly = null;
        }
        // a new window replaced the open one without a close: the old one's last contents stay
        if (prev != null && at != null && !ReferenceEquals(prev, win))
            _store.Record(new ContainerSighting(at.Dim, at.Pos, at.Cells, at.BlockId, Copy(prev), Now, _name));
    }

    /// <summary>The client tied window <paramref name="windowId"/> to the block it right-clicked.</summary>
    public void Located(int windowId, BlockPos pos)
    {
        Window? win;
        lock (_lock)
        {
            if (_closedEarly is { } early && early.WindowId == windowId && (_window == null || _window.Id != windowId))
            {
                _closedEarly = null;
                if (Resolve(pos, early.Slots.Length) is { } p0) _store.Record(new ContainerSighting(p0.Dim, p0.Pos, p0.Cells, p0.BlockId, early.Slots, Now, _name));
                return;
            }
            win = _window;
            if (win == null || win.Id != windowId) return;
            _at = Resolve(pos, win.ContainerSlots);
            if (_at == null) { _window = null; return; }
        }
        Snap(win, force: true);
    }

    /// <summary>Any slot changed (0x67, 0x68, a predicted click): a snapshot when a located container is open.</summary>
    public void Changed()
    {
        Window? win;
        lock (_lock)
        {
            win = _window;
            if (win == null || _at == null) return;
        }
        Snap(win, force: false);
    }

    /// <summary>The window closed (0x65 or our own close): its last contents stay, no longer live.</summary>
    public void Closed(Window win)
    {
        Place? at;
        ItemStack[] slots;
        lock (_lock)
        {
            if (!ReferenceEquals(win, _window)) return;
            at = _at;
            _window = null;
            _at = null;
            slots = Copy(win);
            if (at == null) _closedEarly = (win.Id, slots);
        }
        if (at != null) _store.Record(new ContainerSighting(at.Dim, at.Pos, at.Cells, at.BlockId, slots, Now, _name));
    }

    private void Snap(Window win, bool force)
    {
        Place? at;
        lock (_lock)
        {
            if (!ReferenceEquals(win, _window) || _at == null) return;
            long now = Now;
            if (!force && now - _lastSnap < Throttle.TotalMilliseconds) return;
            _lastSnap = now;
            at = _at;
        }
        var inv = _world.Inventory;
        _store.Record(new ContainerSighting(at.Dim, at.Pos, at.Cells, at.BlockId, Copy(win), Now, _name),
            () => _online() && ReferenceEquals(inv.OpenWindow, win) ? Copy(win) : null);
    }

    private ItemStack[] Copy(Window win)
    {
        lock (_world.Inventory.SyncRoot)
        {
            var a = new ItemStack[win.ContainerSlots];
            for (int i = 0; i < a.Length; i++) a[i] = win.Get(i);
            return a;
        }
    }

    /// <summary>The container at the clicked block: its kind from the block, a double chest's canonical (west/north) cell and both
    /// halves when the window has a double chest's 54 slots. Null = the block is not a container with contents to show.</summary>
    private Place? Resolve(BlockPos pos, int slots)
    {
        int id = _world.GetBlock(pos);
        if (ContainerSightings.KindOf(id, slots) == null) return null;
        var cells = ChestPairs.IsPairable(id) && slots > ChestPairs.HalfSlots ? ChestPairs.Cells(_world, pos) : [pos];
        if (slots > ChestPairs.HalfSlots && cells.Length < 2) cells = [pos]; // the other half's column is away: keyed by this one
        return new Place(_world.Dimension, cells[0], cells, id);
    }
}
