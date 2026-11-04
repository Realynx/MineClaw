using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using WPos = MineClaw.World.BlockPos;
using MPos = MineClaw.Agent.Memory.BlockPos;

namespace MineClaw.Bot.Memory;

// The bot reads (and, for demonstrations, writes) agent memory only through these small interfaces. The SQLite
// MemoryStore holds what players/the model taught (waypoints, structures, notes); the ObservationStore holds what the
// WorldIndexer saw (chests, generators, farms...) plus taught/llm world facts.

/// <summary>Resolves names the model uses for places ("home", "cobble_gen", "chests") to block positions.</summary>
public interface IPlaceResolver
{
    /// <summary>Waypoint or structure position by name; for structures, a key block whose label contains
    /// <paramref name="keyHint"/> wins over the structure's corner. Null if unknown.</summary>
    WPos? Resolve(string name, string? keyHint = null);

    /// <summary>All chest-like positions recorded for a structure (key blocks + corners), for organize_chest.</summary>
    IReadOnlyList<WPos> Positions(string name);
}

/// <summary>
/// Named places stored in the OTHER dimension (overworld vs nether), for goto's portal planning. <see cref="IPlaceResolver.Resolve"/>
/// only answers in the bot's current dimension, so nothing ever walks to nether coordinates in the overworld.
/// </summary>
public interface ICrossDimensionPlaces
{
    /// <summary>A waypoint/structure by name that lives in another dimension than the bot's, or null.</summary>
    (WPos Pos, int Dim)? ResolveElsewhere(string name);
    /// <summary>Linked portal pairs (null = none known).</summary>
    PortalLinks? Portals { get; }
}

/// <summary>Boxes the bot must never break into (player builds recorded as structures).</summary>
public interface IProtectedRegionSource
{
    IReadOnlyList<(WPos Min, WPos Max)> ProtectedRegions();

    /// <summary>The same boxes with what they are ("main house"), for refusal texts. Default: unlabelled.</summary>
    IReadOnlyList<(WPos Min, WPos Max, string? Label)> LabeledRegions() =>
        ProtectedRegions().Select(r => (r.Min, r.Max, (string?)null)).ToList();

    /// <summary>The same boxes with who made them and the design they record (the editing context: a saved design yields to
    /// requested work on it, the bot's own marks to requested work). Default: labelled boxes of unknown origin (never yield).</summary>
    IReadOnlyList<ProtectedBox> Boxes() => LabeledRegions().Select(r => new ProtectedBox(r.Min, r.Max, r.Label)).ToList();
}

/// <summary>A protected box: a remembered structure (<paramref name="Structure"/>: its <paramref name="Owner"/> is who taught or
/// marked it, null = the bot itself) or a taught world fact, and the design it records ("captured as design #11", "built from
/// design #4"), if any.</summary>
public sealed record ProtectedBox(WPos Min, WPos Max, string? Label, string? Owner = null, long? Design = null, bool Structure = false);

