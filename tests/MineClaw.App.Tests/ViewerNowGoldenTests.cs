using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="LiveViewerData.NowAsync"/> (WP07): the viewer's <c>now</c> JSON for main (by name and as "main"), a hive clone, its
/// subhive bodies (streamed, one spawning), telemetry sources (online with a thought, tools, a held item; online with nothing; dead;
/// offline) and an unknown bot. Timestamps the rig takes from the wall clock are masked.
/// </summary>
public class ViewerNowGoldenTests
{
    private static readonly Regex Stamp = new(@"""\d{4}-\d\d-\d\dT[^""]*""", RegexOptions.Compiled);

    private sealed class Source(BotSnapshot snap) : IBotTelemetrySource
    {
        public string Name => snap.Name;
        public BotSnapshot Snapshot(bool full) => snap;
        public event Action<PromptUsageRecord>? UsageRecorded { add { } remove { } }
        public Task<bool> SendAsync(string line, CancellationToken ct) => Task.FromResult(false);
    }

    private static readonly DateTimeOffset At = new(2026, 11, 4, 1, 2, 3, TimeSpan.Zero);

    private static IReadOnlyList<IBotTelemetrySource> Sources() =>
    [
        new Source(new BotSnapshot
        {
            Name = "maid_chan_77", Status = BotStatus.Online, Pos = new BotPos(-3.5, 64, 10.2), Hp = 18, Food = 9, Dimension = -1, Task = "mine",
            Header = "pos -4,64,10 hp 18 food 9 held stone_pickaxe x1 night", Brain = "awake",
            Thought = new ThoughtSnapshot
            {
                UpdatedAt = At, Output = "on it", Thinking = "",
                Tools = [new ThoughtToolCall(At, "goto", "x=1", "ok"), new ThoughtToolCall(At.AddSeconds(2), "dig", "x=2")],
            },
        }),
        new Source(new BotSnapshot { Name = "maid_chan_78", Status = BotStatus.Online, Thought = new ThoughtSnapshot { Thinking = "hmm" } }),
        new Source(new BotSnapshot { Name = "maid_chan_79", Status = BotStatus.Dead, Pos = new BotPos(1, 2, 3), Header = "no held item here", Thought = new ThoughtSnapshot() }),
        new Source(BotSnapshot.Offline("maid_chan_80")),
    ];

    [Fact]
    public async Task Now_matches_the_golden_dump()
    {
        var (h, c, bodies, _) = HiveBodyListTests.Live();
        using var _h = h;
        var scripts = new LiveScriptData(h, c.Username, () => null) { Bodies = bodies };
        var controller = new SupervisorControllerData(h, c.Username, null, () => null, () => null);
        var sb = new StringBuilder();
        foreach (var withBodies in new[] { true, false })
        {
            var viewer = new LiveViewerData(h, c.Username, Sources(), () => null, controller, scripts) { Bodies = withBodies ? bodies : null };
            foreach (var bot in new[] { "main", c.Username, HiveBodyListTests.Hive, "maid_chan_102", "maid_chan_114", "maid_chan_124", "maid_chan_131",
                "maid_chan_77", "maid_chan_78", "maid_chan_79", "maid_chan_80", "nobody" })
            {
                var now = await viewer.NowAsync(bot, default);
                sb.Append(withBodies ? "bodies " : "plain ").Append(bot).Append(" -> ")
                    .Append(Stamp.Replace(JsonSerializer.Serialize(now, SupervisorJson.Options), "\"<t>\"")).Append('\n');
            }
        }
        RouteGolden.Check("viewer-now", ViewerNowGoldenData.Expected, sb.ToString().Replace(c.Username, "<main>"));
    }
}
