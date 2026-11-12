namespace MineClaw.Hive.Story;

/// <summary>
/// Where an idle pawn may stand (docs/web/story-mode.md "Idle behaviour"): a safe cell in the colony's home area. Safe means solid
/// ground (never a liquid, leaves, fire, cactus, magma, farmland, a fence top, furniture or a bed), two free cells over it (no crop,
/// no liquid), nothing that burns or hurts next to it, no door beside it (a doorway) and no ledge beside it with a drop of three or
/// more. Pure reads of the colony's world view; the director adds what is the colony's own (designations, build cells, storage and
/// workbench fronts, forbidden areas, the other pawns).
/// </summary>
public static class StoryWander
{
    /// <summary>A flag per block id (0..255, every id both versions have) for the given ids.</summary>
    private static bool[] Flags(params int[] ids)
    {
        var f = new bool[256];
        foreach (var id in ids) f[id] = true;
        return f;
    }

    private static bool In(bool[] flags, int id) => (uint)id < (uint)flags.Length && flags[id];

    /// <summary>Walk-through blocks besides air and plants: a torch, a snow layer, a carpet, rails, redstone dust and torches.</summary>
    private static readonly bool[] WalkThrough = Flags(50, 78, 171, 66, 27, 28, 157, 55, 76, 75);

    /// <summary>Cells a body walks through: air, short plants and flowers, a torch, a snow layer, a carpet, a rail, redstone dust.</summary>
    public static bool Passable(int id) => id == 0 || StoryBlocks.Plants.Contains(id) && id is not (106 or 111) || In(WalkThrough, id);

    /// <summary>What hurts or burns a body standing beside or on it: lava, fire, cactus, magma.</summary>
    public static bool Hurts(int id) => id is 10 or 11 or 51 or 81 or 213;

    /// <summary>
    /// Solid blocks that are no floor: cobweb, farmland, beds, chests, tables, furnaces, fences and gates, panes, trapdoors, iron bars,
    /// ladders, vines, walls; and not ground at all: crops, stems, cane, signs, levers, plates, buttons, wire, tripwire, repeaters,
    /// comparators.
    /// </summary>
    private static readonly bool[] NotFloor = Flags(
        30, 60, 26, 54, 146, 58, 61, 62, 85, 107, 113, 139, 96, 167, 101, 102, 160, 65, 106, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192,
        59, 141, 142, 207, 115, 83, 104, 105, 63, 68, 69, 70, 72, 77, 143, 147, 148, 131, 132, 93, 94, 149, 150, 176, 177);

    private static bool FloorOk(int id) =>
        !Passable(id) && !StoryBlocks.Liquids.Contains(id) && !StoryBlocks.Leaves.Contains(id) && !Hurts(id) && !StoryBlocks.IsDoor(id) && !In(NotFloor, id);

    /// <summary>Feet on solid ground with room for the head (no liquid, no crop in the way): the bare minimum to stand.</summary>
    public static bool Standable(IStoryWorld w, int x, int y, int z) =>
        y > 0 && y < w.Height - 1 && FloorOk(w.Block(x, y - 1, z).Id) && Passable(w.Block(x, y, z).Id) && Passable(w.Block(x, y + 1, z).Id);

    /// <summary>
    /// A cell an idle pawn may stop on: <see cref="Standable"/>, nothing that hurts on or around it, no door beside it, and no ledge
    /// beside it that drops three blocks or more (or into a liquid).
    /// </summary>
    public static bool Safe(IStoryWorld w, P3 c)
    {
        if (!w.Loaded(c.X, c.Z) || !Standable(w, c.X, c.Y, c.Z)) return false;
        foreach (var (dx, dz) in Around)
            if (!NeighbourSafe(w, c, c.X + dx, c.Z + dz)) return false;
        return true;
    }

