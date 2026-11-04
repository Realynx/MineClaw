using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Combat wiring: the <see cref="CombatEngine"/> runs on the tick thread between the driver and the always-on
/// EdgeGuard (which still filters every combat input), and its swings/blocks go out after the movement packet like
/// vanilla (0x12 swing, then 0x07 left-click; sword block = 0x0F use item, release = 0x0E status 5).
/// </summary>
public sealed partial class MinecraftClient
{
    private CombatEngine? _combatInstance;
    private CombatTelemetry? _telemetry;
    private bool _combatBlocking;
    private readonly object _combatInit = new();

    /// <summary>The combat engine (reflexes, guard, spar). Created on first use.</summary>
    public CombatEngine Combat
    {
        get
        {
            if (_combatInstance is { } c) return c;
            lock (_combatInit)
            {
                if (_combatInstance is not null) return _combatInstance;
                _telemetry = new CombatTelemetry(Options.Combat.TelemetryDirectory);
                var e = new CombatEngine(World, Options.Combat, _telemetry)
                {
                    Username = Options.Username, Reserved = () => ReservedItems, Rules = Options.Profile.Combat, TickInterval = Options.TickInterval,
                    ShieldDisabled = () => ItemCooldownTicks(MineClaw.Client.Combat.Release1122CombatRules.ShieldId) > 0,
                };
                e.ShouldShelter = ShouldShelter;
                e.CanBoxIn = CanBoxIn;
                e.Difficulty = () => Difficulty;
                e.EventRaised += ev =>
                {
                    Log("combat: " + ev.Message);
                    Raise(() => CombatEventRaised?.Invoke(ev));
                };
                _combatInstance = e;
                return e;
            }
        }
    }

    /// <summary>Fight started / ended (with summary) / critical moment. Nothing else from combat reaches the Agent.</summary>
    public event Action<CombatEvent>? CombatEventRaised;

    /// <summary>Attacks refused by the hard gate (a player without spar consent, a tamed wolf...).</summary>
    public long RefusedAttacks => Interlocked.Read(ref _refusedAttacks);
    private long _refusedAttacks;

    // ------------------------------------------------------------------ tick integration

    private CombatStep CombatBeforePhysics(PhysicsState phys, MoveInput input)
    {
        var step = Combat.Tick(phys, TickCount, input);
        if (step.AbortAction && CurrentAction is not null and not ("spar" or "attack" or "shelter"))
            AbortCurrentAction($"interrupted: {Combat.Status.Split('|')[0].Trim()}");
        return step;
    }

    /// <summary>After the movement packet: weapon, block release, swing, block start, chat, eating.</summary>
    private void CombatAfterMovement(Session s, CombatStep step)
    {
        if (step.Equip is { } pick) EquipNow(s, pick);
        if (_combatBlocking && (!step.Block || step.AttackId >= 0))
        {
            Send(s, new PlayerDiggingPacket { Status = PlayerDiggingPacket.StatusReleaseUseItem, X = 0, Y = 0, Z = 0, Face = 255 });
            _combatBlocking = false;
        }
        if (step.AttackId >= 0) SendAttack(s, step.AttackId);
        else if (step.Block && !_combatBlocking && Options.Profile.Combat.CanBlock(HeldStack(), World.Inventory.Offhand))
        {
            // b1.8.1: right-click with the sword; 1.12.2: Use Item with the shield's hand (processRightClick: the sword in the main
            // hand has no use, so the off hand's shield goes up).
            int hand = Options.Profile.Combat.BlockHand(HeldStack(), World.Inventory.Offhand);
            Send(s, PlayerBlockPlacementPacket.UseItem(hand == 0 ? HeldStack() : World.Inventory.Offhand, (byte)hand));
            _combatBlocking = true;
        }
        if (step.Chat is { } line) _ = SendChatAsync(line);
        if (step.Eat) _ = EatAfterFightAsync();
        if (step.Shelter) StartShelter("a fight with no way out");
    }

    /// <summary>Hard gate + vanilla order: swing animation, then 0x07 UseEntity (left click).</summary>
    private void SendAttack(Session s, int targetId)
    {
        var target = World.Entities.Get(targetId);
        if (target is null) return;
        if (!Combat.MayAttack(target))
        {
            Interlocked.Increment(ref _refusedAttacks);
            Log($"combat: refused to attack {target.DisplayName}");
            return;
        }
        SendMeleeHit(s, targetId);
    }

