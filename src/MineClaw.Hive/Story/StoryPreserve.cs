using System.Text.Json.Serialization;

namespace MineClaw.Hive.Story;

/// <summary>
/// A zone's Preserve setting (docs/web/story-mode.md "Preserve zones"; Fox 11-06: "A zone that protects the current state of the blocks
/// … if a creeper blows up or something changes, one of the pawns gets queued to fix it to the original state"): on/off, how deep under
/// and how high over the zone's painted level it reaches, when its snapshot was taken, the repair build that keeps it, and the snapshot
/// itself (saved compactly: <see cref="PreserveCodec"/>).
/// </summary>
public sealed class PreserveState
{
    public bool On { get; set; } = true;
    /// <summary>Blocks under the zone's painted level (its lowest) that are kept.</summary>
    public int Below { get; set; } = 1;
    /// <summary>Blocks over the zone's painted level (its highest) that are kept.</summary>
    public int Above { get; set; } = 3;
    public DateTimeOffset? TakenAt { get; set; }
    /// <summary>The repair build (kind <c>preserve</c>) whose ghosts are the cells that differ now; null until the first damage.</summary>
    public string? Build { get; set; }

    private string? _enc;
    private PreserveSnap? _data;
    private bool _decoded = true;

    /// <summary>The snapshot as saved (palette + run lengths, base64); encoded at the save after a change, decoded on first use.</summary>
    public string? Snap
    {
        get => _enc ??= _data == null ? null : PreserveCodec.Encode(_data);
        set { _enc = value; _data = null; _decoded = value == null; }
    }

    /// <summary>The snapshot in memory (null = none yet).</summary>
    [JsonIgnore]
    internal PreserveSnap? Data
    {
        get
        {
            if (_decoded) return _data;
            _decoded = true;
            _data = _enc == null ? null : PreserveCodec.Decode(_enc);
            return _data;
        }
        set { _data = value; _enc = null; _decoded = true; }
    }

    /// <summary>Values changed in place: encoded again at the next save.</summary>
    internal void Changed() => _enc = null;

    /// <summary>The settings and the same snapshot (a zone split in two: each part trims it to its cells).</summary>
    internal PreserveState Copy() => new() { On = On, Below = Below, Above = Above, TakenAt = TakenAt, Data = Data };
}

/// <summary>
/// A snapshot: per zone column (x, z key) the cells from <see cref="Lo"/> to <see cref="Hi"/>. A value is <c>id &lt;&lt; 4 | meta</c>, or
/// <see cref="Unknown"/> (the column wasn't loaded: filled once it is) or <see cref="Held"/> (an owner's directive covers the cell: what
/// it leaves there is taken when it ends). The column map never changes after construction (a pawn's planner thread reads it through
/// the dig filter); values are written in place.
/// </summary>
internal sealed class PreserveSnap(int lo, int hi, Dictionary<long, int[]> cols)
{
    public const int Unknown = -1, Held = -2, Unprotected = -3;

    private static long _serials;

    /// <summary>Unique per snapshot object (the bodies' dig filter is rebuilt when one is replaced).</summary>
    public long Serial { get; } = Interlocked.Increment(ref _serials);

    public int Lo { get; } = lo;
    public int Hi { get; } = hi;
    public Dictionary<long, int[]> Cols { get; } = cols;
    public int Layers => Hi - Lo + 1;
    public long Cells => (long)Cols.Count * Layers;

    public static int Pack(int id, int meta) => id << 4 | (meta & 15);
    public static int IdOf(int v) => v >> 4;
    public static int MetaOf(int v) => v & 15;

    /// <summary>The value at a cell, or null when the snapshot doesn't hold it.</summary>
    public int? At(int x, int y, int z) =>
        y >= Lo && y <= Hi && Cols.TryGetValue(ZoneCells.Key(x, z), out var col) ? col[y - Lo] : null;
}

