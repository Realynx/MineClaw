using MineClaw.Agent.Human;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Scripting;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// The concrete body. Identity, connection and the activity slot are fixed at construction (the routing client needs them
/// before the tools exist); <see cref="BodyFactory.Attach"/> fills in the per-body behaviour afterwards.
/// </summary>
public sealed class Body : IBody
{
    public Body(string name, bool isMain, IGameClient adapter, IGameClient game, ActivityManager activities, StandingOrders? orders = null)
    {
        Name = name;
        IsMain = isMain;
        Adapter = adapter;
        Game = game;
        Client = adapter is MinecraftGameClient mg ? mg.Inner : null;
        Activities = activities;
        Orders = orders;
        Short = ShortName(name, isMain);
    }

    public string Name { get; }
    public string Short { get; }
    public bool IsMain { get; }

    public MinecraftClient? Client { get; }
    public IGameClient Adapter { get; }
    public IGameClient Game { get; }

    public ActivityManager Activities { get; }
    public StandingOrders? Orders { get; }
    public ScriptController? Scripts => ScriptBridge?.Controller;
    /// <summary>The Bot-layer adapter of <see cref="Scripts"/> (tool, gates, events).</summary>
    public GameTools.ScriptBridge? ScriptBridge { get; internal set; }

    public GameBridge? Bridge { get; internal set; }
    public Brainstem.BrainstemController? Brainstem { get; internal set; }
    public Brainstem.Gestures? Gestures { get; internal set; }
    public Brainstem.StallWatchdog? Watchdog { get; internal set; }
    public Brainstem.HandoverWatch? Handovers { get; internal set; }
    internal System.Threading.Timer? HandoverPump { get; set; }
    public HungerLatch? Hunger => Bridge?.Hunger;

    /// <summary>Chat → reaction/action latency for this body (main: the runtime's tracker; a clone: its own).</summary>
    public Agent.Telemetry.LatencyTracker? Latency { get; internal set; }
    /// <summary>A clone's contributor indexer (container snapshots, block changes, gen regen); main's full one is the brain's.</summary>
    public WorldIndex.WorldIndexer? Indexer { get; internal set; }
    /// <summary>This body's remembered world (chunk columns into the shared db, pumped by the one shared flusher).</summary>
    public WorldIndex.WorldMemory? WorldMemory { get; internal set; }
    /// <summary>When this body last logged in (the slot yield: among equals, the most recently started goes first).</summary>
    public DateTimeOffset Started { get; internal set; }
    /// <summary>Per-body wiring torn down with the body (timers, subscriptions).</summary>
    internal List<IDisposable> Owned { get; } = new();

    /// <summary>
    /// Runs a saved script by name on this body's own lane (step 2: <c>hive spawn{script}</c> on an in-process clone; the
    /// brain's script tool stays main's). <paramref name="argsJson"/> is the script's args object, or null.
    /// </summary>
    public async Task<ToolResult> RunScriptAsync(string name, string? argsJson, CancellationToken ct = default)
    {
        if (ScriptBridge is not { } lane) return ToolResult.Error("this body has no script lane");
        var input = new Dictionary<string, object?> { ["op"] = "run", ["name"] = name };
        if (!string.IsNullOrWhiteSpace(argsJson)) input["args"] = System.Text.Json.JsonDocument.Parse(argsJson).RootElement.Clone();
        using (BodyScope.Push(this))
            return await lane.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(input), ct).ConfigureAwait(false);
    }

    /// <summary>Set by the runner (spawn/yield/retire); null = derived from the connection.</summary>
    public BodyState? StateOverride { get; set; }
    public BodyState State => StateOverride ?? (Adapter.IsPlaying ? BodyState.Online : BodyState.Offline);

    public double Progress => Scripts?.Current?.ProgressPct is { } p ? Math.Clamp(p / 100.0, 0, 1) : 0;

    /// <summary>"maid_chan_67" → "67"; the main body → "main"; a name without a number suffix → itself.</summary>
    public static string ShortName(string name, bool isMain)
    {
        if (isMain) return "main";
        int i = name.LastIndexOf('_');
        return i >= 0 && i < name.Length - 1 && name[(i + 1)..].All(char.IsDigit) ? name[(i + 1)..] : name;
    }

    public override string ToString() => $"{Name} ({Short})";
}
