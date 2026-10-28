using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Combat;

public enum CombatEventKind { FightStarted, FightEnded, Critical }

/// <summary>What the Agent hears about combat: fight start, fight end (one-line summary) and critical moments only.</summary>
public sealed record CombatEvent(CombatEventKind Kind, string Message)
{
    /// <summary>The fight was started by a tool call (spar/attack), whose result already carries the outcome.</summary>
    public bool Requested { get; init; }
}

/// <summary>
/// The engine's output for one tick. <see cref="Input"/> is the movement to apply (the proposed one when not
/// <see cref="Overrides"/>); the EdgeGuard still filters it. <see cref="AttackId"/> ≥ 0 = swing at that entity after
/// the movement packet; <see cref="Block"/> = desired sword-block state.
/// </summary>
public readonly record struct CombatStep(MoveInput Input, bool Overrides, int AttackId = -1, bool Block = false,
    WeaponPick? Equip = null, string? Chat = null, bool AbortAction = false, bool Eat = false, bool Shelter = false);

/// <summary>
/// Tick-thread combat brain (no LLM): reflex defense of the bot and guarded players, explicit mob attacks and
/// consent-based sparring. Other threads only post signals (<see cref="OnChat"/>, <see cref="OnEntityStatus"/>...)
/// and commands; everything else runs inside <see cref="Tick"/>.
/// </summary>
public sealed class CombatEngine
{
    private enum FightKind { Mob, Spar }

    private sealed class SparState
    {
        public required string Opponent;
        public int OpponentId;
        public BlockPos Center;
        public required SparZone Zone;
        public required OpponentHpModel Model;
        public int? Rounds;
        public int Round = 1, RoundBot, RoundOpp;
        public long PauseUntil;
        public long MissingSince = -1;
        public int WeaponId;
        public BlockPos? Stance;
        public long StanceUntil;
    }

    private sealed class Fight
    {
        public FightKind Kind;
        public required string Reason;
        public int TargetId;
        public required string TargetName;
        public long StartTick, LastProgressTick, BackoffUntil, StanceUntil, FleeStart = -1, LastSample, TargetSince;
        public int Swings, Hits, Unconfirmed, DamageTaken, HitsReceived, Knockbacks, GuardInterventions, Kills, TargetHits;
        /// <summary>Not a fight: a short move into cover from a ranged mob the bot can't reach.</summary>
        public bool Cover;
        /// <summary>The reflex gave up on this target (it can't be reached): remembered so it doesn't re-trigger.</summary>
        public bool GaveUp;
        public double PrevDist = double.MaxValue, BestDist = double.MaxValue;
        public bool Explicit, FuseAlerted, LowAlerted, ShelterAsked;
        public BlockPos? Stance;
        public int StrafeSign = 1;
        public string? StopRequest;
        public TaskCompletionSource<string>? Done;
        public SparState? Spar;
    }

    private abstract record Signal;
    private sealed record ChatSig(string Sender, string Message) : Signal;
    private sealed record StatusSig(int Id, int Status) : Signal;
    private sealed record AnimSig(int Id, int Anim) : Signal;
    private sealed record DangerSig(DangerEvent Danger) : Signal;
    private sealed record SparCmd(string Opponent, int OpponentId, SparZone? Zone, BlockPos Center, int? Rounds, TaskCompletionSource<string> Done) : Signal;
    private sealed record AttackCmd(int TargetId, TaskCompletionSource<string> Done) : Signal;
    private sealed record StopCmd(string Reason) : Signal;

    /// <summary>A mob the reflex gave up on (couldn't reach it, or took cover from it): it re-triggers only when it becomes
    /// reachable (within melee reach with line of sight), a melee mob actually hits the bot, or a creeper gets within its
    /// ignite range / starts fusing. Ranged hits only ever send the bot into cover.</summary>
    private sealed class Unreachable
    {
        public long Tick, LastCover = long.MinValue / 2;
        public Vec3d Pos;
        public required string Name;
    }

    private readonly Dictionary<int, Unreachable> _unreachable = new();
    private readonly WorldState _w;
    private readonly ConcurrentQueue<Signal> _signals = new();
    private readonly Dictionary<int, long> _lastAttack = new(), _lastHurt = new(), _lastSwing = new(), _provoked = new(), _dead = new();
    private readonly List<(int Id, long Tick, int Weapon, bool CouldCrit)> _pending = new();
    private readonly ConcurrentDictionary<string, DateTime> _sparRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (double Estimate, DateTime At)> _sparMemory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _clock;
    private Fight? _fight;
    private int _lastHealth = -1;
    private bool _blocking;
    private GuardActions _lastGuardLogged;
    private long _tick;
    private volatile string _status = "idle";
    private volatile bool _engaged;
    private long _stopWords;

    public CombatEngine(WorldState world, CombatOptions options, CombatTelemetry telemetry, Func<DateTime>? clock = null)
    {
        _w = world;
        Options = options;
        Telemetry = telemetry;
        _clock = clock ?? (() => DateTime.UtcNow);
        DefendSelf = options.DefendSelf;
        Guarded = options.GuardEnabled ? options.GuardPlayers : [];
    }

    public CombatOptions Options { get; set; }
    public CombatTelemetry Telemetry { get; }
    /// <summary>Bot username (its own chat lines are ignored for stop words).</summary>
    public string Username { get; set; } = "";
    /// <summary>Items never wielded (the client's reserved items).</summary>
    public Func<IReadOnlySet<int>> Reserved { get; set; } = () => new HashSet<int>();
    public event Action<CombatEvent>? EventRaised;

    public volatile bool DefendSelf;

    /// <summary>
    /// The no-path danger reflex (the client decides: blocks on hand, no walk home, low hp or outnumbered): called with the threat
    /// count and whether a retreat cell exists when a fight turns into a retreat. True = the fight ends ("boxing in") and the
    /// step asks the client to box the body in (<see cref="CombatStep.Shelter"/>).
    /// </summary>
    public Func<int, bool, bool>? ShouldShelter { get; set; }

    /// <summary>The body is boxing itself in / waiting it out: no reflex fights start (the box is the defence).</summary>
    public volatile bool Sheltering;
    /// <summary>Players being guarded (replace the whole list to change it).</summary>
    public volatile IReadOnlyList<string> Guarded;

    /// <summary>A fight (mob or spar) is running: the engine owns the bot's movement.</summary>
    public bool Engaged => _engaged;
    public bool Sparring => _engaged && _fight?.Kind == FightKind.Spar;
    /// <summary>The fight is a reflex (defend/guard/cover), not a spar or an attack a player asked for: it ends on its own
    /// within <see cref="CombatOptions.ZeroHitGiveUpTicks"/> when nothing is hit, so a job may wait it out.</summary>
    public bool ReflexEngaged => _engaged && _fight is { Kind: FightKind.Mob, Explicit: false, Done: null };
    /// <summary>Mobs the reflex gave up on (entity ids), for status and tests.</summary>
    public IReadOnlyCollection<int> GaveUpOn { get { lock (_unreachable) return _unreachable.Keys.ToList(); } }
    /// <summary>One-line state for <c>combat_status</c>.</summary>
    public string Status => _status;
    /// <summary>Stop words heard so far (a spar countdown aborts when this changes).</summary>
    public long StopWordCount => Interlocked.Read(ref _stopWords);
    public long CurrentTick => Interlocked.Read(ref _tick);

