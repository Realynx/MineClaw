using System.Text.Json;
using MineClaw.World.Release1122;
using MineClaw.World.Versioning;

namespace MineClaw.World.Tests;

/// <summary>
/// The 1.12.2 collision shapes (multi-version P3): the generated table against minecraft-data's <c>blockCollisionShapes.json</c>
/// for every block state in an empty neighbourhood, and the neighbour-dependent shapes against the decompiled source's
/// AABB constants (<c>BlockFence</c>, <c>BlockWall</c>, <c>BlockPane</c>, <c>BlockStairs</c>, <c>BlockDoor</c>, <c>BlockChest</c>).
/// </summary>
public class Shapes1122Tests
{
    private const int Stone = 1, Glass = 20, Chest = 54, WoodDoor = 64, OakStairs = 53, Fence = 85, GlassPane = 102,
        FenceGate = 107, NetherFence = 113, Wall = 139, SpruceFence = 188, Leaves = 18;

    private static string DataDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "third_party", "minecraft-data", "pc", "1.12.2");
            if (Directory.Exists(p)) return p;
        }
        throw new DirectoryNotFoundException("third_party/minecraft-data/pc/1.12.2");
    }

    private static List<AABB> Boxes(IBlockAccess w, int x, int y, int z)
    {
        var l = new List<AABB>();
        BlockShapes.AddCollisionBoxes(w, x, y, z, l);
        return l.Select(b => new AABB(b.MinX - x, b.MinY - y, b.MinZ - z, b.MaxX - x, b.MaxY - y, b.MaxZ - z))
            .OrderBy(b => b.MinX).ThenBy(b => b.MinY).ThenBy(b => b.MinZ).ThenBy(b => b.MaxX).ThenBy(b => b.MaxY).ToList();
    }

    private static AABB B(double x0, double y0, double z0, double x1, double y1, double z1) => new(x0, y0, z0, x1, y1, z1);

    private static void Same(IEnumerable<AABB> expected, List<AABB> got)
    {
        var e = expected.OrderBy(b => b.MinX).ThenBy(b => b.MinY).ThenBy(b => b.MinZ).ThenBy(b => b.MaxX).ThenBy(b => b.MaxY).ToList();
        Assert.Equal(string.Join(" ", e), string.Join(" ", got));
    }

    [Fact]
    public void Every_State_Alone_Matches_MinecraftData()
    {
        var dir = DataDir();
        using var blocks = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "blocks.json")));
        using var shapes = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "blockCollisionShapes.json")));
        var ids = blocks.RootElement.EnumerateArray().ToDictionary(b => b.GetProperty("name").GetString()!, b => b.GetProperty("id").GetInt32());
        var shapeTable = shapes.RootElement.GetProperty("shapes");
        int checkedStates = 0;
        var w = Physics1122Tests.World();
        foreach (var block in shapes.RootElement.GetProperty("blocks").EnumerateObject())
        {
            int id = ids[block.Name];
            for (int meta = 0; meta < 16; meta++)
            {
                // minecraft-data computes door upper halves as if they were lower halves (meta 8..15 repeat 0..7); the source
                // gives a lone upper half the default state (north, closed): checked separately below.
                if (Shapes1122.IsDoor(id) && meta >= 8) continue;
                int shape = block.Value.ValueKind == JsonValueKind.Array
                    ? block.Value[meta < block.Value.GetArrayLength() ? meta : 0].GetInt32()
                    : block.Value.GetInt32();
                var expected = shapeTable.GetProperty(shape.ToString()).EnumerateArray()
                    .Select(a => B(a[0].GetDouble(), a[1].GetDouble(), a[2].GetDouble(), a[3].GetDouble(), a[4].GetDouble(), a[5].GetDouble()));
                w.Set(0, 64, 0, id, meta);
                Same(expected, Boxes(w, 0, 64, 0));
                w.Set(0, 64, 0, 0);
                checkedStates++;
            }
        }
        Assert.True(checkedStates > 3900);
    }

    [Fact]
    public void Lone_Door_Upper_Half_Takes_The_Default_State()
    {
        var w = Physics1122Tests.World();
        w.Set(0, 65, 0, WoodDoor, 8);
        Same([B(0, 0, 0.8125, 1, 1, 1)], Boxes(w, 0, 65, 0)); // NORTH_AABB
    }

    [Fact]
    public void Door_Halves_Share_State()
    {
        var w = Physics1122Tests.World();
        // Lower: facing east (meta & 3 = 0), open (4); upper: right hinge (bit 1).
        w.Set(0, 64, 0, WoodDoor, 4);
        w.Set(0, 65, 0, WoodDoor, 8 | 1);
        var open = B(0, 0, 0.8125, 1, 1, 1);                    // east + open + right hinge → NORTH_AABB
        Same([open], Boxes(w, 0, 64, 0));
        Same([open], Boxes(w, 0, 65, 0));
        w.Set(0, 65, 0, WoodDoor, 8);                          // left hinge → SOUTH_AABB
        Same([B(0, 0, 0, 1, 1, 0.1875)], Boxes(w, 0, 64, 0));
        w.Set(0, 64, 0, WoodDoor, 0);                          // closed east → EAST_AABB on both halves
        Same([B(0, 0, 0, 0.1875, 1, 1)], Boxes(w, 0, 65, 0));
    }

    [Fact]
    public void Fences_Connect_To_Solid_Faces_Fences_Of_Their_Material_And_Gates()
    {
        var w = Physics1122Tests.World();
        w.Set(0, 64, 0, Fence);
        w.Set(0, 64, -1, Stone);       // north: solid
        w.Set(1, 64, 0, SpruceFence);  // east: wood fence
        w.Set(0, 64, 1, NetherFence);  // south: nether brick (rock) — no
        w.Set(-1, 64, 0, Glass);       // west: glass is an attach exception — no
        Same([B(0.375, 0, 0.375, 0.625, 1.5, 0.625), B(0.375, 0, 0, 0.625, 1.5, 0.375), B(0.625, 0, 0.375, 1, 1.5, 0.625)], Boxes(w, 0, 64, 0));

        w.Set(-1, 64, 0, FenceGate, 0); // gate facing south spans X: its east face is a pole → connects
        Assert.Contains(B(0, 0, 0.375, 0.375, 1.5, 0.625), Boxes(w, 0, 64, 0));
        w.Set(-1, 64, 0, FenceGate, 1); // facing west spans Z: no
        Assert.DoesNotContain(B(0, 0, 0.375, 0.375, 1.5, 0.625), Boxes(w, 0, 64, 0));
        w.Set(-1, 64, 0, Leaves);
        Assert.DoesNotContain(B(0, 0, 0.375, 0.375, 1.5, 0.625), Boxes(w, 0, 64, 0));
    }

    [Fact]
    public void Wall_Straight_Run_Is_Thin_And_Corners_Are_Posts()
    {
        var w = Physics1122Tests.World();
        w.Set(0, 64, -1, Wall);
        w.Set(0, 64, 0, Wall);
        w.Set(0, 64, 1, Wall);
        Same([B(0.3125, 0, 0, 0.6875, 1.5, 1)], Boxes(w, 0, 64, 0));       // N + S → index 5
        w.Set(1, 64, 0, Stone);
        Same([B(0.25, 0, 0, 1, 1.5, 1)], Boxes(w, 0, 64, 0));              // N + S + E → index 13
        Same([B(0.25, 0, 0.25, 0.75, 1.5, 1)], Boxes(w, 0, 64, -1));       // S only → index 1
    }

    [Fact]
    public void Panes_Connect_To_Panes_And_Glass()
    {
        var w = Physics1122Tests.World();
        w.Set(0, 64, 0, GlassPane);
        w.Set(1, 64, 0, Glass);
        w.Set(0, 64, 1, GlassPane);
        Same([B(0.4375, 0, 0.4375, 0.5625, 1, 0.5625), B(0.4375, 0, 0.4375, 1, 1, 0.5625), B(0.4375, 0, 0.4375, 0.5625, 1, 1)], Boxes(w, 0, 64, 0));
    }

    [Fact]
    public void Stair_Corners()
    {
        var w = Physics1122Tests.World();
        // A stair facing north (meta 3) with a stair facing west (meta 1) in front (north of it): OUTER_LEFT (front faces CCW)
        w.Set(0, 64, 0, OakStairs, 3);
        w.Set(0, 64, -1, OakStairs, 1);
        Same([B(0, 0, 0, 1, 0.5, 1), B(0, 0.5, 0, 0.5, 1, 0.5)], Boxes(w, 0, 64, 0));   // slab + the NW eighth
        Assert.Equal(Shapes1122.StairsShape.OuterLeft, Shapes1122.StairShape(w, 3, 0, 64, 0));
        // Behind it (south) a stair facing east (meta 0): INNER_RIGHT (back faces CW) — slab + north quarter + the SE eighth.
        w.Set(0, 64, -1, 0);
        w.Set(0, 64, 1, OakStairs, 0);
        Assert.Equal(Shapes1122.StairsShape.InnerRight, Shapes1122.StairShape(w, 3, 0, 64, 0));
        Same([B(0, 0, 0, 1, 0.5, 1), B(0, 0.5, 0, 1, 1, 0.5), B(0.5, 0.5, 0.5, 1, 1, 1)], Boxes(w, 0, 64, 0));
        // Upside-down: the quarter is the lower one (the source's swapped AABB names).
        w.Set(0, 64, 1, 0);
        w.Set(0, 64, 0, OakStairs, 3 | 4);
        Same([B(0, 0, 0, 1, 0.5, 0.5), B(0, 0.5, 0, 1, 1, 1)], Boxes(w, 0, 64, 0));
    }

    [Fact]
    public void Double_Chest_Reaches_Its_Neighbour()
    {
        var w = Physics1122Tests.World();
        w.Set(0, 64, 0, Chest);
        w.Set(1, 64, 0, Chest);
        Same([B(0.0625, 0, 0.0625, 1, 0.875, 0.9375)], Boxes(w, 0, 64, 0));
        Same([B(0, 0, 0.0625, 0.9375, 0.875, 0.9375)], Boxes(w, 1, 64, 0));
    }

    [Fact]
    public void Beta_World_Keeps_Beta_Shapes()
    {
        // b1.8.1: a fence is a full 1.5-high block and a gate is solid; the 1.12.2 table never applies there.
        var beta = TestWorld.Create();
        beta.Set(0, 64, 0, Fence);
        Same([B(0, 0, 0, 1, 1.5, 1)], Boxes(beta, 0, 64, 0));
        Assert.Same(Beta181PhysicsRules.Instance, ((IBlockAccess)beta).Physics);
        var modern = Physics1122Tests.World();
        modern.Set(0, 64, 0, Fence);
        Same([B(0.375, 0, 0.375, 0.625, 1.5, 0.625)], Boxes(modern, 0, 64, 0));
        Assert.Equal(1.5, BlockShapes.CollisionTop(modern, 0, 64, 0), 9);
        Assert.False(BlockShapes.IsFullCube(modern, 0, 64, 0));
    }
}
