using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Bot.Bodies;
using MineClaw.Hive.Supervisor;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;
using MineClaw.World;

namespace MineClaw.Hive.Crew;

/// <summary>
/// Crew lanes in hive mode (clones are separate processes): a lane is a hive job — a script spawn of the coordinator's saved
/// script on that clone with its role and board in the args (<c>__role</c>, <c>__crew</c>), brain asleep, staying online after
/// (<c>after: sleep</c>). The clone's script lane calls the role; its result line comes back as the hive result. The board is the
/// shared db (<see cref="DbCrewBoard"/>), so main and every clone see the same keys, inboxes and leases. Remote single actions
/// (<c>body("67").dig()</c>) need a lane here.
/// </summary>
public sealed class HiveCrewPort(HiveSupervisor hive, ICrewBoard board, TimeProvider? time = null) : ICrewPort
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ICrewBoard Board { get; } = board;

    public IReadOnlyList<string> Bodies() => hive.Slots.Where(s => s.State != CloneState.Retired).Select(s => s.Number).ToList();

    private CloneSlot? Slot(string body) => hive.Resolve(body) is { } n ? hive.Slot(n) : null;

    public BodyInfo? Info(string body)
    {
        if (Slot(body) is not { } s) return null;
        var v = hive.VitalsOf(s);
        return new BodyInfo(s.Number, v.Hp ?? 0, v.Food ?? 0, new BlockPos((int)Math.Floor(v.X ?? 0), (int)Math.Floor(v.Y ?? 0), (int)Math.Floor(v.Z ?? 0)),
            s.Pending || !s.IsLive, s.Task?.Text, -1);
    }

    public int Count(string body, string item)
    {
        if (Slot(body) is not { } s || hive.VitalsOf(s).Items is not { } items) return -1;
        return items.Where(i => string.Equals(i.Name, item, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Name.Replace(' ', '_'), item, StringComparison.OrdinalIgnoreCase))
            .Sum(i => i.Count);
    }

    public ILane Start(LaneSpec spec)
    {
        if (hive.Resolve(spec.Body) is not { } name || hive.Slot(name) is not { } slot) return Lanes.Failed(spec.Body, $"no clone {spec.Body}");
        if (slot.Pending) return Lanes.Failed(spec.Body, $"body {spec.Body} busy: {slot.Task?.Text}");
        // The clone runs the script by name from the shared db: an unsaved coordinator is saved first (same lint as any script).
        if (hive.ScriptPrep is { } prep)
        {
            var (saved, error, _) = prep(spec.Script, null);
            if (saved == null) (saved, error, _) = prep(spec.Script, spec.Code);
            if (saved == null) return Lanes.Failed(spec.Body, error ?? "script not saved");
        }
        var args = (string.IsNullOrWhiteSpace(spec.ArgsJson) ? null : JsonNode.Parse(spec.ArgsJson) as JsonObject) ?? new JsonObject();
        args["__role"] = spec.Role;
        args["__crew"] = spec.CrewId;
        var r = hive.Spawn($"crew {spec.Script}: {spec.Role}", name, options: new Ipc.HiveSpawnOptions(Script: spec.Script, Args: args.ToJsonString(), After: "sleep"));
        if (r.StartsWith("error", StringComparison.OrdinalIgnoreCase)) return Lanes.Failed(spec.Body, r);
        var task = slot.Task;
        var done = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time).ConfigureAwait(false);
                if (!ReferenceEquals(slot.Task, task) && slot.Task != null) return ActResult.Fail($"clone {spec.Body} was given another task");
                if (slot.ResultIn) return new ActResult(slot.ResultOk, LaneText(slot.ResultText));
                if (slot.State == CloneState.Retired) return ActResult.Fail($"clone {spec.Body} retired: {slot.LastExit}");
            }
        });
        return Lanes.Of(spec.Body, why => hive.Recall(name), done, () => $"lane {spec.Body}: {slot.LastProgress ?? "starting"}");
    }

    /// <summary>"ok: script gen_crew done (67 64, +64 cobblestone, 4m)" → "67 64": the role's own result for parallel().</summary>
    private static string LaneText(string? line)
    {
        var t = line ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^ok: script \S+ done \((.*)\)$");
        if (!m.Success) return t;
        var parts = m.Groups[1].Value.Split(", ").Where(p =>
            !System.Text.RegularExpressions.Regex.IsMatch(p, @"^\+\d+ ") && !System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d+(h\d+m|m\d+s|s)$")).ToList();
        return parts.Count > 0 ? string.Join(", ", parts) : t;
    }

    public Task<ActResult> ActAsync(string body, string action, JsonElement args, CancellationToken ct) =>
        Task.FromResult(ActResult.Fail($"hive mode: {body} acts through a lane (spawn(body, role) / parallel), not one remote action"));
}
