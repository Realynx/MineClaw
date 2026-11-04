namespace MineClaw.Hive.Story;

/// <summary>
/// A quarry zone's stair (docs/web/story-mode.md "Quarries"): the zone's outer ring of cells, clockwise from its north-west-most
/// edge cell; layer k (y = top − k) leaves the ring's cell k mod n standing as a step, so the steps climb one block per cell around
/// the ring, a helix from the surface to the bottom. A 2×2 zone's ring is its 4 cells (a 1-wide spiral shaft); 3×3 and larger use
/// the edge ring. A step's column has the next step n ≥ 4 layers up, so the 2 cells over every step (and the one over that, for the
/// jump) stay open. A 1-wide zone has no ring (no quarry: a trench).
/// </summary>
public static class StoryQuarry
{
    private static readonly (int Dx, int Dz)[] Clockwise = [(1, 0), (0, 1), (-1, 0), (0, -1)]; // east, south, west, north

    /// <summary>The clockwise ring of edge cells (north-west-most first), or null when the zone has no closed ring of 4 or more.</summary>
    public static List<(int X, int Z)>? Ring(ZoneCells cells)
    {
        var edge = new HashSet<(int, int)>();
        foreach (var (x, z) in cells.Sorted())
            foreach (var (dx, dz) in Clockwise)
                if (!cells.Contains(x + dx, z + dz)) { edge.Add((x, z)); break; }
        if (edge.Count < 4) return null;
        var start = edge.OrderBy(c => c.Item2).ThenBy(c => c.Item1).First();
        var ring = new List<(int X, int Z)> { start };
        var seen = new HashSet<(int, int)> { start };
        int heading = 0;
        var cur = start;
        while (true)
        {
            bool moved = false;
            // Outside on the left: try left, straight, right, back.
            foreach (var turn in new[] { -1, 0, 1, 2 })
            {
                int d = (heading + turn + 4) % 4;
                var n = (cur.Item1 + Clockwise[d].Dx, cur.Item2 + Clockwise[d].Dz);
                if (!edge.Contains(n) || !seen.Add(n)) continue;
                ring.Add(n);
                cur = n;
                heading = d;
                moved = true;
                break;
            }
            if (!moved) break;
        }
        var last = ring[^1];
        bool closed = Math.Abs(last.X - start.Item1) + Math.Abs(last.Z - start.Item2) == 1;
        return ring.Count >= 4 && closed ? ring : null;
    }

    /// <summary>The step of layer <paramref name="k"/>: the ring cell k mod n at y = top − k.</summary>
    public static P3 Step(IReadOnlyList<(int X, int Z)> ring, int top, int k)
    {
        var (x, z) = ring[k % ring.Count];
        return new P3(x, top - k, z);
    }

    /// <summary>Blocks that fall when what is under them is dug (sand, gravel, 1.12.2 concrete powder): a step of them is replaced.</summary>
    public static bool Falls(int id) => id is 12 or 13 or 252;

    public static bool Lava(int id) => id is 10 or 11;
    public static bool Water(int id) => id is 8 or 9;
    public const int Bedrock = 7;
}
