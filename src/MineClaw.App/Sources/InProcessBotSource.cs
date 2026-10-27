using MineClaw.Agent;
using MineClaw.Agent.Budget;

using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.App.Viewport;
using MineClaw.Bot;
using MineClaw.Bot.Game;

namespace MineClaw.App.Sources;

/// <summary>Builds <see cref="UsageSnapshot"/>s from a usage tracker + telemetry hub (shared by in-process and clone hosts).</summary>
public static class UsageSnapshots
{
    public static UsageSnapshot From(UsageTracker usage, PromptTelemetry? telemetry)
    {
        var (day, month) = usage.SnapshotTotals();
        static IReadOnlyList<TierUsage> Tiers(UsageTotals t) =>
            UsageTracker.TierLabels.Where(l => t.Tiers.ContainsKey(l)).Select(l =>
            {
                var x = t.Tiers[l];
                return new TierUsage(l, x.Usd, x.Calls, x.CacheHitRatio, x.InputTokens + x.CacheReadTokens + x.CacheWriteTokens, x.OutputTokens);
            }).ToArray();
        return new UsageSnapshot
        {
            DailyCapUsd = usage.Options.DailyCapUsd,
            MonthlyCapUsd = usage.Options.MonthlyCapUsd,
            Mode = usage.Mode.ToString(),
            TodayUsd = day.TotalUsd,
            MonthUsd = month.TotalUsd,
            TodayCacheHit = day.CacheHitRatio,
            Today = Tiers(day),
            Month = Tiers(month),
            CallsLastHour = usage.CallsLastHour,
            CallsPerHour = telemetry?.CallsPerHour(24) ?? [],
            Last = telemetry?.Last,
        };
    }
}

/// <summary>The main bot, served from this process: live world copy, runtime, telemetry hub and console commands.</summary>
public sealed class InProcessBotSource : IBotTelemetrySource, IDisposable
{
    private readonly IGameClient _game;
    private readonly AgentRuntime _rt;
    private readonly PromptTelemetry _telemetry;
    private readonly PausableAgent _agent;
    private readonly ConsoleCommands _commands;
    private readonly ViewportOptions _view;
    private readonly CancellationToken _ct;
    private readonly LinkedList<string> _chat = new();
    private readonly object _lock = new();
    private readonly VoxelCache _voxels = new();
    private volatile bool _everPlayed;

    public InProcessBotSource(IGameClient game, AgentRuntime rt, PromptTelemetry telemetry, PausableAgent agent, ConsoleCommands commands,
        ViewportOptions view, CancellationToken ct)
    {
        _game = game;
        _rt = rt;
        _telemetry = telemetry;
        _agent = agent;
        _commands = commands;
        _view = view;
        _ct = ct;
        _game.ChatReceived += OnChat;
        _game.Spawned += OnSpawned;
        _telemetry.Recorded += Forward;
    }

    public string Name => _game.Username;

    public event Action<PromptUsageRecord>? UsageRecorded;

    public BotSnapshot Snapshot(bool full)
    {
        try
        {
            var w = _game.World;
            var p = w.Player.Snapshot;
            bool playing = _game.IsPlaying;
            if (playing) _everPlayed = true;
            var status = playing ? (_agent.Paused ? BotStatus.Paused : BotStatus.Online)
                : p.IsDead ? BotStatus.Dead : BotStatus.Connecting;
            string[] chat;
            lock (_lock) chat = _chat.ToArray();
            var pos = playing || _everPlayed ? new BotPos(p.Position.X, p.Position.Y, p.Position.Z) : (BotPos?)null;
            return new BotSnapshot
            {
                Name = Name,
                Status = status,
                At = DateTimeOffset.UtcNow,
                Pos = pos,
                Dimension = w.Dimension,
                Hp = p.Health,
                Food = p.Food,
                Task = _rt.Activities.Describe() ?? _game.CurrentAction,
                Header = full ? GameBridge.StateHeader(_game) : null,
                BudgetMode = _rt.Usage.Mode.ToString(),
                Thought = full ? _telemetry.Thoughts.Snapshot() : null,
                Usage = UsageSnapshots.From(_rt.Usage, _telemetry),
                Viewport = full && pos != null ? ViewportBuilder.Build(w, p.Position, _view, Name) : null,
                Voxels = full && pos != null ? _voxels.Get(w, p.Position, _view) : null,
                Chat = chat,
            };
        }
        catch (Exception)
        {
            return BotSnapshot.Offline(Name);
        }
    }

    public async Task<bool> SendAsync(string line, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ct);
        await _commands.ExecuteAsync(line, linked.Token).ConfigureAwait(false);
        return true;
    }

    private void OnChat(string line)
    {
        lock (_lock)
        {
            _chat.AddLast(DateTime.Now.ToString("HH:mm:ss ") + MineClaw.Client.ChatText.StripColours(line));
            while (_chat.Count > 100) _chat.RemoveFirst();
        }
    }

    private void OnSpawned() => _everPlayed = true;

    private void Forward(PromptUsageRecord r) => UsageRecorded?.Invoke(r);

    public void Dispose()
    {
        _game.ChatReceived -= OnChat;
        _game.Spawned -= OnSpawned;
        _telemetry.Recorded -= Forward;
    }
}
