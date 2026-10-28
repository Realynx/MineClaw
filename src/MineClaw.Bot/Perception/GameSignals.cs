using System.Text.Json.Nodes;
using MineClaw.Bot.Demo;
using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Perception;

/// <summary>One typed game event (<see cref="Scripting.Events.GameEventCatalog"/>): its name, payload and the player it is about.</summary>
public sealed record GameSignal(string Name, JsonObject Data, string? Player, DateTimeOffset At)
{
    public string Text => Name + " " + Data.ToJsonString();

    /// <summary>The script engine's form of it (a Signal event whose topic is the name, the payload as <c>Data</c>).</summary>
    public Scripting.ScriptEvent ToScriptEvent() =>
        new(Scripting.ScriptEventKind.Signal, Data.ToJsonString(), Player, Name) { Data = (JsonObject)Data.DeepClone() };
}

/// <summary>
/// The broad, typed signal set, derived from what the client observes (zero tokens, no model): player punches (b1.8.1 has no
/// dig packet for other players, so an arm swing + a raycast along their look, debounced; 1.12.2's Block Break Animation feeds
/// <see cref="OnBreakAnimation"/>), swings, hurt/died (entity status), pickups (collect item), tosses and drops (item spawns),
/// equipment, sneaking, proximity, chat with its intent, being looked at, block changes with who did it, hostile mobs coming
/// near, my own hurt/low hp, dusk/dawn and weather. Raised synchronously on the observing thread: listeners (scripts, the
/// binding runner) must be quick. Inputs are public so tests can feed fake packets.
/// </summary>
public sealed class GameSignals : IDisposable
{
    private readonly IGameClient _c;
    private readonly Func<string, bool> _isBot;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, (BlockPos Pos, DateTimeOffset At)> _punch = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _swung = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _sneak = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _near = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Key, DateTimeOffset Since, bool Fired)> _gaze = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _mobs = new();
    private readonly List<(BlockPos Pos, string Player, DateTimeOffset At)> _touched = new();
    private int _lastHp = -1;
    private bool _low;
    private long _lastTime = -1;
    private bool? _rain;
    private Action? _detach;

    /// <summary>A punch at the same block by the same player within this is one event (digging swings repeat).</summary>
    public TimeSpan PunchDebounce { get; init; } = TimeSpan.FromSeconds(1.5);
    /// <summary>player.near / player.leave radius; mob.near radius.</summary>
    public double NearBlocks { get; init; } = 6;
    public double MobBlocks { get; init; } = 8;
    /// <summary>How long a gaze must rest on me (or a block) before player.lookAt fires.</summary>
    public TimeSpan GazeHold { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Every signal, as it is observed.</summary>
    public event Action<GameSignal>? Raised;

    /// <summary>The intent of a chat line (ChatIntent with the bot's names); null = "banter".</summary>
    public Func<string, string>? Intent { get; init; }

    public GameSignals(IGameClient client, Func<string, bool>? isBot = null, TimeProvider? time = null)
    {
        _c = client;
        _isBot = isBot ?? (_ => false);
        _time = time ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _time.GetUtcNow();
    private WorldState W => _c.World;

    /// <summary>Wires the client's events (swings, status, pickups, equipment, item spawns, block changes, health, joins).</summary>
    public void Attach()
    {
        Action<int, int> anim = (e, a) => Safe(() => OnAnimation(e, a));
        Action<int, int> status = (e, s) => Safe(() => OnStatus(e, s));
        Action<int, int, ItemStack?> collect = (c, i, st) => Safe(() => OnCollect(c, i, st));
        Action<int, int, int, int> equip = (e, slot, id, dmg) => Safe(() => OnEquip(e, slot, id));
        Action<Entity> spawned = e => Safe(() => OnSpawn(e));
        Action<BlockChange> changed = bc => Safe(() => OnBlockChanged(bc.Pos, bc.OldId, bc.NewId));
        Action<int, int> health = (hp, _) => Safe(() => OnHealth(hp));
        Action<string> joined = p => Safe(() => OnJoin(p)), left = p => Safe(() => OnQuit(p));
        _c.EntityAnimated += anim;
        _c.EntityStatus += status;
        _c.ItemCollected += collect;
        _c.EquipmentChanged += equip;
        W.Entities.Spawned += spawned;
        W.BlockChanged += changed;
        _c.HealthChanged += health;
        _c.PlayerJoined += joined;
        _c.PlayerLeft += left;
        _detach = () =>
        {
            _c.EntityAnimated -= anim; _c.EntityStatus -= status; _c.ItemCollected -= collect; _c.EquipmentChanged -= equip;
            W.Entities.Spawned -= spawned; W.BlockChanged -= changed; _c.HealthChanged -= health; _c.PlayerJoined -= joined; _c.PlayerLeft -= left;
        };
    }

    public void Dispose() => _detach?.Invoke();

    private static void Safe(Action a) { try { a(); } catch (Exception) { /* perception is best-effort */ } }

    private void Raise(string name, string? player, JsonObject data)
    {
        if (player != null) data["player"] = player;
        var s = new GameSignal(name, data, player, Now);
        try { Raised?.Invoke(s); } catch (Exception) { }
    }

    private Entity? OtherPlayer(int entityId) =>
        W.Entities.Get(entityId) is { Kind: EntityKind.Player, Name: { Length: > 0 } n } e && !IsMe(n) && !_isBot(n) ? e : null;

    private bool IsMe(string name) => name.Equals(_c.Username, StringComparison.OrdinalIgnoreCase);

    /// <summary>"stone_pickaxe", "cobblestone", "empty" (snake case, like the scripts' item names).</summary>
    public static string ItemName(int id)
    {
        if (id <= 0) return "empty";
        if (Items.Exists(id)) return Snake(Items.Get(id).Name);
        if (Blocks.Exists(id)) return Snake(Blocks.Get(id).Name);
        return "item_" + id;
    }

    private static string Snake(string s) => s.Trim().ToLowerInvariant().Replace(' ', '_');

    private static JsonObject Pos(BlockPos p) => new() { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };
    private static JsonObject Pos(Vec3d p) => new() { ["x"] = Math.Floor(p.X), ["y"] = Math.Floor(p.Y), ["z"] = Math.Floor(p.Z) };

    // ------------------------------------------------------------------ packets

    /// <summary>0x12 animation: 1 swing (+ the punch inference), 2 hurt, 104 crouch, 105 uncrouch.</summary>
    public void OnAnimation(int entityId, int animation)
    {
        switch (animation)
        {
            case 1: OnSwing(entityId); break;
            case 2: OnStatus(entityId, 2); break;
            case 104: Sneak(entityId, true); break;
            case 105: Sneak(entityId, false); break;
        }
    }

    /// <summary>
    /// An arm swing: player.swing, and player.punchBlock when the swing is a punch (not a use-item) and their look ray hits a
    /// solid block in reach; the same block again within <see cref="PunchDebounce"/> is the same punch (digging).
    /// </summary>
    public void OnSwing(int entityId)
    {
        if (OtherPlayer(entityId) is not { } e) return;
        var now = Now;
        lock (_lock) _swung[e.Name!] = now;
        var held = ItemName(e.HeldItem);
        Raise("player.swing", e.Name, new JsonObject { ["held"] = held });
        if (Gestures.IsUseItem(e.HeldItem)) return;
        if (DemonstrationRecorder.LookTarget(W, e.Position, e.Yaw, e.Pitch, reach: 4.5, solidOnly: true) is not { } t) return;
        Punch(e.Name!, t.Pos, t.Id, held, now);
    }

    /// <summary>1.12.2's Block Break Animation (0x08): who is breaking which block (stage 0-9; -1 = stopped).</summary>
    public void OnBreakAnimation(int entityId, BlockPos pos, int stage)
    {
        if (stage < 0 || OtherPlayer(entityId) is not { } e) return;
        Punch(e.Name!, pos, W.GetBlock(pos), ItemName(e.HeldItem), Now);
    }

    private void Punch(string player, BlockPos pos, int blockId, string held, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_punch.TryGetValue(player, out var last) && last.Pos == pos && now - last.At < PunchDebounce)
            {
                _punch[player] = (pos, now); // still digging the same block
                return;
            }
            _punch[player] = (pos, now);
            _touched.Add((pos, player, now));
            _touched.RemoveAll(t => now - t.At > TimeSpan.FromSeconds(3));
        }
        Raise("player.punchBlock", player, new JsonObject { ["pos"] = Pos(pos), ["block"] = ItemName(blockId), ["held"] = held });
    }

    /// <summary>0x26 entity status: 2 hurt (by = the nearest attacker facing them), 3 dead.</summary>
    public void OnStatus(int entityId, int status)
    {
        if (OtherPlayer(entityId) is not { } e) return;
        if (status == 3) { Raise("player.died", e.Name, new JsonObject { ["cause"] = "unknown" }); return; }
        if (status != 2) return;
        var (by, cause) = Attacker(e.Position, e.Name!);
        var data = new JsonObject { ["cause"] = cause };
        if (by != null) data["by"] = by;
        Raise("player.hurt", e.Name, data);
    }

    /// <summary>Nearest player who swung in the last 0.8 s within 4 blocks (melee), else the nearest hostile mob within 4 (mob).</summary>
    private (string? By, string Cause) Attacker(Vec3d at, string? victim)
    {
        var now = Now;
        var near = W.Entities.Nearby(at, 4, x => x.Kind is EntityKind.Player or EntityKind.Mob).OrderBy(x => x.Position.DistanceTo(at)).ToList();
        foreach (var p in near.Where(x => x.Kind == EntityKind.Player && x.Name is { Length: > 0 } n && !n.Equals(victim, StringComparison.OrdinalIgnoreCase)))
            lock (_lock) if (_swung.TryGetValue(p.Name!, out var s) && now - s < TimeSpan.FromSeconds(0.8)) return (p.Name, "melee");
        if (near.FirstOrDefault(x => x.IsHostileMob) is { } m) return (m.DisplayName.ToLowerInvariant(), "mob");
        return (null, "unknown");
    }

    /// <summary>0x16 collect item: a player (not me) picked an item entity up.</summary>
    public void OnCollect(int collector, int itemEntity, ItemStack? stack)
    {
        if (OtherPlayer(collector) is not { } e) return;
        var st = stack is { IsEmpty: false } s ? s : W.Entities.Get(itemEntity)?.Item ?? default;
        if (st.IsEmpty) return;
        Raise("player.pickup", e.Name, new JsonObject { ["item"] = ItemName(st.Id), ["count"] = (int)st.Count });
    }

    /// <summary>0x05 entity equipment (slot 0 = the held item).</summary>
    public void OnEquip(int entityId, int slot, int itemId)
    {
        if (OtherPlayer(entityId) is not { } e) return;
        Raise("player.equip", e.Name, new JsonObject { ["slot"] = slot == 0 ? "held" : "armor" + slot, ["item"] = ItemName(itemId) });
    }

    /// <summary>An entity spawned: an item is item.dropped, and player.toss when it appears at a player's eye height / just ahead of them.</summary>
    public void OnSpawn(Entity e)
    {
        if (e.Kind != EntityKind.Item || e.Item.IsEmpty) return;
        var item = ItemName(e.Item.Id);
        Raise("item.dropped", null, new JsonObject { ["pos"] = Pos(e.Position), ["item"] = item, ["count"] = (int)e.Item.Count });
        foreach (var p in W.Entities.Nearby(e.Position, 2.5, x => x.Kind == EntityKind.Player && x.Name is { Length: > 0 }).OrderBy(x => x.Position.DistanceTo(e.Position)))
        {
            if (IsMe(p.Name!) || _isBot(p.Name!)) continue;
            double eyeY = p.Position.Y + PlayerPhysics.EyeHeight;
            if (Math.Abs(e.Position.Y - (eyeY - 0.3)) > 0.8) continue;
            Raise("player.toss", p.Name, new JsonObject { ["item"] = item, ["count"] = (int)e.Item.Count, ["pos"] = Pos(e.Position) });
            break;
        }
    }

    /// <summary>A block changed: block.changed, with by = the player whose punch/use hit it in the last 2 s.</summary>
    public void OnBlockChanged(BlockPos pos, int from, int to)
    {
        if (from == to) return;
        string? by = null;
        var now = Now;
        lock (_lock)
            by = _touched.LastOrDefault(t => t.Pos == pos && now - t.At < TimeSpan.FromSeconds(2)).Player;
        var data = new JsonObject { ["pos"] = Pos(pos), ["from"] = ItemName(from), ["to"] = to == 0 ? "air" : ItemName(to) };
        if (by != null) data["by"] = by;
        Raise("block.changed", by, data);
    }

    /// <summary>A chat line from a player (any line, addressed or not): player.say with its intent.</summary>
    public void OnChat(string player, string text)
    {
        if (string.IsNullOrWhiteSpace(player) || IsMe(player) || _isBot(player)) return;
        string intent;
        try { intent = Intent?.Invoke(text) ?? "banter"; } catch (Exception) { intent = "banter"; }
        Raise("player.say", player, new JsonObject { ["text"] = text, ["intent"] = intent });
    }

    public void OnJoin(string player) { if (!IsMe(player) && !_isBot(player)) Raise("player.join", player, new JsonObject()); }
    public void OnQuit(string player) { if (!IsMe(player) && !_isBot(player)) Raise("player.quit", player, new JsonObject()); }

    /// <summary>0x08 update health: me.hurt on a drop (by = the nearest attacker), me.lowHp crossing below 8 (re-armed above 12).</summary>
    public void OnHealth(int hp)
    {
        int last;
        lock (_lock) { last = _lastHp; _lastHp = hp; }
        if (last >= 0 && hp < last && hp > 0)
        {
            var (by, _) = Attacker(W.Player.Position, null);
            var data = new JsonObject { ["hp"] = hp };
            if (by != null) data["by"] = by;
            Raise("me.hurt", null, data);
        }
        bool fire = false;
        lock (_lock)
        {
            if (hp > 0 && hp < 8 && !_low) { _low = true; fire = true; }
            else if (hp > 12) _low = false;
        }
        if (fire) Raise("me.lowHp", null, new JsonObject { ["hp"] = hp });
    }

    private void Sneak(int entityId, bool on)
    {
        if (OtherPlayer(entityId) is not { } e) return;
        lock (_lock)
        {
            if (_sneak.TryGetValue(e.Name!, out var was) && was == on) return;
            _sneak[e.Name!] = on;
        }
        Raise(on ? "player.sneak" : "player.unsneak", e.Name, new JsonObject());
    }

    // ------------------------------------------------------------------ polled (the social tick)

    /// <summary>Polled conditions: sneak flags, proximity, gaze, hostile mobs near, dusk/dawn, weather. Call ~4×/s.</summary>
    public void Tick()
    {
        var w = W;
        var me = w.Player.Position;
        var now = Now;
        var players = w.Entities.All().Where(e => e.Kind == EntityKind.Player && e.Name is { Length: > 0 } n && !IsMe(n) && !_isBot(n)).ToList();
        foreach (var p in players)
        {
            Sneak(p.Id, p.IsCrouched);
            double d = p.Position.DistanceTo(me);
            bool isNear;
            lock (_lock) isNear = _near.Contains(p.Name!);
            if (!isNear && d <= NearBlocks) { lock (_lock) _near.Add(p.Name!); Raise("player.near", p.Name, new JsonObject { ["d"] = Math.Round(d, 1) }); }
            else if (isNear && d > NearBlocks + 1) { lock (_lock) _near.Remove(p.Name!); Raise("player.leave", p.Name, new JsonObject { ["d"] = Math.Round(d, 1) }); }
            Gaze(p, me, now);
        }
        List<string> gone;
        lock (_lock) gone = _near.Where(n => players.All(p => !p.Name!.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var n in gone) { lock (_lock) _near.Remove(n); Raise("player.leave", n, new JsonObject { ["d"] = -1 }); }

        var mobs = w.Entities.Nearby(me, MobBlocks, e => e.IsHostileMob).ToList();
        foreach (var m in mobs)
        {
            bool fresh;
            lock (_lock) fresh = _mobs.Add(m.Id);
            if (fresh) Raise("mob.near", null, new JsonObject { ["mob"] = m.DisplayName.ToLowerInvariant(), ["d"] = Math.Round(m.Position.DistanceTo(me), 1), ["pos"] = Pos(m.Position) });
        }
        lock (_lock) _mobs.RemoveWhere(id => mobs.All(m => m.Id != id));

        long t = w.TimeOfDay;
        long prev;
        lock (_lock) { prev = _lastTime; _lastTime = t; }
        if (prev >= 0 && prev != t)
        {
            if (Crossed(prev, t, 12500)) Raise("time.dusk", null, new JsonObject { ["time"] = t });
            if (Crossed(prev, t, 23500)) Raise("time.dawn", null, new JsonObject { ["time"] = t });
        }
        bool rain = w.Rain > 0;
        bool? was;
        lock (_lock) { was = _rain; _rain = rain; }
        if (was is { } r && r != rain) Raise("weather", null, new JsonObject { ["rain"] = rain });
    }

    /// <summary>Time went from <paramref name="a"/> to <paramref name="b"/> (forward, wrapping at 24000) across <paramref name="mark"/>.</summary>
    internal static bool Crossed(long a, long b, long mark)
    {
        long span = ((b - a) % 24000 + 24000) % 24000;
        if (span == 0 || span > 12000) return false; // a jump back (/time set) is no dusk
        long off = ((mark - a) % 24000 + 24000) % 24000;
        return off > 0 && off <= span;
    }

    /// <summary>player.lookAt: their look ray passes within 0.6 of my head (target me) or rests on one block (target block) for <see cref="GazeHold"/>.</summary>
    private void Gaze(Entity p, Vec3d me, DateTimeOffset now)
    {
        var eye = p.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
        var head = me + new Vec3d(0, PlayerPhysics.EyeHeight, 0);
        string key = "";
        BlockPos? block = null;
        double d = eye.DistanceTo(head);
        if (d <= 16 && RayMiss(eye, p.Yaw, p.Pitch, head) < 0.6) key = "me";
        else if (DemonstrationRecorder.LookTarget(W, p.Position, p.Yaw, p.Pitch, reach: 6, solidOnly: true) is { } t && p.Pitch > 10)
        {
            block = t.Pos;
            key = $"{t.Pos.X},{t.Pos.Y},{t.Pos.Z}";
        }
        bool fire;
        lock (_lock)
        {
            if (!_gaze.TryGetValue(p.Name!, out var g) || g.Key != key) { _gaze[p.Name!] = (key, now, false); return; }
            fire = key.Length > 0 && !g.Fired && now - g.Since >= GazeHold;
            if (fire) _gaze[p.Name!] = (key, g.Since, true);
        }
        if (!fire) return;
        var data = new JsonObject { ["target"] = key == "me" ? "me" : "block" };
        if (block is { } b) { data["pos"] = Pos(b); data["block"] = ItemName(W.GetBlock(b)); }
        Raise("player.lookAt", p.Name, data);
    }

    /// <summary>Distance from <paramref name="point"/> to the look ray (b1.8.1 yaw: 0 = +Z, 90 = −X); +∞ when it's behind.</summary>
    internal static double RayMiss(Vec3d eye, float yaw, float pitch, Vec3d point)
    {
        double y = yaw * Math.PI / 180, p = pitch * Math.PI / 180;
        var dir = new Vec3d(-Math.Sin(y) * Math.Cos(p), -Math.Sin(p), Math.Cos(y) * Math.Cos(p));
        var v = point - eye;
        double along = v.X * dir.X + v.Y * dir.Y + v.Z * dir.Z;
        if (along <= 0) return double.PositiveInfinity;
        var closest = eye + new Vec3d(dir.X * along, dir.Y * along, dir.Z * along);
        return closest.DistanceTo(point);
    }
}
