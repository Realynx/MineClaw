using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>escape_dig{at:"x,y,z"}</c>: break one block to get out of a pit, a cave or a ravine, with whatever is in hand (a hidden script
/// action, no brain definition; Story Mode's <c>story.escape</c> only). Story pawns never break a work target for nothing ("needs a
/// wooden pickaxe: digging stone … would lose the drop"); getting out is worth a lost drop (live 11-04: three pawns waited for the
/// teleport in a hole they could have dug out of by hand). Every other dig rule (protection, the floor under someone, lava, the
/// void) still holds.
/// </summary>
public static class EscapeDigTool
{
    public const string ToolName = "escape_dig";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Break a block to get out (the drop may be lost).", ToolSchema.Create().Str("at", "x,y,z").Build(),
            async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (GameClients.Minecraft(c)?.Inner is not { } real) return GameToolSet.R(await c.DigBlockAsync(p.X, p.Y, p.Z, ct).ConfigureAwait(false));
                return GameToolSet.R(await real.DigForEscapeAsync(new BlockPos(p.X, p.Y, p.Z), ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
