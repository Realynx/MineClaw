using System.Text.Json;
using MineClaw.Agent.Rag;

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
                """);
    }

    public void Save(BuildPlan p)
    {
        p.Updated = _time.GetUtcNow();
        var json = JsonSerializer.Serialize(p, Json);
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
        lock (_db.Gate) _db.Exec("DELETE FROM build_plans WHERE blueprint_id=$id", ("$id", blueprintId));
    }

    private static BuildPlan? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<BuildPlan>(json, Json); }
        catch (JsonException) { return null; }
    }
}
