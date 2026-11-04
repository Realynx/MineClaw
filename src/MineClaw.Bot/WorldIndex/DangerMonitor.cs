using MineClaw.Agent.Events;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

public enum DangerLevel { None, Watch, Danger, Critical }

/// <summary>Enter/exit hysteresis for one (player, rule): a level must persist to be entered and stay clear to be left.</summary>
internal sealed class Hysteresis
{
    public DangerLevel Level { get; private set; }
    private DangerLevel _pending;
    private DateTimeOffset _pendingSince, _clearSince;

    /// <summary>Returns the transition when the level changes.</summary>
    public (DangerLevel From, DangerLevel To)? Update(DangerLevel target, DateTimeOffset now, TimeSpan enter, TimeSpan exit)
    {
        if (target > Level)
        {
            _clearSince = default;
            if (_pending != target) { _pending = target; _pendingSince = now; }
            if (now - _pendingSince < enter) return null;
            var from = Level;
            Level = target;
            _pending = DangerLevel.None;
            return (from, target);
        }
        _pending = DangerLevel.None;
        if (target == Level) { _clearSince = default; return null; }
        if (_clearSince == default) _clearSince = now;
        if (now - _clearSince < exit) return null;
        var f = Level;
        Level = target;
        _clearSince = default;
        return (f, target);
    }

    public void Reset() { Level = DangerLevel.None; _pending = DangerLevel.None; _clearSince = default; }
}

/// <summary>
/// Rate limiting for wakes: per (player, rule) cooldown, escalation-only re-wakes (a higher level than the last wake
/// passes the cooldown), and a global cap per 5 minutes (critical passes the cap).
/// </summary>
internal sealed class WakeGate(TimeProvider time)
{
    private readonly Dictionary<(string, string), (DateTimeOffset At, DangerLevel Level)> _last = new();
    private readonly Queue<DateTimeOffset> _recent = new();

    public bool TryWake(string player, string rule, DangerLevel level, TimeSpan cooldown, int maxPer5Min)
    {
        var now = time.GetUtcNow();
        var key = (player.ToLowerInvariant(), rule);
        if (_last.TryGetValue(key, out var l) && now - l.At < cooldown && level <= l.Level) return false;
        while (_recent.Count > 0 && now - _recent.Peek() > TimeSpan.FromMinutes(5)) _recent.Dequeue();
        if (_recent.Count >= maxPer5Min && level < DangerLevel.Critical) return false;
        _last[key] = (now, level);
        _recent.Enqueue(now);
        return true;
    }

    public DateTimeOffset? LastWake(string player, string rule) =>
        _last.TryGetValue((player.ToLowerInvariant(), rule), out var l) ? l.At : null;
}

/// <summary>A player snapshot as the tracker sees it each pass.</summary>
public readonly record struct PlayerSample(int EntityId, string Name, Vec3d Pos, float Yaw, float Pitch, bool Crouched, int VehicleId, int HeldItem);

/// <summary>
/// Danger rules for <i>other</i> players, built to stay quiet: standing or sneaking at the void edge never alarms; only
/// real falls (fast descent over the void for ≥ FallEnterMs, or below the island's bottom), knockback toward an edge
/// right after a hit (suppressed while the player is building at the edge), mob-attack <i>sessions</i> (one wake at start,
/// one more only on escalation: 3+ hits, edge, or a long fight) and confirmed deaths. Every rule runs through the same
/// hysteresis + cooldown + escalation gate; quiet summaries go to the digest via <see cref="Activity"/>.
/// </summary>
public sealed class DangerMonitor
{
    private sealed class Track
    {
        public Vec3d Pos;
        public DateTimeOffset At;
        public double Vy;
        public Vec3d HVel;
        public DateTimeOffset LastHurt;
        public readonly Queue<DateTimeOffset> Hurts = new();
        public DateTimeOffset LastEdgeWork;
        public readonly Hysteresis Fall = new(), Knock = new(), Hurt = new();
        public MobSession? Fight;
        public bool Vanished;
        /// <summary>Held void-fall wake (text, since).</summary>
        public (string Text, DateTimeOffset At, string Edge)? PendingFall;
        public DateTimeOffset FallWokeAt;
        public string LastEdge = "";
        public PlaceRef? LastIsland;
        public DateTimeOffset VanishedAt;
        public double VanishY;
    }

