using System.Globalization;
using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.World;
using Microsoft.Extensions.Logging;
using C = MineClaw.Bot.WorldIndex.BlockClass.C;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// Dimensions: the indexer's state (entries, clusters, footprints, stations, signs, protection...) always describes ONE
/// dimension, <see cref="Dimension"/>. Every write is stamped with it. On a switch (0x09 with another dimension) the old
/// dimension's pending writes are flushed, all of that in-memory state is dropped (stored rows stay; the next visit
/// reconciles against them like after a restart) and indexing starts fresh for the new one. Islands exist only in the
/// overworld; the nether gets a lava/fire area summary instead. Portals are indexed in both, and going through one
/// records the entry and exit as a linked pair.
/// </summary>
public sealed partial class WorldIndexer
{
    private readonly IDimensionSource _dims;
    private volatile int _dim;
    private int _switchFrom = int.MinValue, _switchTo = int.MinValue;
    private readonly ClusterIndex _portals = new(1);

    /// <summary>The dimension the indexer's state describes (0 overworld, -1 nether).</summary>
    public int Dimension => _dim;

    /// <summary>Raised on the indexer thread after a switch was processed (from, to).</summary>
    public event Action<int, int>? DimensionSwitched;

    private bool InNether => _dim == WorldDim.Nether;
    /// <summary>Void islands only make sense over the overworld's void.</summary>
    private bool IslandsOn => _dim == WorldDim.Overworld;

    private void OnDimensionEvent(int from, int to)
    {
        Volatile.Write(ref _switchFrom, from);
        Volatile.Write(ref _switchTo, to);
    }

    /// <summary>Start of every pass: notices a switch (event or poll) and resets per-dimension state.</summary>
    private void CheckDimension(DateTimeOffset now)
    {
        int to = _dims.Dimension;
        int evTo = Interlocked.Exchange(ref _switchTo, int.MinValue);
        Interlocked.Exchange(ref _switchFrom, int.MinValue);
        if (evTo != int.MinValue) to = _dims.Dimension; // the event only wakes us; the live value wins
        if (to == _dim) return;
        int from = _dim;
        SwitchDimension(from, to, now);
    }

    private void SwitchDimension(int from, int to, DateTimeOffset now)
    {
        // Close the old dimension: removals + whatever still renders (most can't: the client already dropped its chunks).
        Write(now);
        var departed = _departure is { } d && d.End.Dim == from && now - d.At <= TimeSpan.FromSeconds(15) ? d.End : null;
        ResetDimensionState();
        _dim = to;
        _arrival = departed != null ? (departed, now) : null;
        Stats.DimensionSwitches++;
        _log.LogInformation("world index: dimension {From} -> {To}{Portal}", WorldDim.Name(from), WorldDim.Name(to),
            departed != null ? $" through portal {departed.Pos}" : "");
        // Columns of the new dimension that loaded before this pass noticed the switch.
        foreach (var c in _world.Columns)
            if (c.HasData) MarkColumn(c.ChunkX, c.ChunkZ);
        try { DimensionSwitched?.Invoke(from, to); }
        catch (Exception ex) { _log.LogDebug(ex, "DimensionSwitched handler failed"); }
    }

    /// <summary>Drops everything describing the dimension the bot left (nothing stored is touched).</summary>
    private void ResetDimensionState()
    {
        while (_changes.TryDequeue(out _)) { }          // queued for the old world; applying them to the new one would lie
        while (_containerEvents.TryDequeue(out _)) { }
        _entries.Clear();
        _scannedColumns.Clear();
        _pendingClusterRows.Clear();
        _removals.Clear();
        _foot.Clear();
        _islands.Clear(); _gens.Clear(); _trees.Clear(); _builds.Clear(); _portals.Clear();
        foreach (var f in _farms.Values) f.Clear();
        _dug.Clear(); _regens.Clear();
        _stationCells.Clear(); _stationsByChunk.Clear(); _signsByChunk.Clear(); _signText.Clear(); _dirtySigns.Clear();
        _open = null;
        _grid.Clear(); _large.Clear(); _adopted.Clear();
        _mainIsland = null;
        _dwell.Clear(); _lastSample = default;
        _treeNotes.Clear();
        _hazCols.Clear(); _hazDirty.Clear();
        _departure = null;
        lock (_unknownLock) _pendingUnknown.Clear();
        _protected = new Dictionary<BlockPos, string>();
        _floorByChunk.Clear();
        _protectDirty = true;
    }

