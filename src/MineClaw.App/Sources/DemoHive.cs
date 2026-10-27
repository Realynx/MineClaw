using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Viewport;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Sources;

/// <summary>
/// Offline demo for <c>--demo</c>: one small Skyblock world shared by a few fake bots that wander, chat and "spend" tokens.
/// The fake calls go through the real <see cref="PromptTelemetry"/>, <see cref="UsageTracker"/> and <see cref="ViewportBuilder"/>
/// paths; nothing touches the network.
/// </summary>
public sealed class DemoHive : IDisposable
{
    private readonly WorldState _world = new();
    private readonly List<DemoBotSource> _bots = new();
    private readonly Random _rng = new(1881);
    private readonly ViewportOptions _view;
    private Timer? _timer;
    private int _tick;

    public DemoHive(IReadOnlyList<string> names, int online, ViewportOptions? view = null)
    {
        _view = view ?? new ViewportOptions();
        BuildWorld(_world);
        for (int i = 0; i < names.Count; i++)
            _bots.Add(new DemoBotSource(this, names[i], i, online: i < online));
        // Humans and mobs.
        _world.Entities.SpawnPlayer(900, "poofimafox", new Vec3d(-1.5, 64, 3.5), 0, 0);
        _world.Entities.SpawnPlayer(901, "kiwi", new Vec3d(4.5, 64, -1.5), 0, 0);
        _world.Entities.SpawnMob(902, MobTypes.Zombie, new Vec3d(9.5, 64, 9.5), 0, 0);
        _world.Entities.SpawnItem(903, new ItemStack(Blocks.Cobblestone, 3, 0), new Vec3d(5.5, 64, 4.5));
        foreach (var b in _bots.Where(b => b.Online)) _world.Entities.SpawnPlayer(1000 + b.Index, b.Name, b.Position, 0, 0);
    }

    public IReadOnlyList<IBotTelemetrySource> Sources => _bots;
    internal WorldState World => _world;
    internal ViewportOptions View => _view;
    internal Random Rng => _rng;

    public void Start() => _timer ??= new Timer(_ => { try { Tick(); } catch (Exception) { } }, null, 250, 250);

    /// <summary>One simulation step (250 ms of demo time): bots move; every ~4 s each online bot makes a fake call.</summary>
    public void Tick()
    {
        int t = Interlocked.Increment(ref _tick);
        foreach (var b in _bots.Where(b => b.Online))
        {
            b.Move(t);
            _world.Entities.Teleport(1000 + b.Index, b.Position, 0, 0);
            if ((t + b.Index * 5) % 16 == 0) b.FakeCall(t);
        }
        if (t % 8 == 0) _world.Entities.Teleport(902, new Vec3d(9.5 - (t / 8 % 6), 64, 9.5), 0, 0);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var b in _bots) b.Dispose();
    }

    /// <summary>Main island (grass on dirt, tree, chest, furnace, bench, cobble generator) plus a sand island to the north.</summary>
    private static void BuildWorld(WorldState w)
    {
        var air = new byte[16 * 128 * 16];
        for (int cx = -3; cx <= 2; cx++)
            for (int cz = -3; cz <= 2; cz++)
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, air, null, null, null);

        void Set(int x, int y, int z, int id) => w.SetBlock(x, y, z, id, 0);
        for (int x = -3; x <= 6; x++)
            for (int z = -3; z <= 6; z++)
            {
                if (x > 2 && z > 2) continue; // the L cut-out
                Set(x, 61, z, Blocks.Dirt); Set(x, 62, z, Blocks.Dirt); Set(x, 63, z, Blocks.Grass);
            }
        for (int y = 64; y <= 68; y++) Set(1, y, 1, Blocks.Log);
        for (int x = -1; x <= 3; x++)
            for (int z = -1; z <= 3; z++)
                for (int y = 67; y <= 69; y++)
                    if ((x != 1 || z != 1 || y == 69) && Math.Abs(x - 1) + Math.Abs(z - 1) + (y - 67) <= 4) Set(x, y, z, Blocks.Leaves);
        Set(-3, 64, 0, Blocks.Chest); Set(-3, 64, 1, Blocks.Chest);
        Set(0, 64, -3, Blocks.Furnace); Set(-1, 64, -3, Blocks.Workbench);
        Set(4, 64, -2, Blocks.Torch);
        // Cobble generator on the east arm.
        Set(5, 63, 0, Blocks.Cobblestone); Set(5, 63, -1, Blocks.Cobblestone); Set(5, 63, 1, Blocks.Cobblestone);
        Set(6, 64, -1, Blocks.Water); Set(6, 64, 1, Blocks.Lava); Set(6, 64, 0, Blocks.Cobblestone);
        Set(7, 63, -1, Blocks.Cobblestone); Set(7, 63, 0, Blocks.Cobblestone); Set(7, 63, 1, Blocks.Cobblestone);
        // Sand island to the north.
        for (int x = 0; x <= 3; x++)
            for (int z = -14; z <= -11; z++) { Set(x, 61, z, Blocks.Sand); Set(x, 62, z, Blocks.Sand); }
        Set(1, 63, -12, Blocks.Cactus); Set(1, 64, -12, Blocks.Cactus);
        // A bridge of planks back to the main island.
        for (int z = -10; z <= -4; z++) Set(1, 63, z, Blocks.Planks);
        // Stone platform.
        for (int x = -2; x <= 0; x++) for (int z = 4; z <= 6; z++) Set(x, 63, z, Blocks.Stone);
    }
}

