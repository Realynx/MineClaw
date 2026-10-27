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
        _world.Entities.SpawnPlayer(900, "poofimafox", new Vec3d(-0.5, 64, -1.5), 0, 0);
        _world.Entities.SpawnPlayer(901, "kiwi", new Vec3d(1.5, 64, 2.5), 0, 0);  // on the plank landing south of the cut-out
        _world.Entities.SpawnMob(902, MobTypes.Zombie, new Vec3d(2.5, 64, -3.5), 0, 0);
        _world.Entities.SpawnMob(904, MobTypes.Creeper, new Vec3d(-3.5, 64, -3.5), 0, 0);
        _world.Entities.SpawnItem(903, new ItemStack(Blocks.Cobblestone, 3, 0), new Vec3d(-1.5, 64, 2.5));
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
        if (t % 8 == 0) _world.Entities.Teleport(902, new Vec3d(2.5 - (t / 8 % 4), 64, -3.5), 0, 0);
    }

    /// <summary>Closed walk loop around the L (16 blocks long, clear of the trunk, chest and bench); bots are spaced along it.</summary>
    private static readonly (double X, double Z)[] Walk = [(-1.5, 1.5), (-1.5, -2.5), (2.5, -2.5), (2.5, -0.5), (-0.5, -0.5), (-0.5, 1.5), (-1.5, 1.5)];

    internal static double WalkLength { get; } = Enumerable.Range(0, Walk.Length - 1)
        .Sum(i => Math.Abs(Walk[i + 1].X - Walk[i].X) + Math.Abs(Walk[i + 1].Z - Walk[i].Z));

    internal static Vec3d WalkAt(double s)
    {
        double t = s % WalkLength;
        if (t < 0) t += WalkLength;
        for (int i = 0; i + 1 < Walk.Length; i++)
        {
            var (ax, az) = Walk[i];
            var (bx, bz) = Walk[i + 1];
            double len = Math.Abs(bx - ax) + Math.Abs(bz - az);
            if (t <= len || i + 2 == Walk.Length)
            {
                double f = len <= 0 ? 0 : Math.Min(1, t / len);
                return new Vec3d(ax + (bx - ax) * f, 64, az + (bz - az) * f);
            }
            t -= len;
        }
        return new Vec3d(Walk[0].X, 64, Walk[0].Z);
    }

    /// <summary>The demo bots' work target (the cobble generator's output block).</summary>
    internal static readonly int[] GenTarget = [-2, 64, 3];

    /// <summary>Straight walk from the feet block to the block next to <paramref name="target"/> (flat world x,y,z triples).</summary>
    internal static int[] PathTo(Vec3d feet, int[] target)
    {
        var c = feet.ToBlockPos();
        int tx = target[0], tz = target[2] - 1;
        var pts = new List<int>();
        int x = c.X, z = c.Z;
        for (int guard = 0; guard < 32 && (x != tx || z != tz); guard++)
        {
            if (x != tx) x += Math.Sign(tx - x); else z += Math.Sign(tz - z);
            pts.Add(x); pts.Add(c.Y); pts.Add(z);
        }
        return pts.ToArray();
    }

    public void Dispose()
    {
        _timer?.Dispose();
        foreach (var b in _bots) b.Dispose();
    }

    /// <summary>
    /// Classic skyblock start: an L of grass on two layers of dirt (6×6 minus the south-east 3×3), an oak tree on the north
    /// arm, chest + furnace + bench on the west arm, a cobble generator (water | cobble | lava in a cobble trough) off the south
    /// end, a plank landing in the cut-out, and a small sand island with a glass-walled cactus to the north via a plank bridge.
    /// </summary>
    private static void BuildWorld(WorldState w)
    {
        var air = new byte[16 * 128 * 16];
        for (int cx = -3; cx <= 2; cx++)
            for (int cz = -3; cz <= 2; cz++)
                w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, air, null, null, null);

        void Set(int x, int y, int z, int id) => w.SetBlock(x, y, z, id, 0);
        for (int x = -3; x <= 2; x++)
            for (int z = -3; z <= 2; z++)
            {
                if (x >= 0 && z >= 0) continue; // the L cut-out
                Set(x, 61, z, Blocks.Dirt); Set(x, 62, z, Blocks.Dirt); Set(x, 63, z, Blocks.Grass);
            }
        // Oak tree on the north arm.
        for (int y = 64; y <= 68; y++) Set(1, y, -2, Blocks.Log);
        for (int x = -1; x <= 3; x++)
            for (int z = -4; z <= 0; z++)
                for (int y = 67; y <= 69; y++)
                {
                    int d = Math.Abs(x - 1) + Math.Abs(z + 2);
                    if ((x == 1 && z == -2 && y < 69) || d + (y - 67) > 3 || (d == 3 && y == 67 && (x + z) % 2 == 0)) continue;
                    Set(x, y, z, Blocks.Leaves);
                }
        Set(1, 70, -2, Blocks.Leaves);
        // Starter kit on the west arm.
        Set(-3, 64, 1, Blocks.Chest);
        Set(-3, 64, -1, Blocks.Furnace); Set(-3, 64, -2, Blocks.Workbench);
        Set(0, 64, -3, Blocks.Torch);
        Set(-1, 64, 1, Blocks.Sapling);
        // Cobble generator off the south end: trough floor + rim, water | cobble | lava.
        for (int x = -4; x <= 0; x++) for (int z = 3; z <= 4; z++) Set(x, 63, z, Blocks.Cobblestone);
        for (int x = -4; x <= 0; x++) Set(x, 64, 4, Blocks.Cobblestone);
        Set(-4, 64, 3, Blocks.Cobblestone); Set(0, 64, 3, Blocks.Cobblestone);
        Set(-3, 64, 3, Blocks.Water); Set(-2, 64, 3, Blocks.Cobblestone); Set(-1, 64, 3, Blocks.Lava);
        // Plank landing in the cut-out and a plank bridge north to the sand island.
        for (int x = 0; x <= 2; x++) Set(x, 63, 2, Blocks.Planks);
        for (int z = -10; z <= -4; z++) Set(1, 63, z, Blocks.Planks);
        for (int x = 0; x <= 3; x++)
            for (int z = -14; z <= -11; z++) { Set(x, 61, z, Blocks.Sand); Set(x, 62, z, Blocks.Sand); }
        Set(2, 63, -12, Blocks.Sand); Set(2, 64, -12, Blocks.Cactus); Set(2, 65, -12, Blocks.Cactus);
        Set(1, 63, -12, Blocks.Glass); Set(3, 63, -12, Blocks.Glass); Set(2, 63, -13, Blocks.Glass); Set(2, 63, -11, Blocks.Glass);
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
    private readonly VoxelCache _voxels = new();

    internal DemoBotSource(DemoHive hive, string name, int index, bool online)
    {
        _hive = hive;
        Name = name;
        Index = index;
        Online = online;
        _phase = index * DemoHive.WalkLength / 5;
        _agent = new AgentOptions();
        _agent.Chat.Username = name;
        _usage = new UsageTracker(_agent.Budget, _agent.Prices, path: null) { Classify = m => _agent.Llm.TierOfModel(m) };
        _telemetry = new PromptTelemetry(_agent);
        _telemetry.AttachUsage(_usage);
        _telemetry.Recorded += r => UsageRecorded?.Invoke(r);
        Position = DemoHive.WalkAt(_phase);
        if (online) AddChat($"{name} joined the game");
    }

    public string Name { get; }
    public int Index { get; }
    public bool Online { get; }
    public Vec3d Position { get; private set; }

    public event Action<PromptUsageRecord>? UsageRecorded;

    internal void Move(int tick) => Position = DemoHive.WalkAt(tick / 10.0 + _phase);

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
            Voxels = full ? _voxels.Get(_hive.World, p, _hive.View, DemoHive.GenTarget, Index == 0 ? DemoHive.PathTo(p, DemoHive.GenTarget) : null) : null,
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