    /// <summary>A neighbour column of a cell to idle on: in view, nothing that hurts in it, no door at head height, no ledge.</summary>
    private static bool NeighbourSafe(IStoryWorld w, P3 c, int x, int z)
    {
        if (!w.Loaded(x, z)) return false; // the edge of what the colony sees: not a place to idle
        for (int y = c.Y - 1; y <= c.Y + 1; y++)
        {
            int id = w.Block(x, y, z).Id;
            if (Hurts(id) || y >= c.Y && StoryBlocks.IsDoor(id)) return false;
        }
        if (!Passable(w.Block(x, c.Y, z).Id)) return true; // a wall: nothing to fall off there
        return NoLedge(w, c, x, z);
    }

    /// <summary>The neighbour is open: its ground is less than three down, and no step down into water or lava.</summary>
    private static bool NoLedge(IStoryWorld w, P3 c, int x, int z)
    {
        int depth = 0;
        for (int y = c.Y - 1; y > c.Y - 5 && y > 0; y--)
        {
            int id = w.Block(x, y, z).Id;
            if (StoryBlocks.Liquids.Contains(id)) return false; // a step down into water or lava
            if (!Passable(id)) break;
            depth++;
        }
        return depth < 3;
    }

    private static readonly (int Dx, int Dz)[] Around = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>The safe cell in column x,z nearest to <paramref name="refY"/> (within <paramref name="range"/> up or down), else null.</summary>
    public static P3? GroundNear(IStoryWorld w, int x, int z, int refY, int range = 6)
    {
        if (!w.Loaded(x, z)) return null;
        for (int i = 0; i <= 2 * range; i++)
        {
            int dy = (i + 1) / 2 * (i % 2 == 1 ? 1 : -1); // 0, +1, -1, +2, -2, ...
            int y = refY + dy;
            if (!Standable(w, x, y, z)) continue;
            var c = new P3(x, y, z);
            return Safe(w, c) ? c : null;
        }
        return null;
    }

    /// <summary>Under a roof: something solid 2..8 above the feet (inside a house, under a canopy of its own build).</summary>
    public static bool Roofed(IStoryWorld w, P3 c)
    {
        for (int y = c.Y + 2; y <= c.Y + 8 && y < w.Height; y++)
            if (!Passable(w.Block(c.X, y, c.Z).Id)) return true;
        return false;
    }

    /// <summary>Natural ground: stone, grass, dirt, bedrock, sand, gravel, ores, sandstone, obsidian, clay, netherrack, mycelium, terracotta…</summary>
    private static readonly HashSet<int> Terrain = [1, 2, 3, 7, 12, 13, 14, 15, 16, 21, 24, 48, 49, 56, 73, 74, 82, 87, 88, 97, 110, 121, 129, 153, 159, 172, 179, 243];

    /// <summary>
    /// Under ground: three or more blocks of natural ground over the cell (a cave, a ravine's overhang), as opposed to a roof of a
    /// build or a tree's canopy. Live 11-04: pawns walked into caves and couldn't get back out; cave cells are never wandered to, cave
    /// mobs are never guarded against, cave plants are never cut, and a pawn found down there without mining to do heads back up.
    /// An unloaded column reads as air: not under ground.
    /// </summary>
    public static bool Underground(IStoryWorld w, P3 c)
    {
        if (!w.Loaded(c.X, c.Z)) return false;
        int n = 0;
        for (int y = c.Y + 2; y < Math.Min(w.Height, c.Y + 48); y++)
            if (Terrain.Contains(w.Block(c.X, y, c.Z).Id) && ++n >= 3) return true;
        return false;
    }

    /// <summary>The ring around the feet the local surface is read from (columns this far out, every other one), how far under (or
    /// over) that surface the feet must be to be down in (up on) something, and how close to it a walk must get to be out.</summary>
    private const int SurfaceRing = 6, SurfaceDepth = 4, SurfaceReach = 2;
    /// <summary>The most cells a walk from the feet looks at.</summary>
    private const int EnclosedNodes = 3000;
    /// <summary>How far under the feet the ring's ground is looked for: 8 for a pawn down in something; for one up on something,
    /// as far as the tallest poles seen live (live 11-12: 18 to 39 blocks).</summary>
    private const int SurfaceBelow = 8, MaroonedBelow = 48;

