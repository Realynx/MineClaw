using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>face{at:"x,y,z"}</c>: turn the head to look at a point (fractions allowed). A hidden script action (no brain definition): Story Mode
/// pawns face each other while they socialise, look up at the stars, watch the sunset. Pure body language: it moves nothing.
/// </summary>
public static class FaceTool
{
    public const string ToolName = "face";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Turn to look at x,y,z.", ToolSchema.Create().Str("at", "x,y,z").Build(),
            async (i, ct) =>
            {
                var parts = i.ReqStr("at").Split(',');
                if (parts.Length != 3 || !double.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var x)
                    || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var y)
                    || !double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var z))
                    return ToolResult.Error("at must be x,y,z");
                if (c is not MinecraftGameClient m) return ToolResult.Error("this body can't turn");
                var r = await m.Inner.LookAtAsync(new Vec3d(x, y, z), ct).ConfigureAwait(false);
                return r.Ok ? ToolResult.Success("facing " + i.ReqStr("at")) : ToolResult.Error(r.Message);
            }, tiers: ToolTiers.None);
    }
}
