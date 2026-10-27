using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

public sealed partial class WorldIndexer
{
    private readonly HashSet<string> _adopted = new(StringComparer.Ordinal);
    private string? _mainIsland;

    private static BlockPos MinOf(IEnumerable<BlockPos> cells)
    {
        int x = int.MaxValue, y = int.MaxValue, z = int.MaxValue;
        foreach (var c in cells) { x = Math.Min(x, c.X); y = Math.Min(y, c.Y); z = Math.Min(z, c.Z); }
        return new BlockPos(x, y, z);
    }

    private static BlockPos MaxOf(IEnumerable<BlockPos> cells)
    {
        int x = int.MinValue, y = int.MinValue, z = int.MinValue;
        foreach (var c in cells) { x = Math.Max(x, c.X); y = Math.Max(y, c.Y); z = Math.Max(z, c.Z); }
        return new BlockPos(x, y, z);
    }

    private static BlockPos First(IEnumerable<BlockPos> cells)
    {
        BlockPos? best = null;
        foreach (var c in cells) if (best is null || Less(c, best.Value)) best = c;
        return best ?? default;
    }

    private Func<HashSet<BlockPos>, string> NewKey(string kind, string? sub, Func<HashSet<BlockPos>, string> fresh) => cells =>
    {
        var f = fresh(cells);
        var (min, max) = (MinOf(cells), MaxOf(cells));
        if (kind == IndexKinds.Island)
        {
            int y0 = int.MaxValue, y1 = int.MinValue;
            foreach (var c in cells) if (_foot.TryGetValue(XZ(c.X, c.Z), out var ft)) { y0 = Math.Min(y0, ft.MinY); y1 = Math.Max(y1, ft.MaxY); }
            if (y0 <= y1) { min = new BlockPos(min.X, y0, min.Z); max = new BlockPos(max.X, y1, max.Z); }
        }
        var k = AdoptKey(kind, sub, min, max, f);
        if (k != f) _adopted.Add(kind + "|" + k);
        return k;
    };

    private static string PosKey(BlockPos p) => ObservationStore.KeyFor(p.X, p.Y, p.Z);

    private void ReclusterAll(DateTimeOffset now)
    {
        // Order matters: generators and farms first (they explain blocks the structure pass would otherwise flag).
        if (_gens.HasWork)
        {
            var (changed, removed) = _gens.Recluster(NewKey(IndexKinds.Generator, null, c => PosKey(First(c))));
            foreach (var k in removed) if (Find(IndexKinds.Generator, k) is { } e) RemoveEntry(e);
            foreach (var c in changed) ApplyGenerator(c);
        }
        foreach (var (type, layer) in _farms)
        {
            if (!layer.HasWork) continue;
            var (changed, removed) = layer.Recluster(NewKey(IndexKinds.Farm, type, c => $"{type}@{PosKey(MinOf(c))}"));
            foreach (var k in removed) if (Find(IndexKinds.Farm, k) is { } e) RemoveEntry(e);
            foreach (var c in changed) ApplyFarm(type, c);
        }
        if (_trees.HasWork)
        {
            var (changed, removed) = _trees.Recluster(NewKey(IndexKinds.Tree, null, c => PosKey(MinOf(c))));
            foreach (var k in removed) if (Find(IndexKinds.Tree, k) is { } e) RemoveEntry(e);
            foreach (var c in changed) ApplyTree(c);
        }
        if (_builds.HasWork)
        {
            var (changed, removed) = _builds.Recluster(NewKey(IndexKinds.UnknownStructure, null, c => PosKey(MinOf(c))));
            foreach (var k in removed) if (Find(IndexKinds.UnknownStructure, k) is { } e) RemoveEntry(e);
            foreach (var c in changed) ApplyStructure(c);
        }
        if (_islands.HasWork)
        {
            var (changed, removed) = _islands.Recluster(NewKey(IndexKinds.Island, null, c =>
            {
                var (a, b) = (MinOf(c), MaxOf(c));
                return $"{(a.X + b.X) / 2},{(a.Z + b.Z) / 2}";
            }));
            foreach (var k in removed) if (Find(IndexKinds.Island, k) is { } e) RemoveEntry(e);
            foreach (var c in changed) ApplyIsland(c);
            UpdateMainIsland();
        }
    }

