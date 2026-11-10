namespace MineClaw.Hive.Story;

/// <summary>A storage chest's ledger as the web's container panel reads it (a copy; <see cref="Contents"/> are item totals).</summary>
public sealed record StoryChestView(string Colony, P3 Chest, P3? DoubleWith, IReadOnlyList<Stack> Contents, DateTimeOffset? KnownAt, int Slots,
    int FreeSlots, string Priority, string? Label);

/// <summary>
/// Read-only views of the storage ledger for the viewer's container panel (<c>/api/world/containers</c>). They read a copy published at
/// the end of each tick's lock section and of each storage command, never the colony's lock: a chest click must not wait for a pick,
/// a tick or a save (a deposit shows within a tick, about a second; a storage command at once).
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>One published copy of the ledger: every chest, and each chest by either of its cells (the first storage wins, as in
    /// <see cref="StorageAt"/>). Never changed after it is published.</summary>
    private sealed record LedgerSnapshot(IReadOnlyList<StoryChestView> All, Dictionary<P3, StoryChestView> ByCell);

    private LedgerSnapshot? _ledger;

    /// <summary>The storage chest with a block at <paramref name="at"/> (either half of a double chest), or null.</summary>
    public StoryChestView? ChestView(P3 at) => Ledger().ByCell.GetValueOrDefault(at);

    /// <summary>Every storage chest of the colony.</summary>
    public IReadOnlyList<StoryChestView> ChestViews() => Ledger().All;

    /// <summary>The last published copy; a colony that has not ticked yet builds its first one under the lock, once.</summary>
    private LedgerSnapshot Ledger()
    {
        if (Volatile.Read(ref _ledger) is { } published) return published;
        lock (_lock)
        {
            if (_ledger == null) PublishLedger();
            return _ledger!;
        }
    }

    /// <summary>A fresh copy of the ledger, rebuilt whole every time (no change tracking to get wrong). Called under the lock.</summary>
    private void PublishLedger()
    {
        var all = new StoryChestView[Storages.Count];
        var byCell = new Dictionary<P3, StoryChestView>(Storages.Count * 2);
        for (int i = 0; i < all.Length; i++)
        {
            var v = all[i] = View(Storages[i]);
            byCell.TryAdd(v.Chest, v);
            if (v.DoubleWith is { } d) byCell.TryAdd(d, v);
        }
        Volatile.Write(ref _ledger, new LedgerSnapshot(Array.AsReadOnly(all), byCell));
    }

    private StoryChestView View(StorageState s) =>
        new(Id, s.Chest, s.DoubleWith, s.Contents.ToArray(), s.KnownAt, s.Slots, s.FreeSlots, s.Priority, s.Label);
}
