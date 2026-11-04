namespace MineClaw.World;

/// <summary>
/// b1.8.1 doors, trapdoors and fence gates: what state a block is in, read from its metadata (never by bumping into it).
/// Verified against the b1.8.1 jar:
/// <list type="bullet">
/// <item>Door (64 wood, 71 iron): bits 0-1 facing, 0x4 open, 0x8 upper half. Beta keeps the open bit in BOTH halves
/// (BlockDoor.blockActivated sets lower = meta^4 and upper = (meta^4)|8); clicking the upper half toggles the lower.
/// Iron doors ignore right-clicks and only follow redstone (button, lever, pressure plate).</item>
/// <item>Trapdoor (96): bits 0-1 hinge wall, 0x4 open (no upper-half trapdoors before 1.9). Closed = 3/16 floor slab.</item>
/// <item>Fence gate (107): bits 0-1 facing, 0x4 open. Closed collides 1.5 high, open has no collision.</item>
/// <item>Lever (69) 0x8 = on; stone button (77) 0x8 = pressed (pops after 20 ticks); pressure plates (70/72) meta 1 = down.</item>
/// </list>
/// <para>1.12.2 (multi-version P3; the ids are absent from b1.8.1 worlds, so b1.8.1 is unchanged): the wood doors 193-197
/// (spruce, birch, jungle, acacia, dark oak), the fence gates 183-187 and the iron trapdoor 167 (redstone only). The lower door
/// half keeps facing/open (bits 0-1, 0x4); the upper keeps the hinge (0x1) and powered (0x2) instead of a copy of the open bit
/// (<c>BlockDoor.getStateFromMeta</c>), so the state is read from the lower half as here. Trapdoors add 0x8 = top half;
/// facing/open match b1.8.1's hinge layout box for box (<c>BlockTrapDoor.getFacing</c>).</para>
/// </summary>
public static class Openables
{
    public const int WoodDoor = BlockShapes.WoodDoor, IronDoor = BlockShapes.IronDoor, Trapdoor = BlockShapes.Trapdoor,
        FenceGate = BlockShapes.FenceGate, Lever = 69, StoneButton = 77, StonePlate = 70, WoodPlate = 72;

    /// <summary>1.12.2 actuators: the wooden button and the weighted (gold/iron) pressure plates (meta = power).</summary>
    public const int WoodButton = 143, LightWeightedPlate = 147, HeavyWeightedPlate = 148;

    public const int OpenBit = 0x4, UpperBit = 0x8;

    public const int IronTrapdoor = 167;

    public static bool IsDoor(int id) => id is WoodDoor or IronDoor or (>= 193 and <= 197);
    public static bool IsTrapdoor(int id) => id is Trapdoor or IronTrapdoor;
    public static bool IsFenceGate(int id) => id is FenceGate or (>= 183 and <= 187);

    /// <summary>Door, trapdoor or fence gate.</summary>
    public static bool IsOpenable(int id) => IsDoor(id) || IsTrapdoor(id) || IsFenceGate(id);

    /// <summary>Opens on a plain right-click (everything but the iron door and the iron trapdoor).</summary>
    public static bool OpensByHand(int id) => IsOpenable(id) && id is not (IronDoor or IronTrapdoor);

    public static bool IsActuator(int id) => id is Lever || IsButton(id) || IsPlate(id);
    public static bool IsPlate(int id) => id is StonePlate or WoodPlate or LightWeightedPlate or HeavyWeightedPlate;
    public static bool IsButton(int id) => id is StoneButton or WoodButton;

    public static bool IsOpen(int meta) => (meta & OpenBit) != 0;
    public static bool IsUpperHalf(int id, int meta) => IsDoor(id) && (meta & UpperBit) != 0;

    /// <summary>The block that carries the state: the lower half for doors, the block itself otherwise.</summary>
    public static BlockPos StateBlock(IBlockAccess w, BlockPos p)
    {
        int id = w.GetBlock(p.X, p.Y, p.Z);
        if (IsDoor(id) && (w.GetMeta(p.X, p.Y, p.Z) & UpperBit) != 0 && w.GetBlock(p.X, p.Y - 1, p.Z) == id)
            return new BlockPos(p.X, p.Y - 1, p.Z);
        return p;
    }

