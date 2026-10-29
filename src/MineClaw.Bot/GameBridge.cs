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

    /// <summary>
    /// One brain, many bodies: a clone body's bridge (its short name, "67"). Its events carry <see cref="GameEvent.Body"/>; the
    /// brain's state header, the session start and the online list stay main's; its client's chat is dropped except whispers
    /// to its account, which reach main's router as a whisper tagged with this body (only main's account speaks).
    /// </summary>
    public string? Body { get; init; }

    private bool IsClone => Body is { Length: > 0 };

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
        Sub(h => _c.Died += h, h => _c.Died -= h, (Action)(() =>
        {
            _died = true;
            _diedAt = _c.World.Player.Position;
            // A deliberate void step (into_void): the death is the plan — context only, no alarm, no grief (no Death event).
            if (_c.ActiveVoidStep is { } step) Post(GameEvent.Notice($"intentional reset ({step.Reason}): died in the void as planned, respawning", EventPriority.Background));
            else Post(GameEvent.Died());
        }));
        Sub(h => _c.ServerMoved += h, h => _c.ServerMoved -= h, (Action<Vec3d, Vec3d>)OnServerMoved);
        Sub(h => _c.Respawned += h, h => _c.Respawned -= h, (Action)OnRespawned);
        Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)(() => Volatile.Write(ref _dim, _c.World.Dimension)));
        Volatile.Write(ref _dim, _c.World.Dimension);
        Sub(h => _c.Kicked += h, h => _c.Kicked -= h, (Action<string>)(r => Post(MapKick(r))));
        Sub(h => _c.Disconnected += h, h => _c.Disconnected -= h, (Action<string>)(r => _log.LogInformation("disconnected: {Reason}", r)));
        if (!IsClone) Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)SyncOnline);
        // Joining the game opens the startup-chatter window once (two lines at most until someone talks to the bot).
        if (IsClone) return; // main's tab list, session window and state header are the brain's
        Sub(h => _c.Spawned += h, h => _c.Spawned -= h, (Action)(() => { if (Interlocked.Exchange(ref _sessionBegun, 1) == 0) _rt.Chat.BeginSession(); }));
        Sub(h => _c.PlayerJoined += h, h => _c.PlayerJoined -= h, (Action<string>)(n => OnPlayerList(n, joined: true)));
        Sub(h => _c.PlayerLeft += h, h => _c.PlayerLeft -= h, (Action<string>)(n => OnPlayerList(n, joined: false)));
        _rt.Events.Posted += OnPosted;
        _unsubscribe.Add(() => _rt.Events.Posted -= OnPosted);
        // The body reflex's OK for brain jobs (night + no food) and its spawn safety before the startup wake.
        _rt.Agent.BodySafety ??= BodySafety;
        if ((_c as MinecraftGameClient)?.Inner is { } mc) _rt.Agent.SafetyFirst ??= ct => SpawnSafetyAsync(mc, ct);
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
            // A respawn moves the body too: say from where (live 10-27: "I somehow ended up back at spawn").
            var where = _diedAt is { } d ? $"server moved me from {Locations.Fmt(d)} (where I died) to spawn {_c.World.Spawn.X},{_c.World.Spawn.Y},{_c.World.Spawn.Z} (respawn)" : null;
            _diedAt = null;
            var text = from == to ? "respawned at spawn" : $"respawned at spawn in the {DimName(to)}";
            Post(GameEvent.Notice(where != null && died ? text + ": " + where : text, EventPriority.Background));
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
        if (d.Kind == DangerKind.Hunger && AutoFeed && _c.IsPlaying && FeedReflex(d)) return;
        Report(d);
    }

    private void Report(DangerEvent d)
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

    // ------------------------------------------------------------------ night + no food

    /// <summary>Jobs fine at night with no food: getting food, the chests, the furnace, the gen by spawn, defending.</summary>
    private static readonly HashSet<string> NightOk = new(StringComparer.OrdinalIgnoreCase)
    {
        "eat", "withdraw", "deposit", "craft", "smelt", "collect_furnace", "organize_chest", "use_block", "collect_drops", "window",
        "harvest_generator", "defend", "guard", "attack", "stop", "look", "status", "inventory",
    };

    /// <summary>A walk within this of spawn stays on home ground (lit, near the chests).</summary>
    public int HomeRadius { get; init; } = 12;

    /// <summary>
    /// The reflex's OK for a new brain body job (<see cref="BotAgent.BodySafety"/>): at night (or with a hostile within 16) and with
    /// no safe food on hand, a job that leaves home ground is refused with one actionable line and the food reflex starts (live
    /// 10-28 16:21: respawned at night with no food, a walk west to the tower floor, low hp, dead again). Null = go ahead.
    /// </summary>
    public string? BodySafety(IReadOnlyList<(string Tool, System.Text.Json.JsonElement Input)> steps)
    {
        if (!_c.IsPlaying) return null;
        var w = _c.World;
        if (w.Dimension != 0) return null;
        bool night = w.IsNight, mobs = w.Entities.Nearby(w.Player.Position, 16, e => e.IsHostileMob && MineClaw.Client.Combat.CombatGeometry.CanGetAt(w, e, w.Player.Position), 1).Count > 0;
        // A clone spawn walks out at the spawn, not at main: only the night counts for it, not the mobs around main.
        bool spawns = steps.Count > 0 && steps.All(s => BotAgent.IsCloneSpawn(s.Tool, s.Input));
        if (spawns) mobs = false;
        if (!night && !mobs) return null;
        if (FoodSupply.HasReady(_c)) return null;
        if (steps.All(s => NightOk.Contains(s.Tool) || s.Tool == "goto" && GoesHome(s.Input, w) || ToNearPlayer(s.Tool, s.Input, w))) return null;
        string what;
        if (Volatile.Read(ref _feeding) == 1) what = "already getting food (reflex)";
        else
        {
            FoodSupply.Outlook look;
            try { look = new FoodSupply(_c).Survey(); }
            catch (Exception ex) { _log.LogDebug(ex, "food survey failed"); look = new FoodSupply.Outlook([], "no food known"); }
            if (look.CanObtain && Interlocked.CompareExchange(ref _feeding, 1, 0) == 0)
            {
                Feeding = Task.Run(FetchFoodAsync);
                what = $"getting food first ({look.Text})";
            }
            else what = $"none to get ({look.Text}): staying in until it's day or someone brings food";
        }
        if (spawns) return $"night + no food: {what}; not spawning clones into the night now (they'd be pinned at once); at day, or once there is food";
        return $"{(night ? "night" : "mobs near")} + no food: {what}; not starting {steps[0].Tool} now (home ground only: eat, chests, gen)";
    }

    /// <summary>A walk to a player within this is defending, escorting or rescuing them (or picking up their drops): never refused.</summary>
    public int PlayerHelpRadius { get; init; } = 24;

    /// <summary>
    /// A goto/follow to a player (by name, or a point next to one) within <see cref="PlayerHelpRadius"/>: hunger only blocks leaving
    /// for work far from players. Live 10-29: Fox, then Robin, were killed by a zombie at a door while main said "sorry besties,
    /// maid-chan's legs got locked again" (its walk to help refused by this gate). At low hp the reflexes' own retreat/shelter
    /// comes first, so the exemption doesn't apply then.
    /// </summary>
    private bool ToNearPlayer(string tool, System.Text.Json.JsonElement input, WorldState w)
    {
        if (tool is not ("goto" or "follow") || input.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
        if (w.Player.Health <= LowHealthLine) return false;
        var me = w.Player.Position;
        string? Prop(string n) => input.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        var to = (tool == "follow" ? Prop("player") : Prop("to"))?.Trim();
        if (string.IsNullOrEmpty(to)) return false;
        if (w.Entities.FindPlayer(to) is { } pe) return pe.Position.DistanceTo(me) <= PlayerHelpRadius;
        if (tool != "goto") return false;
        var parts = to.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var z)) return false;
        var target = new Vec3d(x + 0.5, y, z + 0.5);
        if (target.DistanceTo(me) > PlayerHelpRadius) return false;
        return w.Entities.Nearby(target, 6, e => e.Kind == EntityKind.Player && e.Id != w.Player.EntityId).Count > 0;
    }

    /// <summary>The survival reflex's low line (its retreat/shelter take over at or below it).</summary>
    private const int LowHealthLine = 8;

    /// <summary>A goto whose target is within <see cref="HomeRadius"/> of spawn (x,y,z), or home/spawn/bed by name.</summary>
    private bool GoesHome(System.Text.Json.JsonElement input, WorldState w)
    {
        if (input.ValueKind != System.Text.Json.JsonValueKind.Object || !input.TryGetProperty("to", out var to) || to.ValueKind != System.Text.Json.JsonValueKind.String)
            return false;
        var t = (to.GetString() ?? "").Trim().ToLowerInvariant();
        if (t is "home" or "spawn" or "bed" or "house" or "the house" or "the chest" or "chest") return true;
        var parts = t.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], out var x) || !int.TryParse(parts[2], out var z)) return false;
        var sp = w.Spawn;
        return (x - sp.X) * (x - sp.X) + (z - sp.Z) * (z - sp.Z) <= HomeRadius * HomeRadius;
    }

    /// <summary>Food into the inventory (chests, crafting, cooking), no eating at a full bar: the night gate's reflex.</summary>
    private async Task FetchFoodAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(FeedTimeout);
            var r = await new FoodSupply(_c).ObtainAsync(cts.Token).ConfigureAwait(false);
            Post(GameEvent.Notice(r.Ok ? $"got food (reflex, night): {r.Text}" : $"couldn't get food (reflex, night): {r.Text}", r.Ok ? EventPriority.Background : EventPriority.Normal));
        }
        catch (Exception ex) { _log.LogDebug(ex, "night food fetch failed"); }
        finally { Volatile.Write(ref _feeding, 0); }
    }

    /// <summary>The client's spawn safety (walk home / box in when exposed), once it has spawned: the startup wake waits for it.</summary>
    private static async Task<string?> SpawnSafetyAsync(MinecraftClient mc, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!mc.IsPlaying && DateTime.UtcNow < until) await Task.Delay(200, ct).ConfigureAwait(false);
        return await mc.SpawnSafety.WaitAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ hunger reflex

    /// <summary>
    /// "Hungry, no food" fetches/crafts/cooks food and eats as a brainstem reflex, zero tokens (<see cref="FoodSupply"/>; live
    /// 10-28 13:25 main starved to food 0 asking a clone for bread with 30 wheat in the house chest). Only when nothing can be
    /// had, or the attempt fails, does the danger reach the brain, naming what's there or the real shortfall.
    /// </summary>
    public bool AutoFeed { get; init; } = true;

    /// <summary>How long one reflex meal (walk, withdraw, craft/cook, eat) may take.</summary>
    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromMinutes(3);

    private int _feeding;

    /// <summary>Last reflex meal (tests): its task, null before the first.</summary>
    internal Task? Feeding { get; private set; }

    /// <summary>True = handled by the reflex (no wake now); false = report <paramref name="d"/> (its text may have grown).</summary>
    private bool FeedReflex(DangerEvent d)
    {
        if (Volatile.Read(ref _feeding) == 1)
        {
            Post(GameEvent.Notice(d.Message + "; already fetching food (reflex)", EventPriority.Background));
            return true;
        }
        FoodSupply.Outlook look;
        try { look = new FoodSupply(_c).Survey(); }
        catch (Exception ex) { _log.LogDebug(ex, "food survey failed"); return false; }
        if (!look.CanObtain || Interlocked.CompareExchange(ref _feeding, 1, 0) != 0)
        {
            Report(d with { Message = d.Message + "; " + look.Text });
            return true;
        }
        _log.LogInformation("hunger reflex: {Plan}", look.Text);
        Post(GameEvent.Notice($"{d.Message}; {look.Text}: eating it now (reflex)", EventPriority.Background));
        Feeding = Task.Run(() => FeedAsync(d));
        return true;
    }

    private async Task FeedAsync(DangerEvent d)
    {
        try
        {
            using var cts = new CancellationTokenSource(FeedTimeout);
            var r = await FoodSupply.EatAsync(_c, cts.Token).ConfigureAwait(false);
            if (r.Ok)
            {
                _log.LogInformation("hunger reflex: {Msg}", r.Message);
                Post(GameEvent.Notice($"ate (reflex): {r.Message}", EventPriority.Background));
                return;
            }
            Report(d with { Message = $"{d.Message}; the food reflex failed: {r.Message}" });
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "hunger reflex failed");
            Report(d with { Message = $"{d.Message}; the food reflex failed: {ex.Message}" });
        }
        finally { Volatile.Write(ref _feeding, 0); }
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

    private Vec3d? _diedAt;
    private readonly Queue<(DateTimeOffset At, string Line)> _recentChat = new();

    /// <summary>How far back a chat line can explain a teleport ("tp maid to me", "Teleporting maid_chan to kiwi").</summary>
    public TimeSpan TeleportChatWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A server-forced move (0x0D far from where the body was, not a login/respawn/portal: those announce themselves) becomes a
    /// note for the brain: "server moved me from A to B (teleport)" with the likely cause when visible: a recent tp line in chat
    /// (who asked), a player standing at the destination. Normal priority, never interrupting the body.
    /// </summary>
    private void OnServerMoved(Vec3d from, Vec3d to)
    {
        try
        {
            var text = ServerMoveText(from, to, RecentChat(), _c.Username,
                _c.World.Entities.Nearby(to, 2.5, e => e.Kind == EntityKind.Player).Select(e => e.Name).FirstOrDefault(n => n != null));
            Post(GameEvent.Notice(text, EventPriority.Normal));
        }
        catch (Exception ex) { _log.LogDebug(ex, "server move note failed"); }
    }

    private IReadOnlyList<string> RecentChat()
    {
        var since = _time.GetUtcNow() - TeleportChatWindow;
        lock (_recentChat)
        {
            while (_recentChat.Count > 0 && _recentChat.Peek().At < since) _recentChat.Dequeue();
            return _recentChat.Select(c => c.Line).ToList();
        }
    }

    private static readonly System.Text.RegularExpressions.Regex TpWords = new(@"(?:^|[\s/<>:])(?:tp|tphere|tpa|teleport\w*|summon\w*|warp)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The note text (pure, for tests): "server moved me from A to B (teleport): kiwi asked "tp maid to me" just before".</summary>
    public static string ServerMoveText(Vec3d from, Vec3d to, IReadOnlyList<string> recentChat, string self, string? playerAtDestination)
    {
        var sb = new System.Text.StringBuilder($"server moved me from {Locations.Fmt(from)} to {Locations.Fmt(to)} ({from.DistanceTo(to):0} blocks; teleport)");
        var tp = recentChat.LastOrDefault(l => TpWords.IsMatch(ChatText.StripColours(l)));
        if (tp != null)
        {
            var clean = ChatText.StripColours(tp).Trim();
            var m = PlayerLine.Match(clean);
            sb.Append(m.Success ? $": {m.Groups[1].Value} said \"{Trunc(m.Groups[2].Value)}\" just before" : $": server said \"{Trunc(clean)}\"");
        }
        if (playerAtDestination != null && !playerAtDestination.Equals(self, StringComparison.OrdinalIgnoreCase))
            sb.Append($"; {playerAtDestination} is right here (probably a tp to them)");
        else if (tp == null) sb.Append("; cause not visible (an op's /tp or a plugin)");
        return sb.ToString();
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..57] + "...";

    private void OnChat(string raw)
    {
        lock (_recentChat)
        {
            _recentChat.Enqueue((_time.GetUtcNow(), raw));
            while (_recentChat.Count > 30) _recentChat.Dequeue();
        }
        if (IsClone)
        {
            // Only main's account chats and main's router hears public chat (main sees the same lines): a clone keeps just the
            // whispers to its own account, for the brain, tagged with this body.
            try
            {
                var wm = _rt.Chat.Parse(raw);
                if (wm.Kind == Agent.Chat.ChatKind.Whisper && wm.Player is { Length: > 0 } who && !who.Equals(_c.Username, StringComparison.OrdinalIgnoreCase)
                    && _rt.Chat.IsHiveBot?.Invoke(who) != true && wm.Text.Length > 0)
                    Post(GameEvent.Whisper(who, wm.Text) with { AddressConfidence = 1 });
            }
            catch (Exception ex) { _log.LogDebug(ex, "clone whisper routing failed for {Line}", raw); }
            return;
        }
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
        if (IsClone && e.Body == null) e = e with { Body = Body };
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
        // The reflex is already surfacing: the brain hears why its job stopped (the cause is in the text).
        DangerKind.Drowning => new GameEvent(EventKind.LowHealth, $"hp {health}/20 {d.Message}", priority: EventPriority.High),
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
        var vitals = c.VitalsKnown ? SurvivalReflex.HeaderText(p.Health, p.Food, w.Inventory.PlayerSlots(), c.ReservedItems, null, w.Player.Effects) : "hp/food loading…";
        var s = $"pos {Locations.Fmt(p.Position)} {vitals} held {heldText} {(w.IsNight ? "night" : "day")}";
        if (w.Dimension != 0) s = $"IN THE {DimName(w.Dimension).ToUpperInvariant()} " + s; // never mistaken for the island
        // Breath (both versions): only while the head is under water or the air is short (live 10-29: drowned, never shown).
        if (w.Player.HeadUnderWater || w.Player.Air < Breath.MaxAir) s += " " + Breath.Text(w.Player.Air) + (w.Player.HeadUnderWater ? " HEAD UNDER WATER" : "");
        if (!c.GroundKnown) s += " ground loading…";
        else if (w.IsPlayerOverVoid(p.Position)) s += " OVER VOID";
        if (c.CurrentAction is { } a) s += " | doing " + a;
        return s;
    }
}
