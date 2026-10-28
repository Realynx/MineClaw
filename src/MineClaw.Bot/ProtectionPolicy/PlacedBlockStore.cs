using System.Collections.Concurrent;
using MineClaw.Agent.Rag;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot;

/// <summary>
/// Persists the client's <see cref="PlacedBlockLog"/> in the shared db (<c>placed_blocks</c>) and keeps it in step with the
/// other hive processes: protection rule 1 (a bot can always take back what any hive bot placed) survives restarts and works
/// across clones. Block-change handlers only enqueue (network thread); <see cref="Flush"/> writes in one transaction and
/// <see cref="Sync"/> re-reads when another process changed the table. A timer drives both (persistent hosts).
/// </summary>
public sealed class PlacedBlockStore : IDisposable
{
    private readonly AgentDb _db;
    private readonly PlacedBlockLog _log;
    private readonly ILogger? _logger;
    private readonly ConcurrentQueue<(bool Add, PlacedBlock? Row, int Dim, BlockPos Pos)> _pending = new();
    private (long Count, long Max) _seen = (-1, -1);
    private System.Threading.Timer? _timer;

    public PlacedBlockStore(AgentDb db, PlacedBlockLog log, ILogger? logger = null)
    {
        _db = db;
        _log = log;
        _logger = logger;
        lock (_db.Gate)
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS placed_blocks(dim INTEGER NOT NULL, x INTEGER NOT NULL, y INTEGER NOT NULL, z INTEGER NOT NULL,
                    block INTEGER NOT NULL, bot TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(dim, x, y, z));
                CREATE INDEX IF NOT EXISTS ix_placed_blocks_at ON placed_blocks(at);
                """);
        _log.Added += r => _pending.Enqueue((true, r, r.Dimension, r.Pos));
        _log.Removed += (d, p) => _pending.Enqueue((false, null, d, p));
        Sync(force: true);
    }

    private readonly object _flushLock = new();

    /// <summary>
    /// One brain, many bodies: a clone body's placements go into the same shared table (rule 1: own/hive placements). Its
    /// client's reader thread only enqueues; the one timer writes.
    /// </summary>
    public void Watch(PlacedBlockLog other)
    {
        if (ReferenceEquals(other, _log)) return;
        other.Added += r => _pending.Enqueue((true, r, r.Dimension, r.Pos));
        other.Removed += (d, p) => _pending.Enqueue((false, null, d, p));
    }

    /// <summary>Rows in the table (tests/diagnostics).</summary>
    public long Rows { get { lock (_db.Gate) return _db.ScalarLong("SELECT COUNT(*) FROM placed_blocks"); } }

    public PlacedBlockStore Start(TimeSpan? every = null)
    {
        var t = every ?? TimeSpan.FromSeconds(2);
        _timer ??= new System.Threading.Timer(_ => { Flush(); Sync(); }, null, t, t);
        return this;
    }

    /// <summary>Writes queued placements/removals (one transaction).</summary>
    public int Flush()
    {
        if (_pending.IsEmpty) return 0;
        int n = 0;
        lock (_flushLock)
        try
        {
            lock (_db.Gate)
                _db.Tx(() =>
                {
                    while (_pending.TryDequeue(out var op))
                    {
                        n++;
                        if (op.Add && op.Row is { } r)
                            _db.Exec("""
                                INSERT INTO placed_blocks(dim,x,y,z,block,bot,at) VALUES($d,$x,$y,$z,$b,$bot,$t)
                                ON CONFLICT(dim,x,y,z) DO UPDATE SET block=$b, bot=$bot, at=$t
                                """, ("$d", r.Dimension), ("$x", r.Pos.X), ("$y", r.Pos.Y), ("$z", r.Pos.Z), ("$b", r.BlockId), ("$bot", r.Bot),
                                ("$t", AgentDb.ToUnixMs(r.At)));
                        else
                            _db.Exec("DELETE FROM placed_blocks WHERE dim=$d AND x=$x AND y=$y AND z=$z",
                                ("$d", op.Dim), ("$x", op.Pos.X), ("$y", op.Pos.Y), ("$z", op.Pos.Z));
                    }
                    // Bounded like the in-memory log: the oldest rows go first.
                    _db.Exec($"DELETE FROM placed_blocks WHERE rowid NOT IN (SELECT rowid FROM placed_blocks ORDER BY at DESC LIMIT {PlacedBlockLog.Capacity})");
                });
            _seen = Signature();
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "placed_blocks flush failed"); }
        return n;
    }

    /// <summary>Re-reads the table when another process changed it (or <paramref name="force"/>). Local queue is flushed first.</summary>
    public bool Sync(bool force = false)
    {
        lock (_flushLock)
        try
        {
            if (!_pending.IsEmpty) Flush();
            var sig = Signature();
            if (!force && sig == _seen) return false;
            List<PlacedBlock> rows;
            lock (_db.Gate)
                rows = _db.Query("SELECT dim,x,y,z,block,bot,at FROM placed_blocks",
                    r => new PlacedBlock(new BlockPos(r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)), r.GetInt32(0), r.GetInt32(4), r.GetString(5),
                        AgentDb.FromUnixMs(r.GetInt64(6))));
            _log.Merge(rows, replace: true);
            _seen = sig;
            return true;
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "placed_blocks sync failed"); return false; }
    }

    private (long, long) Signature()
    {
        lock (_db.Gate)
        {
            var l = _db.Query("SELECT COUNT(*), COALESCE(MAX(at),0), COALESCE(SUM(x*31+y*17+z),0) FROM placed_blocks",
                r => (r.GetInt64(0), r.GetInt64(1) ^ r.GetInt64(2)));
            return l.Count == 0 ? (0, 0) : l[0];
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        Flush();
    }
}
