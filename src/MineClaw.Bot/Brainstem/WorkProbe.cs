using StandingOrder = MineClaw.Agent.Memory.StandingOrder;
using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// "Is there work for this standing order again?" read from the world model, zero tokens (<see cref="Agent.Human.StandingOrders.WorkReady"/>):
/// a tree job → a grown natural tree (log under leaves, not part of a build); a gen job → a regenerated cell; a crop job → a
/// mature crop. Null = this kind of job can't be probed (the backoff simply runs out).
/// </summary>
public static class WorkProbe
{
    public const int Radius = 24;

    public static bool? Ready(StandingOrder order, IGameClient client, Func<BlockPos, IReadOnlyList<BlockPos>>? genCells = null)
    {
        if (!client.IsPlaying) return null;
        var job = (order.Job + " " + order.Input.GetRawText()).ToLowerInvariant();
        var w = client.World;
        var me = w.Player.Position.ToBlockPos();
        bool Protected(BlockPos p, int id) => client.IsProtected?.Invoke(p, id) == true;

        if (ContainsAny(job, "tree", "wood", "log", "chop", "sapling", "lumber"))
            return Scan(w, me, (p, id) => id == Blocks.Log && w.GetBlock(p.X, p.Y + 1, p.Z) is var up && (up == Blocks.Log || up == Blocks.Leaves)
                                          && HasLeavesNear(w, p) && !Protected(p, id));
        if (ContainsAny(job, "cobble", "gen", "stone"))
        {
            if (genCells == null) return null;
            return genCells(me).Any(c => w.GetBlock(c.X, c.Y, c.Z) is Blocks.Cobblestone or Blocks.Stone);
        }
        if (ContainsAny(job, "wheat", "crop", "harvest", "farm"))
            return Scan(w, me, (p, id) => id == Blocks.Crops && w.GetMeta(p.X, p.Y, p.Z) >= 7);
        return null;
    }

    private static bool ContainsAny(string s, params string[] words) => words.Any(s.Contains);

    private static bool HasLeavesNear(WorldState w, BlockPos p)
    {
        for (int dy = 1; dy <= 6; dy++)
            if (w.GetBlock(p.X, p.Y + dy, p.Z) == Blocks.Leaves) return true;
        return false;
    }

    private static bool Scan(WorldState w, BlockPos me, Func<BlockPos, int, bool> match)
    {
        for (int x = me.X - Radius; x <= me.X + Radius; x++)
        for (int z = me.Z - Radius; z <= me.Z + Radius; z++)
        for (int y = Math.Max(0, me.Y - 8); y <= Math.Min(127, me.Y + 8); y++)
        {
            int id = w.GetBlock(x, y, z);
            if (id <= 0) continue;
            if (match(new BlockPos(x, y, z), id)) return true;
        }
        return false;
    }
}
