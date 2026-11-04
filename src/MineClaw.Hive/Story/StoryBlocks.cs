using MineClaw.Data.Versioning;

namespace MineClaw.Hive.Story;

/// <summary>
/// Block classes the designate tools and zones read, for both versions (legacy numeric ids are shared: b1.8.1's are a subset of
/// 1.12.2's; a 1.12.2-only id simply never occurs in a b1.8.1 world). Ages: wheat/carrots/potatoes ripe at meta 7, beetroot and
/// nether wart at 3, cocoa when (meta &gt;&gt; 2) == 2; sugar cane and cactus above their base; melon and pumpkin blocks.
/// </summary>
public static class StoryBlocks
{
    public static readonly HashSet<int> Logs = [17, 162];
    public static readonly HashSet<int> Leaves = [18, 161];
    /// <summary>The cut tool: grass, flowers, ferns, dead bushes, saplings, mushrooms, vines, lily pads (and leaves).</summary>
    public static readonly HashSet<int> Plants = [6, 31, 32, 37, 38, 39, 40, 106, 111, 175];
    /// <summary>1.12.2's two-block-tall plants (sunflower, lilac, double grass, large fern, rose bush, peony): one plant, two halves.</summary>
    public const int DoublePlant = 175;
    public static readonly HashSet<int> Ores =[14, 15, 16, 21, 56, 73, 74, 129, 153];
    public static readonly HashSet<int> Stone = [1, 4, 24, 48, 87, 121, 179];
    public static readonly HashSet<int> Soil = [2, 3, 12, 13, 60, 78, 80, 82, 88, 110];
    public static readonly HashSet<int> Liquids = [8, 9, 10, 11];
    /// <summary>Blocks a pawn may not dig (bedrock, portals, spawners) and air.</summary>
    public static readonly HashSet<int> Undiggable = [0, 7, 8, 9, 10, 11, 51, 52, 90, 119, 120];

    /// <summary>What the world makes by itself: deconstruct leaves these alone.</summary>
    public static readonly HashSet<int> Natural =
    [
        0, 1, 2, 3, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 21, 24, 30, 31, 32, 37, 38, 39, 40, 48, 49, 51, 52, 56, 59, 73, 74, 78,
        79, 80, 81, 82, 83, 86, 87, 88, 89, 90, 99, 100, 103, 106, 110, 111, 115, 121, 127, 129, 141, 142, 153, 159, 161, 162, 172, 174,
        175, 179, 207,
    ];

    /// <summary>Blocks a build may place into without clearing first.</summary>
    public static readonly HashSet<int> Replaceable = [0, 8, 9, 10, 11, 31, 32, 51, 78, 106, 175];

    /// <summary>Ripe? and what to replant (an item name for this version), or null when it isn't a crop.</summary>
    public static (bool Ripe, string? Replant)? Crop(IStoryWorld w, int x, int y, int z)
    {
        var (id, meta) = w.Block(x, y, z);
        if (Replanted.TryGetValue(id, out var c)) return (meta >= c.Ripe, ItemName(w.Data, c.Seed));
        return id switch
        {
            127 => ((meta >> 2) >= 2, null),
            103 or 86 => (true, null),
            83 or 81 => (w.Block(x, y - 1, z).Id == id, null), // above the base: the base regrows
            _ => null,
        };
    }

    /// <summary>Crops sown again from a seed item: the age they are ripe at and the item (wheat, carrots, potatoes, beetroot, nether wart).</summary>
    private static readonly Dictionary<int, (int Ripe, int Seed)> Replanted = new()
    {
        [59] = (7, 295), [141] = (7, 391), [142] = (7, 392), [207] = (3, 435), [115] = (3, 372),
    };

    public static string? ItemName(IGameData d, int id) => d.ItemExists(id) ? d.Item(id).Name : null;

