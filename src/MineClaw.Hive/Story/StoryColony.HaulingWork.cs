namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private readonly Dictionary<string, int> _haulLane = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HaulInspectionPeriod = TimeSpan.FromMinutes(2);

    private string HaulItemName(Stack stack)
    {
        if (World is { } world && world.Data.ItemExists(stack.Id) && world.Data.Item(stack.Id) is { MaxDamage: > 0 } durable)
            return durable.Name;
        var name = BillTakeName(stack);
        return World?.Data.ItemByNameWithDamage(name) is { } item && item.Item.Id == stack.Id && item.Damage == stack.Meta
            ? name : $"{stack.Id}:{stack.Meta}";
    }

    private sealed class HaulSpace
    {
        private int _free;
        private readonly Func<int, int> _max;
        private readonly Dictionary<(int Id, int Meta), int> _partial = new();

        internal HaulSpace(StorageState storage, Func<int, int> max)
        {
            _free = storage.FreeSlots;
            _max = max;
            foreach (var stack in storage.Contents)
            {
                int left = stack.Count % max(stack.Id);
                if (left > 0) _partial[(stack.Id, stack.Meta)] = _partial.GetValueOrDefault((stack.Id, stack.Meta)) + max(stack.Id) - left;
            }
        }

        internal int Reserve(int id, int meta, int requested)
        {
            int max = _max(id), partial = _partial.GetValueOrDefault((id, meta));
            int count = Math.Min(requested, _free * max + partial);
            int stacked = Math.Min(partial, count), fresh = count - stacked;
            int slots = (fresh + max - 1) / max;
            _free -= slots;
            _partial[(id, meta)] = partial - stacked + slots * max - fresh;
            return count;
        }
    }

    /// <summary>Hauling includes ground pickups, storage organization, and discovering what players put in chests.
    /// Explicit pickups and endangered drops come first; ordinary work takes turns only when a pawn accepts a job.
    /// Each part is timed for the perf line's "haul parts" (seen, ents, drops, resort, look; StoryPerf.HaulPart): live 11-10 work:haul
    /// was 56-60 s a minute and a bench in the live storage shape reached only a small part of that.</summary>
    private ActiveJob? HaulingWork(PawnState pawn, PawnLive live)
    {
        long t = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Host.KnowsChests) RefreshSeen(Storages);
        t = StoryPerf.HaulPart("seen", t);
        if (World != null) Ents(); // the world's entities, read once a pick: whoever asks first pays for it (HaulJob asks next)
        t = StoryPerf.HaulPart("ents", t);
        var drop = HaulJob(pawn, live);
        StoryPerf.HaulPart("drops", t);
        if (drop != null && (drop.Source == "designation" || DropAtRisk(drop.Lease)))
            return drop;
        int first = _haulLane.GetValueOrDefault(pawn.Name);
        for (int offset = 0; offset < 3; offset++)
        {
            int lane = (first + offset) % 3;
            ActiveJob? job;
            if (lane == 0) job = drop;
            else
            {
                long tl = System.Diagnostics.Stopwatch.GetTimestamp();
                job = lane == 1 ? RebalanceJob(pawn, live) : HaulInspection(pawn, live);
                StoryPerf.HaulPart(lane == 1 ? "resort" : "look", tl);
            }
            if (job == null) continue;
            // Candidate searches also run for idle audits and comparisons with other work. They must not consume a turn.
            job.Assign += () => _haulLane[pawn.Name] = (lane + 1) % 3;
            return job;
        }
        return null;
    }

    /// <summary>The drop a collect leases ("e:id") is in view and at despawn risk (no key string per entity: asked per haul search).</summary>
    private bool DropAtRisk(string? lease)
    {
        if (lease == null || !lease.StartsWith("e:", StringComparison.Ordinal) || !int.TryParse(lease.AsSpan(2), out var id)) return false;
        foreach (var e in Ents())
            if (e.Id == id && Age(e) >= DespawnRisk) return true;
        return false;
    }

    private ActiveJob? HaulInspection(PawnState pawn, PawnLive live)
    {
        if (!Host.KnowsChests || World is not { } world) return null;
        // The cheap checks first (when it was last seen, last tried); the lease key is made only for the chests in order until one is
        // free (it was made for every chest on every haul search).
        var now = Now;
        var chest = Storages.Where(s => !s.Missing
                && (s.SeenAt is not { } seen || now - seen >= HaulInspectionPeriod)
                && (!_looked.TryGetValue(s.Chest, out var attempted) || now - attempted >= HaulInspectionPeriod) && !LookRested(s.Chest)
                && world.Loaded(s.Chest.X, s.Chest.Z) && world.Block(s.Chest.X, s.Chest.Y, s.Chest.Z).Id is 54 or 146
                && !ChestDeconstructing(s.Chest) && ChestOpen(s.Chest))
            .OrderBy(s => s.SeenAt ?? DateTimeOffset.MinValue).ThenBy(s => s.Chest.Dist(live.Block))
            .FirstOrDefault(s => Open("look:" + s.Chest, pawn.Name, s.Chest));
        if (chest == null) return null;
        var job = Job(WorkTypes.Haul, "look", $"checking storage at {chest.Chest}", chest.Chest,
            "look:" + chest.Chest, "look", chest.Chest.ToString(), "working", xp: 2);
        job.Assign = () => _looked[chest.Chest] = Now;
        return job;
    }
}