    private IndexEntry ClusterEntry(string kind, ClusterChange c, out bool created)
    {
        var e = GetOrCreate(kind, c.Key, out created);
        if (!created) GridRemove(e);
        e.Cells = c.Cells;
        e.Anchor = First(c.Cells);
        e.SetBounds(c.Cells);
        e.Dirty = true;
        e.Seen = true;
        e.MinWriteInterval = TimeSpan.FromSeconds(_o.ClusterMinWriteSeconds);
        GridAdd(e);
        if (created) MarkNear(e);
        return e;
    }

    // ------------------------------------------------------------------ generators

    private void ApplyGenerator(ClusterChange c)
    {
        var e = ClusterEntry(IndexKinds.Generator, c, out bool created);
        var dig = c.Cells.OrderBy(p => p.Y).ThenBy(p => p.X).ThenBy(p => p.Z).ToList();
        var lava = new HashSet<BlockPos>();
        var water = new HashSet<BlockPos>();
        foreach (var d in dig)
            foreach (var o in FlowIn)
            {
                var q = d + o;
                int id = _world.GetBlock(q);
                if (Blocks.IsLava(id)) Flood(q, Blocks.IsLava, lava, d);
                else if (Blocks.IsWater(id)) Flood(q, Blocks.IsWater, water, d);
            }
        var digSet = dig.ToHashSet();
        lava.ExceptWith(digSet); // a dug cell briefly holds flowing lava: still the dig cell
        water.ExceptWith(digSet);
        var all = dig.Concat(lava).Concat(water).ToList();
        e.SetBounds(all);
        var protect = new HashSet<BlockPos>(lava);
        protect.UnionWith(water);
        foreach (var f in lava.Concat(water))
            foreach (var o in Six)
            {
                var q = f + o;
                if (digSet.Contains(q) || protect.Contains(q)) continue;
                int id = _world.GetBlock(q);
                if (id != 0 && !Blocks.IsLava(id) && !Blocks.IsWater(id)) protect.Add(q); // the walls holding the fluids
            }
        SetProtect(e, protect);
        int regens = dig.Sum(d => _regens.GetValueOrDefault(d));
        int stone = dig.Count(d => _world.GetBlock(d) == Blocks.Stone);
        e.Extra["dig"] = dig.Select(Arr).ToList();
        e.Extra["stand"] = FindStand(dig[0], lava) is { } s ? Arr(s) : null;
        e.Extra["makes"] = stone * 2 > dig.Count ? "stone" : "cobblestone";
        e.Extra["confirmed"] = regens > 0;
        e.Extra["regens"] = regens;
        e.Extra["lava"] = lava.Count;
        e.Extra["water"] = water.Count;
        if (regens > 0) e.Force |= created || e.WrittenText?.Contains("unconfirmed") == true;
    }

    private void Flood(BlockPos start, Func<int, bool> match, HashSet<BlockPos> into, BlockPos near)
    {
        var q = new Queue<BlockPos>();
        if (into.Add(start)) q.Enqueue(start);
        while (q.Count > 0 && into.Count < 24)
        {
            var c = q.Dequeue();
            foreach (var o in Six)
            {
                var n = c + o;
                if (Math.Abs(n.X - near.X) > 3 || Math.Abs(n.Y - near.Y) > 3 || Math.Abs(n.Z - near.Z) > 3) continue;
                if (match(_world.GetBlock(n)) && into.Add(n)) q.Enqueue(n);
            }
        }
    }

