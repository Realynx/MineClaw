using MineClaw.World.Release1122;

namespace MineClaw.World.Versioning;

/// <summary>
/// Movement and collision rules of one game version (docs/design/multi-version.md, "Seam: physics"). b1.8.1 =
/// <see cref="PlayerPhysics"/> + <see cref="BlockShapes"/> as they are; 1.12.2 = <see cref="Physics1122"/> + <c>Shapes1122</c>.
/// <para>Consumers reach it two ways: the client tick through the profile, and everything that holds a world (the pathfinder,
/// <c>SwimSim</c>, parkour, <c>EdgeGuard</c>, safety checks) through <see cref="IBlockAccess.Physics"/>: the static
/// <see cref="BlockShapes"/> and <see cref="PlayerPhysics.Tick(IBlockAccess, PhysicsState, MoveInput)"/> entry points dispatch on
/// it (multi-version P3). Hot loops capture the instance once per search.</para>
/// </summary>
public interface IPhysicsRules
{
    double Width { get; }
    double Height { get; }
    double EyeHeight { get; }
    /// <summary>Hitbox height while sneaking (b1.8.1 1.8: the box never shrinks; 1.12.2 1.65 when it fits).</summary>
    double SneakHeight { get; }
    /// <summary>Highest ledge walked onto without a jump (b1.8.1 0.5; 1.12.2 0.6).</summary>
    double StepHeight { get; }
    /// <summary>Blocks per column (b1.8.1 128; 1.12.2 256).</summary>
    int WorldHeight { get; }
    /// <summary>Jump take-off speed (0.42F both).</summary>
    double JumpVelocity { get; }
    /// <summary>How far the sneak edge clamp probes below the box (b1.8.1 1.0; 1.12.2 the step height, 0.6): a sneaking player
    /// walks off an edge whose drop is deeper than this.</summary>
    double SneakEdgeProbe { get; }

    /// <summary>One 50 ms client tick of the local player.</summary>
    void Tick(IBlockAccess world, PhysicsState state, MoveInput input);

    /// <summary>The collision boxes of the block at x,y,z (world coordinates).</summary>
    void AddCollisionBoxes(IBlockAccess world, int x, int y, int z, List<AABB> output);

    /// <summary>The collision boxes of a given block state at x,y,z (what-if checks; neighbours come from the world).</summary>
    void AddCollisionBoxes(IBlockAccess world, int id, int meta, int x, int y, int z, List<AABB> output);

    /// <summary>All collision boxes intersecting <paramref name="area"/> (<c>World.getCollisionBoxes</c>); clears the output first.</summary>
    List<AABB> GetCollidingBoxes(IBlockAccess world, AABB area, List<AABB> output);

    /// <summary>Whether the cell holds something the player climbs (b1.8.1: ladders; 1.12.2: ladders, vines, an open trapdoor over
    /// a ladder of the same facing).</summary>
    bool IsClimbable(IBlockAccess world, int x, int y, int z);

    /// <summary>Ground slipperiness of a block id (b1.8.1: ice 0.98, else 0.6; 1.12.2: ice/packed/frosted 0.98, slime 0.8).</summary>
    double Slipperiness(int blockId);

    /// <summary>Damage (half-hearts) of landing on <paramref name="landingBlockId"/> after falling <paramref name="fallDistance"/>
    /// blocks: ceil(distance − 3) in both, 1.12.2's hay bale ×0.2, bed ×0.5 distance and slime none unless sneaking.</summary>
    int FallDamage(double fallDistance, int landingBlockId, bool sneaking);
}

/// <summary>b1.8.1: wraps the existing static physics, unchanged.</summary>
public sealed class Beta181PhysicsRules : IPhysicsRules
{
    public static readonly Beta181PhysicsRules Instance = new();

    public double Width => PlayerPhysics.Width;
    public double Height => PlayerPhysics.Height;
    public double EyeHeight => PlayerPhysics.EyeHeight;
    public double SneakHeight => PlayerPhysics.Height;
    public double StepHeight => PlayerPhysics.StepHeight;
    public int WorldHeight => ChunkColumn.SizeY;
    public double JumpVelocity => PlayerPhysics.JumpVelocity;
    public double SneakEdgeProbe => 1.0;

