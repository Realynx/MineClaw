using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

public sealed partial class WorldIndexer
{
    private readonly Dictionary<BlockPos, IndexEntry> _stationCells = new();
    private readonly Dictionary<long, HashSet<BlockPos>> _stationsByChunk = new();
    private readonly Dictionary<long, HashSet<BlockPos>> _signsByChunk = new();
    private readonly Dictionary<BlockPos, string[]> _signText = new();
    private readonly HashSet<BlockPos> _dirtySigns = new();
    private (Window Window, IndexEntry Entry, int InvVersion)? _open;
    private int _openSeenVersion;
    private bool _openFirst;

    private static long ChunkOf(BlockPos p) => ColKey(p.X >> 4, p.Z >> 4);

    private static void Track(Dictionary<long, HashSet<BlockPos>> byChunk, BlockPos p, bool on)
    {
        var k = ChunkOf(p);
        if (on)
        {
            if (!byChunk.TryGetValue(k, out var s)) byChunk[k] = s = new HashSet<BlockPos>();
            s.Add(p);
        }
        else if (byChunk.TryGetValue(k, out var s)) s.Remove(p);
    }

    // ------------------------------------------------------------------ stations

    /// <summary>The cells and canonical key cell of the station at <paramref name="p"/> (chest pairs merge, beds key on the head).</summary>
    private (string Kind, BlockPos KeyPos, BlockPos[] Cells)? StationAt(BlockPos p)
    {
        int id = _world.GetBlock(p);
        var kind = IndexKinds.StationKind(id);
        if (kind == null) return null;
        if (id == Blocks.Chest)
        {
            foreach (var o in Six)
            {
                if (o.Y != 0) continue;
                var q = p + o;
                if (_world.GetBlock(q) != Blocks.Chest) continue;
                var min = Less(p, q) ? p : q;
                return (kind, min, [p, q]);
            }
            return (kind, p, [p]);
        }
        if (id == Blocks.Bed)
        {
            int meta = _world.GetMeta(p);
            var dir = (meta & 3) switch { 0 => new BlockPos(0, 0, 1), 1 => new BlockPos(-1, 0, 0), 2 => new BlockPos(0, 0, -1), _ => new BlockPos(1, 0, 0) };
            bool head = (meta & 8) != 0;
            var other = head ? p - dir : p + dir;
            var headPos = head ? p : other;
            return _world.GetBlock(other) == Blocks.Bed ? (kind, headPos, [p, other]) : (kind, p, [p]);
        }
        return (kind, p, [p]);
    }

    private static bool Less(BlockPos a, BlockPos b) => a.X != b.X ? a.X < b.X : a.Y != b.Y ? a.Y < b.Y : a.Z < b.Z;

    /// <summary>Re-derives the station at one cell; creates, merges, re-keys or tombstones entries as needed.</summary>
    private IndexEntry? EvaluateStation(BlockPos p, bool seen = false)
    {
        var st = StationAt(p);
        _stationCells.TryGetValue(p, out var prev);
        if (st is not { } s)
        {
            if (prev != null) DropStation(prev, p);
            return null;
        }
        var key = ObservationStore.KeyFor(s.KeyPos.X, s.KeyPos.Y, s.KeyPos.Z);
        var e = GetOrCreate(s.Kind, key, out bool created);
        foreach (var c in s.Cells)
        {
            if (_stationCells.TryGetValue(c, out var other) && other != e)
            {
                // Re-keyed (a second chest placed beside, bed rebuilt): carry over what we knew.
                e.Contents ??= other.Contents;
                e.Label ??= other.Label;
                e.LabelSign ??= other.LabelSign;
                RemoveEntry(other);
                foreach (var oc in other.Cells) if (_stationCells.GetValueOrDefault(oc) == other) { _stationCells.Remove(oc); Track(_stationsByChunk, oc, false); }
                if (e.Contents != null) e.ContentsDirty = true;
            }
            _stationCells[c] = e;
            Track(_stationsByChunk, c, true);
        }
        var cells = s.Cells.ToHashSet();
        bool lit = _world.GetBlock(s.KeyPos) == Blocks.LitFurnace;
        if (created || !e.Cells.SetEquals(cells) || e.FurnaceLit != lit)
        {
            foreach (var oc in e.Cells.Where(c => !cells.Contains(c)).ToList())
                if (_stationCells.GetValueOrDefault(oc) == e) { _stationCells.Remove(oc); Track(_stationsByChunk, oc, false); }
            e.Cells = cells;
            e.Anchor = s.KeyPos;
            e.SetBounds(cells);
            e.FurnaceLit = lit;
            e.Dirty = true;
            SetProtect(e, new HashSet<BlockPos>(cells));
            if (created) { GridAdd(e); MarkNear(e); }
        }
        if (seen) e.Seen = true;
        return e;
    }

