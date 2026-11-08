using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>A reserved quarry cell is dug from the current access pillar without walking off it to reposition.
/// The usual dig protections and floor guard still apply; this does not authorize clearing protected builds.</summary>
public static class StationaryDigTool
{
    public const string ToolName = "dig_here";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Break one visible block from the current position without walking.",
            ToolSchema.Create().Str("at", "x,y,z").Build(), async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (GameClients.Minecraft(c)?.Inner is not { } real) return ToolResult.Error("stationary digging is unavailable for this body");
                return GameToolSet.R(await real.DigHereAsync(new BlockPos(p.X, p.Y, p.Z), ct, floorGuard: true).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
