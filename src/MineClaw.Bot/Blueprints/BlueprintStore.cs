using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

public static class DesignStatus
{
    public const string Designing = "designing", Review = "review", Draft = "draft", Accepted = "accepted", Building = "building", Built = "built",
        Failed = "failed", Cancelled = "cancelled",
        /// <summary>Deleted by a trusted player (design delete): its blocks taken down, its plans gone, hidden from lists; the row
        /// (DSL history) stays for audit.</summary>
        Deleted = "deleted";
}

/// <summary>A saved design (shared db: every hive process sees it; "build another one like the hut" reuses it).</summary>
public sealed class BlueprintRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Requester { get; set; }
    public string Bot { get; set; } = "";
    public string Dsl { get; set; } = "";
    public string Status { get; set; } = DesignStatus.Designing;
    public int Dim { get; set; }
    public BlockPos Site { get; set; }
    public BlockPos? Origin { get; set; }
    public int Rev { get; set; }
    public string Summary { get; set; } = "";
    public string Eval { get; set; } = "";
    public string Critique { get; set; } = "";
    public List<BillRow> Bill { get; set; } = new();
    public decimal CostUsd { get; set; }
    public int Iterations { get; set; }
    public string? AcceptedBy { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public bool HasPicture { get; set; }
    /// <summary>Where it goes, as the player referenced it ("on the 8×8 floor at the end of the bridge", "where Fox stood").</summary>
    public string Anchor { get; set; } = "";
}

public sealed record BillRow(string Item, int Need, int Have, int Stored, int Missing, string Source);

/// <summary>Progress of one body's part of a build (resume after a restart).</summary>
public sealed class BuildRecord
{
    public long BlueprintId { get; set; }
    public int Part { get; set; }
    public int Parts { get; set; } = 1;
    public string Owner { get; set; } = "";
    public string State { get; set; } = "building";
    public BlockPos Origin { get; set; }
    public int Dim { get; set; }
    public int Placed { get; set; }
    public int Total { get; set; }
    public List<BlockPos> Scaffold { get; set; } = new();
    public string Note { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
}

/// <summary>SQLite persistence for designs and build progress (the shared AgentDb; short transactions under its gate).</summary>
public sealed class BlueprintStore
{
    private readonly AgentDb _db;
    private readonly TimeProvider _time;

    /// <summary>The shared db (the build-plan store lives next to the designs).</summary>
    public AgentDb Db => _db;

