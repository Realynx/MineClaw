using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>clear_dig{at:"x,y,z"}</c>: dig out the block in a Story build's cell before the design's block goes in (a hidden script action,
/// no brain definition; <c>story.place</c>'s clear only). The colony owner ordered that cell built, so soft protection on natural ground
/// yields (live 11-05: natural stone and dirt in a house's cells were refused as "someone's floor", about 9000 failed placements in five
/// hours); a chest, a never-break mark or another block a player placed still refuses, and every safety rule holds
/// (<c>MinecraftClient.DigForBuildAsync</c>).
/// </summary>
public static class ClearDigTool
{
    public const string ToolName = "clear_dig";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Dig out the block in a build's cell before its own block goes in.", ToolSchema.Create().Str("at", "x,y,z").Build(),
            async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (GameClients.Minecraft(c)?.Inner is not { } real) return GameToolSet.R(await c.DigBlockAsync(p.X, p.Y, p.Z, ct).ConfigureAwait(false));
                return GameToolSet.R(await real.DigForBuildAsync(new BlockPos(p.X, p.Y, p.Z), ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
