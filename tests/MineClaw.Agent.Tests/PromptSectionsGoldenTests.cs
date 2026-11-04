using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;

namespace MineClaw.Agent.Tests;

/// <summary>Pins <see cref="PromptSections.Estimate"/> (WP06 refactor): sections and round for anchored, cut and turn-less requests.</summary>
public class PromptSectionsGoldenTests
{
    private const string Expected = "365ECD9E513506E2FBFC63E2186B050F4CEEAEC794CF8AB188D149AE352C3CC3";

    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static LlmToolUse Use(string id) => new(id, "look", JsonSerializer.SerializeToElement(new { at = "here" }));

    [Fact]
    public void Section_estimates_stay_the_same()
    {
        var turnText = "[since] kiwi left\n[self] cheerful, loves cobble\n[memory] the gen is west\n[chat] fox: clawd how much cobble?";
        var turn = new TurnInput(T0, ModelTier.Main, "pos 1,64,2", "[since] kiwi left", "[memory] the gen is west", "[chat] fox: clawd how much cobble?",
            turnText, "[self] cheerful, loves cobble");
        var tools = new List<LlmToolDefinition> { new("look", "Look around.", JsonSerializer.SerializeToElement(new { type = "object" })) };
        LlmSystemBlock[][] systems =
        [
            [new("You are a bot.\n# World core rules\nno void\n# World knowledge\nmore")],
            [new("You are a bot. no knowledge"), new("I am Clawd.\n\nlegacy self profile here"), new("third block")],
            [new("You are a bot.\n# World knowledge\nk"), new("Only identity, one paragraph")],
            [],
        ];
        LlmMessage[][] histories =
        [
            [LlmMessage.User("old turn"), LlmMessage.Assistant("old reply"),
             new LlmMessage(LlmRole.User, [new LlmText("prefix block"), new LlmText(turnText)]),
             new LlmMessage(LlmRole.Assistant, [new LlmText("checking"), Use("t1")]),
             new LlmMessage(LlmRole.User, [new LlmToolResult("t1", "12 cobble", false)]),
             LlmMessage.Assistant("12 cobble")],
            [LlmMessage.User("old turn"), LlmMessage.Assistant("old reply"), LlmMessage.User("cut turn text"),
             new LlmMessage(LlmRole.Assistant, [Use("t2")]), new LlmMessage(LlmRole.User, [new LlmToolResult("t2", "ok", false)]),
             LlmMessage.User("[new events] more chat"), LlmMessage.Assistant("ok")],
            [LlmMessage.Assistant("leading assistant"), LlmMessage.User("task: mine"), LlmMessage.Assistant("a"), LlmMessage.User("b"), LlmMessage.Assistant("c")],
            [],
        ];
        var sb = new StringBuilder();
        foreach (var sys in systems)
            foreach (var msgs in histories)
                foreach (var t in new[] { turn, null })
                {
                    var r = new LlmRequest { Tier = ModelTier.Main, System = sys, Tools = tools, Messages = msgs, Purpose = "turn" };
                    var (s, round) = PromptSections.Estimate(r, t);
                    sb.Append(round).Append(' ').Append(string.Join(",", s.Select(kv => kv.Key + "=" + kv.Value))).Append('\n');
                }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "prompt-sections.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