    /// <summary>A safe spot to stand while mining <paramref name="dig"/>: two free cells on solid ground, no lava next to
    /// feet or head, within reach; farther from lava preferred.</summary>
    private BlockPos? FindStand(BlockPos dig, HashSet<BlockPos> lava)
    {
        BlockPos? best = null;
        double bestScore = double.MaxValue;
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dz == 0) continue;
            var s = dig.Offset(dx, dy, dz);
            if (BlockShapes.HasCollision(_world, s.X, s.Y, s.Z) || BlockShapes.HasCollision(_world, s.X, s.Y + 1, s.Z)) continue;
            if (!BlockShapes.HasCollision(_world, s.X, s.Y - 1, s.Z)) continue;
            int f = _world.GetBlock(s), h = _world.GetBlock(s.Up());
            if (Blocks.IsLava(f) || Blocks.IsWater(f) || Blocks.IsLava(h) || Blocks.IsWater(h)) continue;
            bool lavaNext = false;
            foreach (var o in Six)
                lavaNext |= Blocks.IsLava(_world.GetBlock(s + o)) || Blocks.IsLava(_world.GetBlock(s.Up() + o));
            if (lavaNext) continue;
            var eye = new Vec3d(s.X + 0.5, s.Y + PlayerPhysics.EyeHeight, s.Z + 0.5);
            if (eye.DistanceTo(dig.Center) > 4.5) continue;
            double lavaDist = lava.Count == 0 ? 9 : lava.Min(l => Math.Sqrt(l.DistanceSquaredTo(s)));
            double score = Math.Abs(eye.DistanceTo(dig.Center) - 2) - lavaDist + Math.Abs(dy) * 0.5;
            if (score < bestScore) { bestScore = score; best = s; }
        }
        return best;
    }

    private static int[] Arr(BlockPos p) => [p.X, p.Y, p.Z];

    // ------------------------------------------------------------------ farms / trees

    private void ApplyFarm(string type, ClusterChange c)
    {
        int min = type is "wheat" or "mushroom" ? 2 : 1;
        if (c.Cells.Count < min)
        {
            if (Find(IndexKinds.Farm, c.Key) is { } gone) RemoveEntry(gone);
            return;
        }
        var e = ClusterEntry(IndexKinds.Farm, c, out _);
        e.SubType = type;
        var protect = new HashSet<BlockPos>();
        int farmland = 0, crops = 0, ripe = 0, stems = 0, fruit = 0, tallest = 0, plants = 0;
        var waterCells = new HashSet<BlockPos>();
        var soilIds = new Dictionary<int, int>();
        foreach (var p in c.Cells)
        {
            int id = _world.GetBlock(p);
            switch (id)
            {
                case Blocks.Farmland: farmland++; protect.Add(p); break;
                case Blocks.Crops: crops++; if (_world.GetMeta(p) >= 7) ripe++; break;
                case Blocks.MelonStem or Blocks.PumpkinStem:
                    stems++; protect.Add(p);
                    if (_world.GetBlock(p.Down()) == Blocks.Farmland) protect.Add(p.Down());
                    break;
                case Blocks.Melon or Blocks.Pumpkin: fruit++; break;
                case Blocks.SugarCane or Blocks.Cactus:
                    plants++;
                    if (_world.GetBlock(p.Down()) != id)
                    {
                        int h = 1;
                        while (h < 8 && _world.GetBlock(p.Up(h)) == id) h++;
                        tallest = Math.Max(tallest, h);
                        var soil = p.Down();
                        protect.Add(soil);
                        int sid = _world.GetBlock(soil);
                        soilIds[sid] = soilIds.GetValueOrDefault(sid) + 1;
                        foreach (var o in Six) if (o.Y == 0 && Blocks.IsWater(_world.GetBlock(soil + o))) waterCells.Add(soil + o);
                    }
                    break;
                default: plants++; break;
            }
            if (id == Blocks.Farmland)
                for (int dx = -4; dx <= 4; dx++)
                for (int dz = -4; dz <= 4; dz++)
                    if (Math.Abs(dx) + Math.Abs(dz) <= 4 && Blocks.IsWater(_world.GetBlock(p.X + dx, p.Y, p.Z + dz))) waterCells.Add(new BlockPos(p.X + dx, p.Y, p.Z + dz));
        }
        protect.UnionWith(waterCells.Where(w => _world.GetBlock(w) == Blocks.Water)); // sources only; flowing water isn't "the" irrigation
        SetProtect(e, protect);
        e.Extra.Clear();
        e.Extra["type"] = type;
        if (farmland > 0) e.Extra["farmland"] = farmland;
        if (crops > 0) { e.Extra["crops"] = crops; e.Extra["ripe"] = ripe; }
        if (stems > 0) e.Extra["stems"] = stems;
        if (fruit > 0) e.Extra["fruit"] = fruit;
        if (plants > 0 && type is "sugar_cane" or "cactus" or "mushroom") e.Extra["plants"] = plants;
        if (tallest > 0) e.Extra["tallest"] = tallest;
        if (soilIds.Count > 0) e.Extra["soil"] = Blocks.Get(soilIds.MaxBy(kv => kv.Value).Key).Name;
        e.Extra["water"] = waterCells.Count;
    }

    private void ApplyTree(ClusterChange c)
    {
        int logs = 0, saplings = 0, trunks = 0;
        foreach (var p in c.Cells)
        {
            int id = _world.GetBlock(p);
            if (id == Blocks.Sapling) saplings++;
            else if (id == Blocks.Log) { logs++; if (_world.GetBlock(p.Down()) != Blocks.Log) trunks++; }
        }
        if (saplings == 0 && logs < 3)
        {
            if (Find(IndexKinds.Tree, c.Key) is { } gone) RemoveEntry(gone);
            return;
        }
        var e = ClusterEntry(IndexKinds.Tree, c, out _);
        e.Extra.Clear();
        e.Extra["trees"] = trunks;
        e.Extra["logs"] = logs;
        e.Extra["saplings"] = saplings;
    }

    // ------------------------------------------------------------------ unknown structures

    private void ApplyStructure(ClusterChange c)
    {
        var explainers = _entries.Values.Where(x => x.Kind is IndexKinds.Generator or IndexKinds.Farm).ToList();
        var unexplained = c.Cells.Where(p => !explainers.Any(x => x.Contains(p, x.Kind == IndexKinds.Farm ? 2 : 1))).ToList();
        var (min, max) = (MinOf(c.Cells), MaxOf(c.Cells));
        bool qualifies = unexplained.Count >= _o.StructureMinBlocks && !(IsKnownArea?.Invoke(min, max) ?? false);
        if (!qualifies)
        {
            if (Find(IndexKinds.UnknownStructure, c.Key) is { } gone) RemoveEntry(gone);
            return;
        }
        var e = ClusterEntry(IndexKinds.UnknownStructure, c, out bool created);
        var counts = new Dictionary<int, int>();
        foreach (var p in c.Cells) { int id = _world.GetBlock(p); counts[id] = counts.GetValueOrDefault(id) + 1; }
        e.Extra.Clear();
        e.Extra["blocks"] = counts.OrderByDescending(kv => kv.Value).Take(5).ToDictionary(kv => Blocks.Get(kv.Key).Name, kv => kv.Value);
        e.Extra["ask"] = true;
        if (created && !_adopted.Contains(e.Id)) e.Extra["new"] = true;
        SetProtect(e, new HashSet<BlockPos>(c.Cells));
    }

    // ------------------------------------------------------------------ islands

    private void ApplyIsland(ClusterChange c)
    {
        if (c.Cells.Count < _o.IslandMinColumns)
        {
            if (Find(IndexKinds.Island, c.Key) is { } gone) RemoveEntry(gone);
            return;
        }
        var e = GetOrCreate(IndexKinds.Island, c.Key, out bool created);
        if (!created) GridRemove(e);
        e.Cells = c.Cells;
        int y0 = int.MaxValue, y1 = int.MinValue;
        var hist = new Dictionary<int, int>();
        foreach (var cell in c.Cells)
        {
            if (!_foot.TryGetValue(XZ(cell.X, cell.Z), out var f)) continue;
            y0 = Math.Min(y0, f.MinY);
            y1 = Math.Max(y1, f.MaxY);
            foreach (var h in f.Hist) hist[h >> 16] = hist.GetValueOrDefault(h >> 16) + (h & 0xFFFF);
        }
        if (y0 > y1) { y0 = y1 = 64; }
        var (mn, mx) = (MinOf(c.Cells), MaxOf(c.Cells));
        e.Min = new BlockPos(mn.X, y0, mn.Z);
        e.Max = new BlockPos(mx.X, y1, mx.Z);
        var a = First(c.Cells);
        e.Anchor = new BlockPos(a.X, _foot.TryGetValue(XZ(a.X, a.Z), out var af) ? af.MaxY : y1, a.Z);
        int n = 0, east = 0, s = 0, w = 0;
        foreach (var cell in c.Cells)
        {
            if (!c.Cells.Contains(cell.Offset(0, 0, -1))) n++;
            if (!c.Cells.Contains(cell.Offset(1, 0, 0))) east++;
            if (!c.Cells.Contains(cell.Offset(0, 0, 1))) s++;
            if (!c.Cells.Contains(cell.Offset(-1, 0, 0))) w++;
        }
        e.Extra.Clear();
        e.Extra["columns"] = c.Cells.Count;
        e.Extra["blocks"] = hist.Values.Sum();
        e.Extra["top"] = hist.OrderByDescending(kv => kv.Value).Take(4).ToDictionary(kv => Blocks.Get(kv.Key).Name, kv => kv.Value);
        e.Extra["edges"] = new Dictionary<string, int> { ["N"] = n, ["E"] = east, ["S"] = s, ["W"] = w };
        e.Dirty = true;
        e.Seen = true;
        e.MinWriteInterval = TimeSpan.FromSeconds(_o.ClusterMinWriteSeconds);
        GridAdd(e);
    }

    private void UpdateMainIsland()
    {
        var main = _entries.Values.Where(x => x.Kind == IndexKinds.Island).MaxBy(x => x.Cells.Count)?.Key;
        if (main == _mainIsland) return;
        _mainIsland = main;
        foreach (var e in _entries.Values) e.Dirty = true; // "on main island" moved
    }

    /// <summary>The island entry whose footprint holds column (x, z).</summary>
    private IndexEntry? IslandAt(int x, int z) =>
        _islands.OwnerOf(new BlockPos(x, 0, z)) is { } k ? Find(IndexKinds.Island, k) : null;

    internal string? IslandName(int x, int z) => IslandAt(x, z) is { } i ? IslandName(i) : null;

    private string IslandName(IndexEntry island) =>
        island.Key == _mainIsland ? "main island" : $"island {(island.Min.X + island.Max.X) / 2},{(island.Min.Z + island.Max.Z) / 2}";

    // ------------------------------------------------------------------ spatial grid (near lookups)

    private readonly Dictionary<long, List<IndexEntry>> _grid = new();
    private readonly HashSet<IndexEntry> _large = new();
    private const int GridShift = 3; // 8-block cells

    private static bool IsLarge(IndexEntry e) => e.Max.X - e.Min.X > 8 || e.Max.Z - e.Min.Z > 8 || e.Max.Y - e.Min.Y > 8;

    private void GridAdd(IndexEntry e)
    {
        if (IsLarge(e)) { _large.Add(e); return; }
        var k = XZ(e.Anchor.X >> GridShift, e.Anchor.Z >> GridShift);
        if (!_grid.TryGetValue(k, out var l)) _grid[k] = l = new List<IndexEntry>();
        if (!l.Contains(e)) l.Add(e);
    }

    private void GridRemove(IndexEntry e)
    {
        _large.Remove(e);
        foreach (var l in _grid.Values) l.Remove(e); // rare (entries move only when re-keyed/re-clustered)
    }

    private IEnumerable<IndexEntry> Around(BlockPos p, double radius)
    {
        int r = (int)Math.Ceiling(radius) + 8;
        for (int gx = (p.X - r) >> GridShift; gx <= (p.X + r) >> GridShift; gx++)
        for (int gz = (p.Z - r) >> GridShift; gz <= (p.Z + r) >> GridShift; gz++)
            if (_grid.TryGetValue(XZ(gx, gz), out var l))
                foreach (var e in l) yield return e;
        foreach (var e in _large) yield return e;
    }

    private IndexEntry? NearestEntry(BlockPos p, double radius, Func<IndexEntry, bool> filter, IndexEntry? except = null)
    {
        IndexEntry? best = null;
        double bd = double.MaxValue;
        foreach (var e in Around(p, radius))
        {
            if (e == except || e.Removed || !filter(e)) continue;
            double d = e.DistanceTo(p);
            if (d <= radius && d < bd) { bd = d; best = e; }
        }
        return best;
    }

    /// <summary>Neighbours' "near X" may have changed.</summary>
    private void MarkNear(IndexEntry e)
    {
        foreach (var n in Around(e.Anchor, _o.NearRadius + 2))
            if (n != e && n.Kind is not (IndexKinds.Island or IndexKinds.Hangout or IndexKinds.Activity) && n.DistanceTo(e.Anchor) <= _o.NearRadius + 2)
                n.Dirty = true;
    }
}
