using MineClaw.World;

namespace MineClaw.Bot.Blueprints;

public static partial class DesignChecks
{
    /// <summary>The build's inside: its covered walkable cells, and which of them a walk from outside (a 2-block ring) reaches.</summary>
    private sealed class InteriorScan(VoxelPlan plan, IBlockAccess w)
    {
        private readonly BlockPos min = plan.Min, max = plan.Max;
        private static readonly (int Dx, int Dz)[] Sides = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        private static readonly int[] Rises = [0, 1, -1];

        private int Final(BlockPos p) => plan.Final(w, p);

        private bool Standable(BlockPos p) => Passable(Final(p)) && Passable(Final(p.Up())) && BuildBlocks.Supports(Final(p.Down()))
                                              && Final(p.Down()) is not (Data.Blocks.Fence or Data.Blocks.FenceGate) && w.IsLoaded(p.X, p.Z);

        private bool InFoot(BlockPos p) => p.X >= min.X && p.X <= max.X && p.Z >= min.Z && p.Z <= max.Z;

        /// <summary>Inside the footprint grown by the 2-block ring, from a layer under the build to its top.</summary>
        private bool InRing(BlockPos n) => n.X >= min.X - 2 && n.X <= max.X + 2 && n.Z >= min.Z - 2 && n.Z <= max.Z + 2 && n.Y >= min.Y - 1 && n.Y <= max.Y;

        /// <summary>A planned block that holds something up somewhere above the cell (a roof, a floor).</summary>
        private bool Covered(BlockPos p)
        {
            for (int y = p.Y + 1; y <= max.Y; y++)
                if (plan.Blocks.TryGetValue(new BlockPos(p.X, y, p.Z), out var b) && BuildBlocks.Supports(b.Id) && b.Kind != BuildKind.Door) return true;
            return false;
        }

        /// <summary>Covered walkable cells: with 2 blocks of headroom (interior), or 1 (low).</summary>
        public (List<BlockPos> Interior, List<BlockPos> Low) Cells()
        {
            var interior = new List<BlockPos>();
            var low = new List<BlockPos>();
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    for (int x = min.X; x <= max.X; x++)
                    {
                        var p = new BlockPos(x, y, z);
                        if (!CoveredFloor(p)) continue;
                        if (Passable(Final(p.Up()))) interior.Add(p);
                        else if (LowCeiling(p)) low.Add(p);
                    }
            return (interior, low);
        }

        /// <summary>A free cell (not a door's) on a supporting block, under the build's cover.</summary>
        private bool CoveredFloor(BlockPos p)
        {
            if (plan.Blocks.TryGetValue(p, out var pb) && pb.Kind == BuildKind.Door) return false;
            return Passable(Final(p)) && BuildBlocks.Supports(Final(p.Down())) && Covered(p);
        }

        /// <summary>A stair overhead is the underside of a flight (live #8: 1,0,4 under the step at 1,1,4): walkers climb that
        /// step, nobody stands beneath it, so it isn't a headroom problem.</summary>
        private bool LowCeiling(BlockPos p) => plan.Blocks.TryGetValue(p.Up(), out var ceiling) && ceiling.Kind != BuildKind.Stairs;

        /// <summary>Reachability: walk from standable cells outside the footprint (2-block ring) into the build.</summary>
        public HashSet<BlockPos> Reachable()
        {
            var reached = new HashSet<BlockPos>();
            var q = new Queue<BlockPos>();
            for (int y = min.Y - 1; y <= max.Y; y++)
                for (int z = min.Z - 2; z <= max.Z + 2; z++)
                    for (int x = min.X - 2; x <= max.X + 2; x++)
                    {
                        var p = new BlockPos(x, y, z);
                        if (!InFoot(p) && Standable(p) && reached.Add(p)) q.Enqueue(p);
                    }
            while (q.Count > 0) Step(q.Dequeue(), reached, q);
            return reached;
        }

        private void Step(BlockPos p, HashSet<BlockPos> reached, Queue<BlockPos> q)
        {
            foreach (var (dx, dz) in Sides)
                foreach (int dy in Rises)
                {
                    var n = new BlockPos(p.X + dx, p.Y + dy, p.Z + dz);
                    if (CanStep(p, n, dy) && reached.Add(n)) q.Enqueue(n);
                }
        }

        private bool CanStep(BlockPos p, BlockPos n, int dy)
        {
            if (!InRing(n)) return false;
            if (dy == 1 && !Passable(Final(p.Up(2)))) return false; // jumping needs head room
            if (dy == -1 && !Passable(Final(n.Up(2)))) return false;
            return Standable(n);
        }
    }
}
