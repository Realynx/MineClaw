using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Survival reflexes wired into the client (no LLM in the loop): health alerts from 0x08 (packet thread), and on the
/// tick thread every <see cref="SurvivalOptions.CheckIntervalTicks"/>: out-of-food events, low-health retreat toward the
/// island interior, and auto-eating in safe windows (idle, or between blocks of a mining/follow loop; never mid-fight —
/// fights retreat first and eat after). Risky actions are gated at their start (<see cref="SurvivalGateAsync"/>).
/// </summary>
public sealed partial class MinecraftClient
{
    /// <summary>The survival reflex (thresholds in <see cref="ClientOptions.Survival"/>).</summary>
    public SurvivalReflex Survival { get; }

    /// <summary>Cheap memory lookup appended to the out-of-food event ("chest at 3,64,2 had bread"). Optional.</summary>
    public Func<string?>? FoodHint { get => Survival.FoodHint; set => Survival.FoodHint = value; }

    /// <summary>Food eaten automatically since the client was created.</summary>
    public int AutoMeals => Volatile.Read(ref _autoMeals);

    /// <summary>Risk label of the running action set by its gate ("bridge", "fight"...); cleared when it ends.</summary>
    private volatile string? _actionRisk;
    private int _autoEating, _autoMeals;
    private long _eatRetryTick;
    private BlockPos? _healRetreat;
    private long _healRetreatUntil;
    private BlockPos? _lastSafeSpot;

    private void ResetSurvival()
    {
        Survival.Reset();
        _healRetreat = null;
        _lastSafeSpot = null;
        _eatRetryTick = 0;
    }

    private bool HasUsableFood(int food)
    {
        var slots = World.Inventory.PlayerSlots();
        var reserved = EatReserved(World.Player.Health);
        return SurvivalReflex.HasSafeFood(slots, reserved)
               || (food <= Options.Survival.StarvingFood && SurvivalReflex.HasAnyFood(slots, reserved));
    }

    /// <summary>What a meal must not use right now (<see cref="EatReserved"/> at the current health): the food resolver's view.</summary>
    public IReadOnlySet<int> EatReservedItems => EatReserved(World.Player.Health);

    /// <summary>
    /// Items the survival reflex must not eat: the configured <see cref="ReservedItems"/> plus food held for a hand-off
    /// (<see cref="Handoffs"/>), the latter only while health is above <see cref="HandoffReservations.CriticalHp"/>.
    /// </summary>
    private IReadOnlySet<int> EatReserved(int health)
    {
        var held = Handoffs.Blocked(health, id => World.Inventory.CountItem(id));
        if (held.Count == 0) return ReservedItems;
        var set = new HashSet<int>(ReservedItems);
        set.UnionWith(held);
        return set;
    }

    // ------------------------------------------------------------------ packet thread

    /// <summary>Low / critical health: one danger per episode; cancel risky actions (low) or any action (critical).</summary>
    private void SurvivalOnHealth(int health, int food)
    {
        if (Survival.OnHealth(health, food, HasUsableFood(food)) is not { } alert) return;
        var fx = SurvivalReflex.EffectFacts(World.Player.Effects);
        if (fx.Length > 0) alert = alert with { Message = alert.Message + "; " + fx };
        Log("survival: " + alert.Message);
        RaiseDanger(DangerKind.LowHealth, alert.Message, abort: false, critical: alert.Critical);
        if (!Options.Survival.Enabled || !Options.AbortActionsOnDanger) return;
        if (alert.Critical)
        {
            if (CurrentAction is { } a && a is not ("eat" or "shelter")) AbortCurrentAction($"critical hp {health}/20: stopped {a}");
        }
        else if (RiskyNow() is { } what) AbortCurrentAction($"low health {health}/20: stopped {what}");
    }

    /// <summary>What makes the running action risky at low health, or null (crafting, chests... carry on).</summary>
    private string? RiskyNow()
    {
        if (_actionRisk is { } r) return r;
        switch (CurrentAction)
        {
            case null: return null;
            case "attack": return "fighting";
            case "spar": return "sparring";
            // Mining/digging/placing/collecting go on at low hp (the user's rule); the edge guard and the lava rule still apply,
            // and critical hp stops everything but eating.
            default: return null;
        }
    }

