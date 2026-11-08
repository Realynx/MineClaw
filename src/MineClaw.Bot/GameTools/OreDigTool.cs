using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

public static class OreDigTool
{
    public const string ToolName = "dig_ore_here";
    public static void Register(ToolRegistry tools, IGameClient client)
    {
        tools.Register(ToolName, "Mine only the expected ore from the current position, preserving other blocks and exposure safety.",
            ToolSchema.Create().Str("at", "x,y,z").Int("ore", "canonical ore block ID").Build(), async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (i.Int("ore") is not { } ore) return ToolResult.Error("ore must be a canonical ore block ID");
                if (GameClients.Minecraft(client)?.Inner is not { } real) return ToolResult.Error("guarded ore mining is unavailable for this body");
                return GameToolSet.R(await real.DigOreHereAsync(new BlockPos(p.X, p.Y, p.Z), ore, ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
