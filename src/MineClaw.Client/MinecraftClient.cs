using MineClaw.Client.Survival;
using System.Collections.Concurrent;
using System.Threading.Channels;
using MineClaw.Data;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Headless b1.8.1 client: connection + login, a reader thread that applies every packet to <see cref="World"/>,
/// a 20 TPS tick thread running physics and sending movement like vanilla, and async action primitives
/// (movement, digging, placing, windows, crafting, smelting, gathering) plus compact observation strings.
/// <para>Threading: the reader thread mutates world state; the tick thread owns <c>World.Player.Physics</c>;
/// actions run on the thread pool and are serialized by an action lock (one movement/interaction at a time; chat is
/// independent). Public events are raised on a dedicated dispatcher task, never on the network or tick thread, so a
/// slow handler cannot stall the connection.</para>
/// </summary>
public sealed partial class MinecraftClient : IAsyncDisposable
{
    private readonly Channel<Action> _eventQueue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _eventPump;
    private readonly object _lifecycle = new();
    private Session? _session;
    private volatile bool _userDisconnect;
    private CancellationTokenSource? _reconnectCts;
    private volatile ClientState _state = ClientState.Disconnected;

    public MinecraftClient(ClientOptions options, WorldState? world = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        World = world ?? new WorldState();
        IsProtected = options.IsProtected;
        ReservedItems = options.ReservedItems ?? new HashSet<int>();
        Survival = new SurvivalReflex(options.Survival);
        World.Inventory.CraftResult = (grid, width) => Recipes.Match(grid, width)?.Output ?? ItemStack.Empty;
        World.Inventory.WindowOpened += _ => Interlocked.Increment(ref _windowsOpened);
        _eventPump = Task.Run(PumpEventsAsync);
    }

    public ClientOptions Options { get; }
    public WorldState World { get; }
    public ClientState State => _state;

    /// <summary>Connected, spawned (position known) and alive — actions are possible.</summary>
    public bool IsPlaying => _state == ClientState.Playing && _session is { PositionKnown: true } && !World.Player.IsDead;

    /// <summary>Blocks never to break (player builds). Replaceable at runtime by the Agent (protected regions from memory).</summary>
    public Func<BlockPos, int, bool>? IsProtected { get; set; }

    /// <summary>Items not to eat or deposit automatically.</summary>
    public IReadOnlySet<int> ReservedItems { get; set; }

    public int Difficulty { get; private set; }
    /// <summary>Unexpected server position corrections (rubber-banding) since connect.</summary>
    public int PositionCorrections => _session?.Corrections ?? 0;
    public long TickCount => Interlocked.Read(ref _tickCount);
    public string? LastKickReason { get; private set; }

    // ------------------------------------------------------------------ events (raised on the event dispatcher)

