namespace MineClaw.World;

/// <summary>
/// b1.8 dropped-item (EntityItem) rules from mc-dev. Items never merge in b1.8 (that came in 1.3), so every drop
/// stays its own entity until picked up or despawned.
/// </summary>
public static class ItemEntityRules
{
    /// <summary>Block drops (Block.java:330) and mob/entity drops (Entity.java:853).</summary>
    public const int BlockDropPickupDelay = 10;
    /// <summary>Items a player throws (EntityHuman.java:368): a hand-off can't be picked up for 2 s.</summary>
    public const int PlayerDropPickupDelay = 40;
    /// <summary>EntityItem.java:75: age ≥ 6000 ticks (5 min) → removed.</summary>
    public const int DespawnTicks = 6000;
    /// <summary>EntityItem size 0.25 × 0.25, box centered on the reported position (height = width / 2 = 0.125).</summary>
    public const double HalfSize = 0.125;

    /// <summary>
    /// Whether a player standing with feet at <paramref name="feet"/> touches the item: EntityHuman.java:308 collects
    /// entities intersecting the player box grown by (1, 0, 1), i.e. |dx|, |dz| &lt; 0.3 + 1 + 0.125 and the item box
    /// overlapping [feet, feet + 1.8] (an item lying one block below the feet is NOT in range).
    /// </summary>
    public static bool InPickupRange(Vec3d feet, Vec3d item)
    {
        const double reach = 0.3 + 1.0 + HalfSize;
        return Math.Abs(item.X - feet.X) < reach && Math.Abs(item.Z - feet.Z) < reach
               && item.Y + HalfSize > feet.Y && item.Y - HalfSize < feet.Y + PlayerPhysics.Height;
    }

    /// <summary>The foot cell level a drop at <paramref name="item"/> is looked up from (its cell; an item resting on a full block
    /// reports a y a hair over the block top).</summary>
    public static BlockPos PickupCell(Vec3d item) => new((int)Math.Floor(item.X), (int)Math.Floor(item.Y + 0.1), (int)Math.Floor(item.Z));

    /// <summary>
    /// Where in foot cell (x,y,z) a body standing there touches the drop at <paramref name="item"/>: the cell centre first, else the
    /// point of the cell nearest the item (the body box stays inside the cell); null = not from this cell (nowhere to stand by
    /// <see cref="Safety.StandFeet"/>, the head under water, or out of reach). The client's pickup spots and the colony's "somewhere to
    /// stand by it" both use it. Wading is fine, the head under the gen's falling water is not (it pushes the body about).
    /// </summary>
    public static Vec3d? PickupPoint(IBlockAccess w, int x, int y, int z, Vec3d item)
    {
        if (Safety.StandFeet(w, x, y, z) is not { } feet) return null;
        if (BlockShapes.IsWater(w.GetBlock(x, (int)Math.Floor(feet + PlayerPhysics.EyeHeight), z))) return null;
        var centre = new Vec3d(x + 0.5, feet, z + 0.5);
        if (InPickupRange(centre, item)) return centre;
        const double Inset = PlayerPhysics.Width / 2 + 0.02;
        var edge = new Vec3d(Math.Clamp(item.X, x + Inset, x + 1 - Inset), feet, Math.Clamp(item.Z, z + Inset, z + 1 - Inset));
        return InPickupRange(edge, item) ? edge : null;
    }

    /// <summary>Some foot cell (2 around the drop's cell, its level ± 1: every cell whose pickup box can touch it) has a
    /// <see cref="PickupPoint"/>.</summary>
    public static bool HasPickupSpot(IBlockAccess w, Vec3d item)
    {
        var c = PickupCell(item);
        for (int y = c.Y - 1; y <= c.Y + 1; y++)
        for (int x = c.X - 2; x <= c.X + 2; x++)
        for (int z = c.Z - 2; z <= c.Z + 2; z++)
            if (PickupPoint(w, x, y, z, item) is not null) return true;
        return false;
    }

    /// <summary>Worst-case pickup delay still left for an item seen <paramref name="age"/> ago (unknown origin: assume a
    /// player drop).</summary>
    public static TimeSpan RemainingPickupDelay(TimeSpan age)
    {
        var full = TimeSpan.FromMilliseconds(PlayerDropPickupDelay * 50);
        return age >= full ? TimeSpan.Zero : full - age;
    }
}
