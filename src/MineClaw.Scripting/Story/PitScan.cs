using System.Text.Json.Nodes;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Scripting.Story;

/// <summary>
/// Where a body is stuck and the next move out (the <c>story.escape</c> SDK primitive's eyes; docs/web/story-mode.md "Stuck pawns").
/// Live 11-04: pawns stood for good in 1×1 holes two deep ("no path … it's 2 blocks up"), in waterfalls and in caves.
/// <list type="bullet">
/// <item><b>pit</b>: nothing over the body, and walking (step, 1-block jump, drop ≤ 3) reaches no more than <see cref="PitCells"/>
/// cells and never gets <see cref="Radius"/> from it. A room under a build's roof is never a pit (nothing is dug in a house).</item>
/// <item><b>cave</b>: natural ground over the body; the way out is the nearest walkable cell with none over it, else up.</item>
/// <item><b>water</b>: the body's cells hold water; the way out is the nearest dry standable cell.</item>
/// </list>
/// The move: <c>exit</c> (walk there), else <c>stair</c> (dig these cells, then step up one: a staircase; never below the feet,
/// never a block with liquid beside it or sand/gravel over it, never a container or anything as hard as obsidian), else a pillar
/// step (<c>pillar</c> with <c>blocks</c> of <c>block</c> held: place it at <c>feet</c>), else <c>gather</c> (wall cells that
/// drop a block to pillar with). Nothing at all: the colony is asked for help.
/// </summary>
public static class PitScan
{
    public const int Radius = 8, CaveRadius = 12, MaxNodes = 700;
    /// <summary>A shut-in floor bigger than this isn't a pit (an island, a walled yard): only a hole, a trench, a shaft is.</summary>
    public const int PitCells = 30;

    /// <summary>Blocks a pawn pillars with, best first (cobblestone, dirt, planks, stone, netherrack, sandstone, stone bricks, logs).</summary>
    private static readonly int[] PillarIds = [4, 3, 1, 87, 24, 98, 5, 17, 162]; // planks and logs last: a build's material
    /// <summary>Natural ground (a cave's roof), never a build's: stone, grass, dirt, bedrock, sand, gravel, ores, sandstone, obsidian,
    /// clay, netherrack, terracotta…</summary>
    private static readonly HashSet<int> Terrain = [1, 2, 3, 7, 12, 13, 14, 15, 16, 21, 24, 48, 49, 56, 73, 74, 82, 87, 88, 97, 110, 121, 129, 153, 159, 172, 179, 243];
    /// <summary>Wall blocks that drop a pillar block by hand (grass, dirt, planks, logs) and with a pickaxe (stone, cobble, netherrack, sandstone, stone bricks).</summary>
    private static readonly HashSet<int> HandDrops = [2, 3, 5, 17, 162], PickDrops = [1, 4, 87, 24, 98];
    private static readonly (int Dx, int Dz)[] Dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <param name="climb">No walkable way home from here (a ravine, a canyon, a deep hole the colony saw): treat it as a pit whatever
    /// its size, never walk back down to a cave exit, and say which walkable cell is nearest <paramref name="toward"/> (<c>approach</c>:
    /// the wall to cut the stair into).</param>
    public static JsonObject Scan(WorldState w, IGameData data, Vec3d pos, BlockPos? toward, bool climb = false)
    {
        var g = new PitGround(w, data, toward);
        var feet = g.FeetAt(pos);
        var o = new JsonObject { ["feet"] = ScanGround.Fmt(feet), ["pit"] = false };
        bool water = g.Liquid(feet.X, feet.Y, feet.Z) || g.Liquid(feet.X, feet.Y + 1, feet.Z);
        bool cave = !water && g.Cover(feet.X, feet.Y, feet.Z) >= 1;
        o["feet_y"] = feet.Y;
        if (!water && NotStuck(g, feet, cave, climb)) return o;
        if (water) return Water(o, g, feet);

        // Where it can walk to without digging (step, 1-block jump, drop up to 3).
        var walk = new PitWalk(g, feet, cave, climb);
        walk.Run();
        return WalksAway(walk, cave, climb) ? o : Pit(o, g, feet, walk, cave, climb);
    }