    /// <summary>A tree's log column from its base (the lowest log whose block below isn't a log): straight up, plus one-off branches.</summary>
    public static List<P3> TreeFrom(IStoryWorld w, P3 b, int max = 32)
    {
        var list = new List<P3>();
        var seen = new HashSet<P3>();
        var todo = new Queue<P3>();
        todo.Enqueue(b);
        while (todo.Count > 0 && list.Count < max)
        {
            var p = todo.Dequeue();
            if (!seen.Add(p) || !Logs.Contains(w.Block(p.X, p.Y, p.Z).Id) || p.Y < b.Y || Math.Abs(p.X - b.X) > 3 || Math.Abs(p.Z - b.Z) > 3) continue;
            list.Add(p);
            todo.Enqueue(p.Add(0, 1, 0));
            todo.Enqueue(p.Add(1, 0, 0)); todo.Enqueue(p.Add(-1, 0, 0)); todo.Enqueue(p.Add(0, 0, 1)); todo.Enqueue(p.Add(0, 0, -1));
            todo.Enqueue(p.Add(1, 1, 0)); todo.Enqueue(p.Add(-1, 1, 0)); todo.Enqueue(p.Add(0, 1, 1)); todo.Enqueue(p.Add(0, 1, -1));
        }
        return list;
    }

    /// <summary>Tree bases in an area (a log with no log under it): one job per tree.</summary>
    public static IEnumerable<P3> TreeBases(IStoryWorld w, Area a, int maxScan = 400_000)
    {
        int y1 = Math.Max(0, a.Y1), y2 = Math.Min(w.Height - 1, a.Y2);
        long scanned = 0;
        for (int x = a.X1; x <= a.X2; x++)
            for (int z = a.Z1; z <= a.Z2; z++)
                for (int y = y1; y <= y2; y++)
                {
                    if (++scanned > maxScan) yield break;
                    if (Logs.Contains(w.Block(x, y, z).Id) && !Logs.Contains(w.Block(x, y - 1, z).Id)) yield return new P3(x, y, z);
                }
    }

    /// <summary>Rooted: the block under a trunk's lowest log is ground (not a log, a leaf or air), so it's a tree's base and not a branch.</summary>
    public static bool Rooted(IStoryWorld w, P3 log)
    {
        int below = w.Block(log.X, log.Y - 1, log.Z).Id;
        return below != 0 && !Logs.Contains(below) && !Leaves.Contains(below) && !Liquids.Contains(below);
    }

    /// <summary>
    /// The trunk base of the tree a block belongs to: a log (down its trunk), a branch log (to the trunk carrying it) or a leaf, the
    /// canopy top a top-down map click lands on (the nearest log of its crown, then down). A bounded search through the tree's own logs
    /// and leaves, within <paramref name="reach"/> sideways and <paramref name="down"/> below. Air right above the canopy counts too (the
    /// click's block). Null = no tree there.
    /// </summary>
    public static P3? TreeBaseOf(IStoryWorld w, P3 p, int reach = 5, int down = 32, int budget = 6000)
    {
        var start = p;
        if (!Wood(w, start)) { start = p.Add(0, -1, 0); if (!Wood(w, start)) return null; }
        var seen = new HashSet<P3> { start };
        var todo = new Queue<P3>();
        todo.Enqueue(start);
        var tried = new HashSet<P3>();
        while (todo.Count > 0 && seen.Count <= budget)
        {
            var q = todo.Dequeue();
            if (RootedTrunkBase(w, q, tried) is { } b) return b;
            foreach (var n in new[] { q.Add(0, -1, 0), q.Add(1, 0, 0), q.Add(-1, 0, 0), q.Add(0, 0, 1), q.Add(0, 0, -1), q.Add(0, 1, 0) })
                if (InTreeReach(p, n, reach, down) && !seen.Contains(n) && Wood(w, n)) { seen.Add(n); todo.Enqueue(n); }
        }
        return null;
    }

    private static bool Wood(IStoryWorld w, P3 q) => w.Block(q.X, q.Y, q.Z).Id is var id && (Logs.Contains(id) || Leaves.Contains(id));

