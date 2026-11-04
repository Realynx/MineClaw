using Microsoft.Extensions.Logging;
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
        if (Ids1122.IsChest(id)) return ChestAt(kind, p);
        if (id == Blocks.Bed) return BedAt(kind, p);
        return (kind, p, [p]);
    }

    /// <summary>
    /// One 54-slot chest, keyed on its west/north half whichever block is asked (ChestPairs: same kind only, a trapped
    /// chest with a trapped one; never stacked or diagonal; a third chest touching a pair pairs with nothing). Was: the
    /// first same-id side won, so with three in a row the middle block re-keyed the pair and orphaned the end chest.
    /// </summary>
    private (string Kind, BlockPos KeyPos, BlockPos[] Cells) ChestAt(string kind, BlockPos p)
    {
        if (ChestPairs.Partner(_world, p) is { } q) return Pair(kind, p, q);
        // The other half's column is away (it reads as air): a pair memory knows stays one chest until that column is back,
        // instead of splitting into a single holding all 54 slots' memory (a re-scan beside an unloaded column).
        if (_stationCells.GetValueOrDefault(p) is { Kind: IndexKinds.Chest, Cells.Count: 2 } known
            && known.Cells.First(c => c != p) is var other && !_world.IsLoaded(other.X, other.Z))
            return Pair(kind, p, other);
        return (kind, p, [p]);
    }

    private static (string Kind, BlockPos KeyPos, BlockPos[] Cells) Pair(string kind, BlockPos p, BlockPos q) =>
        ChestPairs.IsFirst(p, q) ? (kind, p, [p, q]) : (kind, q, [q, p]);

    /// <summary>A bed keys on its head cell (both halves when the other half is there).</summary>
    private (string Kind, BlockPos KeyPos, BlockPos[] Cells) BedAt(string kind, BlockPos p)
    {
        int meta = _world.GetMeta(p);
        var dir = (meta & 3) switch { 0 => new BlockPos(0, 0, 1), 1 => new BlockPos(-1, 0, 0), 2 => new BlockPos(0, 0, -1), _ => new BlockPos(1, 0, 0) };
        bool head = (meta & 8) != 0;
        var other = head ? p - dir : p + dir;
        var headPos = head ? p : other;
        return _world.GetBlock(other) == Blocks.Bed ? (kind, headPos, [p, other]) : (kind, p, [p]);
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
        KeepSingleAsHalf(s.Kind, s.Cells, e);
        foreach (var c in s.Cells)
        {
            if (_stationCells.TryGetValue(c, out var other) && other != e) MergeStation(s.Kind, e, c, other);
            _stationCells[c] = e;
            Track(_stationsByChunk, c, true);
        }
        ReshapeStation(e, s.KeyPos, s.Cells, created);
        if (seen) e.Seen = true;
        return e;
    }

    /// <summary>A single chest gaining a half: what it held is its half.</summary>
    private static void KeepSingleAsHalf(string kind, BlockPos[] cells, IndexEntry e)
    {
        if (kind == IndexKinds.Chest && cells.Length == 2 && e.Cells.Count == 1 && e.Contents is { } own && cells.Contains(e.Cells.First()))
            (e.HalfContents ??= new())[e.Cells.First()] = own.ToList();
    }

    /// <summary>Re-keyed (a second chest placed beside, bed rebuilt): carry over what we knew, then drop the old entry.</summary>
    private void MergeStation(string kind, IndexEntry e, BlockPos c, IndexEntry other)
    {
        if (kind == IndexKinds.Chest && other.Kind == IndexKinds.Chest) JoinHalf(e, c, other);
        else e.Contents ??= other.Contents;
        e.Label ??= other.Label;
        e.LabelSign ??= other.LabelSign;
        RemoveEntry(other);
        foreach (var oc in other.Cells) if (_stationCells.GetValueOrDefault(oc) == other) { _stationCells.Remove(oc); Track(_stationsByChunk, oc, false); }
        if (e.Contents != null) e.ContentsDirty = true;
    }

    /// <summary>New cells, anchor, bounds, lit state and protection when the station changed shape (or is new).</summary>
    private void ReshapeStation(IndexEntry e, BlockPos keyPos, BlockPos[] stationCells, bool created)
    {
        var cells = stationCells.ToHashSet();
        bool lit = _world.GetBlock(keyPos) == Blocks.LitFurnace;
        if (!created && e.Cells.SetEquals(cells) && e.FurnaceLit == lit) return;
        foreach (var oc in e.Cells.Where(c => !cells.Contains(c)).ToList())
            if (_stationCells.GetValueOrDefault(oc) == e) { _stationCells.Remove(oc); Track(_stationsByChunk, oc, false); }
        e.Cells = cells;
        e.Anchor = keyPos;
        e.SetBounds(cells);
        e.FurnaceLit = lit;
        e.Dirty = true;
        SetProtect(e, new HashSet<BlockPos>(cells));
        if (created) { GridAdd(e); MarkNear(e); }
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
            if (EvaluateStation(c) is { } ne && ne.Kind == e.Kind) InheritSurvivor(e, c, ne);
    }

    /// <summary>The rest of a broken station, re-derived as <paramref name="ne"/>, keeps the label and (a chest) its own half.</summary>
    private static void InheritSurvivor(IndexEntry e, BlockPos c, IndexEntry ne)
    {
        if (e.Contents != null && ne.Contents == null) ne.Label ??= e.Label;
        // A double chest lost a half: the survivor is a 27-slot chest with its own half's items (the broken half's spilled
        // on the ground). Its half unknown (no 54-slot look this session): contents unknown, never the pair's 54 slots' worth.
        if (ne.Contents != null || e.HalfContents?.GetValueOrDefault(c) is not { } half) return;
        ne.Contents = half.ToList();
        ne.ContentsAt = e.ContentsAt;
        ne.ContentsBy = e.ContentsBy;
        ne.ContentsDirty = true;
    }

    /// <summary>
    /// Chest <paramref name="other"/> became the <paramref name="cell"/> half of <paramref name="e"/>'s pair (a chest placed beside a
    /// remembered one): its memory, this session's look else the shared db's (any hive bot's last open; the re-keyed row is
    /// tombstoned), is that half's, and the pair holds both halves' items. Was: only this session's look carried over, so a
    /// chest another body had filled read as unknown once a second chest went down west/north of it.
    /// </summary>
    private void JoinHalf(IndexEntry e, BlockPos cell, IndexEntry other)
    {
        var mem = other.Contents is { } oc ? new ContainerMemory(oc, other.ContentsAt, other.ContentsBy) : Remembered(other);
        if (mem is null) return;
        if (other.Cells.Count != 1) { e.Contents ??= mem.Items.ToList(); return; }
        var halves = e.HalfContents ??= new();
        halves[cell] = mem.Items.ToList();
        e.Contents = Sum(halves.Values.SelectMany(h => h));
        e.ContentsAt = OlderLook(e.ContentsAt, mem.At);
        e.ContentsBy ??= mem.By;
    }

    /// <summary>The older of two look times (either may be unknown).</summary>
    private static DateTimeOffset? OlderLook(DateTimeOffset? x, DateTimeOffset? y) =>
        x is { } a && y is { } b ? (a < b ? a : b) : x ?? y;

    private ContainerMemory? Remembered(IndexEntry e)
    {
        try { return _sink.PreviousContents(e.Kind, e.Key, _dim) is { Items.Count: > 0 } m ? m : null; }
        catch (Exception ex) { _log.LogDebug(ex, "previous contents of {Key} unavailable", e.Key); return null; }
    }

    private static List<ItemCount> Sum(IEnumerable<ItemCount> items) => items
        .GroupBy(c => (c.ItemId, c.Damage))
        .Select(g => new ItemCount(g.Key.ItemId, g.First().Name, g.Sum(c => c.Count), g.Key.Damage))
        .OrderByDescending(c => c.Count).ToList();

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
            var sign = UpsertSign(p, label);
            if (label.Length == 0) continue;
            if (LabelTarget(p, id) is { } target) ApplyLabel(sign, target, p, label);
        }
    }

    private IndexEntry UpsertSign(BlockPos p, string label)
    {
        var sign = GetOrCreate(IndexKinds.Sign, ObservationStore.KeyFor(p.X, p.Y, p.Z), out bool created);
        ClearLabel(sign);
        sign.Anchor = p;
        sign.Min = sign.Max = p;
        sign.Cells = [p];
        sign.Label = label;
        sign.Dirty = true;
        sign.Seen = true;
        SetProtect(sign, [p]);
        if (created) GridAdd(sign);
        return sign;
    }

    /// <summary>Wall signs label the block they hang on first; otherwise the nearest thing within the radius.</summary>
    private IndexEntry? LabelTarget(BlockPos p, int id)
    {
        IndexEntry? target = null;
        if (id == Blocks.WallSign)
        {
            var back = (_world.GetMeta(p) & 7) switch
            {
                2 => p.Offset(0, 0, 1), 3 => p.Offset(0, 0, -1), 4 => p.Offset(1, 0, 0), 5 => p.Offset(-1, 0, 0), _ => p,
            };
            target = _stationCells.GetValueOrDefault(back);
        }
        return target ?? NearestEntry(p, _o.SignAttachRadius, x => x.Kind is not (IndexKinds.Sign or IndexKinds.Island or IndexKinds.Hangout or IndexKinds.Activity));
    }

    /// <summary>A second sign on the same thing adds to its label ("a / b").</summary>
    private void ApplyLabel(IndexEntry sign, IndexEntry target, BlockPos p, string label)
    {
        if (target.LabelSign is { } other && other != p && _signText.ContainsKey(other)) target.Label = target.Label + " / " + label;
        else target.Label = label;
        target.LabelSign = p;
        target.Dirty = true;
        sign.Extra["labels"] = target.Kind + "@" + target.Key;
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
            if (ev.Open) OnContainerOpened(ev);
            else if (_open is { } o && o.Window == ev.Window) OnContainerClosed(ev, o.Entry, o.InvVersion, now);
        }
        if (_open is { } cur) RefreshOpenContainer(cur.Window, cur.Entry, cur.InvVersion, now);
    }

    private void OnContainerOpened(ContainerEvent ev)
    {
        var pos = ResolveContainer(ev);
        _open = pos is { } p && EvaluateStation(p, seen: true) is { } e ? (ev.Window, e, ev.InvVersion) : null;
        _openSeenVersion = ev.InvVersion;
        _openFirst = true;
    }

    private void OnContainerClosed(ContainerEvent ev, IndexEntry entry, int openVersion, DateTimeOffset now)
    {
        // Only trust the snapshot if the window's items actually arrived after it opened.
        if (ev.InvVersion > openVersion && ev.Snapshot != null)
        {
            if (_openFirst) NoteVanished(entry, ev.Initial, now);
            ApplySnapshot(entry, ev.Window.Type, ev.Snapshot, ev.Burning, force: true, first: _openFirst, now);
        }
        _open = null;
    }

    /// <summary>The open window's items changed since the last look: snapshot them.</summary>
    private void RefreshOpenContainer(Window window, IndexEntry entry, int openVersion, DateTimeOffset now)
    {
        int v = Volatile.Read(ref _inventoryVersion);
        if (v == _openSeenVersion || v <= openVersion || _world.Inventory.OpenWindow != window) return;
        _openSeenVersion = v;
        MineClaw.Data.ItemStack[] snap;
        lock (_world.Inventory.SyncRoot) snap = window.ContainerContents().Select(t => t.Item).ToArray();
        if (_openFirst) NoteVanished(entry, _initialContents.TryGetValue(window, out var first0) ? first0 : snap, now);
        // The first look after an open is written at once (forced): other hive bots must not act on stale contents.
        ApplySnapshot(entry, window.Type, snap, window.IsBurning, force: _openFirst, first: _openFirst, now);
        _openFirst = false;
    }

    private BlockPos? ResolveContainer(ContainerEvent ev)
    {
        var want = ContainerBlocks(ev.Window.Type);
        if (ev.FeedPos is { } fp && want.Contains(_world.GetBlock(fp))) return fp;
        if (Raycast.Cast(_world, ev.Eye, ev.Yaw, ev.Pitch, 6) is { } hit)
        {
            if (want.Contains(hit.BlockId)) return hit.Pos;
            // Looking at another chest-like container (an ender chest's own 27 slots, a shulker box, a hopper): its window is
            // never the nearest chest's contents (was: written over the chest beside it).
            if (OtherContainer(hit.BlockId)) return null;
        }
        var near = _world.FindBlocks(ev.Eye, 6, (id, _) => want.Contains(id), 1);
        return near.Count > 0 ? near[0].Pos : null;
    }

    private static int[] ContainerBlocks(WindowType type) => type switch
    {
        WindowType.Chest => new[] { Blocks.Chest, Ids1122.TrappedChest },
        WindowType.Furnace => new[] { Blocks.Furnace, Blocks.LitFurnace },
        WindowType.Dispenser => new[] { Blocks.Dispenser },
        _ => [],
    };

    private static bool OtherContainer(int id) => id is Ids1122.EnderChest or Ids1122.Hopper or Ids1122.Dropper || Ids1122.IsShulkerBox(id);

    /// <summary>
    /// First look after an open: what shared memory said the container held (any bot's last open, from the db) versus what it
    /// really holds now (<paramref name="initial"/>, before our own clicks). Anything missing was taken by someone unseen.
    /// </summary>
    private void NoteVanished(IndexEntry e, MineClaw.Data.ItemStack[]? initial, DateTimeOffset now)
    {
        if (initial is null || e.Kind != IndexKinds.Chest) return;
        ContainerMemory? before = null;
        try { before = _sink.PreviousContents(e.Kind, e.Key, _dim); }
        catch (Exception ex) { _log.LogDebug(ex, "previous contents of {Key} unavailable", e.Key); }
        if (before is null && e.Contents is { } mem) before = new ContainerMemory(mem, e.ContentsAt, _selfName);
        if (before is null || before.Items.Count == 0) return;
        static int Dmg(int id, int damage) => Items.Exists(id) && Items.Get(id).MaxDamage > 0 ? 0 : damage;
        var have = initial.Where(s => !s.IsEmpty).GroupBy(s => ((int)s.Id, Dmg(s.Id, s.Damage)))
            .ToDictionary(g => g.Key, g => g.Sum(s => (int)s.Count));
        var gone = before.Items
            .Select(c => (c, missing: c.Count - have.GetValueOrDefault((c.ItemId, Dmg(c.ItemId, c.Damage)), 0)))
            .Where(t => t.missing > 0).Select(t => t.c with { Count = t.missing }).ToList();
        if (gone.Count == 0) return;
        var v = new ContainerVanished(e.Kind, e.Key, e.Anchor, gone, before, now);
        _lastVanished[e.Anchor] = v;
        try { ContentsVanished?.Invoke(v); }
        catch (Exception ex) { _log.LogDebug(ex, "ContentsVanished handler failed"); }
    }

    private static List<ItemCount> Group(IEnumerable<MineClaw.Data.ItemStack> slots) => slots.Where(s => !s.IsEmpty)
        .GroupBy(s => (s.Id, Items.Get(s.Id).MaxDamage > 0 ? 0 : (int)s.Damage))
        .Select(g => new ItemCount(g.Key.Id, ItemName(g.Key.Id), g.Sum(s => s.Count), g.Key.Item2))
        .OrderByDescending(c => c.Count).ToList();

    private void ApplySnapshot(IndexEntry e, WindowType type, MineClaw.Data.ItemStack[] slots, bool burning, bool force, bool first, DateTimeOffset now)
    {
        var list = Group(slots);
        e.HalfContents = HalvesOf(e, slots);
        e.ContentsBy = null;
        if (type == WindowType.Furnace)
        {
            e.FurnaceSlots = (FurnaceSlot(slots, 0), FurnaceSlot(slots, 1), FurnaceSlot(slots, 2));
            e.FurnaceLit |= burning;
        }
        bool same = SameContents(e.Contents, list);
        e.Contents = list;
        e.ContentsAt = now;
        // Every open re-stamps the contents (freshness for every hive bot), even when they didn't change.
        if (!same || first || force) e.ContentsDirty = true;
        e.Seen = true;
        e.Dirty = true;
        e.MinWriteInterval = force ? TimeSpan.Zero : TimeSpan.FromSeconds(_o.ContainerMinWriteSeconds);
        if (force) e.Force = true;
        ContainerSnapshot?.Invoke(e.Kind, e.Key, e.Anchor, list, first);
    }

    /// <summary>Either half's click opens the same 54 slots (slots 0-26 = the key cell, the west/north half): one row holds them
    /// all, and each half's share is kept for the day one half is broken. Null for anything but a full double chest.</summary>
    private static Dictionary<BlockPos, List<ItemCount>>? HalvesOf(IndexEntry e, MineClaw.Data.ItemStack[] slots) =>
        e.Kind == IndexKinds.Chest && e.Cells.Count == 2 && slots.Length >= ChestPairs.DoubleSlots
            ? new Dictionary<BlockPos, List<ItemCount>>
            {
                [e.Anchor] = Group(slots.Take(ChestPairs.HalfSlots)),
                [e.Cells.First(c => c != e.Anchor)] = Group(slots.Skip(ChestPairs.HalfSlots).Take(ChestPairs.HalfSlots)),
            }
            : null;

    private static ItemCount? FurnaceSlot(MineClaw.Data.ItemStack[] slots, int i) =>
        i < slots.Length && !slots[i].IsEmpty ? new ItemCount(slots[i].Id, ItemName(slots[i].Id), slots[i].Count, slots[i].Damage) : null;

    private static bool SameContents(List<ItemCount>? was, List<ItemCount> now) =>
        was != null && was.Count == now.Count && was.Zip(now).All(t => t.First == t.Second);

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
        try { rows = _sink.Near(null, (cx << 4) + 8, 64, (cz << 4) + 8, 72, 400, _dim); }
        catch { return; }
        foreach (var r in rows) ReconcileRow(r, cx, cz);
    }

    /// <summary>A persisted row anchored in this column: a point row is checked against the world now, a cluster row waits for
    /// the cluster pass.</summary>
    private void ReconcileRow(ObservationRow r, int cx, int cz)
    {
        if (r.Bounds is not { } b) return;
        int ax = (int)Math.Floor(b.MinX), az = (int)Math.Floor(b.MinZ);
        if (ax >> 4 != cx || az >> 4 != cz) return;
        // Chest rows of either size (a double's box spans two blocks; its key is the west/north half = the box's min).
        if (IndexKinds.PointKinds.Contains(r.Kind) && (b.IsPoint || r.Kind == IndexKinds.Chest))
            ReconcilePointRow(r, b, new BlockPos(ax, (int)Math.Floor(b.MinY), az));
        else if (IndexKinds.ClusterKinds.Contains(r.Kind) && r.Source == ObservationSource.Auto)
            _pendingClusterRows.Add(r);
    }

    private void ReconcilePointRow(ObservationRow r, Box b, BlockPos p)
    {
        if (Find(r.Kind, r.Key) is { } live)
        {
            if (r.Kind == IndexKinds.Chest) ReconcileChestSize(r, b, live);
            return;
        }
        if (StillPresent(r.Kind, p)) return;
        if (r.Kind == IndexKinds.Chest) CarryPhantomHalf(r, p);
        _removals.Add((r.Kind, r.Key));
    }

    /// <summary>The row's thing is still there. A row on the non-key half of a pair is a phantom second chest (a chest went down
    /// west/north of a remembered single while the bot was away): gone, its memory carried into the pair. Was: kept, and the
    /// double counted twice.</summary>
    private bool StillPresent(string kind, BlockPos p) =>
        kind == IndexKinds.Sign
            ? _world.GetBlock(p) is Blocks.SignPost or Blocks.WallSign
            : StationAt(p) is { } s && s.Kind == kind && (kind != IndexKinds.Chest || s.KeyPos == p);

    /// <summary>The phantom half's remembered contents go to the pair when the pair has none of its own.</summary>
    private void CarryPhantomHalf(ObservationRow r, BlockPos p)
    {
        if (_stationCells.GetValueOrDefault(p) is not { Kind: IndexKinds.Chest, Contents: null } pair || Remembered(pair) is not null
            || _sink.PreviousContents(r.Kind, r.Key, _dim) is not { Items.Count: > 0 } half) return;
        pair.Contents = half.Items.ToList();
        pair.ContentsAt = half.At;
        pair.ContentsBy = half.By;
        if (pair.Cells.Count == 2) (pair.HalfContents ??= new())[p] = pair.Contents.ToList();
        pair.ContentsDirty = pair.Dirty = true;
    }

    /// <summary>
    /// A remembered double chest found single on rejoin (one half broken while the bot was away): its row's contents were both
    /// halves', so they go (tombstone + rewrite: contents unknown) instead of a 27-slot chest claiming 54 slots' items. Only once
    /// both blocks' columns are loaded (an unloaded half reads as air). A single that gained a half keeps its memory: the
    /// new half went down empty.
    /// </summary>
    private void ReconcileChestSize(ObservationRow r, Box b, IndexEntry live)
    {
        if (b.IsPoint || live.Cells.Count != 1 || live.Contents != null) return;
        var far = new BlockPos((int)Math.Floor(b.MaxX), (int)Math.Floor(b.MaxY), (int)Math.Floor(b.MaxZ));
        if (!_world.IsLoaded(far.X, far.Z) || live.Cells.Contains(far)) return;
        _removals.Add((r.Kind, r.Key));
        live.WrittenText = null; // re-upserted after the tombstone, in this pass's write
        live.Dirty = true;
    }

    /// <summary>Persisted auto cluster rows nobody re-claimed once their whole box has been scanned → tombstone.</summary>
    private void ReconcilePending()
    {
        if (_pendingClusterRows.Count == 0) return;
        for (int i = _pendingClusterRows.Count - 1; i >= 0; i--)
        {
            var r = _pendingClusterRows[i];
            if (Find(r.Kind, r.Key) != null) { _pendingClusterRows.RemoveAt(i); continue; }
            if (!BoxScanned(r.Bounds!.Value)) continue;
            _removals.Add((r.Kind, r.Key));
            _pendingClusterRows.RemoveAt(i);
        }
        if (_pendingClusterRows.Count > 2000) _pendingClusterRows.RemoveRange(0, _pendingClusterRows.Count - 2000);
    }

    /// <summary>Every column under the box was scanned and none is waiting for a re-scan.</summary>
    private bool BoxScanned(Box b)
    {
        bool covered = true;
        for (int cx = (int)Math.Floor(b.MinX) >> 4; cx <= (int)Math.Floor(b.MaxX) >> 4 && covered; cx++)
        for (int cz = (int)Math.Floor(b.MinZ) >> 4; cz <= (int)Math.Floor(b.MaxZ) >> 4 && covered; cz++)
            covered = _scannedColumns.Contains(ColKey(cx, cz));
        lock (_columnsLock) covered &= _dirtyColumns.Count == 0 || !_dirtyColumns.Any(k => { var (x, z) = ColOf(k); return x >= (int)Math.Floor(b.MinX) >> 4 && x <= (int)Math.Floor(b.MaxX) >> 4 && z >= (int)Math.Floor(b.MinZ) >> 4 && z <= (int)Math.Floor(b.MaxZ) >> 4; });
        return covered;
    }

    /// <summary>A stored row no live entry holds; an auto row only of the same sub-type.</summary>
    private bool Adoptable(ObservationRow row, string kind, string? subType) =>
        Find(kind, row.Key) == null
        && (row.Source != ObservationSource.Auto || subType == null || row.Key.StartsWith(subType + "@", StringComparison.Ordinal));

    /// <summary>Key for a brand-new cluster: reuse a persisted row of the same kind it overlaps (auto rows of the same
    /// sub-type, or any taught/llm row — the store then only annotates that one), else <paramref name="fresh"/>.</summary>
    private string AdoptKey(string kind, string? subType, BlockPos min, BlockPos max, string fresh)
    {
        double cx = (min.X + max.X) / 2.0, cy = (min.Y + max.Y) / 2.0, cz = (min.Z + max.Z) / 2.0;
        double r = Math.Sqrt(Math.Pow(max.X - min.X, 2) + Math.Pow(max.Y - min.Y, 2) + Math.Pow(max.Z - min.Z, 2)) / 2 + 3;
        IReadOnlyList<ObservationRow> rows;
        try { rows = _sink.Near(kind, cx, cy, cz, r, 10, _dim); }
        catch { return fresh; }
        string? best = null;
        double bestOverlap = -1;
        foreach (var row in rows)
        {
            if (row.Bounds is not { } b || !Adoptable(row, kind, subType)) continue;
            double raw = Overlap(b, min, max);
            if (raw < 0) continue;
            double overlap = raw * (row.Source == ObservationSource.Auto ? 1 : 2);
            if (overlap > bestOverlap) { bestOverlap = overlap; best = row.Key; }
        }
        _pendingClusterRows.RemoveAll(x => x.Kind == kind && x.Key == best);
        // A stored row matched: this is a thing already known from an earlier pass/session, even when its fresh key happens
        // to equal the stored one (an unchanged structure) - it is not "new" (was: re-announced on every join).
        if (best != null) _adopted.Add(kind + "|" + best);
        return best ?? fresh;
    }

    /// <summary>Overlap volume of the row's box with the cluster's box grown by 2; -1 when they don't touch.</summary>
    private static double Overlap(Box b, BlockPos min, BlockPos max)
    {
        double ox = Math.Min(b.MaxX, max.X + 2) - Math.Max(b.MinX, min.X - 2) + 1;
        double oy = Math.Min(b.MaxY, max.Y + 2) - Math.Max(b.MinY, min.Y - 2) + 1;
        double oz = Math.Min(b.MaxZ, max.Z + 2) - Math.Max(b.MinZ, min.Z - 2) + 1;
        return ox <= 0 || oy <= 0 || oz <= 0 ? -1 : ox * oy * oz;
    }

    private static string Json(Dictionary<string, object?> fields) => JsonSerializer.Serialize(fields);
}