    /// <summary>It can walk away, or around a wide floor (an island, a courtyard): whatever failed, it isn't a pit.</summary>
    private static bool WalksAway(PitWalk walk, bool cave, bool climb) => !cave && !climb && (walk.Open || walk.Seen.Count > PitCells);

    /// <summary>Shut in: a pit (or cave, or climb); its exit, the approach toward home, how high the rim is, and the moves out.</summary>
    private static JsonObject Pit(JsonObject o, PitGround g, BlockPos feet, PitWalk walk, bool cave, bool climb)
    {
        o["pit"] = true;
        if (cave) o["cave"] = true;
        if (!climb && walk.Exit is { } e && e != feet) o["exit"] = ScanGround.Fmt(e);
        if (climb) Climb(o, g, feet, walk.Seen);
        if (!cave) o["need"] = RimNeed(g, feet);
        return Finish(o, g, feet, walk.Seen);
    }

    /// <summary>Under a build's roof (a house, never dug out of), or in the air (falling, mid-jump): not stuck.</summary>
    private static bool NotStuck(PitGround g, BlockPos feet, bool cave, bool climb) =>
        !cave && !climb && g.Roofed(feet.X, feet.Y, feet.Z) || !g.Solid(feet.X, feet.Y - 1, feet.Z);

    /// <summary>In water: out to the nearest dry standable cell around (a bank, a ledge), higher ones a little later.</summary>
    private static JsonObject Water(JsonObject o, PitGround g, BlockPos feet)
    {
        BlockPos? best = null;
        double bd = double.MaxValue;
        for (int dx = -4; dx <= 4; dx++)
        for (int dz = -4; dz <= 4; dz++)
        for (int dy = -1; dy <= 3; dy++)
        {
            int x = feet.X + dx, y = feet.Y + dy, z = feet.Z + dz;
            if (!g.Stand(x, y, z) || g.Liquid(x, y, z) || g.Liquid(x, y + 1, z)) continue;
            double d = Math.Abs(dx) + Math.Abs(dz) + Math.Max(0, dy) * 0.5 + g.Pref(x, z) * 0.01;
            if (d < bd) { bd = d; best = new BlockPos(x, y, z); }
        }
        o["pit"] = true;
        o["water"] = true;
        o["exit"] = best is { } b ? ScanGround.Fmt(b) : null;
        return Finish(o, g, feet, []);
    }

    /// <summary>The walkable cell nearest home: the stair starts from there (a ravine's floor runs along; its wall faces home).</summary>
    private static void Climb(JsonObject o, PitGround g, BlockPos feet, HashSet<BlockPos> seen)
    {
        o["climb"] = true;
        if (g.Toward == null) return;
        var near = seen.OrderBy(c => g.Pref(c.X, c.Z)).ThenBy(c => -c.Y).FirstOrDefault();
        if (g.Pref(near.X, near.Z) < g.Pref(feet.X, feet.Z) - 0.5) o["approach"] = ScanGround.Fmt(near);
    }

    /// <summary>How high the rim is: the blocks a nerd-pole needs to step out (null: no rim within 8).</summary>
    private static int? RimNeed(PitGround g, BlockPos feet)
    {
        int rim = int.MaxValue;
        foreach (var (dx, dz) in Dirs)
            for (int y = feet.Y + 1; y <= feet.Y + 8; y++)
                if (g.Stand(feet.X + dx, y, feet.Z + dz)) { rim = Math.Min(rim, y); break; }
        return rim == int.MaxValue ? null : Math.Max(0, rim - 1 - feet.Y);
    }

    /// <summary>The moves out: a stair step, a pillar step and its blocks, wall cells to gather blocks from.</summary>
    private static JsonObject Finish(JsonObject o, PitGround g, BlockPos feet, HashSet<BlockPos> seen)
    {
        o["stair"] = Stair(g, feet, seen);
        // Pillar: room to jump, and a block to put under itself.
        o["pillar"] = g.Free(feet.X, feet.Y + 1, feet.Z) && g.Free(feet.X, feet.Y + 2, feet.Z) && g.Solid(feet.X, feet.Y - 1, feet.Z);
        var (block, blocks) = g.Stock(PillarIds);
        o["block"] = block;
        o["blocks"] = blocks;
        o["gather"] = Gather(g, feet);
        if (g.Toward is { } tw) o["toward"] = ScanGround.Fmt(tw);
        return o;
    }

