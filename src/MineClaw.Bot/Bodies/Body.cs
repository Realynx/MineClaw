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