    public void Tick(IBlockAccess world, PhysicsState state, MoveInput input) => PlayerPhysics.TickBeta(world, state, input, PlayerPhysics.StepHeight);

    public void AddCollisionBoxes(IBlockAccess world, int x, int y, int z, List<AABB> output)
    {
        int id = world.GetBlock(x, y, z);
        if (id == 0) return;
        BlockShapes.AddCollisionBoxesBeta(world, id, world.GetMeta(x, y, z), x, y, z, output);
    }

    public void AddCollisionBoxes(IBlockAccess world, int id, int meta, int x, int y, int z, List<AABB> output) =>
        BlockShapes.AddCollisionBoxesBeta(world, id, meta, x, y, z, output);

    public List<AABB> GetCollidingBoxes(IBlockAccess world, AABB area, List<AABB> output) => BlockShapes.GetCollidingBoxesBeta(world, area, output);

    public bool IsClimbable(IBlockAccess world, int x, int y, int z) => world.GetBlock(x, y, z) == BlockShapes.Ladder;

    public double Slipperiness(int blockId) => BlockShapes.Slipperiness(blockId);

    public int FallDamage(double fallDistance, int landingBlockId, bool sneaking) => Math.Max(0, (int)Math.Ceiling(fallDistance - 3));
}

/// <summary>
/// 1.12.2 movement (multi-version P3): <see cref="Physics1122"/> (the <c>EntityPlayerSP</c> / <c>EntityLivingBase</c> /
/// <c>Entity.move</c> port: step 0.6F without ySize smoothing, the 1.65F sneaking box, the sneak clamp probing one step down,
/// the sprint attribute and its one-tick lag, jump ticks, ladders/vines/trapdoors, swimming, soul sand, slime, ice, web, fall
/// damage) and <c>Shapes1122</c> (the minecraft-data 1.12.2 collision shapes per state + the neighbour-connected fences, walls,
/// panes, stairs, doors, chests and chorus plants), in a 256-high world.
/// Sprint and sneak reach the server as Entity Action, translated by the 1.12.2 connection.
/// </summary>
public sealed class Release1122PhysicsRules : IPhysicsRules
{
    public static readonly Release1122PhysicsRules Instance = new();

    public const double Step = Physics1122.StepHeight;

    public double Width => Physics1122.Width;
    public double Height => Physics1122.Height;
    public double EyeHeight => Physics1122.EyeHeight;
    public double SneakHeight => Physics1122.SneakHeight;
    public double StepHeight => Step;
    public int WorldHeight => ChunkColumn.MaxHeight;
    public double JumpVelocity => Physics1122.JumpVelocity;
    public double SneakEdgeProbe => Step;

    public void Tick(IBlockAccess world, PhysicsState state, MoveInput input) => Physics1122.Tick(world, state, input);

    public void AddCollisionBoxes(IBlockAccess world, int x, int y, int z, List<AABB> output)
    {
        int id = world.GetBlock(x, y, z);
        if (id == 0) return;
        Shapes1122.AddCollisionBoxes(world, id, world.GetMeta(x, y, z), x, y, z, output);
    }

    public void AddCollisionBoxes(IBlockAccess world, int id, int meta, int x, int y, int z, List<AABB> output) =>
        Shapes1122.AddCollisionBoxes(world, id, meta, x, y, z, output);

    public List<AABB> GetCollidingBoxes(IBlockAccess world, AABB area, List<AABB> output) => Shapes1122.GetCollidingBoxes(world, area, output);

    public bool IsClimbable(IBlockAccess world, int x, int y, int z) => Shapes1122.IsClimbable(world, x, y, z);

    public double Slipperiness(int blockId) => Shapes1122.Slipperiness(blockId);

    public int FallDamage(double fallDistance, int landingBlockId, bool sneaking) => Physics1122.FallDamage((float)fallDistance, landingBlockId, sneaking);
}