    /// <summary>A staircase step: dig what is in the way of a 1-block jump onto a neighbour, toward the target first; the fewest digs win.</summary>
    private static JsonObject? Stair(PitGround g, BlockPos feet, HashSet<BlockPos> seen)
    {
        JsonObject? stair = null;
        int fewest = int.MaxValue;
        foreach (var (dx, dz) in Dirs.OrderBy(d => g.Pref(feet.X + d.Dx, feet.Z + d.Dz)))
        {
            if (StairStep(g, feet, seen, feet.X + dx, feet.Z + dz) is not { } step || step.Dig.Count >= fewest) continue;
            fewest = step.Dig.Count;
            stair = new JsonObject { ["dig"] = new JsonArray(step.Dig.Select(c => (JsonNode)ScanGround.Fmt(c)).ToArray()), ["to"] = ScanGround.Fmt(step.To) };
        }
        return stair;
    }

    /// <summary>The cells to dig to step up onto the solid neighbour at x,z (null: not a safe new step).</summary>
    private static (List<BlockPos> Dig, BlockPos To)? StairStep(PitGround g, BlockPos feet, HashSet<BlockPos> seen, int x, int z)
    {
        if (!g.World.IsLoaded(x, z) || !g.Solid(x, feet.Y, z) || g.Liquid(x, feet.Y, z)) return null;
        var to = new BlockPos(x, feet.Y + 1, z);
        if (seen.Contains(to)) return null; // already walkable: no new ground
        if (StairDig(g, feet, x, z) is not { } dig) return null;
        if (g.Liquid(x, feet.Y + 3, z) && dig.Any(c => c.Y == feet.Y + 2)) return null;
        return (dig, to);
    }

    /// <summary>The head-room over the feet and the neighbour's two cells that aren't free (null: one isn't safe to dig).</summary>
    private static List<BlockPos>? StairDig(PitGround g, BlockPos feet, int x, int z)
    {
        var dig = new List<BlockPos>();
        foreach (var c in new[] { new BlockPos(feet.X, feet.Y + 2, feet.Z), new BlockPos(x, feet.Y + 2, z), new BlockPos(x, feet.Y + 1, z) })
        {
            if (g.Free(c.X, c.Y, c.Z)) continue;
            if (!g.Safe(c)) return null;
            dig.Add(c);
        }
        return dig;
    }

    /// <summary>Wall cells that drop something to pillar with (dirt by hand; stone, cobble, netherrack with a pickaxe), head height first.</summary>
    private static JsonArray Gather(PitGround g, BlockPos feet)
    {
        var data = g.Data;
        int pick = g.World.Inventory.PlayerSlots().Where(s => !s.IsEmpty && data.ItemExists(s.Id) && data.Item(s.Id).Name.EndsWith("pickaxe", StringComparison.Ordinal))
            .Select(s => (int)s.Id).DefaultIfEmpty(-1).First();
        var gather = new JsonArray();
        foreach (int dy in new[] { 1, 0 })
            foreach (var (dx, dz) in Dirs)
            {
                var c = new BlockPos(feet.X + dx, feet.Y + dy, feet.Z + dz);
                int id = g.World.GetBlock(c.X, c.Y, c.Z);
                bool yields = HandDrops.Contains(id) || PickDrops.Contains(id) && pick > 0 && data.CanHarvest(id, pick);
                if (yields && g.Safe(c)) gather.Add(ScanGround.Fmt(c));
            }
        return gather;
    }

    /// <summary>The scan's world tests plus what only a pit needs: natural cover, a build's roof, nearness to the target, safe digs.</summary>
    private sealed class PitGround(WorldState w, IGameData data, BlockPos? toward) : ScanGround(w, data)
    {
        public BlockPos? Toward => toward;

