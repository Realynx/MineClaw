using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Client;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.World;

namespace MineClaw.Hive.Testing;

/// <summary>Fake inventory: stacks + optional chests with a fixed number of free slots (one ItemMsg = one stack).</summary>
public sealed class FakePort : IInventoryPort
{
    private readonly object _lock = new();
    public List<ItemMsg> Stacks { get; } = new();
    public Dictionary<BlockPos, int> ChestFree { get; } = new();
    public bool Safe { get; set; } = true;
    public int Deposits;

    public FakePort(int stacks = 0, int chestSlots = 0)
    {
        for (int i = 0; i < stacks; i++) Stacks.Add(new ItemMsg(4, 0, 16, "cobblestone"));
        if (chestSlots > 0) ChestFree[new BlockPos(0, 64, 0)] = chestSlots;
    }

    public IReadOnlyList<ItemMsg> Items() { lock (_lock) return Stacks.ToList(); }
    public bool CanActSafely => Safe;
    public IReadOnlyList<StorageChest> Chests() { lock (_lock) return ChestFree.Select(kv => new StorageChest(kv.Key, kv.Value, 3)).ToList(); }

    public void AddChest(BlockPos p, int slots) { lock (_lock) ChestFree[p] = slots; }
    /// <summary>An item picked up (e.g. main's hand-off toss).</summary>
    public void Add(ItemMsg item) { lock (_lock) Stacks.Add(item); }

    /// <summary>Each deposit first waits this long (a contended chest, a long walk); cancellable like the real one.</summary>
    public TimeSpan DepositDelay { get; set; }

    public async Task<ActionResult> DepositToAsync(BlockPos chest, CancellationToken ct)
    {
        if (DepositDelay > TimeSpan.Zero) await Task.Delay(DepositDelay, ct).ConfigureAwait(false);
        return await DepositNow(chest).ConfigureAwait(false);
    }

    private Task<ActionResult> DepositNow(BlockPos chest)
    {
        lock (_lock)
        {
            Interlocked.Increment(ref Deposits);
            if (!ChestFree.TryGetValue(chest, out var free) || free == 0) return Task.FromResult(ActionResult.Fail("chest is full"));
            int n = Math.Min(free, Stacks.Count);
            Stacks.RemoveRange(0, n);
            ChestFree[chest] = free - n;
            Hidden.AddRange(RejectedOnDeposit);
            RejectedOnDeposit.Clear();
            return Task.FromResult(n > 0 ? ActionResult.Success($"deposited {n} stacks", n) : ActionResult.Fail("nothing to deposit"));
        }
    }

    /// <summary>Items the server has but the (stale/predicted) local copy doesn't show until <see cref="SettleAsync"/>.</summary>
    public List<ItemMsg> Hidden { get; } = new();
    /// <summary>Moved into <see cref="Hidden"/> by the next deposit (a click the server rejected after we predicted it).</summary>
    public List<ItemMsg> RejectedOnDeposit { get; } = new();
    public int Settles;

    public Task<bool> SettleAsync(CancellationToken ct)
    {
        lock (_lock) { Settles++; Stacks.AddRange(Hidden); Hidden.Clear(); }
        return Task.FromResult(true);
    }

    public Task<ActionResult> TossAllAtAsync(Vec3d at, CancellationToken ct) => Task.FromResult(ActionResult.Fail("can't toss in a test"));
    public Task<(ActionResult Result, BlockPos? Chest)> PlaceLabelledChestAsync(string label, CancellationToken ct) =>
        Task.FromResult<(ActionResult, BlockPos?)>((ActionResult.Fail("no chest item"), null));
}

/// <summary>Fake game + brain for a clone: records calls; task text drives behaviour ("done", "die", "spend").</summary>
public sealed class FakeBody : ICloneBody
{
    public FakeBody(string name, FakePort port) { Name = name; Port = port; }

