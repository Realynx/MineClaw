using System.Runtime.InteropServices;

namespace MineClaw.Hive.Story;

/// <summary>
/// A mine build's search scales with its frontier, not its queue (live 11-10: a 47,000-cell strip mine made every builder's pick read
/// all six neighbours of every queued cell, 284k block reads a pick, 153 s of picks a minute). Each mine build keeps, in memory only,
/// what the last read of each cell found: a buried cell is parked (no pick looks at it) until <see cref="MineRecheck"/>, a few per
/// pick so the rereads spread out; a cell the colony digs out wakes its neighbours at once (<see cref="MineCellOpened"/>); an exposed
/// cell is read again before it is handed out, so a pawn is never sent to a cell that is buried now. A change the colony doesn't see
/// (another player's dig) is found once the buried cell beside it is reread.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>How long a mine cell's exposed/buried verdict stands before it is read again. Only a change the colony doesn't see waits
    /// for it (its own digs wake their neighbours at once), so it is long: 47,000 buried cells reread every 30 s are about 9,000 block
    /// reads a second over all picks, where every pick used to read 284,000.</summary>
    internal static readonly TimeSpan MineRecheck = TimeSpan.FromSeconds(30);

    /// <summary>Parked cells of one build woken for their recheck in one pick, at most (a whole build parked at once comes back over
    /// many picks, not as one pick reading thousands of cells).</summary>
    private const int MineWakeMax = 32;

    /// <summary>What the build search knows of one mine build's cells, index by index of its ghost list. Never saved: made again from
    /// the build when it is first searched (after a load, an edit of its cells, another world view).</summary>
    private sealed class MineCells
    {
        /// <summary>The ghost list (and its length, and the build's id) this was made for.</summary>
        public List<GhostState>? Of;
        public int Count;
        public string Id = "";
        /// <summary>The cell each verdict is about (a cell moved by a retry is read again).</summary>
        public P3[] At = [];
        /// <summary>When each verdict expires (UTC ticks); 0 = read it.</summary>
        public long[] Until = [];
        public bool[] Exposed = [];
        /// <summary>Each cell's lease key (<c>g:build|x,y,z</c>), made once.</summary>
        public string?[] Keys = [];
        /// <summary>Cells a pick looks at: all but the parked ones (open, out with a pawn, done, blocked, not read yet). Digs (mining work)
        /// in <see cref="Looked"/>, the blocks the mine puts in (building work) in <see cref="LookedPlaces"/>.</summary>
        public readonly List<int> Looked = new(), LookedPlaces = new();
        /// <summary>Whether each cell is a dig, as last seen (which looked list it is in).</summary>
        public bool[] Dig = [];
        /// <summary>The build's cells that put a block in (torches, ladders, rails): none means its building side has nothing to look at.</summary>
        public int Places;
        /// <summary>Where in each looked list a cell to do was last found (<see cref="AnyTodoOf"/>).</summary>
        public int DigsHint, PlacesHint;
        /// <summary>Parked: buried when last read (in <see cref="Parked"/>, not in <see cref="Looked"/>).</summary>
        public bool[] Out = [];
        /// <summary>Parked cells in the order their verdicts expire (an entry whose cell woke meanwhile is passed over).</summary>
        public readonly Queue<(int I, long Until)> Parked = new();
        /// <summary>Cell → index, for the neighbours of a dug cell.</summary>
        public readonly Dictionary<P3, int> Index = new();
        /// <summary>A cell moved: the index above is made again on the next search.</summary>
        public bool Moved;
    }

    /// <summary>One open cell of a mine, with what the search sorts by.</summary>
    private readonly record struct MineOpen(int I, int Y, bool Dig, long D2, bool Fresh);

    private readonly Dictionary<BuildState, MineCells> _mineCells = new(ReferenceEqualityComparer.Instance);
    private IStoryWorld? _mineCellsWorld;
    /// <summary>The open-cell list a mine search fills (taken while in use, so a nested search gets its own).</summary>
    private List<MineOpen>? _mineOpen = new();

    private static readonly (int X, int Y, int Z)[] Faces = [(0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, -1, 0)];

    /// <summary>
    /// A mine build's next job for the pawn, or null (and why it has none, as blocked work): its exposed cells top-down, nearest; one
    /// cell a job, with the exposed cells from the build's <see cref="MineCells"/> instead of six block reads per queued cell. A mine
    /// design's digs are mining work (<paramref name="digs"/>: the Mine search, <see cref="MineDesignJob"/>); the blocks it puts in
    /// (torches, ladders, rails) stay building work (the Build search, <see cref="BuildJobFor"/>). Owner 11-10: "these types of
    /// designs, when imported, should count as mining, not building".
    /// </summary>
    private ActiveJob? MineJobFor(IStoryWorld w, PawnState p, PawnLive live, BuildState b, int builders, P3 from, int cap, bool digs)
    {
        var ix = MineCellsOf(w, b);
        if (!digs && ix.Places == 0) { FinishIfNoneLeft(ix, b, digs); return null; } // nothing to put in: its digs are the miners'
        var open = _mineOpen ?? new List<MineOpen>();
        _mineOpen = null;
        try
        {
            long now = Now.UtcTicks;
            Wake(ix, now);
            bool todo = MineOpenCells(ix, b, from, cap, now, open, digs, out bool capped) || ParkedTodo(ix, b, digs);
            if (!todo) { FinishIfNoneLeft(ix, b, digs); return null; } // the cells left are the other work's
            if (capped) LeftUpTop(b);
            CollectionsMarshal.AsSpan(open).Sort(MineOrder);
            var scan = new GhostScan { Build = b, Builders = builders, From = from, Mine = true };
            int openCount = open.Count;
            for (int k = 0; k < open.Count; k++)
            {
                var e = open[k];
                var g = b.Ghosts[e.I];
                // Read again before it is handed out: a block may have gone in beside it since (a pillar, another player).
                if (!e.Fresh && !Exposed(g.At)) { Remember(ix, e.I, false, now); openCount--; continue; }
                if (GhostCell(w, p, live, ref scan, g, ix.Keys[e.I] ??= $"g:{b.Id}|{g.At}") is { } job) return job;
                if (scan.SkipBuild) return null;
            }
            // Its blocks wait in cells not dug out yet: that is mining work, maybe nobody's at hand.
            if (!digs && openCount == 0) { Blocked($"build {b.Id}: its blocks go in once the mine is dug to them (mining work)"); return null; }
            NoteBuildHeld(b, ref scan, openCount);
            return null;
        }
        finally
        {
            open.Clear();
            _mineOpen = open;
        }
    }

    /// <summary>A looked-at cell of the list is to do or out with a pawn. The search starts where the last one found one (finished cells
    /// stay in the list, so a long-dug tunnel would otherwise be walked on every builder's pick).</summary>
    private static bool AnyTodoOf(List<int> looked, List<GhostState> gs, ref int hint)
    {
        int n = looked.Count;
        if (hint >= n) hint = 0;
        for (int k = 0; k < n; k++)
        {
            int at = hint + k;
            if (at >= n) at -= n;
            var g = gs[looked[at]];
            if (g.Job && g.State is "todo" or "leased") { hint = at; return true; }
        }
        return false;
    }

    /// <summary>No cell of this work is left: the build is done (or stuck) only when none of the other work's is left either.</summary>
    private void FinishIfNoneLeft(MineCells ix, BuildState b, bool digs)
    {
        bool other = digs ? AnyTodoOf(ix.LookedPlaces, b.Ghosts, ref ix.PlacesHint) : AnyTodoOf(ix.Looked, b.Ghosts, ref ix.DigsHint);
        if (other || ParkedTodo(ix, b, null)) return;
        FinishOrStuck(b);
    }

    /// <summary>The work a build cell's job counts as: a mine design's digs are mining (pickaxes, miners' priorities); everything else,
    /// the blocks a mine design puts in too, is building.</summary>
    internal static string CellWork(BuildState b, GhostState g) => g.Remove && b.Kind == "mine" ? WorkTypes.Mine : WorkTypes.Build;

    /// <summary>
    /// The mine designs' digs (builds of kind <c>mine</c>), in queue order: the Mine work search's, after the mine marks and before the
    /// mine zones (a design is an order; a quarry digs on by itself). The share counts the miners; a pawn down in a quarry pit leaves
    /// cells up top to a miner up there; with no free slot nothing is dug (as a quarry: its dig would pick up nothing and ask again).
    /// </summary>
    private ActiveJob? MineDesignJob(PawnState p, PawnLive live)
    {
        if (World is not { } w) return null;
        var queue = OpenBuildsInOrder();
        try
        {
            int miners = 0, cap = int.MaxValue;
            bool counted = false;
            foreach (var (b, _) in queue)
            {
                if (b.Kind != "mine") continue;
                if (!counted)
                {
                    miners = Math.Max(1, WorkersOf(WorkTypes.Mine));
                    cap = BuildCap(p, live, WorkTypes.Mine);
                    counted = true;
                }
                if (MineJobFor(w, p, live, b, miners, live.Block, cap, digs: true) is not { } job) continue;
                if (live.FreeSlots <= 0) { PocketsFullNote(p, live, $"mine design {b.Label}"); return null; }
                return job;
            }
            return null;
        }
        finally
        {
            queue.Clear();
            _buildQueue = queue;
        }
    }

    /// <summary>Top-down first, digs before blocks, nearest, then queue order (the order the build search always had).</summary>
    private static int MineOrder(MineOpen a, MineOpen b) =>
        a.Y != b.Y ? b.Y.CompareTo(a.Y) : a.Dig != b.Dig ? (a.Dig ? -1 : 1) : a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.I.CompareTo(b.I);

    /// <summary>
    /// The looked-at cells of one work (<paramref name="digs"/>: the digs, else the blocks put in): the exposed ones still to do into
    /// <paramref name="open"/> (none higher than <paramref name="cap"/>: <paramref name="capped"/> says some were), the buried ones
    /// parked. True when any of them is to do or out with a pawn.
    /// </summary>
    private bool MineOpenCells(MineCells ix, BuildState b, P3 from, int cap, long now, List<MineOpen> open, bool digs, out bool capped)
    {
        capped = false;
        bool todo = false;
        var gs = b.Ghosts;
        var looked = digs ? ix.Looked : ix.LookedPlaces;
        for (int k = 0; k < looked.Count; k++)
        {
            int i = looked[k];
            var g = gs[i];
            if (g.Remove != digs)
            {
                // Turned from a dig into a block (or back) since the index was made: the other work's list takes it.
                ix.Dig[i] = g.Remove;
                ix.Places += g.Remove ? -1 : 1;
                (g.Remove ? ix.Looked : ix.LookedPlaces).Add(i);
                looked[k] = looked[^1];
                looked.RemoveAt(looked.Count - 1);
                k--;
                continue;
            }
            if (!g.Job) continue;
            if (g.State == "leased") { todo = true; continue; }
            if (g.State != "todo") continue;
            todo = true;
            if (!MineExposed(ix, i, g, now, out bool fresh))
            {
                // Buried: no pick looks at it until its recheck or a dig beside it.
                ix.Out[i] = true;
                ix.Parked.Enqueue((i, ix.Until[i]));
                looked[k] = looked[^1];
                looked.RemoveAt(looked.Count - 1);
                k--;
                continue;
            }
            var at = g.At;
            if (at.Y > cap) { capped = true; continue; }
            long dx = at.X - from.X, dy = at.Y - from.Y, dz = at.Z - from.Z;
            open.Add(new MineOpen(i, at.Y, g.Remove, dx * dx + dy * dy + dz * dz, fresh));
        }
        return todo;
    }

    /// <summary>A parked cell of one work (<paramref name="digs"/>; null: either) is to do or out with a pawn (the first found ends it:
    /// usually the first one looked at).</summary>
    private static bool ParkedTodo(MineCells ix, BuildState b, bool? digs)
    {
        var gs = b.Ghosts;
        for (int i = 0; i < gs.Count; i++)
            if (ix.Out[i] && (digs == null || ix.Dig[i] == digs) && gs[i].Job && gs[i].State is "todo" or "leased") return true;
        return false;
    }

    /// <summary>Parked cells whose verdict expired are looked at again (read anew), at most <see cref="MineWakeMax"/> a pick.</summary>
    private static void Wake(MineCells ix, long now)
    {
        for (int n = 0; n < MineWakeMax && ix.Parked.TryPeek(out var head) && head.Until <= now; )
        {
            ix.Parked.Dequeue();
            if (!ix.Out[head.I] || ix.Until[head.I] != head.Until) continue; // woken meanwhile (a dig beside it)
            Unpark(ix, head.I);
            n++;
        }
    }

    private static void Unpark(MineCells ix, int i)
    {
        ix.Until[i] = 0;
        if (!ix.Out[i]) return;
        ix.Out[i] = false;
        (ix.Dig[i] ? ix.Looked : ix.LookedPlaces).Add(i);
    }

    /// <summary>Whether the cell is exposed: the verdict while it stands, else read now (<paramref name="fresh"/>).</summary>
    private bool MineExposed(MineCells ix, int i, GhostState g, long now, out bool fresh)
    {
        if (ix.At[i] == g.At && ix.Until[i] > now)
        {
            fresh = false;
            return ix.Exposed[i];
        }
        if (ix.At[i] != g.At)
        {
            ix.At[i] = g.At;
            ix.Keys[i] = null;
            ix.Moved = true;
        }
        fresh = true;
        bool exposed = Exposed(g.At);
        Remember(ix, i, exposed, now);
        return exposed;
    }

    private static void Remember(MineCells ix, int i, bool exposed, long now)
    {
        ix.Exposed[i] = exposed;
        ix.Until[i] = now + MineRecheck.Ticks;
    }

    /// <summary>The build's cell index, made again when its ghost list, its id or the world view changed.</summary>
    private MineCells MineCellsOf(IStoryWorld w, BuildState b)
    {
        if (!ReferenceEquals(_mineCellsWorld, w))
        {
            _mineCells.Clear();
            _mineCellsWorld = w;
        }
        if (!_mineCells.TryGetValue(b, out var ix))
        {
            PruneMineCells();
            _mineCells[b] = ix = new MineCells();
        }
        if (ix.Of != b.Ghosts || ix.Count != b.Ghosts.Count || ix.Id != b.Id || ix.Moved) ResetMineCells(ix, b);
        return ix;
    }

    private static void ResetMineCells(MineCells ix, BuildState b)
    {
        var gs = b.Ghosts;
        int n = gs.Count;
        ix.Of = gs;
        ix.Count = n;
        ix.Id = b.Id;
        ix.Moved = false;
        ix.At = new P3[n];
        ix.Until = new long[n];
        ix.Exposed = new bool[n];
        ix.Keys = new string?[n];
        ix.Out = new bool[n];
        ix.Dig = new bool[n];
        ix.Places = 0;
        ix.DigsHint = ix.PlacesHint = 0;
        ix.Parked.Clear();
        ix.Looked.Clear();
        ix.LookedPlaces.Clear();
        ix.Index.Clear();
        for (int i = 0; i < n; i++)
        {
            ix.At[i] = gs[i].At;
            ix.Dig[i] = gs[i].Remove;
            if (gs[i].Remove) ix.Looked.Add(i);
            else { ix.LookedPlaces.Add(i); ix.Places++; }
            ix.Index.TryAdd(gs[i].At, i);
        }
    }

    /// <summary>Indexes of builds that left the queue go.</summary>
    private void PruneMineCells()
    {
        if (_mineCells.Count == 0) return;
        List<BuildState>? gone = null;
        foreach (var b in _mineCells.Keys)
            if (!Builds.Contains(b)) (gone ??= new()).Add(b);
        if (gone != null) foreach (var b in gone) _mineCells.Remove(b);
    }

    /// <summary>The colony opened the cell (dug, or found open): the mine cells beside it are read again on the next search.</summary>
    private void MineCellOpened(P3 at)
    {
        if (_mineCells.Count == 0) return;
        foreach (var ix in _mineCells.Values)
            foreach (var (dx, dy, dz) in Faces)
                if (ix.Index.TryGetValue(at.Add(dx, dy, dz), out int i)) Unpark(ix, i);
    }
}
