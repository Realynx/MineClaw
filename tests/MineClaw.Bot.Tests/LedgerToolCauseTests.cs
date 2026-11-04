using System.Text.Json;
using MineClaw.Bot.Ledger;

namespace MineClaw.Bot.Tests;

/// <summary>The ledger's tool-cause table (WP03 refactor) answers exactly like the switch it replaced, over every tool name the
/// registry knows plus near misses, with and without arguments.</summary>
public class LedgerToolCauseTests
{
    private static string? Old(string tool, JsonElement i)
    {
        string? Arg(string k) => i.ValueKind == JsonValueKind.Object && i.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return tool switch
        {
            "craft" => $"crafting {Arg("item") ?? "something"}",
            "deposit" => null,
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

    [Fact]
    public void Tool_causes_match_the_old_switch()
    {
        string[] tools =
        [
            "craft", "deposit", "withdraw", "organize_chest", "smelt", "collect_furnace", "place", "use_block", "eat", "chop_tree", "mine",
            "harvest_generator", "dig", "collect_drops", "goto", "follow", "see", "act", "script", "", "Craft", "MINE", "goto ", "equip",
        ];
        string[] inputs = ["{}", """{"item":"bread"}""", """{"at":"1,2,3"}""", """{"chest":"main"}""", """{"at":"1,2,3","chest":"x","item":"log"}""", """{"item":5}""", "[]", "\"x\""];
        foreach (var t in tools)
        foreach (var json in inputs)
        {
            var i = JsonDocument.Parse(json).RootElement;
            Assert.Equal(Old(t, i), InventoryLedgerTools.ToolCause(t, i));
        }
    }
}
