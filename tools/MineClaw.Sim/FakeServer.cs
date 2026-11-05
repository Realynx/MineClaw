using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Release1122;
using MineClaw.Protocol.Versioning;
using MineClaw.World;

namespace MineClaw.Sim;

/// <summary>Blocking in-memory byte pipe; one side writes, the other reads.</summary>
internal sealed class BytePipe
{
    private readonly Queue<byte> _queue = new();
    private bool _closed;

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_queue)
        {
            if (_closed) throw new IOException("pipe closed");
            foreach (var b in data) _queue.Enqueue(b);
            Monitor.PulseAll(_queue);
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (_queue)
        {
            while (_queue.Count == 0 && !_closed)
                if (!Monitor.Wait(_queue, TimeSpan.FromSeconds(30))) throw new TimeoutException("pipe read timed out");
            int n = 0;
            while (n < buffer.Length && _queue.Count > 0) buffer[n++] = _queue.Dequeue();
            return n;
        }
    }

    public void Close()
    {
        lock (_queue)
        {
            _closed = true;
            Monitor.PulseAll(_queue);
        }
    }
}

/// <summary>A full-duplex in-memory stream endpoint.</summary>
internal sealed class DuplexStream(BytePipe readFrom, BytePipe writeTo) : Stream
{
    public static (DuplexStream A, DuplexStream B) CreatePair()
    {
        var ab = new BytePipe();
        var ba = new BytePipe();
        return (new DuplexStream(ba, ab), new DuplexStream(ab, ba));
    }

    public override int Read(byte[] buffer, int offset, int count) => readFrom.Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => readFrom.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => writeTo.Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) => writeTo.Write(buffer);
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        writeTo.Close();
        readFrom.Close();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// Minimal scripted server for offline tests: offline-mode login, a small island chunk, spawn position,
/// window/transaction replies and simple dig/place/open behaviour. Records everything the client sends.
/// <para>b1.8.1 by default (<see cref="PacketConnection"/>). With <c>release1122: true</c> the same canonical game logic runs
/// over the 1.12.2 wire (<see cref="Release1122ServerConnection"/>) and, like the vanilla 1.12.2 server, keeps its own window
/// model: each click is applied by the 1.12.2 <c>Container.slotClick</c> (<see cref="InventoryModel.Release1122"/>) and its
/// expected item compared (NBT included) to accept or reject it; after a rejection clicks are ignored until the client's
/// apology (Confirm Transaction accepted); the crafting output is sent during the click, before its confirm; placements use
/// the server's own hotbar (1.12.2 sends no held item).</para>
/// </summary>
public partial class FakeServer : IDisposable
{
    public const int EntityId = 42;
    private readonly IGameConnection _conn;
    private readonly Thread _thread;
    private readonly DuplexStream? _clientEnd;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => _completion.Task;
    public Action? OnLogin { get; set; }

    public ConcurrentQueue<IPacket> Received { get; } = new();
    /// <summary>Island blocks (x, y, z) → id. Default: a 5×5 stone platform at y=63 around (8, 63, 8), plus grass on top.</summary>
    public Dictionary<(int, int, int), int> Blocks { get; } = new();
    public (double X, double Y, double Z) SpawnAt { get; set; } = (8.5, 64, 8.5);
    private (double X, double Y, double Z)? _pickupPosition;
    /// <summary>The difficulty Join Game announces (default 1, easy).</summary>
    public int Difficulty { get; set; } = 1;
    /// <summary>Player inventory sent in the login 0x68 (45 slots).</summary>
    public ItemStack[] Inventory { get; } = Enumerable.Repeat(ItemStack.Empty, 45).ToArray();
    /// <summary>Action numbers to reject with 0x6A accepted=false.</summary>
    public HashSet<short> RejectActions { get; } = new();
    /// <summary>Chest positions: right-clicking one opens a 27-slot chest window with these contents.</summary>
    public Dictionary<(int, int, int), ItemStack[]> Chests { get; } = new();
    /// <summary>Furnace positions: right-clicking one opens a furnace window (input, fuel, output).</summary>
    public Dictionary<(int, int, int), ItemStack[]> Furnaces { get; } = new();
    /// <summary>Blocks that grow back (block id) this many ms after being dug — a cobblestone generator.</summary>
    public Dictionary<(int, int, int), int> Regenerate { get; } = new();
    /// <summary>Generator refill delay; the short default preserves existing accelerated client fixtures.</summary>
    public int RegenerateDelayMs { get; set; } = 60;
    /// <summary>Item entities lying around: picked up when the player walks within 1.3 blocks.</summary>
    public ConcurrentDictionary<int, (double X, double Y, double Z, ItemStack Item)> Drops { get; } = new();
    /// <summary>Drop an item entity here whenever a block is dug.</summary>
    public bool DropOnDig { get; set; }
    /// <summary>With <see cref="DropOnDig"/>: how many ms after the block change the drop of the dug cell spawns (a lagging server's
    /// entity tracker); null or 0 = right after it.</summary>
    public Func<(int X, int Y, int Z), int>? DropDelayMs { get; set; }
    /// <summary>A player standing still also picks up drops in reach (every on-ground/look tick, like a real server's entity tick).
    /// Off by default: the client fixtures spawn drops beside the player and expect them to stay until they walk.</summary>
    public bool StationaryPickup { get; set; }
    /// <summary>Pickups use mc-dev's vertical box overlap too (EntityHuman.java:308), not just a loose 1.5 height window.</summary>
    public bool StrictPickup { get; set; }
    /// <summary>Block metadata (wood type of logs/leaves...); sent in chunks and block changes.</summary>
    public Dictionary<(int, int, int), int> Metas { get; } = new();
    /// <summary>With <see cref="DropOnDig"/>: what a dug block drops (null = nothing); default = the block itself (stone → cobble).
    /// Drops fall to the first block below (gone when only void is below).</summary>
    public Func<(int X, int Y, int Z), int, int, ItemStack?>? DigDrop { get; set; }
    /// <summary>BlockFalling: sand and gravel placed with air under them drop at once to the first block below (gone over the void).</summary>
    public bool FallingBlocks { get; set; }
    /// <summary>Placing a block takes one from the held stack (SetSlot from the packet's held item, like the real server).</summary>
    public bool ConsumeOnPlace { get; set; }
    private volatile int _heldSlot;
    /// <summary>Blocks dug (status 2) in order.</summary>
    public ConcurrentQueue<(int X, int Y, int Z, int Id)> Dug { get; } = new();
    private int _nextEntity = 1000;
    public bool BreakOnFinish { get; set; } = true;
    public bool PlaceBlocks { get; set; } = true;
    public volatile bool LoggedIn;
    private int _windowId;
    /// <summary>Server-side vitals; eating (0x0F use item with food, then 32 ticks ≈ <see cref="EatDelayMs"/>) raises food.</summary>
    public volatile int Health = 20, Food = 20;
    public int EatDelayMs { get; set; } = 40;
    /// <summary>Food items the server finished eating (id per meal).</summary>
    public ConcurrentQueue<int> Eaten { get; } = new();
    /// <summary>Runs first for every client packet; return true to skip the default handling.</summary>
    public Func<IPacket, bool>? Hook { get; set; }
    /// <summary>Openables (state block) whose right-clicks are swallowed (a protection plugin): the server re-sends the
    /// unchanged block.</summary>
    public HashSet<(int, int, int)> Locked { get; } = new();
    /// <summary>Signs the server refuses to update (not editable).</summary>
    public bool SignsLocked { get; set; }
    /// <summary>Two fake servers standing in for one world (two bodies on one server): they share this lock, and every block a
    /// client places or digs is handed to <see cref="Mirror"/> (the other server's <see cref="SetBlock"/>).</summary>
    public object BlockLock { get; set; } = new();
    public Action<int, int, int, int, int>? Mirror { get; set; }
    /// <summary>Server tick count at which the bow draw started (0x0F use with a bow), or -1.</summary>
    private int _drawStartTicks = -1;
    private int _flyingPackets;
    /// <summary>Draw lengths (server ticks = movement packets) of every released bow shot.</summary>
    public ConcurrentQueue<int> Shots { get; } = new();

