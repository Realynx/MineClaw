using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// A hidden <c>fish {at, seconds?, max?}</c> (no tier sees its definition; scripts call <c>fish(at, seconds)</c>, as Story Mode's
/// pawn script does in a fishing zone): cast at the water cell, reel in on bites, for the session
/// (<see cref="Client.MinecraftClient.FishAsync"/>).
/// </summary>
public static class FishingTool
{
    public const string ToolName = "fish";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Fish with a rod at a water cell for a while (cast, reel in on a bite, repeat).",
            ToolSchema.Create().Str("at", "x,y,z of a water cell").Int("seconds", "session length (default 120)", required: false)
                .Int("max", "stop after this many fish", required: false).Build(),
            async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                var secs = Math.Clamp(i.Int("seconds") ?? 120, 5, 600);
                var r = await c.FishAsync(new Vec3d(p.X + 0.5, p.Y + 0.9, p.Z + 0.5), TimeSpan.FromSeconds(secs),
                    Math.Clamp(i.Int("max") ?? 64, 1, 640), ct).ConfigureAwait(false);
                return GameToolSet.R(r);
            }, longRunning: true, tiers: ToolTiers.None);
    }
}