    private void DropStation(IndexEntry e, BlockPos gone)
    {
        _stationCells.Remove(gone);
        Track(_stationsByChunk, gone, false);
        var rest = e.Cells.Where(c => c != gone).ToList();
        foreach (var c in rest) if (_stationCells.GetValueOrDefault(c) == e) { _stationCells.Remove(c); Track(_stationsByChunk, c, false); }
        RemoveEntry(e);
        if (e.LabelSign is { } sp) _dirtySigns.Add(sp);
        foreach (var c in rest)
            if (EvaluateStation(c) is { } ne && e.Contents != null && ne.Contents == null && ne.Kind == e.Kind)
                ne.Label ??= e.Label;
    }

    // ------------------------------------------------------------------ signs

    private void EvaluateSign(BlockPos p)
    {
        int id = _world.GetBlock(p);
        bool isSign = id is Blocks.SignPost or Blocks.WallSign;
        Track(_signsByChunk, p, isSign);
        var key = ObservationStore.KeyFor(p.X, p.Y, p.Z);
        var e = Find(IndexKinds.Sign, key);
        if (!isSign)
        {
            if (e != null) { ClearLabel(e); RemoveEntry(e); }
            _signText.Remove(p);
            return;
        }
        if (e != null) e.Seen = true;
        else if (_signText.ContainsKey(p)) _dirtySigns.Add(p);
    }

    private void PollSigns(DateTimeOffset now)
    {
        IReadOnlyDictionary<BlockPos, string[]> signs;
        try { signs = _feed.Signs; }
        catch { return; }
        foreach (var (pos, lines) in signs)
        {
            if (_signText.TryGetValue(pos, out var known) && (ReferenceEquals(known, lines) || known.AsSpan().SequenceEqual(lines))) continue;
            _signText[pos] = lines.ToArray();
            _dirtySigns.Add(pos);
            SignChanged?.Invoke(pos, _signText[pos]);
        }
    }

    /// <summary>Raised on the indexer thread when a sign's text is first seen or changes (the activity tracker attributes it).</summary>
    internal event Action<BlockPos, string[]>? SignChanged;

    public static string SignLabel(string[] lines)
    {
        var parts = lines.Select(l => StripColors(l ?? "").Trim()).Where(l => l.Length > 0);
        var s = string.Join(" ", parts);
        return s.Length > 60 ? s[..60] : s;
    }

