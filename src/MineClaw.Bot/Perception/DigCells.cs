using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Agent.Memory;
using BlockPos = MineClaw.World.BlockPos;
using MineClaw.World;
using MPos = MineClaw.Agent.Memory.BlockPos;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Perception;

/// <summary>
/// Cobblestone-generator dig cells the bot must be allowed to mine: taught ones (a player punched it, stood there and said
/// "here", or a structure key block labelled dig/mine/cell/output) plus the world indexer's regen-confirmed cells. A dig cell
/// is never protected, whatever box or wall protection covers it. Lock-free reads (pathfinder inner loop).
/// </summary>
public sealed partial class DigCells
{
    private readonly MemoryStore? _memory;
    private readonly Func<WPos, bool>? _confirmed;
    private readonly ConcurrentDictionary<(int Dim, WPos Pos), byte> _taught = new();
    private volatile HashSet<(int Dim, WPos Pos)> _fromMemory = new();
    private DateTimeOffset _lastLoad = DateTimeOffset.MinValue;

    [GeneratedRegex(@"\b(?:dig|mine|mining|cell|output|regen)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DigLabel();

    /// <param name="confirmed">The indexer's "this cell refilled after a dig" check (null = none).</param>
    public DigCells(MemoryStore? memory, Func<WPos, bool>? confirmed = null)
    {
        _memory = memory;
        _confirmed = confirmed;
        Reload();
    }

    /// <summary>Current dimension (taught cells are per dimension).</summary>
    public Func<int>? Dimension { get; set; }

    private int Dim => Dimension?.Invoke() ?? 0;

    public bool IsDigCell(WPos p)
    {
        int dim = Dim;
        if (_taught.ContainsKey((dim, p))) return true;
        MaybeReload();
        if (_fromMemory.Contains((dim, p))) return true;
        return _confirmed?.Invoke(p) == true;
    }

    /// <summary>All taught cells (this dimension).</summary>
    public IReadOnlyList<WPos> Taught()
    {
        int dim = Dim;
        MaybeReload();
        return _taught.Keys.Where(k => k.Dim == dim).Select(k => k.Pos).Concat(_fromMemory.Where(k => k.Dim == dim).Select(k => k.Pos))
            .Distinct().ToList();
    }

    /// <summary>
    /// A player taught this cell (punch / "here" / demo): never protected from now on; also saved as a "dig" key block on the
    /// nearest generator structure in memory (within 6 blocks), so it survives restarts and reaches clones (shared db).
    /// Returns false when it was already known.
    /// </summary>
    public bool Register(WPos p, string? by = null)
    {
        int dim = Dim;
        bool added = _taught.TryAdd((dim, p), 0);
        if (_memory == null) return added;
        try
        {
            var s = _memory.Read(d => d.Structures.Where(x => x.Dimension == dim && x.Min != null
                    && (x.Kind == StructureKinds.CobbleGenerator || x.Name.Contains("gen", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => Dist(x, p)).FirstOrDefault());
            if (s != null && Dist(s, p) <= 6 && !s.KeyBlocks.Any(k => k.X == p.X && k.Y == p.Y && k.Z == p.Z))
            {
                _memory.UpsertStructure(s.Name, st => st.KeyBlocks.Add(new KeyBlock { Label = by != null ? $"dig (taught by {by})" : "dig", X = p.X, Y = p.Y, Z = p.Z }));
                _lastLoad = DateTimeOffset.MinValue;
            }
        }
        catch (Exception) { /* memory busy: the in-process set still has it */ }
        return added;
    }

    private static double Dist(Structure s, WPos p)
    {
        var a = s.Min!;
        var b = s.Max ?? a;
        double dx = Math.Max(0, Math.Max(Math.Min(a.X, b.X) - p.X, p.X - Math.Max(a.X, b.X)));
        double dy = Math.Max(0, Math.Max(Math.Min(a.Y, b.Y) - p.Y, p.Y - Math.Max(a.Y, b.Y)));
        double dz = Math.Max(0, Math.Max(Math.Min(a.Z, b.Z) - p.Z, p.Z - Math.Max(a.Z, b.Z)));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Re-read memory's key blocks on the next check (after a structure was edited).</summary>
    public void Invalidate() => _lastLoad = DateTimeOffset.MinValue;

    private void MaybeReload()
    {
        if (DateTimeOffset.UtcNow - _lastLoad < TimeSpan.FromSeconds(5)) return;
        Reload();
    }

    private void Reload()
    {
        _lastLoad = DateTimeOffset.UtcNow;
        if (_memory == null) return;
        try
        {
            _fromMemory = _memory.Read(d => d.Structures
                .SelectMany(s => s.KeyBlocks.Where(k => DigLabel().IsMatch(k.Label)).Select(k => (s.Dimension, new WPos(k.X, k.Y, k.Z))))
                .ToHashSet());
        }
        catch (Exception) { /* keep the last snapshot */ }
    }
}
