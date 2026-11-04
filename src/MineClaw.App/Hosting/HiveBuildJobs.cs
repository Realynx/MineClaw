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
        // Default helpers: online and idle. Named ones are asked even when offline (the job logs them in), but only when a model
        // asked now: a build C# resumes (a standing order after main's restart) never logs clones in (live 10-30: a re-run crew
        // after the reboot launched 16 processes).
        bool evenOffline = names != null && MineClaw.Agent.Tools.ToolCaller.Current != null;
        foreach (var n in names ?? DefaultCandidateNames())
            if (CandidateSlot(n, evenOffline) is { } slot) list.Add(Member(slot));
        return list;
    }

    /// <summary>No fixed roster: without configured names, the supervisor's current slots (dynamic accounts) are the candidates.</summary>
    private IReadOnlyList<string> DefaultCandidateNames() => clones.Count > 0 ? clones : hive.Slots.Select(s => s.Name).ToList();

    /// <summary>The clone's slot when it can join the crew (known, not retired, and online and idle unless <paramref name="evenOffline"/>).</summary>
    private CloneSlot? CandidateSlot(string n, bool evenOffline)
    {
        if (hive.Resolve(n) is not { } name || hive.Slot(name) is not { } slot) return null;
        if (slot.State == CloneState.Retired) return null;
        return evenOffline || HiveSupervisor.IdleOnline(slot) ? slot : null;
    }

    private CrewMember Member(CloneSlot slot)
    {
        var v = hive.VitalsOf(slot);
        var at = v.X is { } x && v.Y is { } y && v.Z is { } z ? new BlockPos((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z)) : default;
        return new CrewMember(slot.Name, at, v.Items?.Any(i => i.Id == Items.WaterBucket) == true);
    }

    public string? StartLane(string body, long blueprintId, int lane)
    {
        if (MineClaw.Agent.Tools.ToolCaller.Current == null && hive.Resolve(body) is { } who && hive.Slot(who) is { IsLive: false })
            return $"{body} isn't in game: a resumed build never logs clones in (ask again to bring it in)";
        var args = JsonSerializer.Serialize(new { bp = blueprintId, lane });
        var r = hive.Spawn($"build design #{blueprintId}, lane {lane} of its crew plan", body, options: new HiveSpawnOptions(Code: LaneScript, Args: args));
        return r.StartsWith("error", StringComparison.OrdinalIgnoreCase) ? r : null;
    }
}