/// <summary>The structure note that links a design: "Fox's build, captured as design #11" / "built from design #4 for Fox: …".</summary>
public static partial class DesignLinks
{
    [GeneratedRegex(@"(?:captured as|built from) design #(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRx();

    public static long? Of(string? note) =>
        note is { } n && LinkRx().Match(n) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
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

/// <summary>
/// Every adapter over the Agent's SQLite <see cref="MemoryStore"/> (taught structures/waypoints/notes) and, when given,
/// its <see cref="ObservationStore"/> (indexed world: nearest chest/generator..., taught/llm world facts).
/// </summary>
public sealed partial class MemoryStoreAdapter(MemoryStore memory, ObservationStore? observations = null, Func<WPos?>? position = null)
    : IPlaceResolver, IProtectedRegionSource, IReservedItemSource, IStructureMemory, ICrossDimensionPlaces
{
    private int Dim => memory.CurrentDimension;
    public PortalLinks? Portals => observations?.Portals;

    public (WPos Pos, int Dim)? ResolveElsewhere(string name)
    {
        int here = Dim;
        if (memory.GetWaypoint(name) is { } w) return w.Dimension != here ? (new WPos(w.X, w.Y, w.Z), w.Dimension) : null;
        if (memory.GetStructure(name) is { } s && s.Dimension != here)
        {
            if (s.Min is { } m) return (ToW(m), s.Dimension);
            if (s.KeyBlocks.Count > 0) return (new WPos(s.KeyBlocks[0].X, s.KeyBlocks[0].Y, s.KeyBlocks[0].Z), s.Dimension);
        }
        return null;
    }

    /// <summary>Structure kinds that are not player builds worth protecting as a whole box.</summary>
    private static readonly HashSet<string> Unprotected = new(StringComparer.OrdinalIgnoreCase) { StructureKinds.Unknown };

    public WPos? Resolve(string name, string? keyHint = null)
    {
        // Only places in the bot's dimension: a nether waypoint's coordinates mean nothing here (goto plans portals instead).
        if (memory.GetWaypoint(name) is { } w) return w.Dimension == Dim ? new WPos(w.X, w.Y, w.Z) : null;
        if (memory.GetStructure(name) is { } s && s.Dimension == Dim && StructureSpot(s, keyHint) is { } spot) return spot;
        // Indexed world: "cobble_gen", "chest", "furnace", "farm"... → the nearest one.
        if (observations == null || IndexKinds.FromAlias(name) is not { } kind) return null;
        var here = position?.Invoke() ?? new WPos(0, 64, 0);
        var row = observations.Nearest(kind, here.X, here.Y, here.Z, 1, 512).Select(t => t.Row).FirstOrDefault();
        return row == null ? null : RowPosition(row, keyHint);
    }

    /// <summary>A saved structure's spot: the key block the hint names, else its first key block (no box), else its min corner.</summary>
    private static WPos? StructureSpot(Structure s, string? keyHint)
    {
        if (keyHint != null && s.KeyBlocks.FirstOrDefault(k => k.Label.Contains(keyHint, StringComparison.OrdinalIgnoreCase)) is { } kb)
            return new WPos(kb.X, kb.Y, kb.Z);
        if (s.KeyBlocks.Count > 0 && s.Min == null) return new WPos(s.KeyBlocks[0].X, s.KeyBlocks[0].Y, s.KeyBlocks[0].Z);
        return s.Min is { } m ? ToW(m) : null;
    }

    /// <summary>Best block of an observation for a purpose: generator dig cell (hint mine/output) or stand spot (hint stand).</summary>
    public static WPos? RowPosition(ObservationRow row, string? keyHint)
    {
        if (row.FieldsJson is { } json && FieldPosition(json, PositionField(row, keyHint)) is { } fromFields) return fromFields;
        if (Locations.ParseCoords(row.Key) is { } k) return k;
        return row.Bounds is { } b ? new WPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ)) : null;
    }

    /// <summary>"stand" for a stand hint, "dig" for a mine/output/dig hint (and a generator by default), else none.</summary>
    private static string? PositionField(ObservationRow row, string? keyHint) => keyHint?.ToLowerInvariant() switch
    {
        "stand" => "stand",
        "mine" or "output" or "dig" => "dig",
        _ => row.Kind == IndexKinds.Generator ? "dig" : null,
    };

    /// <summary>The x,y,z the row's fields hold under <paramref name="field"/> (the first of a list), or null.</summary>
    private static WPos? FieldPosition(string json, string? field)
    {
        if (field == null) return null;
        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty(field, out var v)) return null;
            var arr = v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 && v[0].ValueKind == JsonValueKind.Array ? v[0] : v;
            return arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 3 ? new WPos(arr[0].GetInt32(), arr[1].GetInt32(), arr[2].GetInt32()) : null;
        }
        catch (JsonException) { return null; }
    }

    public IReadOnlyList<WPos> Positions(string name)
    {
        var s = memory.GetStructure(name);
        if (s != null && s.Dimension != Dim) return [];
        if (s == null)
        {
            if (NearbyChests(name) is { Count: > 0 } chests) return chests;
            return Resolve(name) is { } p ? [p] : [];
        }
        var list = s.KeyBlocks.Select(k => new WPos(k.X, k.Y, k.Z)).ToList();
        if (list.Count == 0 && s.Min != null) list.Add(ToW(s.Min));
        return list;
    }

    /// <summary>"chest(s)": the indexed chests near me (up to 8 within 24), or null.</summary>
    private List<WPos>? NearbyChests(string name)
    {
        if (observations == null || IndexKinds.FromAlias(name) is not IndexKinds.Chest) return null;
        var here = position?.Invoke() ?? new WPos(0, 64, 0);
        return observations.Nearest(IndexKinds.Chest, here.X, here.Y, here.Z, 8, 24).Select(t => RowPosition(t.Row, null))
            .Where(p => p != null).Select(p => p!.Value).ToList();
    }

    /// <summary>
    /// Taught structures (memory) plus taught/llm world facts with a box, or any fact marked "protected". Auto-indexed
    /// things are protected per block by the indexer's own fast predicate, so they aren't repeated here.
    /// </summary>
    public IReadOnlyList<(WPos Min, WPos Max)> ProtectedRegions() => LabeledRegions().Select(r => (r.Min, r.Max)).ToList();

    /// <summary>Taught/marked structures (by name) and taught/llm facts with a box (by their key), for refusal texts.</summary>
    public IReadOnlyList<(WPos Min, WPos Max, string? Label)> LabeledRegions() => Boxes().Select(b => (b.Min, b.Max, b.Label)).ToList();

    /// <summary>The boxes with their maker (a structure's taught_by; null = the bot's own mark) and the design they link.</summary>
    public IReadOnlyList<ProtectedBox> Boxes()
    {
        int dim = Dim;
        var list = memory.Read(d => d.Structures
            .Where(s => s.Min != null && !Unprotected.Contains(s.Kind) && s.Dimension == dim)
            .Select(s => new ProtectedBox(ToW(s.Min!), ToW(s.Max ?? s.Min!), s.Name, s.TaughtBy, DesignLinks.Of(s.Note), Structure: true))
            .ToList());
        if (observations == null) return list;
        var here = position?.Invoke() ?? new WPos(0, 64, 0);
        foreach (var (row, _) in observations.Nearest(null, here.X, here.Y, here.Z, 300, 512))
        {
            if (row.Bounds is not { } b || row.Source == ObservationSource.Auto) continue;
            bool flagged = row.FieldsJson?.Contains("\"protected\":true", StringComparison.OrdinalIgnoreCase) == true;
            if (!flagged && IndexKinds.NeverBoxProtected.Contains(row.Kind)) continue;
            list.Add(new ProtectedBox(new WPos((int)Math.Floor(b.MinX), (int)Math.Floor(b.MinY), (int)Math.Floor(b.MinZ)),
                new WPos((int)Math.Floor(b.MaxX), (int)Math.Floor(b.MaxY), (int)Math.Floor(b.MaxZ)), row.Key));
        }
        return list;
    }

    [GeneratedRegex(@"(?:don'?t|do not|never)\s+(?:use|eat|deposit|give away|spend|craft with|smelt)\s+(?:the\s+|my\s+|our\s+|any\s+)?([a-z_ ]+?)(?:[.,!;]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DontUse();

    [GeneratedRegex(@"^(?:reserve|keep|save)\s*:?\s+(?:the\s+|my\s+|our\s+)?([a-z_ ,]+?)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReserveLine();

    private static readonly HashSet<string> ReserveSubjects = new(StringComparer.Ordinal) { "reserved", "reserve", "keep", "reserved_items", "reserved items" };

    /// <summary>Items memory says to keep: notes with subject "reserved"/"keep" (comma list), or "don't use the X" / "reserve X".</summary>
    public IReadOnlyCollection<string> ReservedItemNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        memory.Read(d =>
        {
            foreach (var n in d.Notes)
            {
                if (ReserveSubjects.Contains(n.Subject.Trim().ToLowerInvariant()))
                    foreach (var part in n.Text.Split(',', ';')) Add(part);
                if (DontUse().Match(n.Text) is { Success: true } m) Add(m.Groups[1].Value);
                if (ReserveLine().Match(n.Text.Trim()) is { Success: true } r) foreach (var part in r.Groups[1].Value.Split(',')) Add(part);
            }
            return 0;
        });
        return names;

        void Add(string raw)
        {
            var s = raw.Trim().ToLowerInvariant();
            if (s.Length == 0) return;
            if (MineClaw.Data.Items.ByName(s) != null) { names.Add(s); return; }
            if (s.EndsWith('s') && MineClaw.Data.Items.ByName(s[..^1]) != null) names.Add(s[..^1]);
        }
    }

    public (WPos Min, WPos Max)? Area(string name)
    {
        var s = memory.GetStructure(name);
        if (s?.Min == null || s.Dimension != Dim) return null;
        return (ToW(s.Min), ToW(s.Max ?? s.Min));
    }

    /// <summary>Header line before each later demo's steps ("demo 2 by PoofimaFox (variant):").</summary>
    public const string DemoHeader = "demo ";
    private const int MaxStructureSteps = 30;

    private static bool ContainsRun(List<string> have, IReadOnlyList<string> run)
    {
        if (run.Count == 0) return true;
        for (int i = 0; i + run.Count <= have.Count; i++)
        {
            int k = 0;
            while (k < run.Count && have[i + k] == run[k]) k++;
            if (k == run.Count) return true;
        }
        return false;
    }

    public void SaveSteps(string name, IReadOnlyList<string> steps, string taughtBy)
    {
        var s = memory.UpsertStructure(name, s =>
        {
            // A second demo of the same structure (the other gen cell) is a variant: appended, never wiping the first.
            if (s.Steps.Count == 0) s.Steps.AddRange(steps);
            else if (!ContainsRun(s.Steps, steps))
            {
                int n = 1 + s.Steps.Count(x => x.StartsWith(DemoHeader, StringComparison.Ordinal)) + 1;
                s.Steps.Add($"{DemoHeader}{n} by {taughtBy} (variant):");
                s.Steps.AddRange(steps);
                // Bounded: the oldest whole demo goes first.
                while (s.Steps.Count > MaxStructureSteps)
                {
                    int next = s.Steps.FindIndex(1, x => x.StartsWith(DemoHeader, StringComparison.Ordinal));
                    if (next <= 0) { s.Steps.RemoveRange(0, s.Steps.Count - MaxStructureSteps); break; }
                    s.Steps.RemoveRange(0, next);
                }
            }
            s.TaughtBy = taughtBy;
            s.Confidence = Math.Max(s.Confidence, 0.7);
        });
        // Mirror the taught structure into world observations: findable by where_is, protected, and never overwritten by the indexer.
        if (observations == null || s.Min == null) return;
        var mx = s.Max ?? s.Min;
        try
        {
            observations.Upsert(new Observation("structure", name.Trim().ToLowerInvariant(),
                $"{name} ({s.Kind.Replace('_', ' ')}) taught by {taughtBy}: " + string.Join("; ", steps.Take(4)))
            {
                Bounds = Box.Of(s.Min.X, s.Min.Y, s.Min.Z, mx.X, mx.Y, mx.Z),
                FieldsJson = JsonSerializer.Serialize(new { @protected = true, steps = steps.Count, by = taughtBy }),
                Source = ObservationSource.Taught,
                Confidence = 0.8,
                Dimension = s.Dimension,
            });
        }
        catch { /* memory steps are saved; the mirror is best effort */ }
    }

    /// <summary>True when a box overlaps a taught structure (so the indexer doesn't call it "unknown").</summary>
    public bool IsKnownArea(WPos min, WPos max) { int dim = Dim; return memory.Read(d => d.Structures.Any(s =>
        s.Min != null && s.Dimension == dim && Overlaps(ToW(s.Min), ToW(s.Max ?? s.Min), min, max, 1))); }

    /// <summary>Name of the taught structure at a position (how-to evidence), or null.</summary>
    public string? TaughtStructureAt(WPos p) { int dim = Dim; return memory.Read(d => d.Structures.FirstOrDefault(s =>
        s.Min != null && s.TaughtBy != null && s.Dimension == dim && Overlaps(ToW(s.Min), ToW(s.Max ?? s.Min), p, p, 2))?.Name); }

    private static bool Overlaps(WPos a0, WPos a1, WPos b0, WPos b1, int pad) =>
        Math.Min(a0.X, a1.X) - pad <= Math.Max(b0.X, b1.X) && Math.Max(a0.X, a1.X) + pad >= Math.Min(b0.X, b1.X) &&
        Math.Min(a0.Y, a1.Y) - pad <= Math.Max(b0.Y, b1.Y) && Math.Max(a0.Y, a1.Y) + pad >= Math.Min(b0.Y, b1.Y) &&
        Math.Min(a0.Z, a1.Z) - pad <= Math.Max(b0.Z, b1.Z) && Math.Max(a0.Z, a1.Z) + pad >= Math.Min(b0.Z, b1.Z);

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
