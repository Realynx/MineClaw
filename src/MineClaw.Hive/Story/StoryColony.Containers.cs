namespace MineClaw.Hive.Story;

/// <summary>A storage chest's ledger as the web's container panel reads it (a copy; <see cref="Contents"/> are item totals).</summary>
public sealed record StoryChestView(string Colony, P3 Chest, P3? DoubleWith, IReadOnlyList<Stack> Contents, DateTimeOffset? KnownAt, int Slots,
    int FreeSlots, string Priority, string? Label);

/// <summary>Read-only views of the storage ledger for the viewer's container panel (<c>/api/world/containers</c>), under the colony's lock.</summary>
public sealed partial class StoryColony
{
    /// <summary>The storage chest with a block at <paramref name="at"/> (either half of a double chest), or null.</summary>
    public StoryChestView? ChestView(P3 at)
    {
        lock (_lock) return StorageAt(at) is { } s ? View(s) : null;
    }

    /// <summary>Every storage chest of the colony.</summary>
    public IReadOnlyList<StoryChestView> ChestViews()
    {
        lock (_lock) return Storages.Select(View).ToList();
    }

    private StoryChestView View(StorageState s) =>
        new(Id, s.Chest, s.DoubleWith, s.Contents.ToList(), s.KnownAt, s.Slots, s.FreeSlots, s.Priority, s.Label);
}
