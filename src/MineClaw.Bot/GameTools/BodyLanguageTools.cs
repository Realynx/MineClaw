using MineClaw.Agent;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>act {move, target?, times?}</c> (brain only, instant): jump, spin, look at a player, wave, crouch, playful punch.
/// Backed by <c>MinecraftClient.ActAsync</c>; punch consent and safety are enforced client-side. The target defaults to
/// whoever spoke this turn.
/// </summary>
public static class BodyLanguageTools
{
    public const string ToolName = "act";
    public static readonly string[] Moves = ["jump", "spin", "look", "wave", "crouch", "punch"];

    public static void Register(ToolRegistry tools, IGameClient client) =>
        tools.Register(ToolName,
            "Body language: jump, spin, look at/punch a player (playful), wave, crouch (times 0 = stand).",
            ToolSchema.Create().Str("move", enumValues: Moves).Str("target", "player", required: false).Int("times", required: false).Build(),
            async (input, ct) =>
            {
                if (client is not MinecraftGameClient mg) return ToolResult.Error("body language isn't available on this client");
                var move = input.ReqStr("move");
                var target = input.Str("target") is { Length: > 0 } t ? t : TurnContext.Current?.Speaker;
                var r = await mg.Inner.ActAsync(move, target, input.Int("times"), ct).ConfigureAwait(false);
                return r.Ok ? ToolResult.Success(r.Message) : ToolResult.Error(r.Message);
            }, tiers: ToolTiers.Main);
}
