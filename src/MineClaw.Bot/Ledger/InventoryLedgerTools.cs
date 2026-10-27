using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Tools;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.Skills;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Ledger;

/// <summary>
/// Wires the <see cref="InventoryLedger"/> into a live bot: inventory snapshots, pickups (own and other players'),
/// death, server corrections (0x68), cause scopes around game tools and skills; and answers "where did X go" through the
/// existing <c>inventory {item}</c> and <c>where_is {item}</c> tools (no new tool, no prefix growth).
/// </summary>
public static class InventoryLedgerTools
{
    /// <summary>Game tools that move items, and how their changes read in the ledger.</summary>
    private static string? ToolCause(string tool, JsonElement i)
    {
        string? Arg(string k) => i.ValueKind == JsonValueKind.Object && i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            "craft" => $"crafting {Arg("item") ?? "something"}",
            "deposit" => $"deposited ({Arg("at") ?? Arg("chest") ?? "chest"})",
            "withdraw" => $"taken from a chest ({Arg("at") ?? Arg("chest") ?? "chest"})",
            "organize_chest" => "organizing a chest",
            "smelt" => "smelting",
            "collect_furnace" => "taken from a furnace",
            "place" => "placed as a block",
            "use_block" => "used on a block",
            "eat" => "eaten",
            "chop_tree" => "chop_tree",
            "mine" or "harvest_generator" or "dig" => tool,
            "collect_drops" => "picked up",
            "goto" or "follow" => "placed while moving (bridge/pillar block)",
            _ => null,
        };
    }

    private static string SkillCause(string skill, JsonElement args)
    {
        string? player = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return skill switch
        {
            "give_items" => $"tossed to {player ?? "a player"}",
            "drop_items" => "dropped on purpose",
            _ => $"skill {skill}",
        };
    }

    /// <summary>Counts per (item, damage) of everything the bot carries: window-0 slots 1..44 (craft grid, armor,
    /// inventory, hotbar; not the craft output) plus the cursor.</summary>
    public static Dictionary<(int Id, int Damage), int> Counts(InventoryManager inv)
    {
        var d = new Dictionary<(int, int), int>();
        var slots = inv.PlayerSlots();
        void Add(ItemStack s)
        {
            if (s.IsEmpty) return;
            int dmg = MineClaw.Data.Items.Get(s.Id) is { MaxDamage: > 0 } ? 0 : s.Damage; // tool wear isn't a new item
            d[(s.Id, dmg)] = d.GetValueOrDefault((s.Id, dmg)) + s.Count;
        }
        for (int i = 1; i < slots.Length; i++) Add(slots[i]);
        Add(inv.Cursor);
        return d;
    }

    public static InventoryLedger Attach(AgentRuntime rt, MinecraftClient client, SkillRegistry? skills, ILogger? log, TimeProvider? time = null)
    {
        var ledger = new InventoryLedger(time) { CurrentAction = () => client.CurrentAction };
        var inv = client.World.Inventory;
        inv.Changed += () => { try { ledger.Observe(Counts(inv)); } catch (Exception) { /* never break the packet thread */ } };
        client.WindowItemsArriving += _ => ledger.NoteResync();
        client.ItemCollected += (collector, _, stack) =>
        {
            if (stack is not { IsEmpty: false } s) return;
            if (collector == client.World.Player.EntityId) ledger.NotePickup(s.Id, s.Count);
            else if (client.World.Entities.Get(collector) is { Name: { Length: > 0 } name }) ledger.NoteOtherPickup(name, s.Id, s.Count);
        };
        client.Died += () =>
        {
            var p = client.World.Player.Position.ToBlockPos();
            ledger.NoteDeath($"{p.X},{p.Y},{p.Z}");
        };
        ledger.Recorded += e =>
        {
            if (e.Delta < 0) log?.LogInformation("ledger: {Entry}", e);
            else log?.LogDebug("ledger: {Entry}", e);
        };
        ledger.Pump = new Timer(_ => { try { ledger.Flush(); } catch (Exception) { } }, null, 500, 500);
        if (skills is not null) skills.RunScope = (name, args) => ledger.Scope(SkillCause(name, args));

        WrapGameTools(rt.Tools, ledger);
        WrapInventory(rt.Tools, ledger, inv);
        WrapWhereIs(rt.Tools, ledger, inv);
        return ledger;
    }

    private static void WrapGameTools(ToolRegistry tools, InventoryLedger ledger)
    {
        foreach (var name in new[] { "craft", "deposit", "withdraw", "organize_chest", "smelt", "collect_furnace", "place", "use_block", "eat",
                     "chop_tree", "mine", "harvest_generator", "dig", "collect_drops", "goto", "follow" })
        {
            if (!tools.TryGet(name, out var inner)) continue;
            tools.Replace(new WrappedTool(inner, null, async (input, ct) =>
            {
                using var _ = ToolCause(name, input) is { } cause ? ledger.Scope(cause) : null;
                return await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
            }));
        }
    }

    /// <summary><c>inventory {item}</c>: that item's count now plus where it came from / went this session.</summary>
    private static void WrapInventory(ToolRegistry tools, InventoryLedger ledger, InventoryManager inv)
    {
        if (!tools.TryGet("inventory", out var inner)) return;
        var schema = AddProperty(inner.InputSchema, "item"); // item = its history: where it went / came from
        tools.Replace(new WrappedTool(inner, schema, async (input, ct) =>
        {
            var item = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("item", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(item)) return await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
            int have = Have(inv, item);
            return ToolResult.Success(ledger.Explain(item, have) ?? $"{item}: have {have}; no gains or losses of it recorded this session");
        }, inner.Description.TrimEnd('.') + ". item=where it went."));
    }

    /// <summary><c>where_is {item}</c> that finds none in known chests also says what the ledger knows ("tossed to ...").</summary>
    private static void WrapWhereIs(ToolRegistry tools, InventoryLedger ledger, InventoryManager inv)
    {
        if (!tools.TryGet("where_is", out var inner)) return;
        tools.Replace(new WrappedTool(inner, null, async (input, ct) =>
        {
            var r = await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
            var item = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("item", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(item) || ledger.Explain(item, Have(inv, item), 260) is not { } story) return r;
            return r with { Text = r.Text + " | my ledger: " + story };
        }));
    }

    private static int Have(InventoryManager inv, string query) =>
        Counts(inv).Where(kv => InventoryLedger.Matches(MineClaw.Data.Items.DisplayName(kv.Key.Id, kv.Key.Damage), query)).Sum(kv => kv.Value);

    private static JsonElement AddProperty(JsonElement schema, string name)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!.AsObject();
        if (node["properties"] is not System.Text.Json.Nodes.JsonObject props) node["properties"] = props = new System.Text.Json.Nodes.JsonObject();
        props[name] = new System.Text.Json.Nodes.JsonObject { ["type"] = "string" };
        return JsonSerializer.SerializeToElement(node);
    }

    private sealed class WrappedTool(IAgentTool inner, JsonElement? schema, Func<JsonElement, CancellationToken, Task<ToolResult>> run,
        string? description = null) : IAgentTool
    {
        public string Name => inner.Name;
        public string Description => description ?? inner.Description;
        public JsonElement InputSchema => schema ?? inner.InputSchema;
        public bool IsLongRunning => inner.IsLongRunning;
        public bool AvailableToCheapTier => inner.AvailableToCheapTier;
        public ToolTiers Tiers => inner.Tiers;
        public int? MaxResultChars => inner.MaxResultChars;
        public Task<ToolResult> ExecuteAsync(JsonElement input, CancellationToken ct) => run(input, ct);
    }
}
