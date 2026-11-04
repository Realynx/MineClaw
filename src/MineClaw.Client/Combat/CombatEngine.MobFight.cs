using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>One mob fight's tick (<see cref="MobTick"/>), phase by phase: the fight ends or gives up, the target is re-checked,
/// the outmatched and low-health exits, creepers, positioning against the void, the swing and the move.</summary>
public sealed partial class CombatEngine
{
    /// <summary>One mob-fight tick's working set, filled phase by phase (a struct: nothing is allocated per tick).</summary>
    private struct MobTurn
    {
        public Fight F;
        public PhysicsState Phys;
        public Ctx Ctx;
        public Entity Target;
        public MobProfile Prof;
        public Vec3d Me;
        /// <summary>Holding still, looking where the body looks: the input of every tick that ends the fight.</summary>
        public MoveInput Face;
        public MoveInput Look;
        public double Dist, Reach;
        public int Cooldown;
        public bool Approaching, ClimbStep, EdgeBehind, Jump;
        public Vec3d? MoveTo;
    }

    private MoveInput MobTick(Fight f, PhysicsState phys, Ctx ctx)
    {
        if (_pendingEquip is { } eq) { ctx.Equip = eq; _pendingEquip = null; }
        var face = Hold(phys);
        if (f.StopRequest is { } stop) { EndFight(f, "stopped: " + stop, phys, ctx); return face; }

        var target = _w.Entities.Get(f.TargetId);
        if (target is null || _dead.ContainsKey(f.TargetId))
        {
            MobTargetGone(f, phys, ctx);
            return face;
        }

        var t = new MobTurn { F = f, Phys = phys, Ctx = ctx, Target = target, Prof = CombatRules.ProfileOf(target), Me = phys.Position, Face = face };
        t.Dist = Horizontal(t.Me, target.Position);
        if (t.Dist > Options.MaxEngageDistance + 8) { EndFight(f, $"{f.TargetName} out of range", phys, ctx); return face; }
        if (t.Dist < f.BestDist - 0.5) { f.BestDist = t.Dist; f.LastProgressTick = _tick; }
        if (f.Cover) return CoverTick(f, phys, target, ctx);
        if (MobGaveUp(in t)) return face;
        t.Approaching = MobApproaching(f, t.Me, target, t.Dist);
        Sample(f, phys, target);
        return MobEngage(ref t);
    }

