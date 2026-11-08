using MineClaw.Agent.Rag;
using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

/// <summary>A Design Studio session: who, which model, the design it edits (if any), the canvas and the money it cost.</summary>
public sealed class DesignerSessionRecord
{
    public long Id { get; set; }
    public string Owner { get; set; } = "";
    public string Model { get; set; } = "";
    public long? DesignId { get; set; }
    /// <summary>The opened or last saved design revision (separate from the mutable canvas revision).</summary>
    public int? DesignRevision { get; set; }
    public string Name { get; set; } = "";
    public DesignerCanvas Canvas { get; set; } = new();
    /// <summary>Design → canvas offset of a loaded design (a save to the same design puts it back where it stands).</summary>
    public BlockPos Offset { get; set; }
    /// <summary>The design's anchor on the canvas ("move origin"): the cell that lands on the build site; null = the footprint centre.</summary>
    public BlockPos? Anchor { get; set; }
    public decimal CostUsd { get; set; }
    public int Calls { get; set; }
    /// <summary>What the user changed since the AI's last turn ("placed 12 planks; removed 3"), told to the AI with the next message.</summary>
    public string PendingEdits { get; set; } = "";
    /// <summary>A mine session's version preference (<c>preferred_for</c>): a strip mine that suits that version's ore heights.</summary>
    public string? PreferredFor { get; set; }
    /// <summary>Opaque, bounded editor metadata. Excavation cells remain the executable design.</summary>
    public string? MineAuthoring { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>The canvas's game version (<see cref="DesignerCanvas.Version"/>).</summary>
    public string Version => Canvas.Version;

    /// <summary>Sets the canvas's version (null/unknown: the server's: a session saved before versions were recorded).</summary>
    public DesignerSessionRecord WithVersion(string? version)
    {
        Canvas.Version = DesignVersions.Normalize(version) ?? DesignVersions.Server;
        return this;
    }
}

/// <summary>One transcript line (user or assistant). Assistant lines carry the ops they applied (kind, label, cell count).</summary>
public sealed record DesignerMessageRecord(long Session, int Seq, string Role, string Text, DateTimeOffset At, string? Model, decimal CostUsd,
    IReadOnlyList<DesignerOpSummary> Ops);

public sealed record DesignerOpSummary(string Kind, string Label, int Cells);

/// <summary>
/// Design Studio persistence in the shared agent db (<c>designer_sessions</c>, <c>designer_messages</c>): a session survives
/// restarts, its canvas stored in the designs system's own run encoding.
/// </summary>
public sealed class DesignerStore
{
    private readonly AgentDb _db;
    private readonly TimeProvider _time;

