using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>Explicit Story chest deconstruction; refuses contents rather than spilling them into item entities.</summary>
public static class DeconstructChestTool
{
    public const string ToolName = "deconstruct_chest";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Remove an explicitly designated chest after freshly verifying both halves are empty.",
            ToolSchema.Create().Str("at", "x,y,z").Build(), async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (GameClients.Minecraft(c)?.Inner is not { } real) return ToolResult.Error("safe chest deconstruction is unavailable for this body");
                return GameToolSet.R(await real.DeconstructChestAsync(new BlockPos(p.X, p.Y, p.Z), ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
