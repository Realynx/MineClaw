using MineClaw.Bot.WorldIndex;
using MineClaw.Protocol.Recording;
using MineClaw.Replay;

namespace MineClaw.Bot.Tests;

/// <summary>Offline replay of committed fixture recordings (tests/fixtures/recordings). Real session recordings go next to
/// the synthetic one after the live calibration; each needs a .truth.txt and gets scored here.</summary>
public class ReplayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    internal static string Fixtures
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "tests", "fixtures", "recordings");
                if (Directory.Exists(p)) return p;
            }
            throw new DirectoryNotFoundException("tests/fixtures/recordings not found");
        }
    }

    private static string Synthetic => Path.Combine(Fixtures, "synthetic_calibration.mcrec");

    [Fact]
    public void Committed_synthetic_fixture_matches_its_generator()
    {
        Assert.True(File.Exists(Synthetic), "regenerate: dotnet run --project tools/Replay -- --synthetic tests/fixtures/recordings/synthetic_calibration.mcrec");
        Assert.Equal(SyntheticRecording.Build(), File.ReadAllBytes(Synthetic));
    }

    [Fact]
    public void Replay_is_deterministic()
    {
        var a = ReplayHarness.Run(Synthetic).ToJson();
        var b = ReplayHarness.Run(Synthetic).ToJson();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Synthetic_session_detects_every_action_and_keeps_the_bots_dig_its_own()
    {
        var r = ReplayHarness.Run(Synthetic);
        output.WriteLine(r.RenderTimeline());
        Assert.Equal("maid_chan", r.Self);
        var act = r.Timeline.Where(e => e.Source == "activity").ToList();
        Assert.Equal(5, act.Where(e => e.Player == "poofimafox" && e.Kind == ActivityKinds.Broke).Sum(e => e.Count));
        Assert.Equal(3, act.Where(e => e.Player == "kiwi" && e.Kind == ActivityKinds.Placed).Sum(e => e.Count));
        Assert.Contains(act, e => e.Player == "poofimafox" && e.Kind == ActivityKinds.WroteSign && e.Text.Contains("fox was here"));
        Assert.Contains(act, e => e.Player == "kiwi" && e.Kind == ActivityKinds.Died);
        // The log the bot dug while Robin faced it and swung: the bot's, never hers.
        Assert.Contains(r.Timeline, e => e.Source == "self" && e.Kind == "dug" && e.Text.StartsWith("log at 1,64,1"));
        Assert.DoesNotContain(act, e => e.Kind == ActivityKinds.Broke && e.BlockId == MineClaw.Data.Blocks.Log);
        Assert.True(r.SelfEditsSkipped >= 1);
        // Robin building at the edge doesn't alarm; her fall/death does.
        var wakes = r.Timeline.Where(e => e.Source == "wake" && e.Priority is "normal" or "high" or "critical").ToList();
        Assert.All(wakes, w => Assert.True(w.OffsetMs >= 38_000, $"early alarm: {w.Format()}"));
        Assert.Contains(wakes, w => w.Player == "kiwi");
    }

    [Fact]
    public void Calibration_scores_the_synthetic_truth_perfectly()
    {
        var r = ReplayHarness.Run(Synthetic);
        var truthFile = Path.ChangeExtension(Synthetic, ".truth.txt");
        var truth = Calibration.ParseTruth(File.ReadAllText(truthFile));
        var score = Calibration.Score(r, truth);
        output.WriteLine(score.Render());
        Assert.Empty(score.Missed);
        Assert.All(score.Kinds, k => { Assert.Equal(1.0, k.Recall); Assert.Equal(1.0, k.Precision); });
        Assert.All(score.Kinds, k => Assert.True(k.MaxAbsOffsetMs <= 2000, $"{k.Kind} off by {k.MaxAbsOffsetMs} ms"));
    }

    [Fact]
    public void Truth_parser_reads_counts_blocks_and_comments()
    {
        var t = Calibration.ParseTruth("# header\n01:02.5 kiwi placed cobblestone x3  # at the edge\n00:10 poofimafox chat\n");
        Assert.Equal(2, t.Count);
        Assert.Equal(new TruthEntry(10_000, "poofimafox", "chat", null, 1, 3), t[0]);
        Assert.Equal(62_500, t[1].OffsetMs);
        Assert.Equal(("cobblestone", 3), (t[1].What, t[1].Count));
        Assert.Throws<FormatException>(() => Calibration.ParseTruth("soon kiwi broke"));
    }

    [Fact]
    public void Every_committed_recording_with_a_truth_file_replays()
    {
        foreach (var f in Directory.GetFiles(Fixtures, "*.mcrec"))
        {
            var rec = PacketRecording.Load(f);
            var r = ReplayHarness.Run(rec);
            output.WriteLine($"{Path.GetFileName(f)}: {r.Frames} frames, {r.Timeline.Count} events");
            var truth = Path.ChangeExtension(f, ".truth.txt");
            if (File.Exists(truth)) output.WriteLine(Calibration.Score(r, Calibration.ParseTruth(File.ReadAllText(truth))).Render());
        }
    }
}
