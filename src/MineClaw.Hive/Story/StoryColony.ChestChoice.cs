namespace MineClaw.Hive.Story;

/// <summary>
/// Which chests can take an item, worked out once per item kind per pick (docs/web/story-mode.md "Hauling"). Live 11-10 (374 chests, 30
/// pawns): the haul search cost about 0.9 s a call, up to 4.5 s, under the colony's one lock. Every drop it looked at asked
/// <see cref="BestChest"/>, and every stored stack of the re-sort plan did too; each of those walked all 374 chests (the deconstruct
/// marks, the lid, the pawn's unreachable chests, the filter, the room) and ordered the ones that qualified, allocating as it went;
/// the re-sort plan was made again for nearly every pawn (it holds the picking pawn's own unreachable chests). Now a pick (and the
/// tick's despawn check) remembers, per item kind, the chests that take it, in the order <see cref="BestChest"/> and
/// <see cref="Fallback"/> rank them; each question is then one pass over the best-ranked group for the nearest. The answers are the
/// same: highest rank (or the fallback's order), then the nearest, the first in <see cref="Storages"/>' order on a tie.
/// The memory lasts one pick at most and is dropped as soon as anything in the colony changes (its revision, the storage or mark
/// lists, a chest's cells) or another pawn picks.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>The chests that take one item kind: grouped by preference (best group first), each group in <see cref="Storages"/>' order.</summary>
    private sealed class ChestOptions(StorageState[] chests, int[] ends)
    {
        public static readonly ChestOptions None = new([], []);
        /// <summary>The chests, group after group.</summary>
        public readonly StorageState[] Chests = chests;
        /// <summary>Each group's end (exclusive) in <see cref="Chests"/>.</summary>
        public readonly int[] Ends = ends;

        /// <summary>The nearest chest of the best group that has one other than <paramref name="except"/>; ties: the first.</summary>
        public StorageState? Nearest(P3 from, StorageState? except)
        {
            int start = 0;
            foreach (int end in Ends)
            {
                StorageState? best = null;
                double bestD = double.MaxValue;
                for (int i = start; i < end; i++)
                {
                    var s = Chests[i];
                    if (s == except) continue;
                    double d = s.Chest.Dist(from);
                    if (best == null || d < bestD) { best = s; bestD = d; }
                }
                if (best != null) return best;
                start = end;
            }
            return null;
        }
    }

    /// <summary>The pick's memory of <see cref="ChestOptions"/> and what it was made from.</summary>
    private sealed class ChestChoice
    {
        public int Depth;
        public string? Pawn;
        public long Rev, Cells;
        public int StorageVersion, StorageCount, MarkVersion, MarkCount;
        public readonly Dictionary<(int Id, int Meta), ChestOptions> Best = new(), Fallback = new();
        public readonly List<(int Key, StorageState S)> Scratch = new();
        /// <summary>Per storage (in <see cref="Storages"/>' order): not being dismantled and open to the picking pawn; null until asked.</summary>
        public bool[]? Usable;
    }

    private readonly ChestChoice _choice = new();

    /// <summary>A pick (or the tick's despawn check) starts: chests' answers may be remembered until <see cref="EndChestChoice"/>.</summary>
    private void BeginChestChoice()
    {
        if (_choice.Depth++ == 0) ForgetChestChoice();
    }

    private void EndChestChoice()
    {
        if (--_choice.Depth <= 0) { _choice.Depth = 0; ForgetChestChoice(); }
    }

    private void ForgetChestChoice()
    {
        _choice.Best.Clear();
        _choice.Fallback.Clear();
        _choice.Usable = null;
        _choice.Pawn = _pickPawn;
        _choice.Rev = _rev;
        _choice.Cells = StorageState.CellsVersion;
        _choice.StorageVersion = ListVersion<StorageState>.Of(Storages);
        _choice.StorageCount = Storages.Count;
        _choice.MarkVersion = ListVersion<DesignationState>.Of(Designations);
        _choice.MarkCount = Designations.Count;
    }

    /// <summary>Remembering now, and what is remembered still holds (else it is forgotten here). False: outside a pick, work it out.</summary>
    private bool ChestChoiceOn()
    {
        var c = _choice;
        if (c.Depth == 0) return false;
        if (c.Pawn != _pickPawn || c.Rev != _rev || c.Cells != StorageState.CellsVersion || c.StorageVersion != ListVersion<StorageState>.Of(Storages)
            || c.StorageCount != Storages.Count || c.MarkVersion != ListVersion<DesignationState>.Of(Designations) || c.MarkCount != Designations.Count)
            ForgetChestChoice();
        return true;
    }

    /// <summary>The chests <see cref="BestChest"/> may choose for this item: not being dismantled, open to the picking pawn, accepting
    /// it, with room; by rank, highest first.</summary>
    private ChestOptions BestOptions(int id, int meta)
    {
        bool on = ChestChoiceOn();
        if (on && _choice.Best.TryGetValue((id, meta), out var known)) return known;
        var list = _choice.Scratch;
        list.Clear();
        var usable = on ? UsableChests() : null;
        for (int i = 0; i < Storages.Count; i++)
        {
            var s = Storages[i];
            if ((usable?[i] ?? Usable(s)) && Accepts(s, id, meta) && HasRoom(s, id, meta)) list.Add((-StorageRank(s), s));
        }
        var options = Group(list);
        // A lid's alert while asking moves the revision: what was remembered before is forgotten, this answer kept.
        if (on && ChestChoiceOn()) _choice.Best[(id, meta)] = options;
        return options;
    }

    /// <summary>The chests <see cref="Fallback"/> may choose ("unfiltered storage takes anything"): no sign, not being dismantled, open,
    /// with room, not denying it; open ("*") chests first, then the lowest priority.</summary>
    private ChestOptions FallbackOptions(int id, int meta)
    {
        bool on = ChestChoiceOn();
        if (on && _choice.Fallback.TryGetValue((id, meta), out var known)) return known;
        var list = _choice.Scratch;
        list.Clear();
        var usable = on ? UsableChests() : null;
        for (int i = 0; i < Storages.Count; i++)
        {
            var s = Storages[i];
            if (s.Signs.Count == 0 && (usable?[i] ?? Usable(s)) && HasRoom(s, id, meta) && !Denied(s, id, meta))
                list.Add(((s.Filter.Allow.Contains("*") ? 0 : 1) * 100 + StoragePriority.Rank(s.Priority), s));
        }
        var options = Group(list);
        if (on && ChestChoiceOn()) _choice.Fallback[(id, meta)] = options;
        return options;
    }

    /// <summary>Not being dismantled, and open to the picking pawn (its lid, the colony's and the pawn's unreachable chests).</summary>
    private bool Usable(StorageState s) => !ChestDeconstructing(s.Chest) && ChestOpen(s.Chest);

    /// <summary><see cref="Usable"/> for every storage, once per pick (the same for every item kind asked about).</summary>
    private bool[] UsableChests()
    {
        if (_choice.Usable is { } known && known.Length == Storages.Count) return known;
        var usable = new bool[Storages.Count];
        for (int i = 0; i < usable.Length; i++) usable[i] = Usable(Storages[i]);
        // A lid's alert while asking moves the revision (what was remembered is forgotten): these hold all the same.
        ChestChoiceOn();
        return _choice.Usable = usable;
    }

    /// <summary>The qualifying chests grouped by key (smallest first), each group in the order they came (stable).</summary>
    private static ChestOptions Group(List<(int Key, StorageState S)> list)
    {
        if (list.Count == 0) return ChestOptions.None;
        var chests = new StorageState[list.Count];
        var ends = new List<int>(4);
        int n = 0, last = int.MinValue;
        // Few distinct keys (the priorities): taken in order, smallest first, each a pass over the list.
        while (n < list.Count)
        {
            int next = int.MaxValue;
            foreach (var (k, _) in list) if (k > last && k < next) next = k;
            foreach (var (k, s) in list) if (k == next) chests[n++] = s;
            ends.Add(n);
            last = next;
        }
        return new ChestOptions(chests, ends.ToArray());
    }
}