    /// <summary>Open state of the openable at <paramref name="p"/> (door: from its lower half), or null if it isn't one.</summary>
    public static bool? IsOpenAt(IBlockAccess w, BlockPos p)
    {
        var s = StateBlock(w, p);
        int id = w.GetBlock(s.X, s.Y, s.Z);
        return IsOpenable(id) ? IsOpen(w.GetMeta(s.X, s.Y, s.Z)) : null;
    }

    /// <summary>
    /// Whether the openable at <paramref name="p"/> lets a body through its doorway. Gates and trapdoors: the open bit.
    /// Doors: by geometry, because a b1.8.1 door pair stores its second door with the open bit SET while shut
    /// (ItemDoor: facing ((f−1)&amp;3)+4 puts the panel on the same plane as its partner's). The panel side (state 0 z−,
    /// 1 x+, 2 z+, 3 x−) is compared with the doorway's axis (walls/doors on two opposite sides); no clear axis → the bit.
    /// </summary>
    public static bool LooksOpen(IBlockAccess w, BlockPos p)
    {
        var s = StateBlock(w, p);
        int id = w.GetBlock(s.X, s.Y, s.Z), meta = w.GetMeta(s.X, s.Y, s.Z);
        if (!IsOpenable(id)) return false;
        if (!IsDoor(id)) return IsOpen(meta);
        // The panel's plane from its collision box (b1.8.1: state 0/2 = a z-face, mc-dev BlockDoor; 1.12.2: the actual state).
        var panel = new List<AABB>(2);
        BlockShapes.AddCollisionBoxes(w, id, meta, s.X, s.Y, s.Z, panel);
        bool zFace = panel.Count > 0 ? panel[0].MaxZ - panel[0].MinZ < 0.5 : ((meta & OpenBit) == 0 ? (meta - 1) & 3 : meta & 3) is 0 or 2;
        bool Side(int x, int z) => BlockShapes.HasCollision(w, x, s.Y, z) || IsDoor(w.GetBlock(x, s.Y, z));
        bool wallsX = Side(s.X - 1, s.Z) && Side(s.X + 1, s.Z), wallsZ = Side(s.X, s.Z - 1) && Side(s.X, s.Z + 1);
        if (wallsZ && !wallsX) return zFace;   // passage along x: x-face panels block it
        if (wallsX && !wallsZ) return !zFace;  // passage along z
        return IsOpen(meta);
    }

    /// <summary>Metadata after one right-click / redstone flip (door lower half or trapdoor/gate). Gates swing away from
    /// the player in vanilla, but the open bit is what passability depends on, so the planner just flips it.</summary>
    public static int Toggled(int meta) => meta ^ OpenBit;

    /// <summary>Lever/button/plate state (lever on, button pressed, plate down).</summary>
    public static bool IsActuatorOn(int id, int meta) => id switch
    {
        Lever or StoneButton or WoodButton => (meta & 0x8) != 0,
        StonePlate or WoodPlate or LightWeightedPlate or HeavyWeightedPlate => meta != 0,
        _ => false,
    };

    public static string Name(int id) => Names.TryGetValue(id, out var name) ? name : "block";

    private static readonly Dictionary<int, string> Names = BuildNames();

    private static Dictionary<int, string> BuildNames()
    {
        var n = new Dictionary<int, string>();
        foreach (int id in new[] { WoodDoor, 193, 194, 195, 196, 197 }) n[id] = "door";
        n[IronDoor] = "iron door";
        n[Trapdoor] = "trapdoor";
        n[IronTrapdoor] = "iron trapdoor";
        foreach (int id in new[] { FenceGate, 183, 184, 185, 186, 187 }) n[id] = "fence gate";
        n[Lever] = "lever";
        n[StoneButton] = n[WoodButton] = "button";
        foreach (int id in new[] { StonePlate, WoodPlate, LightWeightedPlate, HeavyWeightedPlate }) n[id] = "pressure plate";
        return n;
    }
}
