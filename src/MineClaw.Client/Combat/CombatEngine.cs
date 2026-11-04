using System.Collections.Concurrent;
using System.Diagnostics;
using MineClaw.Client.Versioning;
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
public sealed partial class CombatEngine
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
        public long StartTick, LastProgressTick, BackoffUntil, StanceUntil, FleeStart = -1, LastSample, TargetSince, WetSince = -1;
        public int Swings, Hits, Unconfirmed, DamageTaken, HitsReceived, Knockbacks, GuardInterventions, Kills, TargetHits;
        /// <summary>Not a fight: a short move into cover from a ranged mob the bot can't reach.</summary>
        public bool Cover;
        /// <summary>Cover from an archer it can't beat: held until hidden for a while or out of its range, not just one tick unseen.</summary>
        public bool Outmatched;
        /// <summary>Tick since which the archer hasn't seen the bot (cover holding), or -1.</summary>
        public long HiddenSince = -1;
        /// <summary>The reflex gave up on this target (it can't be reached): remembered so it doesn't re-trigger.</summary>
        public bool GaveUp;
        public double PrevDist = double.MaxValue, BestDist = double.MaxValue;
        public Vec3d? PrevTargetPos;
        public bool MobApproaching;
        public bool Explicit, FuseAlerted, LowAlerted, ShelterAsked;
        public long OutmatchCheck;
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
    /// <summary>The version's melee rules (the client sets its profile's): b1.8.1 spam clicks and sword blocks; 1.12.2 waits for the
    /// attack charge, crits and sweeps by its rules, blocks with a shield.</summary>
    public ICombatRules Rules { get; set; } = Beta181CombatRules.Instance;
    /// <summary>The shield is on its cooldown (an axe disabled it); null = never.</summary>
    public Func<bool>? ShieldDisabled { get; set; }

    // The 1.9 attack cooldown: ticksSinceLastSwing is reset by our own swings (any the server sees) and held-item changes.
    private long _ownSwing = long.MinValue / 4, _heldChanged = long.MinValue / 4;
    // ... and when they went out on the wall clock (Stopwatch timestamps): the server counts the charge in its own ticks, which
    // follow real time, not ours (see SinceSwing).
    private long _ownSwingAt = long.MinValue / 4, _heldChangedAt = long.MinValue / 4;
    private int _heldId = int.MinValue;
    private Vec3d _prevPos;
    private double _walked;
    private bool _emergency, _tick0 = true;

    /// <summary>An arm swing the client sent (digging, gestures, attacks): on 1.12.2 it resets the attack charge server-side.</summary>
    public void NoteOwnSwing(long tick)
    {
        Interlocked.Exchange(ref _ownSwing, Math.Max(Interlocked.Read(ref _ownSwing), tick));
        Interlocked.Exchange(ref _ownSwingAt, Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// The client's tick length (the server's nominal tick). Set: the charge also counts only the server ticks that have really
    /// passed since the last swing went out. The client's tick loop catches up in bursts (Windows sleeps in ~15.6 ms steps: three
    /// 5 ms test ticks at once, a third of a live 50 ms tick), so N of our ticks can be fewer than N of the server's, which
    /// follow real time; a swing on our count alone landed at 0.75 instead of 0.93. Zero = ticks only (unit tests drive ticks).
    /// </summary>
    public TimeSpan TickInterval { get; set; }

    /// <summary>The attack charge right now (1 on b1.8.1).</summary>
    public float Charge => Rules.AttackStrength(SinceSwing(), Math.Max(0, _heldId));

    /// <summary>The server's <c>ticksSinceLastSwing</c> as far as we can be sure of it: our ticks since the last swing or held-item
    /// change, but never more than the server ticks that fit in the real time since then (<see cref="TickInterval"/>), less half a
    /// tick for the two packets' different trips (the server counts from when it handled the swing, not when we sent it).</summary>
    private int SinceSwing()
    {
        long ticks = _tick - Math.Max(Interlocked.Read(ref _ownSwing), _heldChanged);
        if (TickInterval > TimeSpan.Zero)
        {
            long at = Math.Max(Interlocked.Read(ref _ownSwingAt), _heldChangedAt);
            if (at > 0) ticks = Math.Min(ticks, (long)Math.Floor(Stopwatch.GetElapsedTime(at).TotalMilliseconds / TickInterval.TotalMilliseconds - 0.5));
        }
        return (int)Math.Clamp(ticks, 0, 10000);
    }
    /// <summary>Bot username (its own chat lines are ignored for stop words).</summary>
    public string Username { get; set; } = "";
    /// <summary>Items never wielded (the client's reserved items).</summary>
    public Func<IReadOnlySet<int>> Reserved { get; set; } = () => new HashSet<int>();
    public event Action<CombatEvent>? EventRaised;

    public volatile bool DefendSelf;

    /// <summary>
    /// The brain's "brave" override (defend mode brave): no pre-emptive fights. Only a mob that hit the bot, one in reach or a fusing
    /// creeper starts one. The client clears it the moment the bot takes damage (live 10-30: main kept stopping Fox's stair dig to
    /// fight and box in against mobs that weren't hurting it).
    /// </summary>
    public volatile bool Brave;

    /// <summary>
    /// The no-path danger reflex (the client decides: blocks on hand, no walk home, low hp or outnumbered): called with the threat
    /// count and whether a retreat cell exists when a fight turns into a retreat. True = the fight ends ("boxing in") and the
    /// step asks the client to box the body in (<see cref="CombatStep.Shelter"/>).
    /// </summary>
    public Func<int, bool, bool>? ShouldShelter { get; set; }

    /// <summary>
    /// Whether the body could box itself in right now (blocks on hand, not already boxing in): asked when a 1.9+ melee fight can't be
    /// won (<see cref="Outmatched"/>), so it boxes in instead of trading hits it loses. Null = never.
    /// </summary>
    public Func<int, bool>? CanBoxIn { get; set; }

    /// <summary>The server's difficulty (0..3) for the mobs' damage; default normal.</summary>
    public Func<int>? Difficulty { get; set; }

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

    private readonly ConcurrentDictionary<int, DateTime> _permits = new();

    /// <summary>
    /// The owner's explicit order to fight this player entity (Story Mode: a drafted pawn told to attack a player or another bot, the
    /// owner having confirmed it), good until <paramref name="ttl"/> runs out. Only an explicit attack on exactly this entity may then
    /// hit it; nothing ever starts a fight with a player by itself.
    /// </summary>
    public void Permit(int entityId, TimeSpan ttl) => _permits[entityId] = DateTime.UtcNow + ttl;

    /// <summary>Withdraws a permit (undrafted, the order cancelled).</summary>
    public void Revoke(int entityId) => _permits.TryRemove(entityId, out _);

    /// <summary>An owner's permit to attack this player entity is in force (<see cref="Permit"/>).</summary>
    public bool Permitted(int entityId) => _permits.TryGetValue(entityId, out var until) && until > DateTime.UtcNow;

    /// <summary>Hard gate used by the packet layer: may the bot hit this entity right now?</summary>
    public bool MayAttack(Entity e)
    {
        if (e.Kind == EntityKind.Player)
            return _fight is { Kind: FightKind.Spar, Spar: { } s } && s.OpponentId == e.Id && _tick >= s.PauseUntil
                || _fight is { Kind: FightKind.Mob, Explicit: true } f && f.TargetId == e.Id && Permitted(e.Id);
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
        TrackHeldItem(tick);
        TrackWalked(phys);
        while (_signals.TryDequeue(out var sig)) Handle(sig, phys, ctx);
        TrackHealth(phys, ctx);
        ExpirePending(ctx);

        if (tick % 100 == 0) ForgetGoneMobs();
        if (Sheltering && _fight is { Explicit: false, Done: null, Kind: FightKind.Mob } sf) EndFight(sf, "boxed in", phys, ctx);
        if (_fight is null && !Sheltering && tick % 5 == 0 && Scan(phys) is { } found) Engage(found, phys, ctx);
        var step = AvoidEndermanGaze(phys, FightOrIdleStep(phys, proposed, ctx));
        if (step.AttackId >= 0) step = step with { Block = false };
        _blocking = step.Block;
        _engaged = _fight is not null;
        _status = Describe(phys);
        return step;
    }

    /// <summary>A new held item restarts the attack cooldown (EntityPlayer.onUpdate resetCooldown).</summary>
    private void TrackHeldItem(long tick)
    {
        int heldNow = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id;
        if (heldNow == _heldId) return;
        if (_heldId != int.MinValue) { _heldChanged = tick; _heldChangedAt = Stopwatch.GetTimestamp(); }
        _heldId = heldNow;
    }

    /// <summary>distanceWalkedModified − prev (Entity.move).</summary>
    private void TrackWalked(PhysicsState phys)
    {
        double mdx = phys.Position.X - _prevPos.X, mdz = phys.Position.Z - _prevPos.Z;
        _walked = _tick0 ? 0 : Math.Sqrt(mdx * mdx + mdz * mdz) * 0.6;
        _prevPos = phys.Position;
        _tick0 = false;
    }

    /// <summary>A threat the scan found: fights it, takes cover from an archer it can't beat, or boxes in from a melee fight it would
    /// lose (1.9+: bare hands vs a zombie: 2 dps against 20 hp while taking 3/s; live 10-29 02:07, clone 69: "fight started: zombie",
    /// slain 3 s later).</summary>
    private void Engage((Entity E, string Why, bool Cover) found, PhysicsState phys, Ctx ctx)
    {
        string? lost = !found.Cover && MayBoxInstead(found.Why) ? Outmatched(phys, found.E) : null;
        if (lost is { } why && CombatRules.ProfileOf(found.E).Tactic == MobTactic.Ranged && CombatGeometry.HasLineOfSight(_w, Eye(found.E), phys.EyePosition))
        {
            // 1.12.2, live 10-29: bodies with bare hands traded blows with skeletons in tree shade and were shot dead (102,
            // 117). An archer it can't beat: out of its sight (cover, else away), held until it has lost us.
            ctx.Abort = StartFight(FightKind.Mob, found.E, $"taking cover: can't win ({why})", phys, explicitDone: null);
            _fight!.Cover = true;
            _fight.Outmatched = true;
        }
        else if (lost is { } why2 && CanBoxIn is { } can && SafeAsk(can, MeleeThreats(phys.Position).Count))
        {
            Sheltering = true;
            ctx.Shelter = true;
            ctx.Abort = true;
            Raise(CombatEventKind.Critical, $"{found.E.DisplayName} {found.E.Position.DistanceTo(phys.Position):0}m away and I can't win ({why2}): boxing in");
        }
        else
        {
            ctx.Abort = StartFight(FightKind.Mob, found.E, found.Why, phys, explicitDone: null);
            if (found.Cover) _fight!.Cover = true;
        }
    }

    private CombatStep FightOrIdleStep(PhysicsState phys, MoveInput proposed, Ctx ctx)
    {
        if (_fight is not { } f)
            return new CombatStep(proposed, false, Chat: ctx.Chat, Eat: ctx.Eat, Equip: ctx.Equip, AbortAction: ctx.Abort, Shelter: ctx.Shelter);
        var input = f.Kind == FightKind.Spar ? SparTick(f, phys, ctx) : MobTick(f, phys, ctx);
        return new CombatStep(input, true, ctx.Attack, ctx.Block, ctx.Equip, ctx.Chat, ctx.Abort, ctx.Eat, ctx.Shelter);
    }

    /// <summary>Never meet an enderman's gaze (unless it is the one being fought).</summary>
    private CombatStep AvoidEndermanGaze(PhysicsState phys, CombatStep step)
    {
        var ender = _w.Entities.Nearby(phys.Position, 64, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Enderman
                                                            && (_fight is null || e.Id != _fight.TargetId));
        if (ender.Count == 0) return step;
        var centers = ender.Select(e => { var b = CombatRules.Hitbox(e); return b.Center; }).ToList();
        float p = CombatRules.AvoidEndermanGaze(phys.EyePosition, step.Input.Yaw, step.Input.Pitch, centers);
        return step with { Input = step.Input with { Pitch = p } };
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
            case ChatSig c: OnChatSignal(c); break;
            case AnimSig a: OnAnimSignal(a, phys); break;
            case StatusSig s: OnStatusSignal(s, phys); break;
            case DangerSig d: OnDangerSignal(d); break;
            case StopCmd stop:
                if (_fight is { } f) f.StopRequest ??= stop.Reason;
                break;
            case SparCmd sc: OnSparCommand(sc, phys); break;
            case AttackCmd ac: OnAttackCommand(ac, phys); break;
        }
    }

    private void OnChatSignal(ChatSig c)
    {
        if (_fight is { Kind: FightKind.Spar } sf && SparWords.IsStop(c.Message, Options.StopWords))
            sf.StopRequest ??= $"{c.Sender} said \"{Trim(c.Message)}\"";
    }

    private void OnAnimSignal(AnimSig a, PhysicsState phys)
    {
        if (a.Anim == CombatRules.AnimSwing) _lastSwing[a.Id] = _tick;
        else if (a.Anim == CombatRules.AnimHurt) OnHurt(a.Id, phys);
    }

    private void OnStatusSignal(StatusSig s, PhysicsState phys)
    {
        if (s.Status == CombatRules.StatusHurt) OnHurt(s.Id, phys);
        else if (s.Status == CombatRules.StatusDead) _dead[s.Id] = _tick;
    }

    private void OnDangerSignal(DangerSig d)
    {
        if (_fight is { Kind: FightKind.Spar } spar && d.Danger.Kind is not (DangerKind.Damage or DangerKind.HostileNearby or DangerKind.Recovered))
            spar.StopRequest ??= "danger: " + d.Danger.Message;
    }

    private void OnSparCommand(SparCmd sc, PhysicsState phys)
    {
        if (_fight is not null) { sc.Done.TrySetResult($"refused: already fighting {_fight.TargetName}"); return; }
        if (_w.Entities.Get(sc.OpponentId) is not { Kind: EntityKind.Player } opp) { sc.Done.TrySetResult("refused: opponent not visible"); return; }
        var zr = sc.Zone is { } given ? new SparZoneResult(given, null) : SparZone.Find(_w, sc.Center.BottomCenter, Options, [phys.Position, opp.Position]);
        if (zr.Zone is not { } zone) { sc.Done.TrySetResult("refused: no safe arena: " + zr.Reason); return; }
        StartFight(FightKind.Spar, opp, "spar", phys, sc.Done);
        _fight!.Spar = new SparState
        {
            Opponent = sc.Opponent, OpponentId = sc.OpponentId, Center = zone.Center, Zone = zone, Rounds = sc.Rounds,
            Model = new OpponentHpModel(Options, SparStartHealth(sc.Opponent), Rules),
            WeaponId = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id,
        };
        Log("spar_start", phys, opp.Position, new() { ["opponent"] = sc.Opponent, ["est_hp"] = _fight.Spar.Model.Estimate, ["center"] = sc.Center.ToString(), ["rounds"] = sc.Rounds });
    }

    private void OnAttackCommand(AttackCmd ac, PhysicsState phys)
    {
        if (_fight is not null) { ac.Done.TrySetResult($"refused: already fighting {_fight.TargetName}"); return; }
        if (_w.Entities.Get(ac.TargetId) is not { } t) { ac.Done.TrySetResult("refused: target gone"); return; }
        // A player only with the owner's permit for exactly that entity (MayAttack then lets each hit through).
        if (t.Kind == EntityKind.Player ? !Permitted(t.Id) : !MayAttack(t)) { ac.Done.TrySetResult($"refused: won't attack {t.DisplayName}"); return; }
        StartFight(FightKind.Mob, t, "told to attack", phys, ac.Done);
        _fight!.Explicit = true;
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
        if (_fight is { } f) RecordHitTaken(f, phys, attacker, dmg, hp);
    }

    /// <summary>A hit taken in a fight: counted (the spar opponent's per round), logged, and a spar stops on a hit from anything else.</summary>
    private void RecordHitTaken(Fight f, PhysicsState phys, Entity? attacker, int dmg, int hp)
    {
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

    /// <summary>Defending a guarded player is never abandoned for a box while the body's own hp holds (the low-hp reflexes still
    /// come first): live 10-29, Fox and Robin were killed by a zombie at a door while main stayed put.</summary>
    private bool MayBoxInstead(string why) =>
        !why.StartsWith("guarding", StringComparison.Ordinal) || _w.Player.Health <= Options.DisengageHealth + 2;

    private static bool SafeAsk(Func<int, bool> f, int n)
    {
        try { return f(n); } catch { return false; }
    }

    /// <summary>Melee threats within 8 blocks (what can hit the body in the next seconds).</summary>
    /// <summary>Melee mobs within 8 that can get at the bot: a zombie in the next cave behind stone is no part of the fight (live
    /// 10-30: "3 mobs with 53 hp, taking 10.6/s" behind the shaft's walls boxed main in mid-dig).</summary>
    private List<Entity> MeleeThreats(Vec3d me) =>
        _w.Entities.Nearby(me, 8, e => CombatRules.IsThreat(e) && CombatRules.ProfileOf(e).Tactic == MobTactic.Melee && Senses(e, me)).ToList();

    /// <summary>The fight's kill and die times (s) and its description; null without a 1.9+ cooldown or a melee target.</summary>
    private (double Kill, double Die, string Text)? Estimate(PhysicsState phys, Entity target)
    {
        if (Rules.AttackCooldownTicks(0) <= 0) return null;
        var tactic = CombatRules.ProfileOf(target).Tactic;
        bool ranged = tactic == MobTactic.Ranged;
        if (tactic != MobTactic.Melee && !ranged || !CombatRules.IsThreat(target)) return null;
        var pick = WeaponChoice.Best(_w.Inventory.PlayerSlots(), Reserved(), Options.NeverWield, Rules);
        if (EstimateDps(pick, ranged) is not { } dps) return null;
        var mobs = MeleeThreats(phys.Position);
        if (!mobs.Any(m => m.Id == target.Id)) mobs.Add(target);
        int diff = Difficulty?.Invoke() ?? 2;
        double incoming = IncomingDps(phys, target, ranged, mobs, diff);
        if (incoming <= 0) return null;
        double health = mobs.Sum(m => m.Health is float h and > 0 ? h : CombatRules.ProfileOf(m).MaxHealth);
        double walk = ranged ? Math.Max(0, Horizontal(phys.Position, target.Position) - Options.Reach) / RangedApproachSpeed : 0;
        double kill = health / dps + walk, die = Math.Max(0, _w.Player.Health - 1) / incoming;
        return (kill, die, EstimateText(pick, dps, mobs.Count, health, incoming));
    }

    /// <summary>The weapon's damage per second (null without one). An archer strafes and backs off (1.12.2 EntityAIAttackRangedBow):
    /// at best about one landed swing a second, after the walk in.</summary>
    private double? EstimateDps(WeaponPick pick, bool ranged)
    {
        if (WeaponChoice.Dps(pick.ItemId, Rules) is not { } dps || dps <= 0) return null;
        return ranged ? Math.Min(dps, Rules.ChargedDamage(Math.Max(0, pick.ItemId), 1f) * RangedHitsPerSecond) : dps;
    }

    /// <summary>Damage per second coming in: the melee mobs' (the target, knocked back by every charged hit, lands about 0.6 of its
    /// hits) and the archers' in sight within their 15-block range (a shield in the off hand takes most arrows).</summary>
    private double IncomingDps(PhysicsState phys, Entity target, bool ranged, List<Entity> mobs, int diff)
    {
        double incoming = mobs.Where(m => CombatRules.ProfileOf(m).Tactic != MobTactic.Ranged)
            .Sum(m => CombatRules.MonsterDamageToPlayer(CombatRules.MeleeDamageOf(m), diff) * (m.Id == target.Id ? KnockedBackHitShare : 1.0));
        var archers = _w.Entities.Nearby(phys.Position, 16, e => CombatRules.IsThreat(e) && CombatRules.ProfileOf(e).Tactic == MobTactic.Ranged
            && (e.Id == target.Id || CombatGeometry.HasLineOfSight(_w, Eye(e), phys.EyePosition))).ToList();
        if (ranged && !archers.Any(a => a.Id == target.Id)) archers.Add(target);
        return incoming + archers.Count * ArrowDps(diff) * (CanBlockNow() ? ShieldArrowShare : 1.0);
    }

    private string EstimateText(WeaponPick pick, double dps, int mobs, double health, double incoming)
    {
        string weapon = pick.ItemId > 0 ? Items.Get(pick.ItemId).Name.Replace('_', ' ') : "bare hands";
        return $"{weapon} {dps:0.#} dmg/s vs {mobs} mob{(mobs > 1 ? "s" : "")} with {health:0} hp, taking {incoming:0.#}/s at hp {_w.Player.Health}";
    }

    /// <summary>Beyond this an archer stops shooting (its 15-block attack range, plus a margin).</summary>
    public const double OutOfArcherRange = 18;
    /// <summary>Unseen this long, the archer has lost the bot (cover holds).</summary>
    public const int HiddenHoldTicks = 40;
    /// <summary>Longest an outmatched cover lasts before the job resumes.</summary>
    public const int OutmatchedCoverMaxTicks = 400;

    /// <summary>Landed swings per second on a strafing archer, at most.</summary>
    public const double RangedHitsPerSecond = 1.0;
    /// <summary>Closing speed on an archer (sprinting zig-zag, m/s).</summary>
    public const double RangedApproachSpeed = 4.5;
    /// <summary>Arrows that still hurt with a shield raised toward the archer.</summary>
    public const double ShieldArrowShare = 0.35;

    /// <summary>
    /// One skeleton's arrow damage per second: 1.12.2 AbstractSkeleton.setCombatTask attacks every 40 ticks (20 on hard), an arrow at
    /// velocity 1.6 does about 3-4 (EntityArrow: ceil(velocity × 2)), and about 3 in 4 hit a target that isn't hiding.
    /// </summary>
    public static double ArrowDps(int difficulty) => difficulty >= 3 ? 3.5 * 0.75 : 3.5 * 0.75 / 2;

    /// <summary>Share of its attacks a melee target lands while our charged hits knock it back (it walks in again each time).</summary>
    public const double KnockedBackHitShare = 0.6;

    /// <summary>
    /// 1.9+ only (b1.8.1 spam-clicks, no estimate): why a melee fight against <paramref name="target"/> (and the other melee mobs
    /// within 8) can't be won, or null. Our damage per second is the best weapon's charged hits (<see cref="WeaponChoice.Dps"/>);
    /// theirs is each mob's melee damage at the difficulty once a second (<c>EntityAIAttackMelee</c>'s 20-tick attack wait; the target,
    /// knocked back by each hit, lands <see cref="KnockedBackHitShare"/> of them). Lost
    /// when killing them all takes longer than ¾ of the time to drop to 1 hp.
    /// </summary>
    public string? Outmatched(PhysicsState phys, Entity target) =>
        Estimate(phys, target) is { } e && e.Kill > e.Die * 0.75 ? e.Text : null;

    private readonly Dictionary<int, (long Tick, BlockPos From, BlockPos At, bool Ok)> _walkable = new();
    private readonly Dictionary<int, (long Tick, bool Ok)> _sensed = new();

    /// <summary><see cref="CombatGeometry.CanGetAt"/>, cached half a second per mob.</summary>
    private bool Senses(Entity e, Vec3d me)
    {
        if (_sensed.TryGetValue(e.Id, out var c) && _tick - c.Tick < 10) return c.Ok;
        bool ok = CombatGeometry.CanGetAt(_w, e, me);
        if (_sensed.Count > 64) _sensed.Clear();
        _sensed[e.Id] = (_tick, ok);
        return ok;
    }

    /// <summary>Nearest hostile within <paramref name="radius"/> that can get at the bot (<see cref="CombatGeometry.CanGetAt"/>).</summary>
    public Entity? NearestThreat(Vec3d me, double radius) =>
        _w.Entities.Nearby(me, radius, e => e.IsHostileMob && CombatGeometry.CanGetAt(_w, e, me), 1).FirstOrDefault();

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
            var pick = WeaponChoice.Best(slots, Reserved(), Options.NeverWield, Rules);
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
        ctx.Eat = food <= Options.EatBelowFood && NearestThreat(phys.Position, 10) is null;
        Raise(CombatEventKind.FightEnded, "fight ended: " + summary, f.Done is not null);
        f.Done?.TrySetResult(summary);
    }

    private void Raise(CombatEventKind kind, string msg, bool requested = false)
    {
        var ev = new CombatEvent(kind, msg) { Requested = requested };
        try { EventRaised?.Invoke(ev); } catch { /* handlers must not break the tick */ }
    }

    // ------------------------------------------------------------------ mob fights

    /// <summary>Into cover from a ranged mob the bot can't reach: the nearest cell its eyes can't see, else away from it;
    /// ends once hidden or after <see cref="CombatOptions.CoverMaxTicks"/>.</summary>
    private MoveInput CoverTick(Fight f, PhysicsState phys, Entity target, Ctx ctx)
    {
        var me = phys.Position;
        var look = FaceAt(phys, target);
        bool seen = CombatGeometry.HasLineOfSight(_w, Eye(target), phys.EyePosition);
        if (f.Outmatched) return OutmatchedCoverTick(f, phys, target, ctx, look, seen);
        if (!seen) { EndFight(f, $"took cover from {f.TargetName}", phys, ctx); return Hold(phys); }
        if (_tick - f.StartTick > Options.CoverMaxTicks) { EndFight(f, $"no cover from {f.TargetName} nearby, kept working", phys, ctx); return Hold(phys); }
        RefreshCoverStance(f, me, target, 6, 8);
        if (f.Stance is not { } c)
        {
            EndFight(f, $"no cover from {f.TargetName} nearby, kept working", phys, ctx);
            return Hold(phys);
        }
        if (Options.Block && CanBlockNow(target)) { ctx.Block = true; return Slow(MoveOrHold(phys, c, look, sneak: false)); }
        return MoveOrHold(phys, c, look, sneak: false);
    }

    /// <summary>An archer it can't beat: stay hidden until it has lost us (2 s unseen) or we are out of its range; a shield faces it
    /// while exposed; keep moving away from it when there's no cover.</summary>
    private MoveInput OutmatchedCoverTick(Fight f, PhysicsState phys, Entity target, Ctx ctx, MoveInput look, bool seen)
    {
        var me = phys.Position;
        double far = Horizontal(me, target.Position);
        if (!seen) { if (f.HiddenSince < 0) f.HiddenSince = _tick; } else f.HiddenSince = -1;
        if (OutmatchedCoverEnded(f, phys, ctx, far)) return Hold(phys);
        if (!seen) return Hold(phys) with { Yaw = look.Yaw, Pitch = look.Pitch };
        RefreshCoverStance(f, me, target, 8, 10);
        var go = f.Stance is { } st ? MoveOrHold(phys, st, look, sneak: false) : look;
        if (Options.Block && CanBlockNow(target)) { ctx.Block = true; return Slow(go); }
        return go with { Sprint = f.Stance is not null && _w.Player.Food > 6 };
    }

    /// <summary>Out of its range, or hidden long enough (it has lost us), or kept away long enough: the cover fight ends.</summary>
    private bool OutmatchedCoverEnded(Fight f, PhysicsState phys, Ctx ctx, double far)
    {
        if (far > OutOfArcherRange || f.HiddenSince >= 0 && _tick - f.HiddenSince >= HiddenHoldTicks)
        {
            EndFight(f, $"took cover from {f.TargetName} ({(far > OutOfArcherRange ? "out of its range" : "out of its sight")})", phys, ctx);
            return true;
        }
        if (_tick - f.StartTick <= OutmatchedCoverMaxTicks) return false;
        EndFight(f, $"kept away from {f.TargetName}", phys, ctx);
        return true;
    }

    /// <summary>The cover cell (out of its eyes' sight), else a retreat cell within <paramref name="radius"/> at least
    /// <paramref name="minDistance"/> from it; looked for again once a second.</summary>
    private void RefreshCoverStance(Fight f, Vec3d me, Entity target, int radius, double minDistance)
    {
        if (f.Stance is not null && _tick <= f.StanceUntil) return;
        f.Stance = CombatGeometry.CoverCell(_w, me, Eye(target)) ?? CombatGeometry.RetreatCell(_w, me, [target.Position], radius, minDistance);
        f.StanceUntil = _tick + 20;
    }

    /// <summary>Ticks until the next hit may land on <paramref name="id"/> (≤ 0 = now). 1.9+ also waits for the attack charge to
    /// reach <see cref="CombatOptions.MinCharge"/> (except in an emergency).</summary>
    private int Cooldown(int id)
    {
        long ready = 0;
        if (_lastAttack.TryGetValue(id, out var a)) ready = Math.Max(ready, a + Options.AttackIntervalTicks);
        if (_lastHurt.TryGetValue(id, out var h)) ready = Math.Max(ready, h + Rules.InvulnerabilityTicks);
        int held = Math.Max(0, _heldId);
        if (!_emergency && Rules.AttackCooldownTicks(held) > 0)
            ready = Math.Max(ready, _tick + ChargeTicks(held) - SinceSwing());
        return (int)Math.Max(-1000, ready - _tick);
    }

    private int _chargeItem = int.MinValue, _chargeTicks;

    /// <summary>The first tick after a swing at which the charge reaches <see cref="CombatOptions.MinCharge"/>.</summary>
    private int ChargeTicks(int held)
    {
        if (held == _chargeItem) return _chargeTicks;
        int t = 0;
        while (t < 200 && Rules.AttackStrength(t, held) < Options.MinCharge) t++;
        (_chargeItem, _chargeTicks) = (held, t);
        return t;
    }

    /// <summary>The bot's state for the version's crit and sweep rules.</summary>
    private AttackContext Context(PhysicsState phys, Entity? target)
    {
        int held = Math.Max(0, _heldId);
        return new AttackContext(Rules.AttackStrength(SinceSwing(), held), phys.Velocity.Y, phys.FallDistance, phys.OnGround, phys.OnLadder, phys.InWater,
            phys.Sprinting, held, Blind: _w.Player.HasEffect(PotionEffects.Blindness), Riding: false,
            TargetLiving: target is null || target.Kind is EntityKind.Mob or EntityKind.Player, WalkedThisTick: _walked,
            AiMoveSpeed: float.IsNaN(phys.LandSpeed) ? 0.1 : phys.LandSpeed);
    }

    /// <summary>A living entity other than the target that a sweep would hit and must not: a player, a pet, a neutral or passive mob.</summary>
    private Entity? SweepVictim(PhysicsState phys, Entity target)
    {
        var box = Rules.SweepBox(CombatRules.Hitbox(target));
        foreach (var e in _w.Entities.Nearby(phys.Position, 3))
        {
            if (e.Id == target.Id || e.Kind is not (EntityKind.Player or EntityKind.Mob)) continue;
            if (e.Kind == EntityKind.Mob && CombatRules.IsThreat(e) && !CombatRules.IsTamedWolf(e)) continue;
            if (box.Intersects(CombatRules.Hitbox(e))) return e;
        }
        return null;
    }

    /// <summary>A block can go up now: b1.8.1 a sword in hand; 1.9+ a shield (not on its cooldown, and not against an axe, which
    /// would disable it).</summary>
    private bool CanBlockNow(Entity? attacker = null)
    {
        var main = _w.Inventory.HeldItem(_w.Player.HeldSlot);
        if (!Rules.CanBlock(main, _w.Inventory.Offhand)) return false;
        if (Rules.ShieldDisableTicks > 0 && (ShieldDisabled?.Invoke() ?? false)) return false;
        return attacker is null || !Rules.DisablesShield(attacker.HeldItem);
    }

    private void Swing(Fight f, Entity target, PhysicsState phys, Ctx ctx)
    {
        if (!MayAttack(target)) return;
        ctx.Attack = target.Id;
        ctx.Block = false;
        _lastAttack[target.Id] = _tick;
        f.Swings++;
        int weapon = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id;
        var ac = Context(phys, target);
        // b1.8.1: fallDistance > 0 && !onGround (vanilla); 1.9+ also a charge > 0.9 and no sprint (Rules.IsCriticalHit).
        bool couldCrit = Rules.AttackCooldownTicks(weapon) > 0 ? Rules.IsCriticalHit(ac) : !phys.OnGround && !phys.InWater && !phys.OnLadder;
        _pending.Add((target.Id, _tick, weapon, couldCrit));
        NoteOwnSwing(_tick);
        Log("swing", phys, target.Position, new()
        {
            ["target"] = target.DisplayName, ["reach"] = Math.Round(CombatRules.ReachTo(phys.EyePosition, CombatRules.Hitbox(target)), 2),
            ["weapon"] = weapon, ["crit"] = couldCrit, ["hp"] = _w.Player.Health,
            ["charge"] = Rules.AttackCooldownTicks(weapon) > 0 ? Math.Round(ac.Strength, 2) : null,
            ["sweep"] = Rules.IsSweep(ac) ? true : null,
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
    private Vec3d? SafeApproachPoint(Vec3d me, Vec3d target, double standOff) => SafeApproachPoint(me, target, standOff, out _);

    /// <param name="climb">The next step is a safe one-block rise: jump it instead of ending the chase at a ledge.</param>
    private Vec3d? SafeApproachPoint(Vec3d me, Vec3d target, double standOff, out bool climb)
    {
        climb = false;
        double dx = target.X - me.X, dz = target.Z - me.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len <= standOff) return null;
        double t = (len - standOff) / len;
        var p = new Vec3d(me.X + dx * t, me.Y, me.Z + dz * t);
        // Only one step at a time: re-evaluated every tick, so check the next 1.5 blocks and the end point.
        var step = new Vec3d(me.X + dx / len * Math.Min(1.5, len - standOff), me.Y, me.Z + dz / len * Math.Min(1.5, len - standOff));
        int y = (int)Math.Floor(me.Y + 1e-6);
        var stepCell = new BlockPos((int)Math.Floor(step.X), y, (int)Math.Floor(step.Z));
        if (!CombatGeometry.WalkSafe(_w, me, stepCell, 2))
        {
            if (!CombatGeometry.CanStepUp(_w, me, stepCell)) return null;
            climb = true;
            stepCell = stepCell with { Y = y + 1 };
        }
        if (Safety.EdgeDistance(_w, stepCell.X, stepCell.Y, stepCell.Z, 1) < 2) return null;
        return Horizontal(me, p) > 1.5 || climb ? step : p;
    }

    // ------------------------------------------------------------------ sparring

    private MoveInput SparTick(Fight f, PhysicsState phys, Ctx ctx)
    {
        var s = f.Spar!;
        var face = Hold(phys);
        var opp = _w.Entities.Get(s.OpponentId) ?? _w.Entities.FindPlayer(s.Opponent);
        if (opp is not null) s.OpponentId = opp.Id;
        if (SparStopReason(f, s, phys, opp) is { } stop) { EndFight(f, stop, phys, ctx); return face; }
        if (opp is null) return face;

        Sample(f, phys, opp);
        var look = FaceAt(phys, opp);
        if (SparBetweenRounds(f, s, phys, ctx, opp, look, face) is { } between) return between;
        return SparFightTick(f, s, phys, ctx, opp, look, face);
    }

    /// <summary>Why the spar stops now: the request, the opponent out of sight for 2 s, or <see cref="SparStopCheck"/>.</summary>
    private string? SparStopReason(Fight f, SparState s, PhysicsState phys, Entity? opp)
    {
        string? stop = f.StopRequest;
        if (stop is null && opp is null)
        {
            if (s.MissingSince < 0) s.MissingSince = _tick;
            else if (_tick - s.MissingSince > 40) stop = "opponent out of sight";
        }
        else if (opp is not null) s.MissingSince = -1;
        return stop ?? SparStopCheck(f, s, phys, opp);
    }

    /// <summary>Rounds: first to N confirmed hits; short pause between rounds (back to the centre, "go!" as it ends). Null = fighting.</summary>
    private MoveInput? SparBetweenRounds(Fight f, SparState s, PhysicsState phys, Ctx ctx, Entity opp, MoveInput look, MoveInput face)
    {
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
        if (_tick >= s.PauseUntil) return null;
        if (s.PauseUntil - _tick == 1) ctx.Chat = "go!";
        return MoveOrHold(phys, new BlockPos(s.Center.X, s.Center.Y, s.Center.Z), look, sneak: false);
    }

    private MoveInput SparFightTick(Fight f, SparState s, PhysicsState phys, Ctx ctx, Entity opp, MoveInput look, MoveInput face)
    {
        var me = phys.Position;
        double reach = CombatRules.ReachTo(phys.EyePosition, CombatRules.Hitbox(opp));
        // Positioning: my knockback (away from them) and theirs (away from me) should both point into the zone.
        // A sprinting player's hit throws ~4x further (b1.8 sprint knockback, CombatRules.SprintKnockbackHorizontal).
        double myDepth = opp.IsSprinting ? Math.Max(Options.KnockbackDepth, Options.SprintKnockbackDepth) : Options.KnockbackDepth;
        bool myBackUnsafe = CombatGeometry.EdgeBehind(_w, me, opp.Position, myDepth);
        bool theirBackUnsafe = CombatGeometry.EdgeBehind(_w, opp.Position, me, Options.KnockbackDepth);
        var moveTo = SparMoveTo(s, phys, opp, reach, myDepth, myBackUnsafe, theirBackUnsafe);

        int cooldown = Cooldown(opp.Id);
        bool inReach = reach <= Options.Reach && CombatGeometry.HasLineOfSight(_w, phys.EyePosition, CombatRules.AimPoint(opp));
        if (inReach && cooldown <= 0 && phys.OnGround && SparSwingEnded(f, s, phys, ctx, opp, theirBackUnsafe)) return face;
        return moveTo is { } m ? CombatGeometry.SteerTo(me, m, look.Yaw, look.Pitch) : look;
    }

    /// <summary>Where to stand: a stance with both knockbacks into the zone while an edge is behind either of us (or the last stance
    /// still holds), else closer in on safe ground inside the zone.</summary>
    private Vec3d? SparMoveTo(SparState s, PhysicsState phys, Entity opp, double reach, double myDepth, bool myBackUnsafe, bool theirBackUnsafe)
    {
        if (myBackUnsafe || theirBackUnsafe || (s.Stance is not null && _tick <= s.StanceUntil))
            return SparStance(s, phys, opp, myDepth, myBackUnsafe);
        if (reach > Options.Reach - 0.3 && SafeApproachPoint(phys.Position, opp.Position, 2.2) is { } p && s.Zone.Contains(p)) return p;
        return null;
    }

    private Vec3d? SparStance(SparState s, PhysicsState phys, Entity opp, double myDepth, bool myBackUnsafe)
    {
        var me = phys.Position;
        if (s.Stance is null || _tick > s.StanceUntil || CombatGeometry.EdgeBehind(_w, s.Stance.Value.BottomCenter, opp.Position, myDepth))
        {
            s.Stance = s.Zone.Stance(_w, me, opp.Position, myDepth);
            s.StanceUntil = _tick + 20;
            Log("reposition", phys, opp.Position, new() { ["to"] = s.Stance?.ToString() ?? "none", ["why"] = myBackUnsafe ? "edge behind me" : "edge behind them" });
        }
        return s.Stance is { } st && Horizontal(me, st.BottomCenter) > 0.3 ? st.BottomCenter : null;
    }

    /// <summary>The opponent's knockback must land on safe ground too; and never a hit that could be fatal (that ends the spar: true).
    /// Otherwise swings, or holds fire with the opponent near an edge.</summary>
    private bool SparSwingEnded(Fight f, SparState s, PhysicsState phys, Ctx ctx, Entity opp, bool theirBackUnsafe)
    {
        int weapon = _w.Inventory.HeldItem(_w.Player.HeldSlot).Id;
        double worst = s.Model.WorstHit(weapon, couldCrit: false, Armor(opp));
        if (s.Model.NextHitRisky(worst)) { EndFight(f, "next hit could be dangerous for the opponent", phys, ctx); return true; }
        int oppEdge = SparZone.EdgeAt(_w, opp.Position);
        if (oppEdge <= Options.SparNoSwingEdge)
            Log("hold_fire", phys, opp.Position, new() { ["why"] = $"opponent {oppEdge} from an edge" });
        else if (theirBackUnsafe)
            Log("hold_fire", phys, opp.Position, new() { ["why"] = "opponent's knockback would carry them near an edge" });
        else Swing(f, opp, phys, ctx);
        return false;
    }

    private string? SparStopCheck(Fight f, SparState s, PhysicsState phys, Entity? opp) =>
        SparHealthStop(opp) ?? s.Model.StopReason() ?? SparArenaStop(s, phys, opp) ?? SparIntrusionStop(f, s);

    /// <summary>My hp at the threshold, or the opponent's next (critical) hit could bring me to 1.</summary>
    private string? SparHealthStop(Entity? opp)
    {
        int hp = _w.Player.Health;
        if (hp <= Options.SparSelfStopHealth) return $"my hp {hp} at threshold";
        if (opp is null) return null;
        int incoming = (int)Math.Ceiling(Rules.DamageAfterArmor(Rules.CritDamage(Rules.AttackDamage(opp.HeldItem)),
            Enumerable.Range(5, 4).Select(i => _w.Inventory.GetPlayerSlot(i))) - 1e-9);
        return hp - incoming <= 1 ? $"my hp {hp}: their next hit could take {incoming}" : null;
    }

    /// <summary>Either of us too close to an edge, or out of the arena.</summary>
    private string? SparArenaStop(SparState s, PhysicsState phys, Entity? opp)
    {
        var me = phys.Position;
        int stopEdge = Options.MinEdgeDistance - 1;
        if (phys.OnGround && SparZone.EdgeAt(_w, me) <= stopEdge) return "I got too close to the edge";
        if (opp is not null && SparZone.EdgeAt(_w, opp.Position) <= stopEdge) return $"{s.Opponent} got too close to the edge";
        if (s.Zone.OutsideDistance(me) > Options.ArenaLeaveSlack) return "I left the arena";
        if (opp is not null && s.Zone.OutsideDistance(opp.Position) > Options.ArenaLeaveSlack) return $"{s.Opponent} left the arena";
        return null;
    }

    /// <summary>A mob near the arena, or the time limit.</summary>
    private string? SparIntrusionStop(Fight f, SparState s)
    {
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