    /// <summary>One melee hit in the version's vanilla order: b1.8.1 swing then attack; 1.12.2 attack then swing
    /// (<c>Minecraft.clickMouse</c>: <c>attackEntity</c> first, <c>swingArm</c> after; the server's <c>swingArm</c> resets the charge).</summary>
    private void SendMeleeHit(Session s, int targetId)
    {
        int self = World.Player.EntityId;
        var swing = new AnimationPacket { EntityId = self, Animation = 1 };
        var attack = new UseEntityPacket { UserEntityId = self, TargetEntityId = targetId, LeftClick = true };
        if (Options.Profile.Combat.SwingBeforeAttack) { Send(s, swing); Send(s, attack); }
        else { Send(s, attack); Send(s, swing); }
    }

    private void EquipNow(Session s, WeaponPick pick)
    {
        var inv = World.Inventory;
        if (pick.InHotbar) { SelectHotbarFromTick(s, pick.HotbarIndex); return; }
        // Main inventory: only swap with clicks when nothing else is using the window (no action running).
        if (CurrentAction is null && inv.OpenWindow is null && inv.Cursor.IsEmpty)
        {
            int hb = ChooseHotbarTarget();
            ClickNoWait(s, pick.Slot);
            ClickNoWait(s, 36 + hb);
            if (!inv.Cursor.IsEmpty) ClickNoWait(s, pick.Slot);
            if (inv.GetPlayerSlot(36 + hb).Id == pick.ItemId) { SelectHotbarFromTick(s, hb); return; }
        }
        var slots = inv.PlayerSlots().ToArray();
        for (int i = 9; i < 36; i++) slots[i] = ItemStack.Empty;
        var hot = WeaponChoice.Best(slots, ReservedItems, Options.Combat.NeverWield, Options.Profile.Combat);
        if (hot.InHotbar) SelectHotbarFromTick(s, hot.HotbarIndex);
    }

    private void SelectHotbarFromTick(Session s, int index)
    {
        if (World.Player.HeldSlot == index && _serverHeldSlot == index) return;
        World.Player.HeldSlot = index;
        _serverHeldSlot = index;
        Send(s, new HoldingChangePacket { Slot = (short)index });
    }

