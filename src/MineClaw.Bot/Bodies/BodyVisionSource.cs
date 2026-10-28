using MineClaw.Bot.Perception.Vision;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// Hive vision served from the registry (one brain, many bodies, step 2): the clone bodies' live positions, jobs and vitals
/// straight from their clients, the claim board's leases, and "send me your view" as a direct flush of the body's world
/// memory (no IPC). The see hub adds main itself.
/// </summary>
public sealed class BodyVisionSource(BodyRegistry registry, Func<IReadOnlyList<HiveLease>>? leases = null, TimeProvider? time = null) : IHiveVision
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public IReadOnlyList<HiveBotState> Clones()
    {
        var now = _time.GetUtcNow();
        var list = new List<HiveBotState>();
        foreach (var b in registry.All)
        {
            if (b.IsMain || !b.Adapter.IsPlaying) continue;
            var w = b.Adapter.World;
            var p = w.Player;
            string? job = b.Activities.Describe() ?? (b.Scripts?.Current is { } s ? "script " + s.Name : null) ?? b.Adapter.CurrentAction;
            list.Add(new HiveBotState(b.Name, w.Dimension, p.Position.X, p.Position.Y, p.Position.Z, job, p.Health, p.Food, now));
        }
        return list;
    }

    public IReadOnlyList<HiveLease> Leases() => leases?.Invoke() ?? [];

    public Task<HiveEyePose?> ViewAsync(string clone, CancellationToken ct)
    {
        if (registry.Find(clone) is not { IsMain: false } b || !b.Adapter.IsPlaying) return Task.FromResult<HiveEyePose?>(null);
        int flushed = 0;
        try { flushed = (b as Body)?.WorldMemory?.FlushForView() ?? 0; } catch (Exception) { /* the pose still helps */ }
        var w = b.Adapter.World;
        var p = w.Player;
        return Task.FromResult<HiveEyePose?>(new HiveEyePose(b.Name, w.Dimension, p.Position.X, p.Position.Y, p.Position.Z, p.Yaw, p.Pitch,
            _time.GetUtcNow(), flushed));
    }
}
