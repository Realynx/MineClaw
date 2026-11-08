namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private readonly Dictionary<string, int> _haulLane = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan HaulInspectionPeriod = TimeSpan.FromMinutes(2);

    private string HaulItemName(Stack stack)
    {
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
    /// Explicit pickups and endangered drops come first; ordinary work takes turns only when a pawn accepts a job.</summary>
    private ActiveJob? HaulingWork(PawnState pawn, PawnLive live)
    {
        if (Host.KnowsChests) RefreshSeen(Storages);
        var drop = HaulJob(pawn, live);
        if (drop != null && (drop.Source == "designation" || Ents().Any(e => "e:" + e.Id == drop.Lease && Age(e) >= DespawnRisk)))
            return drop;
        int first = _haulLane.GetValueOrDefault(pawn.Name);
        for (int offset = 0; offset < 3; offset++)
        {
            int lane = (first + offset) % 3;
            var job = lane switch
            {
                0 => drop,
                1 => RebalanceJob(pawn, live),
                _ => HaulInspection(pawn, live),
            };
            if (job == null) continue;
            // Candidate searches also run for idle audits and comparisons with other work. They must not consume a turn.
            job.Assign += () => _haulLane[pawn.Name] = (lane + 1) % 3;
            return job;
        }
        return null;
    }

    private ActiveJob? HaulInspection(PawnState pawn, PawnLive live)
    {
        if (!Host.KnowsChests || World is not { } world) return null;
        var chest = Storages.Where(s => !s.Missing && world.Loaded(s.Chest.X, s.Chest.Z)
                && world.Block(s.Chest.X, s.Chest.Y, s.Chest.Z).Id is 54 or 146
                && !ChestDeconstructing(s.Chest) && ChestOpen(s.Chest)
                && (s.SeenAt is not { } seen || Now - seen >= HaulInspectionPeriod)
                && (!_looked.TryGetValue(s.Chest, out var attempted) || Now - attempted >= HaulInspectionPeriod)
                && Open("look:" + s.Chest, pawn.Name, s.Chest))
            .OrderBy(s => s.SeenAt ?? DateTimeOffset.MinValue).ThenBy(s => s.Chest.Dist(live.Block)).FirstOrDefault();
        if (chest == null) return null;
        var job = Job(WorkTypes.Haul, "look", $"checking storage at {chest.Chest}", chest.Chest,
            "look:" + chest.Chest, "look", chest.Chest.ToString(), "working", xp: 2);
        job.Assign = () => _looked[chest.Chest] = Now;
        return job;
    }
}