    /// <summary>Where a pawn is cut off from the ground around it (<see cref="CutOffAt"/>).</summary>
    public enum Cut
    {
        /// <summary>Not cut off, or the colony can't tell.</summary>
        No,
        /// <summary>Shut in under the ground around it: a pit, a hole, a pocket, a cave, a ravine's floor (<see cref="Enclosed"/>).</summary>
        Below,
        /// <summary>Marooned over the ground around it with no walk down: a pillar's top, a roof, a tree, a ledge, a floating island.</summary>
        Above,
    }

    /// <summary>
    /// Shut in at <paramref name="feet"/>: down in something (at least <see cref="SurfaceDepth"/> under the ground around it, the
    /// median height of the columns <see cref="SurfaceRing"/> out) and walking from there (level, a 1-block jump with head room, a drop
    /// of up to 3) never gets back up to within <see cref="SurfaceReach"/> of that ground: a pit, a 1×1 hole, a closed pocket, a cave,
    /// a ravine's floor however long (live 11-04: 3 wide, 13 deep). Only then is a pawn whose targets were out of reach stuck (live
    /// 11-12: pawns on open ground were sent to escape and poled up 18 to 39 blocks). Level with the ground around, it is not; nor
    /// when the colony can't tell (feet over nothing solid, too little of the ring in view, a walk that meets the edge of the view or
    /// runs past <see cref="EnclosedNodes"/> cells): the pawn's own <c>story.go</c> still escapes a pit it finds itself in.
    /// </summary>
    public static bool Enclosed(IStoryWorld w, P3 feet) => CutOffAt(w, feet) == Cut.Below;

    /// <summary>
    /// Marooned at <paramref name="feet"/>: up on something (at least <see cref="SurfaceDepth"/> over the ground around it) and walking
    /// from there never gets back down to within <see cref="SurfaceReach"/> of that ground: the top of a pillar, a roof, a tree, a
    /// ledge, a floating island of blocks. Review 11-12: once only a pawn shut in below was stuck, a pawn stranded up high was never
    /// marked stuck, escaped or teleported. Fox 11-12: "If a bot can't navigate something without deleting its nerd pole, then it
    /// should teleport." The same "can't tell" cases as <see cref="Enclosed"/> are not marooned.
    /// </summary>
    public static bool Marooned(IStoryWorld w, P3 feet) => CutOffAt(w, feet) == Cut.Above;

    /// <summary>
    /// Cut off from the ground around <paramref name="feet"/>, below it (<see cref="Enclosed"/>) or above it (<see cref="Marooned"/>).
    /// One read of the ring and at most one walk of <see cref="EnclosedNodes"/> cells: asked only when a run of targets out of reach
    /// ends, never per tick.
    /// </summary>
    public static Cut CutOffAt(IStoryWorld w, P3 feet)
    {
        if (feet.Y <= 0 || feet.Y >= w.Height - 2) return Cut.No;
        if (!Passable(w.Block(feet.X, feet.Y, feet.Z).Id) && Passable(w.Block(feet.X, feet.Y + 1, feet.Z).Id)) feet = feet.Add(0, 1, 0); // on a slab, farmland
        int under = w.Block(feet.X, feet.Y - 1, feet.Z).Id;
        if (Passable(under) || StoryBlocks.Liquids.Contains(under)) return Cut.No;
        var tops = RingTops(w, feet, out int samples);
        if (Median(tops.Where(y => y >= feet.Y - SurfaceBelow).ToList(), samples) is { } surface && feet.Y <= surface - SurfaceDepth)
            return WalksTo(w, feet, t => t.Y >= surface - SurfaceReach) ? Cut.No : Cut.Below;
        if (Median(tops, samples) is { } ground && feet.Y >= ground + SurfaceDepth)
            return WalksTo(w, feet, t => t.Y <= ground + SurfaceReach) ? Cut.No : Cut.Above;
        return Cut.No;
    }

