using System.Text.Json;
using MineClaw.Agent.Rag;

namespace MineClaw.Hive.Story;

/// <summary>
/// Story Mode in the agent db (tables created on first use, idempotent): <c>story_colonies</c> (one row per colony: its settings),
/// <c>story_pawns</c> (characteristics, priorities, schedule, drafted, queue) and <c>story_objects</c> (zones, designations,
/// storages, builds with their ghosts, workbenches with their bills). A colony survives restarts and resumes.
/// </summary>
public sealed class StoryStore(AgentDb db)
{
    private bool _ready;
    private readonly object _lock = new();

    private void Ensure()
    {
        if (_ready) return;
        db.Exec("CREATE TABLE IF NOT EXISTS story_colonies(id TEXT PRIMARY KEY, json TEXT NOT NULL, rev INTEGER NOT NULL, updated INTEGER NOT NULL)");
        db.Exec("CREATE TABLE IF NOT EXISTS story_pawns(colony TEXT NOT NULL, name TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(colony, name))");
        db.Exec("CREATE TABLE IF NOT EXISTS story_objects(colony TEXT NOT NULL, kind TEXT NOT NULL, id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(colony, kind, id))");
        _ready = true;
    }

    private static readonly JsonSerializerOptions J = StoryJson.Options;

    public void Save(StoryColony c)
    {
        lock (_lock)
        {
            Ensure();
            db.Tx(() =>
            {
                db.Exec("INSERT INTO story_colonies(id, json, rev, updated) VALUES ($i, $j, $r, $u) ON CONFLICT(id) DO UPDATE SET json = excluded.json, rev = excluded.rev, updated = excluded.updated",
                    ("$i", c.Id), ("$j", JsonSerializer.Serialize(c.Meta, J)), ("$r", c.Rev), ("$u", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                db.Exec("DELETE FROM story_pawns WHERE colony = $c", ("$c", c.Id));
                foreach (var p in c.Pawns.Values)
                    db.Exec("INSERT INTO story_pawns(colony, name, json) VALUES ($c, $n, $j)", ("$c", c.Id), ("$n", p.Name), ("$j", JsonSerializer.Serialize(p, J)));
                db.Exec("DELETE FROM story_objects WHERE colony = $c", ("$c", c.Id));
                void Put<T>(string kind, string id, T v) =>
                    db.Exec("INSERT OR REPLACE INTO story_objects(colony, kind, id, json) VALUES ($c, $k, $i, $j)", ("$c", c.Id), ("$k", kind), ("$i", id), ("$j", JsonSerializer.Serialize(v, J)));
                foreach (var z in c.Zones) Put("zone", z.Id, z);
                foreach (var d in c.Designations) Put("designation", d.Id, d);
                foreach (var s in c.Storages) Put("storage", s.Chest.ToString(), s);
                foreach (var b in c.Builds) Put("build", b.Id, b);
                foreach (var w in c.Benches) Put("bench", w.Pos.ToString(), w);
            });
        }
    }

    /// <summary>Every saved colony (released ones too: their state is kept).</summary>
    public List<StoryColony> Load(Func<ColonyMeta, StoryColony> make)
    {
        lock (_lock)
        {
            Ensure();
            var list = new List<StoryColony>();
            foreach (var (id, json, rev) in db.Query("SELECT id, json, rev FROM story_colonies ORDER BY id", r => (r.GetString(0), r.GetString(1), r.GetInt64(2))))
            {
                var meta = JsonSerializer.Deserialize<ColonyMeta>(json, J);
                if (meta == null) continue;
                var c = make(meta);
                foreach (var pj in db.Query("SELECT json FROM story_pawns WHERE colony = $c", r => r.GetString(0), ("$c", id)))
                    if (JsonSerializer.Deserialize<PawnState>(pj, J) is { } p) c.Pawns[p.Name] = p;
                foreach (var (kind, oj) in db.Query("SELECT kind, json FROM story_objects WHERE colony = $c ORDER BY rowid", r => (r.GetString(0), r.GetString(1)), ("$c", id)))
                {
                    switch (kind)
                    {
                        case "zone" when JsonSerializer.Deserialize<ZoneState>(oj, J) is { } z: c.Zones.Add(z); break;
                        case "designation" when JsonSerializer.Deserialize<DesignationState>(oj, J) is { } d: c.Designations.Add(d); break;
                        case "storage" when JsonSerializer.Deserialize<StorageState>(oj, J) is { } s: c.Storages.Add(s); break;
                        case "build" when JsonSerializer.Deserialize<BuildState>(oj, J) is { } b:
                            foreach (var g in b.Ghosts.Where(g => g.State == "leased")) g.State = "todo"; // its lease went with the old process
                            c.Builds.Add(b);
                            break;
                        case "bench" when JsonSerializer.Deserialize<WorkbenchState>(oj, J) is { } w: c.Benches.Add(w); break;
                    }
                }
                c.SetRev(rev);
                list.Add(c);
            }
            return list;
        }
    }

    public void Delete(string colony)
    {
        lock (_lock)
        {
            Ensure();
            db.Tx(() =>
            {
                db.Exec("DELETE FROM story_colonies WHERE id = $c", ("$c", colony));
                db.Exec("DELETE FROM story_pawns WHERE colony = $c", ("$c", colony));
                db.Exec("DELETE FROM story_objects WHERE colony = $c", ("$c", colony));
            });
        }
    }
}
