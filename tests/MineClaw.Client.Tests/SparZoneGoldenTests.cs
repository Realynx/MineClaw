using System.Text;
using MineClaw.Client.Combat;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client.Tests;

/// <summary>
/// Equivalence golden for <see cref="SparZone.Find"/> (WP05 refactor): 60 seeded random islands (stepped floors, holes, lava, cactus,
/// low ceilings) searched from several spots with a few option sets; every result (reason, centre, size, centre edge and the full cell
/// list) is dumped and hashed. The digest was taken from the single-method implementation before it was split up.
/// </summary>
public class SparZoneGoldenTests
{
    private static WorldState Island(int seed)
    {
        var rng = new Random(seed);
        var w = new WorldState();
        w.ApplyChunkRegion(-32, 0, -32, 64, 128, 64, new byte[64 * 128 * 64], null, null, null);
        int half = 3 + rng.Next(9);
        for (int x = -half; x <= half; x++)
        for (int z = -half; z <= half; z++)
        {
            if (rng.Next(14) == 0) continue; // a hole to the void
            int top = 63 + (rng.Next(6) == 0 ? rng.Next(-2, 3) : 0);
            for (int y = 60; y <= top; y++) w.SetBlock(x, y, z, Blocks.Stone, 0);
            int roll = rng.Next(60);
            if (roll == 0) w.SetBlock(x, top, z, Blocks.Lava, 0);
            else if (roll == 1) w.SetBlock(x, top + 1, z, Blocks.Cactus, 0);
            else if (roll == 2) w.SetBlock(x, top + 3, z, Blocks.Stone, 0); // a low ceiling over a step
        }
        return w;
    }

    [Fact]
    public void Find_matches_the_golden() => SimRulesGoldenTests.Check("spar-zone", "B1592E47823FD22E146168D6C6A6877D0191C305907DEA68CD02EEA8E9AB0018", sb =>
    {
        var opts = new[]
        {
            new CombatOptions { TelemetryDirectory = null },
            new CombatOptions { TelemetryDirectory = null, MinEdgeDistance = 2, ArenaMinCells = 4, ArenaMaxStep = 0 },
            new CombatOptions { TelemetryDirectory = null, MinEdgeDistance = 4, ArenaHazardRadius = 0, ArenaSearchRadius = 6 },
        };
        for (int seed = 0; seed < 60; seed++)
        {
            var w = Island(seed);
            foreach (var (nx, nz) in new[] { (0.5, 0.5), (3.5, -2.5), (-6.5, 4.5) })
                for (int k = 0; k < opts.Length; k++)
                {
                    var r = SparZone.Find(w, new Vec3d(nx, 64, nz), opts[k], seed % 2 == 0 ? null : [new Vec3d(nx, 64, nz), new Vec3d(nx + 3, 64, nz - 1)]);
                    sb.Append(seed).Append(' ').Append(nx).Append(',').Append(nz).Append(' ').Append(k).Append(": ");
                    if (r.Zone is not { } zone) { sb.Append(r.Reason).Append('\n'); continue; }
                    sb.Append(zone.Center).Append(" n=").Append(zone.Count).Append(" e=").Append(zone.CenterEdge).Append(' ');
                    sb.AppendJoin(';', zone.Cells.OrderBy(c => c.X).ThenBy(c => c.Z).Select(c => $"{c.X},{c.Y},{c.Z}")).Append('\n');
                }
        }
    });
}
