using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Telemetry;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="LatencyReport.FromLog"/> (WP06 refactor) over a synthetic log: queued and merged player lines, other bots,
/// stale lines, worker calls before any tool, body tools vs other tools, stop, lines with no turn, and a midnight wrap.
/// </summary>
public class LatencyLogGoldenTests
{
    private const string Expected = "0996485AB766C0DCCE4BACB8C46BA5F813F0D4EBDDC7EAF646C74EC3BF365B4F";

    private static readonly string[] Log =
    [
        "garbage line without a time",
        "06:47:03.718 INF App: chat: <PoofimaFox> grab some cobble stone and come here",
        "06:47:04.000 INF App: chat: <poofimafox> and some sand",
        "06:47:04.100 INF App: chat: <kiwi> hi maid",
        "06:47:05.220 DBG Agent.BotAgent: triage → Main (config)",
        "06:47:07.132 INF Agent.BotAgent: call opus (claude-opus-5-5) round 0: in 4 out 51",
        "06:47:07.179 DBG Agent.BotAgent: tool inventory → ok: held: nothing",
        "06:47:08.842 INF Agent.BotAgent: call opus (claude-opus-5-5) round 1: in 2 out 51",
        "06:47:09.000 DBG Agent.BotAgent: tool goto → ok: arrived",
        "06:47:10.339 INF App: chat: <maid_chan> got it foxy",
        "06:47:10.500 INF App: chat: <robinchan> lol",
        "06:47:11.339 INF App: chat: <maid_chan> omw",
        "06:47:11.658 INF Agent.BotAgent: turn: tier Main calls 2",
        "06:48:00.000 INF App: chat: <robinchan> hi",
        "06:48:01.000 INF App: chat: <kiwi> maid help me",
        "06:48:30.000 DBG Agent.BotAgent: triage → Main (config)",
        "06:48:31.000 INF Agent.BotAgent: turn: tier Main calls 1",
        "06:49:00.000 INF App: chat: <kiwi> maid mine the gen",
        "06:49:01.000 DBG Agent.BotAgent: triage → Main (config)",
        "06:49:03.000 INF Agent.Worker: worker call 0: claude-sonnet-5-5",
        "06:49:04.000 INF Agent.Worker: worker call 1: claude-sonnet-5-5",
        "06:49:05.000 DBG Agent.BotAgent: tool delegate → ok: ok: mined",
        "06:49:06.000 INF App: chat: <maid_chan> done!",
        "06:49:07.000 INF Agent.BotAgent: turn: tier Main calls 1",
        "06:50:00.000 INF App: chat: <fox> stop",
        "06:50:00.500 DBG Agent.BotAgent: triage → Main (config)",
        "06:50:01.000 DBG Agent.BotAgent: tool stop → ok: stopped",
        "06:50:02.000 INF Agent.BotAgent: turn: tier Main calls 1",
        "06:50:03.000 INF Agent.BotAgent: turn: tier Main calls 1",
        "23:59:58.000 INF App: chat: <fox> what are you doing",
        "00:00:01.000 DBG Agent.BotAgent: triage → Main (config)",
        "00:00:02.000 DBG Agent.BotAgent: tool look → error: dark",
        "00:00:03.000 INF Agent.BotAgent: call opus (claude-opus-5-5) round 1: in 2 out 9",
        "00:00:04.000 INF App: chat: <maid_chan> chilling",
        "00:00:05.000 INF Agent.BotAgent: turn: tier Main calls 1",
        "00:00:06.000 INF App: chat: <fox> hello",
        "00:00:07.000 INF Agent.BotAgent: turn: tier Main calls 1",
    ];

    [Fact]
    public void Log_reconstruction_stays_the_same()
    {
        var sb = new StringBuilder();
        foreach (var (others, names, queue) in new[] { ((string[]?)["robinchan"], (string[]?)["maid"], 15.0), (null, null, 0.5), (["robinchan"], ["maid"], 100) })
        {
            var recs = LatencyReport.FromLog(Log, "maid_chan", others, names, queue, new DateOnly(2026, 10, 26));
            foreach (var r in recs) sb.Append(JsonSerializer.Serialize(r)).Append('\n');
            sb.Append("--\n");
        }
        var dump = sb.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "latency-log.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
