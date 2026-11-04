using System.Globalization;

namespace MineClaw.World.Claims;

/// <summary>What a hive lease covers. Areas cover every block inside them (inclusive box).</summary>
public enum ClaimKind { Block, Area, Chest, Furnace, Task }

/// <summary>
/// A claimable thing: a block, an inclusive block box, a chest/furnace (by its block), or a named task.
/// <see cref="Id"/> is the stable wire/string form ("block:1,64,-3", "area:0,60,0..4,70,4", "chest:..", "task:cobble");
/// spatial keys outside the overworld carry their dimension ("chest:1,64,-3@-1"), so the same coordinates in the
/// overworld and the nether are different leases. Tasks are dimension-free.
/// </summary>
public readonly record struct ClaimKey(ClaimKind Kind, BlockPos Min, BlockPos Max, string? Name = null, int Dim = 0)
{
    public static ClaimKey Block(BlockPos p, int dim = 0) => new(ClaimKind.Block, p, p, null, dim);
    public static ClaimKey Block(int x, int y, int z, int dim = 0) => Block(new BlockPos(x, y, z), dim);
    public static ClaimKey Chest(BlockPos p, int dim = 0) => new(ClaimKind.Chest, p, p, null, dim);
    public static ClaimKey Furnace(BlockPos p, int dim = 0) => new(ClaimKind.Furnace, p, p, null, dim);
    public static ClaimKey Task(string name) => new(ClaimKind.Task, default, default, (name ?? "").Trim().ToLowerInvariant());

    public static ClaimKey Area(BlockPos a, BlockPos b, int dim = 0) => new(ClaimKind.Area,
        new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
        new BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)), null, dim);

    public string Id => Kind switch
    {
        ClaimKind.Task => "task:" + Name,
        ClaimKind.Area => $"area:{P(Min)}..{P(Max)}{DimSuffix}",
        _ => $"{Kind.ToString().ToLowerInvariant()}:{P(Min)}{DimSuffix}",
    };

    private string DimSuffix => Dim == 0 ? "" : "@" + Dim.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => Id;

    private static string P(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");

    public bool Contains(BlockPos p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;

    private bool Overlaps(ClaimKey o) =>
        Min.X <= o.Max.X && o.Min.X <= Max.X && Min.Y <= o.Max.Y && o.Min.Y <= Max.Y && Min.Z <= o.Max.Z && o.Min.Z <= Max.Z;

    /// <summary>
    /// Whether two leases can't be held by different owners at once: same thing, or (blocks/areas only) one box overlapping
    /// the other. Furnaces and tasks only conflict with themselves; a chest also with a chest key on a block beside it at the
    /// same height (<see cref="ChestPairs"/>: both halves of a double chest are one window, so a lease taken through one half
    /// must block the other; a trapped chest beside a normal one is over-locked, harmless).
    /// </summary>
    public bool ConflictsWith(ClaimKey o)
    {
        if (Kind != ClaimKind.Task && o.Kind != ClaimKind.Task && Dim != o.Dim) return false; // same coordinates, other dimension
        if (Kind == o.Kind) return SameKindConflicts(o);
        return IsSpatial(Kind) && IsSpatial(o.Kind) && Overlaps(o);
    }

    private bool SameKindConflicts(ClaimKey o) => Kind switch
    {
        ClaimKind.Task => string.Equals(Name, o.Name, StringComparison.Ordinal),
        ClaimKind.Area => Overlaps(o),
        ClaimKind.Chest => Min == o.Min || (Min.Y == o.Min.Y && Math.Abs(Min.X - o.Min.X) + Math.Abs(Min.Z - o.Min.Z) == 1),
        _ => Min == o.Min,
    };

    private static bool IsSpatial(ClaimKind kind) => kind is ClaimKind.Block or ClaimKind.Area;

    public static bool TryParse(string? id, out ClaimKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(id)) return false;
        int c = id.IndexOf(':');
        if (c <= 0) return false;
        string kind = id[..c], rest = id[(c + 1)..];
        if (!SplitDim(kind, ref rest, out int dim)) return false;
        return FromParts(kind, rest, dim, out key);
    }

    /// <summary>A spatial key's "@dim" suffix (tasks have none: an '@' is part of the name).</summary>
    private static bool SplitDim(string kind, ref string rest, out int dim)
    {
        dim = 0;
        int at = kind == "task" ? -1 : rest.LastIndexOf('@');
        if (at < 0) return true;
        if (!int.TryParse(rest[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out dim)) return false;
        rest = rest[..at];
        return true;
    }

    private static bool FromParts(string kind, string rest, int dim, out ClaimKey key)
    {
        key = default;
        switch (kind)
        {
            case "task" when rest.Length > 0: key = Task(rest); return true;
            case "area": return TryArea(rest, dim, out key);
            case "block" when TryPos(rest, out var p): key = Block(p, dim); return true;
            case "chest" when TryPos(rest, out var p): key = Chest(p, dim); return true;
            case "furnace" when TryPos(rest, out var p): key = Furnace(p, dim); return true;
            default: return false;
        }
    }

    /// <summary>"x,y,z..x,y,z".</summary>
    private static bool TryArea(string rest, int dim, out ClaimKey key)
    {
        key = default;
        var i = rest.IndexOf("..", StringComparison.Ordinal);
        if (i < 0 || !TryPos(rest[..i], out var a) || !TryPos(rest[(i + 2)..], out var b)) return false;
        key = Area(a, b, dim);
        return true;
    }

    private static bool TryPos(string s, out BlockPos p)
    {
        p = default;
        var parts = s.Split(',');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var z)) return false;
        p = new BlockPos(x, y, z);
        return true;
    }
}