    /// <summary>A log's trunk base (down its column), when that is rooted and wasn't tried before.</summary>
    private static P3? RootedTrunkBase(IStoryWorld w, P3 q, HashSet<P3> tried)
    {
        if (!Logs.Contains(w.Block(q.X, q.Y, q.Z).Id)) return null;
        var b = q;
        for (int i = 0; i < 48 && b.Y > 0 && Logs.Contains(w.Block(b.X, b.Y - 1, b.Z).Id); i++) b = b.Add(0, -1, 0);
        return tried.Add(b) && Rooted(w, b) ? b : null;
    }

    /// <summary>Within the search's box around the clicked block: <paramref name="reach"/> sideways, <paramref name="down"/> below, 2 above.</summary>
    private static bool InTreeReach(P3 p, P3 n, int reach, int down) =>
        n.Y >= 0 && n.Y >= p.Y - down && n.Y <= p.Y + 2 && Math.Abs(n.X - p.X) <= reach && Math.Abs(n.Z - p.Z) <= reach;

    /// <summary>
    /// The trees in an area, by their trunk bases: any log or leaf in the area's columns (from <paramref name="below"/> under its bottom,
    /// so a box drawn over the canopy still finds the trunks) resolves to its tree's base, one per tree. Branches and crowns never count
    /// as trees of their own.
    /// </summary>
    public static List<P3> TreesIn(IStoryWorld w, Area a, int below = 32, int max = 512, int maxScan = 600_000)
    {
        var scan = new TreeScan(w);
        int y1 = Math.Max(0, a.Y1 - below), y2 = Math.Min(w.Height - 1, a.Y2);
        long scanned = 0;
        for (int x = a.X1; x <= a.X2; x++)
            for (int z = a.Z1; z <= a.Z2; z++)
                for (int y = y1; y <= y2; y++)
                {
                    if (++scanned > maxScan || scan.Bases.Count >= max) return scan.Bases;
                    scan.Look(x, y, z);
                }
        return scan.Bases;
    }

    /// <summary>The trees found so far by <see cref="TreesIn"/>: their bases and the logs those trees cover.</summary>
    private sealed class TreeScan(IStoryWorld w)
    {
        public readonly List<P3> Bases = new();
        private readonly HashSet<P3> _covered = new();
        private int _resolved;

        /// <summary>Every log standing on ground is a trunk (as before: each column of a 2x2 trunk too); the rest (branches, the crown,
        /// leaves) resolves to the trunk that carries it, unless that tree was found already.</summary>
        public void Look(int x, int y, int z)
        {
            int id = w.Block(x, y, z).Id;
            bool log = Logs.Contains(id);
            if (!log && !Leaves.Contains(id)) return;
            var q = new P3(x, y, z);
            if (log && Rooted(w, q)) { Add(q); return; }
            if (_covered.Contains(q) || !log && Near(q)) return;
            if (++_resolved > 512) return;
            if (TreeBaseOf(w, q) is { } b) Add(b);
        }

        private bool Near(P3 q) => Bases.Any(b => Math.Abs(b.X - q.X) <= 4 && Math.Abs(b.Z - q.Z) <= 4 && q.Y >= b.Y);

        private void Add(P3 b)
        {
            if (Bases.Contains(b)) return;
            Bases.Add(b);
            foreach (var l in TreeFrom(w, b, 64)) _covered.Add(l);
        }
    }

    /// <summary>The mine tool's filter: all | ores | stone | soil.</summary>
    public static bool MineMatch(int id, string? filter) => !Undiggable.Contains(id) && (filter ?? "all") switch
    {
        "ores" => Ores.Contains(id),
        "stone" => Stone.Contains(id),
        "soil" => Soil.Contains(id),
        _ => true,
    };

    public static bool IsDoor(int id) => id is 64 or 71 or >= 193 and <= 197;
}