    // ------------------------------------------------------------------ tick thread

    private void SurvivalTick(PhysicsState phys)
    {
        var o = Options.Survival;
        if (!o.Enabled || TickCount % Math.Max(1, o.CheckIntervalTicks) != 0) return;
        var p = World.Player;
        int hp = p.Health, food = p.Food;
        if (hp <= 0) return;
        var slots = World.Inventory.PlayerSlots();
        Handoffs.Reconcile(id => World.Inventory.CountItem(id)); // delivered / timed-out holds end
        var reserved = EatReserved(hp);
        var me = phys.Position;
        var cell = new BlockPos((int)Math.Floor(me.X), (int)Math.Floor(me.Y + 1e-6), (int)Math.Floor(me.Z));
        int edge = Safety.EdgeDistance(World, cell.X, cell.Y, cell.Z, 4);
        if (phys.OnGround && edge >= o.RetreatEdgeDistance) _lastSafeSpot = cell;

        if (Survival.CheckFood(food, slots, reserved, Difficulty) is { } hungry)
        {
            Log("survival: " + hungry.Message);
            Raise(() => Danger?.Invoke(hungry));
        }

        bool engaged = _combatInstance?.Engaged ?? false;
        bool idle = !engaged && CurrentAction is null && _driver is null && phys.OnGround && !phys.Frozen;

        // Low health near the edge: step inward first (eating comes after, from a safe spot).
        if (Survival.IsLow(hp) && idle && _healRetreat is null && edge < o.RetreatEdgeDistance)
        {
            var threats = World.Entities.Nearby(me, 12, CombatRules.IsThreat).Select(e => e.Position).ToList();
            var to = CombatGeometry.RetreatCell(World, me, threats, 7) ?? Guard.FindInterior(World, phys, 6);
            if (to is null && _lastSafeSpot is { } last && last.Y == cell.Y
                && Math.Max(Math.Abs(last.X - cell.X), Math.Abs(last.Z - cell.Z)) <= 8 && CombatGeometry.WalkSafe(World, me, last))
                to = last;
            if (to is { } t)
            {
                _healRetreat = t;
                _healRetreatUntil = TickCount + 100;
                Log($"survival: hp {hp}/20 at the edge; stepping inward to {Fmt(t)}");
            }
        }

        // No way home and low hp or outnumbered (not in a fight: the combat engine asks itself): box in.
        if (o.ShelterEnabled && !engaged && CurrentAction is null && phys.OnGround && Volatile.Read(ref _sheltering) == 0 && TickCount % 20 == 0)
        {
            var near = World.Entities.Nearby(me, 8, CombatRules.IsThreat);
            if (near.Count > 0 && (Survival.IsLow(hp) || near.Count >= o.ShelterThreats)
                && ShouldShelter(near.Count, CombatGeometry.RetreatCell(World, me, near.Select(e => e.Position).ToList(), 7) != null))
            {
                Log($"survival: hp {hp}/20, {near.Count} hostile(s) near and no way home: boxing in");
                StartShelter($"hp {hp}, {near.Count} near, no way home");
            }
        }

        if (o.AutoEat && idle && _healRetreat is null && TickCount >= _eatRetryTick
            && World.Entities.NearestHostile(me, 4) is null
            && SurvivalReflex.EatNow(slots, reserved, hp, food, o, Options.Profile.Hunger, p.Saturation) is { } pick
            && Interlocked.CompareExchange(ref _autoEating, 1, 0) == 0)
            _ = Task.Run(() => AutoEatAsync(pick));
    }

    /// <summary>Walks to the low-health retreat cell (only while no action drives the body).</summary>
    private MoveInput HealRetreatOverride(PhysicsState phys, MoveInput input, bool idle)
    {
        if (_healRetreat is not { } t) return input;
        var c = new Vec3d(t.X + 0.5, phys.Position.Y, t.Z + 0.5);
        double dx = c.X - phys.Position.X, dz = c.Z - phys.Position.Z;
        if (!idle || TickCount > _healRetreatUntil || dx * dx + dz * dz < 0.3 * 0.3 || !phys.OnGround && phys.Velocity.Y < -0.5)
        {
            _healRetreat = null;
            return input;
        }
        var yaw = PlayerPhysics.YawPitchTo(phys.Position, c).Yaw;
        return new MoveInput(Forward: 1, Yaw: yaw, Pitch: input.Pitch, Sneak: Guard.IsNearEdge(World, phys));
    }

