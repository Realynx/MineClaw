namespace MineClaw.World.Versioning;

/// <summary>
/// Movement and collision rules of one game version (docs/design/multi-version.md, "Seam: physics"). b1.8.1 =
/// <see cref="PlayerPhysics"/> + <see cref="BlockShapes"/> as they are; 1.12.2 differs in sneak/sprint/swim/ladder handling,
/// the 0.6 step height, new block shapes (fences, panes, walls connect; slabs/stairs by state) and the 256-high world.
/// <para>P0: defined and registered in the profile; the tick, simulators (<c>SwimSim</c>, parkour) and the pathfinder still
/// call the statics. P3 routes them through the profile (hot loops capture the instance once per search).</para>
/// </summary>
public interface IPhysicsRules
{
    double Width { get; }
    double Height { get; }
    double EyeHeight { get; }
    /// <summary>Highest ledge walked onto without a jump (b1.8.1 0.5; 1.12.2 0.6).</summary>
    double StepHeight { get; }
    /// <summary>Blocks per column (b1.8.1 128; 1.12.2 256).</summary>
    int WorldHeight { get; }

    /// <summary>One 50 ms client tick of the local player.</summary>
    void Tick(IBlockAccess world, PhysicsState state, MoveInput input);

    /// <summary>The collision boxes of the block at x,y,z (world coordinates).</summary>
    void AddCollisionBoxes(IBlockAccess world, int x, int y, int z, List<AABB> output);

    /// <summary>Ground slipperiness of a block id (b1.8.1: ice 0.98, else 0.6).</summary>
    double Slipperiness(int blockId);
}

/// <summary>b1.8.1: wraps the existing static physics, unchanged.</summary>
public sealed class Beta181PhysicsRules : IPhysicsRules
{
    public static readonly Beta181PhysicsRules Instance = new();

    public double Width => PlayerPhysics.Width;
    public double Height => PlayerPhysics.Height;
    public double EyeHeight => PlayerPhysics.EyeHeight;
    public double StepHeight => PlayerPhysics.StepHeight;
    public int WorldHeight => ChunkColumn.SizeY;

    public void Tick(IBlockAccess world, PhysicsState state, MoveInput input) => PlayerPhysics.Tick(world, state, input);

    public void AddCollisionBoxes(IBlockAccess world, int x, int y, int z, List<AABB> output) =>
        BlockShapes.AddCollisionBoxes(world, x, y, z, output);

    public double Slipperiness(int blockId) => BlockShapes.Slipperiness(blockId);
}

/// <summary>
/// 1.12.2 movement, P1 basics: b1.8.1's <see cref="PlayerPhysics"/> where the decompiled 1.12.2 source does the same thing
/// (gravity 0.08 and ×0.98 drag, ground slipperiness 0.6 × 0.91, jump 0.42, sprint ×1.3 and the 0.2 sprint-jump boost, water
/// 0.8 / lava 0.5 drag: <c>EntityLivingBase.travel</c>, <c>jump</c>), with the 1.12.2 step height of 0.6F (<c>EntityLivingBase</c>'s constructor sets <c>stepHeight = 0.6F</c>) and the 256-high world.
/// Sprint and sneak reach the server as Entity Action, translated by the 1.12.2 connection.
/// <para>Pending (multi-version P3, from <c>Entity.move</c>, <c>EntityLivingBase.travel</c>, <c>EntityPlayerSP.onLivingUpdate</c>):
/// 1.12.2's step without ySize smoothing, the 1.65-high sneaking box (<c>EntityPlayer.updateSize</c>, when it fits) and its
/// eye height 1.54 (<c>EntityPlayer.getEyeHeight</c>), the attribute-based
/// walk speed, ladder/vine rules, the new collision shapes (fences, panes, walls, stairs by state) and new blocks'
/// slipperiness (slime 0.8, packed ice 0.98).</para>
/// </summary>
public sealed class Release1122PhysicsRules : IPhysicsRules
{
    public static readonly Release1122PhysicsRules Instance = new();

    public const double Step = 0.6f;

    public double Width => PlayerPhysics.Width;
    public double Height => PlayerPhysics.Height;
    public double EyeHeight => PlayerPhysics.EyeHeight;
    public double StepHeight => Step;
    public int WorldHeight => ChunkColumn.MaxHeight;

    public void Tick(IBlockAccess world, PhysicsState state, MoveInput input) => PlayerPhysics.Tick(world, state, input, Step);

    public void AddCollisionBoxes(IBlockAccess world, int x, int y, int z, List<AABB> output) =>
        BlockShapes.AddCollisionBoxes(world, x, y, z, output);

    public double Slipperiness(int blockId) => BlockShapes.Slipperiness(blockId);
}
