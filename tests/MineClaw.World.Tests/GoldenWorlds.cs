using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

/// <summary>
/// Seeded worlds for the pathing and physics goldens (complexity refactor WP04): flat fields with obstacles, hills with trees,
/// ditches and channels beside farmland, a quarry with a spiral stair, caves and a ravine, void islands and pillars, water, and
/// doorways. Real block data of either version (<see cref="Blocks.Get"/> under a <see cref="GameData.Override"/> for 1.12.2).
/// Blocks span x,z −24..23; ground tops out around y 63; below the ground is the void unless the kind fills it.
/// </summary>
internal static class GoldenWorlds
{
    public const int Stone = 1, Grass = 2, Dirt = 3, Cobble = 4, Planks = 5, Sapling = 6, Bedrock = 7, Water = 8, StillWater = 9,
        Lava = 10, StillLava = 11, Sand = 12, Gravel = 13, Log = 17, Leaves = 18, Glass = 20, Web = 30, Slab = 44, Torch = 50,
        Stairs = 53, Chest = 54, Wheat = 59, Farmland = 60, Door = 64, Ladder = 65, CobbleStairs = 67, Lever = 69, Plate = 70,
        IronDoor = 71, Button = 77, Snow = 78, Ice = 79, Cactus = 81, Reeds = 83, Fence = 85, SoulSand = 88, Trapdoor = 96,
        Bars = 101, Pane = 102, Vine = 106, Gate = 107;

    public static readonly string[] Kinds = ["field", "hills", "ditches", "quarry", "cave", "islands", "water", "doors"];

    public const int Min = -24, Max = 23;

    /// <summary>Scopes the version's block data (1.12.2) for the calling flow; dispose after the run.</summary>
    public static IDisposable? Data(bool release1122) => release1122 ? GameData.Override(Release1122GameData.Instance) : null;

    public static WorldState Empty(bool release1122)
    {
        var w = release1122 ? new WorldState(height: 256, physics: Release1122PhysicsRules.Instance) : new WorldState();
        int h = release1122 ? 256 : 128;
        const int size = 64;
        w.ApplyChunkRegion(-32, 0, -32, size, h, size, new byte[size * h * size], null, null, null);
        return w;
    }

    public static WorldState Build(string kind, int seed, bool release1122)
    {
        var w = Empty(release1122);
        var rng = new Random(seed * 7919 + Array.IndexOf(Kinds, kind) * 101 + (release1122 ? 1 : 0));
        switch (kind)
        {
            case "field": Field(w, rng); break;
            case "hills": Hills(w, rng); break;
            case "ditches": Ditches(w, rng); break;
            case "quarry": Quarry(w, rng); break;
            case "cave": Cave(w, rng); break;
            case "islands": Islands(w, rng); break;
            case "water": WaterWorld(w, rng); break;
            case "doors": Doors(w, rng); break;
        }
        return w;
    }