/// <summary>A snapshot as text: varints (zigzag for signed) of a version, the y range, the columns as row runs (z, x1, x2), a palette and
/// (run length, palette index) pairs over the values (columns in row order, each from Lo up), base64.</summary>
internal static class PreserveCodec
{
    public static string Encode(PreserveSnap s)
    {
        var w = new List<byte>(256);
        Put(w, 1);
        PutS(w, s.Lo);
        PutS(w, s.Hi);
        var rows = new ZoneCells(s.Cols.Keys).Rows();
        Put(w, rows.Count);
        foreach (var r in rows) { PutS(w, r[0]); PutS(w, r[1]); PutS(w, r[2]); }
        var palette = new Dictionary<int, int>();
        var runs = new List<(int N, int Idx)>();
        foreach (var r in rows)
            for (int x = r[1]; x <= r[2]; x++)
                foreach (var v in s.Cols[ZoneCells.Key(x, r[0])]) AddRun(runs, palette, v);
        Put(w, palette.Count);
        foreach (var v in palette.OrderBy(p => p.Value).Select(p => p.Key)) PutS(w, v);
        Put(w, runs.Count);
        foreach (var (n, idx) in runs) { Put(w, n); Put(w, idx); }
        return Convert.ToBase64String(w.ToArray());
    }

    private static void AddRun(List<(int N, int Idx)> runs, Dictionary<int, int> palette, int v)
    {
        if (!palette.TryGetValue(v, out var idx)) palette[v] = idx = palette.Count;
        if (runs.Count > 0 && runs[^1].Idx == idx) runs[^1] = (runs[^1].N + 1, idx);
        else runs.Add((1, idx));
    }

    /// <summary>The snapshot, or null when the text is damaged (the zone then takes a new one).</summary>
    public static PreserveSnap? Decode(string text)
    {
        try { return DecodeCore(Convert.FromBase64String(text)); }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static PreserveSnap? DecodeCore(byte[] b)
    {
        int at = 0;
        if (Get(b, ref at) != 1) return null;
        int lo = GetS(b, ref at), hi = GetS(b, ref at);
        if (hi < lo) return null;
        var cols = new Dictionary<long, int[]>();
        var order = new List<long>();
        int nRows = Get(b, ref at);
        for (int i = 0; i < nRows; i++)
        {
            int z = GetS(b, ref at), x1 = GetS(b, ref at), x2 = GetS(b, ref at);
            for (int x = x1; x <= x2; x++) { long k = ZoneCells.Key(x, z); order.Add(k); cols[k] = new int[hi - lo + 1]; }
        }
        var palette = new int[Get(b, ref at)];
        for (int i = 0; i < palette.Length; i++) palette[i] = GetS(b, ref at);
        return Fill(b, ref at, lo, hi, cols, order, palette);
    }

    private static PreserveSnap? Fill(byte[] b, ref int at, int lo, int hi, Dictionary<long, int[]> cols, List<long> order, int[] palette)
    {
        int layers = hi - lo + 1, nRuns = Get(b, ref at);
        long cell = 0, total = (long)order.Count * layers;
        for (int i = 0; i < nRuns; i++)
        {
            int n = Get(b, ref at), v = palette[Get(b, ref at)];
            for (int k = 0; k < n && cell < total; k++, cell++) cols[order[(int)(cell / layers)]][(int)(cell % layers)] = v;
        }
        return cell == total ? new PreserveSnap(lo, hi, cols) : null;
    }

    private static void Put(List<byte> w, int v)
    {
        uint u = (uint)v;
        while (u >= 0x80) { w.Add((byte)(u | 0x80)); u >>= 7; }
        w.Add((byte)u);
    }

    private static void PutS(List<byte> w, int v) => Put(w, (v << 1) ^ (v >> 31));

    private static int Get(byte[] b, ref int at)
    {
        uint u = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte x = b[at++];
            u |= (uint)(x & 0x7F) << shift;
            if (x < 0x80 || shift > 28) return (int)u;
        }
    }

    private static int GetS(byte[] b, ref int at)
    {
        int u = Get(b, ref at);
        return (int)((uint)u >> 1) ^ -(u & 1);
    }
}