    /// <summary>Raw chat line from the server (may contain § colour codes). See <see cref="ChatText.TryParsePlayerChat"/>.</summary>
    public event Action<string>? ChatReceived;
    public event Action<string>? Kicked;
    public event Action<string>? Disconnected;
    /// <summary>Logged in and the first position arrived.</summary>
    public event Action? Spawned;
    public event Action? Died;
    public event Action? Respawned;
    /// <summary>(health, food) after a 0x08 update that changed either.</summary>
    public event Action<int, int>? HealthChanged;
    public event Action<string>? PlayerJoined;
    public event Action<string>? PlayerLeft;
    public event Action<DangerEvent>? Danger;
    /// <summary>(entity id, animation) from 0x12 for other entities (1 = arm swing, 2 = hurt). Used to watch players teach.</summary>
    public event Action<int, int>? EntityAnimated;
    /// <summary>(collector entity id, item entity id, stack if the item entity was tracked) from 0x16 Collect Item.</summary>
    public event Action<int, int, ItemStack?>? ItemCollected;
    /// <summary>(entity id, slot 0 held / 1-4 armor boots..helmet, item id (-1 empty), damage) from 0x05.</summary>
    public event Action<int, int, int, int>? EquipmentChanged;
    /// <summary>(entity id, status) from 0x26: 2 hurt, 3 dead, 6/7 wolf taming, 8 wolf shaking, 9 eating accepted (self).</summary>
    public event Action<int, int>? EntityStatus;
    /// <summary>(entity id, bed head x, y, z) from 0x11 Use Bed.</summary>
    public event Action<int, int, int, int>? EntityUsedBed;
    /// <summary>(entity id, vehicle id or -1 = dismount) from 0x27.</summary>
    public event Action<int, int>? EntityAttached;
    /// <summary>(x, y, z, four lines) from 0x82 Update Sign.</summary>
    public event Action<int, int, int, string[]>? SignUpdated;
    /// <summary>(window id, window type 0 chest / 1 workbench / 2 furnace / 3 dispenser, x, y, z) when a 0x64 Open Window
    /// arrives for the block the bot just right-clicked (<see cref="ActivateBlockAsync"/> / <see cref="OpenContainerAsync"/>).</summary>
    public event Action<int, int, int, int, int>? ContainerOpened;
    /// <summary>(block, data1, data2) from 0x36 block action (note blocks, pistons; chest lids on servers that send them).</summary>
    public event Action<BlockPos, int, int>? BlockAction;

    private void Raise(Action a) => _eventQueue.Writer.TryWrite(a);

    private async Task PumpEventsAsync()
    {
        await foreach (var a in _eventQueue.Reader.ReadAllAsync())
        {
            try { a(); }
            catch (Exception ex) { Log($"event handler threw: {ex.Message}"); }
        }
    }

    private void Log(string msg) => Options.Log?.Invoke(msg);

    // ------------------------------------------------------------------ session

    /// <summary>Per-connection state; replaced on reconnect.</summary>
    private sealed class Session(PacketConnection conn)
    {
        public PacketConnection Conn { get; } = conn;
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource SpawnedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Thread? Reader;
        public Thread? Ticker;
        public volatile bool PositionKnown;
        /// <summary>True until the next server 0x0D (initial login, after respawn): don't move, don't count it as a correction.</summary>
        public volatile bool AwaitingTeleport = true;
        public volatile bool Ended;
        public int Corrections;
        public int TeleportsReceived;
        public string? EndReason;
        public readonly ConcurrentQueue<PlayerPositionLookPacket> Teleports = new();
        public readonly HashSet<string> Online = new(StringComparer.OrdinalIgnoreCase);
        public MineClaw.Protocol.Recording.PacketRecorder? Recorder;
    }

    /// <summary>Path of the current session's packet recording, or null.</summary>
    public string? RecordingPath => _session?.Recorder?.Path;

