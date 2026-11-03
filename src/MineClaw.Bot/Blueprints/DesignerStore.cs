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
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
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
                """);
    }

    private const string Cols = "id,owner,model,design_id,name,rev,blocks,ox,oy,oz,ax,ay,az,cost_usd,calls,pending,created,updated";

    public DesignerSessionRecord Insert(DesignerSessionRecord s)
    {
        s.Created = s.Updated = _time.GetUtcNow();
        lock (_db.Gate)
        {
            _db.Exec("""
                INSERT INTO designer_sessions(owner,model,design_id,name,rev,blocks,ox,oy,oz,cost_usd,calls,pending,created,updated)
                VALUES($o,$m,$d,$n,$r,$b,$ox,$oy,$oz,0,0,'',$t,$t)
                """, ("$o", s.Owner), ("$m", s.Model), ("$d", s.DesignId), ("$n", s.Name), ("$r", s.Canvas.Rev), ("$b", s.Canvas.Encode()),
                ("$ox", s.Offset.X), ("$oy", s.Offset.Y), ("$oz", s.Offset.Z), ("$t", AgentDb.ToUnixMs(s.Created)));
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
                    cost_usd=$c, calls=$k, pending=$p, updated=$t WHERE id=$id
                """, ("$m", s.Model), ("$d", s.DesignId), ("$n", s.Name), ("$r", s.Canvas.Rev), ("$b", s.Canvas.Encode()),
                ("$ox", s.Offset.X), ("$oy", s.Offset.Y), ("$oz", s.Offset.Z), ("$ax", s.Anchor?.X), ("$ay", s.Anchor?.Y), ("$az", s.Anchor?.Z),
                ("$c", (double)s.CostUsd), ("$k", s.Calls), ("$p", s.PendingEdits), ("$t", AgentDb.ToUnixMs(s.Updated)), ("$id", s.Id));
    }

    public DesignerSessionRecord? Get(long id)
    {
        lock (_db.Gate)
            return _db.Query($"SELECT {Cols} FROM designer_sessions WHERE id=$id", r => new DesignerSessionRecord
            {
                Id = r.GetInt64(0), Owner = r.GetString(1), Model = r.GetString(2), DesignId = AgentDb.LongOrNull(r, 3), Name = r.GetString(4),
                Canvas = DesignerCanvas.Decode(r.GetString(6), r.GetInt32(5)), Offset = new BlockPos(r.GetInt32(7), r.GetInt32(8), r.GetInt32(9)),
                Anchor = r.IsDBNull(10) ? null : new BlockPos(r.GetInt32(10), r.GetInt32(11), r.GetInt32(12)),
                CostUsd = (decimal)r.GetDouble(13), Calls = r.GetInt32(14), PendingEdits = r.GetString(15),
                Created = AgentDb.FromUnixMs(r.GetInt64(16)), Updated = AgentDb.FromUnixMs(r.GetInt64(17)),
            }, ("$id", id)).FirstOrDefault();
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