    /// <summary>Sets health/food server-side and sends 0x08.</summary>
    public void SetVitals(int health, int food, float saturation = 0)
    {
        Health = health;
        Food = food;
        Send(new UpdateHealthPacket { Health = (short)health, Food = (short)food, FoodSaturation = saturation });
    }

    /// <summary>Scripted zombie fixture: spawn 0x18, optionally hurt the player with 0x26 + 0x08.
    /// No autonomous mob AI; the caller chooses when contact happens.</summary>
    public int SpawnZombie(double x, double y, double z, int damage = 0)
    {
        if (damage < 0 || damage > 20) throw new ArgumentOutOfRangeException(nameof(damage));
        int eid = Interlocked.Increment(ref _nextEntity);
        Send(new MobSpawnPacket { EntityId = eid, Type = 54,
            X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y), Z = FixedPoint.ToAbsolute(z) });
        if (damage > 0)
        {
            Send(new EntityStatusPacket { EntityId = EntityId, Status = 2 });
            SetVitals(Math.Max(0, Health - damage), Food);
        }
        return eid;
    }

    public FakeServer(bool release1122 = false) : this(DuplexStream.CreatePair(), release1122) { }

    private FakeServer((DuplexStream Client, DuplexStream Server) pair, bool release1122) : this(pair.Server, release1122)
    {
        _clientEnd = pair.Client;
    }

    /// <summary>A scripted session over a supplied transport; call Start after configuring the world.</summary>
    public FakeServer(Stream serverStream, bool release1122 = false)
    {
        Modern = release1122;
        _conn = release1122 ? new Release1122ServerConnection(serverStream) : new PacketConnection(serverStream, PacketDirection.ServerBound);
        if (release1122)
        {
            _srv = new InventoryManager(MineClaw.Client.Versioning.GameVersionProfiles.Java1122.ItemRules, InventoryModel.Release1122)
            {
                // The vanilla 1.12.2 server's CraftingManager (P5: the 1.12.2 recipes; birch logs make birch planks).
                CraftResult = (grid, width) => MineClaw.Data.Versioning.Release1122GameData.Instance.MatchRecipe(grid, width)?.Output ?? ItemStack.Empty,
            };
        }
        for (int x = 6; x <= 10; x++)
        for (int z = 6; z <= 10; z++)
            Blocks[(x, 63, z)] = MineClaw.Data.Blocks.Stone;
        _thread = new Thread(Run) { IsBackground = true, Name = "fake-server" };
    }

    public PacketConnection CreateClientConnection(CancellationToken _)
    {
        if (!_thread.IsAlive) Start();
        return new PacketConnection(_clientEnd ?? throw new InvalidOperationException("TCP sessions have no in-memory endpoint"));
    }

    public void Start() => _thread.Start();

    /// <summary>The client end for either version (1.12.2: the translating client connection).</summary>
    public IGameConnection CreateConnection(CancellationToken ct)
    {
        if (!Modern) return CreateClientConnection(ct);
        if (!_thread.IsAlive) Start();
        return new Release1122Connection(_clientEnd ?? throw new InvalidOperationException("TCP sessions have no in-memory endpoint")) { Lang = LangTable.Builtin };
    }

    /// <summary>Speaks 1.12.2 (see the class remarks).</summary>
    public bool Modern { get; }
    /// <summary>1.12.2: the server's window model (window 0 mirrors <see cref="Inventory"/>, plus the off hand).</summary>
    private readonly InventoryManager? _srv;
    private (int, int, int)? _openChest;
    private short? _awaitingApology;
    /// <summary>1.12.2: crafting tables that open a workbench window when right-clicked.</summary>
    public HashSet<(int, int, int)> Workbenches { get; } = new();
    /// <summary>1.12.2: every click the server judged (mode, slot, accepted).</summary>
    public ConcurrentQueue<(byte Mode, short Slot, bool Accepted)> Clicks { get; } = new();
    /// <summary>1.12.2: the server's off-hand slot.</summary>
    public ItemStack Offhand => _srv?.Offhand ?? ItemStack.Empty;
    /// <summary>The server's view of the held hotbar stack (what a dig is judged with).</summary>
    public ItemStack Held => Inventory[36 + _heldSlot];
    /// <summary>1.12.2: the server model's cursor.</summary>
    public ItemStack ServerCursor => _srv?.Cursor ?? ItemStack.Empty;

    public void Send(IPacket p) => _conn.Send(p);

    public List<IPacket> Snapshot() => Received.ToList();

    public IEnumerable<T> Got<T>() where T : IPacket => Received.OfType<T>();

    public bool WaitFor(Func<IPacket, bool> pred, int timeoutMs = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (Received.Any(pred)) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    private void Run()
    {
        try
        {
            while (_conn.ReadPacket() is { } p)
            {
                Received.Enqueue(p);
                lock (BlockLock)
                {
                    MaybeConfirm(p);
                    Handle(p);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or ProtocolException or TimeoutException)
        {
        }
        finally { _completion.TrySetResult(); }
    }

    private void Handle(IPacket p)
    {
        if (HandledBeforeVanilla(p)) return;
        switch (p)
        {
            case UpdateSignPacket sign:
                HandleSignEdit(sign);
                break;
            case PlayerBlockPlacementPacket place:
                HandleUseOrPlace(place);
                break;
            case PlayerDiggingPacket dig:
                HandleDigging(dig);
                break;
            case HandshakePacket:
                Send(new HandshakePacket("-"));
                break;
            case LoginRequestPacket:
                HandleLoginRequest();
                break;
            case WindowClickPacket click:
                HandleWindowClick(click);
                break;
            case HoldingChangePacket hc:
                _heldSlot = hc.Slot;
                break;
            default:
                HandlePickupMovement(p);
                break;
        }
    }

    /// <summary>The trackers, the test hook, the movement checks and the opt-in features, in order; true = the packet is handled.</summary>
    private bool HandledBeforeVanilla(IPacket p)
    {
        CountFlyingPacket(p);
        TrackPlacer(p); // the look and feet a placed block's facing reads (FakeServer.Placement.cs)
        if (Hook?.Invoke(p) == true) return true;
        if (TrampleFarmland) TrackTrample(p); // farmland back to dirt under a careless walker / a hard landing (FakeServer.Movement.cs)
        if (MovementChecks && CheckMovement(p)) return true; // vanilla "moved too quickly" / "moved wrongly" (FakeServer.Movement.cs)
        HandleMobs(p); // P4: the bot's melee on mobs (server-side charge and crit), orb pickups
        if (Modern && p is PlayerBlockPlacementPacket { HeldItem.IsEmpty: true } pl) pl.HeldItem = Inventory[36 + _heldSlot]; // the server's own hotbar
        return HandledByOptIns(p);
    }

    private void CountFlyingPacket(IPacket p)
    {
        if (p is PlayerOnGroundPacket or PlayerPositionPacket or PlayerLookPacket or PlayerPositionLookPacket) Interlocked.Increment(ref _flyingPackets);
    }

    /// <summary>Buckets, fishing (FakeServer.Fishing.cs), brewing stands and enchanting tables (FakeServer.Stations.cs), the 1.12.2
    /// server logic and crafting, each when switched on; true = handled.</summary>
    private bool HandledByOptIns(IPacket p) =>
        Buckets && HandleBucket(p)
        || Fishing && HandleFishing(p)
        || Modern && (HandleStations(p) || HandleModern(p))
        || CraftingEnabled && HandleCrafting(p);

    private void HandleSignEdit(UpdateSignPacket sign)
    {
        if (SignsLocked) return;
        var lines = sign.Lines.Select(l => l.Length > 15 || l.Any(c => c < 0x20 || c > 0x7E) ? "!?" : l).ToArray();
        Send(new UpdateSignPacket { X = sign.X, Y = sign.Y, Z = sign.Z, Lines = lines });
    }

    private void HandleLoginRequest()
    {
        Send(new LoginRequestPacket { EntityId = EntityId, MapSeed = 1234, ServerMode = 0, Dimension = 0, Difficulty = (sbyte)Difficulty, WorldHeight = 128, MaxPlayers = 8 });
        Send(new SpawnPositionPacket { X = 8, Y = 64, Z = 8 });
        Send(new TimeUpdatePacket { Time = 6000 });
        Send(new PlayerListItemPacket { PlayerName = "tester", Online = true, Ping = 1 });
        if (ChunksAfterConfirm) _chunksPending = true;
        else SendSpawnChunks();
        Send(new WindowItemsPacket { WindowId = 0, Items = (ItemStack[])Inventory.Clone() });
        Send(new UpdateHealthPacket { Health = 20, Food = 20, FoodSaturation = 5 });
        Send(new PlayerPositionLookPacket { X = SpawnAt.X, Y = SpawnAt.Y, Stance = SpawnAt.Y + 1.62, Z = SpawnAt.Z, Yaw = 0, Pitch = 0, OnGround = false });
        LoggedIn = true;
        OnLogin?.Invoke();
    }

    private void HandleWindowClick(WindowClickPacket click)
    {
        if (!RejectActions.Contains(click.ActionNumber))
        {
            Send(new TransactionPacket { WindowId = click.WindowId, ActionNumber = click.ActionNumber, Accepted = true });
            return;
        }
        Send(new TransactionPacket { WindowId = click.WindowId, ActionNumber = click.ActionNumber, Accepted = false });
        Send(new WindowItemsPacket { WindowId = click.WindowId, Items = click.WindowId == 0 ? (ItemStack[])Inventory.Clone() : Enumerable.Repeat(ItemStack.Empty, 63).ToArray() });
        Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = ItemStack.Empty });
    }

    /// <summary>Items are picked up where the player's last position packet put it; a stationary player still picks up items every
    /// server tick (opt-in).</summary>
    private void HandlePickupMovement(IPacket p)
    {
        switch (p)
        {
            case PlayerPositionPacket pp:
                _pickupPosition = (pp.X, pp.Y, pp.Z);
                CheckPickups(pp.X, pp.Y, pp.Z);
                break;
            case PlayerPositionLookPacket ppl:
                _pickupPosition = (ppl.X, ppl.Y, ppl.Z);
                CheckPickups(ppl.X, ppl.Y, ppl.Z);
                break;
            case PlayerOnGroundPacket or PlayerLookPacket when StationaryPickup:
                var pos = _pickupPosition ?? SpawnAt;
                CheckPickups(pos.X, pos.Y, pos.Z);
                break;
        }
    }

    // ------------------------------------------------------------------ digging, using and placing

    private void HandleDigging(PlayerDiggingPacket dig)
    {
        if (dig.Status == PlayerDiggingPacket.StatusReleaseUseItem) ReleaseBowDraw();
        // ItemInWorldManager.blockClicked / 1.12.2 PlayerInteractionManager.onBlockClicked: a block that breaks in one
        // tick (hardness 0: tall grass, flowers, torches...) is harvested on the start packet; the client sends no finish.
        else if (BreakOnFinish && (dig.Status == PlayerDiggingPacket.StatusFinished
                                   || dig.Status == PlayerDiggingPacket.StatusStarted && InstantBreak(dig.X, dig.Y, dig.Z)))
            BreakDugBlock(dig.X, dig.Y, dig.Z);
    }

    /// <summary>A bow released after a draw: the shot's draw time (in flying packets) is recorded and one arrow used up.</summary>
    private void ReleaseBowDraw()
    {
        if (_drawStartTicks < 0) return;
        int held = _flyingPackets - _drawStartTicks;
        _drawStartTicks = -1;
        Shots.Enqueue(held);
        int slot = Array.FindIndex(Inventory, st => !st.IsEmpty && st.Id == MineClaw.Data.Items.Arrow);
        if (slot < 0) return;
        var cur = Inventory[slot];
        Inventory[slot] = cur.Count <= 1 ? ItemStack.Empty : new ItemStack(cur.Id, (byte)(cur.Count - 1), cur.Damage);
        Send(new SetSlotPacket { WindowId = 0, Slot = (short)slot, Item = Inventory[slot] });
    }

    private void BreakDugBlock(int x, int y, int z)
    {
        Blocks.TryGetValue((x, y, z), out int was);
        Metas.Remove((x, y, z), out int wasMeta);
        Blocks.Remove((x, y, z));
        if (was != 0) Dug.Enqueue((x, y, z, was));
        Send(new BlockChangePacket { X = x, Y = y, Z = z, BlockId = 0, Metadata = 0 });
        Mirror?.Invoke(x, y, z, 0, 0);
        if (DigDrop is not null) SettleDrops(x, y, z);
        if (DropOnDig && was != 0) DropDugBlockAfter(DropDelayMs?.Invoke((x, y, z)) ?? 0, x, y, z, was, wasMeta);
        if (Regenerate.TryGetValue((x, y, z), out int regenId)) RegenerateLater((x, y, z), regenId);
    }

    private void DropDugBlockAfter(int delayMs, int x, int y, int z, int was, int wasMeta)
    {
        if (delayMs <= 0) DropDugBlock(x, y, z, was, wasMeta);
        else _ = Task.Delay(delayMs).ContinueWith(_ => DropDugBlock(x, y, z, was, wasMeta));
    }

    private void DropDugBlock(int x, int y, int z, int was, int wasMeta)
    {
        // Beta wheat always drops seeds; ripe wheat also drops one wheat.
        // Deterministic ripe seed count (one) keeps the tiny server reproducible.
        if (was == 59 && wasMeta == 7)
            SpawnDrop(x + 0.5, y + 0.1, z + 0.5, new ItemStack(296, 1, 0));
        var drop = DigDrop is { } dd ? dd((x, y, z), was, wasMeta)
            : new ItemStack((short)(was == 59 ? 295 : was == MineClaw.Data.Blocks.Stone ? MineClaw.Data.Blocks.Cobblestone : was), 1, 0);
        if (drop is { IsEmpty: false } d && DigDrop is null) SpawnDrop(x + 0.5, y + 0.1, z + 0.5, d);
        else if (drop is { IsEmpty: false } d2 && GroundBelow(x, y, z) is { } gy) SpawnDrop(x + 0.5, gy + 0.1, z + 0.5, d2);
    }

    private void RegenerateLater((int, int, int) key, int regenId)
    {
        _ = Task.Delay(RegenerateDelayMs).ContinueWith(_ =>
        {
            Blocks[key] = regenId;
            Send(new BlockChangePacket { X = key.Item1, Y = key.Item2, Z = key.Item3, BlockId = (byte)regenId });
        });
    }

    private void HandleUseOrPlace(PlayerBlockPlacementPacket place)
    {
        if (place.Direction == PlayerBlockPlacementPacket.NoDirection)
        {
            if (place.HeldItem.Id == MineClaw.Data.Items.Bow) StartBowDraw();
            else EatHeldLater(place.HeldItem);
        }
        else if (!ToggleOpenable(place.X, place.Y, place.Z)) OpenOrPlace(place);
    }

    private void StartBowDraw()
    {
        if (Array.Exists(Inventory, st => !st.IsEmpty && st.Id == MineClaw.Data.Items.Arrow)) _drawStartTicks = _flyingPackets;
    }

    /// <summary>Eating the held food (when hungry): after <see cref="EatDelayMs"/>, one is used up, the eat status sent and food restored.</summary>
    private void EatHeldLater(ItemStack held)
    {
        if (held.IsEmpty || MineClaw.Data.Items.Food(held.Id) is not { } meal || Food >= 20) return;
        int id = held.Id;
        _ = Task.Delay(EatDelayMs).ContinueWith(_ =>
        {
            int slot = Array.FindIndex(Inventory, 36, 9, st => !st.IsEmpty && st.Id == id);
            if (slot < 0) slot = Array.FindIndex(Inventory, 9, 27, st => !st.IsEmpty && st.Id == id);
            if (slot >= 0)
            {
                var cur = Inventory[slot];
                Inventory[slot] = cur.Count <= 1 ? ItemStack.Empty : new ItemStack(cur.Id, (byte)(cur.Count - 1), cur.Damage);
                Send(new SetSlotPacket { WindowId = 0, Slot = (short)slot, Item = Inventory[slot] });
            }
            Send(new EntityStatusPacket { EntityId = EntityId, Status = 9 });
            Eaten.Enqueue(id);
            SetVitals(Health, Math.Min(20, Food + meal.Hunger), meal.Hunger * meal.Saturation * 2);
        });
    }

    /// <summary>A click on a chest or furnace opens its window; else the held block (or seeds on farmland) is placed.</summary>
    private void OpenOrPlace(PlayerBlockPlacementPacket place)
    {
        if (Chests.TryGetValue((place.X, place.Y, place.Z), out var contents)) OpenContainerWindow(0, "Chest", contents, 27);
        else if (Furnaces.TryGetValue((place.X, place.Y, place.Z), out var fslots)) OpenContainerWindow(2, "Furnace", fslots, 3);
        else if (PlaceBlocks && !place.HeldItem.IsEmpty && (place.HeldItem.Id < 256 ||
                 (place.HeldItem.Id == 295 && place.Direction == 1 && Blocks.GetValueOrDefault((place.X, place.Y, place.Z)) == 60)))
            PlaceHeldBlock(place);
    }

    /// <summary>Opens a container window: its <paramref name="size"/> slots, then the player's 36.</summary>
    private void OpenContainerWindow(byte type, string title, ItemStack[] slots, int size)
    {
        sbyte id = (sbyte)++_windowId;
        Send(new OpenWindowPacket { WindowId = id, InventoryType = type, Title = title, SlotCount = (byte)size });
        var items = new ItemStack[size + 36];
        for (int i = 0; i < size; i++) items[i] = slots[i];
        for (int i = 0; i < 36; i++) items[size + i] = Inventory[9 + i];
        Send(new WindowItemsPacket { WindowId = id, Items = items });
    }

    private void PlaceHeldBlock(PlayerBlockPlacementPacket place)
    {
        var (x, y, z) = PlacedCell(place);
        int placed = place.HeldItem.Id == 295 ? 59 : place.HeldItem.Id; // seeds on farmland plant wheat crops
        if (placed == 59 && Blocks.GetValueOrDefault((x, y, z)) != 0) return;
        Blocks[(x, y, z)] = placed;
        int meta = OrientedMeta(place, placed, place.HeldItem.Damage, x, y, z) ?? PlainPlacedMeta(place); // facing, axis, half (FakeServer.Placement.cs)
        if (meta != 0) Metas[(x, y, z)] = meta;
        else Metas.Remove((x, y, z));
        RegisterStation(x, y, z, placed);
        Send(new BlockChangePacket { X = x, Y = y, Z = z, BlockId = (byte)placed, Metadata = (byte)meta });
        Mirror?.Invoke(x, y, z, placed, meta);
        if (FallingBlocks) FallIfUnsupported(x, y, z, placed);
        if (ConsumeOnPlace) ConsumePlacedItem(place.HeldItem);
    }

    /// <summary>The cell next to the clicked block on the clicked face.</summary>
    private static (int X, int Y, int Z) PlacedCell(PlayerBlockPlacementPacket place)
    {
        var (dx, dy, dz) = place.Direction switch { 0 => (0, -1, 0), 1 => (0, 1, 0), 2 => (0, 0, -1), 3 => (0, 0, 1), 4 => (-1, 0, 0), _ => (1, 0, 0) };
        return (place.X + dx, place.Y + dy, place.Z + dz);
    }

    /// <summary>The old rule's meta: the item's variant for saplings, logs and wool, a torch from its face (BlockTorch.postPlace), else 0.</summary>
    private static int PlainPlacedMeta(PlayerBlockPlacementPacket place) =>
        place.HeldItem.Id is MineClaw.Data.Blocks.Sapling or MineClaw.Data.Blocks.Log or MineClaw.Data.Blocks.Wool ? place.HeldItem.Damage
        : place.HeldItem.Id == MineClaw.Data.Blocks.Torch ? MineClaw.World.PlaceRules.TorchMeta((MineClaw.World.BlockFace)place.Direction)
        : 0;

    /// <summary>Placed sand or gravel with no block right under it falls: its cell empties and it lands on the first block below (or
    /// is lost over the void).</summary>
    private void FallIfUnsupported(int x, int y, int z, int placed)
    {
        if (placed is not (MineClaw.Data.Blocks.Sand or MineClaw.Data.Blocks.Gravel) || GroundBelow(x, y, z) == y) return;
        int? land = GroundBelow(x, y, z);
        SetBlock(x, y, z, 0);
        Mirror?.Invoke(x, y, z, 0, 0);
        if (land is not { } ly) return;
        SetBlock(x, ly, z, placed);
        Mirror?.Invoke(x, ly, z, placed, 0);
    }

    private void ConsumePlacedItem(ItemStack h)
    {
        var left = h.Count <= 1 ? ItemStack.Empty : new ItemStack(h.Id, (byte)(h.Count - 1), h.Damage);
        Inventory[36 + _heldSlot] = left;
        var slotUpdate = new SetSlotPacket { WindowId = 0, Slot = (short)(36 + _heldSlot), Item = left };
        // 1.12.2: processTryUseItemOnBlock sends only the Block Changes; the Set Slot comes from the player's next
        // detectAndSendChanges, a tick later (b1.8.1's NetServerHandler sends it in the same handler).
        if (Modern) _ = Task.Delay(60).ContinueWith(_ => { try { Send(slotUpdate); } catch (Exception) { } }, TaskScheduler.Default);
        else Send(slotUpdate);
    }

    /// <summary>1.12.2 server logic that differs from b1.8.1's (see the class remarks). True = handled.</summary>
    private bool HandleModern(IPacket p) => p switch
    {
        TransactionPacket { Accepted: true } t when _awaitingApology == t.ActionNumber => ModernApologyAccepted(),
        WindowClickPacket click => ModernClick(click),
        CloseWindowPacket cw => ModernClose(cw),
        PlayerDiggingPacket { Status: PlayerDiggingPacket.StatusSwapHands } => ModernSwapHands(),
        PlayerBlockPlacementPacket place when place.Direction != PlayerBlockPlacementPacket.NoDirection => ModernOpenStation(place),
        _ => false,
    };

    /// <summary>processConfirmTransaction: the window unlocks.</summary>
    private bool ModernApologyAccepted()
    {
        _awaitingApology = null;
        return true;
    }

    private bool ModernClick(WindowClickPacket click)
    {
        if (_awaitingApology is not null) return true; // getCanCraft false: ignored, no reply
        SyncIn();
        var w = _srv!.ActiveWindow;
        if (w.Id != click.WindowId) return true;
        var outputBefore = w.Get(0);
        var r = _srv.ApplyClick(click.Slot, (ClickMode)click.EffectiveMode, click.EffectiveButton);
        bool ok = !RejectActions.Contains(click.ActionNumber) && r.ExpectedItem.Equals(click.ClickedItem);
        SyncOut();
        Clicks.Enqueue((click.EffectiveMode, click.Slot, ok));
        if (ok) AcceptModernClick(click, w, outputBefore);
        else RejectModernClick(click, w);
        return true;
    }

    private void AcceptModernClick(WindowClickPacket click, Window w, ItemStack outputBefore)
    {
        // slotChangedCraftingGrid sends the output during slotClick, ahead of the confirm.
        if (w.Type is WindowType.Player or WindowType.Workbench && !w.Get(0).Equals(outputBefore))
            Send(new SetSlotPacket { WindowId = (sbyte)w.Id, Slot = 0, Item = w.Get(0) });
        Send(new TransactionPacket { WindowId = click.WindowId, ActionNumber = click.ActionNumber, Accepted = true });
    }

    /// <summary>A rejected click: the window locks until the client apologises, and gets the server's window and cursor.</summary>
    private void RejectModernClick(WindowClickPacket click, Window w)
    {
        _awaitingApology = click.ActionNumber;
        Send(new TransactionPacket { WindowId = click.WindowId, ActionNumber = click.ActionNumber, Accepted = false });
        Send(new WindowItemsPacket { WindowId = (sbyte)w.Id, Items = AllSlots(w) });
        Send(new SetSlotPacket { WindowId = -1, Slot = -1, Item = _srv!.Cursor });
    }

    private static ItemStack[] AllSlots(Window w)
    {
        var all = new ItemStack[w.SlotCount];
        for (int i = 0; i < all.Length; i++) all[i] = w.Get(i);
        return all;
    }

    private bool ModernClose(CloseWindowPacket cw)
    {
        SyncIn();
        _srv!.Close(cw.WindowId);
        _openChest = null;
        _openFurnace = null;
        return true;
    }

    private bool ModernSwapHands()
    {
        SyncIn();
        _srv!.SwapHands(_heldSlot);
        SyncOut();
        Send(new SetSlotPacket { WindowId = 0, Slot = (short)(36 + _heldSlot), Item = Inventory[36 + _heldSlot] });
        Send(new SetSlotPacket { WindowId = 0, Slot = InventoryModel.OffhandSlot, Item = _srv.Offhand });
        return true;
    }

    /// <summary>A click on a crafting table, furnace or chest opens its window; anything else isn't handled here.</summary>
    private bool ModernOpenStation(PlayerBlockPlacementPacket place)
    {
        var at = (place.X, place.Y, place.Z);
        if (Workbenches.Contains(at)) return OpenModernWindow(WindowType.Workbench, 1, "Crafting", 9, null);
        // ContainerFurnace: input 0, fuel 1, output 2, then the player's main inventory and hotbar.
        if (Furnaces.TryGetValue(at, out var fslots))
        {
            OpenModernWindow(WindowType.Furnace, 2, "Furnace", 3, fslots, () => _openFurnace = at);
            return true;
        }
        if (!Chests.TryGetValue(at, out var contents)) return false;
        return OpenModernWindow(WindowType.Chest, 0, "Chest", 27, contents, () => _openChest = at);
    }

    /// <summary>Opens a window on the server model (filled with <paramref name="contents"/> and the player's inventory when given),
    /// notes it open, and sends its 0x64 and contents.</summary>
    private bool OpenModernWindow(WindowType type, byte inventoryType, string title, int slots, ItemStack[]? contents, Action? noteOpen = null)
    {
        SyncIn();
        sbyte id = (sbyte)++_windowId;
        var w = _srv!.Open(id, type, title, slots);
        if (contents is not null) _srv.SetWindowItems(id, contents.Concat(Enumerable.Range(9, 36).Select(i => Inventory[i])).ToArray());
        noteOpen?.Invoke();
        Send(new OpenWindowPacket { WindowId = id, InventoryType = inventoryType, Title = title, SlotCount = (byte)slots });
        Send(new WindowItemsPacket { WindowId = id, Items = AllSlots(w) });
        return true;
    }

    /// <summary>The server model's window 0 from <see cref="Inventory"/> (pickups, eating and tests write the array).</summary>
    private void SyncIn()
    {
        var cur = _srv!.PlayerSlots();
        for (int i = 0; i < 45; i++)
            if (!cur[i].Equals(Inventory[i])) _srv.PlayerWindow.Set(i, Inventory[i]);
    }

    private void SyncOut()
    {
        var cur = _srv!.PlayerSlots();
        for (int i = 0; i < 45; i++) Inventory[i] = cur[i];
        SyncOutOpenContainer();
    }

    /// <summary>The open chest's or furnace's slots back into its contents.</summary>
    private void SyncOutOpenContainer()
    {
        if (_openChest is { } at && _srv!.OpenWindow is { Type: WindowType.Chest } w && Chests.TryGetValue(at, out var contents))
            for (int i = 0; i < contents.Length && i < w.ContainerSlots; i++) contents[i] = w.Get(i);
        if (_openFurnace is { } fat && _srv!.OpenWindow is { Type: WindowType.Furnace } fw && Furnaces.TryGetValue(fat, out var fs))
            for (int i = 0; i < 3; i++) fs[i] = fw.Get(i);
    }

    private (int, int, int)? _openFurnace;

    /// <summary>1.12.2: placing a crafting table or a furnace makes it usable (a workbench window / a furnace with empty slots).</summary>
    private void RegisterStation(int x, int y, int z, int id)
    {
        if (!Modern) return;
        if (id == MineClaw.Data.Blocks.CraftingTable) Workbenches.Add((x, y, z));
        else if (id == MineClaw.Data.Blocks.Furnace && !Furnaces.ContainsKey((x, y, z))) Furnaces[(x, y, z)] = Enumerable.Repeat(ItemStack.Empty, 3).ToArray();
    }

    private CancellationTokenSource? _smelter;
    private readonly Dictionary<(int, int, int), (int Burn, int Cook)> _furnaceState = new();

    /// <summary>Furnace time, sped up (<paramref name="ticksPerStep"/> furnace ticks every 10 ms; otherwise TileEntityFurnace.update):
    /// fuel burns one item at a time (FuelTicks), an item cooks in CookTicks, the output stacks in slot 2. An open furnace window
    /// gets the slot updates.</summary>
    public void StartSmelting(int ticksPerStep = 40)
    {
        if (_smelter != null) return;
        _smelter = new CancellationTokenSource();
        var ct = _smelter.Token;
        MineClaw.Data.Versioning.IGameData data = Modern ? MineClaw.Data.Versioning.Release1122GameData.Instance : MineClaw.Data.Versioning.Beta181GameData.Instance;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(10, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                lock (BlockLock) SmeltStep(data, ticksPerStep);
            }
        }, ct);
    }

    private void SmeltStep(MineClaw.Data.Versioning.IGameData data, int ticksPerStep)
    {
        foreach (var (pos, slots) in Furnaces) SmeltFurnace(data, ticksPerStep, pos, slots);
    }

    /// <summary>One furnace's step: the open window's slots first, then refuel, cook (or cool down), and the window gets the changes.</summary>
    private void SmeltFurnace(MineClaw.Data.Versioning.IGameData data, int ticksPerStep, (int, int, int) pos, ItemStack[] slots)
    {
        var st = _furnaceState.GetValueOrDefault(pos);
        var fw = OpenFurnaceWindow(pos);
        if (fw != null) for (int i = 0; i < 3; i++) slots[i] = fw.Get(i);
        var input = slots[0];
        var recipe = input.IsEmpty ? null : data.SmeltingFor(input.Id, input.Damage);
        bool fits = OutputFits(recipe, slots[2]);
        bool changed = fits && Refuel(ref st, slots, data);
        if (fits && st.Burn > 0) changed |= Cook(ref st, slots, recipe!, data, ticksPerStep);
        else CoolDown(ref st, ticksPerStep);
        if (st.Burn < 0) st.Burn = 0;
        _furnaceState[pos] = st;
        if (changed && fw != null) PushFurnaceSlots(fw, slots);
    }

    /// <summary>Nothing cooking: the progress resets and what still burns burns on.</summary>
    private static void CoolDown(ref (int Burn, int Cook) st, int ticksPerStep)
    {
        st.Cook = 0;
        if (st.Burn > 0) st.Burn -= ticksPerStep;
    }

    private Window? OpenFurnaceWindow((int, int, int) pos) =>
        _openFurnace == pos && _srv?.OpenWindow is { Type: WindowType.Furnace } ow ? ow : null;

    /// <summary>A recipe whose output can go into the output slot (empty, or the same output below a full stack).</summary>
    private static bool OutputFits(SmeltingRecipe? recipe, ItemStack output) =>
        recipe != null && (output.IsEmpty || output.Id == recipe.OutputId && output.Damage == recipe.OutputDamage && output.Count < 64);

    /// <summary>A burnt-out furnace takes one fuel item: true when it did.</summary>
    private static bool Refuel(ref (int Burn, int Cook) st, ItemStack[] slots, MineClaw.Data.Versioning.IGameData data)
    {
        if (st.Burn > 0 || slots[1].IsEmpty || data.FuelTicks(slots[1].Id) is not (> 0 and var burn)) return false;
        st.Burn = burn;
        slots[1] = slots[1].Count <= 1 ? ItemStack.Empty : new ItemStack(slots[1].Id, (byte)(slots[1].Count - 1), slots[1].Damage);
        return true;
    }

    /// <summary>Burns and cooks one step: true when an item finished (one input off, one output on).</summary>
    private static bool Cook(ref (int Burn, int Cook) st, ItemStack[] slots, SmeltingRecipe recipe, MineClaw.Data.Versioning.IGameData data, int ticksPerStep)
    {
        st.Burn -= ticksPerStep;
        st.Cook += ticksPerStep;
        if (st.Cook < data.CookTicks) return false;
        st.Cook = 0;
        var input = slots[0];
        slots[0] = input.Count <= 1 ? ItemStack.Empty : new ItemStack(input.Id, (byte)(input.Count - 1), input.Damage);
        slots[2] = slots[2].IsEmpty ? new ItemStack((short)recipe.OutputId, 1, (short)recipe.OutputDamage)
            : new ItemStack(slots[2].Id, (byte)(slots[2].Count + 1), slots[2].Damage);
        return true;
    }

    private void PushFurnaceSlots(Window fw, ItemStack[] slots)
    {
        for (int i = 0; i < 3; i++)
        {
            fw.Set(i, slots[i]);
            Send(new SetSlotPacket { WindowId = (sbyte)fw.Id, Slot = (short)i, Item = slots[i] });
        }
    }

    /// <summary>BlockDoor/BlockTrapDoor/BlockFenceGate.blockActivated: flips the open bit (both door halves) and sends the
    /// block changes; locked ones get the unchanged block re-sent. False when (x,y,z) is not a wooden openable.</summary>
    private bool ToggleOpenable(int x, int y, int z)
    {
        Blocks.TryGetValue((x, y, z), out int id);
        if (id is not (MineClaw.Data.Blocks.WoodenDoor or MineClaw.Data.Blocks.Trapdoor or MineClaw.Data.Blocks.FenceGate)) return false;
        Metas.TryGetValue((x, y, z), out int meta);
        if (DoorHalfAt(id, x, y - 1, z) && (meta & 8) != 0) { y--; Metas.TryGetValue((x, y, z), out meta); }
        if (Locked.Contains((x, y, z)))
        {
            Send(new BlockChangePacket { X = x, Y = y, Z = z, BlockId = (byte)id, Metadata = (byte)meta });
            return true;
        }
        SetBlock(x, y, z, id, meta ^ 4);
        if (DoorHalfAt(id, x, y + 1, z)) SetBlock(x, y + 1, z, id, (meta ^ 4) | 8);
        return true;
    }

    /// <summary>A wooden door's other half is at (x, y, z).</summary>
    private bool DoorHalfAt(int id, int x, int y, int z) =>
        id == MineClaw.Data.Blocks.WoodenDoor && Blocks.TryGetValue((x, y, z), out int other) && other == id;

    /// <summary>Item gravity: drops resting on a removed block fall to the next block below (or out of the world).</summary>
    private void SettleDrops(int x, int y, int z)
    {
        foreach (var (id, d) in Drops)
        {
            if ((int)Math.Floor(d.X) != x || (int)Math.Floor(d.Z) != z || Math.Abs(d.Y - (y + 1.1)) > 0.25) continue;
            if (GroundBelow(x, y + 1, z) is { } gy)
            {
                Drops[id] = (d.X, gy + 0.1, d.Z, d.Item);
                Send(new EntityTeleportPacket { EntityId = id, X = FixedPoint.ToAbsolute(d.X), Y = FixedPoint.ToAbsolute(gy + 0.1), Z = FixedPoint.ToAbsolute(d.Z) });
            }
            else if (Drops.TryRemove(id, out _)) Send(new DestroyEntityPacket { EntityId = id });
        }
    }

    /// <summary>Top of the first block below (x, y, z), or null over the void.</summary>
    public int? GroundBelow(int x, int y, int z)
    {
        for (int yy = y - 1; yy >= 0; yy--)
            if (Blocks.TryGetValue((x, yy, z), out int id) && id != 0) return yy + 1;
        return null;
    }

    /// <summary>Hardness-0 ids of both versions (b1.8.1 and 1.12.2: saplings, tall grass, ferns, dead bushes, flowers, mushrooms,
    /// torches, wheat, sugar cane, nether wart, carrots, potatoes, double plants, beetroot), for ids the active block table lacks.</summary>
    private static readonly HashSet<int> HardnessZero = [6, 31, 32, 37, 38, 39, 40, 50, 59, 83, 115, 141, 142, 175, 207];

    private bool InstantBreak(int x, int y, int z) =>
        Blocks.TryGetValue((x, y, z), out int id) && id != 0
        && (HardnessZero.Contains(id) || MineClaw.Data.Blocks.Exists(id) && MineClaw.Data.Blocks.Get(id).Hardness == 0);

    /// <summary>Server-side block change (decay, another player...).</summary>
    public void SetBlock(int x, int y, int z, int id, int meta = 0)
    {
        if (id == 0) { Blocks.Remove((x, y, z)); Metas.Remove((x, y, z)); }
        else { Blocks[(x, y, z)] = id; if (meta != 0) Metas[(x, y, z)] = meta; else Metas.Remove((x, y, z)); }
        Send(new BlockChangePacket { X = x, Y = y, Z = z, BlockId = (byte)id, Metadata = (byte)meta });
    }

    public int SpawnDrop(double x, double y, double z, ItemStack item)
    {
        int id = Interlocked.Increment(ref _nextEntity);
        Drops[id] = (x, y, z, item);
        Send(new PickupSpawnPacket
        {
            EntityId = id, ItemId = item.Id, Count = item.Count, Damage = item.Damage,
            X = FixedPoint.ToAbsolute(x), Y = FixedPoint.ToAbsolute(y), Z = FixedPoint.ToAbsolute(z),
        });
        return id;
    }

    /// <summary>The item's box against the player's box expanded by 1 on x/z (strict: the y overlap too: a slab stand misses floor
    /// drops).</summary>
    private bool InPickupReach(double dropX, double dropY, double dropZ, double x, double y, double z)
    {
        if (Math.Abs(dropX - x) > 1.425 || Math.Abs(dropZ - z) > 1.425 || Math.Abs(dropY - y) > 1.5) return false;
        return !(StrictPickup && (dropY + 0.125 <= y || dropY - 0.125 >= y + 1.8));
    }

    private void CheckPickups(double x, double y, double z)
    {
        foreach (var (id, d) in Drops)
        {
            // Vanilla EntityPlayer.onLivingUpdate: items intersecting the player box expanded by 1 on x/z
            // (0.3 + 1 + item half-width 0.125 per axis).
            if (!InPickupReach(d.X, d.Y, d.Z, x, y, z) || !Drops.TryRemove(id, out _)) continue;
            int slot = Array.FindIndex(Inventory, 36, 9, s => s.IsEmpty || s.Id == d.Item.Id);
            if (slot < 0) slot = Array.FindIndex(Inventory, 9, 27, s => s.IsEmpty || s.Id == d.Item.Id);
            var cur = Inventory[slot];
            Inventory[slot] = new ItemStack(d.Item.Id, (byte)((cur.IsEmpty ? 0 : cur.Count) + d.Item.Count), d.Item.Damage);
            Send(new CollectItemPacket { CollectedEntityId = id, CollectorEntityId = EntityId });
            Send(new DestroyEntityPacket { EntityId = id });
            Send(new SetSlotPacket { WindowId = 0, Slot = (short)slot, Item = Inventory[slot] });
        }
    }

    /// <summary>
    /// Like the real b1.8.1 server: the spawn chunks stream only after the client confirms the login 0x0D (EntityPlayerMP.a(true)
    /// runs from NetServerHandler.a(Packet10Flying) once checkMovement is set). Off by default (chunks before the 0x0D).
    /// </summary>
    public bool ChunksAfterConfirm { get; set; }
    private volatile bool _chunksPending;

    private void SendSpawnChunks()
    {
        for (int cx = -1; cx <= 1; cx++)
        for (int cz = -1; cz <= 1; cz++)
        {
            Send(new PreChunkPacket { ChunkX = cx, ChunkZ = cz, Load = true });
            Send(ChunkDataDecoder.Encode(BuildChunk(cx, cz)));
        }
    }

    /// <summary>A C→S position matching the login 0x0D's X/Z confirms it (NetServerHandler.java:66-71): stream the chunks.</summary>
    private void MaybeConfirm(IPacket p)
    {
        if (!_chunksPending) return;
        (bool has, double x, double z) = p switch
        {
            PlayerPositionLookPacket pl => (true, pl.X, pl.Z),
            PlayerPositionPacket pp => (true, pp.X, pp.Z),
            _ => (false, 0, 0),
        };
        if (!has || x != SpawnAt.X || z != SpawnAt.Z) return;
        _chunksPending = false;
        SendSpawnChunks();
    }

    /// <summary>(Re)sends one chunk column built from <see cref="Blocks"/> (e.g. after a respawn).</summary>
    public void SendChunk(int cx, int cz)
    {
        Send(new PreChunkPacket { ChunkX = cx, ChunkZ = cz, Load = true });
        Send(ChunkDataDecoder.Encode(BuildChunk(cx, cz)));
    }

    private ChunkRegion BuildChunk(int cx, int cz)
    {
        var r = new ChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16);
        Array.Fill(r.SkyLight, (byte)15);
        foreach (var ((x, y, z), id) in Blocks)
        {
            if (x >> 4 != cx || z >> 4 != cz) continue;
            r.BlockIds[r.Index(x - cx * 16, y, z - cz * 16)] = (byte)id;
            if (Metas.TryGetValue((x, y, z), out int m)) r.Metadata[r.Index(x - cx * 16, y, z - cz * 16)] = (byte)m;
        }
        return r;
    }

    public void Dispose()
    {
        _ai?.Cancel();
        _smelter?.Cancel();
        try { _conn.Dispose(); } catch { }
        try { _clientEnd?.Dispose(); } catch { }
    }
}