/// <summary>
/// What a preserve zone counts as a change (docs/web/story-mode.md "Preserve zones": "What counts as a change"). A snapshot cell is one of:
/// <list type="bullet">
/// <item><b>ignored</b>: leaves (they decay), and in a growing zone or a woodlot its logs too (trees grow and are felled there);</item>
/// <item><b>open</b>: air, or what comes and goes by itself in an open cell: plants, tall grass, flowers, saplings, crops at any stage,
/// stems, cane, cactus, pumpkins and melons, cocoa, snow layers, fire, water and lava. Only a <i>foreign</i> block in it is a change
/// (it is dug out); what comes and goes there is not;</item>
/// <item><b>kept</b>: any other block. It must be there again, by the build rule (<see cref="StoryColony.Matches"/>: the ground family is
/// one block, so dirt, grass and farmland never fight tilling or grass spreading; a lit furnace is a furnace; turned blocks keep their
/// facing). A block whose meta is runtime state (redstone power, a pressed plate, a cake's bites, a cauldron's water) is compared by id.</item>
/// </list>
/// Containers' contents, mobs, items and other entities are never part of it.
/// </summary>
internal static class PreserveRules
{
    /// <summary>What grows, falls, flows and burns in an open cell (crops at any stage, plants, snow, fire, liquids), and a piston's
    /// head or moving block.</summary>
    private static readonly HashSet<int> Comes =
    [
        0, 6, 8, 9, 10, 11, 31, 32, 34, 36, 37, 38, 39, 40, 51, 59, 78, 81, 83, 86, 103, 104, 105, 106, 111, 115, 127, 141, 142, 175, 207,
    ];

    /// <summary>Significant metadata bits for blocks with changing state. Keep facing/half while ignoring power, opening,
    /// occupancy, contents and other normal operation. Null delegates to the ordinary build matching rule.</summary>
    private static int? StableMetaMask(int id, int meta, bool modern) => id switch
    {
        23 or 27 or 28 or 29 or 33 or 69 or 77 or 143 or 154 or 157 or 158 => 7,
        26 or 96 or 167 => 11,
        64 or 71 or 193 or 194 or 195 or 196 or 197 => modern && (meta & 8) != 0 ? 9 : 11,
        93 or 94 or 107 or 120 or 131 or 149 or 150 or 183 or 184 or 185 or 186 or 187 => 3,
        55 or 66 or 70 or 72 or 84 or 92 or 117 or 118 or 132 or 147 or 148 or 151 or 178 => 0,
        _ => null,
    };

    /// <summary>Ores that only silk touch gives back: restored as the stone they lie in (any of them still there counts as kept).</summary>
    private static readonly HashSet<int> OreToStone = [14, 15, 16, 21, 56, 73, 74, 129];

    /// <summary>A zone whose trees are its crop (a growing zone, a woodlot): logs come and go there.</summary>
    public static bool TreesGrow(string kind) => kind is "farm" or "chop";

    public static bool Ignored(int id, string kind) => StoryBlocks.Leaves.Contains(id) || TreesGrow(kind) && StoryBlocks.Logs.Contains(id);

    /// <summary>The snapshot holds an open cell: only a foreign block in it is a change.</summary>
    public static bool Open(int id) => Comes.Contains(id);

    /// <summary>In an open cell: a block that doesn't come and go by itself there (a block dropped in a field, a pillar).</summary>
    public static bool Foreign(int id, string kind) => !Open(id) && !Ignored(id, kind);

    /// <summary>The block a repair puts back for a kept one: the ground family as dirt (grass can't be placed: dirt grows it, and
    /// farmland is tilled again by farming), ores as stone (netherrack for nether quartz), a runtime twin as its block.</summary>
    public static (int Id, int Meta) Restore(int id, int meta)
    {
        id = StoryDrift.Canon(id);
        if (id is 2 or 3 or 60 or 110 or 208) return (3, 0);
        if (OreToStone.Contains(id)) return (1, 0);
        if (id == 153) return (87, 0);
        return (id, meta);
    }

    /// <summary>Does the world's block keep the snapshot's? Its own block (or what it restores to), by the build rule.</summary>
    public static bool Keeps(int snapId, int snapMeta, int id, int meta, bool modern)
    {
        if (StoryDrift.Canon(id) == StoryDrift.Canon(snapId) && StableMetaMask(snapId, snapMeta, modern) is { } mask)
            return (snapMeta & mask) == (meta & mask);
        if (StoryColony.MatchesBlock(snapId, snapMeta, id, meta, modern)) return true;
        var (rid, rmeta) = Restore(snapId, snapMeta);
        return (rid != snapId || rmeta != snapMeta) && StoryColony.MatchesBlock(rid, rmeta, id, meta, modern);
    }

    /// <summary>The dig filter's test (no meta there): the block standing is the kept one (or its restore), by id.</summary>
    public static bool KeepsId(int snapId, int id, bool modern) =>
        StoryDrift.Canon(id) == StoryDrift.Canon(snapId) || StoryDrift.SameGround(snapId, id, modern) || Restore(snapId, 0).Id == StoryDrift.Canon(id);
}
