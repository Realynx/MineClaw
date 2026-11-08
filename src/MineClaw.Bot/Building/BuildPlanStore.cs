using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot.Building;

/// <summary>
/// Persisted build plans (db <c>build_plans</c>, one row per blueprint part): the step list with per-step status, columns,
/// the gate state and the placed-block ledger, so a restart, death, danger pause or stop resumes the same plan (re-verified
/// against the world) and a build waiting at its confirmation gate asks again instead of carrying on.
/// </summary>
public sealed class BuildPlanStore
{
    private readonly AgentDb _db;
    private readonly TimeProvider _time;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    public BuildPlanStore(AgentDb db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS build_plans(blueprint_id INTEGER NOT NULL, part INTEGER NOT NULL, version TEXT NOT NULL,
                    state TEXT NOT NULL, gate TEXT NOT NULL, plan TEXT NOT NULL, updated INTEGER NOT NULL, PRIMARY KEY(blueprint_id, part));
                CREATE TABLE IF NOT EXISTS build_lanes(blueprint_id INTEGER NOT NULL, slice TEXT NOT NULL, lane INTEGER NOT NULL, body TEXT NOT NULL,
                    state TEXT NOT NULL, progress TEXT NOT NULL, updated INTEGER NOT NULL, PRIMARY KEY(blueprint_id, slice, lane));
                """);
    }

    public void Save(BuildPlan p)
    {
        string json;
        // Crew lanes mutate the plan from several bodies at once: they (and this snapshot) lock the plan object.
        lock (p)
        {
            p.Updated = _time.GetUtcNow();
            json = JsonSerializer.Serialize(p, Json);
        }
        lock (_db.Gate)
            _db.Exec("""
                INSERT INTO build_plans(blueprint_id,part,version,state,gate,plan,updated) VALUES($id,$p,$v,$s,$g,$j,$u)
                ON CONFLICT(blueprint_id,part) DO UPDATE SET version=$v, state=$s, gate=$g, plan=$j, updated=$u
                """, ("$id", p.BlueprintId), ("$p", p.Part), ("$v", p.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("$s", p.State), ("$g", p.Gate.State.ToString()), ("$j", json), ("$u", AgentDb.ToUnixMs(p.Updated)));
    }

    public BuildPlan? Get(long blueprintId, int part)
    {
        lock (_db.Gate)
            return _db.Query("SELECT plan FROM build_plans WHERE blueprint_id=$id AND part=$p", r => Parse(r.GetString(0)), ("$id", blueprintId), ("$p", part))
                .FirstOrDefault();
    }

    public List<BuildPlan> ForBlueprint(long blueprintId)
    {
        lock (_db.Gate)
            return _db.Query("SELECT plan FROM build_plans WHERE blueprint_id=$id ORDER BY part", r => Parse(r.GetString(0)), ("$id", blueprintId))
                .Where(p => p != null).Select(p => p!).ToList();
    }

    /// <summary>Builds paused at their confirmation gate, newest first.</summary>
    public List<BuildPlan> AtGate()
    {
        lock (_db.Gate)
            return _db.Query("SELECT plan FROM build_plans WHERE gate='Waiting' ORDER BY updated DESC", r => Parse(r.GetString(0)))
                .Where(p => p != null).Select(p => p!).ToList();
    }

    public void Delete(long blueprintId)
    {
        lock (_db.Gate)
        {
            _db.Exec("DELETE FROM build_plans WHERE blueprint_id=$id", ("$id", blueprintId));
            _db.Exec("DELETE FROM build_lanes WHERE blueprint_id=$id", ("$id", blueprintId));
        }
    }

    // ------------------------------------------------------------------ crew lanes

    /// <summary>One lane's own progress row: its steps' status, the blocks it placed, its scaffold still up (a lane writes only
    /// its row, so bodies in other processes never overwrite each other's progress).</summary>
    private sealed record LaneRow(string Body, string State, string? Note, List<int[]> Status, List<BlockPos> Placed, List<BlockPos> Scaffold,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        List<OreFrontierCell>? OreFrontier = null);

    /// <summary>Saves lane <paramref name="lane"/>'s progress for the plan's current slicing (<see cref="BuildPlan.SliceId"/>).</summary>
    public void SaveLane(BuildPlan bp, PlanLane lane)
    {
        if (string.IsNullOrEmpty(bp.SliceId)) return;
        LaneRow row;
        lock (bp)
        {
            var steps = lane.Steps.Where(i => i >= 0 && i < bp.Steps.Count).Select(i => bp.Steps[i]).ToList();
            row = new LaneRow(lane.Body, lane.State, lane.Note,
                steps.Where(s => s.Status != StepStatus.Pending).Select(s => new[] { s.Index, (int)s.Status }).ToList(),
                steps.Where(s => s.Action == StepAction.Place && s.Status is StepStatus.Done or StepStatus.Verified).Select(s => s.Target).ToList(),
                steps.Where(s => s.Action == StepAction.Scaffold && s.Finished && bp.ScaffoldUp.Contains(s.Target)).Select(s => s.Target).ToList(),
                bp.OreFrontier?.Count > 0 ? bp.OreFrontier.ToList() : null);
        }
        var json = JsonSerializer.Serialize(row, Json);
        lock (_db.Gate)
            _db.Exec("""
                INSERT INTO build_lanes(blueprint_id,slice,lane,body,state,progress,updated) VALUES($id,$s,$l,$b,$st,$j,$u)
                ON CONFLICT(blueprint_id,slice,lane) DO UPDATE SET body=$b, state=$st, progress=$j, updated=$u
                """, ("$id", bp.BlueprintId), ("$s", bp.SliceId), ("$l", lane.Index), ("$b", lane.Body), ("$st", lane.State), ("$j", json),
                ("$u", AgentDb.ToUnixMs(_time.GetUtcNow())));
    }

    /// <summary>
    /// Folds every lane's progress row (same slicing) into <paramref name="bp"/>: finished steps, lane states, placed blocks and
    /// scaffold up, water taken back. Returns how many lane rows were found.
    /// </summary>
    public int MergeLanes(BuildPlan bp)
    {
        if (string.IsNullOrEmpty(bp.SliceId)) return 0;
        List<(int Lane, LaneRow? Row)> rows;
        lock (_db.Gate)
            rows = _db.Query("SELECT lane, progress FROM build_lanes WHERE blueprint_id=$id AND slice=$s",
                r => (r.GetInt32(0), ParseRow(r.GetString(1))), ("$id", bp.BlueprintId), ("$s", bp.SliceId));
        lock (bp)
            foreach (var (laneIx, row) in rows)
                if (row != null) MergeLane(bp, laneIx, row);
        return rows.Count;
    }

    /// <summary>One lane's row: its steps' status, placed blocks and scaffold up, and the lane's state.</summary>
    private static void MergeLane(BuildPlan bp, int laneIx, LaneRow row)
    {
        foreach (var st in row.Status) MergeStatus(bp, st);
        foreach (var p in row.Placed) if (!bp.Placed.Contains(p)) bp.Placed.Add(p);
        foreach (var p in row.Scaffold) if (!bp.ScaffoldUp.Contains(p)) bp.ScaffoldUp.Add(p);
        foreach (var ore in row.OreFrontier ?? [])
        {
            var frontier = bp.OreFrontier ??= [];
            if (frontier.All(c => c.Pos != ore.Pos)) frontier.Add(ore);
        }
        if (laneIx >= 0 && laneIx < bp.Lanes.Count) { bp.Lanes[laneIx].State = row.State; bp.Lanes[laneIx].Note = row.Note; }
    }

    /// <summary>[step, status] for a step not finished yet; water taken back marks its column removed.</summary>
    private static void MergeStatus(BuildPlan bp, int[] st)
    {
        if (st.Length != 2 || st[0] < 0 || st[0] >= bp.Steps.Count || bp.Steps[st[0]].Finished) return;
        var s = bp.Steps[st[0]];
        s.Status = (StepStatus)st[1];
        if (s.Action == StepAction.PickupWater && s.Finished) foreach (var c in bp.Columns.Where(c => c.Source == s.Target)) c.Removed = true;
    }

    private static LaneRow? ParseRow(string json)
    {
        try { return JsonSerializer.Deserialize<LaneRow>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static BuildPlan? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<BuildPlan>(json, Json); }
        catch (JsonException) { return null; }
    }
}
