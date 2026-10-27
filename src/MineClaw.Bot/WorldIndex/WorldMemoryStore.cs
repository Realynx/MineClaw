using MineClaw.Agent.Rag;

namespace MineClaw.Bot.WorldIndex;

/// <summary>One remembered chunk column: content hash (the diff key), per-section block summary, deflated blocks.</summary>
public sealed record ChunkRow(int Dim, int X, int Z, long Hash, string Summary, byte[] Data, DateTimeOffset LastSeen);

/// <summary>
/// Notified state of one unknown/auto structure, keyed by a content-anchored key ("min corner ~ block signature"); matched
/// fuzzily (overlapping box + similar block mix) so small edits keep the same record across restarts.
/// </summary>
public sealed record SceneryRow(int Dim, string Key, string Kind, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ,
    string Blocks, DateTimeOffset? AnnouncedAt, DateTimeOffset? AskedAt, DateTimeOffset LastSeen);

/// <summary>Persistence for the remembered world (chunk baselines) and the scenery ledger (what was announced/asked).</summary>
public interface IWorldMemoryStore
{
    ChunkRow? GetChunk(int dim, int x, int z);
    /// <summary>Hash + last_seen only (no blob), for cheap baseline checks.</summary>
    (long Hash, DateTimeOffset LastSeen)? GetChunkHash(int dim, int x, int z);
    /// <summary>Every column of a dimension, nearest first, at most <paramref name="max"/>.</summary>
    IReadOnlyList<ChunkRow> Chunks(int dim, int nearX, int nearZ, int max);
    void PutChunks(IReadOnlyList<ChunkRow> rows);
    void TouchChunks(int dim, IReadOnlyList<(int X, int Z)> cells, DateTimeOffset at);
    long TotalBytes();
    /// <summary>Deletes least-recently-seen columns until the total is ≤ <paramref name="maxBytes"/>, never one of
    /// <paramref name="keepDim"/> within <paramref name="keepRadius"/> chunks of (keepX, keepZ). Returns rows deleted.</summary>
    int Evict(long maxBytes, int keepDim, int keepX, int keepZ, int keepRadius);

    IReadOnlyList<SceneryRow> Scenery(int dim);
    void PutScenery(SceneryRow row, string? replacesKey = null);
}

/// <summary>SQLite store on the shared agent db (<c>world_chunks</c>, <c>scenery</c>), created on first use.</summary>
public sealed class SqliteWorldMemoryStore : IWorldMemoryStore
{
    private readonly AgentDb _db;