    // ------------------------------------------------------------------ portals

    private (PortalEnd End, DateTimeOffset At)? _departure;
    private (PortalEnd From, DateTimeOffset Since)? _arrival;

    /// <summary>The bottom portal cell the bot's feet/head are in, or null.</summary>
    private BlockPos? PortalCellAtBot()
    {
        var feet = _world.Player.Position.ToBlockPos();
        BlockPos? cell = _world.GetBlock(feet) == Blocks.Portal ? feet : _world.GetBlock(feet.Up()) == Blocks.Portal ? feet.Up() : null;
        if (cell is not { } c) return null;
        while (c.Y > 0 && _world.GetBlock(c.Down()) == Blocks.Portal) c = c.Down();
        return c;
    }

    /// <summary>Portal blocks connected to <paramref name="start"/> (one portal's sheet, bounded).</summary>
    private HashSet<BlockPos> PortalSheet(BlockPos start)
    {
        var set = new HashSet<BlockPos> { start };
        var q = new Queue<BlockPos>();
        q.Enqueue(start);
        while (q.Count > 0 && set.Count < 64)
        {
            var c = q.Dequeue();
            foreach (var o in Six)
            {
                var n = c + o;
                if (_world.GetBlock(n) == Blocks.Portal && set.Add(n)) q.Enqueue(n);
            }
        }
        return set;
    }

    private PortalEnd EndAt(BlockPos cell)
    {
        var key = _portals.OwnerOf(cell) ?? PosKey(MinOf(PortalSheet(cell)));
        return new PortalEnd(_dim, key, cell.X, cell.Y, cell.Z);
    }

    /// <summary>Every pass: remember the portal the bot stands in (it may be gone with the world on the next pass), and
    /// finish a pending pair once the bot is standing in the exit portal of the new dimension.</summary>
    private void TrackPortals(DateTimeOffset now)
    {
        var cell = PortalCellAtBot();
        if (_arrival is { } a)
        {
            if (now - a.Since > TimeSpan.FromSeconds(30)) _arrival = null; // never saw the exit (died, kicked, teleported)
            else if (cell is { } exitCell && a.From.Dim != _dim)
            {
                var exit = EndAt(exitCell);
                _arrival = null;
                try
                {
                    _sink.LinkPortals(a.From, exit);
                    Stats.PortalLinks++;
                    _log.LogInformation("portal pair: {FromDim} {From} <-> {ToDim} {To}", WorldDim.Name(a.From.Dim), a.From.Pos, WorldDim.Name(exit.Dim), exit.Pos);
                }
                catch (Exception ex) { _log.LogWarning(ex, "portal link failed"); }
                _portals.Add(exitCell); // index the exit at once (its column scan may not have run yet)
                foreach (var p in PortalSheet(exitCell)) _portals.Add(p);
                _departure = (exit, now); // standing in it: going straight back is a trip too
                return;
            }
        }
        if (cell is { } c && (_departure is not { } d || d.End.X != c.X || d.End.Y != c.Y || d.End.Z != c.Z || d.End.Dim != _dim))
            _departure = (EndAt(c), now);
        else if (cell is not null && _departure is { } keep) _departure = (keep.End, now);
    }

