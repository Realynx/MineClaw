using MineClaw.World;

namespace MineClaw.Client.Combat;

/// <summary>The reflex scan (<see cref="Scan"/>): which nearby mob, if any, a reflex fight starts on, and why.</summary>
public sealed partial class CombatEngine
{
    /// <summary>What the scan knows about one candidate mob.</summary>
    private readonly record struct MobSighting(Entity E, bool Provoked, long ProvokedTick, double D, bool Ranged, bool Creeper, bool InReach,
        bool Sensed, bool CreeperDanger);

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
            if (ScanReason(phys, me, e, guards) is not { } hit || hit.D >= bestD) continue;
            bestD = hit.D;
            best = (e, hit.Why, hit.Cover);
        }
        return best;
    }

    /// <summary>Why a fight would start on <paramref name="e"/> (and whether it is into cover), with its distance; null = no reason.</summary>
    private (string Why, bool Cover, double D)? ScanReason(PhysicsState phys, Vec3d me, Entity e, List<Entity?> guards)
    {
        if (CombatRules.IsTamedWolf(e)) return null;
        var prof = CombatRules.ProfileOf(e);
        if (prof.Tactic == MobTactic.Ignore) return null;
        bool provoked = _provoked.TryGetValue(e.Id, out var pt) && _tick - pt < 200;
        if (ExcusedFromScan(e, provoked)) return null;
        var s = Sight(phys, me, e, prof, provoked, pt);
        Unreachable? u;
        lock (_unreachable) _unreachable.TryGetValue(e.Id, out u);
        var reason = u is not null ? GivenUpReason(phys, me, in s, u) : FreshReason(phys, me, in s, guards);
        return reason is { } r ? (r.Why, r.Cover, s.D) : null;
    }

    /// <summary>Not a threat unless it hit us: a peaceful mob, an enderman, a spider in daylight (neutral).</summary>
    private bool ExcusedFromScan(Entity e, bool provoked) =>
        !CombatRules.IsThreat(e) && !provoked
        || e.TypeId == MobTypes.Enderman && !provoked
        || e.TypeId == MobTypes.Spider && !_w.IsNight && !provoked;

    private MobSighting Sight(PhysicsState phys, Vec3d me, Entity e, MobProfile prof, bool provoked, long provokedTick)
    {
        double d = e.Position.DistanceTo(me);
        bool ranged = prof.Tactic == MobTactic.Ranged;
        bool creeper = e.TypeId == MobTypes.Creeper;
        bool inReach = InMeleeReach(phys, e);
        // Out of its sight and off our level (a cave under the floor, a ledge): no danger until it shows up or hits us.
        bool sensed = provoked || Senses(e, me);
        bool creeperDanger = sensed && creeper && (CombatRules.IsCreeperFusing(e) ? d <= CombatRules.CreeperCancelRange + 1 : Horizontal(me, e.Position) <= Options.CreeperAvoidRadius);
        return new MobSighting(e, provoked, provokedTick, d, ranged, creeper, inReach, sensed, creeperDanger);
    }

    /// <summary>A mob given up on: only a real change re-triggers (live 10-27 22:09-22:10: a creeper across a gap restarted a 20 s fight
    /// every 200 ms and cancelled every walk north).</summary>
    private (string Why, bool Cover)? GivenUpReason(PhysicsState phys, Vec3d me, in MobSighting s, Unreachable u)
    {
        if (GivenUpCreeperReason(me, in s, u) is { } creeperWhy) return (creeperWhy, false);
        if (s.InReach && DefendSelf) return ("in reach", false);
        bool hitMeSince = s.Provoked && s.ProvokedTick > u.Tick;
        if (!hitMeSince || !DefendSelf) return null;
        if (!s.Ranged) return ("it attacked", false);
        return MayTakeCoverAgain(phys, s.E, u) ? ("taking cover", true) : null;
    }

    /// <summary>An archer given up on that keeps shooting: cover again at low hp, unseen, once the last cover is long past.</summary>
    private bool MayTakeCoverAgain(PhysicsState phys, Entity e, Unreachable u) =>
        _w.Player.Health <= Options.CoverHealth && _tick - u.LastCover > Options.CoverMaxTicks * 2
        && CombatGeometry.HasLineOfSight(_w, Eye(e), phys.EyePosition);

    /// <summary>A given-up creeper that's merely close re-triggers only once it has moved since (out of its pocket) or fuses (live
    /// 10-28 17:20: one trapped 3 m west of the tower restarted an 8 s fight back to back, freezing main).</summary>
    private string? GivenUpCreeperReason(Vec3d me, in MobSighting s, Unreachable u)
    {
        if (CombatRules.IsCreeperFusing(s.E) && s.CreeperDanger) return "creeper fusing";
        if (s.CreeperDanger && Horizontal(u.Pos, s.E.Position) > 1.0 && (s.InReach || CanWalkTo(me, s.E))) return "creeper close";
        return null;
    }

    /// <summary>Only what the bot can actually get to starts a fight: within melee reach, or a safe first step toward it. Self-defence
    /// first, then a guarded player's attacker.</summary>
    private (string Why, bool Cover)? FreshReason(PhysicsState phys, Vec3d me, in MobSighting s, List<Entity?> guards)
    {
        bool approachable = s.InReach || CanWalkTo(me, s.E);
        var reason = DefendSelf && s.Sensed ? DefendReason(phys, in s, approachable) : null;
        if (reason is null && GuardReason(in s, approachable, guards) is { } guarding) reason = (guarding, false);
        return reason;
    }

    private (string Why, bool Cover)? DefendReason(PhysicsState phys, in MobSighting s, bool approachable)
    {
        // One that hit us from water over its head is fought only once it's in reach (no chasing it in).
        if (s.Provoked && s.D <= 10 && !s.Ranged && (s.InReach || !CombatGeometry.DeepWater(_w, s.E.Position))) return ("it attacked", false);
        if (s.Ranged && (s.Provoked || CombatGeometry.HasLineOfSight(_w, phys.EyePosition, Eye(s.E)))) return ArcherReason(in s, approachable);
        if (Brave) return BraveReason(in s);
        return CautiousReason(in s, approachable);
    }

    /// <summary>Live 10-28 night: skeletons at 4-11 m on ledges/across gaps held every bot "busy" with 0 hits. Shot from within its
    /// range by one it can walk to: close in (an unwinnable one turns into cover in Tick).</summary>
    private (string Why, bool Cover)? ArcherReason(in MobSighting s, bool approachable)
    {
        if ((s.D <= Options.SkeletonEngageRange || s.Provoked && s.D <= OutOfArcherRange - 2) && approachable && (s.Provoked || !Brave))
            return (s.Provoked ? "it attacked" : "skeleton in sight", false);
        if (s.Provoked && _w.Player.Health <= Options.CoverHealth) return ("taking cover", true);
        return null;
    }

    private static (string Why, bool Cover)? BraveReason(in MobSighting s)
    {
        if (s.CreeperDanger && CombatRules.IsCreeperFusing(s.E)) return ("creeper fusing", false);
        if (s.InReach && !s.Creeper) return ("in reach", false);
        return null;
    }

    private (string Why, bool Cover)? CautiousReason(in MobSighting s, bool approachable)
    {
        if (s.CreeperDanger) return (CombatRules.IsCreeperFusing(s.E) ? "creeper fusing" : "creeper close", false);
        if (s.Creeper && s.D <= CombatRules.CreeperCancelRange && approachable) return ("creeper close", false);
        if (!s.Creeper && s.D <= Options.DefendRadius && approachable) return ("defending myself", false);
        return null;
    }

    /// <summary>A mob near a guarded player, when the bot can get to it (an archer only within its engage range).</summary>
    private string? GuardReason(in MobSighting s, bool approachable, List<Entity?> guards)
    {
        if (!approachable || s.Ranged && !(s.D <= Options.SkeletonEngageRange)) return null;
        foreach (var g in guards)
            if (g!.Position.DistanceTo(s.E.Position) <= Options.GuardRadius) return "guarding " + g.Name;
        return null;
    }
}