    // ------------------------------------------------------------------ signals (any thread)

    public void OnChat(string sender, string message)
    {
        if (string.Equals(sender, Username, StringComparison.OrdinalIgnoreCase)) return;
        if (SparWords.IsRequest(message)) _sparRequests[sender] = _clock();
        if (SparWords.IsStop(message, Options.StopWords)) Interlocked.Increment(ref _stopWords);
        _signals.Enqueue(new ChatSig(sender, message));
    }

    public void OnEntityStatus(int id, int status) => _signals.Enqueue(new StatusSig(id, status));
    public void OnAnimation(int id, int anim) => _signals.Enqueue(new AnimSig(id, anim));
    public void OnDanger(DangerEvent d) => _signals.Enqueue(new DangerSig(d));
    public void RequestStop(string reason) => _signals.Enqueue(new StopCmd(reason));

    /// <summary>True when <paramref name="player"/> asked for a spar in chat within the consent window.</summary>
    public bool HasSparRequest(string player) =>
        _sparRequests.TryGetValue(player, out var at) && _clock() - at <= Options.SparConsentWindow;

    /// <summary>Starting estimate for a new spar: the last estimate if recent (no regeneration assumed), else 20.</summary>
    public double SparStartHealth(string player) =>
        _sparMemory.TryGetValue(player, out var m) && _clock() - m.At <= Options.SparMemory ? m.Estimate : 20;