    /// <summary>Directory to record into: <see cref="ClientOptions.RecordDirectory"/>, else env MINECLAW_RECORD.</summary>
    public static string? ResolveRecordDirectory(string? configured, string? env)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (string.IsNullOrWhiteSpace(env) || env is "0" or "false" or "off") return null;
        return env is "1" or "true" or "on" ? Path.Combine(Environment.CurrentDirectory, "logs", "recordings") : env;
    }

    private void StartRecording(Session s)
    {
        var dir = ResolveRecordDirectory(Options.RecordDirectory, Environment.GetEnvironmentVariable("MINECLAW_RECORD"));
        if (dir is null) return;
        try
        {
            s.Recorder = MineClaw.Protocol.Recording.PacketRecorder.CreateFile(dir, Options.RecordGzip,
                $"user={Options.Username};host={Options.Host}:{Options.Port};protocol={ProtocolInfo.ProtocolVersion}");
            s.Recorder.Attach(s.Conn);
            Log($"recording packets to {s.Recorder.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"can't record packets in {dir}: {ex.Message}");
        }
    }

    private CancellationToken SessionToken => _session?.Cts.Token ?? new CancellationToken(true);

    /// <summary>Connects, logs in and waits until the server has placed the player. Throws on failure/kick/timeout.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _userDisconnect = false;
        await ConnectOnceAsync(ct).ConfigureAwait(false);
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        lock (_lifecycle)
        {
            if (_session is { Ended: false }) throw new InvalidOperationException("already connected");
            _state = ClientState.Connecting;
        }
        PacketConnection conn;
        try
        {
            conn = Options.ConnectionFactory is { } f
                ? f(ct)
                : await Task.Run(() => PacketConnection.Connect(Options.Host, Options.Port), ct).ConfigureAwait(false);
        }
        catch
        {
            _state = ClientState.Disconnected;
            throw;
        }

        var s = new Session(conn);
        ResetForNewSession();
        StartRecording(s);
        lock (_lifecycle) _session = s;
        s.Reader = new Thread(() => ReaderLoop(s)) { IsBackground = true, Name = "mc-reader" };
        s.Ticker = new Thread(() => TickLoop(s)) { IsBackground = true, Name = "mc-tick" };
        s.Reader.Start();
        s.Ticker.Start();
        Send(s, new HandshakePacket(Options.Username));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Options.LoginTimeout);
        try
        {
            await s.SpawnedTcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            EndSession(s, ct.IsCancellationRequested ? "connect cancelled" : "login timed out", sendQuit: true);
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("login timed out (no spawn position within " + Options.LoginTimeout + ")");
        }
        _state = ClientState.Playing;
        Log($"spawned at {World.Player.Position}");
        Raise(() => Spawned?.Invoke());
    }

    private void ResetForNewSession()
    {
        World.Clear();
        World.Entities.Clear();
        World.Inventory.Close(-1);
        _driver = null;
        _txResults.Clear();
        _signs.Clear();
        _vehicleId = -1;
        _entityStatusSeq.Clear();
        _wasDead = false;
        _lastHealth = -1;
        _lastFood = -1;
        _serverHeldSlot = -1; // the server starts every login on hotbar slot 0; resend our choice before using items
        _lastSprint = _lastSneak = false;
        _voidAlerted = _lavaAlerted = false;
        _hostileAlerted.Clear();
        _combatBlocking = false;
        _combatInstance?.RequestStop("disconnected");
        ResetSafety();
        ResetSurvival();
    }

    /// <summary>Sends a clean quit (0xFF), closes the connection and stops auto-reconnect.</summary>
    public async Task DisconnectAsync(string reason = "Quitting")
    {
        _userDisconnect = true;
        _reconnectCts?.Cancel();
        var s = _session;
        if (s is null) return;
        EndSession(s, "disconnected by client: " + reason, sendQuit: true, quitReason: reason);
        await Task.Run(() =>
        {
            s.Reader?.Join(TimeSpan.FromSeconds(2));
            s.Ticker?.Join(TimeSpan.FromSeconds(2));
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _eventQueue.Writer.TryComplete();
        if (_telemetry is not null) await _telemetry.DisposeAsync().ConfigureAwait(false);
        try { await _eventPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { /* ignore */ }
    }

    private void EndSession(Session s, string reason, bool sendQuit = false, string quitReason = "Quitting")
    {
        lock (s)
        {
            if (s.Ended) return;
            s.Ended = true;
            s.EndReason = reason;
        }
        if (sendQuit)
        {
            try { s.Conn.Send(new KickPacket(quitReason)); } catch { /* ignore */ }
        }
        s.Cts.Cancel();
        try { s.Conn.Dispose(); } catch { /* ignore */ }
        s.Recorder?.Dispose();
        s.SpawnedTcs.TrySetException(new IOException(reason));
        _driver?.Tcs.TrySetException(new ActionAbortedException("disconnected"));
        _driver = null;
        _actionCts?.Cancel();
        _changed.Pulse();
        bool wasPlaying = _state == ClientState.Playing;
        _state = ClientState.Disconnected;
        Log($"session ended: {reason}");
        if (wasPlaying || s.PositionKnown) Raise(() => Disconnected?.Invoke(reason));

        if (Options.AutoReconnect && !_userDisconnect && wasPlaying)
            StartReconnectLoop(reason);
    }

    /// <summary>First reconnect delay after a session ended for <paramref name="endReason"/>: long after a duplicate-login
    /// kick (another session owns the account), otherwise <see cref="ClientOptions.ReconnectInitialDelay"/>.</summary>
    public static TimeSpan FirstReconnectDelay(string? endReason, ClientOptions o) =>
        endReason is not null && endReason.Contains("logged in from another location", StringComparison.OrdinalIgnoreCase)
            ? o.DuplicateLoginBackoff
            : o.ReconnectInitialDelay;

    private void StartReconnectLoop(string endReason)
    {
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        _state = ClientState.Reconnecting;
        _ = Task.Run(async () =>
        {
            var delay = FirstReconnectDelay(endReason, Options);
            while (!cts.IsCancellationRequested && !_userDisconnect)
            {
                Log($"reconnecting in {delay.TotalSeconds:0}s");
                try { await Task.Delay(delay, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    await ConnectOnceAsync(cts.Token).ConfigureAwait(false);
                    Log("reconnected");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"reconnect failed: {ex.Message}");
                    if (LastKickReason is { } k && k.Contains("Outdated", StringComparison.OrdinalIgnoreCase)) return;
                    _state = ClientState.Reconnecting;
                }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, Options.ReconnectMaxDelay.Ticks));
            }
        });
    }

    // ------------------------------------------------------------------ sending

    private void Send(IPacket p)
    {
        var s = _session;
        if (s is null || s.Ended) throw new ActionAbortedException("not connected");
        Send(s, p);
    }

    private void Send(Session s, IPacket p)
    {
        if (s.Ended) return;
        NoteSelfEdit(p);
        try
        {
            s.Conn.Send(p);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            EndSession(s, "send failed: " + ex.Message);
        }
    }

    /// <summary>Own digs/placements go into <see cref="WorldState.SelfEdits"/> so observers never credit them to a player.</summary>
    private void NoteSelfEdit(IPacket p)
    {
        switch (p)
        {
            case PlayerDiggingPacket d when d.Status is PlayerDiggingPacket.StatusStarted or PlayerDiggingPacket.StatusFinished:
                World.SelfEdits.Note(new BlockPos(d.X, d.Y, d.Z));
                break;
            case PlayerBlockPlacementPacket b when b.Direction != PlayerBlockPlacementPacket.NoDirection:
                World.SelfEdits.Note(PlacementTarget(b.X, b.Y, b.Z, b.Direction));
                break;
        }
    }

    /// <summary>The cell a 0x0F placement fills: the clicked block offset by the face (0 -y, 1 +y, 2 -z, 3 +z, 4 -x, 5 +x).</summary>
    public static BlockPos PlacementTarget(int x, int y, int z, int face) => face switch
    {
        0 => new(x, y - 1, z), 1 => new(x, y + 1, z), 2 => new(x, y, z - 1), 3 => new(x, y, z + 1), 4 => new(x - 1, y, z), _ => new(x + 1, y, z),
    };

    // ------------------------------------------------------------------ reader thread

    private void ReaderLoop(Session s)
    {
        string reason = "server closed the connection";
        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                var p = s.Conn.ReadPacket();
                if (p is null) break;
                try
                {
                    Dispatch(s, p);
                }
                catch (Exception ex) when (ex is not (IOException or ObjectDisposedException))
                {
                    Log($"error handling 0x{p.Id:X2}: {ex.Message}");
                }
                if (p.Id != KeepAlivePacket.PacketId) _changed.Pulse();
                if (s.Ended) return;
            }
        }
        catch (ProtocolException ex)
        {
            reason = "protocol error: " + ex.Message;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            reason = "connection lost: " + ex.Message;
        }
        EndSession(s, s.EndReason ?? reason);
    }
}

/// <summary>Thrown inside actions when they must stop for a reason that is not a plain cancellation.</summary>
public sealed class ActionAbortedException(string message) : Exception(message);
