using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
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
        var g = await c.GotoAsync(new GoalBlock(entry.X, entry.Y, entry.Z), options, ct).ConfigureAwait(false);
        if (!g.Ok && c.World.Dimension == here) return ToolResult.Error($"can't reach the portal at {entry.Pos}: {g.Message}");
        // b1.8.1: ~4 s standing in the sheet, then 0x09 Respawn with the other dimension, then chunks + a teleport.
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
