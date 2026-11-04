using System.Globalization;
using System.Text;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Hive.Hierarchy;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="HiveBodyList.Rows"/> (WP07; it feeds <c>/api/bots</c>, <c>/api/bodies</c>, <c>/api/world/entities</c> and
/// <c>/api/viewer/now</c>): main with and without a script, main's own bodies (main and duplicates skipped), hive clones in the hub
/// with and without a slot (script and paused-script brains), connected slots without a position, starting slots, tree-only
/// slots, duplicate slots, the tree's nodes not in game (script, subhive, hive, root and hive owners, no Since), throwing sources
/// and no tree. Every field of every row is dumped.
/// </summary>
public class HiveBodyListGoldenTests
{
    private const string Main = "maid_chan";
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static HiveTree Tree()
    {
        var t = new HiveTree(Main);
        t.Put(new HiveNode("maid_chan_109", CloneKind.Hive, BrainKind.Opus, HiveTree.Root) { State = "running", Task = "chop the forest", Since = T0.AddMinutes(-5) });
        t.Put(new HiveNode("maid_chan_102", CloneKind.Subhive, null, "maid_chan_109") { State = "running" });
        t.Put(new HiveNode("maid_chan_131", CloneKind.Subhive, null, "maid_chan_109") { State = "spawning" });
        t.Put(new HiveNode("maid_chan_140", CloneKind.Script, null, HiveTree.Root) { State = "running", Script = "cobble_loop", Task = "loop", Since = T0.AddMinutes(-1) });
        t.Put(new HiveNode("maid_chan_141", CloneKind.Script, null, HiveTree.Root) { State = "spawning" });
        t.Put(new HiveNode("maid_chan_150", CloneKind.Hive, BrainKind.Opus, HiveTree.Root) { State = "spawning" });
        t.Put(new HiveNode("maid_chan_151", CloneKind.Hive, BrainKind.Opus, HiveTree.Root) { State = "releasing", Task = "dig" });
        t.Put(new HiveNode("maid_chan_160", CloneKind.Subhive, null, "maid_chan_150") { State = "spawning", Task = "wait", Since = T0.AddSeconds(-3) });
        t.Put(new HiveNode("Odd_Name", CloneKind.Subhive, null, HiveTree.Root) { State = "running" });
        t.Put(new HiveNode("maid_chan_170", CloneKind.Hive, BrainKind.Opus, HiveTree.Root) { State = "running" });
        return t;
    }

    private static BodyBrief Brief(string name, double x, bool playing = true, BodyScriptBrief? script = null, string? task = null) =>
        new() { Name = name, Dimension = name.EndsWith('4') ? -1 : 0, X = x, Y = 64.25, Z = -x, Hp = 17, Food = 12, Task = task, Playing = playing, At = T0.AddSeconds(-x), Script = script };

    private static BotSnapshot Snapshot(string name, BotPos? pos, string? brain, string? task, params BodyBrief[] bodies) => new()
    {
        Name = name, Status = BotStatus.Online, At = T0.AddSeconds(-2), Pos = pos, Dimension = 1, Hp = 15, Food = 9, Task = task, Brain = brain,
        Bodies = bodies.Length == 0 ? null : bodies,
    };

    private static IReadOnlyList<HiveBotState> Hub() =>
    [
        new HiveBotState("maid_chan_109", 0, 20.123, 64, 20.987, "chop the forest", 20, 19, T0, Brain: "asleep (script chop_crew 34%)"),
        new HiveBotState(Main, 0, 0.555, 64.004, 0.5, "idle", 20, 20, T0, IsMain: true),
        new HiveBotState("maid_chan_120", -1, -5.5, 70, 3.333, null, 8, 4, T0.AddSeconds(-9), Brain: "asleep (script cobble_loop paused 10%)"),
        new HiveBotState("maid_chan_121", 0, 1, 2, 3, "build", 20, 20, T0, Brain: "awake"),
        new HiveBotState("maid_chan_121", 0, 9, 9, 9, "dup", 20, 20, T0),
        new HiveBotState("maid_chan_170", 0, 7, 8, 9, "fight", 20, 20, T0, Brain: null),
    ];

    private static IEnumerable<SlotView> Slots() =>
    [
        new SlotView("maid_chan_109", true, Snapshot("maid_chan_109", new BotPos(20, 64, 20), "awake", "chop",
            Brief("maid_chan_102", 21.5, script: new BodyScriptBrief("chop_crew", "running", 14, false, 3), task: "script chop_crew"),
            Brief("maid_chan_114", 25), Brief("maid_chan_124", 30, playing: false), Brief(Main, 1))),
        new SlotView("maid_chan_120", false, Snapshot("maid_chan_120", null, null, null, Brief("maid_chan_125", 2))), // not connected: no bodies
        new SlotView("maid_chan_130", true, Snapshot("maid_chan_130", null, "asleep (script fish paused", "fishing")), // connected, no position yet
        new SlotView("maid_chan_131", false, null), // tree only (spawning)
        new SlotView("maid_chan_132", false, null, "build a hut", Starting: true),
        new SlotView("maid_chan_133", false, null, "idle slot"), // not listed
        new SlotView("maid_chan_134", true, Snapshot("maid_chan_134", new BotPos(1.5, 2.5, 3.5), "awake", null, Brief("maid_chan_135", 4))),
        new SlotView("maid_chan_134", true, null), // a duplicate slot name
        new SlotView("maid_chan_150", false, null, null, Starting: true),
        new SlotView("maid_chan_151", false, null, null),
    ];

    private static IEnumerable<BodyBrief> Local() => [Brief(Main, 0.1), Brief("maid_chan_180", 11), Brief("maid_chan_181", 12, playing: false), Brief("maid_chan_180", 13)];

    private static string Row(BodyRow r) => string.Create(CultureInfo.InvariantCulture,
        $"{r.Name}|{r.Short}|{r.Kind}|{r.Owner ?? "null"}|main={r.IsMain}|online={r.Online}|dim={r.Dim}|{r.X:R},{r.Y:R},{r.Z:R}|hp={r.Hp}|food={r.Food}|" +
        $"job={r.Job ?? "null"}|brain={r.Brain ?? "null"}|script={(r.Script is { } s ? $"{s.Name}/{s.Status}/{s.Line}/{s.Paused}/{s.Version?.ToString(CultureInfo.InvariantCulture) ?? "null"}" : "null")}|" +
        $"at={r.At:O}|pos={r.HasPos}|state={r.State ?? "null"}");

    internal static string Dump()
    {
        var sb = new StringBuilder();
        var time = new FixedTime(T0);
        var cases = new (string Label, HiveBodyList List)[]
        {
            ("full", new HiveBodyList(Main, Hub, Local, Slots, Tree, () => new BodyScriptBrief("guard", "running", 3, false, null), time) { Ttl = TimeSpan.Zero }),
            ("no tree, no main script", new HiveBodyList(Main, Hub, Local, Slots, null, null, time) { Ttl = TimeSpan.Zero }),
            ("everything throws", new HiveBodyList(Main, () => throw new InvalidOperationException("hub"), () => throw new InvalidOperationException("local"),
                () => throw new InvalidOperationException("slots"), () => throw new InvalidOperationException("tree"), () => throw new InvalidOperationException("script"), time) { Ttl = TimeSpan.Zero }),
            ("main script throws", new HiveBodyList(Main, Hub, null, null, Tree, () => throw new InvalidOperationException("script"), time) { Ttl = TimeSpan.Zero }),
            ("no main in the hub", new HiveBodyList(Main, () => Hub().Where(b => !b.IsMain).ToList(), () => null, Slots, Tree, null, time) { Ttl = TimeSpan.Zero }),
            ("tree only", new HiveBodyList(Main, () => [], null, null, Tree, null, time) { Ttl = TimeSpan.Zero }),
        };
        foreach (var (label, list) in cases)
        {
            sb.Append("== ").Append(label).Append('\n');
            foreach (var r in list.Rows()) sb.Append(Row(r)).Append('\n');
        }
        return sb.ToString();
    }

    [Fact]
    public void Rows_match_the_golden_dump() => RouteGolden.Check("hive-body-list", HiveBodyListGoldenData.Expected, Dump());
}
