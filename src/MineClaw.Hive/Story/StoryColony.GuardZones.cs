namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private bool InGuardZone(StoryEntity mob) => mob.Kind == "mob" && Fresh(mob) && !ForbiddenEntity(mob.Id)
        && !Forbidden(mob.Block) && Zones.Any(z => z.Kind == "guard" && z.Contains(mob.Block.X, mob.Block.Z));

    private bool ColonySees(StoryEntity mob) => Pawns.Values.Any(p => GuardSees(p, Host.Live(p.Name), mob));

    /// <summary>Explicitly painted guard columns admit any mob, including animals, at any height. A current sighting from a
    /// colony pawn may bring the others over, but each attacker must acquire its own sight before it can fight.</summary>
    private (StoryEntity Mob, bool Visible)? GuardZoneTarget(PawnState p, PawnLive live)
    {
        if (!live.Online || World is not { } world || live.Dim != world.Dimension || p.Drafted
            || p.Priorities.GetValueOrDefault(WorkTypes.Guard) is not (>= 1 and <= 4)
            || !Zones.Any(z => z.Kind == "guard")) return null;
        foreach (var mob in Ents().Where(InGuardZone).OrderByDescending(e => e.Hostile).ThenBy(e => e.Block.Dist(live.Block)))
        {
            if (Skipped("e:" + mob.Id) || Skipped(PawnKey("e:" + mob.Id, p.Name)) || NearHazard(mob.Block, p.Name)) continue;
            bool visible = GuardSees(p, live, mob);
            if (visible || ColonySees(mob)) return (mob, visible);
        }
        return null;
    }

    private ActiveJob? GuardZoneJob(PawnState p, PawnLive live)
    {
        if (GuardZoneTarget(p, live) is not { } target) return null;
        var (mob, visible) = target;
        // No entity lease: every available guard may help with the same target, as with a drafted group attack.
        var job = Job(WorkTypes.Guard, visible ? "attack" : "go", $"guard zone: {(visible ? "attacking" : "approaching")} {mob.Name}",
            mob.Block, null, "guard", mob.Id.ToString(), visible ? "fighting" : "moving", j =>
            {
                if (visible) { j["mob"] = mob.Name; j["target"] = mob.Id; }
                else j["dist"] = 0; // do not finish on the far side of an opaque wall; the tick stops this once sight is clear
            }, xp: visible ? 25 : 0);
        job.GuardZone = true;
        return job;
    }

    private void StopInvalidZoneGuards()
    {
        var entities = World?.Entities();
        foreach (var (name, job) in _current.Where(kv => kv.Value.GuardZone).ToList())
        {
            var mob = entities?.FirstOrDefault(e => e.Id.ToString() == job.SourceId);
            bool sameDimension = World is { } world && Host.Live(name).Dim == world.Dimension;
            if (sameDimension && mob != null && InGuardZone(mob)) continue;
            Supersede(Pawns[name], sameDimension ? "guard target left the guard zone" : "guard target is in another dimension");
            Pawns[name].Rev = Bump();
        }
    }

    private void GuardZoneTick()
    {
        StopInvalidZoneGuards();
        if (!Meta.Active || Meta.Paused) return;
        foreach (var (name, job) in _current.Where(kv => kv.Value.GuardZone).ToList())
        {
            var p = Pawns[name];
            var mob = Ents().FirstOrDefault(e => e.Id.ToString() == job.SourceId);
            bool sees = mob != null && GuardSees(p, Host.Live(name), mob);
            string? why = mob == null || !ColonySees(mob) ? "guard target out of sight"
                : job.Kind == "attack" && !sees ? "guard target out of sight"
                : job.Kind == "go" && sees ? "guard target now in sight"
                : job.Kind == "go" && job.At is { } at && mob.Block.Dist(at) > 3 ? "guard target moved" : null;
            if (why == null) continue;
            Supersede(p, why);
            p.Rev = Bump();
        }
        if (!Zones.Any(z => z.Kind == "guard")) return;
        foreach (var p in Pawns.Values)
        {
            if (!MayLeaveForGuardZone(p) || GuardZoneTarget(p, Host.Live(p.Name)) == null) continue;
            Supersede(p, "mob in guard zone");
            p.Rev = Bump();
        }
    }

    /// <summary>Guard work preempts lower-priority ordinary work, and a dedicated guard also leaves its equal-priority hunt.
    /// Direct orders, needs, schedules, deliveries, equipment, rescue and escape work always keep control.</summary>
    private bool MayLeaveForGuardZone(PawnState p)
    {
        int priority = p.Priorities.GetValueOrDefault(WorkTypes.Guard);
        if (p.Drafted || p.Queue.Count > 0 || _leaving.ContainsKey(p.Name) || priority is < 1 or > 4
            || ScheduleBlockAt(p, HourNow) is not (ScheduleBlocks.Work or ScheduleBlocks.Anything)
            || !_current.TryGetValue(p.Name, out var job) || job.GuardZone) return false;
        if (job.Source is "none" or "wander") return job.Type == "idle";
        if (job.Source is not ("designation" or "ghost" or "bill" or "furnace" or "drop" or "zone" or "crop" or "sow" or "guard" or "fish")
            || !WorkTypes.All.Contains(job.Type) || job.Type == WorkTypes.Doctor) return false;
        int current = p.Priorities.GetValueOrDefault(job.Type);
        return p.Role == "guard" || current == 0 || priority < current;
    }
}