    public SqliteWorldMemoryStore(AgentDb db)
    {
        _db = db;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS world_chunks(dim INTEGER NOT NULL, cx INTEGER NOT NULL, cz INTEGER NOT NULL,
                    hash INTEGER NOT NULL, summary TEXT NOT NULL, data BLOB NOT NULL, bytes INTEGER NOT NULL, last_seen INTEGER NOT NULL,
                    PRIMARY KEY(dim, cx, cz));
                CREATE INDEX IF NOT EXISTS ix_world_chunks_seen ON world_chunks(last_seen);
                CREATE TABLE IF NOT EXISTS scenery(dim INTEGER NOT NULL, key TEXT NOT NULL, kind TEXT NOT NULL,
                    min_x INTEGER NOT NULL, min_y INTEGER NOT NULL, min_z INTEGER NOT NULL, max_x INTEGER NOT NULL, max_y INTEGER NOT NULL,
                    max_z INTEGER NOT NULL, blocks TEXT NOT NULL, announced_at INTEGER, asked_at INTEGER, last_seen INTEGER NOT NULL,
                    PRIMARY KEY(dim, key));
                """);
    }

    private static ChunkRow Row(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3),
        r.GetString(4), (byte[])r.GetValue(5), AgentDb.FromUnixMs(r.GetInt64(6)));

    public ChunkRow? GetChunk(int dim, int x, int z)
    {
        lock (_db.Gate)
            return _db.Query("SELECT dim,cx,cz,hash,summary,data,last_seen FROM world_chunks WHERE dim=$d AND cx=$x AND cz=$z", Row,
                ("$d", dim), ("$x", x), ("$z", z)).FirstOrDefault();
    }

    public (long Hash, DateTimeOffset LastSeen)? GetChunkHash(int dim, int x, int z)
    {
        lock (_db.Gate)
        {
            var l = _db.Query("SELECT hash,last_seen FROM world_chunks WHERE dim=$d AND cx=$x AND cz=$z",
                r => (r.GetInt64(0), AgentDb.FromUnixMs(r.GetInt64(1))), ("$d", dim), ("$x", x), ("$z", z));
            return l.Count == 0 ? null : l[0];
        }
    }

    public IReadOnlyList<ChunkRow> Chunks(int dim, int nearX, int nearZ, int max)
    {
        lock (_db.Gate)
            return _db.Query("""
                SELECT dim,cx,cz,hash,summary,data,last_seen FROM world_chunks WHERE dim=$d
                ORDER BY (cx-$x)*(cx-$x)+(cz-$z)*(cz-$z) LIMIT $n
                """, Row, ("$d", dim), ("$x", nearX), ("$z", nearZ), ("$n", max));
    }

    public void PutChunks(IReadOnlyList<ChunkRow> rows)
    {
        if (rows.Count == 0) return;
        lock (_db.Gate)
            _db.Tx(() =>
            {
                foreach (var c in rows)
                    _db.Exec("""
                        INSERT INTO world_chunks(dim,cx,cz,hash,summary,data,bytes,last_seen) VALUES($d,$x,$z,$h,$s,$b,$n,$t)
                        ON CONFLICT(dim,cx,cz) DO UPDATE SET hash=$h, summary=$s, data=$b, bytes=$n, last_seen=$t
                        """, ("$d", c.Dim), ("$x", c.X), ("$z", c.Z), ("$h", c.Hash), ("$s", c.Summary), ("$b", c.Data),
                        ("$n", c.Data.Length + c.Summary.Length + 32), ("$t", AgentDb.ToUnixMs(c.LastSeen)));
            });
    }

    public void TouchChunks(int dim, IReadOnlyList<(int X, int Z)> cells, DateTimeOffset at)
    {
        if (cells.Count == 0) return;
        lock (_db.Gate)
            _db.Tx(() =>
            {
                foreach (var (x, z) in cells)
                    _db.Exec("UPDATE world_chunks SET last_seen=$t WHERE dim=$d AND cx=$x AND cz=$z",
                        ("$t", AgentDb.ToUnixMs(at)), ("$d", dim), ("$x", x), ("$z", z));
            });
    }

    public long TotalBytes() { lock (_db.Gate) return _db.ScalarLong("SELECT COALESCE(SUM(bytes),0) FROM world_chunks"); }

    public int Evict(long maxBytes, int keepDim, int keepX, int keepZ, int keepRadius)
    {
        lock (_db.Gate)
        {
            long total = _db.ScalarLong("SELECT COALESCE(SUM(bytes),0) FROM world_chunks");
            if (total <= maxBytes) return 0;
            var victims = _db.Query("""
                SELECT dim,cx,cz,bytes FROM world_chunks
                WHERE NOT (dim=$d AND abs(cx-$x) <= $r AND abs(cz-$z) <= $r)
                ORDER BY last_seen ASC LIMIT 4096
                """, r => (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3)), ("$d", keepDim), ("$x", keepX), ("$z", keepZ), ("$r", keepRadius));
            int n = 0;
            _db.Tx(() =>
            {
                foreach (var (d, x, z, b) in victims)
                {
                    if (total <= maxBytes) break;
                    _db.Exec("DELETE FROM world_chunks WHERE dim=$d AND cx=$x AND cz=$z", ("$d", d), ("$x", x), ("$z", z));
                    total -= b;
                    n++;
                }
            });
            return n;
        }
    }

    public IReadOnlyList<SceneryRow> Scenery(int dim)
    {
        lock (_db.Gate)
            return _db.Query("""
                SELECT dim,key,kind,min_x,min_y,min_z,max_x,max_y,max_z,blocks,announced_at,asked_at,last_seen FROM scenery WHERE dim=$d
                """, r => new SceneryRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5),
                    r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetString(9),
                    AgentDb.LongOrNull(r, 10) is { } a ? AgentDb.FromUnixMs(a) : null,
                    AgentDb.LongOrNull(r, 11) is { } q ? AgentDb.FromUnixMs(q) : null, AgentDb.FromUnixMs(r.GetInt64(12))), ("$d", dim));
    }

    public void PutScenery(SceneryRow s, string? replacesKey = null)
    {
        lock (_db.Gate)
            _db.Tx(() =>
            {
                if (replacesKey != null && replacesKey != s.Key)
                    _db.Exec("DELETE FROM scenery WHERE dim=$d AND key=$k", ("$d", s.Dim), ("$k", replacesKey));
                _db.Exec("""
                    INSERT INTO scenery(dim,key,kind,min_x,min_y,min_z,max_x,max_y,max_z,blocks,announced_at,asked_at,last_seen)
                    VALUES($d,$k,$kind,$x0,$y0,$z0,$x1,$y1,$z1,$b,$a,$q,$t)
                    ON CONFLICT(dim,key) DO UPDATE SET kind=$kind, min_x=$x0, min_y=$y0, min_z=$z0, max_x=$x1, max_y=$y1, max_z=$z1,
                        blocks=$b, announced_at=$a, asked_at=$q, last_seen=$t
                    """, ("$d", s.Dim), ("$k", s.Key), ("$kind", s.Kind), ("$x0", s.MinX), ("$y0", s.MinY), ("$z0", s.MinZ),
                    ("$x1", s.MaxX), ("$y1", s.MaxY), ("$z1", s.MaxZ), ("$b", s.Blocks),
                    ("$a", s.AnnouncedAt is { } a ? AgentDb.ToUnixMs(a) : null), ("$q", s.AskedAt is { } q ? AgentDb.ToUnixMs(q) : null),
                    ("$t", AgentDb.ToUnixMs(s.LastSeen)));
            });
    }
}

/// <summary>In-process store (tests, hosts without a db).</summary>
public sealed class InMemoryWorldMemoryStore : IWorldMemoryStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(int, int, int), ChunkRow> _chunks = new();
    private readonly Dictionary<(int, string), SceneryRow> _scenery = new();

    public int ChunkWrites { get; private set; }

    public ChunkRow? GetChunk(int dim, int x, int z) { lock (_lock) return _chunks.GetValueOrDefault((dim, x, z)); }

    public (long Hash, DateTimeOffset LastSeen)? GetChunkHash(int dim, int x, int z)
    {
        lock (_lock) return _chunks.TryGetValue((dim, x, z), out var c) ? (c.Hash, c.LastSeen) : null;
    }

    public IReadOnlyList<ChunkRow> Chunks(int dim, int nearX, int nearZ, int max)
    {
        lock (_lock)
            return _chunks.Values.Where(c => c.Dim == dim)
                .OrderBy(c => (long)(c.X - nearX) * (c.X - nearX) + (long)(c.Z - nearZ) * (c.Z - nearZ)).Take(max).ToList();
    }

    public void PutChunks(IReadOnlyList<ChunkRow> rows)
    {
        lock (_lock) foreach (var r in rows) { _chunks[(r.Dim, r.X, r.Z)] = r; ChunkWrites++; }
    }

    public void TouchChunks(int dim, IReadOnlyList<(int X, int Z)> cells, DateTimeOffset at)
    {
        lock (_lock)
            foreach (var (x, z) in cells)
                if (_chunks.TryGetValue((dim, x, z), out var c)) _chunks[(dim, x, z)] = c with { LastSeen = at };
    }

    private static long Bytes(ChunkRow c) => c.Data.Length + c.Summary.Length + 32;

    public long TotalBytes() { lock (_lock) return _chunks.Values.Sum(Bytes); }

    public int Evict(long maxBytes, int keepDim, int keepX, int keepZ, int keepRadius)
    {
        lock (_lock)
        {
            long total = _chunks.Values.Sum(Bytes);
            int n = 0;
            foreach (var c in _chunks.Values.Where(c => !(c.Dim == keepDim && Math.Abs(c.X - keepX) <= keepRadius && Math.Abs(c.Z - keepZ) <= keepRadius))
                         .OrderBy(c => c.LastSeen).ToList())
            {
                if (total <= maxBytes) break;
                _chunks.Remove((c.Dim, c.X, c.Z));
                total -= Bytes(c);
                n++;
            }
            return n;
        }
    }

    public IReadOnlyList<SceneryRow> Scenery(int dim) { lock (_lock) return _scenery.Values.Where(s => s.Dim == dim).ToList(); }

    public void PutScenery(SceneryRow row, string? replacesKey = null)
    {
        lock (_lock)
        {
            if (replacesKey != null) _scenery.Remove((row.Dim, replacesKey));
            _scenery[(row.Dim, row.Key)] = row;
        }
    }
}