    public string Name { get; }
    public FakePort Port { get; }
    public bool IsPlaying { get; set; } = true;
    public IInventoryPort Inventory => Port;
    public List<string> Calls { get; } = new();
    public Func<TaskMsg, Task>? OnTask { get; set; }
    /// <summary>Chat lines main forwards (console /as): the "chat" mode routes them through a real ChatRouter.</summary>
    public Action<string>? OnInject { get; set; }
    /// <summary>What the streamed snapshot says the clone is doing.</summary>
    public string TaskText { get; set; } = "fake";
    public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ItemsAtDisconnect = -1;

    public event Action<PromptUsageRecord>? UsageRecorded;
    public event Action<string>? Died;

    public void RaiseUsage(PromptUsageRecord r) => UsageRecorded?.Invoke(r);
    public void Die(string cause) => Died?.Invoke(cause);

    public BotSnapshot Snapshot(bool full) => new()
    {
        Name = Name, Status = IsPlaying ? BotStatus.Online : BotStatus.Connecting, At = DateTimeOffset.UtcNow,
        Pos = new BotPos(1.5, 64, 1.5), Hp = 20, Food = 20, Task = TaskText,
        Viewport = full ? new ViewportGrid { Radius = 1, Top = ["...", ".@.", "..."] } : null,
    };

    private void Call(string c) { lock (Calls) Calls.Add(c); }
    public Task ConnectAsync(CancellationToken ct) { Call("connect"); return Task.CompletedTask; }
    public void StartAgent(CancellationToken ct) => Call("start");
    public Task StopAgentAsync() { Call("stop"); return Task.CompletedTask; }
    public void AssignTask(TaskMsg task) { Call("task " + task.Text); if (OnTask is { } f) _ = Task.Run(() => f(task)); }
    /// <summary>Loadout fetch from storage (null hook = unsupported, like the interface default).</summary>
    public Func<IReadOnlyList<string>, LoadoutReport?>? Loadout { get; set; }
    public Task<LoadoutReport?> PrepareLoadoutAsync(IReadOnlyList<string> loadout, CancellationToken ct) => Task.FromResult(Loadout?.Invoke(loadout));
    public Task<bool> ExecuteAsync(string line, CancellationToken ct) { Call("cmd " + line); return Task.FromResult(true); }
    public void InjectChat(string raw) { Call("inject " + raw); OnInject?.Invoke(raw); }
    /// <summary>Web-viewer lines main forwards (InjectMsg with an origin).</summary>
    public List<MineClaw.Agent.Chat.RemoteChat> Remote { get; } = new();
    public void InjectRemoteChat(MineClaw.Agent.Chat.RemoteChat chat) { Call("remote " + chat.Text); lock (Remote) Remote.Add(chat); }
    public void Note(string text) => Call("note " + text);
    /// <summary>Public chat the host said from C# (the void-spawn help request).</summary>
    public List<string> Said { get; } = new();
    public Task SayAsync(string text) { lock (Said) Said.Add(text); return Task.CompletedTask; }
    /// <summary>The client's post-login/teleport safety hold (over the void it lasts until a /tp).</summary>
    public bool HoldingStill { get; set; }
    /// <summary>A real game client behind the fake body (the "dupkick" mode): disconnecting it stops its auto-reconnect.</summary>
    public Func<Task>? OnDisconnect { get; set; }
    public async Task DisconnectAsync(string reason)
    {
        ItemsAtDisconnect = Port.Items().Count;
        Call("disconnect");
        if (OnDisconnect is { } d) await d();
        Disconnected.TrySetResult();
    }
}

/// <summary>Fake LLM: every call costs the same (1M Haiku input tokens = $0.10 at the default prices).</summary>
public sealed class FakeLlm(long inputTokens = 1_000_000) : ILlmClient
{
    public int Calls;
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new LlmResponse([new LlmText("ok")], StopReasons.EndTurn, new LlmUsage(inputTokens, 0, 0, 0), "claude-haiku-5-5"));
    }
}