    /// <summary>Walking from the feet reaches a cell that is <paramref name="out"/>; true too when the colony can't tell (the walk
    /// meets the edge of the view, or runs past <see cref="EnclosedNodes"/> cells).</summary>
    private static bool WalksTo(IStoryWorld w, P3 feet, Func<P3, bool> @out)
    {
        var seen = new HashSet<P3> { feet };
        var queue = new Queue<P3>();
        queue.Enqueue(feet);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            foreach (var (dx, dz) in Around)
            {
                if (!w.Loaded(c.X + dx, c.Z + dz)) return true; // the edge of the view: can't tell
                if (WalkStep(w, c, c.X + dx, c.Z + dz) is not { } t || !seen.Add(t)) continue;
                if (@out(t)) return true; // back at the ground around: it walks out
                if (seen.Count > EnclosedNodes) return true;
                queue.Enqueue(t);
            }
        }
        return false;
    }

    /// <summary>The highest standable cell (never on leaves) of each column on a ring <see cref="SurfaceRing"/> out, every other one,
    /// looked for from 40 over the feet down to <see cref="MaroonedBelow"/> under them; <paramref name="samples"/>: the columns looked
    /// at.</summary>
    private static List<int> RingTops(IStoryWorld w, P3 feet, out int samples)
    {
        var tops = new List<int>();
        int ring = SurfaceRing;
        samples = 0;
        for (int i = -ring; i <= ring; i += 2)
            foreach (var (x, z) in new[] { (feet.X + i, feet.Z - ring), (feet.X + i, feet.Z + ring), (feet.X - ring, feet.Z + i), (feet.X + ring, feet.Z + i) })
            {
                samples++;
                if (!w.Loaded(x, z)) continue;
                for (int y = Math.Min(w.Height - 2, feet.Y + 40); y >= Math.Max(1, feet.Y - MaroonedBelow); y--)
                    if (Standable(w, x, y, z)) { tops.Add(y); break; }
            }
        return tops;
    }

    /// <summary>The ground around a spot: the median of the ring's tops; null when fewer than half of the columns looked at are in
    /// view and have ground.</summary>
    private static int? Median(List<int> tops, int samples)
    {
        if (tops.Count * 2 < samples) return null;
        tops.Sort();
        return tops[tops.Count / 2];
    }

    /// <summary>Feet fit in a cell over ground (no liquid in it, none as its floor).</summary>
    private static bool Stands(IStoryWorld w, int x, int y, int z)
    {
        if (y <= 0 || y >= w.Height - 1) return false;
        int floor = w.Block(x, y - 1, z).Id;
        return !Passable(floor) && !StoryBlocks.Liquids.Contains(floor) && Passable(w.Block(x, y, z).Id) && Passable(w.Block(x, y + 1, z).Id);
    }

    /// <summary>The cell walking from <paramref name="c"/> toward x,z lands on (level, a drop of up to 3, a 1-block jump), else null.</summary>
    private static P3? WalkStep(IStoryWorld w, P3 c, int x, int z)
    {
        if (!w.Loaded(x, z)) return null;
        if (Stands(w, x, c.Y, z)) return new P3(x, c.Y, z);
        if (Passable(w.Block(x, c.Y, z).Id) && Passable(w.Block(x, c.Y + 1, z).Id))
        {
            for (int d = 1; d <= 3; d++)
            {
                if (Stands(w, x, c.Y - d, z)) return new P3(x, c.Y - d, z);
                if (!Passable(w.Block(x, c.Y - d, z).Id)) break;
            }
            return null;
        }
        return Passable(w.Block(c.X, c.Y + 2, c.Z).Id) && Stands(w, x, c.Y + 1, z) ? new P3(x, c.Y + 1, z) : null;
    }

    /// <summary>Horizontal distance between two cells; cells more than two storeys apart never crowd each other.</summary>
    public static double Gap(P3 a, P3 b) =>
        Math.Abs(a.Y - b.Y) > 2 ? double.PositiveInfinity : Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z));
}
