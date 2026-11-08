using System.Collections.Frozen;
using MineClaw.Bot.Bodies;
using MineClaw.Client;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Supervisor;

public sealed partial class StoryHost
{
    private readonly object _storageSignLock = new();
    private readonly Dictionary<(string Colony, int Dimension), (long Revision, Dictionary<BlockPos, StorageSignPolicy> Rules)> _storageSignSources = new();
    private long _storageSignRevision;
    private FrozenDictionary<(int Dimension, BlockPos Chest), StorageSignPolicy> _storageSignRules = FrozenDictionary<(int, BlockPos), StorageSignPolicy>.Empty;
    private BodyRegistry? _storageGuardBodies;

    public void PublishStorageSignRules(string colony, int dimension, IReadOnlyList<StoryStorageSignRule> rules)
    {
        var categories = StoryCategories.For(World!.Data);
        var snapshot = new Dictionary<BlockPos, StorageSignPolicy>();
        foreach (var rule in rules)
        {
            var filter = new StorageFilter { Allow = rule.Allow.ToList(), Deny = rule.Deny.ToList() };
            var chests = rule.DoubleWith is { } other ? new[] { Pos(rule.Chest), Pos(other) } : [Pos(rule.Chest)];
            var policy = new StorageSignPolicy(chests, rule.Signs.Select(s => (Pos(s.At), s.Lines)), (id, meta) => categories.Accepts(filter, id, meta));
            foreach (var chest in chests) snapshot[chest] = policy;
        }
        lock (_storageSignLock)
        {
            _storageSignSources[(colony, dimension)] = (++_storageSignRevision, snapshot);
            var merged = new Dictionary<(int, BlockPos), StorageSignPolicy>();
            foreach (var source in _storageSignSources.OrderBy(s => s.Value.Revision))
                foreach (var (chest, policy) in source.Value.Rules) merged[(source.Key.Dimension, chest)] = policy;
            Volatile.Write(ref _storageSignRules, merged.ToFrozenDictionary());
            AttachStorageSignRegistry();
        }
    }

    /// <summary>Main, existing bodies, and later spawns all share one lock-free snapshot lookup.</summary>
    public void AttachStorageSignGuards(MinecraftClient main)
    {
        main.StorageSignRules = StorageSignRuleAt;
        lock (_storageSignLock) AttachStorageSignRegistry();
    }

    private StorageSignPolicy? StorageSignRuleAt(int dimension, BlockPos at) =>
        Volatile.Read(ref _storageSignRules).GetValueOrDefault((dimension, at));

    private void AttachStorageSignBody(IBody body, bool added)
    {
        if (added && body is Body { Client: { } client }) client.StorageSignRules = StorageSignRuleAt;
    }

    private void AttachStorageSignRegistry()
    {
        if (_bodies() is not { } bodies || ReferenceEquals(bodies, _storageGuardBodies)) return;
        if (_storageGuardBodies is { } old) old.Changed -= AttachStorageSignBody;
        _storageGuardBodies = bodies;
        bodies.Changed += AttachStorageSignBody;
        foreach (var body in bodies.All) AttachStorageSignBody(body, true);
    }

    private static BlockPos Pos(P3 p) => new(p.X, p.Y, p.Z);
}
