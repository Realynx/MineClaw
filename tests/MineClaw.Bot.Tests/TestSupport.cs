using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Llm;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>In-memory <see cref="IGameClient"/>: a real <see cref="WorldState"/>, every action recorded as "name args".</summary>
public sealed class FakeGameClient : IGameClient
{
    public FakeGameClient()
    {
        // Load chunks -1..0 (blocks -16..15) as air.
        var size = 32;
        World.ApplyChunkRegion(-16, 0, -16, size, 128, size, new byte[size * 128 * size], null, null, null);
        World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        World.Player.SetHealth(20, 20, 5);
    }

    public WorldState World { get; } = new();
    public string Username { get; set; } = "maid_chan";
    public bool IsPlaying { get; set; } = true;
    public string? CurrentAction { get; set; }
    public double StationSearchRadius { get; set; } = 24;
    public Func<MineClaw.World.BlockPos, int, bool>? IsProtected { get; set; }
    public IReadOnlySet<int> ReservedItems { get; set; } = new HashSet<int>();

    public ConcurrentQueue<string> Calls { get; } = new();
    public List<string> CallList => Calls.ToList();
    /// <summary>Per-action result override (by action name); default ok.</summary>
    public Dictionary<string, Func<ActionResult>> Results { get; } = new();
    /// <summary>Side effects per action name (e.g. smelting adds ingots).</summary>
    public Dictionary<string, Action> Effects { get; } = new();
    public Goal? LastGoal { get; private set; }
    public GotoOptions? LastGotoOptions { get; private set; }
    public Func<ItemStack, bool>? LastDepositFilter { get; private set; }
    public List<string> Said { get; } = new();

    public event Action<string>? ChatReceived;
    public event Action<string>? Kicked;
    public event Action<string>? Disconnected;
    public event Action? Spawned;
    public event Action? Died;
    public event Action? Respawned;
    public event Action<int, int>? HealthChanged;
    public event Action<string>? PlayerJoined;
    public event Action<string>? PlayerLeft;
    public event Action<DangerEvent>? Danger;
    public event Action<int, int>? EntityAnimated;
    public event Action<MineClaw.World.BlockPos, int, int>? BlockAction;

    public void RaiseChat(string line) => ChatReceived?.Invoke(line);
    public void RaiseKicked(string r) => Kicked?.Invoke(r);
    public void RaiseDied() => Died?.Invoke();
    public void RaiseRespawned() => Respawned?.Invoke();
    public event Action<MineClaw.World.Vec3d, MineClaw.World.Vec3d>? ServerMoved;
    public void RaiseServerMoved(MineClaw.World.Vec3d from, MineClaw.World.Vec3d to) => ServerMoved?.Invoke(from, to);
    public void RaiseDanger(DangerEvent d) => Danger?.Invoke(d);
    public void RaiseJoined(string n) => PlayerJoined?.Invoke(n);
    public void RaiseLeft(string n) => PlayerLeft?.Invoke(n);
    public void RaiseSwing(int entityId) => EntityAnimated?.Invoke(entityId, 1);
    public void RaiseBlockAction(MineClaw.World.BlockPos p, int a, int b) => BlockAction?.Invoke(p, a, b);
    public void Touch() { _ = Disconnected; _ = Spawned; _ = Respawned; _ = HealthChanged; _ = PlayerLeft; }

    public string DescribeStatus() => "status-line";
    public string DescribeInventory(bool detail = false, int maxChars = 600) => $"inv detail={detail} max={maxChars}";
    public string DescribeSurroundings(double radius = 12, bool detail = false, int maxChars = 600) => $"look r={radius} detail={detail} max={maxChars}";
    public string DescribeOpenWindow(int maxChars = 600) => "window";

    private Task<ActionResult> Act(string call)
    {
        Calls.Enqueue(call);
        var name = call.Split(' ')[0];
        if (Effects.TryGetValue(name, out var fx)) fx();
        return Task.FromResult(Results.TryGetValue(name, out var r) ? r() : ActionResult.Success(call));
    }

