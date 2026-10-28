using MineClaw.Agent.Brainstem;
using MineClaw.Agent.Telemetry;
using Xunit.Abstractions;

namespace MineClaw.Agent.Tests;

/// <summary>
/// The reactive-character baseline (docs/design/reactive-character.md): chat → first visible reaction / full answer, p50/p95 by
/// kind of message, reconstructed from the live log snapshots (data/alice-logs*, logs/live*) that predate latency-*.jsonl.
/// The numbers print to the test output; the asserts only pin the parser and the classifier.
/// </summary>
public class LatencyBaselineTests(ITestOutputHelper output)
{
    private static string? RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MineClaw.sln"))) d = d.Parent;
        return d?.FullName;
    }

    private static readonly string[] OtherBots = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337", "robinchan"];

    [Fact]
    public void Baseline_from_live_logs()
    {
        var root = RepoRoot();
        if (root == null) return;
        var all = new List<LatencyRecord>();
        foreach (var dir in new[] { "data/alice-logs-s3", "logs/live2", "logs/live-hive", "logs" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var log in Directory.GetFiles(full, "mineclaw-*.log", SearchOption.AllDirectories))
            {
                if (dir == "logs" && Path.GetDirectoryName(log) != full) continue; // live2/live-hive are read on their own
                var parent = Path.GetFileName(Path.GetDirectoryName(log)!);
                var bot = parent.StartsWith("maid_chan_", StringComparison.Ordinal) ? parent : "maid_chan";
                var recs = LatencyReport.FromLog(File.ReadLines(log), bot, OtherBots, ["maid", "maid chan", "claudechan"]);
                output.WriteLine($"{Path.GetRelativePath(root, log)}: {recs.Count} chats");
                all.AddRange(recs);
            }
        }
        if (all.Count == 0) return; // snapshots not present (CI)
        output.WriteLine(LatencyReport.Markdown(all));
        output.WriteLine("by reaction: " + string.Join(", ", all.GroupBy(r => r.ReactionBy ?? "none").Select(g => $"{g.Key} {g.Count()}")));
        Assert.All(all, r => Assert.Contains(r.Category, ChatIntent.Categories));
    }

    [Fact]
    public void Log_reconstruction_measures_line_and_action()
    {
        string[] log =
        [
            "06:47:03.718 INF App: chat: <PoofimaFox> grab some cobble stone and come here",
            "06:47:05.220 DBG Agent.BotAgent: triage → Main (config)",
            "06:47:07.132 INF Agent.BotAgent: call opus (claude-opus-5-5) round 0: in 4 out 51",
            "06:47:07.179 DBG Agent.BotAgent: tool inventory → ok: held: nothing",
            "06:47:08.842 INF Agent.BotAgent: call opus (claude-opus-5-5) round 1: in 2 out 51",
            "06:47:09.000 DBG Agent.BotAgent: tool goto → ok: arrived",
            "06:47:10.339 INF App: chat: <maid_chan> got it foxy",
            "06:47:10.658 INF Agent.BotAgent: turn: tier Main calls 2",
            "06:48:00.000 INF App: chat: <robinchan> hi",
        ];
        var r = Assert.Single(LatencyReport.FromLog(log, "maid_chan", OtherBots));
        Assert.Equal(ChatIntent.Command, r.Category);
        Assert.Equal(5124, r.FirstActionMs);  // the goto started when round 1 ended
        Assert.Equal(5124, r.ReactionMs);
        Assert.Equal("action", r.ReactionBy);
        Assert.Equal(6621, r.FullMs);         // the line came after the action
    }

    [Theory]
    [InlineData("help!", "help")]
    [InlineData("maid help me a zombie is on me", "help")]
    [InlineData("come here", "command")]
    [InlineData("can you grab some cobble?", "command")]
    [InlineData("go chop wood", "command")]
    [InlineData("where did you put the wheat?", "question")]
    [InlineData("what are you doing", "question")]
    [InlineData("hi maid", "greeting")]
    [InlineData("gn everyone", "greeting")]
    [InlineData("thanks!", "banter")]
    [InlineData("lol", "banter")]
    [InlineData("you're so dumb", "banter")]
    public void Categories(string line, string category) =>
        Assert.Equal(category, ChatIntent.Classify(line, ["maid"]).Category);

    [Fact]
    public void Percentiles_nearest_rank()
    {
        long?[] v = [100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, null];
        Assert.Equal(500, LatencyReport.P(v, 50));
        Assert.Equal(1000, LatencyReport.P(v, 95));
    }
}