    private static string StripColors(string s)
    {
        if (!s.Contains('§')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '§') { i++; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private void AttachLabels()
    {
        if (_dirtySigns.Count == 0) return;
        foreach (var p in _dirtySigns.ToList())
        {
            _dirtySigns.Remove(p);
            int id = _world.GetBlock(p);
            bool loaded = _world.IsLoaded(p.X, p.Z);
            if (loaded && id is not (Blocks.SignPost or Blocks.WallSign)) continue;
            if (!_signText.TryGetValue(p, out var lines)) continue;
            var label = SignLabel(lines);
            var key = ObservationStore.KeyFor(p.X, p.Y, p.Z);
            var sign = GetOrCreate(IndexKinds.Sign, key, out bool created);
            ClearLabel(sign);
            sign.Anchor = p;
            sign.Min = sign.Max = p;
            sign.Cells = [p];
            sign.Label = label;
            sign.Dirty = true;
            sign.Seen = true;
            SetProtect(sign, [p]);
            if (created) GridAdd(sign);
            if (label.Length == 0) continue;

            // Wall signs label the block they hang on first; otherwise the nearest thing within the radius.
            IndexEntry? target = null;
            if (id == Blocks.WallSign)
            {
                var back = (_world.GetMeta(p) & 7) switch
                {
                    2 => p.Offset(0, 0, 1), 3 => p.Offset(0, 0, -1), 4 => p.Offset(1, 0, 0), 5 => p.Offset(-1, 0, 0), _ => p,
                };
                target = _stationCells.GetValueOrDefault(back);
            }
            target ??= NearestEntry(p, _o.SignAttachRadius, x => x.Kind is not (IndexKinds.Sign or IndexKinds.Island or IndexKinds.Hangout or IndexKinds.Activity));
            if (target == null) continue;
            if (target.LabelSign is { } other && other != p && _signText.ContainsKey(other)) target.Label = target.Label + " / " + label;
            else target.Label = label;
            target.LabelSign = p;
            target.Dirty = true;
            sign.Extra["labels"] = target.Kind + "@" + target.Key;
        }
    }

    private void ClearLabel(IndexEntry sign)
    {
        if (sign.Extra.GetValueOrDefault("labels") is not string t) return;
        sign.Extra.Remove("labels");
        var i = t.IndexOf('@');
        if (i > 0 && Find(t[..i], t[(i + 1)..]) is { } target && target.LabelSign == sign.Anchor)
        {
            target.Label = null;
            target.LabelSign = null;
            target.Dirty = true;
        }
    }

    // ------------------------------------------------------------------ containers

    private void HandleContainers(DateTimeOffset now)
    {
        while (_containerEvents.TryDequeue(out var ev))
        {
            if (ev.Open)
            {
                var pos = ResolveContainer(ev);
                _open = pos is { } p && EvaluateStation(p, seen: true) is { } e ? (ev.Window, e, ev.InvVersion) : null;
                _openSeenVersion = ev.InvVersion;
                _openFirst = true;
            }
            else if (_open is { } o && o.Window == ev.Window)
            {
                // Only trust the snapshot if the window's items actually arrived after it opened.
                if (ev.InvVersion > o.InvVersion && ev.Snapshot != null) ApplySnapshot(o.Entry, ev.Window.Type, ev.Snapshot, ev.Burning, force: true, first: _openFirst);
                _open = null;
            }
        }
        if (_open is { } cur)
        {
            int v = Volatile.Read(ref _inventoryVersion);
            if (v != _openSeenVersion && v > cur.InvVersion && _world.Inventory.OpenWindow == cur.Window)
            {
                _openSeenVersion = v;
                MineClaw.Data.ItemStack[] snap;
                lock (_world.Inventory.SyncRoot) snap = cur.Window.ContainerContents().Select(t => t.Item).ToArray();
                ApplySnapshot(cur.Entry, cur.Window.Type, snap, cur.Window.IsBurning, force: false, first: _openFirst);
                _openFirst = false;
            }
        }
    }

    private BlockPos? ResolveContainer(ContainerEvent ev)
    {
        var want = ev.Window.Type switch
        {
            WindowType.Chest => new[] { Blocks.Chest },
            WindowType.Furnace => new[] { Blocks.Furnace, Blocks.LitFurnace },
            WindowType.Dispenser => new[] { Blocks.Dispenser },
            _ => [],
        };
        if (ev.FeedPos is { } fp && want.Contains(_world.GetBlock(fp))) return fp;
        if (Raycast.Cast(_world, ev.Eye, ev.Yaw, ev.Pitch, 6) is { } hit && want.Contains(hit.BlockId)) return hit.Pos;
        var near = _world.FindBlocks(ev.Eye, 6, (id, _) => want.Contains(id), 1);
        return near.Count > 0 ? near[0].Pos : null;
    }

    private void ApplySnapshot(IndexEntry e, WindowType type, MineClaw.Data.ItemStack[] slots, bool burning, bool force, bool first)
    {
        var list = slots.Where(s => !s.IsEmpty)
            .GroupBy(s => (s.Id, Items.Get(s.Id).MaxDamage > 0 ? 0 : (int)s.Damage))
            .Select(g => new ItemCount(g.Key.Id, ItemName(g.Key.Id), g.Sum(s => s.Count), g.Key.Item2))
            .OrderByDescending(c => c.Count).ToList();
        if (type == WindowType.Furnace)
        {
            ItemCount? At(int i) => i < slots.Length && !slots[i].IsEmpty ? new ItemCount(slots[i].Id, ItemName(slots[i].Id), slots[i].Count, slots[i].Damage) : null;
            e.FurnaceSlots = (At(0), At(1), At(2));
            e.FurnaceLit |= burning;
        }
        bool same = e.Contents != null && e.Contents.Count == list.Count && e.Contents.Zip(list).All(t => t.First == t.Second);
        e.Contents = list;
        if (!same) e.ContentsDirty = true;
        e.Seen = true;
        e.Dirty = true;
        e.MinWriteInterval = force ? TimeSpan.Zero : TimeSpan.FromSeconds(_o.ContainerMinWriteSeconds);
        if (force) e.Force = true;
        ContainerSnapshot?.Invoke(e.Kind, e.Key, e.Anchor, list, first);
    }

    /// <summary>Raised on the indexer thread after the bot snapshots a container (kind, key, position, contents, first snapshot since it opened).</summary>
    internal event Action<string, string, BlockPos, IReadOnlyList<ItemCount>, bool>? ContainerSnapshot;

    internal static string ItemName(int id) => Items.Exists(id) ? Items.Get(id).Name : Blocks.Exists(id) ? Blocks.Get(id).Name : "item_" + id;

    // ------------------------------------------------------------------ freshness + reconcile with persisted rows

    private void MarkSeenInColumn(int cx, int cz)
    {
        int x0 = cx << 4, z0 = cz << 4;
        foreach (var e in _entries.Values)
            if (e.Kind is not (IndexKinds.Hangout or IndexKinds.Activity) && e.Anchor.X >= x0 && e.Anchor.X < x0 + 16 && e.Anchor.Z >= z0 && e.Anchor.Z < z0 + 16)
                e.Seen = true;
    }

    /// <summary>First scan of a column this session: rows persisted earlier whose block is gone get tombstoned.</summary>
    private void QueueReconcile(int cx, int cz)
    {
        IReadOnlyList<ObservationRow> rows;
        try { rows = _sink.Near(null, (cx << 4) + 8, 64, (cz << 4) + 8, 72, 400); }
        catch { return; }
        foreach (var r in rows)
        {
            if (r.Bounds is not { } b) continue;
            int ax = (int)Math.Floor(b.MinX), az = (int)Math.Floor(b.MinZ);
            if (ax >> 4 != cx || az >> 4 != cz) continue;
            if (IndexKinds.PointKinds.Contains(r.Kind) && b.IsPoint)
            {
                if (Find(r.Kind, r.Key) != null) continue;
                var p = new BlockPos(ax, (int)Math.Floor(b.MinY), az);
                bool present = r.Kind == IndexKinds.Sign
                    ? _world.GetBlock(p) is Blocks.SignPost or Blocks.WallSign
                    : StationAt(p) is { } s && s.Kind == r.Kind;
                if (!present) _removals.Add((r.Kind, r.Key));
            }
            else if (IndexKinds.ClusterKinds.Contains(r.Kind) && r.Source == ObservationSource.Auto)
                _pendingClusterRows.Add(r);
        }
    }

    /// <summary>Persisted auto cluster rows nobody re-claimed once their whole box has been scanned → tombstone.</summary>
    private void ReconcilePending()
    {
        if (_pendingClusterRows.Count == 0) return;
        for (int i = _pendingClusterRows.Count - 1; i >= 0; i--)
        {
            var r = _pendingClusterRows[i];
            if (Find(r.Kind, r.Key) != null) { _pendingClusterRows.RemoveAt(i); continue; }
            var b = r.Bounds!.Value;
            bool covered = true;
            for (int cx = (int)Math.Floor(b.MinX) >> 4; cx <= (int)Math.Floor(b.MaxX) >> 4 && covered; cx++)
            for (int cz = (int)Math.Floor(b.MinZ) >> 4; cz <= (int)Math.Floor(b.MaxZ) >> 4 && covered; cz++)
                covered = _scannedColumns.Contains(ColKey(cx, cz));
            lock (_columnsLock) covered &= _dirtyColumns.Count == 0 || !_dirtyColumns.Any(k => { var (x, z) = ColOf(k); return x >= (int)Math.Floor(b.MinX) >> 4 && x <= (int)Math.Floor(b.MaxX) >> 4 && z >= (int)Math.Floor(b.MinZ) >> 4 && z <= (int)Math.Floor(b.MaxZ) >> 4; });
            if (!covered) continue;
            _removals.Add((r.Kind, r.Key));
            _pendingClusterRows.RemoveAt(i);
        }
        if (_pendingClusterRows.Count > 2000) _pendingClusterRows.RemoveRange(0, _pendingClusterRows.Count - 2000);
    }

    /// <summary>Key for a brand-new cluster: reuse a persisted row of the same kind it overlaps (auto rows of the same
    /// sub-type, or any taught/llm row — the store then only annotates that one), else <paramref name="fresh"/>.</summary>
    private string AdoptKey(string kind, string? subType, BlockPos min, BlockPos max, string fresh)
    {
        double cx = (min.X + max.X) / 2.0, cy = (min.Y + max.Y) / 2.0, cz = (min.Z + max.Z) / 2.0;
        double r = Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2) + Math.Pow(max.Z - min.Z, 2)) / 2 + 3;
        IReadOnlyList<ObservationRow> rows;
        try { rows = _sink.Near(kind, cx, cy, cz, r, 10); }
        catch { return fresh; }
        string? best = null;
        double bestOverlap = -1;
        foreach (var row in rows)
        {
            if (row.Bounds is not { } b || Find(kind, row.Key) != null) continue;
            if (row.Source == ObservationSource.Auto && subType != null && !row.Key.StartsWith(subType + "@", StringComparison.Ordinal)) continue;
            double ox = Math.Min(b.MaxX, max.X + 2) - Math.Max(b.MinX, min.X - 2) + 1;
            double oy = Math.Min(b.MaxY, max.Y + 2) - Math.Max(b.MinY, min.Y - 2) + 1;
            double oz = Math.Min(b.MaxZ, max.Z + 2) - Math.Max(b.MinZ, min.Z - 2) + 1;
            if (ox <= 0 || oy <= 0 || oz <= 0) continue;
            double overlap = ox * oy * oz * (row.Source == ObservationSource.Auto ? 1 : 2);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = row.Key; }
        }
        _pendingClusterRows.RemoveAll(x => x.Kind == kind && x.Key == best);
        return best ?? fresh;
    }

    private static string Json(Dictionary<string, object?> fields) => JsonSerializer.Serialize(fields);
}
