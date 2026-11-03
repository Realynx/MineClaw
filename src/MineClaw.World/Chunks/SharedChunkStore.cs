namespace MineClaw.World.Chunks;

/// <summary>What a <see cref="SharedChunkStore"/> holds right now.</summary>
/// <param name="Columns">Distinct columns in the store.</param>
/// <param name="References">Column references over all views (a column two bodies hold counts twice).</param>
/// <param name="Views">Worlds currently holding at least one column.</param>
/// <param name="AllocatedSections">16³ sections allocated over all columns and layers.</param>
/// <param name="ApproximateBytes">Approximate managed bytes of the columns (<see cref="ChunkColumn.ApproximateBytes"/>).</param>
public readonly record struct SharedChunkStoreStats(int Columns, int References, int Views, long AllocatedSections, long ApproximateBytes)
{
    public override string ToString() =>
        $"{Columns} columns ({References} refs, {Views} views), {AllocatedSections} sections, ~{ApproximateBytes / 1048576.0:F1} MiB";
}

/// <summary>
/// One chunk-column store per process, shared by the in-process bodies' <see cref="WorldState"/>s (each on its own connection):
/// columns keyed by (dimension, chunk x, chunk z) and reference-counted by the views holding them, so bodies in the same area
/// share one <see cref="ChunkColumn"/> instead of each keeping a copy. A world built with the store acquires a column when its
/// connection loads it and releases it on unload, dimension change (<see cref="WorldState.Clear"/>) and
/// <see cref="WorldState.Dispose"/>; a column nobody holds leaves the store (it is never handed out again, so no view sees
/// stale data). One game version per process: every world on a store has the same height. Thread-safe.
/// </summary>
public sealed class SharedChunkStore
{
    private readonly Dictionary<(int Dim, int X, int Z), ChunkColumn> _columns = new();
    private readonly Dictionary<WorldState, int> _views = new(ReferenceEqualityComparer.Instance);
    private readonly object _sync = new();
    private int _height;
    private int _references;

    /// <summary>A store whose height is fixed by the first world built on it.</summary>
    public SharedChunkStore() { }

    /// <summary>A store for <paramref name="height"/>-high columns (128 b1.8.1, 256 1.12.2).</summary>
    public SharedChunkStore(int height)
    {
        if (height is not (ChunkColumn.SizeY or ChunkColumn.MaxHeight)) throw new ArgumentOutOfRangeException(nameof(height), height, "column height is 128 or 256");
        _height = height;
    }

    /// <summary>Blocks per column of every world on this store; 0 until the first world binds it.</summary>
    public int Height { get { lock (_sync) return _height; } }

    /// <summary>Lock every <see cref="LightEngine"/> on this store's worlds updates under: their BFS reads and writes the same
    /// shared light arrays.</summary>
    internal object LightSync { get; } = new();

    /// <summary>The biomes every body on this store has walked past (one memory for the crew, kept after chunks unload).</summary>
    public BiomeMemory Biomes { get; } = new();

    /// <summary>Distinct columns held.</summary>
    public int ColumnCount { get { lock (_sync) return _columns.Count; } }

    /// <summary>Approximate managed bytes of the held columns.</summary>
    public long ApproximateBytes => Stats().ApproximateBytes;

    /// <summary>A consistent snapshot of the store's size.</summary>
    public SharedChunkStoreStats Stats()
    {
        ChunkColumn[] cols;
        int refs, views;
        lock (_sync)
        {
            cols = _columns.Values.ToArray();
            refs = _references;
            views = _views.Count;
        }
        long sections = 0, bytes = 0;
        foreach (var c in cols)
        {
            sections += c.AllocatedSections;
            bytes += c.ApproximateBytes;
        }
        return new SharedChunkStoreStats(cols.Length, refs, views, sections, bytes);
    }

    /// <summary>Fixes or checks the height (a world is being built on the store).</summary>
    internal void Bind(int height)
    {
        lock (_sync)
        {
            if (_height == 0) _height = height;
            else if (_height != height)
                throw new ArgumentException($"this chunk store holds {_height}-high columns, the world is {height} high (one game version per process)", nameof(height));
        }
    }

    /// <summary>The column at (dim, cx, cz) for <paramref name="view"/>, created when no view holds it.</summary>
    /// <summary>A column some body in this process holds with data, or null (read-only peek: the crew's combined view, e.g. a
    /// hive brain asking "trees within 64" while its own body only loaded part of the area its bodies stand in).</summary>
    public ChunkColumn? Peek(int dim, int cx, int cz)
    {
        lock (_sync) return _columns.TryGetValue((dim, cx, cz), out var col) && col.HasData ? col : null;
    }

    internal ChunkColumn Acquire(WorldState view, int dim, int cx, int cz)
    {
        lock (_sync)
        {
            if (!_columns.TryGetValue((dim, cx, cz), out var col))
            {
                col = new ChunkColumn(cx, cz, _height) { Dimension = dim, SharedHolders = [] };
                _columns[(dim, cx, cz)] = col;
            }
            lock (col.Sync)
            {
                var holders = col.SharedHolders!;
                if (Array.IndexOf(holders, view) < 0)
                {
                    col.SharedHolders = [.. holders, view];
                    _references++;
                    _views[view] = _views.GetValueOrDefault(view) + 1;
                }
            }
            return col;
        }
    }

    /// <summary><paramref name="view"/> no longer holds <paramref name="col"/>; the last release removes it from the store.</summary>
    internal void Release(WorldState view, ChunkColumn col)
    {
        lock (_sync)
        {
            lock (col.Sync)
            {
                var holders = col.SharedHolders;
                if (holders is null || Array.IndexOf(holders, view) < 0) return;
                col.SharedHolders = holders.Where(h => !ReferenceEquals(h, view)).ToArray();
                view.ForgetTold(col.ChunkX, col.ChunkZ);
                _references--;
                if (_views[view] <= 1) _views.Remove(view);
                else _views[view]--;
                if (col.SharedHolders.Length == 0 && _columns.TryGetValue((col.Dimension, col.ChunkX, col.ChunkZ), out var cur) && ReferenceEquals(cur, col))
                    _columns.Remove((col.Dimension, col.ChunkX, col.ChunkZ));
            }
        }
    }
}
