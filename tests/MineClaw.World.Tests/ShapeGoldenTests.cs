using System.Text;
using MineClaw.World.Release1122;
using static MineClaw.World.Tests.Golden;

namespace MineClaw.World.Tests;

/// <summary>
/// Block shape and rule goldens (complexity refactor WP04), taken before the lookups were reshaped into tables: every id
/// predicate over every id (and meta), and every block state's collision boxes, selection boxes, tops, climbability, solid
/// sides, stair shapes, door states and liquid flow in seeded neighbourhoods (fences, panes, walls, stairs, chests and doors
/// connect to their neighbours), for both versions.
/// </summary>
public class ShapeGoldenTests
{
    [Fact]
    public void Id_predicates_match_the_golden() => Check("shape-ids", "EDE2EEE44546132871356DCE6504A42A40CCD101809C172EE5BAF58ABE0EA56B", sb =>
    {
        for (int id = -3; id < 4100; id++)
        {
            sb.Append(id).Append(':')
                .Append(Bit(BlockShapes.IsOpaqueCube(id))).Append(Bit(BlockShapes.MaterialBlocksMovement(id))).Append(Bit(BlockShapes.MaterialIsBuildable(id)))
                .Append(Bit(BlockShapes.IsStairs(id))).Append(Bit(BlockShapes.IsLiquid(id))).Append(R(BlockShapes.Slipperiness(id))).Append(' ')
                .Append(Bit(Physics1122.BlocksMovement(id))).Append(Bit(Shapes1122.IsStairs(id))).Append(Bit(Shapes1122.IsFence(id)))
                .Append(Bit(Shapes1122.IsWoodFence(id))).Append(Bit(Shapes1122.IsFenceGate(id))).Append(Bit(Shapes1122.IsPane(id)))
                .Append(Bit(Shapes1122.IsDoor(id))).Append(Bit(Shapes1122.IsChest(id))).Append(Bit(Shapes1122.IsTrapdoor(id)))
                .Append(R(Shapes1122.Slipperiness(id))).Append(' ').Append(Light1122.Opacity(id)).Append('/').Append(Light1122.Emission(id)).Append(' ');
            for (int meta = 0; meta < 16; meta++)
                sb.Append(Bit(Light1122.IsSpawnFloor(id, meta))).Append(Bit(Light1122.IsSpawnCellClear(id, meta)));
            sb.Append('\n');
        }
    });

    private static char Bit(bool b) => b ? '1' : '0';

    public static IEnumerable<object[]> Versions() => [[false], [true]];

    /// <summary>Neighbours that connect, cut, attach or don't: fences of both kinds, panes, bars, walls, gates, stairs, chests,
    /// doors, trapdoors, glass, leaves, pumpkins, slabs, full cubes, liquids, air.</summary>
    private static readonly int[] Palette =
        [0, 0, 1, 2, 4, 5, 8, 9, 10, 18, 20, 26, 29, 33, 43, 44, 53, 54, 64, 65, 67, 71, 78, 85, 86, 88, 89, 95, 96, 101, 102, 103, 106, 107,
         108, 113, 114, 118, 126, 138, 139, 146, 154, 160, 161, 165, 166, 167, 169, 183, 188, 193, 199, 200, 205, 212, 219, 234];