    private sealed class MobSession
    {
        public string Mob = "mob";
        public DateTimeOffset Start, Last;
        public int Hits;
        public DangerLevel Level;
        public bool NearEdge;
    }

    private sealed class PendingDeath
    {
        public string Player = "";
        public string? Cause;
        public DateTimeOffset At;
        public bool Chat, Status, Vanish, Respawn, Tracked;
        public BlockPos? Pos;
    }

    private readonly WorldState _world;
    private readonly DangerOptions _o;
    private readonly WakeOptions _w;
    private readonly TimeProvider _time;
    private readonly WakeGate _gate;
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingDeath> _deaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _deathPosted = new(StringComparer.OrdinalIgnoreCase);

    internal DangerMonitor(WorldState world, ActivityOptions options, TimeProvider time, WakeGate gate, Func<int, int, int?>? islandBottom,
        Func<BlockPos, PlaceRef?>? placeAt)
    {
        _world = world;
        _o = options.Danger;
        _w = options.Wake;
        _time = time;
        _gate = gate;
        IslandBottom = islandBottom;
        PlaceAt = placeAt;
    }

    /// <summary>Dimension switch: per-player motion/fight tracks describe the world the bot left (deaths stay deduped).</summary>
    internal void ResetTracks() => _tracks.Clear();

    private Func<int, int, int?>? IslandBottom { get; }
    private Func<BlockPos, PlaceRef?>? PlaceAt { get; }

    /// <summary>Wake-worthy events (to the agent's EventQueue).</summary>
    public event Action<GameEvent>? Wake;
    /// <summary>Quiet facts for the activity log/digest (fought sessions, deaths).</summary>
    public event Action<ActivityEvent>? Activity;

    private Track T(string name) => _tracks.TryGetValue(name, out var t) ? t : _tracks[name] = new Track();

    private static BlockPos Cell(Vec3d v) => new((int)Math.Floor(v.X), (int)Math.Floor(v.Y + 0.001), (int)Math.Floor(v.Z));

    private bool NearEdge(Vec3d feet)
    {
        var c = Cell(feet);
        if (!_world.IsLoaded(c.X, c.Z)) return false;
        return Safety.VoidNeighbourCount(_world, c.X, c.Y, c.Z) > 0 || Safety.EdgeDistance(_world, c.X, c.Y, c.Z, 1) <= 1;
    }

    private bool OverVoid(Vec3d feet)
    {
        var c = Cell(feet);
        return _world.IsLoaded(c.X, c.Z) && Safety.IsPlayerOverVoid(_world, feet);
    }

    /// <summary>
    /// Another (non-hive) bot account (<c>Chat:OtherBots</c>, learned bots): its dangers and deaths are Background notes only
    /// (live 10-28 02:23: robinchan's spider fight and death interrupted main's delegate and gen script, and the brain mourned
    /// "robin"). Null = no bots known.
    /// </summary>
    public Func<string, bool>? IsOtherBot { get; set; }

