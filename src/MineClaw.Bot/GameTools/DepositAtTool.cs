using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.GameTools;

/// <summary>A planned evacuation delivers only to its reserved destination. A full chest leaves items carried.</summary>
public static class DepositAtTool
{
    public const string ToolName = "deposit_at";

    public static void Register(ToolRegistry tools, IGameClient c)
    {
        tools.Register(ToolName, "Deposit selected items only at this chest; keep leftovers carried if it cannot accept them.",
            ToolSchema.Create().Str("at", "x,y,z").StrArray("items", "exact items to deliver").Build(), async (i, ct) =>
            {
                if (Locations.ParseCoords(i.ReqStr("at")) is not { } p) return ToolResult.Error("at must be x,y,z");
                var wanted = new List<(int Id, int Meta)>();
                foreach (var name in i.StrList("items"))
                    if (TransferItemSelector.Resolve(name) is { } item) wanted.Add((item.Item.Id, item.Damage));
                    else return ToolResult.Error($"unknown item '{name}'");
                if (wanted.Count == 0) return ToolResult.Error("items required");
                bool Match(ItemStack stack) => !stack.IsEmpty && wanted.Any(w => w.Id == stack.Id && (w.Meta < 0 || w.Meta == stack.Damage));
                if (!c.World.Inventory.PlayerSlots().Skip(9).Any(Match)) return ToolResult.Success("nothing to deposit");
                var result = await c.DepositAllAsync(new BlockPos(p.X, p.Y, p.Z), Match, ct).ConfigureAwait(false);
                if (!result.Ok) return ToolResult.Error($"chest at {p.X},{p.Y},{p.Z}: {result.Message}; remaining items carried");
                return c.World.Inventory.PlayerSlots().Skip(9).Any(Match)
                    ? ToolResult.Error($"chest at {p.X},{p.Y},{p.Z} is full; remaining items carried") : GameToolSet.R(result);
            }, tiers: ToolTiers.None);
    }
}