    private void ApplyPortal(ClusterChange c)
    {
        var e = ClusterEntry(IndexKinds.Portal, c, out _);
        var (min, max) = (MinOf(c.Cells), MaxOf(c.Cells));
        bool alongX = max.X > min.X || (max.Z == min.Z && _world.GetBlock(min.Offset(1, 0, 0)) == Blocks.Obsidian);
        var frameMin = new BlockPos(min.X - (alongX ? 1 : 0), min.Y - 1, min.Z - (alongX ? 0 : 1));
        var frameMax = new BlockPos(max.X + (alongX ? 1 : 0), max.Y + 1, max.Z + (alongX ? 0 : 1));
        e.Min = frameMin;
        e.Max = frameMax;
        e.Anchor = min;
        // A frame flagged as an "unknown build" before the sheet was lit is re-judged (now explained by the portal).
        for (int x = frameMin.X; x <= frameMax.X; x++)
        for (int y = frameMin.Y; y <= frameMax.Y; y++)
        for (int z = frameMin.Z; z <= frameMax.Z; z++)
            if (_builds.Contains(new BlockPos(x, y, z))) _builds.Touch(new BlockPos(x, y, z));
        // Protect the sheet and its obsidian frame: breaking either strands the bot in the other dimension.
        var protect = new HashSet<BlockPos>(c.Cells);
        for (int x = frameMin.X; x <= frameMax.X; x++)
        for (int y = frameMin.Y; y <= frameMax.Y; y++)
        for (int z = frameMin.Z; z <= frameMax.Z; z++)
            if (_world.GetBlock(x, y, z) == Blocks.Obsidian) protect.Add(new BlockPos(x, y, z));
        SetProtect(e, protect);
        // Exact standing spot: a bottom portal cell with solid ground under it (walking into it starts the trip).
        var stand = c.Cells.Where(p => p.Y == min.Y).OrderBy(p => p.X + p.Z).FirstOrDefault();
        // Approach spot: free 2-high cell on solid ground right in front of the sheet.
        BlockPos? approach = null;
        foreach (var b in c.Cells.Where(p => p.Y == min.Y))
        foreach (var off in alongX ? new[] { new BlockPos(0, 0, 1), new BlockPos(0, 0, -1) } : new[] { new BlockPos(1, 0, 0), new BlockPos(-1, 0, 0) })
        {
            var s = b + off;
            if (!BlockShapes.HasCollision(_world, s.X, s.Y, s.Z) && !BlockShapes.HasCollision(_world, s.X, s.Y + 1, s.Z)
                && BlockShapes.HasCollision(_world, s.X, s.Y - 1, s.Z)) { approach ??= s; }
        }
        e.Extra.Clear();
        e.Extra["stand"] = Arr(stand);
        e.Extra["approach"] = approach is { } ap ? Arr(ap) : null;
        e.Extra["frame"] = new List<int[]> { Arr(frameMin), Arr(frameMax) };
        e.Extra["cells"] = c.Cells.Count;
    }

    // ------------------------------------------------------------------ nether hazard summary (instead of islands)

    private const int HazardRegionShift = 2; // 4x4 chunks = 64x64 blocks
    private const int LavaSeaY = 31;

    private sealed record HazCol(int LavaSea, int LavaHigh, int LavaHighMinY, int LavaHighMaxY, int Fire, int SoulSand, int Glowstone);

    private readonly Dictionary<long, HazCol> _hazCols = new();
    private readonly HashSet<long> _hazDirty = new();

    private void NoteHazardColumn(int cx, int cz, HazCol h)
    {
        _hazCols[ColKey(cx, cz)] = h;
        _hazDirty.Add(ColKey(cx >> HazardRegionShift, cz >> HazardRegionShift));
    }