    public DesignerStore(AgentDb db, TimeProvider? time = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS designer_sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT NOT NULL, model TEXT NOT NULL,
                    design_id INTEGER, name TEXT NOT NULL, rev INTEGER NOT NULL, blocks TEXT NOT NULL, ox INTEGER NOT NULL DEFAULT 0,
                    oy INTEGER NOT NULL DEFAULT 0, oz INTEGER NOT NULL DEFAULT 0, ax INTEGER, ay INTEGER, az INTEGER,
                    cost_usd REAL NOT NULL DEFAULT 0, calls INTEGER NOT NULL DEFAULT 0, pending TEXT NOT NULL DEFAULT '',
                    created INTEGER NOT NULL, updated INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS designer_messages(session_id INTEGER NOT NULL, seq INTEGER NOT NULL, role TEXT NOT NULL, text TEXT NOT NULL,
                    at INTEGER NOT NULL, model TEXT, cost_usd REAL NOT NULL DEFAULT 0, ops TEXT NOT NULL DEFAULT '', PRIMARY KEY(session_id, seq));
                CREATE TABLE IF NOT EXISTS designer_mine_authoring(design_id INTEGER NOT NULL, rev INTEGER NOT NULL, data TEXT NOT NULL,
                    PRIMARY KEY(design_id, rev));
                CREATE TABLE IF NOT EXISTS designer_library_deleted(design_id INTEGER PRIMARY KEY, deleted INTEGER NOT NULL, deleted_by TEXT NOT NULL);
                """);
        // The canvas's game version (added 2026-11-03). A session from before has none: it is the server's (its blocks are).
        lock (_db.Gate)
            try { _db.Exec("ALTER TABLE designer_sessions ADD COLUMN version TEXT"); } catch (Microsoft.Data.Sqlite.SqliteException) { /* already there */ }
        // Mine sessions (added 2026-11-03): the canvas's kind, its terrain seed and its version preference. Older rows are builds.
        foreach (var col in new[] { "kind TEXT", "seed TEXT", "preferred_for TEXT", "mine_authoring TEXT", "design_revision INTEGER" })
            lock (_db.Gate)
                try { _db.Exec("ALTER TABLE designer_sessions ADD COLUMN " + col); } catch (Microsoft.Data.Sqlite.SqliteException) { /* already there */ }
    }

    private const string Cols = "id,owner,model,design_id,name,rev,blocks,ox,oy,oz,ax,ay,az,cost_usd,calls,pending,created,updated,version,kind,seed,preferred_for,mine_authoring,design_revision";

    /// <summary>Library deletion is independent of the design's world status: existing builds and repairs retain their records.</summary>
    public bool IsLibraryDeleted(long designId)
    {
        lock (_db.Gate) return _db.ScalarLong("SELECT count(*) FROM designer_library_deleted WHERE design_id=$id", ("$id", designId)) != 0;
    }

    public void DeleteFromLibrary(long designId, string who)
    {
        lock (_db.Gate) _db.Exec("INSERT OR IGNORE INTO designer_library_deleted(design_id,deleted,deleted_by) VALUES($id,$at,$who)",
            ("$id", designId), ("$at", AgentDb.ToUnixMs(_time.GetUtcNow())), ("$who", who));
    }

    public DesignerSessionRecord Insert(DesignerSessionRecord s)
    {
        s.Created = s.Updated = _time.GetUtcNow();
        lock (_db.Gate)
        {
            _db.Exec("""
                INSERT INTO designer_sessions(owner,model,design_id,name,rev,blocks,ox,oy,oz,ax,ay,az,cost_usd,calls,pending,created,updated,version,kind,seed,preferred_for,mine_authoring,design_revision)
                VALUES($o,$m,$d,$n,$r,$b,$ox,$oy,$oz,$ax,$ay,$az,0,0,'',$t,$t,$v,$k,$s,$p,$ma,$dr)
                """, ("$o", s.Owner), ("$m", s.Model), ("$d", s.DesignId), ("$n", s.Name), ("$r", s.Canvas.Rev), ("$b", s.Canvas.Encode()),
                ("$ox", s.Offset.X), ("$oy", s.Offset.Y), ("$oz", s.Offset.Z), ("$ax", s.Anchor?.X), ("$ay", s.Anchor?.Y), ("$az", s.Anchor?.Z),
                ("$t", AgentDb.ToUnixMs(s.Created)), ("$v", s.Canvas.Version),
                ("$k", s.Canvas.Kind), ("$s", s.Canvas.Mine ? s.Canvas.Seed : null), ("$p", s.PreferredFor), ("$ma", s.MineAuthoring), ("$dr", s.DesignRevision));
            s.Id = _db.ScalarLong("SELECT last_insert_rowid()");
        }
        return s;
    }

    public void Update(DesignerSessionRecord s)
    {
        s.Updated = _time.GetUtcNow();
        lock (_db.Gate)
            _db.Exec("""
                UPDATE designer_sessions SET model=$m, design_id=$d, name=$n, rev=$r, blocks=$b, ox=$ox, oy=$oy, oz=$oz, ax=$ax, ay=$ay, az=$az,
                    cost_usd=$c, calls=$k, pending=$p, updated=$t, version=$v, kind=$kind, seed=$seed, preferred_for=$pf, mine_authoring=$ma, design_revision=$dr WHERE id=$id
                """, ("$v", s.Canvas.Version), ("$kind", s.Canvas.Kind), ("$seed", s.Canvas.Mine ? s.Canvas.Seed : null), ("$pf", s.PreferredFor), ("$m", s.Model), ("$d", s.DesignId), ("$n", s.Name), ("$r", s.Canvas.Rev), ("$b", s.Canvas.Encode()),
                ("$ox", s.Offset.X), ("$oy", s.Offset.Y), ("$oz", s.Offset.Z), ("$ax", s.Anchor?.X), ("$ay", s.Anchor?.Y), ("$az", s.Anchor?.Z),
                ("$c", (double)s.CostUsd), ("$k", s.Calls), ("$p", s.PendingEdits), ("$t", AgentDb.ToUnixMs(s.Updated)), ("$id", s.Id), ("$ma", s.MineAuthoring), ("$dr", s.DesignRevision));
    }

    public DesignerSessionRecord? Get(long id)
    {
        lock (_db.Gate)
            return _db.Query($"SELECT {Cols} FROM designer_sessions WHERE id=$id", r => new DesignerSessionRecord
            {
                Id = r.GetInt64(0), Owner = r.GetString(1), Model = r.GetString(2), DesignId = AgentDb.LongOrNull(r, 3), Name = r.GetString(4),
                // The kind, seed and version first: a mine canvas reads its dug cells against its own plot.
                Canvas = DesignerCanvas.Decode(r.GetString(6), r.GetInt32(5), AgentDb.StrOrNull(r, 19), AgentDb.StrOrNull(r, 20),
                    AgentDb.StrOrNull(r, 18) ?? DesignVersions.Server), Offset = new BlockPos(r.GetInt32(7), r.GetInt32(8), r.GetInt32(9)),
                Anchor = r.IsDBNull(10) ? null : new BlockPos(r.GetInt32(10), r.GetInt32(11), r.GetInt32(12)),
                CostUsd = (decimal)r.GetDouble(13), Calls = r.GetInt32(14), PendingEdits = r.GetString(15),
                Created = AgentDb.FromUnixMs(r.GetInt64(16)), Updated = AgentDb.FromUnixMs(r.GetInt64(17)),
                PreferredFor = DesignVersions.Normalize(AgentDb.StrOrNull(r, 21)),
                MineAuthoring = AgentDb.StrOrNull(r, 22),
                DesignRevision = r.IsDBNull(23) ? null : r.GetInt32(23),
            }.WithVersion(AgentDb.StrOrNull(r, 18)), ("$id", id)).FirstOrDefault();
    }

    /// <summary>Each saved revision owns its own immutable editor layout; never borrow metadata from a newer revision.</summary>
    public void SaveMineAuthoring(long designId, int rev, string? data)
    {
        if (data == null) return;
        lock (_db.Gate)
            _db.Exec("INSERT OR IGNORE INTO designer_mine_authoring(design_id,rev,data) VALUES($d,$r,$v)", ("$d", designId), ("$r", rev), ("$v", data));
    }

    public string? MineAuthoring(long designId, int rev)
    {
        lock (_db.Gate)
            return _db.Query("SELECT data FROM designer_mine_authoring WHERE design_id=$d AND rev=$r", r => r.GetString(0),
                ("$d", designId), ("$r", rev)).FirstOrDefault();
    }

    public DesignerMessageRecord AddMessage(long session, string role, string text, string? model = null, decimal cost = 0,
        IReadOnlyList<DesignerOpSummary>? ops = null)
    {
        var at = _time.GetUtcNow();
        ops ??= [];
        var enc = string.Join("\n", ops.Select(o => $"{o.Kind}\t{o.Cells}\t{o.Label.Replace('\t', ' ').Replace('\n', ' ')}"));
        lock (_db.Gate)
        {
            int seq = (int)_db.ScalarLong("SELECT COALESCE(MAX(seq),0)+1 FROM designer_messages WHERE session_id=$s", ("$s", session));
            _db.Exec("INSERT INTO designer_messages(session_id,seq,role,text,at,model,cost_usd,ops) VALUES($s,$q,$r,$t,$a,$m,$c,$o)",
                ("$s", session), ("$q", seq), ("$r", role), ("$t", text), ("$a", AgentDb.ToUnixMs(at)), ("$m", model), ("$c", (double)cost), ("$o", enc));
            return new DesignerMessageRecord(session, seq, role, text, at, model, cost, ops);
        }
    }

    /// <summary>The transcript, oldest first (the newest <paramref name="max"/>).</summary>
    public List<DesignerMessageRecord> Messages(long session, int max = 200)
    {
        lock (_db.Gate)
        {
            var list = _db.Query("SELECT session_id,seq,role,text,at,model,cost_usd,ops FROM designer_messages WHERE session_id=$s ORDER BY seq DESC LIMIT $n",
                r => new DesignerMessageRecord(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), AgentDb.FromUnixMs(r.GetInt64(4)),
                    AgentDb.StrOrNull(r, 5), (decimal)r.GetDouble(6), ParseOps(r.GetString(7))), ("$s", session), ("$n", max));
            list.Reverse();
            return list;
        }
    }

    private static List<DesignerOpSummary> ParseOps(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t', 3)).Where(a => a.Length == 3)
            .Select(a => new DesignerOpSummary(a[0], a[2], int.TryParse(a[1], out var n) ? n : 0)).ToList();
}
