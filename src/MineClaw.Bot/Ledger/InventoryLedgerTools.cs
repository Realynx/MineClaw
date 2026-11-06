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
    internal static string? ToolCause(string tool, JsonElement i) => tool switch
    {
        "craft" => $"crafting {StrArg(i, "item") ?? "something"}",
        "withdraw" => $"taken from a chest ({StrArg(i, "at") ?? StrArg(i, "chest") ?? "chest"})",
        // deposit: none here, the ledger names the chest the client really opened ("deposited (chest 14,64,-5)")
        _ => FixedCauses.GetValueOrDefault(tool),
    };

    /// <summary>The causes that don't depend on the call's arguments.</summary>
    private static readonly Dictionary<string, string> FixedCauses = new(StringComparer.Ordinal)
    {
        ["organize_chest"] = "organizing a chest",
        ["smelt"] = "smelting",
        ["collect_furnace"] = "taken from a furnace",
        ["place"] = "placed as a block",
        ["use_block"] = "used on a block",
        ["eat"] = "eaten",
        ["chop_tree"] = "chop_tree",
        ["mine"] = "mine",
        ["harvest_generator"] = "harvest_generator",
        ["dig"] = "dig",
        ["collect_drops"] = "picked up",
        ["goto"] = InventoryLedger.MovingCause,
        ["follow"] = InventoryLedger.MovingCause,
    };

    private static string? StrArg(JsonElement i, string k) =>
        i.ValueKind == JsonValueKind.Object && i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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

    /// <summary>
    /// The main body's ledger, and the tool wrappers every body shares: inventory/where_is {item} and the cause scopes read the
    /// ledger of the body in scope (one brain, many bodies: a clone body's own, <see cref="Create"/>), else main's.
    /// </summary>
    public static InventoryLedger Attach(AgentRuntime rt, MinecraftClient client, SkillRegistry? skills, ILogger? log, TimeProvider? time = null)
    {
        var ledger = Create(client, log, time);
        var inv = client.World.Inventory;
        (InventoryLedger Ledger, InventoryManager Inv) Current() =>
            Bodies.BodyScope.Current is Bodies.Body { IsMain: false, Ledger: { } own, Client: { } c } ? (own, c.World.Inventory) : (ledger, inv);
        if (skills is not null) skills.RunScope = (name, args) => Current().Ledger.Scope(SkillCause(name, args));

        WrapGameTools(rt.Tools, () => Current().Ledger);
        WrapInventory(rt.Tools, Current);
        WrapWhereIs(rt.Tools, Current);
        return ledger;
    }

    /// <summary>
    /// A body's ledger over its own connection: inventory snapshots, pickups (own and other players'), eating, death, server
    /// corrections, and its pump. The tools find it through the body in scope (<see cref="Attach"/>).
    /// </summary>
    public static InventoryLedger Create(MinecraftClient client, ILogger? log, TimeProvider? time = null, string? body = null)
    {
        var ledger = new InventoryLedger(time)
        {
            CurrentAction = () => client.CurrentAction,
            CurrentContainer = () => client.LastContainerPos is { } p ? $"chest {p.X},{p.Y},{p.Z}" : null,
        };
        var inv = client.World.Inventory;
        inv.Changed += () => { try { ledger.Observe(Counts(inv)); } catch (Exception) { /* never break the packet thread */ } };
        client.WindowItemsArriving += _ => ledger.NoteResync();
        client.ItemCollected += (collector, _, stack) =>
        {
            if (stack is not { IsEmpty: false } s) return;
            if (collector == client.World.Player.EntityId) ledger.NotePickup(s.Id, s.Count);
            else if (client.World.Entities.Get(collector) is { Name: { Length: > 0 } name }) ledger.NoteOtherPickup(name, s.Id, s.Count);
        };
        client.EatingStarted += (id, auto) => ledger.NoteEat(id, auto);
        client.Died += () =>
        {
            var p = client.World.Player.Position.ToBlockPos();
            ledger.NoteDeath($"{p.X},{p.Y},{p.Z}");
        };
        string tag = body is { Length: > 0 } b ? b + " " : "";
        ledger.Recorded += e =>
        {
            if (e.Delta < 0) log?.LogInformation("ledger: {Body}{Entry}", tag, e);
            else log?.LogDebug("ledger: {Body}{Entry}", tag, e);
        };
        ledger.Pump = new Timer(_ => { try { ledger.Flush(); } catch (Exception) { } }, null, 500, 500);
        return ledger;
    }

    private static void WrapGameTools(ToolRegistry tools, Func<InventoryLedger> ledger)
    {
        foreach (var name in new[] { "craft", "deposit", "withdraw", "organize_chest", "smelt", "collect_furnace", "place", "use_block", "eat",
                     "chop_tree", "mine", "harvest_generator", "dig", "collect_drops", "goto", "follow" })
        {
            if (!tools.TryGet(name, out var inner)) continue;
            bool gen = name == "harvest_generator";
            tools.Replace(new WrappedTool(inner, null, async (input, ct) =>
            {
                var l = ledger();
                var since = DateTimeOffset.UtcNow;
                int before = gen ? HarvestedCobble(l, since) : 0;
                ToolResult r;
                using (ToolCause(name, input) is { } cause ? l.Scope(cause) : null)
                    r = await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
                return gen ? r with { Text = LedgerCount(r.Text, HarvestedCobble(l, since) - before) } : r;
            }));
        }
    }

    /// <summary>Cobblestone the ledger booked as gained by a harvest (picked up, or unattributed gains in its scope) in entries
    /// that can still merge with changes after <paramref name="since"/> (entries grow in place within the merge window).</summary>
    private static int HarvestedCobble(InventoryLedger ledger, DateTimeOffset since)
    {
        var from = since - ledger.MergeWindow - TimeSpan.FromSeconds(1);
        return ledger.Entries.Where(e => e.ItemId == Blocks.Cobblestone && e.Delta > 0 && e.At >= from
                                         && (e.Cause.StartsWith("picked up", StringComparison.Ordinal) || e.Cause == "harvest_generator"))
            .Sum(e => e.Delta);
    }

    private static readonly System.Text.RegularExpressions.Regex GenCountRx = new(@"\bgenerator: (\d+)/(\d+) cobblestone\b");

    /// <summary>
    /// The task result states the cobble the ledger saw arrive (live 10-28: "dug 64/64, stored 4" became "I mined all 64"):
    /// "generator: 7/64 cobblestone ..." keeps the smaller of the loop's own count and the ledger's.
    /// </summary>
    internal static string LedgerCount(string text, int ledgerGained)
    {
        var m = GenCountRx.Match(text);
        if (!m.Success || ledgerGained < 0 || ledgerGained >= int.Parse(m.Groups[1].Value)) return text;
        return text[..m.Groups[1].Index] + ledgerGained + text[(m.Groups[1].Index + m.Groups[1].Length)..];
    }

    /// <summary><c>inventory {item}</c>: that item's count now plus where it came from / went this session.</summary>
    private static void WrapInventory(ToolRegistry tools, Func<(InventoryLedger Ledger, InventoryManager Inv)> current)
    {
        if (!tools.TryGet("inventory", out var inner)) return;
        var schema = AddProperty(inner.InputSchema, "item"); // item = its history: where it went / came from
        tools.Replace(new WrappedTool(inner, schema, async (input, ct) =>
        {
            var item = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("item", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(item)) return await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
            var (ledger, inv) = current();
            int have = Have(inv, item);
            return ToolResult.Success(ledger.Explain(item, have) ?? $"{item}: have {have}; no gains or losses of it recorded this session");
        }, inner.Description.TrimEnd('.') + ". item=where it went."));
    }

    /// <summary><c>where_is {item}</c> that finds none in known chests also says what the ledger knows ("tossed to ...").</summary>
    private static void WrapWhereIs(ToolRegistry tools, Func<(InventoryLedger Ledger, InventoryManager Inv)> current)
    {
        if (!tools.TryGet("where_is", out var inner)) return;
        tools.Replace(new WrappedTool(inner, null, async (input, ct) =>
        {
            var r = await inner.ExecuteAsync(input, ct).ConfigureAwait(false);
            var item = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("item", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var (ledger, inv) = current();
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

    internal sealed class WrappedTool(IAgentTool inner, JsonElement? schema, Func<JsonElement, CancellationToken, Task<ToolResult>> run,
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