    /// <summary>The target died or left: on to the next one a reflex would pick, else the fight ends.</summary>
    private void MobTargetGone(Fight f, PhysicsState phys, Ctx ctx)
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
            f.PrevTargetPos = null;
            f.LastProgressTick = f.TargetSince = _tick;
            f.TargetHits = 0;
            f.Stance = null;
            return;
        }
        EndFight(f, what, phys, ctx);
    }

    /// <summary>The fight gives up (and ends): no progress for too long, a reflex's target never hit, or one in deep water.</summary>
    private bool MobGaveUp(in MobTurn t)
    {
        var f = t.F;
        string? why = null;
        if (_tick - f.LastProgressTick > Options.StaleFightTicks) why = $"couldn't reach {f.TargetName} safely";
        // A reflex never holds the body for a mob it can't hit (live 10-28: "busy: fighting skeleton 11.4m: 0 hits").
        else if (ZeroHitStale(in t)) why = $"couldn't reach {f.TargetName} (0 hits in {Options.ZeroHitGiveUpTicks / 20}s), left it alone";
        else if (WetChaseExpired(in t)) why = $"won't chase {f.TargetName} into the water, left it alone";
        if (why is null) return false;
        f.GaveUp = true;
        EndFight(f, why, t.Phys, t.Ctx);
        return true;
    }

    private bool ZeroHitStale(in MobTurn t)
    {
        var f = t.F;
        return !f.Explicit && f.Done is null && f.TargetHits == 0 && f.FleeStart < 0 && _tick - f.TargetSince > Options.ZeroHitGiveUpTicks
               && !InMeleeReach(t.Phys, t.Target) && !AnyFusingCreeper(t.Me, out _);
    }

    /// <summary>A reflex never follows a mob into water over its head: no footing, no crits, the air runs out, and the breath reflex's
    /// swim to shore got cut by the next re-engage (live 10-29 23:35: main chased a zombie under water, down to 7 s of air and
    /// 15 hp with bare hands). One that comes back into reach (or hits us) is fought again. True once it has been wet 30 ticks.</summary>
    private bool WetChaseExpired(in MobTurn t)
    {
        var f = t.F;
        if (f.Explicit || f.Done is not null || f.FleeStart >= 0 || InMeleeReach(t.Phys, t.Target)
            || !(CombatGeometry.DeepWater(_w, t.Target.Position) || _w.Player.HeadUnderWater))
        {
            f.WetSince = -1;
            return false;
        }
        if (f.WetSince < 0)
        {
            f.WetSince = _tick;
            return false;
        }
        return _tick - f.WetSince > 30;
    }

    /// <summary>"Approaching" is the MOB closing in (its own move toward where I stand now), not the gap shrinking: while the bot walks
    /// up to a mob busy with someone else the gap shrinks too, and "let it come to us" then held the bot 5 m short until the 8 s
    /// give-up (live 10-29: Fox and Robin killed by a zombie at a door; this guard fight never reached it). Sampled every 10 ticks.</summary>
    private bool MobApproaching(Fight f, Vec3d me, Entity target, double dist)
    {
        if ((_tick - f.StartTick) % 10 != 0) return f.MobApproaching;
        bool approaching = f.PrevTargetPos is { } pt && Horizontal(me, pt) - dist > 0.3;
        f.PrevTargetPos = target.Position;
        f.MobApproaching = approaching;
        f.PrevDist = dist;
        return approaching;
    }

    /// <summary>The fight proper, once the target is there and worth it.</summary>
    private MoveInput MobEngage(ref MobTurn t)
    {
        if (MobOutmatchedTick(ref t) is { } outmatched) return outmatched;
        if (MobFleeTick(ref t) is { } flee) return flee;
        t.Look = FaceAt(t.Phys, t.Target);
        t.Reach = CombatRules.ReachTo(t.Phys.EyePosition, CombatRules.Hitbox(t.Target));
        // 1.9+: wait for the charge, unless hurt badly with the mob in reach (an uncharged hit still knocks it back).
        _emergency = Rules.AttackCooldownTicks(Math.Max(0, _heldId)) > 0 && _w.Player.Health <= Options.EmergencySwingHealth && t.Reach <= Options.Reach;
        t.Cooldown = Cooldown(t.Target.Id);
        if (MobCreeperTick(ref t) is { } creeper) return creeper;
        MobPosition(ref t);
        if (MobSwingTick(ref t) is { } swing) return swing;
        return MobMoveInput(ref t);
    }

    // ---- 1.9+ fight-or-flight: a reflex fight that turned out unwinnable (more mobs, hp lost)

    /// <summary>Checked every 10 ticks of a reflex fight: an archer it can't beat sends the body into cover (never trade arrows for
    /// nothing); a melee fight it can't win boxes in while it still can.</summary>
    private MoveInput? MobOutmatchedTick(ref MobTurn t)
    {
        var f = t.F;
        bool ranged = t.Prof.Tactic == MobTactic.Ranged;
        if (f.Explicit || f.FleeStart >= 0 || !ranged && t.Prof.Tactic != MobTactic.Melee || _tick - f.OutmatchCheck < 10 || !MayBoxInstead(f.Reason))
            return null;
        f.OutmatchCheck = _tick;
        return ranged ? MobOutmatchedByArcher(ref t) : MobOutmatchedInMelee(ref t);
    }

    private MoveInput? MobOutmatchedByArcher(ref MobTurn t)
    {
        var f = t.F;
        if (Outmatched(t.Phys, t.Target) is not { } why || !CombatGeometry.HasLineOfSight(_w, Eye(t.Target), t.Phys.EyePosition)) return null;
        f.Cover = true;
        f.Outmatched = true;
        f.Stance = null;
        Raise(CombatEventKind.Critical, $"can't win against {f.TargetName} at hp {_w.Player.Health} ({why}): taking cover");
        return CoverTick(f, t.Phys, t.Target, t.Ctx);
    }

    private MoveInput? MobOutmatchedInMelee(ref MobTurn t)
    {
        if (Outmatched(t.Phys, t.Target) is not { } why || CanBoxIn is not { } can || !SafeAsk(can, MeleeThreats(t.Me).Count)) return null;
        Sheltering = true;
        t.Ctx.Shelter = true;
        EndFight(t.F, $"boxing in at hp {_w.Player.Health}: can't win ({why})", t.Phys, t.Ctx);
        return t.Face;
    }

    /// <summary>Low health: disengage inward (or box in when there's nowhere to go), then eat.</summary>
    private MoveInput? MobFleeTick(ref MobTurn t)
    {
        var f = t.F;
        if (_w.Player.Health <= Options.DisengageHealth && f.FleeStart < 0)
        {
            f.FleeStart = _tick;
            if (!f.LowAlerted) { f.LowAlerted = true; Raise(CombatEventKind.Critical, $"hp {_w.Player.Health}/20 fighting {f.TargetName}: disengaging inward"); }
        }
        if (f.FleeStart < 0) return null;
        var threats = _w.Entities.Nearby(t.Me, 12, CombatRules.IsThreat).Select(e => e.Position).ToList();
        if (threats.Count == 0 || t.Dist > 10 || _tick - f.FleeStart > 200) { EndFight(f, $"disengaged at hp {_w.Player.Health}", t.Phys, t.Ctx); return t.Face; }
        var retreat = CombatGeometry.RetreatCell(_w, t.Me, threats, 7, 8);
        // Retreat inward has nowhere to go (no cell, or no walk home from up here): box in instead of dying in the open.
        if (MobShelterInstead(ref t, threats.Count, retreat != null)) return t.Face;
        return MoveOrHold(t.Phys, retreat, FaceAt(t.Phys, t.Target), sneak: false);
    }

    /// <summary>Asks once per fight whether to box in instead of retreating; true = boxing in (the fight ended).</summary>
    private bool MobShelterInstead(ref MobTurn t, int threats, bool canRetreat)
    {
        var f = t.F;
        if (f.ShelterAsked || ShouldShelter is not { } shelter) return false;
        f.ShelterAsked = true;
        bool go;
        try { go = shelter(threats, canRetreat); } catch { go = false; }
        if (!go) return false;
        Sheltering = true;
        t.Ctx.Shelter = true;
        EndFight(f, $"boxing in at hp {_w.Player.Health} ({threats} near, no way out)", t.Phys, t.Ctx);
        return true;
    }

    /// <summary>Creepers: the fuse decides. A fusing one is backed away from (or blocked); otherwise the avoid tactic, or a backoff
    /// after a hit, keeps the distance.</summary>
    private MoveInput? MobCreeperTick(ref MobTurn t)
    {
        if (t.Prof.Tactic != MobTactic.Creeper && !AnyFusingCreeper(t.Me, out _)) return null;
        if (AnyFusingCreeper(t.Me, out var fusing)) return MobBackOffFuse(ref t, fusing!);
        t.F.FuseAlerted = false;
        if (Options.Creeper != CreeperTactic.Avoid && _tick >= t.F.BackoffUntil) return null;
        double keep = Options.Creeper == CreeperTactic.Avoid ? 6 : 5;
        if (t.Dist < keep) return MoveOrHold(t.Phys, CombatGeometry.RetreatCell(_w, t.Me, [t.Target.Position], 6, keep), t.Look, sneak: false);
        return t.Look;
    }

    private MoveInput MobBackOffFuse(ref MobTurn t, Entity fusing)
    {
        if (!t.F.FuseAlerted)
        {
            t.F.FuseAlerted = true;
            Log("creeper_fuse", t.Phys, fusing.Position, null);
            Raise(CombatEventKind.Critical, $"creeper fusing {Horizontal(t.Me, fusing.Position):0.#}m away: backing off");
        }
        var away = CombatGeometry.RetreatCell(_w, t.Me, [fusing.Position], 7, CombatRules.CreeperCancelRange + 0.5);
        if (away is null) { t.Ctx.Block = CanBlockNow(fusing); return Slow(t.Look); }
        return MoveOrHold(t.Phys, away, t.Look, sneak: false);
    }

    /// <summary>Positioning: never with the void behind (knockback goes away from the attacker); out of reach, close in (an archer
    /// by its zig-zag; a melee mob that is coming is let come).</summary>
    private void MobPosition(ref MobTurn t)
    {
        t.EdgeBehind = CombatGeometry.EdgeBehind(_w, t.Me, t.Target.Position, Options.KnockbackDepth);
        if (t.EdgeBehind)
        {
            MobStanceAwayFromEdge(ref t);
            return;
        }
        if (!(t.Reach > Options.Reach - 0.2)) return;
        var f = t.F;
        f.Stance = null;
        bool melee = t.Prof.Tactic is MobTactic.Melee or MobTactic.Enderman or MobTactic.Creeper;
        if (t.Prof.Tactic == MobTactic.Ranged)
            t.MoveTo = RangedApproach(f, t.Phys, t.Target);
        else if (!(melee && t.Approaching && t.Dist < 8)) // let it come to us; otherwise close in on safe ground only
        {
            t.MoveTo = SafeApproachPoint(t.Me, t.Target.Position, Options.Reach - 0.6, out bool up);
            t.ClimbStep = up;
        }
    }

    private void MobStanceAwayFromEdge(ref MobTurn t)
    {
        var f = t.F;
        if (f.Stance is not { } st || _tick > f.StanceUntil || CombatGeometry.EdgeBehind(_w, st.BottomCenter, t.Target.Position, Options.KnockbackDepth))
        {
            f.Stance = CombatGeometry.ChooseStance(_w, t.Me, t.Target.Position, Options.KnockbackDepth)
                       ?? CombatGeometry.RetreatCell(_w, t.Me, [t.Target.Position], 6);
            f.StanceUntil = _tick + 20;
            Log("reposition", t.Phys, t.Target.Position, new() { ["to"] = f.Stance?.ToString() ?? "none", ["why"] = "edge behind" });
        }
        if (f.Stance is { } s2) t.MoveTo = s2.BottomCenter;
    }

    // ---- swing timing: one full hit per invulnerability window; crit when falling

    /// <summary>The swing when the target is in reach and the charge is back (unless rising into a crit); null = no swing this tick
    /// (<see cref="MobTurn.Jump"/> says whether to jump for one).</summary>
    private MoveInput? MobSwingTick(ref MobTurn t)
    {
        var phys = t.Phys;
        bool inReach = t.Reach <= Options.Reach && CombatGeometry.HasLineOfSight(_w, phys.EyePosition, CombatRules.AimPoint(t.Target));
        bool safeForJump = Safety.EdgeDistance(_w, (int)Math.Floor(t.Me.X), (int)Math.Floor(t.Me.Y + 1e-6), (int)Math.Floor(t.Me.Z), 4) > Options.KnockbackDepth;
        t.Jump = WantsCritJump(in t, inReach, safeForJump);
        if (!inReach || t.Cooldown > 0 || RisingForCrit(phys, t.Cooldown)) return null;
        return MobSwingOrHoldFire(ref t, safeForJump);
    }

    /// <summary>A jump now meets the swing with a fall (a crit): in reach at 4-6 ticks of cooldown, or <see cref="MeetsApproachingMob"/>.</summary>
    private bool WantsCritJump(in MobTurn t, bool inReach, bool safeForJump)
    {
        if (!Options.Crits || !t.Phys.OnGround || !safeForJump || t.MoveTo is not null) return false;
        return inReach && t.Cooldown is >= 4 and <= 6 || MeetsApproachingMob(in t, inReach);
    }

    /// <summary>1.9+: every charged hit knocks a melee mob ~2.5 blocks back, so the next one is charged before it walks back into reach.
    /// Jump as it comes (within a block of reach) so the fall meets it; on the ground only a jump in reach could crit.</summary>
    private bool MeetsApproachingMob(in MobTurn t, bool inReach) =>
        Rules.AttackCooldownTicks(Math.Max(0, _heldId)) > 0 && t.Prof.Tactic == MobTactic.Melee && !inReach && t.Approaching
        && t.Reach <= Options.Reach + 1.0 && t.Cooldown <= 6;

    /// <summary>Off the ground and not falling yet (rising, or at the top of the jump with no fall distance): hold the swing for the crit.</summary>
    private static bool RisingForCrit(PhysicsState phys, int cooldown) =>
        !phys.OnGround && !phys.InWater && !phys.OnLadder && (phys.Velocity.Y > 0 || phys.FallDistance <= 0) && cooldown > -6;

    /// <summary>1.9+: a charged sword hit on the ground sweeps everything around the target: never into a player, a pet or a neutral
    /// mob. Hold it and jump for a crit instead (a crit never sweeps). Conservative: the server judges the step length itself, so a
    /// slow-step sweep is assumed whatever this tick's step was. Otherwise: swing.</summary>
    private MoveInput? MobSwingOrHoldFire(ref MobTurn t, bool safeForJump)
    {
        var phys = t.Phys;
        if (Rules.IsSweep(Context(phys, t.Target) with { WalkedThisTick = 0 }) && SweepVictim(phys, t.Target) is { } bystander)
        {
            if (t.F.Swings == 0 || _tick % 20 == 0) Log("hold_fire", phys, t.Target.Position, new() { ["why"] = $"a sweep would hit {bystander.DisplayName}" });
            t.Jump = phys.OnGround && safeForJump && t.MoveTo is null;
            return null;
        }
        Swing(t.F, t.Target, phys, t.Ctx);
        return t.MoveTo is { } m0 ? CombatGeometry.SteerTo(t.Me, m0, t.Look.Yaw, t.Look.Pitch) : t.Look;
    }

    /// <summary>The tick's move: block between swings, steer to the chosen spot, jump (crit or a climb step), sprint after a fleeing
    /// animal or an archer.</summary>
    private MoveInput MobMoveInput(ref MobTurn t)
    {
        var phys = t.Phys;
        if (BlocksBetweenSwings(in t)) t.Ctx.Block = true;
        var input = t.MoveTo is { } m ? CombatGeometry.SteerTo(t.Me, m, t.Look.Yaw, t.Look.Pitch) : t.Look;
        if (t.Jump || t.ClimbStep && phys.OnGround && phys.CollidedHorizontally) input = input with { Jump = true };
        if (SprintsAfterTarget(in t)) input = input with { Sprint = true };
        return t.Ctx.Block ? Slow(input) : input;
    }

    /// <summary>Block between swings (melee threats close; b1.8.1 a sword, 1.9+ a shield that an axe isn't about to disable).</summary>
    private bool BlocksBetweenSwings(in MobTurn t) =>
        Options.Block && CanBlockNow(t.Target) && t.Dist <= 4 && t.Cooldown >= Math.Max(2, Rules.BlockRaiseTicks) && !t.Jump && t.Phys.OnGround
        && t.Prof.Tactic is not MobTactic.Creeper;

    /// <summary>A passive animal runs when hit (panic: faster than a walk): sprint after it on open ground, like a player hunting.
    /// Closing on an archer: sprint the zig-zag (fewer arrows on the way in). Never with the void behind or while blocking.</summary>
    private bool SprintsAfterTarget(in MobTurn t)
    {
        if (t.MoveTo is null || t.EdgeBehind || t.Ctx.Block || _w.Player.Food <= 6) return false;
        return !t.Prof.Hostile && t.Dist > Options.Reach || t.Prof.Tactic == MobTactic.Ranged && t.Dist > Options.Reach + 1;
    }
}
