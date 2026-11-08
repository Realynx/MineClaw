using System.Text.Json;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

public static class RemoveStorageSignTool
{
    public const string ToolName = "remove_storage_sign";

    public static void Register(ToolRegistry tools, IGameClient client)
    {
        tools.Register(ToolName, "Remove the unchanged storage label sign attached to this chest.",
            ToolSchema.Create().Str("at", "sign x,y,z").Str("chest", "attached chest x,y,z").StrArray("lines", "four observed sign lines").Build(),
            async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } at || Locations.ParseCoords(i.ReqStr("chest")) is not { } chest)
                    return ToolResult.Error("at and chest must be x,y,z");
                if (!i.TryGetProperty("lines", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4
                    || value.EnumerateArray().Any(line => line.ValueKind != JsonValueKind.String)) return ToolResult.Error("lines must contain four strings");
                var lines = value.EnumerateArray().Select(line => line.GetString()!).ToArray(); // blank lines are part of the exact snapshot
                if (GameClients.Minecraft(client)?.Inner is not { } real) return ToolResult.Error("safe storage sign removal is unavailable for this body");
                return GameToolSet.R(await real.RemoveStorageSignAsync(new BlockPos(at.X, at.Y, at.Z),
                    new BlockPos(chest.X, chest.Y, chest.Z), lines, ct).ConfigureAwait(false));
            }, tiers: ToolTiers.None);
    }
}
