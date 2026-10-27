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
/// </summary>
public static class Openables
{
    public const int WoodDoor = BlockShapes.WoodDoor, IronDoor = BlockShapes.IronDoor, Trapdoor = BlockShapes.Trapdoor,
        FenceGate = BlockShapes.FenceGate, Lever = 69, StoneButton = 77, StonePlate = 70, WoodPlate = 72;

    public const int OpenBit = 0x4, UpperBit = 0x8;

    public static bool IsDoor(int id) => id is WoodDoor or IronDoor;

    /// <summary>Door, trapdoor or fence gate.</summary>
    public static bool IsOpenable(int id) => id is WoodDoor or IronDoor or Trapdoor or FenceGate;

    /// <summary>Opens on a plain right-click (everything but the iron door).</summary>
    public static bool OpensByHand(int id) => id is WoodDoor or Trapdoor or FenceGate;

    public static bool IsActuator(int id) => id is Lever or StoneButton or StonePlate or WoodPlate;
    public static bool IsPlate(int id) => id is StonePlate or WoodPlate;

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
        int state = (meta & OpenBit) == 0 ? (meta - 1) & 3 : meta & 3;
        bool zFace = state is 0 or 2;
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
        Lever or StoneButton => (meta & 0x8) != 0,
        StonePlate or WoodPlate => meta != 0,
        _ => false,
    };

    public static string Name(int id) => id switch
    {
        WoodDoor => "door",
        IronDoor => "iron door",
        Trapdoor => "trapdoor",
        FenceGate => "fence gate",
        Lever => "lever",
        StoneButton => "button",
        StonePlate or WoodPlate => "pressure plate",
        _ => "block",
    };
}