/// <summary>A fake hive member for the demo.</summary>
public sealed class DemoBotSource : IBotTelemetrySource, IDisposable
{
    private static readonly string[] Tasks = ["idle", "delegate #4 31s", "goto #7 6s", "follow poofimafox", "delegate #9 102s"];
    private static readonly string[] Asks =
    [
        "<poofimafox> can you top up the cobble chest",
        "<kiwi> claude where did we put the saplings",
        "[task ok] ok: mined 16 cobblestone, deposited in chest by the tree",
        "<poofimafox> nice, thanks!",
    ];

    private readonly DemoHive _hive;
    private readonly AgentOptions _agent;
    private readonly UsageTracker _usage;
    private readonly PromptTelemetry _telemetry;
    private readonly LinkedList<string> _chat = new();
    private readonly object _lock = new();
    private readonly double _phase;

    internal DemoBotSource(DemoHive hive, string name, int index, bool online)
    {
        _hive = hive;
        Name = name;
        Index = index;
        Online = online;
        _phase = index * 2.1;
        _agent = new AgentOptions();
        _agent.Chat.Username = name;
        _usage = new UsageTracker(_agent.Budget, _agent.Prices, path: null) { Classify = m => _agent.Llm.TierOfModel(m) };
        _telemetry = new PromptTelemetry(_agent);
        _telemetry.AttachUsage(_usage);
        _telemetry.Recorded += r => UsageRecorded?.Invoke(r);
        Position = new Vec3d(0.5 + index, 64, 0.5);
        if (online) AddChat($"{name} joined the game");
    }

    public string Name { get; }
    public int Index { get; }
    public bool Online { get; }
    public Vec3d Position { get; private set; }

    public event Action<PromptUsageRecord>? UsageRecorded;

    internal void Move(int tick)
    {
        double a = tick / 12.0 + _phase;
        Position = new Vec3d(1.5 + Math.Cos(a) * (2 + Index), 64, 1.5 + Math.Sin(a) * (2 + Index % 2));
    }

    public BotSnapshot Snapshot(bool full)
    {
        if (!Online) return BotSnapshot.Offline(Name);
        string[] chat;
        lock (_lock) chat = _chat.ToArray();
        var p = Position;
        return new BotSnapshot
        {
            Name = Name,
            Status = BotStatus.Online,
            At = DateTimeOffset.UtcNow,
            Pos = new BotPos(p.X, p.Y, p.Z),
            Hp = 20 - Index * 3,
            Food = 18 - Index,
            Task = Tasks[(int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond / 7 + Index) % Tasks.Length],
            Header = $"pos {new BotPos(p.X, p.Y, p.Z)} hp {20 - Index * 3} food {18 - Index} held stone_pickaxe day",
            BudgetMode = _usage.Mode.ToString(),
            Thought = full ? _telemetry.Thoughts.Snapshot() : null,
            Usage = UsageSnapshots.From(_usage, _telemetry),
            Viewport = full ? ViewportBuilder.Build(_hive.World, p, _hive.View, Name) : null,
            Chat = chat,
        };
    }

    public Task<bool> SendAsync(string line, CancellationToken ct)
    {
        if (!Online) return Task.FromResult(false);
        AddChat(line.StartsWith('/') ? $"(demo) {Name} ignores command {line}" : $"<{Name}> {line}");
        return Task.FromResult(true);
    }

