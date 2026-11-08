using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

public static class PolicyChopTool
{
    public const string ToolName = "chop_with_tool";
    public static void Register(ToolRegistry tools, IGameClient client)
    {
        tools.Register(ToolName, "Chop exactly this tree while the policy-required tool remains available; stop when it breaks.",
            ToolSchema.Create().Str("at", "x,y,z").Str("tool", "required tool item").Build(), async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (Items.ByNameWithDamage(i.ReqStr("tool")) is not { } item) return ToolResult.Error("unknown required tool");
                if (GameClients.Minecraft(client)?.Inner is not { } real) return ToolResult.Error("policy chopping is unavailable for this body");
                return GameToolSet.R(await real.ChopTreeAsync(new BlockPos(p.X, p.Y, p.Z), options: TreeChopOptions.Default with
                    { OnlyThisTree = true, RequiredToolId = item.Item.Id }, ct: ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None, longRunning: true);
    }
}
