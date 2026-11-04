using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// <c>goto</c> to a place in the other dimension: through the best known linked portal pair (least walking on both sides),
/// or an honest failure ("no known portal to nether") — never a walk to the other dimension's coordinates here.
/// </summary>
public static class PortalTravel
{
    public static TimeSpan SwitchTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan ArriveTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The route (or why there is none) without moving.</summary>
    public static PortalRoute? Plan(PortalLinks? links, int fromDim, Vec3d from, int toDim, BlockPos target) =>
        links?.Plan(fromDim, (from.X, from.Y, from.Z), toDim, (target.X + 0.5, target.Y, target.Z + 0.5));

    public static async Task<ToolResult> GotoAsync(IGameClient c, PortalLinks? links, BlockPos target, int targetDim, GotoOptions? options,
        double dist, CancellationToken ct)
    {
        int here = c.World.Dimension;
        var route = Plan(links, here, c.World.Player.Position, targetDim, target);
        if (route == null) return ToolResult.Error($"no known portal to {WorldDim.Name(targetDim)}");

        var entry = route.Entry;
        var (portal, error) = await FindEntryAsync(c, entry, options, here, ct).ConfigureAwait(false);
        if (error != null) return error;
        if (c.World.Dimension == here)
        {
            // b1.8.1: step out first if standing in it, walk in, ~4 s standing in the sheet, then 0x09 Respawn with the
            // other dimension, then chunks + a teleport.
            var e = await EnterAsync(c, portal!.Value, options, ct).ConfigureAwait(false);
            if (!e.Ok && c.World.Dimension == here) return ToolResult.Error($"portal at {entry.Pos}: {e.Message}");
        }
        if (!await WaitAsync(() => c.World.Dimension == targetDim, SwitchTimeout, ct).ConfigureAwait(false))
            return ToolResult.Error($"stood in the portal at {entry.Pos} but nothing happened");
        await WaitAsync(() =>
        {
            var p = c.World.Player.Position.ToBlockPos();
            return c.IsPlaying && c.World.IsLoaded(p.X, p.Z);
        }, ArriveTimeout, ct).ConfigureAwait(false);

        Goal goal = dist <= 0 ? new GoalBlock(target.X, target.Y, target.Z) : new GoalNear(target.X, target.Y, target.Z, dist);
        var r = await c.GotoAsync(goal, options, ct).ConfigureAwait(false);
        var via = $"via portal {entry.Pos} -> {WorldDim.Name(targetDim)} {route.Exit.Pos}";
        return r.Ok ? ToolResult.Success($"{via}: {r.Message}") : ToolResult.Error($"{via}, then: {r.Message}");
    }

    /// <summary>The entry portal's sheet cell; out of sight, walk there first (the sheet's blocks are only known once its chunk
    /// is loaded). An error when it can't be reached or isn't lit any more (unless the walk already changed dimension).</summary>
    private static async Task<(BlockPos? Portal, ToolResult? Error)> FindEntryAsync(IGameClient c, PortalEnd entry, GotoOptions? options, int here,
        CancellationToken ct)
    {
        var portal = PortalCellNear(c.World, new BlockPos(entry.X, entry.Y, entry.Z));
        if (portal != null) return (portal, null);
        var g = await c.GotoAsync(new GoalNear(entry.X, entry.Y, entry.Z, 3), options, ct).ConfigureAwait(false);
        if (!g.Ok && c.World.Dimension == here) return (null, ToolResult.Error($"can't reach the portal at {entry.Pos}: {g.Message}"));
        portal = PortalCellNear(c.World, new BlockPos(entry.X, entry.Y, entry.Z));
        if (portal == null && c.World.Dimension == here) return (null, ToolResult.Error($"no lit portal at {entry.Pos} any more"));
        return (portal, null);
    }

    /// <summary>Walks into the portal holding <paramref name="portal"/> and stands still until the dimension changes
    /// (<see cref="PortalEntry"/>: steps out first when already inside, the server's cooldown refreshes in the sheet).</summary>
    public static Task<ActionResult> EnterAsync(IGameClient c, BlockPos portal, GotoOptions? options, CancellationToken ct)
    {
        var tick = GameClients.Minecraft(c) is { } mg ? mg.Inner.Options.TickInterval : TimeSpan.FromMilliseconds(50);
        return PortalEntry.EnterAsync(c.World, (goal, t) => c.GotoAsync(goal, options, t),
            (ticks, t) => Task.Delay(tick * Math.Max(1, ticks), t), portal, SwitchTimeout, ct);
    }

    /// <summary><c>goto</c> onto a portal block: go through it (step in, stand still, wait for the switch and the arrival
    /// chunks). Live: "goto 2,66,0" was a within-1 goal that the bot already met standing in the sheet next to it.</summary>
    public static async Task<ToolResult> GoThroughAsync(IGameClient c, BlockPos portal, GotoOptions? options, CancellationToken ct)
    {
        int here = c.World.Dimension;
        var e = await EnterAsync(c, portal, options, ct).ConfigureAwait(false);
        if (c.World.Dimension == here) return ToolResult.Error(e.Message);
        await WaitAsync(() =>
        {
            var p = c.World.Player.Position.ToBlockPos();
            return c.IsPlaying && c.World.IsLoaded(p.X, p.Z);
        }, ArriveTimeout, ct).ConfigureAwait(false);
        return ToolResult.Success($"went through the portal: now in the {WorldDim.Name(c.World.Dimension).ToUpperInvariant()} at "
                                  + Locations.Fmt(c.World.Player.Position));
    }

    /// <summary>A portal block at or right around a recorded standing spot (the sheet may have been seen from another cell).</summary>
    public static BlockPos? PortalCellNear(WorldState w, BlockPos p)
    {
        if (w.GetBlock(p) == Blocks.Portal) return p;
        var f = w.FindBlocks(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5), 3, Blocks.Portal, 1);
        return f.Count > 0 ? f[0].Pos : null;
    }

    private static async Task<bool> WaitAsync(Func<bool> done, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return true;
    }
}