/// <summary>Answer to a claim request. <see cref="Holder"/> = who has it when refused (or the caller when granted).</summary>
public sealed record ClaimResult(bool Ok, string? Holder = null, DateTimeOffset Expires = default, string? Message = null)
{
    public static ClaimResult Granted(string owner, DateTimeOffset expires) => new(true, owner, expires);
    public static ClaimResult Refused(string holder, DateTimeOffset expires) => new(false, holder, expires, $"claimed by {holder}");
}

/// <summary>
/// Hive claim board (leases with TTLs) as seen by one bot. The authoritative board lives in the main process; clones talk to
/// it over IPC and keep a pushed mirror so <see cref="IsFree"/> is a fast local check. Skills/actions should:
/// <list type="bullet">
/// <item>skip candidates where <see cref="IsFree"/> is false (block selection in mining/harvest/tree felling, chest choice);</item>
/// <item><see cref="TryClaimAsync"/> before touching a chest/furnace or starting an area/task job, and back off when refused;</item>
/// <item>call it again to renew long jobs before the TTL runs out, and <see cref="ReleaseAsync"/> when done.</item>
/// </list>
/// A single bot (no hive) uses <see cref="NullClaimBoard"/>: everything is free and every claim is granted.
/// </summary>
public interface IClaimBoard
{
    /// <summary>The bot this view belongs to (claim owner).</summary>
    string Owner { get; }

    /// <summary>True when no OTHER bot holds a live lease conflicting with <paramref name="key"/>. Fast, sync, never throws.</summary>
    bool IsFree(ClaimKey key);

    /// <summary>Acquires (or renews, if already ours) a lease. Default TTL is the board's (typically 60 s).</summary>
    Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default);

    /// <summary>Releases our lease (no-op when we don't hold it).</summary>
    Task ReleaseAsync(ClaimKey key, CancellationToken ct = default);

    /// <summary>Who ELSE holds a live lease conflicting with <paramref name="key"/> (for "held by 1337 ×2" notes); null = free or
    /// unknown. Fast, local, never throws.</summary>
    string? HolderOf(ClaimKey key) => null;
}

/// <summary>No hive: nothing is ever claimed by anyone else.</summary>
public sealed class NullClaimBoard(string owner = "self") : IClaimBoard
{
    public static readonly NullClaimBoard Instance = new();
    public string Owner => owner;
    public bool IsFree(ClaimKey key) => true;
    public Task<ClaimResult> TryClaimAsync(ClaimKey key, TimeSpan? ttl = null, string? note = null, CancellationToken ct = default) =>
        System.Threading.Tasks.Task.FromResult(ClaimResult.Granted(owner, DateTimeOffset.MaxValue));
    public Task ReleaseAsync(ClaimKey key, CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
}