    /// <summary>Hands a prepared spar (consent, zone and countdown done) to the engine; completes with the summary.</summary>
    public Task<string> StartSpar(string opponent, int opponentId, SparZone zone, int? rounds)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _signals.Enqueue(new SparCmd(opponent, opponentId, zone, zone.Center, rounds, tcs));
        return tcs.Task;
    }

    /// <summary>As above, computing the zone around <paramref name="center"/> when the spar starts (refused if none).</summary>
    public Task<string> StartSpar(string opponent, int opponentId, BlockPos center, int? rounds)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _signals.Enqueue(new SparCmd(opponent, opponentId, null, center, rounds, tcs));
        return tcs.Task;
    }

    /// <summary>Fights one mob (explicit instruction); completes with the summary.</summary>
    public Task<string> StartAttack(int targetId)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _signals.Enqueue(new AttackCmd(targetId, tcs));
        return tcs.Task;
    }

    /// <summary>Hard gate used by the packet layer: may the bot hit this entity right now?</summary>
    public bool MayAttack(Entity e)
    {
        if (e.Kind == EntityKind.Player)
            return _fight is { Kind: FightKind.Spar, Spar: { } s } && s.OpponentId == e.Id && _tick >= s.PauseUntil;
        if (e.Kind != EntityKind.Mob) return false;
        return !CombatRules.IsTamedWolf(e);
    }

    // ------------------------------------------------------------------ tick-thread hooks

    /// <summary>A 0x1C velocity for the bot was applied (knockback).</summary>
    public void OnKnockback(Vec3d velocity, bool nearEdge)
    {
        if (_fight is not { } f) return;
        f.Knockbacks++;
        Telemetry.Log(_tick, "knockback", Me(), null, new Dictionary<string, object?> { ["vel"] = velocity, ["near_edge"] = nearEdge });
    }

    /// <summary>The EdgeGuard decision of this tick (logged during fights when it intervened).</summary>
    public void OnGuard(GuardDecision d)
    {
        if (_fight is not { } f) { _lastGuardLogged = GuardActions.None; return; }
        var a = d.Actions & ~GuardActions.ForcedSneak;
        if (a == GuardActions.None) { _lastGuardLogged = GuardActions.None; return; }
        f.GuardInterventions++;
        if (a == _lastGuardLogged) return;
        _lastGuardLogged = a;
        Telemetry.Log(_tick, "edgeguard", Me(), null, new Dictionary<string, object?> { ["actions"] = a.ToString() });
    }

    private Vec3d Me() => _w.Player.Physics.Position;

    /// <summary>Runs one tick. <paramref name="proposed"/> is the driver/idle input.</summary>
    public CombatStep Tick(PhysicsState phys, long tick, MoveInput proposed)
    {
        Interlocked.Exchange(ref _tick, tick);
        var ctx = new Ctx();
        while (_signals.TryDequeue(out var sig)) Handle(sig, phys, ctx);
        TrackHealth(phys, ctx);
        ExpirePending(ctx);

        if (tick % 100 == 0) ForgetGoneMobs();
        CombatStep step;
        if (Sheltering && _fight is { Explicit: false, Done: null, Kind: FightKind.Mob } sf) EndFight(sf, "boxed in", phys, ctx);
        if (_fight is null && !Sheltering)
        {
            if (tick % 5 == 0 && Scan(phys) is { } found)
            {
                ctx.Abort = StartFight(FightKind.Mob, found.E, found.Why, phys, explicitDone: null);
                if (found.Cover) _fight!.Cover = true;
            }
        }
        if (_fight is { } f)
        {
            var input = f.Kind == FightKind.Spar ? SparTick(f, phys, ctx) : MobTick(f, phys, ctx);
            step = new CombatStep(input, true, ctx.Attack, ctx.Block, ctx.Equip, ctx.Chat, ctx.Abort, ctx.Eat, ctx.Shelter);
        }
        else step = new CombatStep(proposed, false, Chat: ctx.Chat, Eat: ctx.Eat, Equip: ctx.Equip, AbortAction: ctx.Abort, Shelter: ctx.Shelter);

        // Never meet an enderman's gaze (unless it is the one being fought).
        var ender = _w.Entities.Nearby(phys.Position, 64, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Enderman
                                                            && (_fight is null || e.Id != _fight.TargetId));
        if (ender.Count > 0)
        {
            var centers = ender.Select(e => { var b = CombatRules.Hitbox(e); return b.Center; }).ToList();
            float p = CombatRules.AvoidEndermanGaze(phys.EyePosition, step.Input.Yaw, step.Input.Pitch, centers);
            step = step with { Input = step.Input with { Pitch = p } };
        }
        if (step.AttackId >= 0) step = step with { Block = false };
        _blocking = step.Block;
        _engaged = _fight is not null;
        _status = Describe(phys);
        return step;
    }

    private sealed class Ctx
    {
        public int Attack = -1;
        public bool Block, Abort, Eat, Shelter;
        public WeaponPick? Equip;
        public string? Chat;
    }

    // ------------------------------------------------------------------ signals

    private void Handle(Signal sig, PhysicsState phys, Ctx ctx)
    {
        switch (sig)
        {
            case ChatSig c:
                if (_fight is { Kind: FightKind.Spar } sf && SparWords.IsStop(c.Message, Options.StopWords))
                    sf.StopRequest ??= $"{c.Sender} said \"{Trim(c.Message)}\"";
                break;
            case AnimSig a:
                if (a.Anim == CombatRules.AnimSwing) _lastSwing[a.Id] = _tick;
                else if (a.Anim == CombatRules.AnimHurt) OnHurt(a.Id, phys);
                break;
            case StatusSig s:
                if (s.Status == CombatRules.StatusHurt) OnHurt(s.Id, phys);
                else if (s.Status == CombatRules.StatusDead) _dead[s.Id] = _tick;
                break;
            case DangerSig d:
                if (_fight is { Kind: FightKind.Spar } spar && d.Danger.Kind is not (DangerKind.Damage or DangerKind.HostileNearby or DangerKind.Recovered))
                    spar.StopRequest ??= "danger: " + d.Danger.Message;
                break;
            case StopCmd stop:
                if (_fight is { } f) f.StopRequest ??= stop.Reason;
                break;
            case SparCmd sc:
                if (_fight is not null) { sc.Done.TrySetResult($"refused: already fighting {_fight.TargetName}"); break; }
                if (_w.Entities.Get(sc.OpponentId) is not { Kind: EntityKind.Player } opp) { sc.Done.TrySetResult("refused: opponent not visible"); break; }
                var zr = sc.Zone is { } given ? new SparZoneResult(given, null) : SparZone.Find(_w, sc.Center.BottomCenter, Options, [phys.Position, opp.Position]);
                if (zr.Zone is not { } zone) { sc.Done.TrySetResult("refused: no safe arena: " + zr.Reason); break; }
                StartFight(FightKind.Spar, opp, "spar", phys, sc.Done);
                _fight!.Spar = new SparState
                {
                    Opponent = sc.Opponent, OpponentId = sc.OpponentId, Center = zone.Center, Zone = zone, Rounds = sc.Rounds,
                    Model = new OpponentHpModel(Options, SparStartHealth(sc.Opponent)),
                    WeaponId = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id,
                };
                Log("spar_start", phys, opp.Position, new() { ["opponent"] = sc.Opponent, ["est_hp"] = _fight.Spar.Model.Estimate, ["center"] = sc.Center.ToString(), ["rounds"] = sc.Rounds });
                break;
            case AttackCmd ac:
                if (_fight is not null) { ac.Done.TrySetResult($"refused: already fighting {_fight.TargetName}"); break; }
                if (_w.Entities.Get(ac.TargetId) is not { } t) { ac.Done.TrySetResult("refused: target gone"); break; }
                if (!MayAttack(t)) { ac.Done.TrySetResult($"refused: won't attack {t.DisplayName}"); break; }
                StartFight(FightKind.Mob, t, "told to attack", phys, ac.Done);
                _fight!.Explicit = true;
                break;
        }
    }

    /// <summary>Hurt status/animation for an entity: confirms our pending swing, or is damage from something else.</summary>
    private void OnHurt(int id, PhysicsState phys)
    {
        if (_lastHurt.TryGetValue(id, out var prev) && _tick - prev < 2) return; // 0x26 and 0x12 for the same hit
        _lastHurt[id] = _tick;
        int idx = _pending.FindIndex(p => p.Id == id && _tick - p.Tick <= 8);
        if (idx >= 0)
        {
            var p = _pending[idx];
            _pending.RemoveAt(idx);
            OnHitConfirmed(id, p.Weapon, p.CouldCrit, phys);
            return;
        }
        if (_fight is { Spar: { } s } f && s.OpponentId == id)
        {
            s.Model.OnOtherDamage();
            f.StopRequest ??= "opponent hurt by something else";
            Log("opponent_other_damage", phys, _w.Entities.Get(id)?.Position, new() { ["est_hp"] = s.Model.Estimate });
            return;
        }
        // A guarded player got hurt: whatever mob is right next to them is now fair game.
        if (_w.Entities.Get(id) is { Kind: EntityKind.Player } pl && IsGuarded(pl.Name)
            && _w.Entities.Nearest(pl.Position, 4, e => e.Kind == EntityKind.Mob && !CombatRules.IsTamedWolf(e)) is { } culprit)
            _provoked[culprit.Id] = _tick;
    }

    private void OnHitConfirmed(int id, int weapon, bool couldCrit, PhysicsState phys)
    {
        if (_fight is not { } f) return;
        f.Hits++;
        if (id == f.TargetId) f.TargetHits++;
        f.LastProgressTick = _tick;
        var e = _w.Entities.Get(id);
        var fields = new Dictionary<string, object?> { ["target"] = e?.DisplayName ?? id.ToString(), ["weapon"] = weapon, ["crit"] = couldCrit };
        if (f.Spar is { } s && s.OpponentId == id)
        {
            double d = s.Model.OnConfirmedHit(weapon, couldCrit, Armor(e));
            s.RoundBot++;
            fields["est_dmg"] = d;
            fields["est_hp"] = s.Model.Estimate;
        }
        if (e is { TypeId: MobTypes.Creeper, Kind: EntityKind.Mob }) f.BackoffUntil = _tick + 20;
        Log("hit", phys, e?.Position, fields);
    }

    private static IEnumerable<ItemStack> Armor(Entity? e) =>
        e is null ? [] : Enumerable.Range(1, 4).Select(e.EquipmentAt);

    private void ExpirePending(Ctx ctx)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (_tick - _pending[i].Tick <= 8) continue;
            if (_fight is { } f) f.Unconfirmed++;
            _pending.RemoveAt(i);
        }
    }

    private void TrackHealth(PhysicsState phys, Ctx ctx)
    {
        int hp = _w.Player.Health;
        int prev = _lastHealth;
        _lastHealth = hp;
        if (prev < 0 || hp >= prev) return;
        int dmg = prev - hp;
        var attacker = Attacker(phys);
        if (attacker is { Kind: EntityKind.Mob }) _provoked[attacker.Id] = _tick;
        if (_fight is not { } f) return;
        f.DamageTaken += dmg;
        bool fromOpponent = f.Spar is { } s && attacker?.Id == s.OpponentId;
        if (fromOpponent) { f.HitsReceived++; f.Spar!.RoundOpp++; }
        else if (attacker is not null) f.HitsReceived++;
        Log("hit_received", phys, attacker?.Position, new() { ["dmg"] = dmg, ["hp"] = hp, ["from"] = attacker?.DisplayName ?? "unknown" });
        if (f.Spar is not null && !fromOpponent) f.StopRequest ??= $"took {dmg} damage from {attacker?.DisplayName ?? "something else"}";
    }

    /// <summary>Best guess at what just hurt the bot.</summary>
    private Entity? Attacker(PhysicsState phys)
    {
        var me = phys.Position;
        if (_fight?.Spar is { } s && _w.Entities.Get(s.OpponentId) is { } opp && opp.Position.DistanceTo(me) <= 5
            && _lastSwing.TryGetValue(opp.Id, out var sw) && _tick - sw <= 10) return opp;
        var melee = _w.Entities.Nearest(me, 4, e => e.Kind == EntityKind.Mob
                                                    && (CombatRules.MeleeDamageOf(e) > 0 || e.TypeId == MobTypes.Silverfish));
        if (melee is not null) return melee;
        var swinger = _w.Entities.Nearest(me, 4.5, e => e.Kind == EntityKind.Player && _lastSwing.TryGetValue(e.Id, out var t) && _tick - t <= 10);
        if (swinger is not null) return swinger;
        return _w.Entities.Nearest(me, 24, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Skeleton
                                               && CombatGeometry.HasLineOfSight(_w, phys.EyePosition, Eye(e)));
    }

    private static Vec3d Eye(Entity e) => e.Position + new Vec3d(0, (CombatRules.Hitbox(e).MaxY - e.Position.Y) * 0.85, 0);

    private bool IsGuarded(string? name) => name is not null && Guarded.Any(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ reflex scan

    private (Entity E, string Why, bool Cover)? Scan(PhysicsState phys)
    {
        var guarded = Guarded;
        if (!DefendSelf && guarded.Count == 0) return null;
        var me = phys.Position;
        var guards = guarded.Select(n => _w.Entities.FindPlayer(n)).Where(p => p is not null).ToList();
        (Entity E, string Why, bool Cover)? best = null;
        double bestD = double.MaxValue;
        foreach (var e in _w.Entities.Nearby(me, Options.MaxEngageDistance, e => e.Kind == EntityKind.Mob))
        {
            if (CombatRules.IsTamedWolf(e)) continue;
            var prof = CombatRules.Profile(e.TypeId);
            if (prof.Tactic == MobTactic.Ignore) continue;
            bool provoked = _provoked.TryGetValue(e.Id, out var pt) && _tick - pt < 200;
            if (!CombatRules.IsThreat(e) && !provoked) continue;
            if (e.TypeId == MobTypes.Enderman && !provoked) continue;
            if (e.TypeId == MobTypes.Spider && !_w.IsNight && !provoked) continue; // neutral in daylight
            double d = e.Position.DistanceTo(me);
            bool ranged = prof.Tactic == MobTactic.Ranged;
            bool creeper = e.TypeId == MobTypes.Creeper;
            bool inReach = InMeleeReach(phys, e);
            bool creeperDanger = creeper && (CombatRules.IsCreeperFusing(e) ? d <= CombatRules.CreeperCancelRange + 1 : Horizontal(me, e.Position) <= Options.CreeperAvoidRadius);
            string? why = null;
            bool cover = false;

            Unreachable? u;
            lock (_unreachable) _unreachable.TryGetValue(e.Id, out u);
            if (u is not null)
            {
                // Gave up on it: only a real change re-triggers (live 10-27 22:09-22:10: a creeper across a gap restarted a
                // 20 s fight every 200 ms and cancelled every walk north).
                bool hitMeSince = provoked && pt > u.Tick;
                if (creeperDanger) why = CombatRules.IsCreeperFusing(e) ? "creeper fusing" : "creeper close";
                else if (inReach && DefendSelf) why = "in reach";
                else if (hitMeSince && !ranged && DefendSelf) why = "it attacked";
                else if (hitMeSince && ranged && DefendSelf && _w.Player.Health <= Options.CoverHealth && _tick - u.LastCover > Options.CoverMaxTicks * 2
                         && CombatGeometry.HasLineOfSight(_w, Eye(e), phys.EyePosition))
                { why = "taking cover"; cover = true; }
                if (why is null || d >= bestD) continue;
                bestD = d;
                best = (e, why, cover);
                continue;
            }

            // Only what the bot can actually get to starts a fight: within melee reach, or a safe first step toward it.
            bool approachable = inReach || CanWalkTo(me, e);
            if (DefendSelf)
            {
                if (provoked && d <= 10 && !ranged) why = "it attacked";
                else if (ranged && (provoked || CombatGeometry.HasLineOfSight(_w, phys.EyePosition, Eye(e))))
                {
                    // Live 10-28 night: skeletons at 4-11 m on ledges/across gaps held every bot "busy" with 0 hits.
                    if (d <= Options.SkeletonEngageRange && approachable) why = provoked ? "it attacked" : "skeleton in sight";
                    else if (provoked && _w.Player.Health <= Options.CoverHealth) { why = "taking cover"; cover = true; }
                }
                else if (creeperDanger) why = CombatRules.IsCreeperFusing(e) ? "creeper fusing" : "creeper close";
                else if (creeper && d <= CombatRules.CreeperCancelRange && approachable) why = "creeper close";
                else if (!creeper && d <= Options.DefendRadius && approachable) why = "defending myself";
            }
            if (why is null && approachable && (!ranged || d <= Options.SkeletonEngageRange))
                foreach (var g in guards)
                    if (g!.Position.DistanceTo(e.Position) <= Options.GuardRadius) { why = "guarding " + g.Name; break; }
            if (why is null || d >= bestD) continue;
            bestD = d;
            best = (e, why, cover);
        }
        return best;
    }

    private readonly Dictionary<int, (long Tick, BlockPos From, BlockPos At, bool Ok)> _walkable = new();

    /// <summary>A walk (no jumps, digging or placing) gets the bot within melee reach of it; cached ~1 s per mob and spot.</summary>
    private bool CanWalkTo(Vec3d me, Entity e)
    {
        var from = me.ToBlockPos();
        var at = e.Position.ToBlockPos();
        if (_walkable.TryGetValue(e.Id, out var c) && c.From == from && c.At == at && _tick - c.Tick < 20) return c.Ok;
        bool ok = CombatGeometry.CanWalkNear(_w, me, e.Position, Options.Reach + 0.3);
        if (_walkable.Count > 64) _walkable.Clear();
        _walkable[e.Id] = (_tick, from, at, ok);
        return ok;
    }

    /// <summary>Within melee reach of the bot's eyes with nothing in between: reachable right now.</summary>
    private bool InMeleeReach(PhysicsState phys, Entity e) =>
        CombatRules.ReachTo(phys.EyePosition, CombatRules.Hitbox(e)) <= Options.Reach + 0.3
        && CombatGeometry.HasLineOfSight(_w, phys.EyePosition, CombatRules.AimPoint(e));

    private void GiveUpOn(Fight f, bool cover)
    {
        if (f.Explicit || f.Done is not null || f.Kind != FightKind.Mob) return;
        var e = _w.Entities.Get(f.TargetId);
        lock (_unreachable)
        {
            if (!_unreachable.TryGetValue(f.TargetId, out var u))
                _unreachable[f.TargetId] = u = new Unreachable { Name = f.TargetName, Tick = _tick };
            u.Tick = _tick;
            if (e is not null) u.Pos = e.Position;
            if (cover) u.LastCover = _tick;
        }
    }

    private void ForgetGoneMobs()
    {
        lock (_unreachable)
            foreach (var id in _unreachable.Keys.ToList())
                if (_w.Entities.Get(id) is null || _dead.ContainsKey(id)) _unreachable.Remove(id);
    }

    private bool StartFight(FightKind kind, Entity target, string why, PhysicsState phys, TaskCompletionSource<string>? explicitDone)
    {
        _fight = new Fight
        {
            Kind = kind, Reason = why, TargetId = target.Id, TargetName = target.DisplayName, StartTick = _tick,
            LastProgressTick = _tick, Done = explicitDone, LastSample = _tick, TargetSince = _tick,
        };
        _engaged = true;
        var slots = _w.Inventory.PlayerSlots();
        double dist = target.Position.DistanceTo(phys.Position);
        Log("start", phys, target.Position, new() { ["kind"] = kind.ToString(), ["target"] = target.DisplayName, ["why"] = why, ["hp"] = _w.Player.Health });
        if (kind == FightKind.Mob)
            Raise(CombatEventKind.FightStarted, $"fight started: {target.DisplayName} {dist:0}m away ({why})", explicitDone is not null);
        else
            Raise(CombatEventKind.FightStarted, $"spar started vs {target.DisplayName}", true);
        if (kind == FightKind.Mob)
        {
            var pick = WeaponChoice.Best(slots, Reserved(), Options.NeverWield);
            if (pick.Slot != 36 + _w.Player.HeldSlot) _pendingEquip = pick;
        }
        return explicitDone is null; // reflex fights interrupt whatever action is running
    }

    private WeaponPick? _pendingEquip;

    private void EndFight(Fight f, string result, PhysicsState phys, Ctx ctx)
    {
        string summary;
        if (f.Spar is { } s)
        {
            summary = $"spar vs {s.Opponent}: {f.Hits} hits landed, took {f.DamageTaken} dmg, stopped: {result}";
            _sparMemory[s.Opponent] = (s.Model.Estimate, _clock());
            ctx.Chat = $"gg {s.Opponent}! ({result})";
        }
        else
            summary = $"fight vs {f.TargetName}: {result}, {f.Hits} hits{(f.Kills > 1 ? $", {f.Kills} kills" : "")}, took {f.DamageTaken} dmg, hp {_w.Player.Health}/20";
        Log("end", phys, _w.Entities.Get(f.TargetId)?.Position, new()
        {
            ["result"] = result, ["swings"] = f.Swings, ["hits"] = f.Hits, ["unconfirmed"] = f.Unconfirmed,
            ["hits_received"] = f.HitsReceived, ["dmg_taken"] = f.DamageTaken, ["knockbacks"] = f.Knockbacks,
            ["edgeguard"] = f.GuardInterventions, ["ticks"] = _tick - f.StartTick, ["est_hp"] = f.Spar?.Model.Estimate,
        });
        Log("summary", null, null, new() { ["text"] = summary });
        if (f.GaveUp || f.Cover) GiveUpOn(f, f.Cover);
        _fight = null;
        _engaged = false;
        _pending.Clear();
        ctx.Block = false;
        int food = _w.Player.Food;
        ctx.Eat = food <= Options.EatBelowFood && _w.Entities.NearestHostile(phys.Position, 10) is null;
        Raise(CombatEventKind.FightEnded, "fight ended: " + summary, f.Done is not null);
        f.Done?.TrySetResult(summary);
    }

    private void Raise(CombatEventKind kind, string msg, bool requested = false)
    {
        var ev = new CombatEvent(kind, msg) { Requested = requested };
        try { EventRaised?.Invoke(ev); } catch { /* handlers must not break the tick */ }
    }

    // ------------------------------------------------------------------ mob fights

    private MoveInput MobTick(Fight f, PhysicsState phys, Ctx ctx)
    {
        if (_pendingEquip is { } eq) { ctx.Equip = eq; _pendingEquip = null; }
        var me = phys.Position;
        var face = Hold(phys);
        if (f.StopRequest is { } stop) { EndFight(f, "stopped: " + stop, phys, ctx); return face; }

        var target = _w.Entities.Get(f.TargetId);
        if (target is null || _dead.ContainsKey(f.TargetId))
        {
            bool killed = _dead.ContainsKey(f.TargetId) || (_lastHurt.TryGetValue(f.TargetId, out var h) && _tick - h < 40);
            if (killed) f.Kills++;
            string what = killed ? $"killed {f.TargetName}" : $"{f.TargetName} gone";
            if (!f.Explicit && f.FleeStart < 0 && Scan(phys) is { } next)
            {
                Log("retarget", phys, next.E.Position, new() { ["after"] = what, ["target"] = next.E.DisplayName });
                f.TargetId = next.E.Id;
                f.TargetName = next.E.DisplayName;
                f.PrevDist = f.BestDist = double.MaxValue;
                f.LastProgressTick = f.TargetSince = _tick;
                f.TargetHits = 0;
                f.Stance = null;
                return face;
            }
            EndFight(f, what, phys, ctx);
            return face;
        }

        var prof = CombatRules.Profile(target.TypeId);
        double dist = Horizontal(me, target.Position);
        if (dist > Options.MaxEngageDistance + 8) { EndFight(f, $"{f.TargetName} out of range", phys, ctx); return face; }
        if (dist < f.BestDist - 0.5) { f.BestDist = dist; f.LastProgressTick = _tick; }
        if (f.Cover) return CoverTick(f, phys, target, ctx);
        if (_tick - f.LastProgressTick > Options.StaleFightTicks)
        {
            f.GaveUp = true;
            EndFight(f, $"couldn't reach {f.TargetName} safely", phys, ctx);
            return face;
        }
        // A reflex never holds the body for a mob it can't hit (live 10-28: "busy: fighting skeleton 11.4m: 0 hits").
        if (!f.Explicit && f.Done is null && f.TargetHits == 0 && f.FleeStart < 0 && _tick - f.TargetSince > Options.ZeroHitGiveUpTicks
            && !InMeleeReach(phys, target) && !AnyFusingCreeper(me, out _))
        {
            f.GaveUp = true;
            EndFight(f, $"couldn't reach {f.TargetName} (0 hits in {Options.ZeroHitGiveUpTicks / 20}s), left it alone", phys, ctx);
            return face;
        }
        bool approaching = false;
        if ((_tick - f.StartTick) % 10 == 0) { approaching = dist < f.PrevDist - 0.3; f.PrevDist = dist; }
        else approaching = dist < f.PrevDist;
        Sample(f, phys, target);

        // ---- low health: disengage inward, then eat
        if (_w.Player.Health <= Options.DisengageHealth && f.FleeStart < 0)
        {
            f.FleeStart = _tick;
            if (!f.LowAlerted) { f.LowAlerted = true; Raise(CombatEventKind.Critical, $"hp {_w.Player.Health}/20 fighting {f.TargetName}: disengaging inward"); }
        }
        if (f.FleeStart >= 0)
        {
            var threats = _w.Entities.Nearby(me, 12, CombatRules.IsThreat).Select(e => e.Position).ToList();
            if (threats.Count == 0 || dist > 10 || _tick - f.FleeStart > 200) { EndFight(f, $"disengaged at hp {_w.Player.Health}", phys, ctx); return face; }
            var retreat = CombatGeometry.RetreatCell(_w, me, threats, 7, 8);
            // Retreat inward has nowhere to go (no cell, or no walk home from up here): box in instead of dying in the open.
            if (!f.ShelterAsked && ShouldShelter is { } shelter)
            {
                f.ShelterAsked = true;
                bool go;
                try { go = shelter(threats.Count, retreat != null); } catch { go = false; }
                if (go)
                {
                    Sheltering = true;
                    ctx.Shelter = true;
                    EndFight(f, $"boxing in at hp {_w.Player.Health} ({threats.Count} near, no way out)", phys, ctx);
                    return face;
                }
            }
            return MoveOrHold(phys, retreat, FaceAt(phys, target), sneak: false);
        }

        var look = FaceAt(phys, target);
        double reach = CombatRules.ReachTo(phys.EyePosition, CombatRules.Hitbox(target));
        int cooldown = Cooldown(target.Id);

        // ---- creepers: the fuse decides
        if (prof.Tactic == MobTactic.Creeper || AnyFusingCreeper(me, out _))
        {
            if (AnyFusingCreeper(me, out var fusing))
            {
                if (!f.FuseAlerted)
                {
                    f.FuseAlerted = true;
                    Log("creeper_fuse", phys, fusing!.Position, null);
                    Raise(CombatEventKind.Critical, $"creeper fusing {Horizontal(me, fusing.Position):0.#}m away: backing off");
                }
                var away = CombatGeometry.RetreatCell(_w, me, [fusing!.Position], 7, CombatRules.CreeperCancelRange + 0.5);
                if (away is null) { ctx.Block = HoldingSword(); return Slow(look); }
                return MoveOrHold(phys, away, look, sneak: false);
            }
            f.FuseAlerted = false;
            if (Options.Creeper == CreeperTactic.Avoid || _tick < f.BackoffUntil)
            {
                double keep = Options.Creeper == CreeperTactic.Avoid ? 6 : 5;
                if (dist < keep) return MoveOrHold(phys, CombatGeometry.RetreatCell(_w, me, [target.Position], 6, keep), look, sneak: false);
                return look;
            }
        }

        // ---- positioning: never with the void behind (knockback goes away from the attacker)
        Vec3d? moveTo = null;
        bool edgeBehind = CombatGeometry.EdgeBehind(_w, me, target.Position, Options.KnockbackDepth);
        if (edgeBehind)
        {
            if (f.Stance is not { } st || _tick > f.StanceUntil || CombatGeometry.EdgeBehind(_w, st.BottomCenter, target.Position, Options.KnockbackDepth))
            {
                f.Stance = CombatGeometry.ChooseStance(_w, me, target.Position, Options.KnockbackDepth)
                           ?? CombatGeometry.RetreatCell(_w, me, [target.Position], 6);
                f.StanceUntil = _tick + 20;
                Log("reposition", phys, target.Position, new() { ["to"] = f.Stance?.ToString() ?? "none", ["why"] = "edge behind" });
            }
            if (f.Stance is { } s2) moveTo = s2.BottomCenter;
        }
        else if (reach > Options.Reach - 0.2)
        {
            f.Stance = null;
            bool melee = prof.Tactic is MobTactic.Melee or MobTactic.Enderman or MobTactic.Creeper;
            if (prof.Tactic == MobTactic.Ranged)
                moveTo = RangedApproach(f, phys, target);
            else if (!(melee && approaching && dist < 8)) // let it come to us; otherwise close in on safe ground only
                moveTo = SafeApproachPoint(me, target.Position, Options.Reach - 0.6);
        }

        // ---- swing timing: one full hit per invulnerability window; crit when falling
        bool inReach = reach <= Options.Reach && CombatGeometry.HasLineOfSight(_w, phys.EyePosition, CombatRules.AimPoint(target));
        bool safeForJump = Safety.EdgeDistance(_w, (int)Math.Floor(me.X), (int)Math.Floor(me.Y + 1e-6), (int)Math.Floor(me.Z), 4) > Options.KnockbackDepth;
        bool jump = Options.Crits && inReach && phys.OnGround && cooldown is >= 4 and <= 6 && safeForJump && moveTo is null;
        bool rising = !phys.OnGround && phys.Velocity.Y > 0 && cooldown > -6;
        if (inReach && cooldown <= 0 && !rising)
        {
            Swing(f, target, phys, ctx);
            return moveTo is { } m0 ? CombatGeometry.SteerTo(me, m0, look.Yaw, look.Pitch) : look;
        }

        // ---- block between swings (melee threats close, sword in hand)
        if (Options.Block && HoldingSword() && dist <= 4 && cooldown >= 2 && !jump && phys.OnGround
            && prof.Tactic is not MobTactic.Creeper)
            ctx.Block = true;

        var input = moveTo is { } m ? CombatGeometry.SteerTo(me, m, look.Yaw, look.Pitch) : look;
        if (jump) input = input with { Jump = true };
        return ctx.Block ? Slow(input) : input;
    }

    /// <summary>Into cover from a ranged mob the bot can't reach: the nearest cell its eyes can't see, else away from it;
    /// ends once hidden or after <see cref="CombatOptions.CoverMaxTicks"/>.</summary>
    private MoveInput CoverTick(Fight f, PhysicsState phys, Entity target, Ctx ctx)
    {
        var me = phys.Position;
        var look = FaceAt(phys, target);
        bool seen = CombatGeometry.HasLineOfSight(_w, Eye(target), phys.EyePosition);
        if (!seen) { EndFight(f, $"took cover from {f.TargetName}", phys, ctx); return Hold(phys); }
        if (_tick - f.StartTick > Options.CoverMaxTicks) { EndFight(f, $"no cover from {f.TargetName} nearby, kept working", phys, ctx); return Hold(phys); }
        if (f.Stance is null || _tick > f.StanceUntil)
        {
            f.Stance = CombatGeometry.CoverCell(_w, me, Eye(target)) ?? CombatGeometry.RetreatCell(_w, me, [target.Position], 6, 8);
            f.StanceUntil = _tick + 20;
        }
        if (f.Stance is not { } c)
        {
            EndFight(f, $"no cover from {f.TargetName} nearby, kept working", phys, ctx);
            return Hold(phys);
        }
        return MoveOrHold(phys, c, look, sneak: false);
    }

    /// <summary>Ticks until the next hit may land on <paramref name="id"/> (≤ 0 = now).</summary>
    private int Cooldown(int id)
    {
        long ready = 0;
        if (_lastAttack.TryGetValue(id, out var a)) ready = Math.Max(ready, a + Options.AttackIntervalTicks);
        if (_lastHurt.TryGetValue(id, out var h)) ready = Math.Max(ready, h + CombatRules.InvulnerabilityTicks);
        return (int)Math.Max(-1000, ready - _tick);
    }

    private void Swing(Fight f, Entity target, PhysicsState phys, Ctx ctx)
    {
        if (!MayAttack(target)) return;
        ctx.Attack = target.Id;
        ctx.Block = false;
        _lastAttack[target.Id] = _tick;
        f.Swings++;
        int weapon = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id;
        bool couldCrit = !phys.OnGround && !phys.InWater && !phys.OnLadder; // fallDistance > 0 && !onGround (vanilla)
        _pending.Add((target.Id, _tick, weapon, couldCrit));
        Log("swing", phys, target.Position, new()
        {
            ["target"] = target.DisplayName, ["reach"] = Math.Round(CombatRules.ReachTo(phys.EyePosition, CombatRules.Hitbox(target)), 2),
            ["weapon"] = weapon, ["crit"] = couldCrit, ["hp"] = _w.Player.Health,
        });
    }

    private bool AnyFusingCreeper(Vec3d me, out Entity? creeper)
    {
        creeper = _w.Entities.Nearest(me, CombatRules.CreeperCancelRange + 1, CombatRules.IsCreeperFusing);
        return creeper is not null;
    }

    /// <summary>Skeleton: close in along safe ground (zig-zag), else break line of sight, else hold.</summary>
    private Vec3d? RangedApproach(Fight f, PhysicsState phys, Entity target)
    {
        var me = phys.Position;
        if (_tick % 15 == 0) f.StrafeSign = -f.StrafeSign;
        if (SafeApproachPoint(me, target.Position, 1.8) is { } p)
        {
            // Sidestep perpendicular to the line of fire while closing in.
            double dx = p.X - me.X, dz = p.Z - me.Z, len = Math.Max(1e-6, Math.Sqrt(dx * dx + dz * dz));
            var side = new Vec3d(me.X + dx / len - dz / len * 0.6 * f.StrafeSign, me.Y, me.Z + dz / len + dx / len * 0.6 * f.StrafeSign);
            return CombatGeometry.WalkSafe(_w, me, side.ToBlockPos() with { Y = (int)Math.Floor(me.Y + 1e-6) }, 2) ? side : p;
        }
        if (CombatGeometry.HasLineOfSight(_w, Eye(target), phys.EyePosition)
            && CombatGeometry.CoverCell(_w, me, Eye(target)) is { } cover) return cover.BottomCenter;
        return null;
    }

    /// <summary>A point <paramref name="standOff"/> short of the target, if walking there stays ≥ 2 from every edge.</summary>
    private Vec3d? SafeApproachPoint(Vec3d me, Vec3d target, double standOff)
    {
        double dx = target.X - me.X, dz = target.Z - me.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len <= standOff) return null;
        double t = (len - standOff) / len;
        var p = new Vec3d(me.X + dx * t, me.Y, me.Z + dz * t);
        // Only one step at a time: re-evaluated every tick, so check the next 1.5 blocks and the end point.
        var step = new Vec3d(me.X + dx / len * Math.Min(1.5, len - standOff), me.Y, me.Z + dz / len * Math.Min(1.5, len - standOff));
        int y = (int)Math.Floor(me.Y + 1e-6);
        var stepCell = new BlockPos((int)Math.Floor(step.X), y, (int)Math.Floor(step.Z));
        if (!CombatGeometry.WalkSafe(_w, me, stepCell, 2)) return null;
        if (Safety.EdgeDistance(_w, stepCell.X, y, stepCell.Z, 1) < 2) return null;
        return Horizontal(me, p) > 1.5 ? step : p;
    }

    // ------------------------------------------------------------------ sparring

    private MoveInput SparTick(Fight f, PhysicsState phys, Ctx ctx)
    {
        var s = f.Spar!;
        var me = phys.Position;
        var face = Hold(phys);
        var opp = _w.Entities.Get(s.OpponentId) ?? _w.Entities.FindPlayer(s.Opponent);
        if (opp is not null) s.OpponentId = opp.Id;

        string? stop = f.StopRequest;
        if (stop is null && opp is null)
        {
            if (s.MissingSince < 0) s.MissingSince = _tick;
            else if (_tick - s.MissingSince > 40) stop = "opponent out of sight";
        }
        else if (opp is not null) s.MissingSince = -1;
        stop ??= SparStopCheck(f, s, phys, opp);
        if (stop is not null) { EndFight(f, stop, phys, ctx); return face; }
        if (opp is null) return face;

        Sample(f, phys, opp);
        var look = FaceAt(phys, opp);

        // Rounds: first to N confirmed hits; short pause between rounds.
        if (s.Rounds is { } rounds && (s.RoundBot >= Options.SparRoundHits || s.RoundOpp >= Options.SparRoundHits))
        {
            string score = $"me {s.RoundBot} - {s.Opponent} {s.RoundOpp}";
            if (s.Round >= rounds) { EndFight(f, $"rounds done (last: {score})", phys, ctx); return face; }
            ctx.Chat = $"round {s.Round}: {score}. next round in 3s";
            Log("round", phys, opp.Position, new() { ["round"] = s.Round, ["score"] = score });
            s.Round++;
            s.RoundBot = s.RoundOpp = 0;
            s.PauseUntil = _tick + 60;
            return face;
        }
        if (_tick < s.PauseUntil)
        {
            if (s.PauseUntil - _tick == 1) ctx.Chat = "go!";
            return MoveOrHold(phys, new BlockPos(s.Center.X, s.Center.Y, s.Center.Z), look, sneak: false);
        }

        double reach = CombatRules.ReachTo(phys.EyePosition, CombatRules.Hitbox(opp));
        Vec3d? moveTo = null;
        // Positioning: my knockback (away from them) and theirs (away from me) should both point into the zone.
        // A sprinting player's hit throws ~4x further (b1.8 sprint knockback, CombatRules.SprintKnockbackHorizontal).
        double myDepth = opp.IsSprinting ? Math.Max(Options.KnockbackDepth, Options.SprintKnockbackDepth) : Options.KnockbackDepth;
        bool myBackUnsafe = CombatGeometry.EdgeBehind(_w, me, opp.Position, myDepth);
        bool theirBackUnsafe = CombatGeometry.EdgeBehind(_w, opp.Position, me, Options.KnockbackDepth);
        if (myBackUnsafe || theirBackUnsafe || (s.Stance is not null && _tick <= s.StanceUntil))
        {
            if (s.Stance is null || _tick > s.StanceUntil || CombatGeometry.EdgeBehind(_w, s.Stance.Value.BottomCenter, opp.Position, myDepth))
            {
                s.Stance = s.Zone.Stance(_w, me, opp.Position, myDepth);
                s.StanceUntil = _tick + 20;
                Log("reposition", phys, opp.Position, new() { ["to"] = s.Stance?.ToString() ?? "none", ["why"] = myBackUnsafe ? "edge behind me" : "edge behind them" });
            }
            if (s.Stance is { } st && Horizontal(me, st.BottomCenter) > 0.3) moveTo = st.BottomCenter;
        }
        else if (reach > Options.Reach - 0.3 && SafeApproachPoint(me, opp.Position, 2.2) is { } p && s.Zone.Contains(p))
            moveTo = p;

        int cooldown = Cooldown(opp.Id);
        bool inReach = reach <= Options.Reach && CombatGeometry.HasLineOfSight(_w, phys.EyePosition, CombatRules.AimPoint(opp));
        if (inReach && cooldown <= 0 && phys.OnGround)
        {
            // The opponent's knockback must land on safe ground too; and never a hit that could be fatal.
            int weapon = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id;
            double worst = s.Model.WorstHit(weapon, couldCrit: false, Armor(opp));
            if (s.Model.NextHitRisky(worst)) { EndFight(f, "next hit could be dangerous for the opponent", phys, ctx); return face; }
            int oppEdge = SparZone.EdgeAt(_w, opp.Position);
            if (oppEdge <= Options.SparNoSwingEdge)
                Log("hold_fire", phys, opp.Position, new() { ["why"] = $"opponent {oppEdge} from an edge" });
            else if (theirBackUnsafe)
                Log("hold_fire", phys, opp.Position, new() { ["why"] = "opponent's knockback would carry them near an edge" });
            else Swing(f, opp, phys, ctx);
        }
        return moveTo is { } m ? CombatGeometry.SteerTo(me, m, look.Yaw, look.Pitch) : look;
    }

    private string? SparStopCheck(Fight f, SparState s, PhysicsState phys, Entity? opp)
    {
        var me = phys.Position;
        int hp = _w.Player.Health;
        if (hp <= Options.SparSelfStopHealth) return $"my hp {hp} at threshold";
        if (opp is not null)
        {
            int incoming = CombatRules.DamageAfterArmor(CombatRules.MaxCritDamage(CombatRules.WeaponDamage(opp.HeldItem)),
                CombatRules.ArmorValue(Enumerable.Range(5, 4).Select(i => _w.Inventory.GetPlayerSlot(i))));
            if (hp - incoming <= 1) return $"my hp {hp}: their next hit could take {incoming}";
        }
        if (s.Model.StopReason() is { } r) return r;
        int stopEdge = Options.MinEdgeDistance - 1;
        if (phys.OnGround && SparZone.EdgeAt(_w, me) <= stopEdge) return "I got too close to the edge";
        if (opp is not null && SparZone.EdgeAt(_w, opp.Position) <= stopEdge) return $"{s.Opponent} got too close to the edge";
        if (s.Zone.OutsideDistance(me) > Options.ArenaLeaveSlack) return "I left the arena";
        if (opp is not null && s.Zone.OutsideDistance(opp.Position) > Options.ArenaLeaveSlack) return $"{s.Opponent} left the arena";
        var center = s.Center.BottomCenter;
        var mob = _w.Entities.Nearest(center, 16, e => e.Kind == EntityKind.Mob
            && (CombatRules.IsThreat(e) || s.Zone.OutsideDistance(e.Position) <= 2));
        if (mob is not null) return $"mob appeared: {mob.DisplayName}";
        if (_tick - f.StartTick > Options.SparMaxDuration.TotalSeconds * 20) return "time limit";
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private void Sample(Fight f, PhysicsState phys, Entity target)
    {
        if (_tick - f.LastSample < 10) return;
        f.LastSample = _tick;
        Log("sample", phys, target.Position, new()
        {
            ["hp"] = _w.Player.Health, ["est_hp"] = f.Spar?.Model.Estimate, ["block"] = _blocking, ["ground"] = phys.OnGround,
            ["edge"] = Safety.EdgeDistance(_w, (int)Math.Floor(phys.Position.X), (int)Math.Floor(phys.Position.Y + 1e-6), (int)Math.Floor(phys.Position.Z), 4),
        });
    }

    private MoveInput Hold(PhysicsState phys) => new(Yaw: phys.Yaw, Pitch: phys.Pitch);

    private static MoveInput FaceAt(PhysicsState phys, Entity e)
    {
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(phys.EyePosition, CombatRules.AimPoint(e));
        return new MoveInput(Yaw: yaw, Pitch: pitch);
    }

    private static MoveInput MoveOrHold(PhysicsState phys, BlockPos? cell, MoveInput look, bool sneak) =>
        cell is { } c ? CombatGeometry.SteerTo(phys.Position, c.BottomCenter, look.Yaw, look.Pitch, sneak) : look;

    private static MoveInput Slow(MoveInput i) =>
        i with { Forward = i.Forward * CombatRules.UseItemSlowdown, Strafe = i.Strafe * CombatRules.UseItemSlowdown, Sprint = false, Jump = false };

    private bool HoldingSword() => CombatRules.IsSword(_w.Inventory.HeldItem(_w.Player.HeldSlot).Id);

    private static double Horizontal(Vec3d a, Vec3d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static string Trim(string s) => s.Length <= 30 ? s : s[..30];

    private void Log(string ev, PhysicsState? phys, Vec3d? target, Dictionary<string, object?>? fields) =>
        Telemetry.Log(_tick, ev, phys?.Position, target, fields);

    private string Describe(PhysicsState phys)
    {
        var guards = Guarded;
        string reflex = $"defend {(DefendSelf ? "on" : "off")}, guard {(guards.Count == 0 ? "off" : string.Join("+", guards))}";
        if (_fight is not { } f) return "idle | " + reflex;
        var t = _w.Entities.Get(f.TargetId);
        string dist = t is null ? "?" : $"{t.Position.DistanceTo(phys.Position):0.#}m";
        if (f.Spar is { } s)
            return $"sparring {s.Opponent} {dist}: landed {f.Hits}, took {f.DamageTaken}, their est hp {s.Model.Estimate:0.#}"
                   + (s.Rounds is { } r ? $", round {s.Round}/{r}" : "") + " | " + reflex;
        return $"fighting {f.TargetName} {dist} ({f.Reason}): {f.Hits} hits, took {f.DamageTaken}, hp {_w.Player.Health}"
               + (f.FleeStart >= 0 ? ", retreating" : "") + " | " + reflex;
    }
}