    public Task<ActionResult> SendChatAsync(string text, CancellationToken ct = default) { lock (Said) Said.Add(text); return Task.FromResult(ActionResult.Success("said")); }
    public Task<ActionResult> GotoAsync(Goal goal, GotoOptions? options = null, CancellationToken ct = default)
    {
        LastGoal = goal; LastGotoOptions = options;
        Gotos.Enqueue((goal, options));
        if (GotoGate is { } gate) return GatedGotoAsync(gate, goal, options, ct);
        return Act($"goto {goal.GetType().Name} break={options?.AllowBreak} place={options?.AllowPlace}");
    }
    /// <summary>Every goto (goal, options), in order.</summary>
    public ConcurrentQueue<(Goal Goal, GotoOptions? Options)> Gotos { get; } = new();
    /// <summary>When set, goto waits on it (a walk "in progress"); cancellation fails the goto like the real client.</summary>
    public Func<CancellationToken, Task>? GotoGate { get; set; }
    private async Task<ActionResult> GatedGotoAsync(Func<CancellationToken, Task> gate, Goal goal, GotoOptions? options, CancellationToken ct)
    {
        CurrentAction = "goto";
        try { await gate(ct); }
        catch (OperationCanceledException) { return ActionResult.Fail("cancelled"); }
        finally { CurrentAction = null; }
        return await Act($"goto {goal.GetType().Name} break={options?.AllowBreak} place={options?.AllowPlace}");
    }
    public Task<ActionResult> FollowPlayerAsync(string name, double distance = 3, CancellationToken ct = default) => Act($"follow {name} {distance}");
    public Task<ActionResult> StopAsync(CancellationToken ct = default) => Act("stop");
    public Task<ActionResult> MineAsync(string blockName, int count, double radius = 16, CancellationToken ct = default) => Act($"mine {blockName} {count} {radius}");
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, CancellationToken ct = default) => Act($"generator {x},{y},{z} {count}");
    public Task<ActionResult> HarvestGeneratorAsync(int x, int y, int z, int count, MineClaw.World.BlockPos? stand, CancellationToken ct = default) =>
        stand is { } s ? Act($"generator {x},{y},{z} {count} stand {s.X},{s.Y},{s.Z}") : HarvestGeneratorAsync(x, y, z, count, ct);
    public Task<ActionResult> CollectDropsAsync(double radius = 8, CancellationToken ct = default) => Act($"collect {radius}");
    public Task<ActionResult> CraftAsync(int itemId, int damage = -1, int count = 1, CancellationToken ct = default) => Act($"craft {itemId} {count}");
    public Task<ActionResult> SmeltAsync(int inputItemId, int count, int? fuelItemId = null, bool wait = false, MineClaw.World.BlockPos? furnace = null, CancellationToken ct = default) =>
        Act($"smelt {inputItemId} {count} fuel={fuelItemId} wait={wait} at={furnace?.X},{furnace?.Y},{furnace?.Z}");
    public Task<ActionResult> CollectFurnaceAsync(MineClaw.World.BlockPos? furnace = null, CancellationToken ct = default) => Act($"collect_furnace {furnace}");
    public Task<ActionResult> OpenContainerAsync(int x, int y, int z, CancellationToken ct = default)
    {
        OnOpen?.Invoke(new MineClaw.World.BlockPos(x, y, z));
        return Act($"open {x},{y},{z}");
    }
    public Action<MineClaw.World.BlockPos>? OnOpen { get; set; }
    public Task<ActionResult> ClickSlotAsync(int slot, bool rightClick = false, bool shift = false, CancellationToken ct = default)
    {
        World.Inventory.ApplyClick(slot, rightClick, shift);
        return Act($"click {slot}{(shift ? " shift" : "")}");
    }
    public Task<ActionResult> CloseWindowAsync(CancellationToken ct = default)
    {
        if (World.Inventory.OpenWindow is { } w) { OnClose?.Invoke(w); World.Inventory.Close(w.Id); }
        return Act("close");
    }
    public Action<Window>? OnClose { get; set; }
    public Task<ActionResult> DepositAllAsync(MineClaw.World.BlockPos? container = null, Func<ItemStack, bool>? filter = null, CancellationToken ct = default)
    {
        LastDepositFilter = filter;
        return Act($"deposit {container?.X},{container?.Y},{container?.Z} filter={(filter != null)}");
    }
    public Task<ActionResult> WithdrawAsync(int itemId, int count, MineClaw.World.BlockPos? container = null, int? damage = null, CancellationToken ct = default) =>
        Act($"withdraw {itemId} {count} {container?.X},{container?.Y},{container?.Z} dmg={damage}");
    public Task<ActionResult> PlaceBlockAsync(int x, int y, int z, int itemId, CancellationToken ct = default) => Act($"place {x},{y},{z} {itemId}");
    public Task<ActionResult> DigBlockAsync(int x, int y, int z, CancellationToken ct = default) => Act($"dig {x},{y},{z}");
    public Task<ActionResult> ActivateBlockAsync(int x, int y, int z, CancellationToken ct = default) => Act($"activate {x},{y},{z}");
    public Task<ActionResult> EatAsync(CancellationToken ct = default) => Act("eat");
    public Task<ActionResult> ChopTreeAsync(MineClaw.World.BlockPos? at = null, int? wood = null, CancellationToken ct = default) =>
        Act($"chop at={(at is { } p ? $"{p.X},{p.Y},{p.Z}" : "")} wood={wood}");

    // ---- brainstem body
    /// <summary>Points the head was turned to (FaceIdle), in order.</summary>
    public ConcurrentQueue<Vec3d> Looks { get; } = new();
    public int Swings;
    public bool Safety { get; set; }
    public bool FaceIdle(Vec3d point)
    {
        if (!IsPlaying || CurrentAction != null || Safety) return false;
        Looks.Enqueue(point);
        return true;
    }
    public bool SwingIdle() { if (CurrentAction != null || Safety) return false; Interlocked.Increment(ref Swings); return true; }
    public bool SafetyEngaged => Safety;
    public Task<ActionResult> GestureAsync(string move, string? target = null, int? times = null, CancellationToken ct = default) =>
        Act($"act {move} {target}");

    // ---- world helpers
    public void Floor(int y = 64, int r = 6, int id = Blocks.Stone)
    {
        for (int x = -r; x <= r; x++)
        for (int z = -r; z <= r; z++)
            World.SetBlock(x, y, z, id, 0);
    }

    public void Give(int slot, int id, int count, int damage = 0) => World.Inventory.SetSlot(0, slot, new ItemStack((short)id, (byte)count, (short)damage));
}

