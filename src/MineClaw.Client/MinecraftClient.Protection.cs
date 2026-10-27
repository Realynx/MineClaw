using MineClaw.Data;
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
    /// <summary>Last edit that WORKED within <see cref="VanillaSpawnProtection"/> of spawn (Environment.TickCount64, pos):
    /// proof the bot can edit there, so a later single failure nearby is something else (lag, a race, decay).</summary>
    private (long At, BlockPos Pos)? _lastOkInsideSpawn;
    /// <summary>How long a successful edit inside the spawn square disproves spawn protection.</summary>
    private static readonly TimeSpan SpawnProofWindow = TimeSpan.FromMinutes(15);
    /// <summary>Undone edits near spawn not yet believed (one alone is lag, a race with another bot, leaf decay...):
    /// (Environment.TickCount64, pos, action instance). A region is learned only from two INDEPENDENT ones.</summary>
    private readonly List<(long At, BlockPos Pos, object? Action)> _undoneNearSpawn = new();
    /// <summary>How long an unconfirmed undone edit waits for an independent second one.</summary>
    private static readonly TimeSpan UndoneWindow = TimeSpan.FromMinutes(10);
    /// <summary>Two undone edits this far apart (Chebyshev, blocks) are independent even within one action: one tree crown
    /// (radius 2) never spans it.</summary>
    public const int IndependentEditDistance = 5;

    /// <summary>
    /// Hive: true when another bot is working at/next to this block (a lease it holds, or its body is right there), so the
    /// server "undoing" my edit is a race with it, never evidence of spawn protection. Set by the Bot layer.
    /// </summary>
    public Func<BlockPos, bool>? EditContested { get; set; }

    /// <summary>The learned area where the server undoes my digs/placements, or null. Mining, chop_tree and the
    /// pathfinder's dig/place moves skip it up front while it is fresh (<see cref="ClientOptions.ProtectedRegionRetry"/>);
    /// after that one attempt there is allowed again, and a success clears it.</summary>
    public LearnedProtectedRegion? ProtectedRegion => _protectedRegion;

    /// <summary>Raised (event dispatcher) when a protected region is learned/updated, or cleared (null) after a
    /// successful edit inside it. The Bot records it as an observation of kind <c>protected_region</c>.</summary>
    public event Action<LearnedProtectedRegion?>? ProtectedRegionChanged;

    /// <summary>
    /// A dig/place near spawn worked (event dispatcher). Lets the Bot retract a protected_region fact remembered from an
    /// earlier session (the client's own learned region is per session; ops lift spawn protection).
    /// </summary>
    public event Action<BlockPos, int>? EditSucceededNearSpawn;

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
        var sp = World.Spawn;
        if (World.Dimension == 0 && Math.Max(Math.Abs(p.X - sp.X), Math.Abs(p.Z - sp.Z)) <= VanillaSpawnProtection)
        {
            _lastOkInsideSpawn = (Environment.TickCount64, p);
            lock (_undoneNearSpawn) _undoneNearSpawn.Clear(); // a success near spawn disproves the unconfirmed failures
        }
        if (World.Dimension == 0 && Math.Max(Math.Abs(p.X - sp.X), Math.Abs(p.Z - sp.Z)) <= VanillaSpawnProtection * 2)
        {
            int dim = World.Dimension;
            Raise(() => EditSucceededNearSpawn?.Invoke(p, dim));
        }
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
        if (nearSpawn && _lastOkInsideSpawn is { } proof && Environment.TickCount64 - proof.At < SpawnProofWindow.TotalMilliseconds)
        {
            // My digs/placements inside the spawn square worked minutes ago (op, or protection off): one that didn't take
            // is a lag/race failure, not spawn protection. Learning a region here once blocked replanting a tree whose
            // logs I had just felled at that very spot.
            Log($"server didn't take my {what} at {Fmt(p)}, but my edit at {Fmt(proof.Pos)} ({d} from spawn too) worked "
                + $"{(Environment.TickCount64 - proof.At) / 1000}s ago: not spawn protection");
            nearSpawn = false;
        }
        else if (nearSpawn && World.GetBlock(p) == Blocks.Leaves)
        {
            // Leaves race with decay and with another bot chopping the same crown: never evidence of protection.
            Log($"server didn't take my {what} at {Fmt(p)} (leaves): not counted as spawn protection");
        }
        else if (nearSpawn && EditContested?.Invoke(p) == true)
        {
            Log($"server didn't take my {what} at {Fmt(p)}, but another hive bot is working right there: a race, not spawn protection");
            nearSpawn = false;
        }
        else if (nearSpawn)
        {
            var old = _protectedRegion;
            int fails;
            bool confirmed;
            long now = Environment.TickCount64;
            object? action = _actionCts;
            lock (_undoneNearSpawn)
            {
                _undoneNearSpawn.RemoveAll(f => now - f.At > UndoneWindow.TotalMilliseconds);
                // Independent = from a separate action, or far enough apart that it isn't one tree crown / one spot.
                confirmed = old != null || _undoneNearSpawn.Any(f => !ReferenceEquals(f.Action, action) || ChebyshevDistance(f.Pos, p) >= IndependentEditDistance);
                _undoneNearSpawn.Add((now, p, action));
                fails = (old?.Failures ?? 0) + (old == null ? _undoneNearSpawn.Count : 1);
                if (confirmed) _undoneNearSpawn.Clear();
            }
            if (confirmed)
            {
                int radius = Math.Max(VanillaSpawnProtection, Math.Max(d, old?.Radius ?? 0));
                var r = new LearnedProtectedRegion(sp, radius, World.Dimension, fails, $"server undid {what} at {Fmt(p)}, {d} from spawn ({fails} undone edits)", DateTime.UtcNow);
                _protectedRegion = r;
                Log("learned " + r);
                Raise(() => ProtectedRegionChanged?.Invoke(r));
            }
            else Log($"server didn't take my {what} at {Fmt(p)} ({d} from spawn): one undone edit, not yet spawn protection (needs a second, independent one)");
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

    private static int ChebyshevDistance(BlockPos a, BlockPos b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));

    /// <summary>
    /// The region was retracted elsewhere (another hive process's successful edit removed the shared protected_region fact):
    /// forget my in-memory copy too. No <see cref="ProtectedRegionChanged"/> (the fact is already gone).
    /// </summary>
    public bool ForgetProtectedRegion(string why)
    {
        lock (_undoneNearSpawn) _undoneNearSpawn.Clear();
        if (Interlocked.Exchange(ref _protectedRegion, null) is not { } r) return false;
        Log($"forgetting {r}: {why}");
        return true;
    }

    /// <summary>
    /// Adopt a region another hive process learned (the shared protected_region fact). It keeps the learner's time, so a
    /// stale one only allows a probe (<see cref="EditBlocked"/>). No <see cref="ProtectedRegionChanged"/> (no echo).
    /// </summary>
    public bool AdoptProtectedRegion(LearnedProtectedRegion r)
    {
        if (_protectedRegion != null) return false;
        if (_lastOkInsideSpawn is { } proof && Environment.TickCount64 - proof.At < SpawnProofWindow.TotalMilliseconds) return false; // my edits work here
        _protectedRegion = r;
        Log("adopted shared " + r);
        return true;
    }
}
