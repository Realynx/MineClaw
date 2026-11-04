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
    /// <summary>Cells a body walks through: air, short plants and flowers, a torch, a snow layer, a carpet, a rail, redstone dust.</summary>
    public static bool Passable(int id) =>
        id == 0 || StoryBlocks.Plants.Contains(id) && id != 106 && id != 111 || id is 50 or 78 or 171 or 66 or 27 or 28 or 157 or 55 or 76 or 75;

    /// <summary>What hurts or burns a body standing beside or on it: lava, fire, cactus, magma.</summary>
    public static bool Hurts(int id) => id is 10 or 11 or 51 or 81 or 213;

    private static bool FloorOk(int id) =>
        !Passable(id) && !StoryBlocks.Liquids.Contains(id) && !StoryBlocks.Leaves.Contains(id) && !Hurts(id) && !StoryBlocks.IsDoor(id)
        && id is not (30 or 60 or 26 or 54 or 146 or 58 or 61 or 62 or 85 or 107 or 113 or 139 or 96 or 167 or 101 or 102 or 160 or 65 or 106)
        && id is not (>= 183 and <= 192)
        // Not ground at all: crops, stems, cane, signs, levers, plates, buttons, wire, tripwire, repeaters, comparators.
        && id is not (59 or 141 or 142 or 207 or 115 or 83 or 104 or 105 or 63 or 68 or 69 or 70 or 72 or 77 or 143 or 147 or 148 or 131 or 132
            or 93 or 94 or 149 or 150 or 176 or 177);

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
        {
            int x = c.X + dx, z = c.Z + dz;
            if (!w.Loaded(x, z)) return false; // the edge of what the colony sees: not a place to idle
            for (int y = c.Y - 1; y <= c.Y + 1; y++)
            {
                int id = w.Block(x, y, z).Id;
                if (Hurts(id) || y >= c.Y && StoryBlocks.IsDoor(id)) return false;
            }
            if (!Passable(w.Block(x, c.Y, z).Id)) continue; // a wall: nothing to fall off there
            // The neighbour is open: how far down is its ground?
            int depth = 0;
            for (int y = c.Y - 1; y > c.Y - 5 && y > 0; y--)
            {
                int id = w.Block(x, y, z).Id;
                if (StoryBlocks.Liquids.Contains(id)) return false; // a step down into water or lava
                if (!Passable(id)) break;
                depth++;
            }
            if (depth >= 3) return false;
        }
        return true;
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

    /// <summary>Horizontal distance between two cells; cells more than two storeys apart never crowd each other.</summary>
    public static double Gap(P3 a, P3 b) =>
        Math.Abs(a.Y - b.Y) > 2 ? double.PositiveInfinity : Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z));
}