    public BlueprintStore(AgentDb db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS blueprints(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, description TEXT NOT NULL,
                    requester TEXT, bot TEXT NOT NULL, dsl TEXT NOT NULL, status TEXT NOT NULL, dim INTEGER NOT NULL,
                    sx INTEGER, sy INTEGER, sz INTEGER, ox INTEGER, oy INTEGER, oz INTEGER, rev INTEGER NOT NULL DEFAULT 0,
                    summary TEXT, eval TEXT, critique TEXT, bill TEXT, cost_usd REAL NOT NULL DEFAULT 0, iterations INTEGER NOT NULL DEFAULT 0,
                    accepted_by TEXT, png BLOB, created INTEGER NOT NULL, updated INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS blueprint_builds(blueprint_id INTEGER NOT NULL, part INTEGER NOT NULL, parts INTEGER NOT NULL,
                    owner TEXT NOT NULL, state TEXT NOT NULL, dim INTEGER NOT NULL, ox INTEGER, oy INTEGER, oz INTEGER, placed INTEGER, total INTEGER,
                    scaffold TEXT, note TEXT, updated INTEGER NOT NULL, PRIMARY KEY(blueprint_id, part));
                """);
        lock (_db.Gate)
            try { _db.Exec("ALTER TABLE blueprints ADD COLUMN anchor TEXT"); } catch (Microsoft.Data.Sqlite.SqliteException) { /* already there */ }
    }

    private const string Cols = "id,name,description,requester,bot,dsl,status,dim,sx,sy,sz,ox,oy,oz,rev,summary,eval,critique,bill,cost_usd,iterations,accepted_by,created,updated,png IS NOT NULL,anchor";

    public BlueprintRecord Insert(BlueprintRecord r)
    {
        var now = _time.GetUtcNow();
        r.Created = r.Updated = now;
        lock (_db.Gate)
        {
            _db.Exec("""
                INSERT INTO blueprints(name,description,requester,bot,dsl,status,dim,sx,sy,sz,rev,created,updated)
                VALUES($n,$d,$r,$b,$dsl,$s,$dim,$sx,$sy,$sz,$rev,$t,$t)
                """, ("$n", r.Name), ("$d", r.Description), ("$r", r.Requester), ("$b", r.Bot), ("$dsl", r.Dsl), ("$s", r.Status), ("$dim", r.Dim),
                ("$sx", r.Site.X), ("$sy", r.Site.Y), ("$sz", r.Site.Z), ("$rev", r.Rev), ("$t", AgentDb.ToUnixMs(now)));
            r.Id = _db.ScalarLong("SELECT last_insert_rowid()");
        }
        return r;
    }

    public void Update(BlueprintRecord r, byte[]? png = null)
    {
        r.Updated = _time.GetUtcNow();
        lock (_db.Gate)
        {
            _db.Exec("""
                UPDATE blueprints SET name=$n, description=$d, requester=$r, dsl=$dsl, status=$s, dim=$dim, sx=$sx, sy=$sy, sz=$sz,
                    ox=$ox, oy=$oy, oz=$oz, rev=$rev, summary=$sum, eval=$ev, critique=$cr, bill=$bill, cost_usd=$c, iterations=$it,
                    accepted_by=$acc, updated=$t, anchor=$an WHERE id=$id
                """, ("$an", r.Anchor), ("$n", r.Name), ("$d", r.Description), ("$r", r.Requester), ("$dsl", r.Dsl), ("$s", r.Status), ("$dim", r.Dim),
                ("$sx", r.Site.X), ("$sy", r.Site.Y), ("$sz", r.Site.Z), ("$ox", r.Origin?.X), ("$oy", r.Origin?.Y), ("$oz", r.Origin?.Z),
                ("$rev", r.Rev), ("$sum", r.Summary), ("$ev", r.Eval), ("$cr", r.Critique), ("$bill", JsonSerializer.Serialize(r.Bill)),
                ("$c", (double)r.CostUsd), ("$it", r.Iterations), ("$acc", r.AcceptedBy), ("$t", AgentDb.ToUnixMs(r.Updated)), ("$id", r.Id));
            if (png != null)
            {
                _db.Exec("UPDATE blueprints SET png=$p WHERE id=$id", ("$p", png), ("$id", r.Id));
                r.HasPicture = true;
            }
        }
    }

    public BlueprintRecord? Get(long id)
    {
        lock (_db.Gate) return _db.Query($"SELECT {Cols} FROM blueprints WHERE id=$id", Map, ("$id", id)).FirstOrDefault();
    }

    /// <summary>By id ("3", "#3") or name ("hut": the newest design whose name contains it).</summary>
    public BlueprintRecord? Find(string idOrName)
    {
        var s = idOrName.Trim().TrimStart('#');
        if (long.TryParse(s, out var id)) return Get(id);
        lock (_db.Gate)
            return _db.Query($"SELECT {Cols} FROM blueprints WHERE (name LIKE $q OR description LIKE $q) AND status<>'deleted' ORDER BY (status IN ('built','accepted','building','review')) DESC, id DESC LIMIT 1",
                Map, ("$q", "%" + s + "%")).FirstOrDefault();
    }

    public List<BlueprintRecord> List(int max = 20)
    {
        lock (_db.Gate) return _db.Query($"SELECT {Cols} FROM blueprints WHERE status<>'deleted' ORDER BY id DESC LIMIT $n", Map, ("$n", max));
    }

    public byte[]? Picture(long id)
    {
        lock (_db.Gate) return _db.Scalar("SELECT png FROM blueprints WHERE id=$id", ("$id", id)) as byte[];
    }

    private static BlueprintRecord Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Name = r.GetString(1), Description = r.GetString(2), Requester = AgentDb.StrOrNull(r, 3), Bot = r.GetString(4),
        Dsl = r.GetString(5), Status = r.GetString(6), Dim = r.GetInt32(7),
        Site = new BlockPos(r.IsDBNull(8) ? 0 : r.GetInt32(8), r.IsDBNull(9) ? 0 : r.GetInt32(9), r.IsDBNull(10) ? 0 : r.GetInt32(10)),
        Origin = r.IsDBNull(11) ? null : new BlockPos(r.GetInt32(11), r.GetInt32(12), r.GetInt32(13)),
        Rev = r.GetInt32(14), Summary = AgentDb.StrOrNull(r, 15) ?? "", Eval = AgentDb.StrOrNull(r, 16) ?? "", Critique = AgentDb.StrOrNull(r, 17) ?? "",
        Bill = AgentDb.StrOrNull(r, 18) is { Length: > 0 } b ? JsonSerializer.Deserialize<List<BillRow>>(b) ?? new() : new(),
        CostUsd = (decimal)r.GetDouble(19), Iterations = r.GetInt32(20), AcceptedBy = AgentDb.StrOrNull(r, 21),
        Created = AgentDb.FromUnixMs(r.GetInt64(22)), Updated = AgentDb.FromUnixMs(r.GetInt64(23)), HasPicture = r.GetInt64(24) != 0,
        Anchor = AgentDb.StrOrNull(r, 25) ?? "",
    };

    // ------------------------------------------------------------------ build progress

    public void SaveBuild(BuildRecord b)
    {
        b.Updated = _time.GetUtcNow();
        lock (_db.Gate)
            _db.Exec("""
                INSERT INTO blueprint_builds(blueprint_id,part,parts,owner,state,dim,ox,oy,oz,placed,total,scaffold,note,updated)
                VALUES($id,$p,$ps,$o,$s,$dim,$x,$y,$z,$pl,$t,$sc,$n,$u)
                ON CONFLICT(blueprint_id,part) DO UPDATE SET parts=$ps, owner=$o, state=$s, dim=$dim, ox=$x, oy=$y, oz=$z, placed=$pl, total=$t,
                    scaffold=$sc, note=$n, updated=$u
                """, ("$id", b.BlueprintId), ("$p", b.Part), ("$ps", b.Parts), ("$o", b.Owner), ("$s", b.State), ("$dim", b.Dim),
                ("$x", b.Origin.X), ("$y", b.Origin.Y), ("$z", b.Origin.Z), ("$pl", b.Placed), ("$t", b.Total),
                ("$sc", string.Join(";", b.Scaffold.Select(p => $"{p.X},{p.Y},{p.Z}"))), ("$n", b.Note), ("$u", AgentDb.ToUnixMs(b.Updated)));
    }

    public BuildRecord? GetBuild(long blueprintId, int part)
    {
        lock (_db.Gate)
            return _db.Query("SELECT blueprint_id,part,parts,owner,state,dim,ox,oy,oz,placed,total,scaffold,note,updated FROM blueprint_builds WHERE blueprint_id=$id AND part=$p",
                MapBuild, ("$id", blueprintId), ("$p", part)).FirstOrDefault();
    }

    /// <summary>Unfinished builds (state building) owned by <paramref name="owner"/>: resume candidates after a restart.</summary>
    public List<BuildRecord> Unfinished(string owner)
    {
        lock (_db.Gate)
            return _db.Query("SELECT blueprint_id,part,parts,owner,state,dim,ox,oy,oz,placed,total,scaffold,note,updated FROM blueprint_builds WHERE owner=$o AND state='building' ORDER BY updated DESC",
                MapBuild, ("$o", owner));
    }

    public List<BuildRecord> Builds(long blueprintId)
    {
        lock (_db.Gate)
            return _db.Query("SELECT blueprint_id,part,parts,owner,state,dim,ox,oy,oz,placed,total,scaffold,note,updated FROM blueprint_builds WHERE blueprint_id=$id ORDER BY part",
                MapBuild, ("$id", blueprintId));
    }

    private static BuildRecord MapBuild(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        BlueprintId = r.GetInt64(0), Part = r.GetInt32(1), Parts = r.GetInt32(2), Owner = r.GetString(3), State = r.GetString(4), Dim = r.GetInt32(5),
        Origin = new BlockPos(r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)), Placed = r.GetInt32(9), Total = r.GetInt32(10),
        Scaffold = (AgentDb.StrOrNull(r, 11) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split(','))
            .Where(a => a.Length == 3).Select(a => new BlockPos(int.Parse(a[0]), int.Parse(a[1]), int.Parse(a[2]))).ToList(),
        Note = AgentDb.StrOrNull(r, 12) ?? "", Updated = AgentDb.FromUnixMs(r.GetInt64(13)),
    };
}