    private async Task AutoEatAsync(FoodPick pick)
    {
        try
        {
            if (IsBusy) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(Options.TickInterval.TotalMilliseconds * HungerRules.EatTicks * 3 + 3000));
            var r = await RunActionAsync("eat", cts.Token, c => UseCoreAsync(pick.ItemId, c)).ConfigureAwait(false);
            Log($"survival: auto-eat {r}");
            if (r.Ok) Interlocked.Increment(ref _autoMeals);
            else _eatRetryTick = TickCount + 100;
        }
        catch (Exception ex)
        {
            Log("survival: auto-eat failed: " + ex.Message);
            _eatRetryTick = TickCount + 100;
        }
        finally
        {
            Volatile.Write(ref _autoEating, 0);
        }
    }

    // ------------------------------------------------------------------ action layer

    /// <summary>A safe window inside a long action (between blocks of mine/generator, between follow re-plans): eat
    /// now if the reflex wants to. Runs under the caller's action lock.</summary>
    private async Task SurvivalWindowAsync(CancellationToken ct)
    {
        var o = Options.Survival;
        if (!o.Enabled || !o.AutoEat || TickCount < _eatRetryTick || (_combatInstance?.Engaged ?? false)) return;
        var p = World.Player;
        if (!p.OnGround || World.Entities.NearestHostile(p.Position, 4) is not null) return;
        if (SurvivalReflex.EatNow(World.Inventory.PlayerSlots(), EatReserved(p.Health), p.Health, p.Food, o, Options.Profile.Hunger, p.Saturation) is not { } pick) return;
        var r = await UseCoreAsync(pick.ItemId, ct).ConfigureAwait(false);
        Log($"survival: ate during {CurrentAction}: {r}");
        if (r.Ok) Interlocked.Increment(ref _autoMeals);
        else _eatRetryTick = TickCount + 100;
    }

    /// <summary>
    /// Gate at the start of a risky action (<paramref name="what"/>: "bridge", "fight", "spar", "mine 32 blocks"):
    /// refuses with a clear reason when health or food is below the thresholds, eating first when that fixes it.
    /// Null = go ahead (the action is then marked risky for the low-health reflex).
    /// </summary>
    private async Task<ActionResult?> SurvivalGateAsync(string what, CancellationToken ct)
    {
        var o = Options.Survival;
        if (!o.Enabled) return null;
        var p = World.Player;
        var reserved = EatReserved(p.Health);
        var pick = SurvivalReflex.PickFood(World.Inventory.PlayerSlots(), reserved, p.Health, p.Food, o);
        if (SurvivalReflex.GateReason(what, p.Health, p.Food, pick is not null || SurvivalReflex.HasSafeFood(World.Inventory.PlayerSlots(), reserved), o,
                effects: p.Effects) is { } why)
        {
            if (pick is { } eat && p.Food < HungerRules.RegenFood)
                Log($"survival: gate ate before refusing: {await UseCoreAsync(eat.ItemId, ct).ConfigureAwait(false)}");
            Log($"survival: refused {what}: {why}");
            return ActionResult.Fail(why);
        }
        if (p.Food < o.GateMinFood && pick is { } f)
        {
            var r = await UseCoreAsync(f.ItemId, ct).ConfigureAwait(false);
            if (SurvivalReflex.NeedsFood(what) && (!r.Ok || World.Player.Food < o.GateMinFood))
                return ActionResult.Fail($"too hungry to {what} (food {World.Player.Food}; eating failed: {r.Message})");
        }
        // Only fights/spars are cancelled by the low-health reflex; mining, chopping, bridging carry on (critical hp stops all).
        if (SurvivalReflex.CancelAtLowHealth(what)) _actionRisk = what;
        return null;
    }
}
