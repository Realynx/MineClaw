using MineClaw.Data;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>
    /// Per quarry: what its lava sources waited for when a pawn last asked to collect one (no accepting storage, no empty bucket, no
    /// free slot). A lava source is collected, never plugged, so the quarry stands still until it can be: the status says this instead
    /// of "collecting" while nobody is. Forgotten when a collection is handed out or no lava source is left to collect.
    /// </summary>
    private readonly Dictionary<string, string> _quarryLavaHold = new(StringComparer.Ordinal);

    /// <summary>A lava source in a quarry's own columns: collected into a bucket, never plugged, on a step and in the stair's head room
    /// too. Outside every quarry it is a hazard like any liquid: behind the stair's head room it gets a block. Its columns decide, not
    /// its height: with a column out of view no top is kept yet, and a source over the painted level read as outside and got a block.</summary>
    private bool KeptLava(IStoryWorld w, P3 at) => IsSourceAt(w, at, lava: true) && QuarryColumn(at) != null;

    private ActiveJob? QuarryLavaJob(PawnState p, PawnLive live, ZoneState z, P3 at)
    {
        if (World is not { } w) return null;
        if (LavaHold(w, live, z, out var stored) is { } hold)
        {
            _quarryLavaHold[z.Id] = hold;
            Blocked($"quarry {ZoneName(z)}: {hold}");
            return null;
        }
        _quarryLavaHold.Remove(z.Id);
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

    /// <summary>
    /// What keeps this pawn from collecting a lava source now, or null: no chest takes the filled bucket; no empty bucket carried or
    /// stored (<see cref="BucketWait"/> has one made); no free slot for the bucket. <paramref name="stored"/>: the empty buckets to
    /// fetch when it carries none. A bucket is only asked for once a chest takes the filled one: buckets are tools a pawn keeps, so
    /// one crafted with nowhere to store it stays in its crafter's pockets and the next ask would have another made (3 iron each).
    /// </summary>
    private string? LavaHold(IStoryWorld w, PawnLive live, ZoneState z, out (StorageState Store, Stack Stack)? stored)
    {
        stored = null;
        if (HaulDest(Items.LavaBucket, 0, live.Block)?.Chest == null) return "needs storage accepting lava buckets";
        int carried = 0;
        foreach (var s in live.Inventory) if (s.Id == Items.Bucket) carried += s.Count;
        if (carried == 0 && (stored = FindStored(s => s.Id == Items.Bucket, live.Block)) == null) return BucketWait(w, z);
        // A bucket out of a chest takes a slot, and so does the filled one of a stack: 1.12.2 stacks empty buckets, and with no slot
        // the filled bucket drops at the pawn's feet (the collection is then "not confirmed"). One bucket alone is filled in place.
        bool slot = carried == 0 || carried > 1 && MaxStack(Items.Bucket) > 1;
        return slot && live.FreeSlots <= 0 ? "needs a free slot for the bucket (pockets full)" : null;
    }

    /// <summary>
    /// No empty bucket, carried or stored: the colony makes one when it can (an immediate craft task, unless a collection under way
    /// holds the buckets), and the wait says what holds that craft up: no crafting table, a missing ingredient, nobody who crafts.
    /// </summary>
    private string BucketWait(IStoryWorld w, ZoneState z)
    {
        const string needs = "needs an empty bucket to collect lava";
        const string making = needs + " (3 iron ingots at a crafting table)";
        if (_current.Values.Any(j => j.Kind == "scoop_lava")) return making;
        if (!NeedTask(w, Items.Bucket, 1, $"collecting lava from quarry {ZoneName(z)}")) return needs + ": no crafting table to craft one at";
        return CraftHold(w, Items.Bucket) is { } held ? $"{needs}: {held}" : making;
    }

    /// <summary>What holds the open craft task for an item up, in words ("its craft waits (no iron ingot: checked 2 chests …)", "no
    /// pawn does craft work"), or null: it is queued or being made.</summary>
    private string? CraftHold(IStoryWorld w, int outputId)
    {
        StoryRecipe? recipe = null;
        foreach (var r in StoryRecipes.For(w.Data))
            if (r.Workbench == "crafting_table" && r.Output.Id == outputId) { recipe = r; break; }
        if (recipe == null) return null;
        foreach (var bench in Benches)
            foreach (var t in bench.Tasks)
            {
                if (t.Recipe != recipe.Id || t.Paused || t.Mode == "count" && t.Done >= t.Target) continue;
                if ((t.SuspendedReason ?? _taskBlocked.GetValueOrDefault(t.Id)) is { } why) return $"its craft waits ({why})";
                foreach (var o in Pawns.Values)
                    if (o.Priorities.GetValueOrDefault(recipe.WorkType) is >= 1 and <= 4) return null;
                return $"no pawn does {recipe.WorkType} work";
            }
        return null;
    }

    /// <summary>What collecting the lava source at <paramref name="at"/> waits for (what the last pawn to ask was missing), or null: a
    /// pawn holds the cell (it is being collected), or none was turned away.</summary>
    private string? LavaHeld(ZoneState z, P3 at) =>
        _quarryLavaHold.TryGetValue(z.Id, out var hold) && Free("b:" + at, "") ? hold : null;

    /// <summary>What the stair's top missing step waits for: its block, or (a lava source in its cell) the collection.</summary>
    private string StepWait(ZoneState z, QuarryScan q)
    {
        var step = q.StepFix.MaxBy(f => f.At.Y).At;
        if (World is not { } w || !IsSourceAt(w, step, lava: true)) return "placing a step" + RepairRest(step);
        return LavaHeld(z, step) is { } hold ? $"lava source on the step: {hold}" : "collecting a lava source on the step" + RepairRest(step);
    }

    /// <summary>A look that finds no lava source left to collect ends the quarry's remembered wait.</summary>
    private void ForgetLavaHold(IStoryWorld w, ZoneState z, QuarryScan q)
    {
        if (!_quarryLavaHold.ContainsKey(z.Id)) return;
        foreach (var f in q.SealFix) if (KeptLava(w, f.At)) return;
        foreach (var f in q.StepFix) if (IsSourceAt(w, f.At, lava: true)) return;
        // One in the stair's head room, or behind a block of it, is the stair repair's to hand out (StoryColony.QuarryAccess.cs).
        foreach (var c in q.StairClear) if (KeptLava(w, c.At) || LiquidInto(w, c.At) is { } l && KeptLava(w, l)) return;
        _quarryLavaHold.Remove(z.Id);
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
                .Concat(q.StepFix.Select(f => f.At)).Any(at => KeptLava(w, at) && Open("b:" + at, p.Name, at)))) return null;
        return DeliverJob(p, live, lava, dumpOk: false);
    }
}
