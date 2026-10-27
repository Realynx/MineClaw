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

    public void Attach()
    {
        Sub(h => _c.ChatReceived += h, h => _c.ChatReceived -= h, (Action<string>)OnChat);
        Sub(h => _c.Danger += h, h => _c.Danger -= h, (Action<DangerEvent>)OnDanger);
        Sub(h => _c.Died += h, h => _c.Died -= h, (Action)(() => Post(GameEvent.Died())));
        Sub(h => _c.Respawned += h, h => _c.Respawned -= h, (Action)(() => Post(GameEvent.Notice("respawned at spawn", EventPriority.Background))));
        Sub(h => _c.Kicked += h, h => _c.Kicked -= h, (Action<string>)(r => Post(MapKick(r))));
        Sub(h => _c.Disconnected += h, h => _c.Disconnected -= h, (Action<string>)(r => _log.LogInformation("disconnected: {Reason}", r)));
        Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)SyncOnline);
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
        DangerKind.LowHealth => new GameEvent(EventKind.LowHealth, d.Message.Contains("/20") ? d.Message : $"hp {health}/20 {d.Message}",
            priority: d.Critical ? EventPriority.Critical : EventPriority.High),
        DangerKind.Lava => new GameEvent(EventKind.LowHealth, $"hp {health}/20 {d.Message}", priority: EventPriority.High),
        DangerKind.HostileNearby => GameEvent.Notice(d.Message, EventPriority.Low),
        // Survival reflex "hungry, no food" (re-sent only when it gets worse): 1 hungry, 2 starving, 3 food 0.
        DangerKind.Hunger => GameEvent.Notice(d.Message, d.Level >= 3 ? EventPriority.High : d.Level == 2 ? EventPriority.Normal : EventPriority.Low),
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
        if (w.IsPlayerOverVoid(p.Position)) s += " OVER VOID";
        if (c.CurrentAction is { } a) s += " | doing " + a;
        return s;
    }
}