    private bool OtherBot(string player)
    {
        try { return IsOtherBot?.Invoke(player) == true; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A human's void fall or death: nothing the bot can do about it, so it never interrupts our own job or script (live 10-28
    /// 03:16: Fox's void death paused main's gen script until the brain restarted it by hand). It still wakes the brain once,
    /// even under a sleeping script (MustWake, Normal priority), for one short condolence line.
    /// </summary>
    private GameEvent Loss(string text, string player, DateTimeOffset at) =>
        GameEvent.Activity(text, player, EventPriority.Normal) with { At = at, MustWake = true };

    private void Post(string player, string rule, DangerLevel level, string text, EventPriority priority)
    {
        if (!_w.PlayerDanger && rule != "death") return;
        if (OtherBot(player))
        {
            Wake?.Invoke(GameEvent.Activity(text, player, EventPriority.Background) with { At = _time.GetUtcNow() });
            return;
        }
        if (!_gate.TryWake(player, rule, level, TimeSpan.FromSeconds(_o.CooldownSeconds), _w.MaxWakesPer5Min)) return;
        Wake?.Invoke(rule == "fall" ? Loss(text, player, _time.GetUtcNow()) : GameEvent.Activity(text, player, priority) with { At = _time.GetUtcNow() });
    }

    private string Where(Vec3d p)
    {
        var c = Cell(p);
        var place = PlaceAt?.Invoke(c);
        return ActivityText.At(place, c);
    }

    // ------------------------------------------------------------------ inputs

    /// <summary>The player placed/broke a block at <paramref name="at"/> (edge work context).</summary>
    public void BlockWork(string player, BlockPos at)
    {
        var t = T(player);
        if (_world.IsLoaded(at.X, at.Z) && (Safety.VoidNeighbourCount(_world, at.X, at.Y, at.Z) > 0 || NearEdge(t.Pos)))
            t.LastEdgeWork = _time.GetUtcNow();
    }

    /// <summary>True while the player is sneaking at an edge or worked an edge block recently.</summary>
    public bool BuildingAtEdge(string player) =>
        _tracks.TryGetValue(player, out var t) && _time.GetUtcNow() - t.LastEdgeWork <= TimeSpan.FromSeconds(_o.EdgeWorkSeconds);

    /// <summary>Called once per tracker pass for every visible player.</summary>
    public void Sample(PlayerSample p)
    {
        var now = _time.GetUtcNow();
        var t = T(p.Name);
        if (t.At != default)
        {
            double dt = Math.Max(0.02, (now - t.At).TotalSeconds);
            t.Vy = (p.Pos.Y - t.Pos.Y) / dt;
            t.HVel = new Vec3d((p.Pos.X - t.Pos.X) / dt, 0, (p.Pos.Z - t.Pos.Z) / dt);
        }
        t.Pos = p.Pos;
        t.At = now;
        t.Vanished = false;
        if (p.Crouched && NearEdge(p.Pos)) t.LastEdgeWork = now;
        bool building = now - t.LastEdgeWork <= TimeSpan.FromSeconds(_o.EdgeWorkSeconds);
        var exit = TimeSpan.FromMilliseconds(_o.ExitMs);

        // Falling: fast descent with nothing below for FallEnterMs; below the island's bottom is critical.
        var level = DangerLevel.None;
        if (p.VehicleId < 0 && t.Vy < -_o.FallSpeed && OverVoid(p.Pos))
        {
            level = DangerLevel.Danger;
            var c = Cell(p.Pos);
            if (IslandBottom?.Invoke(c.X, c.Z) is { } bottom && p.Pos.Y < bottom - 2) level = DangerLevel.Critical;
        }
        if (level == DangerLevel.None && !OverVoid(p.Pos) && PlaceAt?.Invoke(Cell(p.Pos)) is { Kind: IndexKinds.Island } isl) t.LastIsland = isl;
        // One fall, one wake: entering danger starts a held alarm (a death inside the window merges into it); going
        // critical mid-fall doesn't re-alarm.
        if (t.Fall.Update(level, now, TimeSpan.FromMilliseconds(_o.FallEnterMs), exit) is { } fx && fx.From < DangerLevel.Danger && fx.To >= DangerLevel.Danger
            && t.PendingFall == null)
        {
            var edge = EdgeOf(t.LastIsland, p.Pos);
            t.PendingFall = ($"{p.Name} is falling into the void{(edge.Length > 0 ? $" ({edge})" : Where(p.Pos))}", now, edge);
            t.LastEdge = edge;
        }
        FlushFalls(now);

        // Knockback toward an edge right after a hit (never while they're deliberately working the edge).
        var kl = DangerLevel.None;
        if (!building && now - t.LastHurt <= TimeSpan.FromMilliseconds(_o.KnockbackWindowMs) && t.HVel.HorizontalLength >= _o.KnockbackSpeed)
        {
            var dir = t.HVel.Normalized();
            var ahead = p.Pos + dir * 1.5;
            if (OverVoid(ahead)) kl = DangerLevel.Danger;
        }
        if (t.Knock.Update(kl, now, TimeSpan.Zero, exit) is { } kx && kx.To > kx.From && kx.To >= DangerLevel.Danger)
            Post(p.Name, "knockback", kx.To, $"{p.Name} was knocked toward the void edge{Where(p.Pos)}", EventPriority.High);

        // Fight sessions end quietly.
        if (t.Fight is { } f)
        {
            bool mobGone = _world.Entities.Nearby(p.Pos, 8, e => e.IsHostileMob).Count == 0;
            if (now - f.Last > TimeSpan.FromSeconds(_o.MobSessionEndSeconds) || mobGone)
            {
                Activity?.Invoke(new ActivityEvent(p.Name, ActivityKinds.Fought, now, 0.9)
                    { Detail = f.Mob, Count = f.Hits, Pos = Cell(p.Pos), Place = PlaceAt?.Invoke(Cell(p.Pos)) });
                t.Fight = null;
            }
            else if (NearEdge(p.Pos)) { f.NearEdge = true; Escalate(p.Name, t, f, now); }
        }
        t.Hurt.Update(DangerLevel.None, now, TimeSpan.Zero, exit);
        CheckDeaths(now);
    }

    /// <summary>"east edge of main island" from where the player left the island's box.</summary>
    private static string EdgeOf(PlaceRef? island, Vec3d p)
    {
        if (island == null) return "";
        var o = new (string Name, double Over)[]
        {
            ("east", p.X - (island.Max.X + 1)), ("west", island.Min.X - p.X), ("south", p.Z - (island.Max.Z + 1)), ("north", island.Min.Z - p.Z),
        }.MaxBy(t => t.Over);
        return o.Over > -0.5 ? $"{o.Name} edge" : "";
    }

    /// <summary>Emits held void-fall wakes whose window passed without a death.</summary>
    private void FlushFalls(DateTimeOffset now)
    {
        foreach (var (name, t) in _tracks)
        {
            if (t.PendingFall is not { } f || now - f.At < TimeSpan.FromSeconds(_o.VoidFallCoalesceSeconds)) continue;
            t.PendingFall = null;
            t.FallWokeAt = now;
            Post(name, "fall", DangerLevel.Danger, f.Text, EventPriority.High);
        }
    }

    /// <summary>0x12 animation 2 / 0x26 status 2 for a player.</summary>
    public void Hurt(string player, Vec3d? pos = null)
    {
        var now = _time.GetUtcNow();
        var t = T(player);
        if (pos is { } pp) t.Pos = pp;
        t.LastHurt = now;
        t.Hurts.Enqueue(now);
        while (t.Hurts.Count > 0 && now - t.Hurts.Peek() > TimeSpan.FromSeconds(10)) t.Hurts.Dequeue();

        var mob = _world.Entities.Nearest(t.Pos, _o.MobRadius, e => e.IsHostileMob);
        if (t.Fight is { } f)
        {
            f.Hits++;
            f.Last = now;
            if (NearEdge(t.Pos)) f.NearEdge = true;
            Escalate(player, t, f, now);
            return;
        }
        if (mob != null)
        {
            t.Fight = new MobSession { Mob = mob.DisplayName, Start = now, Last = now, Hits = 1, Level = DangerLevel.Danger, NearEdge = NearEdge(t.Pos) };
            Post(player, "mob", DangerLevel.Danger, $"{player} is being attacked by a {mob.DisplayName}{Where(t.Pos)}", EventPriority.Normal);
            Escalate(player, t, t.Fight, now);
            return;
        }
        // Repeated damage with no mob around (lava, fall damage, suffocation).
        var lvl = t.Hurts.Count >= 3 ? DangerLevel.Danger : DangerLevel.None;
        if (t.Hurt.Update(lvl, now, TimeSpan.Zero, TimeSpan.FromMilliseconds(_o.ExitMs)) is { } hx && hx.To > hx.From)
            Post(player, "hurt", hx.To, $"{player} is taking damage{Where(t.Pos)}", EventPriority.Normal);
    }

    private void Escalate(string player, Track t, MobSession f, DateTimeOffset now)
    {
        bool severe = f.Hits >= _o.MobEscalateHits || f.NearEdge || now - f.Start > TimeSpan.FromSeconds(_o.MobEscalateSeconds);
        if (!severe || f.Level >= DangerLevel.Critical) return;
        f.Level = DangerLevel.Critical;
        var why = f.NearEdge ? "near the void edge" : f.Hits >= _o.MobEscalateHits ? $"{f.Hits} hits" : "long fight";
        Post(player, "mob", DangerLevel.Critical, $"{player} is losing a fight with a {f.Mob} ({why}){Where(t.Pos)}", EventPriority.High);
    }

    // ------------------------------------------------------------------ deaths (chat line primary, status 3 exact, vanish/respawn fallback)

    /// <summary>A system death line ("kiwi fell out of the world"). Ignored for players not online.</summary>
    public void DeathLine(string player, string cause)
    {
        if (!_world.Entities.OnlinePlayers.Contains(player, StringComparer.OrdinalIgnoreCase)) return;
        var now = _time.GetUtcNow();
        if (Pending(player, cause) is not { } d) return;
        d.Chat = true;
        if (_tracks.TryGetValue(player, out var t) && t.Vanished && now - t.VanishedAt <= TimeSpan.FromSeconds(_o.DeathDedupeSeconds)) d.Vanish = true;
        CheckDeaths(now);
    }

    /// <summary>0x26 status 3: exact.</summary>
    public void DeathStatus(string player)
    {
        if (Pending(player, null) is not { } d) return;
        d.Status = true;
        CheckDeaths(_time.GetUtcNow());
    }

    /// <summary>The player's entity disappeared (destroyed / out of range).</summary>
    public void Vanished(string player, Vec3d lastPos)
    {
        var now = _time.GetUtcNow();
        var t = T(player);
        t.Vanished = true;
        t.VanishedAt = now;
        t.VanishY = lastPos.Y;
        if (_deaths.TryGetValue(player, out var d)) { d.Vanish = true; CheckDeaths(now); }
        else if (lastPos.Y < 0 || t.Fall.Level >= DangerLevel.Danger)
        {
            // Fallback inference: a candidate only, confirmed by a respawn at spawn (or a chat/status signal).
            if (Pending(player, "fell into the void") is { } p) p.Vanish = true;
        }
    }

    /// <summary>The player's entity (re)appeared; near world spawn right after vanishing = respawn evidence.</summary>
    public void Appeared(string player, Vec3d pos)
    {
        var now = _time.GetUtcNow();
        if (!_tracks.TryGetValue(player, out var t) || !t.Vanished) return;
        var sp = _world.Spawn;
        bool atSpawn = Math.Abs(pos.X - sp.X) <= 16 && Math.Abs(pos.Z - sp.Z) <= 16;
        if (atSpawn && now - t.VanishedAt <= TimeSpan.FromSeconds(60) && _deaths.TryGetValue(player, out var d)) d.Respawn = true;
        t.Vanished = false;
        t.Pos = pos;
        t.At = now;
        CheckDeaths(now);
    }

    private PendingDeath? Pending(string player, string? cause)
    {
        var now = _time.GetUtcNow();
        if (_deathPosted.TryGetValue(player, out var posted) && now - posted <= TimeSpan.FromSeconds(_o.DeathDedupeSeconds * 3))
            return null; // this death was already reported (dedupe across sources)
        if (!_deaths.TryGetValue(player, out var d))
        {
            d = new PendingDeath { Player = player, At = now };
            if (_tracks.TryGetValue(player, out var t) && t.At != default)
            {
                d.Pos = Cell(t.Pos);
                d.Tracked = !t.Vanished || now - t.VanishedAt <= TimeSpan.FromSeconds(_o.DeathDedupeSeconds);
            }
            _deaths[player] = d;
        }
        if (cause != null && (d.Cause == null || d.Cause == "fell into the void" && !d.Chat)) d.Cause = cause;
        return d;
    }

    private void CheckDeaths(DateTimeOffset now)
    {
        foreach (var d in _deaths.Values.ToList())
            if (DeathConfidence(d, now) is { } conf) PostDeath(d, conf, now);
    }

    /// <summary>A pending death that is confirmed (or ran out of time to be), with its confidence; null = keep waiting (or, a
    /// vanish without respawn after a minute, not a death: dropped).</summary>
    private double? DeathConfidence(PendingDeath d, DateTimeOffset now)
    {
        var age = now - d.At;
        if (Confirmed(d)) return 0.95;
        if (d.Chat && age > TimeSpan.FromSeconds(_o.DeathDedupeSeconds)) return d.Tracked ? 0.6 : 0.8; // untracked: nothing could corroborate
        if (!d.Chat && age > TimeSpan.FromSeconds(60)) _deaths.Remove(d.Player); // vanish without respawn: not a death
        return null;
    }

    private static bool Confirmed(PendingDeath d)
    {
        bool evidence = d.Vanish || d.Status || d.Respawn;
        return d.Status || (d.Chat && evidence) || (d.Vanish && d.Respawn);
    }

    private void PostDeath(PendingDeath d, double conf, DateTimeOffset now)
    {
        _deaths.Remove(d.Player);
        _deathPosted[d.Player] = now;
        var cause = d.Cause ?? "died";
        var track = _tracks.GetValueOrDefault(d.Player);
        var where = DeathWhere(d, cause, track);
        Activity?.Invoke(new ActivityEvent(d.Player, ActivityKinds.Died, now, conf) { Detail = cause + where, Pos = d.Pos });
        WakeForDeath(d.Player, conf, $"{(conf < 0.7 ? "probably: " : "")}{d.Player} {cause}{where}", where, track, now);
        if (_tracks.TryGetValue(d.Player, out var t)) { t.Fall.Reset(); t.Fight = null; }
    }

    /// <summary>" off the main island", " at the cobble gen", " (east edge of main island)": where it happened.</summary>
    private string DeathWhere(PendingDeath d, string cause, Track? track)
    {
        var place = d.Pos is { } p ? PlaceAt?.Invoke(p) : null;
        string where = place == null ? "" : place.Kind == IndexKinds.Island ? (cause.Contains("void") ? " off the " : " on ") + place.Name : ActivityText.At(place, d.Pos);
        if (track?.PendingFall is { Edge.Length: > 0 } hf) where = $" ({hf.Edge})";
        else if (track?.LastEdge is { Length: > 0 } le && cause.Contains("void")) where = $" ({le})";
        return where;
    }

    private void WakeForDeath(string player, double conf, string text, string where, Track? track, DateTimeOffset now)
    {
        bool bot = OtherBot(player);
        if (track?.PendingFall is not null) WakeHeldFall(track, player, conf, where, bot, now);
        else if (!_w.PlayerDeath || bot)
            Wake?.Invoke(GameEvent.Activity(text, player, EventPriority.Background) with { At = now });
        else if (track != null && track.FallWokeAt != default && now - track.FallWokeAt <= TimeSpan.FromSeconds(_o.FollowUpSeconds))
            Wake?.Invoke(GameEvent.Activity(text, player, EventPriority.Normal) with { At = now }); // follow-up to the fall alarm
        else if (_gate.TryWake(player, "death", DangerLevel.Critical, TimeSpan.FromSeconds(_o.CooldownSeconds), _w.MaxWakesPer5Min))
            Wake?.Invoke(Loss(text, player, now));
    }

    /// <summary>The fall alarm was still held: one wake carrying both facts.</summary>
    private void WakeHeldFall(Track track, string player, double conf, string where, bool bot, DateTimeOffset now)
    {
        track.PendingFall = null;
        track.FallWokeAt = now;
        var text = $"{(conf < 0.7 ? "probably: " : "")}{player} fell into the void and died{where}";
        if (bot) Wake?.Invoke(GameEvent.Activity(text, player, EventPriority.Background) with { At = now });
        else if (_gate.TryWake(player, "death", DangerLevel.Critical, TimeSpan.FromSeconds(_o.CooldownSeconds), _w.MaxWakesPer5Min))
            Wake?.Invoke(Loss(text, player, now));
    }

    /// <summary>Re-checks pending deaths whose corroboration window ran out (call each pass).</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        CheckDeaths(now);
        FlushFalls(now);
    }
}