    private async Task EatAfterFightAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(Options.TickInterval.TotalMilliseconds * 10)).ConfigureAwait(false);
            if (IsBusy || Combat.Engaged || World.Player.Food >= 20 || PickFood() is null) return;
            var r = await EatAsync().ConfigureAwait(false);
            Log($"combat: post-fight {r}");
        }
        catch (Exception ex) { Log("combat: eating after the fight failed: " + ex.Message); }
    }

    /// <summary>Actions other than the combat ones are refused while a fight owns the body.</summary>
    private string? CombatBlocks(string action) =>
        _combatInstance is { Engaged: true } c && action is not ("spar" or "attack" or "eat" or "shelter") ? "busy: " + c.Status.Split('|')[0].Trim() : null;

    /// <summary>Waits (bounded) for a reflex fight to end so a job can run after it.</summary>
    private async Task WaitForReflexFightAsync(CombatEngine engine, CancellationToken ct)
    {
        var limit = Options.Combat.ReflexWaitForJobs;
        if (limit <= TimeSpan.Zero) return;
        var until = DateTime.UtcNow + limit;
        try
        {
            while (engine.Engaged && engine.ReflexEngaged && DateTime.UtcNow < until)
                await Task.Delay(50, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* the caller's token decides */ }
    }

    /// <summary>
    /// Nearest hostile within <paramref name="radius"/> that can get at the body: in sight, touching or on its level
    /// (<see cref="CombatGeometry.CanGetAt"/>). A cave mob under the floor doesn't count.
    /// </summary>
    public Entity? NearestThreat(double radius = 16) => NearestThreat(World.Player.Position, radius);

    public Entity? NearestThreat(Vec3d me, double radius) =>
        World.Entities.Nearby(me, radius, e => e.IsHostileMob && CombatGeometry.CanGetAt(World, e, me), 1).FirstOrDefault();

    /// <summary>
    /// Foot cells walks keep out of: within <see cref="CombatOptions.CreeperAvoidRadius"/> of a creeper (its ignite range),
    /// except around a creeper the goal or the bot itself is already next to (the combat reflex deals with that one).
    /// </summary>
    public IReadOnlySet<BlockPos>? CreeperAvoidCells(Goal goal)
    {
        double r = Options.Combat.CreeperAvoidRadius;
        if (r <= 0) return null;
        var me = World.Player.Position;
        HashSet<BlockPos>? set = null;
        foreach (var c in World.Entities.Nearby(me, 32, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Creeper))
        {
            double dme = Math.Sqrt((c.Position.X - me.X) * (c.Position.X - me.X) + (c.Position.Z - me.Z) * (c.Position.Z - me.Z));
            if (dme <= r + 0.5 || !CombatGeometry.CanGetAt(World, c, me) && Math.Abs(c.Position.Y - me.Y) >= 2) continue;
            int cx = (int)Math.Floor(c.Position.X), cy = (int)Math.Floor(c.Position.Y + 1e-6), cz = (int)Math.Floor(c.Position.Z);
            int ri = (int)Math.Ceiling(r);
            var cells = new List<BlockPos>();
            bool goalInside = false;
            for (int x = cx - ri; x <= cx + ri && !goalInside; x++)
            for (int z = cz - ri; z <= cz + ri && !goalInside; z++)
            {
                double dx = x + 0.5 - c.Position.X, dz = z + 0.5 - c.Position.Z;
                if (dx * dx + dz * dz > r * r) continue;
                for (int y = cy - 2; y <= cy + 2; y++)
                {
                    var p = new BlockPos(x, y, z);
                    if (goal.IsReached(p)) { goalInside = true; break; }
                    cells.Add(p);
                }
            }
            if (goalInside) continue;
            (set ??= new HashSet<BlockPos>()).UnionWith(cells);
        }
        return set;
    }

    // ------------------------------------------------------------------ public API (tools)

    /// <summary>
    /// Consent-based spar: the opponent must be on the spar list and have asked in chat; health/food must pass the
    /// survival gate; the arena is the best adaptive <see cref="SparZone"/> of the island near both players (walked to
    /// if needed); the opponent must step into it; then a chat countdown. Completes with the one-line summary when any
    /// stop condition fires. No safe zone = an honest refusal.
    /// </summary>
    public async Task<ActionResult> SparAsync(string player, int? rounds = null, CancellationToken ct = default)
    {
        var o = Options.Combat;
        var engine = Combat;
        if (!o.SparPlayers.Any(p => string.Equals(p, player, StringComparison.OrdinalIgnoreCase)))
            return ActionResult.Fail($"{player} is not on my spar list");
        if (!engine.HasSparRequest(player))
            return ActionResult.Fail($"{player} hasn't asked to spar (they must say 'spar' in chat first)");
        if (engine.Engaged) return ActionResult.Fail("busy: " + engine.Status);
        if (rounds is { } rr && (rr < 1 || rr > 9)) rounds = Math.Clamp(rr, 1, 9);

        return await RunActionAsync("spar", ct, async c =>
        {
            var opp = World.Entities.FindPlayer(player);
            if (opp is null) return ActionResult.Fail($"can't see {player}");
            var me = World.Player.Position;
            if (me.DistanceTo(opp.Position) > 24) return ActionResult.Fail($"{player} is too far ({me.DistanceTo(opp.Position):0}m)");
            if (World.Entities.Nearest(me, 16, CombatRules.IsThreat) is { } mob) return ActionResult.Fail($"not with a {mob.DisplayName} around");
            if (await SurvivalGateAsync("spar", c).ConfigureAwait(false) is { } refused) return refused;

            var mid = new Vec3d((me.X + opp.Position.X) / 2, me.Y, (me.Z + opp.Position.Z) / 2);
            var found = SparZone.Find(World, mid, o, [me, opp.Position]);
            if (found.Zone is null && SparZone.Find(World, me, o, [me, opp.Position]) is { Zone: not null } nearMe) found = nearMe;
            if (found.Zone is not { } zone) return ActionResult.Fail($"no safe arena: {found.Reason}");
            var center = zone.Center;
            if (!zone.Contains(World.Player.Position))
            {
                Log($"combat: walking into the spar zone at {Fmt(center)} ({zone.Count} cells, {zone.CenterEdge} from the edge)");
                var g = await GotoCoreAsync(new GoalBlock(center.X, center.Y, center.Z), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(30) }, c).ConfigureAwait(false);
                if (!g.Ok) return ActionResult.Fail($"spar zone {Fmt(center)} unreachable: {g.Message}");
                if (!zone.Contains(World.Player.Position)) return ActionResult.Fail($"couldn't get into the spar zone at {Fmt(center)}");
            }

            var pick = WeaponChoice.ForSpar(o.SparWeapon, World.Inventory.PlayerSlots(), ReservedItems, o.NeverWield, Options.Profile.Combat);
            if (pick.InHotbar) SelectHotbar(pick.HotbarIndex);
            else if (await SelectItemAsync(pick.ItemId, null, c).ConfigureAwait(false) < 0) SelectHotbar(WeaponChoice.Hand(World.Inventory.PlayerSlots(), ReservedItems, o.NeverWield, Options.Profile.Combat).HotbarIndex);

            long stops = engine.StopWordCount;
            string? Abort()
            {
                if (engine.StopWordCount != stops) return "stop word before the start";
                if (engine.Engaged) return "a fight started (mob?)";
                if (World.Entities.Nearest(World.Player.Position, 16, CombatRules.IsThreat) is { } m) return $"mob appeared: {m.DisplayName}";
                var p = World.Entities.FindPlayer(player);
                if (p is null || zone.OutsideDistance(p.Position) > o.ArenaLeaveSlack) return $"{player} isn't in the arena";
                return null;
            }

            await SendChatAsync($"{player}: spar around {Fmt(center)}, keep {o.MinEdgeDistance}+ blocks from the edge. say stop, yield or gg to end", c).ConfigureAwait(false);
            bool came = await WaitUntilAsync(() => engine.StopWordCount != stops
                    || (World.Entities.FindPlayer(player) is { } p && zone.Contains(p.Position)),
                TimeSpan.FromSeconds(30), c).ConfigureAwait(false);
            if (engine.StopWordCount != stops) return ActionResult.Fail("cancelled: stop word before the start");
            if (!came) return ActionResult.Fail($"{player} didn't come into the arena at {Fmt(center)}");
            foreach (var n in new[] { "3", "2", "1" })
            {
                if (Abort() is { } x) { _ = SendChatAsync("spar cancelled: " + x); return ActionResult.Fail("cancelled: " + x); }
                await SendChatAsync(n, c).ConfigureAwait(false);
            }
            if (Abort() is { } y) { _ = SendChatAsync("spar cancelled: " + y); return ActionResult.Fail("cancelled: " + y); }
            await SendChatAsync("go!", c).ConfigureAwait(false);

            var oppNow = World.Entities.FindPlayer(player);
            if (oppNow is null) return ActionResult.Fail($"lost sight of {player}");
            return await AwaitFightAsync(engine.StartSpar(player, oppNow.Id, zone, rounds), c).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Fights the nearest mob of a type (or entity id). Never players (only via <see cref="SparAsync"/>, or an entity id the
    /// owner explicitly permitted: <see cref="CombatEngine.Permit"/>), never tamed wolves.</summary>
    public async Task<ActionResult> AttackAsync(string target, CancellationToken ct = default)
    {
        var engine = Combat;
        string t = target.Trim().ToLowerInvariant().Replace(' ', '_').TrimStart('#');
        // An entity id the owner ordered (Story Mode's confirmed attack on a player or another bot): exactly that entity, nothing else.
        if (int.TryParse(t, out int pid) && World.Entities.Get(pid) is { Kind: EntityKind.Player } pe)
        {
            if (!engine.Permitted(pid)) return ActionResult.Fail("I only fight players in a spar they asked for");
            if (engine.Engaged) return ActionResult.Fail("busy: " + engine.Status);
            return await RunActionAsync("attack", ct, async c =>
                await AwaitFightAsync(engine.StartAttack(pe.Id), c).ConfigureAwait(false)).ConfigureAwait(false);
        }
        if (World.Entities.FindPlayer(t) is not null || World.Entities.OnlinePlayers.Any(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase))
            || t is "player" or "players")
            return ActionResult.Fail("I only fight players in a spar they asked for");
        Entity? e;
        if (int.TryParse(t, out int id)) e = World.Entities.Get(id);
        else
        {
            int? type = MobTypes.ByName(t) ?? (t.EndsWith('s') ? MobTypes.ByName(t[..^1]) : null);
            if (type is null) return ActionResult.Fail($"unknown mob '{target}'");
            e = World.Entities.Nearest(World.Player.Position, Options.Combat.MaxEngageDistance,
                x => x.Kind == EntityKind.Mob && x.TypeId == type && !CombatRules.IsTamedWolf(x));
        }
        if (e is null) return ActionResult.Fail($"no {target} within {Options.Combat.MaxEngageDistance:0}");
        if (e.Kind != EntityKind.Mob) return ActionResult.Fail("mobs only");
        if (CombatRules.IsTamedWolf(e)) return ActionResult.Fail("that wolf is tamed");
        if (engine.Engaged) return ActionResult.Fail("busy: " + engine.Status);
        return await RunActionAsync("attack", ct, async c =>
        {
            if (await SurvivalGateAsync("fight", c).ConfigureAwait(false) is { } refused) return refused;
            return await AwaitFightAsync(engine.StartAttack(e.Id), c).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task<ActionResult> AwaitFightAsync(Task<string> fight, CancellationToken ct)
    {
        try
        {
            var summary = await fight.WaitAsync(ct).ConfigureAwait(false);
            return summary.StartsWith("refused", StringComparison.Ordinal) ? ActionResult.Fail(summary) : ActionResult.Success(summary);
        }
        catch (OperationCanceledException)
        {
            Combat.RequestStop(_abortReason ?? "cancelled");
            try { return ActionResult.Fail(await fight.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false)); }
            catch (TimeoutException) { throw new ActionAbortedException(_abortReason ?? "cancelled"); }
        }
    }

    /// <summary>Ends the current spar (or fight) immediately.</summary>
    public ActionResult StopSpar()
    {
        var e = Combat;
        if (!e.Engaged) return ActionResult.Success("not fighting");
        e.RequestStop("stop_spar");
        return ActionResult.Success(e.Sparring ? "stopping the spar" : "stopping the fight");
    }

    public ActionResult SetDefend(bool on)
    {
        Combat.DefendSelf = on;
        SetBrave(false);
        return ActionResult.Success($"self-defense {(on ? "on" : "off")}");
    }

    private long _braveUntil = -1;
    private int _braveHp;

    /// <summary>The brain's override is on: no box-ins, no pre-emptive fights (<see cref="SetBrave"/>).</summary>
    public bool Brave => Volatile.Read(ref _braveUntil) > TickCount;

    /// <summary>
    /// "brave": the brain overrides its fear reflexes while nothing is hurting it. No box-ins, no fights it didn't get hit into
    /// (one that hits it, one in reach and a fusing creeper are still fought). Damage ends it at once; so do <paramref name="minutes"/>
    /// (10) and defend on/off. Live 10-30: main boxed itself in again and again mid-dig against zombies behind the shaft walls;
    /// Fox: "stop filling it in, you're not being attacked".
    /// </summary>
    public ActionResult SetBrave(bool on, double minutes = 10)
    {
        _braveHp = World.Player.Health;
        Volatile.Write(ref _braveUntil, on ? TickCount + (long)(Math.Clamp(minutes, 0.5, 30) * 60 * 20) : -1);
        Combat.Brave = on;
        if (on) Combat.DefendSelf = true;
        return ActionResult.Success(on ? $"brave for {Math.Clamp(minutes, 0.5, 30):0.#} min: no boxing in or picking fights; any damage turns the reflexes back on" : "reflexes normal");
    }

    /// <summary>From the survival tick: damage or the time ends the override.</summary>
    private void BraveTick(int hp)
    {
        if (Volatile.Read(ref _braveUntil) < 0) return;
        if (!Brave) { SetBrave(false); Log("survival: brave ran out: reflexes normal"); return; }
        if (hp > _braveHp) _braveHp = hp;
        if (hp >= _braveHp) return;
        SetBrave(false);
        var line = $"took damage (hp {hp}): brave off, reflexes back on";
        Log("survival: " + line);
        Raise(() => CombatEventRaised?.Invoke(new CombatEvent(CombatEventKind.Critical, line)));
    }

    /// <summary>Adds a player to the guard list, or clears it with "off".</summary>
    public ActionResult SetGuard(string playerOrOff)
    {
        var e = Combat;
        if (string.Equals(playerOrOff.Trim(), "off", StringComparison.OrdinalIgnoreCase))
        {
            e.Guarded = [];
            return ActionResult.Success("guarding nobody");
        }
        var list = e.Guarded.Where(n => !string.Equals(n, playerOrOff, StringComparison.OrdinalIgnoreCase)).Append(playerOrOff.Trim()).ToList();
        e.Guarded = list;
        return ActionResult.Success("guarding " + string.Join(", ", list));
    }

    public string CombatStatus() => Combat.Status;
}