        /// <summary>The body's feet cell (on a slab / farmland: the cell over it).</summary>
        public BlockPos FeetAt(Vec3d pos)
        {
            var feet = new BlockPos((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 1e-6), (int)Math.Floor(pos.Z));
            return Solid(feet.X, feet.Y, feet.Z) && !Solid(feet.X, feet.Y + 1, feet.Z) ? feet with { Y = feet.Y + 1 } : feet;
        }

        /// <summary>Natural ground over a cell (a cave's roof); a build's roof or a canopy doesn't count.</summary>
        public int Cover(int x, int y, int z)
        {
            int n = 0;
            for (int yy = y + 2; yy < Math.Min(Height, y + 48); yy++)
                if (Terrain.Contains(World.GetBlock(x, yy, z))) n++;
            return n;
        }

        /// <summary>Anything solid over the cell but leaves.</summary>
        public bool Roofed(int x, int y, int z)
        {
            for (int yy = y + 2; yy < Math.Min(Height, y + 48); yy++)
                if (Solid(x, yy, z) && World.GetBlock(x, yy, z) is not (18 or 161)) return true;
            return false;
        }

        public double Pref(int x, int z) => toward is { } t ? Math.Sqrt((double)(t.X - x) * (t.X - x) + (double)(t.Z - z) * (t.Z - z)) : 0;

        /// <summary>A block a pawn may dig: not air, liquid, unknown, unbreakable, obsidian-hard, a container, under sand/gravel or next to liquid.</summary>
        public bool Safe(BlockPos p)
        {
            int id = World.GetBlock(p.X, p.Y, p.Z);
            if (id == 0 || id is >= 8 and <= 11 || !Data.BlockExists(id)) return false;
            var info = Data.Block(id);
            if (info.Hardness < 0 || info.Hardness >= 10 || info.Interactable) return false;
            if (Falling.Contains(World.GetBlock(p.X, p.Y + 1, p.Z))) return false; // sand/gravel over it would come down
            return !LiquidAround(p.X, p.Y, p.Z);
        }
    }

    /// <summary>The walk from the feet (breadth first): where it reaches, whether it gets away, a cave's exit.</summary>
    private sealed class PitWalk(PitGround g, BlockPos feet, bool cave, bool climb)
    {
        private readonly Queue<BlockPos> queue = new();
        private readonly int radius = cave ? CaveRadius : Radius;

        public HashSet<BlockPos> Seen { get; } = [feet];
        public bool Open { get; private set; }
        public BlockPos? Exit { get; private set; }

        public void Run()
        {
            queue.Enqueue(feet);
            while (queue.Count > 0 && !Open)
            {
                var c = queue.Dequeue();
                if (cave && !climb && g.Cover(c.X, c.Y, c.Z) == 0) { Exit = c; break; }
                Visit(c);
            }
        }

        private void Visit(BlockPos c)
        {
            foreach (var (dx, dz) in Dirs)
            {
                if (Step(c, c.X + dx, c.Z + dz) is not { } t || !Seen.Add(t)) continue;
                if (!cave && (Math.Abs(t.X - feet.X) >= radius || Math.Abs(t.Z - feet.Z) >= radius)) { Open = true; return; }
                if (Seen.Count > MaxNodes) { Open = !cave; return; }
                queue.Enqueue(t);
            }
        }

        /// <summary>The cell walking from <paramref name="c"/> toward x,z lands on: level, a drop of up to 3, a 1-block jump.</summary>
        private BlockPos? Step(BlockPos c, int x, int z)
        {
            if (!g.World.IsLoaded(x, z)) return null;
            if (g.Stand(x, c.Y, z)) return new BlockPos(x, c.Y, z);
            if (g.Free(x, c.Y, z) && g.Free(x, c.Y + 1, z)) return Drop(x, c.Y, z);
            if (g.Solid(x, c.Y, z) && g.Free(c.X, c.Y + 2, c.Z) && g.Stand(x, c.Y + 1, z)) return new BlockPos(x, c.Y + 1, z);
            return null;
        }

        private BlockPos? Drop(int x, int y, int z)
        {
            for (int d = 1; d <= 3; d++)
            {
                if (g.Stand(x, y - d, z)) return new BlockPos(x, y - d, z);
                if (!g.Free(x, y - d, z)) break;
            }
            return null;
        }
    }
}
