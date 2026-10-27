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
                var e = new CombatEngine(World, Options.Combat, _telemetry) { Username = Options.Username, Reserved = () => ReservedItems };
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
        if (step.AbortAction && CurrentAction is not null and not ("spar" or "attack"))
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
        else if (step.Block && !_combatBlocking && CombatRules.IsSword(HeldStack().Id))
        {
            Send(s, PlayerBlockPlacementPacket.UseItem(HeldStack()));
            _combatBlocking = true;
        }
        if (step.Chat is { } line) _ = SendChatAsync(line);
        if (step.Eat) _ = EatAfterFightAsync();
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
        int self = World.Player.EntityId;
        Send(s, new AnimationPacket { EntityId = self, Animation = 1 });
        Send(s, new UseEntityPacket { UserEntityId = self, TargetEntityId = targetId, LeftClick = true });
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
        var hot = WeaponChoice.Best(slots, ReservedItems, Options.Combat.NeverWield);
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
        _combatInstance is { Engaged: true } c && action is not ("spar" or "attack" or "eat") ? "busy: " + c.Status.Split('|')[0].Trim() : null;

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

            var pick = WeaponChoice.ForSpar(o.SparWeapon, World.Inventory.PlayerSlots(), ReservedItems, o.NeverWield);
            if (pick.InHotbar) SelectHotbar(pick.HotbarIndex);
            else if (await SelectItemAsync(pick.ItemId, null, c).ConfigureAwait(false) < 0) SelectHotbar(WeaponChoice.Hand(World.Inventory.PlayerSlots(), ReservedItems, o.NeverWield).HotbarIndex);

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

    /// <summary>Fights the nearest mob of a type (or entity id). Never players (only via <see cref="SparAsync"/>), never tamed wolves.</summary>
    public async Task<ActionResult> AttackAsync(string target, CancellationToken ct = default)
    {
        var engine = Combat;
        string t = target.Trim().ToLowerInvariant().Replace(' ', '_');
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
        return ActionResult.Success($"self-defense {(on ? "on" : "off")}");
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
