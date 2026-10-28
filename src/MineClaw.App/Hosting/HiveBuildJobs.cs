using System.Text.Json;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Supervisor;
using MineClaw.World;

namespace MineClaw.App.Hosting;

/// <summary>
/// Hive mode's crew for builds (<see cref="IBuildJobs"/>): the clones are separate processes, so main slices the build for itself
/// plus the clones and gives each clone lane as a hive job, a fixed lane script (<see cref="LaneScript"/>, the brain asleep: zero
/// calls; JavaScript over the <c>build</c> host binding) running the clone's own <c>build({bp, lane})</c> against the shared plan.
/// Stand cells go through the claim board; each lane writes its own progress row; main runs its lane, waits for theirs and takes
/// the scaffold down.
/// </summary>
public sealed class HiveBuildJobs(HiveSupervisor hive, IReadOnlyList<string> clones) : IBuildJobs
{
    /// <summary>The lane job: C#'s fixed enabling primitive (no model authors it), linted and saved by the hive's script prep.</summary>
    public const string LaneScript = BuildJobs.LaneScript;

    public IReadOnlyList<CrewMember> Candidates(IReadOnlyList<string>? names = null)
    {
        var list = new List<CrewMember>();
        // No fixed roster: without configured names, the supervisor's current slots (dynamic accounts) are the candidates.
        foreach (var n in names ?? (clones.Count > 0 ? clones : hive.Slots.Select(s => s.Name).ToList()))
        {
            if (hive.Resolve(n) is not { } name || hive.Slot(name) is not { } slot) continue;
            if (slot.State == CloneState.Retired) continue;
            // Default helpers: online and idle. Named ones are asked even when offline (the job logs them in).
            if (names == null && !HiveSupervisor.IdleOnline(slot)) continue;
            var v = hive.VitalsOf(slot);
            var at = v.X is { } x && v.Y is { } y && v.Z is { } z ? new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)) : default;
            list.Add(new CrewMember(slot.Name, at, v.Items?.Any(i => i.Id == Items.WaterBucket) == true));
        }
        return list;
    }

    public string? StartLane(string body, long blueprintId, int lane)
    {
        var args = JsonSerializer.Serialize(new { bp = blueprintId, lane });
        var r = hive.Spawn($"build design #{blueprintId}, lane {lane} of its crew plan", body, options: new HiveSpawnOptions(Code: LaneScript, Args: args));
        return r.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? r : null;
    }
}
