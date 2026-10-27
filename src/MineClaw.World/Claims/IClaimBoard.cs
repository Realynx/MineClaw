using System.Globalization;

namespace MineClaw.World.Claims;

/// <summary>What a hive lease covers. Areas cover every block inside them (inclusive box).</summary>
public enum ClaimKind { Block, Area, Chest, Furnace, Task }

/// <summary>
/// A claimable thing: a block, an inclusive block box, a chest/furnace (by its block), or a named task.
/// <see cref="Id"/> is the stable wire/string form ("block:1,64,-3", "area:0,60,0..4,70,4", "chest:..", "task:cobble").
/// </summary>
public readonly record struct ClaimKey(ClaimKind Kind, BlockPos Min, BlockPos Max, string? Name = null)
{
    public static ClaimKey Block(BlockPos p) => new(ClaimKind.Block, p, p);
    public static ClaimKey Block(int x, int y, int z) => Block(new BlockPos(x, y, z));
    public static ClaimKey Chest(BlockPos p) => new(ClaimKind.Chest, p, p);
    public static ClaimKey Furnace(BlockPos p) => new(ClaimKind.Furnace, p, p);
    public static ClaimKey Task(string name) => new(ClaimKind.Task, default, default, (name ?? "").Trim().ToLowerInvariant());

    public static ClaimKey Area(BlockPos a, BlockPos b) => new(ClaimKind.Area,
        new BlockPos(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z)),
        new BlockPos(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z)));

    public string Id => Kind switch
    {
        ClaimKind.Task => "task:" + Name,
        ClaimKind.Area => $"area:{P(Min)}..{P(Max)}",
        _ => $"{Kind.ToString().ToLowerInvariant()}:{P(Min)}",
    };

    public override string ToString() => Id;

    private static string P(BlockPos p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y},{p.Z}");

    public bool Contains(BlockPos p) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;

    private bool Overlaps(ClaimKey o) =>
        Min.X <= o.Max.X && o.Min.X <= Max.X && Min.Y <= o.Max.Y && o.Min.Y <= Max.Y && Min.Z <= o.Max.Z && o.Min.Z <= Max.Z;

    /// <summary>
    /// Whether two leases can't be held by different owners at once: same thing, or (blocks/areas only) one box overlapping
    /// the other. Chests, furnaces and tasks only conflict with themselves.
    /// </summary>
    public bool ConflictsWith(ClaimKey o)
    {
        if (Kind == o.Kind) return Kind switch
        {
            ClaimKind.Task => string.Equals(Name, o.Name, StringComparison.Ordinal),
            ClaimKind.Area => Overlaps(o),
            _ => Min == o.Min,
        };
        bool spatial = Kind is ClaimKind.Block or ClaimKind.Area && o.Kind is ClaimKind.Block or ClaimKind.Area;
        return spatial && Overlaps(o);
    }

    public static bool TryParse(string? id, out ClaimKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(id)) return false;
        int c = id.IndexOf(':');
        if (c <= 0) return false;
        string kind = id[..c], rest = id[(c + 1)..];
        static bool Pos(string s, out BlockPos p)
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
        switch (kind)
        {
            case "task" when rest.Length > 0: key = Task(rest); return true;
            case "area":
                {
                    var i = rest.IndexOf("..", StringComparison.Ordinal);
                    if (i < 0 || !Pos(rest[..i], out var a) || !Pos(rest[(i + 2)..], out var b)) return false;
                    key = Area(a, b);
                    return true;
                }
            case "block" when Pos(rest, out var p): key = Block(p); return true;
            case "chest" when Pos(rest, out var p): key = Chest(p); return true;
            case "furnace" when Pos(rest, out var p): key = Furnace(p); return true;
            default: return false;
        }
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
