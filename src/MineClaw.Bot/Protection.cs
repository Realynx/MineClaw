using System.Text.RegularExpressions;
using MineClaw.Bot.Memory;
using MineClaw.Data;
using MineClaw.World;
using MineClaw.World.Claims;
using MineClaw.Bot.WorldIndex;

namespace MineClaw.Bot;

/// <summary>
/// The protection policy (ARCHITECTURE.md "Protection policy"): one place that decides, for every (block, id), whether the
/// bot may break it, why not, and how a player unlocks it. Builds the client's <c>IsProtected</c> predicate (fast, the
/// pathfinder's inner loop asks), <c>ExplainProtection</c> (refusal text + telemetry) and <c>SoftOverride</c>.
/// <list type="number">
/// <item>Blocks any hive bot placed itself (<see cref="PlacedBlockLog"/>, shared through the db) override every SOFT source.</item>
/// <item>A trusted player's explicit permit (<see cref="ProtectionMarks"/>) overrides every SOFT source; containers only for
/// an exact permit (pointed or by coordinates).</item>
/// <item>HARD: a player's "never break" mark never yields to a command (a trusted "you can break X" unmarks it). Void and lava
/// safety are hard too and live in the client's dig vetoes, untouched by this policy.</item>
/// <item>SOFT sources: config blocks (stations/containers, saplings, farmland) and boxes, memory structures (taught or
/// marked: "main house"), the world index (unknown builds, generator walls, farms, tree soil, portals, signs, stations),
/// other hive bots' leases. Generator dig cells are never protected.</item>
/// </list>
/// Memory is snapshotted (refreshed at most every <see cref="BotOptions.MemoryRefreshSeconds"/>).
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
    private volatile (BlockPos Min, BlockPos Max, string? Label)[] _memoryRegions = [];
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
        RepeatWindow = TimeSpan.FromMinutes(Math.Max(1, options.Protection.RepeatRefusalMinutes));
    }

    /// <summary>Config names that didn't resolve (logged by the host).</summary>
    public List<string> Unknown { get; } = new();

    public IReadOnlySet<int> ProtectedBlockIds => _blockIds;

    // ---- sources and overrides wired by the host (all optional)

    /// <summary>World index: which indexed thing protects a cell (kind), or null.</summary>
    public Func<BlockPos, string?>? IndexerKind { get; set; }
    /// <summary>Generator dig cells (taught or regen-confirmed): never protected by anything soft.</summary>
    public Func<BlockPos, bool>? IsDigCell { get; set; }
    /// <summary>Hive leases: a block another bot holds is soft-protected while it works there.</summary>
    public IClaimBoard? Claims { get; set; }
    /// <summary>Rule 1: blocks hive bots placed themselves.</summary>
    public PlacedBlockLog? Placed { get; set; }
    /// <summary>Rule 3/4: trusted permits and "never break" marks.</summary>
    public ProtectionMarks? Marks { get; set; }
    /// <summary>The bot's dimension (default overworld).</summary>
    public Func<int>? Dimension { get; set; }
    /// <summary>Refusals counted by reason (rule 6).</summary>
    public ProtectionTelemetry Telemetry { get; set; } = new();

    private int Dim => Dimension?.Invoke() ?? 0;

    /// <summary>Containers: soft as blocks, but they may hold items, so only an exact permit (pointed/coords) opens them.</summary>
    public static bool IsContainer(int id) => id is Blocks.Chest or Blocks.Furnace or Blocks.LitFurnace or Blocks.Dispenser;

    /// <summary>The fast predicate: may the bot's own choices break this block? (Hard marks, then soft sources minus overrides.)</summary>
    public bool IsProtected(BlockPos p, int blockId)
    {
        int dim = Dim;
        if (Marks is { HasNever: true } marks && marks.NeverAt(p, dim) != null) return true;
        if (!AnySoft(p, blockId, dim)) return false;
        if (IsDigCell?.Invoke(p) == true) return false;
        return !Overridden(p, blockId, dim);
    }

    /// <summary>Rule 1/3 for this block: a hive bot placed it, or a trusted player permitted it (containers: exact permits only).</summary>
    public bool OverridesSoft(BlockPos p, int blockId) => Overridden(p, blockId, Dim);

    private bool Overridden(BlockPos p, int id, int dim)
    {
        bool container = IsContainer(id);
        if (!container && Placed?.IsPlaced(p, dim, id) == true) return true;
        return Marks is { HasPermits: true } m && m.PermitAt(p, dim, exactOnly: container) != null;
    }

    private bool AnySoft(BlockPos p, int id, int dim)
    {
        if (_blockIds.Contains(id)) return true;
        foreach (var r in _configRegions) if (Inside(p, r.Min, r.Max)) return true;
        MaybeRefresh();
        foreach (var r in _memoryRegions) if (Inside(p, r.Min, r.Max)) return true;
        if (IndexerKind?.Invoke(p) != null) return true;
        return Claims != null && !Claims.IsFree(ClaimKey.Block(p, dim));
    }

    /// <summary>The verdict without counting it (tests, diagnostics).</summary>
    public ProtectionVerdict Check(BlockPos p, int blockId)
    {
        int dim = Dim;
        if (Marks?.NeverAt(p, dim) is { } never)
            return new ProtectionVerdict(ProtectionLevel.Hard, "never_break",
                $"{never.By} marked {(string.IsNullOrWhiteSpace(never.Label) ? "it" : never.Label)} never-break");
        if (!AnySoft(p, blockId, dim) || IsDigCell?.Invoke(p) == true || Overridden(p, blockId, dim)) return ProtectionVerdict.None;
        return SoftReason(p, blockId, dim);
    }

    /// <summary>Why it's protected, counted by reason, flagged as a repeat when the same refusal was given recently (rule 5/6).</summary>
    public ProtectionVerdict Explain(BlockPos p, int blockId)
    {
        var v = Check(p, blockId);
        if (!v.Protected) return v;
        var now = _time.GetUtcNow();
        var key = (v.Reason, p.X, p.Z);
        bool repeat;
        lock (_refusals)
        {
            repeat = _refusals.TryGetValue(key, out var at) && now - at < RepeatWindow;
            _refusals[key] = now;
            if (_refusals.Count > 256)
                foreach (var k in _refusals.Where(kv => now - kv.Value > RepeatWindow).Select(kv => kv.Key).ToList()) _refusals.Remove(k);
            LastRefusal = (p, blockId, now);
        }
        Telemetry.Count(v.Reason, $"{Blocks.Get(blockId).Name} at {Locations.Fmt(p)}: {v.Why}");
        return v with { Repeat = repeat };
    }

    /// <summary>A repeat of the same refusal within this window says "already asked" instead of asking again.</summary>
    public TimeSpan RepeatWindow { get; set; }
    private readonly Dictionary<(string, int, int), DateTimeOffset> _refusals = new();

    /// <summary>The last refused block ("you can break it" right after a refusal unlocks it).</summary>
    public (BlockPos Pos, int Id, DateTimeOffset At)? LastRefusal { get; private set; }

    private ProtectionVerdict SoftReason(BlockPos p, int id, int dim)
    {
        static ProtectionVerdict S(string reason, string why, string? unlock = null) => new(ProtectionLevel.Soft, reason, why, unlock);
        string name = Blocks.Get(id).Name.Replace('_', ' ');
        const string containerUnlock = "punch it and say 'you can break it'";
        if (_blockIds.Contains(id))
            return IsContainer(id)
                ? S("container", $"a {name}: it may hold items", containerUnlock)
                : S(id is Blocks.Sapling ? "sapling" : id is Blocks.Farmland ? "farm" : "station", $"a {name}");
        foreach (var r in _memoryRegions)
            if (Inside(p, r.Min, r.Max))
                return S("structure", string.IsNullOrWhiteSpace(r.Label) ? "part of a remembered build" : $"part of the {r.Label} build");
        foreach (var r in _configRegions)
            if (Inside(p, r.Min, r.Max)) return S("config_region", "inside a protected region from my config");
        if (IndexerKind?.Invoke(p) is { } kind)
            return kind switch
            {
                IndexKinds.UnknownStructure => S("unknown_build", "part of a player build I don't know yet"),
                IndexKinds.Generator => S("generator", "a cobble generator's wall or fluid: breaking it can spill lava/water"),
                IndexKinds.Farm => S("farm", "farmland or irrigation of a farm"),
                IndexKinds.Tree => S("tree_soil", "the dirt under a tree or sapling (unrenewable)"),
                IndexKinds.Portal => S("portal", "a nether portal frame"),
                IndexKinds.Sign => S("sign", "a sign"),
                _ when IsContainer(id) => S("container", $"a {name}: it may hold items", containerUnlock),
                _ => S("station", $"a {kind.Replace('_', ' ')}"),
            };
        if (Claims != null && !Claims.IsFree(ClaimKey.Block(p, dim))) return S("claimed", "another hive bot is working on it right now");
        return S("unknown", "a protected block");
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
            _memoryRegions = _regions.LabeledRegions()
                .Select(r => (Min(r.Min, r.Max), Max(r.Min, r.Max), r.Label)).ToArray();
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

    private static bool Inside(BlockPos p, BlockPos min, BlockPos max) =>
        p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;

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
