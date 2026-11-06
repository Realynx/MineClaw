using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>clear_temporary{}</c>: dig back the blocks this body put down for itself (a walk's pillar or bridge, a shelter box, a fall clutch)
/// that it can reach from where it stands (a hidden script action; the Story pawn runs it after every job, whatever the outcome). Fox
/// 11-05: "Bots should delete whatever they put down unless it was a directive to build it." What it can't reach stays tracked: the
/// colony turns it into a cleanup job for any pawn (docs/web/story-mode.md "Temporary blocks").
/// </summary>
public static class ClearTemporaryTool
{
    public const string ToolName = "clear_temporary";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Dig back the scaffold, pillar and shelter blocks this body placed for itself.", ToolSchema.Create().Build(),
            async (_, ct) =>
            {
                if (GameClients.Minecraft(c)?.Inner is not { } real) return ToolResult.Success("temporary blocks: none");
                return GameToolSet.R(await real.CleanupTemporaryBlocksAsync(ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
