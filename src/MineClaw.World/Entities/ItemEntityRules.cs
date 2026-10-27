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

    /// <summary>Worst-case pickup delay still left for an item seen <paramref name="age"/> ago (unknown origin: assume a
    /// player drop).</summary>
    public static TimeSpan RemainingPickupDelay(TimeSpan age)
    {
        var full = TimeSpan.FromMilliseconds(PlayerDropPickupDelay * 50);
        return age >= full ? TimeSpan.Zero : full - age;
    }
}
