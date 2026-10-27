using MineClaw.Agent.Memory;
using WPos = MineClaw.World.BlockPos;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Memory;

// The bot reads (and, for demonstrations, writes) agent memory only through these small interfaces so that the
// Agent's in-progress memory rework (SQLite RAG, world-observation ingestion) can be wired in by replacing
// MemoryStoreAdapter alone.
// TODO(rag-rework): once the Agent exposes its new memory/world-observation API, implement these over it
// (and feed demonstrations / placed stations into its observation ingestion instead of UpsertStructure).

/// <summary>Resolves names the model uses for places ("home", "cobble_gen", "chests") to block positions.</summary>
public interface IPlaceResolver
{
    /// <summary>Waypoint or structure position by name; for structures, a key block whose label contains
    /// <paramref name="keyHint"/> wins over the structure's corner. Null if unknown.</summary>
    WPos? Resolve(string name, string? keyHint = null);

    /// <summary>All chest-like positions recorded for a structure (key blocks + corners), for organize_chest.</summary>
    IReadOnlyList<WPos> Positions(string name);
}

/// <summary>Boxes the bot must never break into (player builds recorded as structures).</summary>
public interface IProtectedRegionSource
{
    IReadOnlyList<(WPos Min, WPos Max)> ProtectedRegions();
}

/// <summary>Items memory says to keep (e.g. "don't use the diamonds"). Config covers the static part.</summary>
public interface IReservedItemSource
{
    IReadOnlyCollection<string> ReservedItemNames();
}

/// <summary>Where learned how-to steps go.</summary>
public interface IStructureMemory
{
    /// <summary>Known extent of a structure (for limiting a demonstration's block events), or null.</summary>
    (WPos Min, WPos Max)? Area(string name);
    void SaveSteps(string name, IReadOnlyList<string> steps, string taughtBy);
}

/// <summary>Current implementation of every adapter over the Agent's JSON <see cref="MemoryStore"/>.</summary>
public sealed class MemoryStoreAdapter(MemoryStore memory) : IPlaceResolver, IProtectedRegionSource, IReservedItemSource, IStructureMemory
{
    /// <summary>Structure kinds that are not player builds worth protecting as a whole box.</summary>
    private static readonly HashSet<string> Unprotected = new(StringComparer.OrdinalIgnoreCase) { StructureKinds.Unknown };

    public WPos? Resolve(string name, string? keyHint = null)
    {
        if (memory.GetWaypoint(name) is { } w) return new WPos(w.X, w.Y, w.Z);
        if (memory.GetStructure(name) is not { } s) return null;
        if (keyHint != null && s.KeyBlocks.FirstOrDefault(k => k.Label.Contains(keyHint, StringComparison.OrdinalIgnoreCase)) is { } kb)
            return new WPos(kb.X, kb.Y, kb.Z);
        if (s.KeyBlocks.Count > 0 && s.Min == null) return new WPos(s.KeyBlocks[0].X, s.KeyBlocks[0].Y, s.KeyBlocks[0].Z);
        return s.Min is { } m ? ToW(m) : null;
    }

    public IReadOnlyList<WPos> Positions(string name)
    {
        var s = memory.GetStructure(name);
        if (s == null) return Resolve(name) is { } p ? [p] : [];
        var list = s.KeyBlocks.Select(k => new WPos(k.X, k.Y, k.Z)).ToList();
        if (list.Count == 0 && s.Min != null) list.Add(ToW(s.Min));
        return list;
    }

    public IReadOnlyList<(WPos Min, WPos Max)> ProtectedRegions() =>
        memory.Read(d => d.Structures
            .Where(s => s.Min != null && !Unprotected.Contains(s.Kind))
            .Select(s => (ToW(s.Min!), ToW(s.Max ?? s.Min!)))
            .ToList());

    // TODO(rag-rework): reserved items have no home in MemoryStore yet; config (Bot:ReservedItems) is the only source.
    public IReadOnlyCollection<string> ReservedItemNames() => [];

    public (WPos Min, WPos Max)? Area(string name)
    {
        var s = memory.GetStructure(name);
        if (s?.Min == null) return null;
        return (ToW(s.Min), ToW(s.Max ?? s.Min));
    }

    public void SaveSteps(string name, IReadOnlyList<string> steps, string taughtBy) =>
        memory.UpsertStructure(name, s =>
        {
            s.Steps.Clear();
            s.Steps.AddRange(steps);
            s.TaughtBy = taughtBy;
            s.Confidence = Math.Max(s.Confidence, 0.7);
        });

    private static WPos ToW(MPos p) => new(p.X, p.Y, p.Z);
}

/// <summary>Null object for tests / memory-less hosts.</summary>
public sealed class NoMemory : IPlaceResolver, IProtectedRegionSource, IReservedItemSource, IStructureMemory
{
    public static readonly NoMemory Instance = new();
    public WPos? Resolve(string name, string? keyHint = null) => null;
    public IReadOnlyList<WPos> Positions(string name) => [];
    public IReadOnlyList<(WPos Min, WPos Max)> ProtectedRegions() => [];
    public IReadOnlyCollection<string> ReservedItemNames() => [];
    public (WPos Min, WPos Max)? Area(string name) => null;
    public void SaveSteps(string name, IReadOnlyList<string> steps, string taughtBy) { }
}
