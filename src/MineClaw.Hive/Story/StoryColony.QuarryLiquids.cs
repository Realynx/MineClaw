using MineClaw.Data;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private ActiveJob? QuarryLavaJob(PawnState p, PawnLive live, ZoneState z, P3 at)
    {
        if (World is not { } w) return null;
        if (HaulDest(Items.LavaBucket, 0, live.Block)?.Chest == null)
        {
            Blocked($"quarry {ZoneName(z)}: needs storage accepting lava buckets");
            return null;
        }
        var carried = live.Inventory.Any(s => s.Id == Items.Bucket && s.Count > 0);
        var stored = carried ? null : FindStored(s => s.Id == Items.Bucket, live.Block);
        if (!carried && stored == null)
        {
            if (!_current.Values.Any(j => j.Kind == "scoop_lava"))
                NeedTask(w, Items.Bucket, 1, $"collecting lava from quarry {ZoneName(z)}");
            Blocked($"quarry {ZoneName(z)}: needs an empty bucket to collect lava (3 iron ingots at a crafting table)");
            return null;
        }
        var job = Job(WorkTypes.Mine, "scoop_lava", $"collecting quarry lava (y {at.Y})", at, "b:" + at, "zone", z.Id,
            fill: j =>
            {
                j["block"] = "bucket";
                if (stored is { } st) j["fetch"] = Fetch(st.Store.Chest, st.Stack, Math.Min(4, st.Stack.Count));
            });
        if (stored is { } take) job.Takes.Add((take.Store.Chest, take.Stack with { Count = Math.Min(4, take.Stack.Count) }));
        // Remember ownership before execution: a filled bucket must still be delivered after an interruption or restart.
        job.Assign = () => { p.QuarryLava = true; p.Rev = Touch(); };
        return job;
    }

    private ActiveJob? QuarryLavaDelivery(PawnState p, PawnLive live)
    {
        if (!p.QuarryLava) return null;
        var lava = live.Inventory.Where(s => s.Id == Items.LavaBucket).ToList();
        if (lava.Count == 0) { p.QuarryLava = false; p.Rev = Touch(); return null; }
        if (Skipped("deliver:" + p.Name)) return null;
        // Fill the empty buckets already carried before making a storage trip, while mining is still scheduled.
        if (live.FreeSlots > NearlyFull && live.Inventory.Any(s => s.Id == Items.Bucket && s.Count > 0)
            && p.Priorities.GetValueOrDefault(WorkTypes.Mine) is >= 1 and <= 4
            && ScheduleBlockAt(p, HourNow) is ScheduleBlocks.Work or ScheduleBlocks.Anything
            && World is { } w && Zones.Where(IsQuarry).Any(z => Quarry(z) is { } q && q.SealFix.Select(f => f.At)
                .Concat(q.StepFix.Select(f => f.At)).Any(at => IsSourceAt(w, at, lava: true) && Open("b:" + at, p.Name, at)))) return null;
        return DeliverJob(p, live, lava, dumpOk: false);
    }
}
