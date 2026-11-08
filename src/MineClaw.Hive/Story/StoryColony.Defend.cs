using MineClaw.Client.Combat;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>
/// Guarding (docs/web/story-mode.md "Defending"): which monsters a pawn goes after. Live 11-04: "attack failed: no skeleton within 16"
/// ×15, creeper ×8, zombie ×7 for one pawn (82 failures): the merged view showed every body's monsters, cave mobs under home
/// included, and each one sent a pawn walking for nothing. Only a monster that is (a) fresh (some body had news of it in the last
/// <see cref="MobFresh"/>), (b) in a home area, (c) reachable: near home's ground level and not under ground (a cave), and (d) not
/// one this pawn just failed to find (<see cref="MobRest"/>, its own rest, never an alert). Among pawns that guard and have nothing
/// better to do, the nearest goes.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>A monster nobody had news of for this long is stale (out of every body's sight).</summary>
    public static readonly TimeSpan MobFresh = TimeSpan.FromSeconds(45);
    /// <summary>A monster a pawn couldn't find or reach rests this long for that pawn.</summary>
    public static readonly TimeSpan MobRest = TimeSpan.FromSeconds(60);
    /// <summary>A monster more than this far above or below home's ground (and the pawn) is not guarded against.</summary>
    public const int GuardDy = 8;

    private bool Fresh(StoryEntity e) => e.SeenAt is not { } s || Now - s <= MobFresh;

    /// <summary>A cell a pawn can walk to from home's level: within <see cref="GuardDy"/> of home's ground (or 3 of the pawn) and
    /// not under ground.</summary>
    private bool OnHomeLevel(P3 at, P3 home, P3 from) =>
        (Math.Abs(at.Y - home.Y) <= GuardDy || Math.Abs(at.Y - from.Y) <= 3) && !(World is { } w && StoryWander.Underground(w, at));

    internal ActiveJob? GuardJob(PawnState p, PawnLive live)
    {
        if (GuardZoneJob(p, live) is { } zone) return zone;
        if (Home() is not { } home) return null;
        var from = live.Block;
        foreach (var mob in Ents().Where(e => e.Kind == "mob" && e.Hostile && Fresh(e) && InHome(e.Block, home) && Open("e:" + e.Id, p.Name, null)
                     && OnHomeLevel(e.Block, home, from) && GuardSees(p, live, e)).OrderBy(e => e.Block.Dist(from)))
        {
            if (CloserGuard(p, mob)) continue;
            return Job(WorkTypes.Guard, "attack", $"guarding: {mob.Name}", mob.Block, "e:" + mob.Id, "guard", mob.Id.ToString(), "fighting",
                j => j["mob"] = mob.Name, xp: 25);
        }
        return null;
    }

    private bool GuardSees(PawnState p, PawnLive live, StoryEntity mob)
    {
        var w = Host.ViewOf(p.Name) ?? World;
        if (!live.Online || World is not { } colony || live.Dim != colony.Dimension || w == null
            || live.Dim != w.Dimension || !w.Loaded(mob.Block.X, mob.Block.Z)) return false;
        return CombatGeometry.HasLineOfSight(new StoryBlockAccess(w), new Vec3d(live.X, live.Y + 1.62, live.Z),
            new Vec3d(mob.X, mob.Y + 0.5, mob.Z));
    }

    private void GuardTick()
    {
        foreach (var (name, job) in _current.Where(kv => kv.Value.Source == "guard").ToList())
        {
            if (job.GuardZone) continue;
            var mob = Ents().FirstOrDefault(e => e.Id.ToString() == job.SourceId);
            if (mob != null && Fresh(mob) && GuardSees(Pawns[name], Host.Live(name), mob)) continue;
            Supersede(Pawns[name], "guard target out of sight");
            Pawns[name].Rev = Bump();
        }
        GuardZoneTick();
    }

    /// <summary>Another guard with nothing better to do (idle, wandering) is clearly nearer the monster: it goes instead.</summary>
    private bool CloserGuard(PawnState p, StoryEntity mob)
    {
        double mine = mob.Block.Dist(Host.Live(p.Name).Block);
        foreach (var o in Pawns.Values)
        {
            if (!FreeGuard(o, p, mob)) continue;
            var l = Host.Live(o.Name);
            if (l.Online && mob.Block.Dist(l.X, l.Y, l.Z) + 4 < mine && GuardSees(o, l, mob)) return true;
        }
        return false;
    }

    /// <summary>Another undrafted guard (priority 1–4) with nothing better to do (idle, wandering) that could find this monster.</summary>
    private bool FreeGuard(PawnState o, PawnState p, StoryEntity mob)
    {
        if (o == p || o.Drafted || o.Priorities.GetValueOrDefault(WorkTypes.Guard) is < 1 or > 4) return false;
        if (_current.TryGetValue(o.Name, out var cur) && cur.Source is not ("none" or "wander")) return false;
        return !Skipped(PawnKey("e:" + mob.Id, o.Name)); // it couldn't find this one
    }
}
