using MineClaw.Data;

namespace MineClaw.World;

/// <summary>Which half a slab or stair should take when placed (1.12.2 decides it from the face and cursor).</summary>
public enum PlaceHalf
{
    /// <summary>No preference: the face centre is clicked (a side face's centre gives the bottom half).</summary>
    Auto,
    /// <summary>A bottom slab / an upright stair.</summary>
    Bottom,
    /// <summary>A top slab / an upside-down stair.</summary>
    Top,
}

/// <summary>
/// The placement cursor (1.12.2 Player Block Placement's cursor x/y/z, each 0..1 from the block's corner) for a build intent.
/// Rules from the decompiled source:
/// <list type="bullet">
/// <item><c>BlockSlab.getStateForPlacement</c> and <c>BlockStairs.getStateForPlacement</c>: <c>facing != DOWN &amp;&amp; (facing
/// == UP || hitY &lt;= 0.5)</c> → bottom, else top. So a top face always gives a bottom half, the underside (a "Hidden"
/// underside click) always a top half, and a side face follows the cursor height.</item>
/// <item><c>ItemSlab.onItemUse</c>: clicking the top of a bottom slab (or the underside of a top slab) of the same type
/// makes a double slab in that cell instead.</item>
/// <item><c>BlockLog</c> takes its axis from the face alone; <c>BlockTorch</c> its facing from the face alone (the metas match
/// b1.8.1's 1..5); stairs face the player's horizontal facing (the yaw). None of these read the cursor.</item>
/// </list>
/// A vanilla client's cursor is the hit point: on the top face y = 1, on the underside y = 0, on a side face its own axis is
/// 0 or 1 and the other two are where the ray hit.
/// </summary>
public static class PlaceCursor
{
    /// <summary>1.12.2 single slabs: stone slab 44, wooden slab 126, red sandstone slab 182, purpur slab 205.</summary>
    public static bool IsSlab(int blockId) => blockId is Blocks.Slab or 126 or 182 or 205;

    /// <summary>Stair blocks (b1.8.1 53, 67, 108, 109, 114 and 1.12.2's 128, 134..136, 156, 163, 164, 180, 203).</summary>
    public static bool IsStairs(int blockId) => Release1122.Shapes1122.IsStairs(blockId);

    /// <summary>Slabs and stairs: their half follows the click on versions where <c>HalvesFollowCursor</c> holds.</summary>
    public static bool HasHalves(int blockId) => IsSlab(blockId) || IsStairs(blockId);

    /// <summary>The half a 1.12.2 block meta encodes: slab bit 8 (BlockSlab HALF top), stairs bit 4 (BlockStairs HALF top).</summary>
    public static PlaceHalf HalfOf(int blockId, int meta) =>
        IsSlab(blockId) ? ((meta & 8) != 0 ? PlaceHalf.Top : PlaceHalf.Bottom)
        : IsStairs(blockId) ? ((meta & 4) != 0 ? PlaceHalf.Top : PlaceHalf.Bottom)
        : PlaceHalf.Auto;

    /// <summary>Whether a click on <paramref name="face"/> can produce <paramref name="half"/> (the top face only a bottom
    /// half, the underside only a top half).</summary>
    public static bool FaceAllows(BlockFace face, PlaceHalf half) => half switch
    {
        PlaceHalf.Bottom => face != BlockFace.Bottom,
        PlaceHalf.Top => face != BlockFace.Top,
        _ => true,
    };

    /// <summary>
    /// The cursor for a click on <paramref name="face"/> wanting <paramref name="half"/>: the face centre, except on a side
    /// face the height 0.25 for a bottom half and 0.75 for a top one (clear of the 0.5 boundary). Top face y = 1, underside y = 0.
    /// </summary>
    public static (float X, float Y, float Z) For(BlockFace face, PlaceHalf half = PlaceHalf.Auto)
    {
        float y = half switch { PlaceHalf.Bottom => 0.25f, PlaceHalf.Top => 0.75f, _ => 0.5f };
        return face switch
        {
            BlockFace.Bottom => (0.5f, 0f, 0.5f),
            BlockFace.Top => (0.5f, 1f, 0.5f),
            BlockFace.North => (0.5f, y, 0f),
            BlockFace.South => (0.5f, y, 1f),
            BlockFace.West => (0f, y, 0.5f),
            BlockFace.East => (1f, y, 0.5f),
            _ => (0.5f, 0.5f, 0.5f),
        };
    }

    /// <summary>The half 1.12.2 gives a slab/stair for this click (<c>getStateForPlacement</c>).</summary>
    public static PlaceHalf Result(BlockFace face, float cursorY) =>
        face != BlockFace.Bottom && (face == BlockFace.Top || !(cursorY > 0.5f)) ? PlaceHalf.Bottom : PlaceHalf.Top;

    /// <summary>The 1.12.2 log meta for a placement: the item's wood type (bits 0-1) plus the axis of the clicked face (bits 2-3:
    /// 0 up/down, 4 east/west, 8 north/south; <c>BlockLog.getStateForPlacement</c> + <c>BlockOldLog.getMetaFromState</c>).</summary>
    public static int LogMeta(int woodType, BlockFace face) => (woodType & 3) | face switch
    {
        BlockFace.East or BlockFace.West => 4,
        BlockFace.North or BlockFace.South => 8,
        _ => 0,
    };
}
