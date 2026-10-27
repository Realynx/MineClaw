using MineClaw.World;

namespace MineClaw.Client;

/// <summary>An area where the server undoes the bot's digs/placements (b1.8.1 spawn protection: Chebyshev distance ≤ 16
/// from the world spawn for non-ops, overworld only). Learned from reverted edits; cleared by a later success inside.</summary>
/// <param name="Radius">Estimated from the failed positions: the vanilla 16 when every failure was within it, else the
/// farthest failure.</param>
public sealed record LearnedProtectedRegion(BlockPos Center, int Radius, int Dimension, int Failures, string Reason, DateTime LearnedAt)
{
    public bool Contains(BlockPos p, int dimension) =>
        dimension == Dimension && Math.Max(Math.Abs(p.X - Center.X), Math.Abs(p.Z - Center.Z)) <= Radius;

    public override string ToString() => $"protected_region center {Center.X},{Center.Y},{Center.Z} r{Radius} ({Reason})";
}

public sealed partial class MinecraftClient
{
    /// <summary>b1.8.1 NetServerHandler: edits within this Chebyshev distance of spawn need op.</summary>
    public const int VanillaSpawnProtection = 16;

    private volatile LearnedProtectedRegion? _protectedRegion;
    private int _actionEditsOk;

    /// <summary>The learned area where the server undoes my digs/placements, or null. Mining, chop_tree and the
    /// pathfinder's dig/place moves skip it up front while it is fresh (<see cref="ClientOptions.ProtectedRegionRetry"/>);
    /// after that one attempt there is allowed again, and a success clears it.</summary>
    public LearnedProtectedRegion? ProtectedRegion => _protectedRegion;

    /// <summary>Raised (event dispatcher) when a protected region is learned/updated, or cleared (null) after a
    /// successful edit inside it. The Bot records it as an observation of kind <c>protected_region</c>.</summary>
    public event Action<LearnedProtectedRegion?>? ProtectedRegionChanged;

    /// <summary>True while edits at <paramref name="p"/> should be skipped: inside the learned region and the fact is
    /// fresh. Once stale, one probe is allowed (its outcome refreshes or clears the fact).</summary>
    public bool EditBlocked(BlockPos p) =>
        _protectedRegion is { } r && r.Contains(p, World.Dimension) && DateTime.UtcNow - r.LearnedAt < Options.ProtectedRegionRetry;

    private string BlockedReason(BlockPos p) =>
        $"skipping {Fmt(p)}: inside the area where the server undid my edits (within {_protectedRegion?.Radius ?? VanillaSpawnProtection} of spawn; spawn protection, needs op)";

    /// <summary>A dig/place at <paramref name="p"/> worked: count it for the fail-fast rule and clear a region it disproves.</summary>
    private void NoteEditOk(BlockPos p)
    {
        Interlocked.Increment(ref _actionEditsOk);
        if (_protectedRegion is { } r && r.Contains(p, World.Dimension))
        {
            _protectedRegion = null;
            Log($"edit at {Fmt(p)} worked: forgetting {r}");
            Raise(() => ProtectedRegionChanged?.Invoke(null));
        }
    }

    /// <summary>
    /// The server ignored/reverted a dig or placement at <paramref name="p"/>. Near spawn this is spawn protection: learn
    /// the region. If no edit of the running action has worked yet, abort the whole macro now (mine, chop_tree, goto
    /// steps...) with a terse reason instead of grinding through every target.
    /// </summary>
    private void NoteEditReverted(BlockPos p, string what)
    {
        var sp = World.Spawn;
        int d = Math.Max(Math.Abs(p.X - sp.X), Math.Abs(p.Z - sp.Z));
        bool nearSpawn = World.Dimension == 0 && d <= VanillaSpawnProtection;
        if (nearSpawn)
        {
            var old = _protectedRegion;
            int fails = (old?.Failures ?? 0) + 1;
            int radius = Math.Max(VanillaSpawnProtection, Math.Max(d, old?.Radius ?? 0));
            var r = new LearnedProtectedRegion(sp, radius, World.Dimension, fails, $"server undid {what} at {Fmt(p)}, {d} from spawn", DateTime.UtcNow);
            _protectedRegion = r;
            Log("learned " + r);
            Raise(() => ProtectedRegionChanged?.Invoke(r));
        }
        if (Volatile.Read(ref _actionEditsOk) == 0)
        {
            string reason = nearSpawn
                ? $"server undid my {what}s within {VanillaSpawnProtection} of spawn (spawn protection? need op)"
                : $"server undid my {what} at {Fmt(p)} (protected area?)";
            _abortReason ??= reason;
            throw new ActionAbortedException(reason);
        }
    }
}
