using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Data;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// A hidden <c>place_turned {block, at, meta}</c> (no tier sees its definition; Story Mode's pawn script calls
/// <c>place_turned(block, at, meta)</c> for a design's furnace, stairs, log or torch): places the item so its block gets the meta's
/// facing, axis or half (<see cref="Client.MinecraftClient.PlaceTurnedAsync"/>). A block the click doesn't turn is placed as by
/// <c>place</c>.
/// </summary>
public static class TurnedPlaceTool
{
    public const string ToolName = "place_turned";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Place a block from inventory at x,y,z turned like the given block meta (facing, axis, half).",
            ToolSchema.Create().Str("block").Str("at", "x,y,z").Int("meta", "the block meta to end up with")
                .Bool("exact", "require the complete block state", required: false).Build(),
            async (i, ct) =>
            {
                if (Items.ByNameWithDamage(i.ReqStr("block")) is not { } it) return ToolResult.Error($"unknown block '{i.Str("block")}'");
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                bool exact = i.Bool("exact") == true;
                int meta = exact ? i.Int("meta") ?? -1 : Math.Clamp(i.Int("meta") ?? 0, 0, 15);
                int? damage = it.Damage > 0 || exact ? it.Damage : null;
                return GameToolSet.R(await c.PlaceTurnedAsync(p.X, p.Y, p.Z, it.Item.Id, damage, meta, ct, exact).ConfigureAwait(false));
            }, longRunning: true, tiers: ToolTiers.None);
    }
}
