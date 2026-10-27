using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Going through a nether portal the way the b1.8.1 server needs it (mc-dev <c>EntityPlayer</c> tick + <c>Entity.Y()</c>
/// "setInPortal", <c>BlockPortal.a(World,x,y,z,Entity)</c>):
/// <list type="bullet">
/// <item>every movement packet whose box overlaps a portal block marks the player "in portal"; each such tick adds
/// 0.0125 to the portal timer, so the switch (0x09 Respawn with the other dimension, then chunks + a 0x0D) comes after
/// <see cref="TravelTicks"/> ticks (~4 s) of standing in the sheet;</item>
/// <item>the switch sets a <see cref="CooldownTicks"/>-tick cooldown, and while it runs every tick spent in a portal
/// RESETS it to 10. The server drops the player INSIDE the exit portal, so standing (or shuffling) there never goes
/// back: step out for more than 10 ticks first, then step in and stand still.</item>
/// </list>
/// Portal blocks have no collision box: a portal cell with a solid block under it is an ordinary standable foot cell.
/// Delegates keep this usable from the client, the skill layer and the bot's tool clients alike.
/// </summary>
public static class PortalEntry
{
    public const int TravelTicks = 80, CooldownTicks = 10;

    /// <summary>Ticks waited outside the sheet before stepping back in (cooldown + margin for packet timing).</summary>
    public static int StepOutTicks { get; set; } = CooldownTicks + 10;

    /// <summary>The player box at <paramref name="feet"/> (shrunk by 0.001 like Entity.move's block-collision pass)
    /// overlaps a portal block.</summary>
    public static bool InPortal(IBlockAccess w, Vec3d feet)
    {
        double h = PlayerPhysics.Width / 2 - 0.001;
        int x0 = (int)Math.Floor(feet.X - h), x1 = (int)Math.Floor(feet.X + h);
        int y0 = (int)Math.Floor(feet.Y + 0.001), y1 = (int)Math.Floor(feet.Y + PlayerPhysics.Height - 0.001);
        int z0 = (int)Math.Floor(feet.Z - h), z1 = (int)Math.Floor(feet.Z + h);
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            if (w.GetBlock(x, y, z) == Blocks.Portal) return true;
        return false;
    }

    /// <summary>The portal blocks of one sheet connected to <paramref name="start"/> (bounded).</summary>
    public static HashSet<BlockPos> Sheet(IBlockAccess w, BlockPos start)
    {
        var set = new HashSet<BlockPos>();
        if (w.GetBlock(start.X, start.Y, start.Z) != Blocks.Portal) return set;
        var q = new Queue<BlockPos>();
        set.Add(start);
        q.Enqueue(start);
        while (q.Count > 0 && set.Count < 512)
        {
            var c = q.Dequeue();
            foreach (var n in new[] { c.Offset(1, 0, 0), c.Offset(-1, 0, 0), c.Offset(0, 1, 0), c.Offset(0, -1, 0), c.Offset(0, 0, 1), c.Offset(0, 0, -1) })
                if (w.GetBlock(n.X, n.Y, n.Z) == Blocks.Portal && set.Add(n)) q.Enqueue(n);
        }
        return set;
    }

    /// <summary>Bottom cells of the sheet with a collision box under them (where a player stands IN the portal), nearest
    /// to <paramref name="from"/> first.</summary>
    public static List<BlockPos> StandCells(IBlockAccess w, BlockPos anyCell, Vec3d from) =>
        Sheet(w, anyCell).Where(p => w.GetBlock(p.X, p.Y - 1, p.Z) != Blocks.Portal && BlockShapes.HasCollision(w, p.X, p.Y - 1, p.Z))
            .OrderBy(p => Sq(p, from)).ToList();

    /// <summary>Standable cells close by whose centred player box touches no portal block (where the cooldown runs out),
    /// nearest first: free feet+head, solid floor, no hazard, not over the void.</summary>
    public static List<BlockPos> StepOutCells(IBlockAccess w, Vec3d feet, int radius = 3)
    {
        var me = new BlockPos((int)Math.Floor(feet.X), (int)Math.Floor(feet.Y + 0.001), (int)Math.Floor(feet.Z));
        var list = new List<BlockPos>();
        for (int dx = -radius; dx <= radius; dx++)
        for (int dz = -radius; dz <= radius; dz++)
        for (int dy = -1; dy <= 1; dy++)
        {
            var p = me.Offset(dx, dy, dz);
            if (!Safety.IsStandable(w, p.X, p.Y, p.Z) || InPortal(w, new Vec3d(p.X + 0.5, p.Y, p.Z + 0.5))) continue;
            list.Add(p);
        }
        return list.OrderBy(p => Sq(p, feet)).ThenBy(p => Math.Abs(p.Y - me.Y)).ToList();
    }

    /// <summary>
    /// Walks into the portal holding <paramref name="portal"/> and stands still in it until the dimension changes:
    /// steps out first when already inside (just arrived), re-enters if nudged out while waiting. Ok = the dimension changed.
    /// </summary>
    public static async Task<ActionResult> EnterAsync(WorldState w, Func<Goal, CancellationToken, Task<ActionResult>> go,
        Func<int, CancellationToken, Task> waitTicks, BlockPos portal, TimeSpan switchTimeout, CancellationToken ct)
    {
        int dim = w.Dimension;
        bool Switched() => w.Dimension != dim;
        var stands = StandCells(w, portal, w.Player.Position);
        if (stands.Count == 0)
            return ActionResult.Fail(w.GetBlock(portal) == Blocks.Portal ? $"portal at {portal} has no floor to stand on" : $"no portal block at {portal}");

        if (InPortal(w, w.Player.Position))
        {
            // Just came through (or never left): the server's cooldown refreshes every tick in the sheet.
            var outs = StepOutCells(w, w.Player.Position);
            ActionResult? stepped = null;
            foreach (var o in outs.Take(4))
            {
                stepped = await go(new GoalBlock(o), ct).ConfigureAwait(false);
                if (Switched()) return Arrived(w, dim);
                if (stepped.Ok && !InPortal(w, w.Player.Position)) break;
            }
            if (InPortal(w, w.Player.Position))
                return ActionResult.Fail("standing in the portal but can't step out of it (the server only lets you back through after "
                                         + "~1 s outside the sheet)" + (stepped is { Ok: false } s ? ": " + s.Message : ""));
            await waitTicks(StepOutTicks, ct).ConfigureAwait(false);
            stands = StandCells(w, portal, w.Player.Position);
            if (stands.Count == 0) return ActionResult.Fail($"the portal at {portal} is gone");
        }

        ActionResult? last = null;
        foreach (var stand in stands.Take(3))
        {
            last = await go(new GoalBlock(stand), ct).ConfigureAwait(false);
            if (Switched()) return Arrived(w, dim);
            if (InPortal(w, w.Player.Position)) break;
        }
        if (!InPortal(w, w.Player.Position))
            return ActionResult.Fail($"couldn't step into the portal at {stands[0]}" + (last is { Ok: false } l ? ": " + l.Message : ""));

        // Stand still: ~80 ticks in the sheet, then 0x09. A shove out (player, mob) gets one walk back in.
        var until = DateTime.UtcNow + switchTimeout;
        bool retried = false;
        while (!Switched())
        {
            if (DateTime.UtcNow >= until) return ActionResult.Fail($"stood in the portal at {stands[0]} but nothing happened (is it lit, allow-nether on?)");
            if (!retried && !InPortal(w, w.Player.Position))
            {
                retried = true;
                await go(new GoalBlock(stands[0]), ct).ConfigureAwait(false);
                continue;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return Arrived(w, dim);
    }

    private static ActionResult Arrived(WorldState w, int from) =>
        ActionResult.Success($"through the portal: {Name(from)} -> {Name(w.Dimension)}");

    public static string Name(int dim) => dim switch { -1 => "nether", 0 => "overworld", 1 => "end", _ => "dimension " + dim };

    private static double Sq(BlockPos p, Vec3d v)
    {
        double dx = p.X + 0.5 - v.X, dy = p.Y - v.Y, dz = p.Z + 0.5 - v.Z;
        return dx * dx + dy * dy + dz * dz;
    }
}
