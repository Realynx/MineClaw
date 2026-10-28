using MineClaw.Agent.Llm;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 02:45–02:49, designs #5/#6/#7: every version failed "missing 'size WxHxD'" for a "10x18x10" tower ($0.10 per
/// design, nothing produced). The usage log shows the real cause: every author call stopped on max_tokens at 1600 with no
/// text (the next round's prompt grew by only ~40 tokens): Opus 5.5 thinks even with thinking "off" (OffStyle = omit), and the
/// thinking used the whole budget. Fixed: a lenient size line, size inferred from the layers, a cut-off reply recognised as
/// such (not a parse failure), an exact-line hint, an early stop on a repeated failure, low effort and a real budget.
/// </summary>
public class DesignSizeHotfixTests
{
    private const string Tower = "palette C=cobblestone\nlayer 0-17\nCCCCCCCCCC\nC........C\nC........C\nC........C\nC........C\nC........C\nC........C\nC........C\nC........C\nCCCCCCCCCC";

    [Theory]
    [InlineData("size 10x18x10")]
    [InlineData("size: 10x18x10")]
    [InlineData("size = 10x18x10")]
    [InlineData("size WxHxD = 10x18x10")]
    [InlineData("size WxHxD: 10x18x10")]
    [InlineData("10x18x10")]
    [InlineData("size 10×18×10")]
    [InlineData("size 10 x 18 x 10")]
    [InlineData("Size: 10 × 18 × 10   # W x H x D")]
    [InlineData("dimensions 10x18x10")]
    public void Size_lines_the_author_writes_all_parse(string line)
    {
        var (bp, issues) = BlueprintParser.Parse($"blueprint mob tower\n{line}\n{Tower}");
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal((10, 18, 10), (bp.W, bp.H, bp.D));
    }

    [Fact]
    public void A_missing_size_line_is_inferred_from_the_layers()
    {
        var (bp, issues) = BlueprintParser.Parse("blueprint mob tower\n" + Tower);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal((10, 18, 10), (bp.W, bp.H, bp.D));
        Assert.Contains(issues, i => i.Code == "size-inferred");
    }

    [Fact]
    public void A_size_line_without_numbers_says_what_is_wrong()
    {
        var (_, issues) = BlueprintParser.Parse("blueprint t\nsize WxHxD\n" + Tower);
        Assert.Contains(issues, i => i.Level == IssueLevel.Error && i.Code == "size" && i.Text.Contains("numbers"));
    }

    /// <summary>Replies exactly like the live ones: max_tokens, no text (all thinking).</summary>
    private sealed class CutOffAuthor(string stop, params string[] texts) : IBlueprintAuthor
    {
        public List<IReadOnlyList<LlmMessage>> Seen { get; } = new();
        public Task<AuthorTurn> WriteAsync(IReadOnlyList<LlmMessage> conversation, CancellationToken ct)
        {
            Seen.Add(conversation.ToList());
            var t = texts[Math.Min(Seen.Count - 1, texts.Length - 1)];
            return Task.FromResult(new AuthorTurn(t, new LlmUsage(4, 1600, 1026, 410), "claude-opus-5-5", 0.033m, stop));
        }
    }

    [Fact]
    public async Task Two_cut_off_replies_stop_early_with_the_real_reason()
    {
        var w = BlueprintTests.Island();
        var author = new CutOffAuthor(StopReasons.MaxTokens, "");
        var loop = new DesignLoop(author, null, new BlueprintPreview(), new BlueprintOptions { MaxIterations = 3 }) { Site = () => BlueprintTests.Site(w) };
        var o = await loop.RunAsync(new DesignRequest("Mob drop tower, size WxHxD = 10x18x10", new BlockPos(0, 63, 0), "ground", "cobble"), CancellationToken.None);
        Assert.Equal(2, author.Seen.Count); // not 3
        Assert.StartsWith("stopped: the same failure twice", o.Stop);
        Assert.Contains("cut off at the output limit", o.Stop);
        Assert.DoesNotContain(o.Versions, v => v.EvalText.Contains("missing 'size", StringComparison.Ordinal));
        Assert.Contains("cut off at the output limit before the blueprint", author.Seen[1][^1].Text); // the retry says why
    }

    [Fact]
    public async Task A_bad_size_line_gets_the_exact_expected_line_and_an_example_then_stops_after_two()
    {
        var w = BlueprintTests.Island();
        var author = new CutOffAuthor(StopReasons.EndTurn, "```\nblueprint t\nsize WxHxD\npalette C=cobblestone\n```");
        var loop = new DesignLoop(author, null, new BlueprintPreview(), new BlueprintOptions { MaxIterations = 3 }) { Site = () => BlueprintTests.Site(w) };
        var o = await loop.RunAsync(new DesignRequest("tower", new BlockPos(0, 63, 0), "ground", "cobble"), CancellationToken.None);
        Assert.Equal(2, author.Seen.Count);
        var hint = author.Seen[1][^1].Text;
        Assert.Contains("`size WxHxD` with numbers", hint);
        Assert.Contains("size 3x2x3", hint);
        Assert.StartsWith("stopped: the same failure twice", o.Stop);
    }

    [Fact]
    public void The_author_runs_at_low_effort_with_room_for_thinking_and_the_blueprint()
    {
        var o = new BlueprintOptions();
        Assert.True(o.AuthorMaxTokens >= 6000, "Opus 5.5 can't turn thinking off: 1600 left no room for the blueprint");
        Assert.Equal("low", o.AuthorEffort);
        Assert.Equal(ModelProfiles.ThinkingOffStyle.Omit, ModelProfiles.OffStyle("claude-opus-5-5")); // why the budget matters
    }
}