    [Theory]
    [MemberData(nameof(Versions))]
    public void State_shapes_match_the_golden(bool release1122) => Check($"shape-states-{(release1122 ? "1122" : "b181")}",
        release1122 ? "0ECDE4FE7EAA00806D156EF90209F0D77D66E942B19F9B7BD48682C4D6EB565F" : "AE51ED24BF899CFCE036448208719732CC4FDFB2D1BF34A354F8807D9A8B941F", sb =>
    {
        using var _ = GoldenWorlds.Data(release1122);
        var w = new Grid(release1122);
        var rng = new Random(release1122 ? 1122 : 181);
        var boxes = new List<AABB>();
        const int cx = 0, cy = 64, cz = 0;
        for (int id = 0; id < 256; id++)
        for (int meta = 0; meta < 16; meta++)
        for (int trial = 0; trial < 3; trial++)
        {
            // Neighbourhood: the 3x3x3 around the block, from the palette (trial 0: all air).
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dy == 0 && dz == 0) continue;
                int nid = trial == 0 ? 0 : rng.Next(4) == 0 ? rng.Next(256) : Palette[rng.Next(Palette.Length)];
                w.Set(cx + dx, cy + dy, cz + dz, nid, trial == 0 ? 0 : rng.Next(16));
            }
            w.Set(cx, cy, cz, id, meta);
            sb.Append(id).Append('/').Append(meta).Append('#').Append(trial).Append(" c=");
            boxes.Clear();
            BlockShapes.AddCollisionBoxes(w, cx, cy, cz, boxes);
            Boxes(sb, boxes);
            sb.Append(" s=");
            boxes.Clear();
            BlockShapes.AddSelectionBoxes(w, cx, cy, cz, boxes);
            Boxes(sb, boxes);
            sb.Append(" top=").Append(R(BlockShapes.CollisionTop(w, cx, cy, cz))).Append(Bit(BlockShapes.HasCollision(w, cx, cy, cz)))
                .Append(Bit(BlockShapes.IsFullCube(w, cx, cy, cz))).Append(Bit(BlockShapes.IsClimbableAt(w, cx, cy, cz)));
            if (release1122)
            {
                sb.Append(" side=");
                foreach (var f in new[] { Shapes1122.Face.North, Shapes1122.Face.South, Shapes1122.Face.West, Shapes1122.Face.East })
                    sb.Append(Bit(Shapes1122.SideSolid(w, id, cx, cy, cz, f)));
                if (Shapes1122.IsStairs(id)) sb.Append(" stair=").Append(Shapes1122.StairShape(w, meta, cx, cy, cz));
                if (Shapes1122.IsDoor(id)) sb.Append(" door=").Append(Shapes1122.DoorState(w, id, meta, cx, cy, cz));
                if (Shapes1122.IsLiquid(id)) sb.Append(" flow=").Append(R(Physics1122.Flow(w, cx, cy, cz)));
            }
            else if (BlockShapes.IsLiquid(id))
                sb.Append(" flow=").Append(R(PlayerPhysics.FlowVector(w, cx, cy, cz, BlockShapes.IsWater(id))));
            // The colliding boxes of a player-sized box overlapping the block and its neighbours.
            sb.Append(" area=");
            var area = new AABB(cx - 0.7 + trial * 0.3, cy - 0.5, cz - 0.4, cx + 0.9, cy + 1.3 + trial * 0.2, cz + 1.6);
            BlockShapes.GetCollidingBoxes(w, area, boxes);
            Boxes(sb, boxes);
            sb.Append('\n');
        }
    });

    /// <summary>A small loaded block grid (x,z −8..7, y 56..71; air elsewhere) with the version's rules and real block data: the
    /// shape lookups read only a block's neighbours, and a full world's light updates made this golden slow.</summary>
    internal sealed class Grid(bool release1122) : IBlockAccess
    {
        private readonly int[] _ids = new int[16 * 16 * 16];
        private readonly int[] _metas = new int[16 * 16 * 16];

        private static int Index(int x, int y, int z) =>
            x is >= -8 and < 8 && z is >= -8 and < 8 && y is >= 56 and < 72 ? (x + 8) << 8 | (y - 56) << 4 | (z + 8) : -1;

        public void Set(int x, int y, int z, int id, int meta)
        {
            int i = Index(x, y, z);
            _ids[i] = id;
            _metas[i] = meta;
        }

        public int GetBlock(int x, int y, int z) => Index(x, y, z) is var i and >= 0 ? _ids[i] : 0;
        public int GetMeta(int x, int y, int z) => Index(x, y, z) is var i and >= 0 ? _metas[i] : 0;
        public bool IsLoaded(int x, int z) => true;
        public MineClaw.Data.BlockInfo Info(int id) => MineClaw.Data.Blocks.Get(id);
        public int WorldHeight => release1122 ? 256 : 128;
        public Versioning.IPhysicsRules Physics => release1122 ? Versioning.Release1122PhysicsRules.Instance : Versioning.Beta181PhysicsRules.Instance;
    }

    internal static void Boxes(StringBuilder sb, List<AABB> boxes)
    {
        foreach (var b in boxes)
            sb.Append('[').Append(R(b.MinX)).Append(',').Append(R(b.MinY)).Append(',').Append(R(b.MinZ)).Append(',')
                .Append(R(b.MaxX)).Append(',').Append(R(b.MaxY)).Append(',').Append(R(b.MaxZ)).Append(']');
    }
}
