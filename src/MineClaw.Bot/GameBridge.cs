using MineClaw.Agent;
using MineClaw.Agent.Events;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.Client;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Bot;

/// <summary>
/// Wires client events into the agent: chat lines → <c>Chat.OnServerChat</c>; danger/death/kick/joins → prioritised
/// <see cref="GameEvent"/>s; keeps the chat router's online list in sync; supplies the one-line state header.
/// </summary>
public sealed class GameBridge : IDisposable
{
    private readonly IGameClient _c;
    private readonly AgentRuntime _rt;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _joinSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action> _unsubscribe = new();

    /// <summary>Out-of-food reports: once, then only when worse/unacknowledged or at low health (null = every signal wakes).</summary>
    public HungerLatch? Hunger { get; init; }

    private static readonly System.Text.RegularExpressions.Regex PlayerLine = new(@"^<([^>]+)>\s*(.*)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>How long a join/leave from the player list waits for the matching chat line before being posted itself.</summary>
    public TimeSpan JoinDedupeDelay { get; init; } = TimeSpan.FromSeconds(2);

    public GameBridge(IGameClient client, AgentRuntime runtime, ILogger? log = null, TimeProvider? time = null)
    {
        _c = client;
        _rt = runtime;
        _log = log ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
    }

    private int _sessionBegun;

    public void Attach()
    {
        Sub(h => _c.ChatReceived += h, h => _c.ChatReceived -= h, (Action<string>)OnChat);
        Sub(h => _c.Danger += h, h => _c.Danger -= h, (Action<DangerEvent>)OnDanger);
        Sub(h => _c.Died += h, h => _c.Died -= h, (Action)(() => { _died = true; Post(GameEvent.Died()); }));
        Sub(h => _c.Respawned += h, h => _c.Respawned -= h, (Action)OnRespawned);
        Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)(() => Volatile.Write(ref _dim, _c.World.Dimension)));
        Volatile.Write(ref _dim, _c.World.Dimension);
        Sub(h => _c.Kicked += h, h => _c.Kicked -= h, (Action<string>)(r => Post(MapKick(r))));
        Sub(h => _c.Disconnected += h, h => _c.Disconnected -= h, (Action<string>)(r => _log.LogInformation("disconnected: {Reason}", r)));
        Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)SyncOnline);
        // Joining the game opens the startup-chatter window once (two lines at most until someone talks to the bot).
        Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)(() => { if (Interlocked.Exchange(ref _sessionBegun, 1) == 0) _rt.Chat.BeginSession(); }));
        Sub(h => _c.PlayerJoined += h, h => _c.PlayerJoined -= h, (Action<string>)(n => OnPlayerList(n, joined: true)));
        Sub(h => _c.PlayerLeft += h, h => _c.PlayerLeft -= h, (Action<string>)(n => OnPlayerList(n, joined: false)));
        _rt.Events.Posted += OnPosted;
        _unsubscribe.Add(() => _rt.Events.Posted -= OnPosted);
        _rt.Agent.StateHeader = () =>
        {
            if (Hunger is { } h && _c.IsPlaying)
            {
                var p = _c.World.Player;
                h.Observe(p.Food, SurvivalReflex.HasSafeFood(_c.World.Inventory.PlayerSlots(), _c.ReservedItems), EatAtFood());
            }
            return StateHeader(_c);
        };
    }

    // ------------------------------------------------------------------ dimension changes

    private int _dim;
    private volatile bool _died;

    /// <summary>"portal pair recorded: overworld -40,65,6 &lt;-&gt; nether 2,66,0" for the portal at (dim, pos), or null while
    /// the world indexer hasn't linked it yet (BotHost wires it to the portal_links store).</summary>
    public Func<int, Vec3d, string?>? PortalPairText { get; init; }

    /// <summary>How long the dimension-change wake waits for the arrival chunks / the indexer's pair before posting anyway.</summary>
    public TimeSpan ArrivalWait { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan PairWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 0x09 Respawn: after a death it's the respawn at spawn (context only); without one it's a portal trip (b1.8.1 sends
    /// Respawn with the other dimension). Live: the bot walked through a lit portal, got "respawned at spawn" as background
    /// context and told players "I somehow ended up back at spawn" while standing in the nether. A trip is a wake the brain
    /// can't miss (MustWake, normal priority: it never cancels the job that walked in on purpose).
    /// </summary>
    private void OnRespawned()
    {
        int to = _c.World.Dimension;
        int from = Interlocked.Exchange(ref _dim, to);
        bool died = _died;
        _died = false;
        if (died || from == to)
        {
            Post(GameEvent.Notice(from == to ? "respawned at spawn" : $"respawned at spawn in the {DimName(to)}", EventPriority.Background));
            return;
        }
        _ = AnnounceDimensionAsync(from, to);
    }

    private async Task AnnounceDimensionAsync(int from, int to)
    {
        try
        {
            bool Arrived()
            {
                var p = _c.World.Player.Position;
                return _c.World.Dimension == to && _c.IsPlaying && _c.World.IsLoaded((int)Math.Floor(p.X), (int)Math.Floor(p.Z));
            }
            await PollAsync(Arrived, ArrivalWait).ConfigureAwait(false);
            if (_c.World.Dimension != to) return; // already went on/back: that switch announces itself
            var pos = _c.World.Player.Position;
            string? pair = null;
            if (PortalPairText is { } f)
                await PollAsync(() => (pair = SafePair(f, to, _c.World.Player.Position)) != null, PairWait).ConfigureAwait(false);
            if (_c.World.Dimension != to) return;
            var text = $"went through a portal: now in the {DimName(to).ToUpperInvariant()} at {Locations.Fmt(pos)} (from the {DimName(from)}); "
                       + (pair ?? "portal pair not recorded yet") + "; return_portal (or goto the portal block) goes back";
            Post(GameEvent.Notice(text, EventPriority.Normal) with { MustWake = true });
        }
        catch (Exception ex) { _log.LogDebug(ex, "dimension announcement failed"); }
    }

    private static string? SafePair(Func<int, Vec3d, string?> f, int dim, Vec3d pos)
    {
        try { return f(dim, pos); }
        catch (Exception) { return null; }
    }

    private static async Task PollAsync(Func<bool> done, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < until) await Task.Delay(50).ConfigureAwait(false);
    }

    private static string DimName(int dim) => dim switch { -1 => "nether", 0 => "overworld", 1 => "end", _ => "dimension " + dim };

    private int EatAtFood() => (_c as MinecraftGameClient)?.Inner.Survival.Options.EatAtFood ?? new SurvivalOptions().EatAtFood;

    private void OnDanger(DangerEvent d)
    {
        int health = _c.World.Player.Health;
        if (d.Kind == DangerKind.Hunger && Hunger is { } h && !h.ShouldWake(d, health))
        {
            // Already reported (or the player said not to worry): context only, never another begging turn.
            _log.LogDebug("hunger already reported: {Msg}", d.Message);
            Post(GameEvent.Notice(d.Message + " (already reported; don't ask again)", EventPriority.Background));
            return;
        }
        Post(MapDanger(d, health, _c.World.Player.Position.Y));
    }

    private void Sub<T>(Action<T> add, Action<T> remove, T handler)
    {
        add(handler);
        _unsubscribe.Add(() => remove(handler));
    }

    public void Dispose()
    {
        foreach (var u in _unsubscribe) u();
        _unsubscribe.Clear();
    }

    private void OnChat(string raw)
    {
        if (Hunger is { } h && PlayerLine.Match(MineClaw.Client.ChatText.StripColours(raw)) is { Success: true } m
            && !m.Groups[1].Value.Equals(_c.Username, StringComparison.OrdinalIgnoreCase) && _rt.Chat.IsHiveBot?.Invoke(m.Groups[1].Value) != true
            && h.OnChat(m.Groups[2].Value))
            _log.LogInformation("hunger acknowledged by {Player}: {Text}", m.Groups[1].Value, m.Groups[2].Value);
        try { _rt.Chat.OnServerChat(raw); }
        catch (Exception ex) { _log.LogWarning(ex, "chat routing failed for {Line}", raw); }
    }

    private void Post(GameEvent? e)
    {
        if (e == null) return;
        _log.LogDebug("event {Event}", e.Format());
        _rt.Events.Post(e);
    }

    // The chat router posts joins/leaves from "X joined the game" lines; remember them so the player-list signal
    // (which usually arrives too) doesn't duplicate them.
    private void OnPosted(GameEvent e)
    {
        if (e.Kind is EventKind.PlayerJoined or EventKind.PlayerLeft && e.Player != null)
            lock (_joinSeen) _joinSeen[Key(e.Player, e.Kind == EventKind.PlayerJoined)] = _time.GetUtcNow();
    }

    private static string Key(string player, bool joined) => (joined ? "+" : "-") + player;

    private void OnPlayerList(string name, bool joined)
    {
        if (string.Equals(name, _c.Username, StringComparison.OrdinalIgnoreCase)) return;
        SyncOnline();
        if (_rt.Chat.IsHiveBot?.Invoke(name) == true) return; // hive bots logging in/out are not player events
        var started = _time.GetUtcNow();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(JoinDedupeDelay, _time).ConfigureAwait(false); } catch { }
            lock (_joinSeen)
                if (_joinSeen.TryGetValue(Key(name, joined), out var seen) && seen > started - TimeSpan.FromSeconds(10)) return;
            Post(joined ? GameEvent.Joined(name) : GameEvent.Left(name));
        });
    }

    private void SyncOnline()
    {
        var online = _c.World.Entities.OnlinePlayers.Where(n => !string.Equals(n, _c.Username, StringComparison.OrdinalIgnoreCase)).ToList();
        if (online.Count > 0) _rt.Chat.SetOnlinePlayers(online);
    }

    // ------------------------------------------------------------------ pure mappings (tested)

    /// <summary>Danger signal → event. Void/death are critical, lava/low health high, mobs low, the rest background.</summary>
    public static GameEvent MapDanger(DangerEvent d, int health, double y) => d.Kind switch
    {
        DangerKind.FallingIntoVoid => GameEvent.VoidFall(y),
        DangerKind.HighFall => GameEvent.Notice(d.Message, EventPriority.High), // fall damage ahead, not the void
        DangerKind.LowHealth => new GameEvent(EventKind.LowHealth, d.Message.Contains("/20") ? d.Message : $"hp {health}/20 {d.Message}",
            priority: d.Critical ? EventPriority.Critical : EventPriority.High),
        DangerKind.Lava => new GameEvent(EventKind.LowHealth, $"hp {health}/20 {d.Message}", priority: EventPriority.High),
        DangerKind.HostileNearby => GameEvent.Notice(d.Message, EventPriority.Low),
        // Survival reflex "hungry, no food" (re-sent only when it gets worse): 1 hungry, 2 starving, 3 food 0. It wakes the brain
        // but never interrupts a job (live 2026-10-27: "starving" cancelled 67's mining): only hp at the low line makes it urgent.
        DangerKind.Hunger => GameEvent.Notice(d.Message, d.Level >= 3 && health <= HungerLatch.LowHealth ? EventPriority.High
            : d.Level >= 2 ? EventPriority.Normal : EventPriority.Low),
        _ => GameEvent.Notice(d.Message, EventPriority.Background), // Damage: context only
    };

    /// <summary>Kicks ride along as context (the host reconnects; there is nothing to say while offline).</summary>
    public static GameEvent MapKick(string reason) => GameEvent.Notice($"kicked: {reason}; reconnecting", EventPriority.Background);

    /// <summary>"pos 3,65,-2 hp 14/20 food 9 (no food!) held cobblestone x12 day | doing goto" (≈15-20 tokens).</summary>
    public static string StateHeader(IGameClient c)
    {
        var w = c.World;
        var p = w.Player.Snapshot;
        if (!c.IsPlaying) return p.IsDead ? "DEAD (respawning)" : "offline (reconnecting)";
        var held = w.Inventory.HeldItem(p.HeldSlot);
        string heldText = held.IsEmpty ? "nothing" : Names.Item(held.Id, held.Damage) + (held.Count > 1 ? " x" + held.Count : "");
        var vitals = SurvivalReflex.HeaderText(p.Health, p.Food, w.Inventory.PlayerSlots(), c.ReservedItems);
        var s = $"pos {Locations.Fmt(p.Position)} {vitals} held {heldText} {(w.IsNight ? "night" : "day")}";
        if (w.Dimension != 0) s = $"IN THE {DimName(w.Dimension).ToUpperInvariant()} " + s; // never mistaken for the island
        if (w.IsPlayerOverVoid(p.Position)) s += " OVER VOID";
        if (c.CurrentAction is { } a) s += " | doing " + a;
        return s;
    }
}