    private void ApplyHazards()
    {
        if (_hazDirty.Count == 0) return;
        foreach (var rk in _hazDirty)
        {
            var (rx, rz) = ColOf(rk);
            int lavaSea = 0, lavaHigh = 0, minY = int.MaxValue, maxY = int.MinValue, fire = 0, soul = 0, glow = 0, cols = 0;
            for (int cx = rx << HazardRegionShift; cx < (rx + 1) << HazardRegionShift; cx++)
            for (int cz = rz << HazardRegionShift; cz < (rz + 1) << HazardRegionShift; cz++)
            {
                if (!_hazCols.TryGetValue(ColKey(cx, cz), out var h)) continue;
                cols++;
                lavaSea += h.LavaSea; lavaHigh += h.LavaHigh; fire += h.Fire; soul += h.SoulSand; glow += h.Glowstone;
                if (h.LavaHigh > 0) { minY = Math.Min(minY, h.LavaHighMinY); maxY = Math.Max(maxY, h.LavaHighMaxY); }
            }
            int bx = rx << (HazardRegionShift + 4), bz = rz << (HazardRegionShift + 4), size = 1 << (HazardRegionShift + 4);
            var e = GetOrCreate(IndexKinds.Hazard, string.Create(CultureInfo.InvariantCulture, $"{rx},{rz}"), out _);
            e.Anchor = new BlockPos(bx + size / 2, 64, bz + size / 2);
            e.Min = new BlockPos(bx, 0, bz);
            e.Max = new BlockPos(bx + size - 1, _world.WorldHeight - 1, bz + size - 1);
            e.Extra.Clear();
            e.Extra["chunks"] = cols;
            e.Extra["lava_sea"] = lavaSea;
            e.Extra["lava_high"] = lavaHigh;
            if (lavaHigh > 0) { e.Extra["lava_high_y"] = new[] { minY, maxY }; }
            e.Extra["fire"] = fire;
            e.Extra["soul_sand"] = soul;
            e.Extra["glowstone"] = glow;
            e.MinWriteInterval = TimeSpan.FromSeconds(Math.Max(_o.ClusterMinWriteSeconds, 30));
            e.Dirty = true;
            e.Seen = true;
        }
        _hazDirty.Clear();
    }

    private static string RenderHazard(IndexEntry e, Dictionary<string, object?> f)
    {
        var x = e.Extra;
        var parts = new List<string>();
        if (x["lava_sea"] is int sea && sea > 0) parts.Add($"lava sea y<={LavaSeaY}");
        if (x["lava_high"] is int hi && hi > 0)
            parts.Add($"lava {hi} above it" + (x.GetValueOrDefault("lava_high_y") is int[] ys ? $" (y{ys[0]}-{ys[1]})" : ""));
        if (x["fire"] is int fire && fire > 0) parts.Add($"fire {fire}");
        if (x["soul_sand"] is int soul && soul > 0) parts.Add($"soul sand {soul}");
        if (x["glowstone"] is int glow && glow > 0) parts.Add($"glowstone {glow}");
        foreach (var (k, v) in x) f[k] = v;
        return $"nether area {P(e.Min)}..{P(e.Max)} ({x["chunks"]}/16 chunks seen): " + (parts.Count > 0 ? string.Join(", ", parts) : "no lava or fire seen");
    }

    /// <summary>Per-column hazard counts during a nether scan (lava sea vs lava above it, fire, soul sand, glowstone).</summary>
    private static HazCol ScanHazards(ChunkColumn col)
    {
        int sea = 0, high = 0, minY = int.MaxValue, maxY = int.MinValue, fire = 0, soul = 0, glow = 0, height = col.Height;
        for (int lx = 0; lx < 16; lx++)
        for (int lz = 0; lz < 16; lz++)
        for (int y = 0; y < height; y++)
        {
            int id = col.GetBlock(lx, y, lz);
            switch (id)
            {
                case Blocks.Lava or Blocks.FlowingLava:
                    if (y <= LavaSeaY) sea++;
                    else { high++; minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                    break;
                case Blocks.Fire: fire++; break;
                case Blocks.SoulSand: soul++; break;
                case Blocks.Glowstone: glow++; break;
            }
        }
        return new HazCol(sea, high, minY, maxY, fire, soul, glow);
    }

    /// <summary>Unusual-block classification for this dimension (natural nether terrain is not a player build there).</summary>
    private bool IsBuildBlock(int id, C c) => (c & C.Build) != 0 && !(InNether && BlockClass.NaturalNether(id));
}
