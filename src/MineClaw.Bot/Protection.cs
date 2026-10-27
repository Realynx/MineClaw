using System.Text.RegularExpressions;
using MineClaw.Bot.Memory;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot;

/// <summary>
/// Builds the client's <c>IsProtected</c> predicate and <c>ReservedItems</c> set from config plus memory. Memory is
/// snapshotted (refreshed at most every <see cref="BotOptions.MemoryRefreshSeconds"/>) because the predicate runs
/// inside the pathfinder's inner loop.
/// </summary>
public sealed partial class Protection
{
    private readonly HashSet<int> _blockIds;
    private readonly List<(BlockPos Min, BlockPos Max)> _configRegions;
    private readonly HashSet<int> _configReserved;
    private readonly IProtectedRegionSource _regions;
    private readonly IReservedItemSource _reserved;
    private readonly TimeSpan _refresh;
    private readonly TimeProvider _time;
    private volatile (BlockPos Min, BlockPos Max)[] _memoryRegions = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private readonly object _lock = new();

    public Protection(BotOptions options, IProtectedRegionSource regions, IReservedItemSource reserved, TimeProvider? time = null)
    {
        _regions = regions;
        _reserved = reserved;
        _time = time ?? TimeProvider.System;
        _refresh = TimeSpan.FromSeconds(Math.Max(1, options.MemoryRefreshSeconds));
        _blockIds = new HashSet<int>();
        foreach (var n in options.ProtectedBlocks)
        {
            if (Blocks.ByName(n) is { } b) _blockIds.Add(b.Id);
            else Unknown.Add("block " + n);
        }
        if (_blockIds.Contains(Blocks.Furnace)) _blockIds.Add(Blocks.LitFurnace);
        _configRegions = options.ProtectedRegions.Select(ParseRegion).Where(r => r != null).Select(r => r!.Value).ToList();
        _configReserved = ResolveItems(options.ReservedItems);
    }

    /// <summary>Config names that didn't resolve (logged by the host).</summary>
    public List<string> Unknown { get; } = new();

    public IReadOnlySet<int> ProtectedBlockIds => _blockIds;

    public bool IsProtected(BlockPos p, int blockId)
    {
        if (_blockIds.Contains(blockId)) return true;
        foreach (var r in _configRegions) if (Inside(p, r)) return true;
        MaybeRefresh();
        foreach (var r in _memoryRegions) if (Inside(p, r)) return true;
        return false;
    }

    /// <summary>Config + memory reserved items (call after memory changes; cheap).</summary>
    public IReadOnlySet<int> ReservedItems()
    {
        var set = new HashSet<int>(_configReserved);
        set.UnionWith(ResolveItems(_reserved.ReservedItemNames()));
        return set;
    }

    /// <summary>Forces the next <see cref="IsProtected"/> call to re-read memory.</summary>
    public void Invalidate() { lock (_lock) _lastRefresh = DateTimeOffset.MinValue; }

    private void MaybeRefresh()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (now - _lastRefresh < _refresh) return;
            _lastRefresh = now;
        }
        try
        {
            _memoryRegions = _regions.ProtectedRegions()
                .Select(r => (Min(r.Min, r.Max), Max(r.Min, r.Max))).ToArray();
        }
        catch { /* keep the last snapshot */ }
    }

    private HashSet<int> ResolveItems(IEnumerable<string> names)
    {
        var set = new HashSet<int>();
        foreach (var n in names)
        {
            if (Items.ByName(n) is { } i) set.Add(i.Id);
            else lock (Unknown) Unknown.Add("item " + n);
        }
        return set;
    }

    private static bool Inside(BlockPos p, (BlockPos Min, BlockPos Max) r) =>
        p.X >= r.Min.X && p.X <= r.Max.X && p.Y >= r.Min.Y && p.Y <= r.Max.Y && p.Z >= r.Min.Z && p.Z <= r.Max.Z;

    private static BlockPos Min(BlockPos a, BlockPos b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
    private static BlockPos Max(BlockPos a, BlockPos b) => new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

    public static (BlockPos Min, BlockPos Max)? ParseRegion(string s)
    {
        var parts = s.Split(':');
        if (parts.Length != 2 || Locations.ParseCoords(parts[0]) is not { } a || Locations.ParseCoords(parts[1]) is not { } b) return null;
        return (Min(a, b), Max(a, b));
    }
}

/// <summary>Coordinate parsing shared by tools ("3,65,-2", "3 65 -2", "x=3 y=65 z=-2").</summary>
public static partial class Locations
{
    [GeneratedRegex(@"^\s*\(?\s*(?:x\s*=\s*)?(-?\d+)\s*[, ]\s*(?:y\s*=\s*)?(-?\d+)\s*[, ]\s*(?:z\s*=\s*)?(-?\d+)\s*\)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CoordsRx();

    public static BlockPos? ParseCoords(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = CoordsRx().Match(s);
        return m.Success ? new BlockPos(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
    }

    public static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";

    public static string Fmt(Vec3d v) => $"{(int)Math.Floor(v.X)},{(int)Math.Floor(v.Y + 0.001)},{(int)Math.Floor(v.Z)}";
}