    private static void Fill(WorldState w, int x0, int y0, int z0, int x1, int y1, int z1, int id, int meta = 0)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
        for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            w.Set(x, y, z, id, meta);
    }

    private static void Ground(WorldState w, int top = 63, int depth = 4)
    {
        Fill(w, Min, top - depth + 1, Min, Max, top - 1, Max, Dirt);
        Fill(w, Min, top, Min, Max, top, Max, Grass);
    }

    private static int R(Random rng, int lo, int hi) => rng.Next(lo, hi + 1);

    private static void Field(WorldState w, Random rng)
    {
        Ground(w);
        for (int i = 0; i < 70; i++)
        {
            int x = R(rng, Min + 1, Max - 1), z = R(rng, Min + 1, Max - 1);
            switch (rng.Next(16))
            {
                case 0: w.Set(x, 64, z, Slab, rng.Next(2) * 8); break;
                case 1: Fill(w, x, 64, z, x, 64, z + R(rng, 0, 4), Fence); break;
                case 2: Fill(w, x, 64, z, x + R(rng, 0, 3), 63 + R(rng, 1, 3), z, Cobble); break;
                case 3: Fill(w, x, 60, z, x, 63, z, 0); break; // a hole into the void
                case 4: Fill(w, x, 62, z, x + 1, 63, z + 1, 0); break; // a 2-deep pit with a floor
                case 5: w.Set(x, 63, z, StillLava); break;
                case 6: w.Set(x, 63, z, Sand); w.Set(x, 64, z, Cactus); break;
                case 7: w.Set(x, 64, z, Snow, rng.Next(4)); break;
                case 8: w.Set(x, 63, z, SoulSand); break;
                case 9: Fill(w, x, 64, z, x + R(rng, 0, 3), 64, z, Pane); break;
                case 10: w.Set(x, 64, z, Web); break;
                case 11: w.Set(x, 64, z, rng.Next(2) == 0 ? Stairs : CobbleStairs, rng.Next(4)); break;
                case 12: w.Set(x, 64, z, Chest, 2); break;
                case 13: w.Set(x, 64, z, Torch, 5); break;
                case 14: Fill(w, x, 64, z, x, 64 + R(rng, 1, 4), z, Log); break;
                case 15: w.Set(x, 63, z, Ice); break;
            }
        }
    }

    private static int[,] Heights(Random rng, int baseY, int amp)
    {
        int n = Max - Min + 1;
        var h = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            h[i, j] = rng.NextDouble() * amp;
        for (int pass = 0; pass < 3; pass++)
        {
            var s = new double[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double sum = 0; int c = 0;
                for (int di = -2; di <= 2; di++)
                for (int dj = -2; dj <= 2; dj++)
                {
                    int a = i + di, b = j + dj;
                    if (a < 0 || b < 0 || a >= n || b >= n) continue;
                    sum += h[a, b]; c++;
                }
                s[i, j] = sum / c;
            }
            h = s;
        }
        var r = new int[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            r[i, j] = baseY + (int)Math.Round((h[i, j] - amp / 2.0) * 3);
        return r;
    }

    private static void Hills(WorldState w, Random rng)
    {
        var h = Heights(rng, 63, 4);
        for (int x = Min; x <= Max; x++)
        for (int z = Min; z <= Max; z++)
        {
            int top = h[x - Min, z - Min];
            Fill(w, x, 56, z, x, top - 1, z, Dirt);
            w.Set(x, top, z, Grass);
        }
        for (int i = 0; i < 9; i++)
        {
            int x = R(rng, Min + 2, Max - 2), z = R(rng, Min + 2, Max - 2), top = h[x - Min, z - Min];
            int th = R(rng, 4, 6);
            Fill(w, x - 2, top + th - 2, z - 2, x + 2, top + th, z + 2, Leaves);
            Fill(w, x, top + 1, z, x, top + th, z, Log);
        }
        for (int i = 0; i < 3; i++)
        {
            int x = R(rng, Min + 3, Max - 4), z = R(rng, Min + 3, Max - 4), top = h[x - Min, z - Min];
            Fill(w, x, top - 1, z, x + R(rng, 1, 3), top, z + R(rng, 1, 3), StillWater);
        }
        int lx = R(rng, Min + 3, Max - 3), lz = R(rng, Min + 3, Max - 3);
        w.Set(lx, h[lx - Min, lz - Min], lz, StillLava);
        for (int i = 0; i < 6; i++)
        {
            int x = R(rng, Min + 1, Max - 1), z = R(rng, Min + 1, Max - 1);
            w.Set(x, h[x - Min, z - Min] + 1, z, Vine, 1 << rng.Next(4));
        }
    }

    private static void Ditches(WorldState w, Random rng)
    {
        Ground(w);
        int x = Min + 3;
        while (x < Max - 3)
        {
            bool wet = rng.Next(3) > 0;
            int z0 = R(rng, Min + 1, -4), z1 = R(rng, 4, Max - 1);
            Fill(w, x, 63, z0, x, 63, z1, wet ? StillWater : 0);
            if (rng.Next(2) == 0) Fill(w, x, 62, z0, x, 62, z1, Dirt);
            for (int side = -1; side <= 1; side += 2)
            {
                if (rng.Next(3) == 0) continue;
                for (int z = z0; z <= z1; z++)
                {
                    w.Set(x + side, 63, z, Farmland, 7);
                    int r = rng.Next(5);
                    if (r < 3) w.Set(x + side, 64, z, Wheat, rng.Next(8));
                    else if (r == 3) w.Set(x + side, 64, z, Sapling);
                }
            }
            if (rng.Next(3) == 0) Fill(w, x + 2, 64, z0, x + 2, 64, z1, Reeds);
            x += R(rng, 4, 7);
        }
        // A double-wide trench and a dry hole.
        Fill(w, -2, 63, Min + 2, -1, 63, Min + 6, 0);
        w.Set(R(rng, 0, 10), 63, R(rng, 0, 10), 0);
    }

    private static void Quarry(WorldState w, Random rng)
    {
        Fill(w, Min, 40, Min, Max, 62, Max, Stone);
        Fill(w, Min, 63, Min, Max, 63, Max, Grass);
        Fill(w, Min, 39, Min, Max, 39, Max, Bedrock);
        int r = R(rng, 6, 9), floor = R(rng, 44, 50);
        Fill(w, -r, floor, -r, r, 63, r, 0);
        // A spiral stair round the walls: one block down per step, against the wall.
        int y = 62, x = -r, z = -r, dir = 0;
        (int Dx, int Dz)[] dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];
        while (y > floor)
        {
            w.Set(x, y, z, Stone);
            for (int yy = floor; yy < y; yy++) w.Set(x, yy, z, Stone);
            int nx = x + dirs[dir].Dx, nz = z + dirs[dir].Dz;
            if (Math.Abs(nx) > r || Math.Abs(nz) > r) { dir = (dir + 1) % 4; continue; }
            x = nx; z = nz; y--;
        }
        // A ladder on the far wall and a torch.
        for (int yy = floor; yy <= 63; yy++) w.Set(r, yy, 0, Ladder, 4);
        Fill(w, r + 1, floor, 0, r + 1, 63, 0, Stone);
        w.Set(0, floor, 0, Torch, 5);
        if (rng.Next(2) == 0) Fill(w, -2, floor, 2, 2, floor, 4, StillWater);
    }

    private static void Cave(WorldState w, Random rng)
    {
        Fill(w, Min, 44, Min, Max, 69, Max, Stone);
        Fill(w, Min, 70, Min, Max, 70, Max, Grass);
        for (int worm = 0; worm < 4; worm++)
        {
            double x = R(rng, -10, 10), y = R(rng, 50, 64), z = R(rng, -10, 10);
            double yaw = rng.NextDouble() * Math.PI * 2;
            for (int i = 0; i < 70; i++)
            {
                for (int dy = 0; dy <= 1; dy++)
                for (int d = 0; d <= 1; d++)
                {
                    int bx = (int)Math.Floor(x) + d, by = (int)Math.Floor(y) + dy, bz = (int)Math.Floor(z);
                    if (bx > Min && bx < Max && bz > Min && bz < Max && by > 44) w.Set(bx, by, bz, 0);
                }
                yaw += (rng.NextDouble() - 0.5) * 0.8;
                x += Math.Cos(yaw); z += Math.Sin(yaw);
                y = Math.Clamp(y + (rng.NextDouble() - 0.5) * 1.2, 46, 69);
                x = Math.Clamp(x, Min + 2, Max - 3); z = Math.Clamp(z, Min + 2, Max - 3);
            }
        }
        // A ravine: two wide, down to a water floor.
        int rx = R(rng, -6, 6), depth = R(rng, 10, 18);
        Fill(w, rx, 70 - depth, -15, rx + 1, 70, 15, 0);
        Fill(w, rx, 70 - depth, -15, rx + 1, 70 - depth, 15, StillWater);
        // A hole down from the surface.
        int hx = R(rng, -12, 12), hz = R(rng, -12, 12);
        Fill(w, hx, 55, hz, hx, 70, hz, 0);
        for (int y = 55; y <= 70; y++) w.Set(hx + 1, y, hz, Ladder, 5);
    }

    private static void Islands(WorldState w, Random rng)
    {
        // Skyblock: islands over the void, gaps of 1..4, some higher or lower.
        int x = -20;
        int y = 63;
        while (x < 18)
        {
            int z = R(rng, -4, 4), size = R(rng, 1, 3);
            Fill(w, x, y - 1, z - size, x + size, y, z + size, Cobble);
            if (rng.Next(4) == 0) w.Set(x, y + 1, z, Torch, 5);
            x += size + 1 + R(rng, 1, 3);
            y = Math.Clamp(y + R(rng, -2, 1), 58, 66);
        }
        // Pillars.
        for (int i = 0; i < 12; i++)
        {
            int px = R(rng, -18, 18), pz = R(rng, 6, 20);
            Fill(w, px, 55, pz, px, 55 + R(rng, 4, 10), pz, Stone);
        }
        // A lone far island and a lava pool island.
        Fill(w, 4, 62, -20, 8, 63, -16, Dirt);
        Fill(w, -10, 62, -20, -6, 63, -16, Cobble);
        w.Set(-8, 63, -18, StillLava);
    }

    private static void WaterWorld(WorldState w, Random rng)
    {
        var h = Heights(rng, 63, 3);
        for (int x = Min; x <= Max; x++)
        for (int z = Min; z <= Max; z++)
        {
            int top = h[x - Min, z - Min];
            Fill(w, x, 50, z, x, top - 1, z, Dirt);
            w.Set(x, top, z, Grass);
        }
        // A lake of varying depth.
        int cx = R(rng, -8, 8), cz = R(rng, -8, 8), rad = R(rng, 5, 8);
        for (int x = cx - rad; x <= cx + rad; x++)
        for (int z = cz - rad; z <= cz + rad; z++)
        {
            double d = Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
            if (d > rad) continue;
            int depth = Math.Max(1, (int)(5 - d * 5 / rad));
            int top = 62;
            Fill(w, x, top - depth + 1, z, x, top + 3, z, 0);
            Fill(w, x, top - depth + 1, z, x, top, z, StillWater);
        }
        // A stream of flowing water down a slope, and a waterfall off a cliff.
        int sx = R(rng, -20, -12);
        for (int i = 0; i < 8; i++) w.Set(sx + i, 64, -18, Water, Math.Min(7, i));
        int fx = R(rng, 10, 18);
        Fill(w, fx, 50, 14, fx + 3, 72, 20, Stone);
        for (int y = 64; y <= 72; y++) w.Set(fx - 1, y, 16, Water, 8);
    }

    private static void Doors(WorldState w, Random rng)
    {
        Ground(w);
        // Rooms with walls, wooden doors, iron doors with a button/lever/plate, fence gates, trapdoors at head height.
        for (int room = 0; room < 4; room++)
        {
            int x0 = -20 + room * 11, z0 = R(rng, -16, -2);
            int x1 = x0 + 8, z1 = z0 + R(rng, 8, 12);
            for (int y = 64; y <= 66; y++)
            {
                Fill(w, x0, y, z0, x1, y, z0, Planks); Fill(w, x0, y, z1, x1, y, z1, Planks);
                Fill(w, x0, y, z0, x0, y, z1, Planks); Fill(w, x1, y, z0, x1, y, z1, Planks);
            }
            int dz = R(rng, z0 + 2, z1 - 2);
            switch (rng.Next(4))
            {
                case 0:
                    w.Set(x0, 64, dz, Door, rng.Next(4)); w.Set(x0, 65, dz, Door, 8);
                    break;
                case 1:
                    w.Set(x0, 64, dz, IronDoor, 0); w.Set(x0, 65, dz, IronDoor, 8);
                    if (rng.Next(2) == 0) w.Set(x0 - 1, 64, dz, Plate);
                    else w.Set(x0, 65, dz - 1 < z0 ? dz + 1 : dz - 1, rng.Next(2) == 0 ? Button : Lever, 2);
                    break;
                case 2:
                    w.Set(x0, 64, dz, Gate, rng.Next(4)); w.Set(x0, 65, dz, 0);
                    break;
                default:
                    w.Set(x0, 64, dz, 0); w.Set(x0, 65, dz, Trapdoor, rng.Next(4));
                    break;
            }
            w.Set(x1, 64, R(rng, z0 + 2, z1 - 2), Door, 0);
            w.Set(x1, 65, R(rng, z0 + 2, z1 - 2), 0);
            // Walls a fence row with a gate.
            int fz = z1 + 3;
            if (fz < Max - 1)
            {
                Fill(w, x0, 64, fz, x1, 64, fz, Fence);
                w.Set(R(rng, x0, x1), 64, fz, Gate, 0);
            }
        }
        // A ladder up to a hatch.
        Fill(w, 15, 64, 15, 15, 70, 15, Stone);
        for (int y = 64; y <= 68; y++) w.Set(14, y, 15, Ladder, 5);
        Fill(w, 12, 69, 13, 16, 69, 17, Planks);
        w.Set(14, 69, 15, Trapdoor, 2);
    }

    /// <summary>The highest foot cell of a column: one above its highest block with two clear blocks over it, or null.</summary>
    public static BlockPos? Surface(WorldState w, int x, int z)
    {
        for (int y = w.WorldHeight - 3; y > 0; y--)
            if (w.GetBlock(x, y - 1, z) != 0 && w.GetBlock(x, y, z) == 0 && w.GetBlock(x, y + 1, z) == 0) return new BlockPos(x, y, z);
        return null;
    }

    /// <summary>Foot cells of the world: every column's foot cells (a block below, two clear above), top first.</summary>
    public static List<BlockPos> FootCells(WorldState w)
    {
        var r = new List<BlockPos>();
        for (int x = Min; x <= Max; x++)
        for (int z = Min; z <= Max; z++)
        for (int y = w.WorldHeight - 3; y > 0; y--)
        {
            int below = w.GetBlock(x, y - 1, z);
            if (below != 0 && !BlockShapes.IsLiquid(below) && w.GetBlock(x, y, z) is 0 or Snow or Torch && w.GetBlock(x, y + 1, z) == 0)
                r.Add(new BlockPos(x, y, z));
        }
        return r;
    }
}