    /// <summary>One fake brain turn: turn input → tool call → result → reply, with a calibrated section breakdown.</summary>
    internal void FakeCall(int tick)
    {
        var rng = _hive.Rng;
        var ask = Asks[(tick / 16 + Index) % Asks.Length];
        AddChat(ask);
        var turn = new TurnInput(DateTimeOffset.UtcNow, ModelTier.Main, $"pos {new BotPos(Position.X, Position.Y, Position.Z)} hp 20 food 18 | task: idle",
            "[since last turn] kiwi placed 4 torches near the bridge", "[memory] #12 chest by the tree: cobblestone x210, saplings x5",
            "[chat] " + ask, "");
        _telemetry.OnTurn(turn with { Text = turn.Header + "\n" + turn.Events });

        var input = JsonSerializer.SerializeToElement(new { task = "mine 16 cobblestone at the generator and deposit", done_when = "16 more in the chest" });
        _telemetry.Thoughts.OnBrainResponse(new LlmResponse([new LlmText("on it!"), new LlmToolUse("tu_" + tick, "delegate", input)],
            StopReasons.ToolUse, LlmUsage.Zero, _agent.Llm.MainModel));
        _telemetry.Thoughts.OnWorkerRound(tick % 5, new LlmRequest { Messages = [LlmMessage.User("task: mine 16 cobblestone")] });
        _telemetry.Thoughts.OnBrainRequest(new LlmRequest
        {
            Messages = [LlmMessage.User(new LlmToolResult("tu_" + tick, "ok: mined 16 cobblestone (inv 16), deposited at chest 2"))],
        });
        _telemetry.Thoughts.OnBrainResponse(new LlmResponse([new LlmText("done, chest's topped up :)")], StopReasons.EndTurn, LlmUsage.Zero, _agent.Llm.MainModel));
        if (tick % 48 == 0) _telemetry.Thoughts.OnCompacted("players asked for cobble; generator east of the tree works");
        AddChat($"<{Name}> on it!");

        var (tier, model, purpose) = (tick / 16 % 3) switch
        {
            0 => (ModelTier.Main, _agent.Llm.MainModel, PromptPurpose.ChatTurn),
            1 => (ModelTier.Worker, _agent.Llm.WorkerModel, PromptPurpose.DelegateRound),
            _ => (ModelTier.Cheap, _agent.Llm.CheapModel, PromptPurpose.AskMemory),
        };
        long prompt = 14_000 + rng.Next(0, 12_000);
        long read = (long)(prompt * (0.6 + rng.NextDouble() * 0.35)), write = rng.Next(0, 1500), fresh = prompt - read - write;
        var usage = new LlmUsage(fresh, 80 + rng.Next(0, 300), read, write);
        var est = new Dictionary<string, int>
        {
            [PromptSection.Tools] = 2600, [PromptSection.System] = 2900, [PromptSection.Knowledge] = 1400, [PromptSection.Identity] = 260,
            [PromptSection.History] = 6000 + rng.Next(0, 6000), [PromptSection.Memory] = 240, [PromptSection.Digest] = 90,
            [PromptSection.Events] = 120, [PromptSection.TurnTools] = rng.Next(0, 900),
        };
        decimal cost = _usage.Record(model, usage, tier);
        _telemetry.Publish(new PromptUsageRecord
        {
            Ts = DateTimeOffset.UtcNow, Bot = Name, Tier = UsageTracker.TierLabel(tier), Model = model, Purpose = purpose, Round = tick % 3,
            InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, CacheReadTokens = usage.CacheReadTokens,
            CacheWriteTokens = usage.CacheWriteTokens, PromptTokens = prompt, CostUsd = cost, CacheHit = UsageTracker.CacheHit(usage),
            Sections = PromptSections.Calibrate(est, prompt), EstimatedTokens = est.Values.Sum(), ContextLimit = 100_000,
            ContextPct = (prompt + 1200) / 100_000.0, MaxOutputTokens = 1200, LatencyMs = 600 + rng.Next(0, 2400),
            StopReason = StopReasons.EndTurn, ToolCalls = purpose == PromptPurpose.ChatTurn ? ["delegate"] : [],
        });
    }

    private void AddChat(string line)
    {
        lock (_lock)
        {
            _chat.AddLast(DateTime.Now.ToString("HH:mm:ss ") + line);
            while (_chat.Count > 60) _chat.RemoveFirst();
        }
    }

    public void Dispose() => _telemetry.Dispose();
}
