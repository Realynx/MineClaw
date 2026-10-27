using MineClaw.Bot.Perception.Vision;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Ipc;

namespace MineClaw.Hive.Supervisor;

/// <summary>
/// Main's hive vision source: clone positions/jobs/vitals from the supervisor's live snapshots, leases from the claim board,
/// and "send me your view" over IPC (the clone flushes its world memory to the shared db and answers with its eye pose).
/// </summary>
public sealed class HiveVisionSource(Func<HiveSupervisor?> supervisor, ClaimBoard board, TimeProvider? time = null) : IHiveVision
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public TimeSpan ViewTimeout { get; init; } = TimeSpan.FromSeconds(4);

    public IReadOnlyList<HiveBotState> Clones()
    {
        var list = new List<HiveBotState>();
        if (supervisor() is not { } sup) return list;
        foreach (var s in sup.Slots)
        {
            if (!s.Connected || s.Snapshot is not { Pos: { } p } snap) continue;
            string? job = !string.IsNullOrWhiteSpace(snap.Task) ? snap.Task : s.Pending ? s.Task?.Text : null;
            list.Add(new HiveBotState(s.Name, snap.Dimension, p.X, p.Y, p.Z, job, snap.Hp, snap.Food, snap.At, false, snap.Brain));
        }
        return list;
    }

    public IReadOnlyList<HiveLease> Leases() => board.Leases().Select(l => new HiveLease(l.Key.Id, l.Owner)).ToList();

    public async Task<HiveEyePose?> ViewAsync(string clone, CancellationToken ct)
    {
        if (supervisor() is not { } sup) return null;
        var name = sup.Resolve(clone) ?? clone;
        var v = await sup.RequestViewAsync(name, ViewTimeout, ct).ConfigureAwait(false);
        return v == null ? null : new HiveEyePose(name, v.Dim, v.X, v.Y, v.Z, v.Yaw, v.Pitch, _time.GetUtcNow(), v.Flushed);
    }
}
