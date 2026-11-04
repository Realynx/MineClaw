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
    private static readonly int[] PillarIds = [4, 3, 5, 1, 87, 24, 98, 17, 162];
    private static readonly HashSet<int> Falling = [12, 13, 122, 145, 252];
    /// <summary>Natural ground (a cave's roof), never a build's: stone, grass, dirt, bedrock, sand, gravel, ores, sandstone, obsidian,
    /// clay, netherrack, terracotta…</summary>
    private static readonly HashSet<int> Terrain = [1, 2, 3, 7, 12, 13, 14, 15, 16, 21, 24, 48, 49, 56, 73, 74, 82, 87, 88, 97, 110, 121, 129, 153, 159, 172, 179, 243];
    private static readonly (int Dx, int Dz)[] Dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public static JsonObject Scan(WorldState w, IGameData data, Vec3d pos, BlockPos? toward)
    {
        int H = w.WorldHeight;
        var feet = new BlockPos((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 1e-6), (int)Math.Floor(pos.Z));
        bool Liquid(int x, int y, int z) => w.GetBlock(x, y, z) is >= 8 and <= 11;
        bool Solid(int x, int y, int z) => y >= 0 && y < H && BlockShapes.HasCollision(w, x, y, z);
        bool Free(int x, int y, int z) => y >= 0 && y < H && !Solid(x, y, z) && !Liquid(x, y, z) && w.GetBlock(x, y, z) is not (51 or 81 or 30);
        bool Stand(int x, int y, int z) => Solid(x, y - 1, z) && !Liquid(x, y - 1, z) && Free(x, y, z) && Free(x, y + 1, z);
        if (Solid(feet.X, feet.Y, feet.Z) && !Solid(feet.X, feet.Y + 1, feet.Z)) feet = feet with { Y = feet.Y + 1 }; // on a slab / farmland
        // Natural ground over a cell (a cave's roof); a build's roof or a canopy doesn't count.
        int Cover(int x, int y, int z)
        {
            int n = 0;
            for (int yy = y + 2; yy < Math.Min(H, y + 48); yy++)
                if (Terrain.Contains(w.GetBlock(x, yy, z))) n++;
            return n;
        }
        bool Roofed(int x, int y, int z)
        {
            for (int yy = y + 2; yy < Math.Min(H, y + 48); yy++)
                if (Solid(x, yy, z) && w.GetBlock(x, yy, z) is not (18 or 161)) return true;
            return false;
        }
        var o = new JsonObject { ["feet"] = Fmt(feet), ["pit"] = false };
        bool water = Liquid(feet.X, feet.Y, feet.Z) || Liquid(feet.X, feet.Y + 1, feet.Z);
        bool cave = !water && Cover(feet.X, feet.Y, feet.Z) >= 1;
        if (!water && !cave && Roofed(feet.X, feet.Y, feet.Z)) return o; // under a build's roof: a house, never dug out of
        if (!water && !Solid(feet.X, feet.Y - 1, feet.Z)) return o;   // in the air (falling, mid-jump): not stuck
        double Pref(int x, int z) => toward is { } t ? Math.Sqrt((double)(t.X - x) * (t.X - x) + (double)(t.Z - z) * (t.Z - z)) : 0;

        if (water)
        {
            // Out of the water: the nearest dry standable cell around (a bank, a ledge), higher ones a little later.
            BlockPos? best = null;
            double bd = double.MaxValue;
            for (int dx = -4; dx <= 4; dx++)
            for (int dz = -4; dz <= 4; dz++)
            for (int dy = -1; dy <= 3; dy++)
            {
                int x = feet.X + dx, y = feet.Y + dy, z = feet.Z + dz;
                if (!Stand(x, y, z) || Liquid(x, y, z) || Liquid(x, y + 1, z)) continue;
                double d = Math.Abs(dx) + Math.Abs(dz) + Math.Max(0, dy) * 0.5 + Pref(x, z) * 0.01;
                if (d < bd) { bd = d; best = new BlockPos(x, y, z); }
            }
            o["pit"] = true;
            o["water"] = true;
            o["exit"] = best is { } b ? Fmt(b) : null;
            return Finish(o, w, data, feet, toward, [], Solid, Free, Liquid, Pref, cave: false);
        }

        // Where it can walk to without digging (step, 1-block jump, drop up to 3).
        int radius = cave ? CaveRadius : Radius;
        var seen = new HashSet<BlockPos> { feet };
        var q = new Queue<BlockPos>();
        q.Enqueue(feet);
        bool open = false;
        BlockPos? exit = null;
        while (q.Count > 0 && !open)
        {
            var c = q.Dequeue();
            if (cave && Cover(c.X, c.Y, c.Z) == 0) { exit = c; break; }
            foreach (var (dx, dz) in Dirs)
            {
                int x = c.X + dx, z = c.Z + dz;
                if (!w.IsLoaded(x, z)) continue;
                BlockPos? to = null;
                if (Stand(x, c.Y, z)) to = new BlockPos(x, c.Y, z);
                else if (Free(x, c.Y, z) && Free(x, c.Y + 1, z))
                {
                    for (int d = 1; d <= 3; d++)
                    {
                        if (Stand(x, c.Y - d, z)) { to = new BlockPos(x, c.Y - d, z); break; }
                        if (!Free(x, c.Y - d, z)) break;
                    }
                }
                else if (Solid(x, c.Y, z) && Free(c.X, c.Y + 2, c.Z) && Stand(x, c.Y + 1, z)) to = new BlockPos(x, c.Y + 1, z);
                if (to is not { } t || !seen.Add(t)) continue;
                if (!cave && (Math.Abs(t.X - feet.X) >= radius || Math.Abs(t.Z - feet.Z) >= radius)) { open = true; break; }
                if (seen.Count > MaxNodes) { open = !cave; break; }
                q.Enqueue(t);
            }
        }
        // It can walk away, or around a wide floor (an island, a courtyard): whatever failed, it isn't a pit.
        if (!cave && (open || seen.Count > PitCells)) return o;
        o["pit"] = true;
        if (cave) o["cave"] = true;
        if (exit is { } e && e != feet) o["exit"] = Fmt(e);
        if (!cave)
        {
            // How high the rim is: the blocks a nerd-pole needs to step out.
            int rim = int.MaxValue;
            foreach (var (dx, dz) in Dirs)
                for (int y = feet.Y + 1; y <= feet.Y + 8; y++)
                    if (Stand(feet.X + dx, y, feet.Z + dz)) { rim = Math.Min(rim, y); break; }
            o["need"] = rim == int.MaxValue ? null : Math.Max(0, rim - 1 - feet.Y);
        }
        return Finish(o, w, data, feet, toward, seen, Solid, Free, Liquid, Pref, cave);
    }

    private static JsonObject Finish(JsonObject o, WorldState w, IGameData data, BlockPos feet, BlockPos? toward, HashSet<BlockPos> seen,
        Func<int, int, int, bool> Solid, Func<int, int, int, bool> Free, Func<int, int, int, bool> Liquid, Func<int, int, double> Pref, bool cave)
    {
        bool Safe(BlockPos p)
        {
            int id = w.GetBlock(p.X, p.Y, p.Z);
            if (id == 0 || id is >= 8 and <= 11 || !data.BlockExists(id)) return false;
            var info = data.Block(id);
            if (info.Hardness < 0 || info.Hardness >= 10 || info.Interactable) return false;
            if (Falling.Contains(w.GetBlock(p.X, p.Y + 1, p.Z))) return false; // sand/gravel over it would come down
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0) })
                if (Liquid(p.X + dx, p.Y + dy, p.Z + dz)) return false;
            return true;
        }
        // A staircase step: dig what is in the way of a 1-block jump onto a neighbour, toward the target first.
        JsonObject? stair = null;
        int fewest = int.MaxValue;
        foreach (var (dx, dz) in Dirs.OrderBy(d => Pref(feet.X + d.Dx, feet.Z + d.Dz)))
        {
            int x = feet.X + dx, z = feet.Z + dz;
            if (!w.IsLoaded(x, z) || !Solid(x, feet.Y, z) || Liquid(x, feet.Y, z)) continue;
            var to = new BlockPos(x, feet.Y + 1, z);
            if (seen.Contains(to)) continue; // already walkable: no new ground
            var dig = new List<BlockPos>();
            bool ok = true;
            foreach (var c in new[] { new BlockPos(feet.X, feet.Y + 2, feet.Z), new BlockPos(x, feet.Y + 2, z), new BlockPos(x, feet.Y + 1, z) })
            {
                if (Free(c.X, c.Y, c.Z)) continue;
                if (!Safe(c)) { ok = false; break; }
                dig.Add(c);
            }
            if (!ok || dig.Count >= fewest) continue;
            if (Liquid(x, feet.Y + 3, z) && dig.Any(c => c.Y == feet.Y + 2)) continue;
            fewest = dig.Count;
            stair = new JsonObject { ["dig"] = new JsonArray(dig.Select(c => (JsonNode)Fmt(c)).ToArray()), ["to"] = Fmt(to) };
        }
        o["stair"] = stair;
        // Pillar: room to jump, and a block to put under itself.
        o["pillar"] = Free(feet.X, feet.Y + 1, feet.Z) && Free(feet.X, feet.Y + 2, feet.Z) && Solid(feet.X, feet.Y - 1, feet.Z);
        var inv = w.Inventory;
        int blocks = 0;
        string? block = null;
        foreach (int id in PillarIds)
        {
            if (!data.ItemExists(id)) continue;
            int n = inv.CountItem(id);
            if (n <= 0) continue;
            blocks += n;
            block ??= data.Item(id).Name;
        }
        o["block"] = block;
        o["blocks"] = blocks;
        // Wall cells that drop something to pillar with (dirt by hand; stone, cobble, netherrack with a pickaxe), head height first.
        int pick = inv.PlayerSlots().Where(s => !s.IsEmpty && data.ItemExists(s.Id) && data.Item(s.Id).Name.EndsWith("pickaxe", StringComparison.Ordinal))
            .Select(s => (int)s.Id).DefaultIfEmpty(-1).First();
        var gather = new JsonArray();
        foreach (int dy in new[] { 1, 0 })
            foreach (var (dx, dz) in Dirs)
            {
                var c = new BlockPos(feet.X + dx, feet.Y + dy, feet.Z + dz);
                int id = w.GetBlock(c.X, c.Y, c.Z);
                bool yields = id is 2 or 3 or 5 or 17 or 162 || id is 1 or 4 or 87 or 24 or 98 && pick > 0 && data.CanHarvest(id, pick);
                if (yields && Safe(c)) gather.Add(Fmt(c));
            }
        o["gather"] = gather;
        if (toward is { } tw) o["toward"] = Fmt(tw);
        return o;
    }

    private static string Fmt(BlockPos p) => $"{p.X},{p.Y},{p.Z}";
}
