using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Character;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Raptor;
using MineClaw.Agent.Rules;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins small parsers and normalizers (WP06 refactor) over corpora: prepared-line lint/parse, summary trimming, search terms,
/// terse levels, shard kinds/modes, prompt purposes, directive time words, chat sanitizing, intents, and <c>do</c> expansion.
/// </summary>
public class MiscParsersGoldenTests
{
    private const string Expected = "F633E0BF58009A3141E135B4B2A6D622EFA67D333AF5843F01B49778AC745028";

    private static readonly System.Reflection.MethodInfo NormalizeInput =
        typeof(DoTool).GetMethod("NormalizeInput", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static string Show(object? o) => o == null ? "null" : JsonSerializer.Serialize(o);

    [Fact]
    public void Parsers_and_normalizers_give_the_same()
    {
        var sb = new StringBuilder();
        var ctx = new CharacterContext("mining cobble", ["kiwi", "PoofImaFox"], "night", "giddy", "ok", "kiwi", "near the 12 chests");
        string?[] lines =
        [
            null, "", "  hi kiwi!  ", "\"on it\"", "`ok`", "line\nbreak", "/kill", "a {json}", "an [array]", "café", "going to 5, 64, -3",
            "i'll build you a house", "gonna mine some coal", "we have 12 chests", "we have 13 chests", "x".PadRight(95, 'x'), "on my way <3",
        ];
        foreach (var l in lines) sb.Append("lint ").Append(l ?? "null").Append(" => ").Append(LinePrefetcher.Lint(l, ctx) ?? "null").Append('\n');
        string?[] replies =
        [
            null, "", "nope", "```json\n{\"greet@kiwi\":[\"hey kiwi\",3,\"yo\"],\"status\":\"mining\",\"bye\":[],\"x\":{\"a\":1}}\n```", "{bad json}",
            "{\"thanks\":[\"ty!\"]} trailing", "[1,2]",
        ];
        foreach (var r in replies)
            sb.Append("parse ").Append(r ?? "null").Append(" => ").Append(Show(LinePrefetcher.Parse(r))).Append('\n');

        string[] summaries =
        [
            "", "no stop", "One. Two. Three", "Ends well.", "Cut mid word", "A! B? C", "Short. And then a much longer tail that never ends",
            "e.g. this. Then", "Line one.\nLine two", "Quote \"done.\" more", "Version 1.8.1 is old. tail",
        ];
        foreach (var s in summaries) sb.Append("trim ").Append(s).Append(" => ").Append(HaikuSummarizer.TrimToSentence(s)).Append('\n');

        string?[] queries = [null, "", "the iron ingots in the chest", "Where's my COBBLE gen??", "a b c", "furnace furnace fuel", "x-ray 1,64,2 tnt's"];
        foreach (var q in queries)
        {
            sb.Append("terms ").Append(q ?? "null").Append(" => ").Append(string.Join(",", TextTokens.Terms(q))).Append(" | ")
              .Append(string.Join(",", TextTokens.Terms(q, ["chest", "furnace"], 2))).Append('\n');
        }

        string?[] terse = [null, "", "off", "OFF", "lite", "light", "full", "on", "true", "false", "0", "1", "2", "3", "ultra", "max", "min", "nonsense", " Full ", "none", "ULTRA"];
        foreach (var t in terse) sb.Append("terse ").Append(t ?? "null").Append(" => ").Append(TerseOptions.TryParse(t, out var lvl)).Append(' ').Append(lvl).Append('\n');

        string?[] kinds = [null, "", "facts", "Fact", "note", "notes", "place", "waypoint", "places", "structure", "claims", "belief", "rule", "rules", "directive",
            "trait", "self", "lesson", "correction", "observation", "obs", "knowledge", "kb", "gamedata", "game data", "episode", "summary", "bogus", "player", "players", "recipe", "recipes", "game", "kbs", "directives", "waypoints", " Facts "];
        foreach (var k in kinds) sb.Append("kind ").Append(k ?? "null").Append(" => ").Append(ShardKinds.Normalize(k) ?? "null").Append('\n');
        string?[] modes = [null, "", "merge", "MERGE", "add", "add-only", "replace", "replace-section", "replace_section", "section", "overwrite", "skip", "keep", "keep-newer", "newer", "bogus", "swap", "swap-pack", "swappack", "swap_pack", "add-mode", "addmode", "add_mode", "remove-mode", "removemode", "remove_mode", "remove", "replacesection", " Replace "];
        foreach (var m in modes) sb.Append("mode ").Append(m ?? "null").Append(" => ").Append(ShardImportOptions.ParseMode(m)).Append('\n');
        string?[] purposes = [null, "", "turn", "Brain", "chat", "say", "reflect", "summary", "raptor", "worker", "delegate", "memory", "perception", "see", "author", "patch", "line", "bogus", "task", "ask_memory", "compact", "CHAT", "Worker"];
        foreach (var p in purposes) sb.Append("purpose ").Append(p ?? "null").Append(" => ").Append(PromptPurpose.Normalize(p)).Append('\n');

        string[] directives = ["only at night", "never during the day", "always in the morning", "every evening and at dawn", "at noon", "after sunset", "before sunrise", "midnight raids", "nothing timed"];
        foreach (var d in directives) sb.Append("time ").Append(d).Append(" => ").Append(string.Join(",", DirectiveParser.TimeWords(d).Order(StringComparer.Ordinal))).Append('\n');

        string[] chats = ["", "plain", "  spaced   out  ", "§cred§r text", "tab\there", "new\nline", "emoji 😀", "a\u0000b", "/cmd", "very".PadRight(300, '!')];
        foreach (var c in chats) sb.Append("sanitize ").Append(Show(c)).Append(" => ").Append(Show(ChatText.Sanitize(c))).Append('\n');

        string[] intents = ["", "come here", "follow me", "stop", "mine 64 cobble", "get me all the iron", "chop 3 trees", "what are you doing", "maybe later", "lol", "go to the gen and mine"];
        foreach (var i in intents) sb.Append("intent ").Append(i).Append(" => ").Append(Show(IntentMatcher.Speculative(i, ["maid_chan"]))).Append('\n');

        string[] dos =
        [
            "{}", "[]", """{"action":"goto","args":{"to":"gen"}}""", """{"goto":"cobble gen","then":{"harvest_generator":{"count":64}}}""",
            """{"action":"goto","args":"gen","then":[{"action":"mine","args":"cobblestone"},{"craft":"furnace"},"stop"]}""",
            """{"action":"goto","then":[1,2,3,4,5,6,7]}""", """{"action":"say","args":{"text":"hi"},"body":"67","then":[{"action":"goto","args":"x","body":"69"}]}""",
            """{"action":"","args":{}}""", """{"follow":"kiwi","queue":true}""", """{"action":"look","args":5}""", """{"then":"stop"}""", """{"Goto":5,"then":{"MINE":"stone","body":"67"}}""", """{"action":" Mine ","args":3,"then":"look"}""",
            """{"action":7}""", """{"action":"goto","to":"gen","queue":true,"body":"69"}""",
        ];
        foreach (var d in dos)
        {
            var input = JsonDocument.Parse(d).RootElement;
            var steps = DoTool.Expand(input, out var error);
            sb.Append("do ").Append(d).Append(" => ").Append(error ?? "ok").Append(" | ").Append(string.Join(" ; ", steps.Select(s => s.Tool + " " + s.Input.GetRawText())))
              .Append(" | queue ").Append(DoTool.Queue(input)).Append('\n');
            string normalized;
            try { normalized = ((JsonElement)NormalizeInput.Invoke(null, [input])!).GetRawText(); }
            catch (System.Reflection.TargetInvocationException ex) { normalized = "throws " + ex.InnerException?.GetType().Name; }
            sb.Append("  normalized ").Append(normalized).Append('\n');
            if (input.ValueKind == JsonValueKind.Object)
                foreach (var (body, part) in DoTool.SplitByBody(input))
                    sb.Append("  split ").Append(body?.GetRawText() ?? "null").Append(' ').Append(part.GetRawText()).Append('\n');
        }

        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "misc-parsers.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
