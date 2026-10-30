using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// A remembered excavation: a pattern site (the rule, its anchor and how many repeats are dug: "spiral stair" = column 3, step
/// 0,-1,0, turn -90 about the core, 30 repeats from 153,60,275), or explicit cells (a room off the shaft, a strip mine). Its air
/// is a must-be-air set (<see cref="KeepAirLog"/>) for as long as it is remembered, so nothing bridges, seals or boxes into it,
/// and it can be extended ("extend the stairs to y12") or re-applied elsewhere ("dig the same shaft over there"). A site may
/// point at the design it was learned from (<see cref="Design"/>): the design carries the rule without the blueprint store
/// changing shape.
/// </summary>
public sealed record DigSite
{
    public required string Name { get; init; }
    /// <summary>pattern | room | strip | box.</summary>
    public string Kind { get; init; } = "box";
    public int Dimension { get; init; }
    /// <summary>The site this one hangs off (a room's shaft, a strip mine's room), or null.</summary>
    public string? Parent { get; init; }
    /// <summary>The pattern rule (<see cref="DigPattern.Rule"/>), or null.</summary>
    public string? Rule { get; init; }
    public BlockPos Anchor { get; init; }
    /// <summary>Pattern repeats dug so far.</summary>
    public int Repeats { get; init; }
    /// <summary>Explicit air cells (rooms, corridors, a pattern's doorways).</summary>
    public IReadOnlyList<BlockPos> Cells { get; init; } = [];
    /// <summary>The design this site was learned from (its id), or null.</summary>
    public int? Design { get; init; }
    /// <summary>One line: what was found or decided ("iron ore 5, coal ore 12 at y12"; "door east, gap 1").</summary>
    public string? Note { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public DigPattern? Pattern => Rule is null ? null : DigPattern.Parse(Rule, Anchor, out _);

    /// <summary>Every must-be-air cell: the pattern's dug repeats plus the explicit cells.</summary>
    public HashSet<BlockPos> Air()
    {
        var set = Pattern is { } p && Repeats > 0 ? p.Cells(0, Repeats) : new HashSet<BlockPos>();
        set.UnionWith(Cells);
        return set;
    }

    /// <summary>"spiral stair (pattern column 3 step 0,-1,0 turn -90 pivot 0.5,0.5, 30 repeats from 153,60,275 down to y29)".</summary>
    public string Describe()
    {
        var air = Air();
        string span = air.Count == 0 ? "nothing dug" : $"y{air.Min(c => c.Y)}..{air.Max(c => c.Y)}, {air.Count} cells";
        return Kind == "pattern" && Rule != null
            ? $"{Name} (pattern {Rule}, {Repeats} repeats from {Anchor.X},{Anchor.Y},{Anchor.Z}; {span})"
            : $"{Name} ({Kind}{(Parent is null ? "" : $" off {Parent}")}; {span}{(Note is null ? "" : $"; {Note}")})";
    }
}

/// <summary>Remembered excavations by name (the Bot layer persists them in the shared db; skills and tests use the in-memory one).</summary>
public interface IDigSites
{
    /// <summary>By name, case-insensitive ("spiral stair", "Spiral_Stair"), or by the design id/name it was learned from.</summary>
    DigSite? Find(string name);
    IReadOnlyList<DigSite> All();
    void Save(DigSite site);
    bool Delete(string name);
}

public sealed class InMemoryDigSites : IDigSites
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DigSite> _sites = new(StringComparer.OrdinalIgnoreCase);

    public DigSite? Find(string name)
    {
        var key = DigSites.Key(name);
        lock (_lock)
            return _sites.TryGetValue(key, out var s) ? s
                : _sites.Values.FirstOrDefault(v => v.Design is { } d && (key == d.ToString() || key == "#" + d));
    }

    public IReadOnlyList<DigSite> All() { lock (_lock) return _sites.Values.ToList(); }
    public void Save(DigSite site) { lock (_lock) _sites[DigSites.Key(site.Name)] = site; }
    public bool Delete(string name) { lock (_lock) return _sites.Remove(DigSites.Key(name)); }
}

public static class DigSites
{
    /// <summary>Lookup key of a site name: trimmed, lower case, underscores and dashes as spaces ("Spiral_Stair" = "spiral stair").</summary>
    public static string Key(string name) => string.Join(' ', name.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Registers every remembered site's air as must-be-air cells (startup; a clone process does the same).</summary>
    public static int Apply(IDigSites sites, KeepAirLog keep)
    {
        int n = 0;
        foreach (var s in sites.All())
        {
            keep.Set(Owner(s), s.Dimension, s.Air());
            n++;
        }
        return n;
    }

    /// <summary>The <see cref="KeepAirLog"/> owner of a site.</summary>
    public static string Owner(DigSite s) => s.Name;
}
