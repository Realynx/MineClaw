namespace MineClaw.Hive.Story;

/// <summary>A sign-only inventory rule sent to bodies. UI preferences are intentionally absent.</summary>
public sealed record StoryStorageSignRule(P3 Chest, P3? DoubleWith, IReadOnlyList<StorySign> Signs,
    IReadOnlyList<string> Allow, IReadOnlyList<string> Deny);

public sealed partial class StoryColony
{
    private void PublishStorageSignRules(IStoryWorld world, IReadOnlyDictionary<P3, StorySign> observed)
    {
        var attached = observed.Values.Select(s => (Sign: s, Chest: StoryStorageKeywords.AttachedChest(world, s.At)))
            .Where(x => x.Chest != null).GroupBy(x => x.Chest!.Value)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Sign).ToArray());
        var storages = Storages.Where(s => s.Signs.Count > 0 || attached.ContainsKey(s.Chest)
            || s.DoubleWith is { } other && attached.ContainsKey(other)).ToList();
        // Ordinary labels also need fingerprints, including on chests the colony has never registered.
        foreach (var chest in attached.Keys)
            if (!storages.Any(s => s.Chest == chest || s.DoubleWith == chest))
                storages.Add(new StorageState { Chest = chest, DoubleWith = DoubleHalf(chest, world) });
        var rules = new List<StoryStorageSignRule>();
        foreach (var storage in storages)
        {
            var signs = storage.Signs.ToDictionary(s => s.At, s => new StorySign(s.At, s.Lines.ToArray()));
            foreach (var chest in new[] { (P3?)storage.Chest, storage.DoubleWith })
                if (chest is { } at && attached.TryGetValue(at, out var labels))
                    foreach (var sign in labels) signs[sign.At] = new StorySign(sign.At, sign.Lines.ToArray());
            var filter = storage.Signs.Count > 0 ? storage.EffectiveFilter : new StorageFilter();
            rules.Add(new(storage.Chest, storage.DoubleWith, signs.Values.ToArray(), filter.Allow.ToArray(), filter.Deny.ToArray()));
        }
        Host.PublishStorageSignRules(Id, world.Dimension, rules);
    }
}
