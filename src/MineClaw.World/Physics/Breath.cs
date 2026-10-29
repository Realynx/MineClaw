namespace MineClaw.World;

/// <summary>
/// Air and drowning, the same in both versions (1.12.2 <c>EntityLivingBase.onEntityUpdate</c> / <c>Entity.isInsideOfMaterial</c>;
/// mc-dev 218a2a71 <c>EntityLiving.y</c> / <c>Entity.a(Material)</c>): the air supply is 300 ticks; each tick the eye is inside
/// water it drops by one (Respiration may skip; Water Breathing stops it), at −20 it is set to 0 and the body takes 2 damage
/// (so 2 every second once it is out); any tick the eye is not in water it is back to 300 at once. The eye is in water when the
/// block at the eye holds water whose surface (<c>getLiquidHeightPercent(meta) − 1/9</c> below the block top) is above it.
/// Live 10-29 (1.12.2): main stood on the gravel bed of a ravine lake at 126,52,295, 2 damage a second, and drowned with nothing
/// tracking its air.
/// </summary>
public static class Breath
{
    /// <summary><c>Entity.getAir</c>'s full supply (ticks).</summary>
    public const int MaxAir = 300;
    /// <summary>The air value at which drowning damage is dealt (then air is 0 and it counts down again).</summary>
    public const int DrownAt = -20;
    /// <summary>Drowning damage per hit.</summary>
    public const int DrownDamage = 2;
    /// <summary>The player's eye height above the feet (standing).</summary>
    public const double EyeHeight = 1.62;

    /// <summary><c>BlockLiquid.getLiquidHeightPercent</c>: how much of the block a water level leaves EMPTY (falling water = full).</summary>
    public static float LiquidHeightPercent(int meta)
    {
        if (meta >= 8) meta = 0;
        return (meta + 1) / 9.0F;
    }

    /// <summary><c>Entity.isInsideOfMaterial(WATER)</c> at <paramref name="eye"/>.</summary>
    public static bool EyeInWater(IBlockAccess w, Vec3d eye)
    {
        int x = (int)Math.Floor(eye.X), y = (int)Math.Floor(eye.Y), z = (int)Math.Floor(eye.Z);
        if (y < 0 || y >= w.WorldHeight) return false;
        if (!BlockShapes.IsWater(w.GetBlock(x, y, z))) return false;
        float f = LiquidHeightPercent(w.GetMeta(x, y, z)) - 0.11111111F;
        float surface = y + 1 - f;
        return eye.Y < surface;
    }

    /// <summary>The head of a body standing (or floating) with its feet at <paramref name="feetY"/> in column (x, z) is under water.</summary>
    public static bool HeadUnderAt(IBlockAccess w, int x, double feetY, int z) => EyeInWater(w, new Vec3d(x + 0.5, feetY + EyeHeight, z + 0.5));

    /// <summary>One tick of the air supply (the server's rule, mirrored client-side); returns the new value and whether a drowning
    /// hit was dealt this tick.</summary>
    public static (int Air, bool Hit) Tick(int air, bool eyeInWater, bool waterBreathing = false)
    {
        if (!eyeInWater) return (MaxAir, false);
        if (waterBreathing) return (air, false);
        air--;
        if (air <= DrownAt) return (0, true);
        return (air, false);
    }

    /// <summary>The HUD's full bubbles (<c>GuiIngame.renderPlayerStats</c>: ceil((air − 2) · 10 / 300)), 0..10.</summary>
    public static int Bubbles(int air) => air <= 0 ? 0 : Math.Clamp((int)Math.Ceiling((air - 2) * 10.0 / MaxAir), 0, 10);

    /// <summary>"air 4/10 bubbles" (seconds of air left in brackets when it is short).</summary>
    public static string Text(int air) =>
        air >= MaxAir ? "air 10/10 bubbles" : $"air {Bubbles(air)}/10 bubbles ({Math.Max(0, air) / 20.0:0.#}s left)";

    /// <summary>Ticks a surfacing swim takes per cell of <see cref="NearestBreathable"/>'s path (swim speed about 0.1 block a
    /// tick, with the turns), and the margin kept on top.</summary>
    public const int TicksPerCell = 10, Margin = 40;

    /// <summary>
    /// The nearest place to breathe from the feet cell <paramref name="feet"/>, by a breadth-first search through water and air
    /// cells a body fits in (at most <paramref name="maxCells"/>, within <paramref name="maxRadius"/>, never more than one block
    /// below the start): a cell whose eye (feet + 1.62) is out of the water: the open surface above (water with air on top), or
    /// a 2-high air pocket under a ceiling. Returns the cell, the number of steps and the path (start excluded); null when none
    /// is reachable within the budget.
    /// </summary>
    public static (BlockPos Cell, int Steps, IReadOnlyList<BlockPos> Path)? NearestBreathable(IBlockAccess w, BlockPos feet, int maxCells = 6000, int maxRadius = 32)
    {
        var parent = new Dictionary<BlockPos, BlockPos> { [feet] = feet };
        var q = new Queue<(BlockPos P, int D)>();
        q.Enqueue((feet, 0));
        while (q.Count > 0 && parent.Count <= maxCells)
        {
            var (p, d) = q.Dequeue();
            if (Breathable(w, p))
            {
                var path = new List<BlockPos>();
                for (var c = p; c != feet; c = parent[c]) path.Add(c);
                path.Reverse();
                return (p, d, path);
            }
            // Up first (the surface is usually straight above), then the sides; down only one block (a cave mouth just below).
            foreach (var n in new[] { p.Up(), p.Offset(1, 0, 0), p.Offset(-1, 0, 0), p.Offset(0, 0, 1), p.Offset(0, 0, -1), p.Down() })
            {
                if (n.Y < feet.Y - 1 || Math.Abs(n.X - feet.X) > maxRadius || Math.Abs(n.Z - feet.Z) > maxRadius) continue;
                if (n.Y < 0 || n.Y + 1 >= w.WorldHeight || !w.IsLoaded(n.X, n.Z) || parent.ContainsKey(n)) continue;
                if (!Passable(w, n) || !Passable(w, n.Up())) continue;
                parent[n] = p;
                q.Enqueue((n, d + 1));
            }
        }
        return null;
    }

    /// <summary>The air needed to swim <paramref name="steps"/> cells to a breathable one, with the margin.</summary>
    public static int AirNeeded(int steps) => steps * TicksPerCell + Margin;

    /// <summary>A body with its feet in cell <paramref name="p"/> breathes there: the head cell is passable and the eye is not in water.</summary>
    public static bool Breathable(IBlockAccess w, BlockPos p) =>
        Passable(w, p) && Passable(w, p.Up()) && !Safety.IsHazard(w, w.GetBlock(p.X, p.Y, p.Z)) && !Safety.IsHazard(w, w.GetBlock(p.X, p.Y + 1, p.Z))
        && !EyeInWater(w, new Vec3d(p.X + 0.5, p.Y + EyeHeight, p.Z + 0.5));

    /// <summary>Water or a block without collision (air, plants), never lava.</summary>
    private static bool Passable(IBlockAccess w, BlockPos p)
    {
        int id = w.GetBlock(p.X, p.Y, p.Z);
        if (BlockShapes.IsLava(id)) return false;
        return BlockShapes.IsWater(id) || !BlockShapes.HasCollision(w, p.X, p.Y, p.Z);
    }
}
