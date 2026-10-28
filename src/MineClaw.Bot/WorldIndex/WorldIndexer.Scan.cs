using System.Diagnostics;
using MineClaw.Data;
using MineClaw.World;
using C = MineClaw.Bot.WorldIndex.BlockClass.C;

namespace MineClaw.Bot.WorldIndex;

public sealed partial class WorldIndexer
{
    /// <summary>Per island-footprint column: vertical extent and block histogram (packed id&lt;&lt;16 | count).</summary>
    private sealed record Foot(int MinY, int MaxY, int[] Hist);

    private readonly Dictionary<long, Foot> _foot = new();
    private readonly ClusterIndex _islands = new(1, flat: true);
    private readonly ClusterIndex _gens = new(2);
    private readonly ClusterIndex _trees = new(2);
    private readonly ClusterIndex _builds = new(1);
    private readonly Dictionary<string, ClusterIndex> _farms;
    private readonly Dictionary<BlockPos, DateTimeOffset> _dug = new();
    private readonly Dictionary<BlockPos, int> _regens = new();
    private readonly int[] _hist = new int[256];

    private static readonly BlockPos[] Six = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1)];
    /// <summary>Directions lava can flow into a cell from / water can harden it from (b1.8: four sides and above).</summary>
    private static readonly BlockPos[] FlowIn = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1), new(0, 1, 0)];

    private static long XZ(int x, int z) => ((long)x << 32) | (uint)z;

    // ------------------------------------------------------------------ column scans

    private void ScanDirtyColumns(DateTimeOffset now)
    {
        List<long> pick;
        lock (_columnsLock)
        {
            if (_dirtyColumns.Count == 0) return;
            var me = _world.Player.Position;
            int px = (int)Math.Floor(me.X) >> 4, pz = (int)Math.Floor(me.Z) >> 4;
            pick = _dirtyColumns.Count <= _o.MaxColumnsPerPass
                ? _dirtyColumns.ToList()
                : _dirtyColumns.OrderBy(k => { var (x, z) = ColOf(k); return (long)(x - px) * (x - px) + (long)(z - pz) * (z - pz); })
                    .Take(Math.Max(1, _o.MaxColumnsPerPass)).ToList();
        }
        long start = Stopwatch.GetTimestamp();
        foreach (var k in pick)
        {
            lock (_columnsLock) _dirtyColumns.Remove(k);
            var (cx, cz) = ColOf(k);
            long t0 = Stopwatch.GetTimestamp();
            if (ScanColumn(cx, cz, now))
            {
                Stats.ColumnsScanned++;
                Stats.ScanMs += ElapsedMs(t0);
            }
            if (ElapsedMs(start) > _o.ScanBudgetMs) break;
        }
    }

    /// <summary>Full scan of one 16x128x16 column: stations, signs, fluids/generators, farms, trees, unusual blocks, footprint.</summary>
    private bool ScanColumn(int cx, int cz, DateTimeOffset now)
    {
        var col = _world.GetColumn(cx, cz);
        if (col is null || !col.HasData) return false;
        bool firstScan = _scannedColumns.Add(ColKey(cx, cz));

        var stations = new List<BlockPos>();
        var signs = new HashSet<BlockPos>();
        var trees = new HashSet<BlockPos>();
        var builds = new HashSet<BlockPos>();
        var floors = new HashSet<BlockPos>();
        var portals = new HashSet<BlockPos>();
        var footCells = new HashSet<BlockPos>();
        bool islands = IslandsOn;
        var genCandidates = new List<BlockPos>();
        Dictionary<string, HashSet<BlockPos>> farms = BlockClass.FarmTypes.ToDictionary(t => t, _ => new HashSet<BlockPos>());
        int bx = cx << 4, bz = cz << 4;
        Span<int> ids = stackalloc int[256];

        for (int lx = 0; lx < 16; lx++)
        for (int lz = 0; lz < 16; lz++)
        {
            int x = bx + lx, z = bz + lz;
            int minY = -1, maxY = -1, distinct = 0;
            int below = 0;
            for (int y = 0; y < WorldState.Height; y++)
            {
                int id = col.GetBlock(lx, y, lz);
                if (id == 0) { below = 0; continue; }
                var c = BlockClass.Of(id);
                if ((c & C.Ground) != 0)
                {
                    if (minY < 0) minY = y;
                    maxY = y;
                    if (_hist[id]++ == 0 && distinct < ids.Length) ids[distinct++] = id;
                }
                if (c == C.None) { below = id; continue; }
                var p = new BlockPos(x, y, z);
                if ((c & C.Station) != 0) stations.Add(p);
                if ((c & C.Sign) != 0) signs.Add(p);
                if ((c & C.Portal) != 0) portals.Add(p);
                if ((c & C.Farm) != 0 && !(InNether && BlockClass.NaturalNether(id))) farms[BlockClass.FarmType(id)!].Add(p);
                if ((c & C.Tree) != 0) trees.Add(p);
                if (IsBuildBlock(id, c)) builds.Add(p); // natural nether terrain isn't a build there
                else if (id == Blocks.Cobblestone && (below == Blocks.Cobblestone || (y + 1 < WorldState.Height && col.GetBlock(lx, y + 1, lz) == Blocks.Cobblestone)))
                    builds.Add(p); // walls/pillars, not one-thick floors
                if (IsFloorCandidate(id, c)) floors.Add(p); // gen cells are dropped when protection is rebuilt
                if ((c & C.GenOutput) != 0 && IsGenCell(p, id, false)) genCandidates.Add(p);
                else if ((c & C.Lava) != 0)
                    foreach (var o in FlowIn)
                    {
                        var q = new BlockPos(x - o.X, y - o.Y, z - o.Z); // cells this lava can flow into
                        if (IsGenCell(q, _world.GetBlock(q), false)) genCandidates.Add(q);
                    }
                below = id;
            }
            var cell = new BlockPos(x, 0, z);
            if (!islands)
            {
                for (int i = 0; i < distinct; i++) _hist[ids[i]] = 0;
                continue;
            }
            if (minY >= 0)
            {
                var hist = new int[distinct];
                for (int i = 0; i < distinct; i++) { hist[i] = (ids[i] << 16) | Math.Min(_hist[ids[i]], 0xFFFF); _hist[ids[i]] = 0; }
                SetFoot(cell, new Foot(minY, maxY, hist));
                footCells.Add(cell);
            }
            else _foot.Remove(XZ(x, z));
        }

        if (islands) _islands.ReplaceChunk(cx, cz, footCells);
        else if (InNether) NoteHazardColumn(cx, cz, ScanHazards(col));
        _trees.ReplaceChunk(cx, cz, trees);
        _builds.ReplaceChunk(cx, cz, builds);
        ReplaceFloors(cx, cz, floors);
        _portals.ReplaceChunk(cx, cz, portals);
        foreach (var (t, set) in farms) _farms[t].ReplaceChunk(cx, cz, set);

        // Generators: re-check known cells here, add new candidates (which may lie in a neighbouring column).
        foreach (var g in _gens.CellsInChunk(cx, cz).ToList()) _gens.Set(g, IsGenCell(g, _world.GetBlock(g), true));
        foreach (var g in genCandidates) _gens.Add(g);

        // Stations: everything previously known in this column is re-evaluated, then everything found.
        if (_stationsByChunk.TryGetValue(ColKey(cx, cz), out var known))
            foreach (var p in known.ToList()) EvaluateStation(p);
        foreach (var p in stations) EvaluateStation(p, seen: true);

        if (_signsByChunk.TryGetValue(ColKey(cx, cz), out var knownSigns))
            foreach (var p in knownSigns.Where(p => !signs.Contains(p)).ToList()) EvaluateSign(p);
        foreach (var p in signs) EvaluateSign(p);

        MarkSeenInColumn(cx, cz);
        if (firstScan) QueueReconcile(cx, cz);
        return true;
    }

    private void SetFoot(BlockPos cell, Foot f)
    {
        var k = XZ(cell.X, cell.Z);
        if (_foot.TryGetValue(k, out var old) && old.MinY == f.MinY && old.MaxY == f.MaxY && old.Hist.AsSpan().SequenceEqual(f.Hist)) return;
        _foot[k] = f;
        _islands.Touch(cell);
    }

    private void UpdateFootColumn(int x, int z)
    {
        int minY = -1, maxY = -1;
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < WorldState.Height; y++)
        {
            int id = _world.GetBlock(x, y, z);
            if (id == 0 || (BlockClass.Of(id) & C.Ground) == 0) continue;
            if (minY < 0) minY = y;
            maxY = y;
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        var cell = new BlockPos(x, 0, z);
        if (minY < 0)
        {
            _foot.Remove(XZ(x, z));
            _islands.Remove(cell);
            return;
        }
        SetFoot(cell, new Foot(minY, maxY, counts.Select(kv => (kv.Key << 16) | Math.Min(kv.Value, 0xFFFF)).ToArray()));
        _islands.Add(cell);
    }

    // ------------------------------------------------------------------ single block changes

    private void HandleChange(BlockChange ch, DateTimeOffset now)
    {
        var p = ch.Pos;
        TrackRegen(ch, now);
        bool queued;
        lock (_columnsLock) queued = _dirtyColumns.Contains(ColKey(p.X >> 4, p.Z >> 4));
        if (queued) return; // the full scan covers it

        if (IslandsOn && ((BlockClass.Of(ch.OldId) | BlockClass.Of(ch.NewId)) & C.Ground) != 0) UpdateFootColumn(p.X, p.Z);
        if (ch.OldId == Blocks.Portal || ch.NewId == Blocks.Portal) _portals.Set(p, ch.NewId == Blocks.Portal);

        if (((BlockClass.Of(ch.OldId) | BlockClass.Of(ch.NewId)) & C.Station) != 0)
        {
            EvaluateStation(p, seen: true);
            if (ch.OldId == Blocks.Chest || ch.NewId == Blocks.Chest)
                foreach (var o in Six)
                    if (o.Y == 0 && _world.GetBlock(p + o) == Blocks.Chest) EvaluateStation(p + o);
            if (ch.OldId == Blocks.Bed || ch.NewId == Blocks.Bed)
                foreach (var o in Six)
                    if (o.Y == 0 && _world.GetBlock(p + o) == Blocks.Bed) EvaluateStation(p + o);
        }
        if (((BlockClass.Of(ch.OldId) | BlockClass.Of(ch.NewId)) & C.Sign) != 0) EvaluateSign(p);

        // Generators: this cell and the cells it can affect.
        _gens.Set(p, IsGenCell(p, ch.NewId, _gens.Contains(p)));
        foreach (var o in Six)
        {
            var q = p + o;
            int id = _world.GetBlock(q);
            if (_gens.Contains(q) || (BlockClass.Of(id) & C.GenOutput) != 0) _gens.Set(q, IsGenCell(q, id, _gens.Contains(q)));
        }
        if (_gens.Contains(p)) _gens.Touch(p);

        foreach (var t in BlockClass.FarmTypes)
        {
            var layer = _farms[t];
            bool member = BlockClass.FarmType(ch.NewId) == t && !(InNether && BlockClass.NaturalNether(ch.NewId));
            layer.Set(p, member);
            if (member) layer.Touch(p); // crops growing: re-render (throttled)
            else if (BlockClass.FarmType(ch.OldId) == t) layer.Touch(p);
        }
        // Water/sand/dirt next to a farm changes its irrigation/soil description.
        if (Blocks.IsWater(ch.OldId) || Blocks.IsWater(ch.NewId))
            foreach (var o in Six) foreach (var layer in _farms.Values) layer.Touch(p + o);
        _trees.Set(p, (BlockClass.Of(ch.NewId) & C.Tree) != 0);
        _builds.Set(p, IsBuildCell(p, ch.NewId));
        SetFloor(p, !_gens.Contains(p) && IsFloorCandidate(ch.NewId, BlockClass.Of(ch.NewId)));
        foreach (var q in new[] { p.Up(), p.Down() })
        {
            int id = _world.GetBlock(q);
            if (id == Blocks.Cobblestone || _builds.Contains(q)) _builds.Set(q, IsBuildCell(q, id));
        }
    }

    // ------------------------------------------------------------------ placed floors (live 10-27 15:55)

    private readonly Dictionary<long, HashSet<BlockPos>> _floorByChunk = new();

    /// <summary>
    /// A placed solid block that may be someone's floor, wall or path: cobblestone/stone (on Skyblock only generators make
    /// them) and every build block. One-thick floors included: "mine cobble" once dug the island floor next to the dual gen
    /// because cobble only counted as a build in walls 2+ high. Floors are protected (kind "floor": soft, never announced as an
    /// unknown structure); a generator's dig cells never are.
    /// </summary>
    private bool IsFloorCandidate(int id, C c)
    {
        if (id == 0 || InNether && BlockClass.NaturalNether(id)) return false;
        if (!Blocks.Get(id).Solid || Blocks.Get(id).Interactable) return false;
        return id is Blocks.Cobblestone or Blocks.Stone or Blocks.MossyCobblestone || IsBuildBlock(id, c);
    }

    private void ReplaceFloors(int cx, int cz, HashSet<BlockPos> floors)
    {
        long k = ColKey(cx, cz);
        if (_floorByChunk.TryGetValue(k, out var old) && old.SetEquals(floors)) return;
        if (floors.Count == 0) _floorByChunk.Remove(k);
        else _floorByChunk[k] = floors;
        _protectDirty = true;
    }

    private void SetFloor(BlockPos p, bool floor)
    {
        long k = ColKey(p.X >> 4, p.Z >> 4);
        if (!_floorByChunk.TryGetValue(k, out var set))
        {
            if (!floor) return;
            _floorByChunk[k] = set = new HashSet<BlockPos>();
        }
        if (floor ? set.Add(p) : set.Remove(p)) _protectDirty = true;
    }

    private IEnumerable<BlockPos> FloorCells() => _floorByChunk.Values.SelectMany(s => s);

    private bool IsBuildCell(BlockPos p, int id)
    {
        if (IsBuildBlock(id, BlockClass.Of(id))) return true;
        return id == Blocks.Cobblestone && (_world.GetBlock(p.Down()) == Blocks.Cobblestone || _world.GetBlock(p.Up()) == Blocks.Cobblestone);
    }

    /// <summary>
    /// A cell where flowing lava meets water and hardens: it holds (or, once dug, held) cobblestone/stone and has lava
    /// flowing in from a side/above and water touching a side/above. <paramref name="sticky"/> keeps a known cell while
    /// it is momentarily air/lava after a dig.
    /// </summary>
    private bool IsGenCell(BlockPos p, int id, bool sticky)
    {
        if ((uint)p.Y >= WorldState.Height) return false;
        bool output = (BlockClass.Of(id) & C.GenOutput) != 0;
        if (!output && !(sticky && (id == 0 || Blocks.IsLava(id) || Blocks.IsWater(id)))) return false;
        bool lava = false, water = false;
        foreach (var o in FlowIn)
        {
            int n = _world.GetBlock(p.X + o.X, p.Y + o.Y, p.Z + o.Z);
            lava |= Blocks.IsLava(n);
            water |= Blocks.IsWater(n);
            if (lava && water) return true;
        }
        return false;
    }

    /// <summary>Generator confirmation: a gen-ish cell dug to air that refills with cobble/stone within the window.</summary>
    private void TrackRegen(BlockChange ch, DateTimeOffset now)
    {
        var p = ch.Pos;
        bool oldOut = (BlockClass.Of(ch.OldId) & C.GenOutput) != 0, newOut = (BlockClass.Of(ch.NewId) & C.GenOutput) != 0;
        if (oldOut && !newOut && (ch.NewId == 0 || Blocks.IsLava(ch.NewId)))
        {
            if (_gens.Contains(p) || IsGenCell(p, ch.OldId, true)) _dug[p] = now;
        }
        else if (newOut && _dug.TryGetValue(p, out var t))
        {
            _dug.Remove(p);
            if (now - t <= TimeSpan.FromSeconds(_o.GeneratorRegenSeconds))
            {
                _regens[p] = _regens.GetValueOrDefault(p) + 1;
                _protectDirty = true; // a confirmed dig cell leaves the protected set (and joins the dig-cell snapshot)
                _gens.Add(p);
                _gens.Touch(p);
                try { RegenConfirmed?.Invoke(p); } catch (Exception) { /* a contributor's listener never breaks the pass */ }
            }
        }
        if (_dug.Count > 256)
            foreach (var k in _dug.Where(kv => now - kv.Value > TimeSpan.FromMinutes(1)).Select(kv => kv.Key).ToList()) _dug.Remove(k);
    }

    /// <summary>A dig cell's refill was just observed (on the indexer thread). A clone body's contributor indexer forwards it
    /// to main's full indexer (<see cref="AdoptRegen"/>), so every body's gen work confirms cells for the brain.</summary>
    public event Action<BlockPos>? RegenConfirmed;

    private readonly System.Collections.Concurrent.ConcurrentQueue<BlockPos> _adoptedRegens = new();

    /// <summary>A regen another body's indexer confirmed: counted here on the next pass (any thread).</summary>
    public void AdoptRegen(BlockPos p)
    {
        _adoptedRegens.Enqueue(p);
        Volatile.Read(ref _wake).TrySetResult();
    }

    private void ApplyAdopted()
    {
        while (_adoptedRegens.TryDequeue(out var p))
        {
            _regens[p] = _regens.GetValueOrDefault(p) + 1;
            _protectDirty = true;
            _gens.Add(p);
            _gens.Touch(p);
        }
    }

    /// <summary>True when a block change at <paramref name="p"/> is a generator refilling (not a player placing).</summary>
    internal bool IsGeneratorCell(BlockPos p) { lock (_pumpLock) return _gens.Contains(p); }

    private volatile HashSet<BlockPos> _confirmedDig = new();

    /// <summary>
    /// A generator cell whose refill was observed (after anyone's dig, the bot's or a player's). Lock-free snapshot, safe in the
    /// pathfinder's inner loop; such a cell is never protected.
    /// </summary>
    public bool IsConfirmedDigCell(BlockPos p) => _confirmedDig.Contains(p);

    /// <summary>Every known generator dig cell (candidates and confirmed) near <paramref name="p"/>, nearest first.</summary>
    public IReadOnlyList<BlockPos> GeneratorCellsNear(BlockPos p, int radius = 6)
    {
        lock (_pumpLock)
            return _gens.AllCells().Where(c => Math.Abs(c.X - p.X) <= radius && Math.Abs(c.Y - p.Y) <= 3 && Math.Abs(c.Z - p.Z) <= radius)
                .OrderBy(c => c.DistanceSquaredTo(p)).ToList();
    }
}
