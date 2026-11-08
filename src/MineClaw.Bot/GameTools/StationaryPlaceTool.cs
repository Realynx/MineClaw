using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>Placement from a construction session's shared access, without walking off its temporary pillar.</summary>
public static class StationaryPlaceTool
{
    public const string ToolName = "place_here";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Place one block against a visible face from here, optionally with the requested rotation, without walking.",
            ToolSchema.Create().Str("block").Str("at", "x,y,z").Int("meta", "requested block meta", required: false).Build(), async (i, ct) =>
            {
                if (Items.ByNameWithDamage(i.ReqStr("block")) is not { } item) return ToolResult.Error($"unknown block '{i.Str("block")}'");
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                if (GameClients.Minecraft(c)?.Inner is not { } real) return ToolResult.Error("stationary placement is unavailable for this body");
                int? meta = i.Int("meta") is { } m ? Math.Clamp(m, 0, 15) : null;
                return GameToolSet.R(await real.PlaceHereAsync(new BlockPos(p.X, p.Y, p.Z), item.Item.Id,
                    item.Damage > 0 ? item.Damage : null, meta, ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