/// <summary>LLM that must never be called (tests are offline).</summary>
public sealed class NoLlm : ILlmClient
{
    public int Calls;
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new LlmResponse([new LlmText("")], StopReasons.EndTurn, LlmUsage.Zero, request.Model ?? "claude-haiku-5-5"));
    }
}

public static class TestBot
{
    public static AgentOptions Options()
    {
        var o = new AgentOptions { KnowledgeDirectory = null, IdleCheckInMinutes = 0 };
        o.Chat.LineDelayMs = 0;
        o.Events.CoalesceWindowMs = 10;
        o.Events.MaxBatchDelayMs = 50;
        o.ChatDebounce.QuietMs = 10;
        o.ChatDebounce.MaxMs = 50;
        o.ChatDebounce.ReplyQuietMs = 0;
        return o;
    }

    public static (BotHost Host, FakeGameClient Client) Create(BotOptions? bot = null)
    {
        var c = new FakeGameClient();
        var host = BotHost.Create(c, Options(), bot, new NoLlm(), persist: false);
        host.Bridge.GetType(); // attached in Create
        return (host, c);
    }

    public static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    /// <summary>Runs a tool as the Opus brain would (the caller identity tool policies check, e.g. only the brain writes scripts).</summary>
    public static async Task<Agent.Tools.ToolResult> Run(this BotHost h, string tool, object input)
    {
        using (Agent.Tools.ToolCaller.Push(Agent.Llm.ModelTier.Main, h.Runtime.Options.Llm.MainModel))
            return await h.Runtime.Tools.ExecuteAsync(tool, J(input), CancellationToken.None);
    }

    /// <summary>Runs a tool as another tier/model (worker, cheap, economy-mode brain), or with no model caller when null.</summary>
    public static async Task<Agent.Tools.ToolResult> RunAs(this BotHost h, Agent.Llm.ModelTier? tier, string tool, object input, string? model = null)
    {
        if (tier is not { } t) return await h.Runtime.Tools.ExecuteAsync(tool, J(input), CancellationToken.None);
        using (Agent.Tools.ToolCaller.Push(t, model ?? h.Runtime.Options.Llm.ModelFor(t)))
            return await h.Runtime.Tools.ExecuteAsync(tool, J(input), CancellationToken.None);
    }
}
