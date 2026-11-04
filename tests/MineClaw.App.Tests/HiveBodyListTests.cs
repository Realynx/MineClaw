using System.Text.Json;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Supervisor;
using MineClaw.Bot;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Tests;
using MineClaw.Hive.Hierarchy;

namespace MineClaw.App.Tests;

/// <summary>
/// Every body the process controls is in the supervisor's lists: main, a hive clone, that hive's subhive bodies (streamed with
/// its snapshot) and a body still spawning (the ownership tree only), each with its kind, owner, vitals, job and script.
/// </summary>
public class HiveBodyListTests
{
    internal const string Hive = "maid_chan_109";

    internal sealed class FakeHive : IHiveVision
    {
        public List<HiveBotState> Bots { get; } = new();
        public IReadOnlyList<HiveBotState> Clones() => Bots;
        public IReadOnlyList<HiveLease> Leases() => [];
        public Task<HiveEyePose?> ViewAsync(string clone, CancellationToken ct) => Task.FromResult<HiveEyePose?>(null);
    }

    private static HiveTree Tree(string main)
    {
        var t = new HiveTree(main);
        t.Put(new HiveNode(Hive, CloneKind.Hive, BrainKind.Opus, HiveTree.Root) { State = "running", Task = "chop the forest" });
        foreach (var n in new[] { "102", "114", "124" }) t.Put(new HiveNode("maid_chan_" + n, CloneKind.Subhive, null, Hive) { State = "running" });
        t.Put(new HiveNode("maid_chan_131", CloneKind.Subhive, null, Hive) { State = "spawning" });
        return t;
    }

    private static BotSnapshot HiveSnapshot(DateTimeOffset now) => new()
    {
        Name = Hive, Status = BotStatus.Online, At = now, Pos = new BotPos(20, 64, 20), Hp = 20, Food = 19, Task = "chop the forest", Brain = "awake",
        Bodies =
        [
            new BodyBrief { Name = "maid_chan_102", X = 21.5, Y = 64, Z = 18.5, Hp = 17, Food = 12, Task = "script chop_crew", Playing = true, At = now,
                Script = new BodyScriptBrief("chop_crew", "running", 14, false, 3) },
            new BodyBrief { Name = "maid_chan_114", X = 25, Y = 65, Z = 19, Hp = 20, Food = 20, Task = "chop", Playing = true, At = now },
            new BodyBrief { Name = "maid_chan_124", X = 30, Y = 63, Z = 22, Hp = 6, Food = 3, Playing = true, At = now },
        ],
    };

    private static HiveBodyList Bodies(Func<IReadOnlyList<HiveBotState>> hiveBots, HiveTree tree, DateTimeOffset now) =>
        new(tree.Main, hiveBots, slots: () => [new SlotView(Hive, true, HiveSnapshot(now))], tree: () => tree) { Ttl = TimeSpan.Zero };

    [Fact]
    public void Rows_list_main_the_hive_its_subhive_bodies_and_a_spawning_one_with_kind_and_owner()
    {
        var now = DateTimeOffset.UtcNow;
        var tree = Tree("maid_chan");
        var bodies = Bodies(() =>
        [
            new HiveBotState("maid_chan", 0, 0.5, 64, 0.5, "idle", 20, 20, now, IsMain: true),
            new HiveBotState(Hive, 0, 20, 64, 20, "chop the forest", 20, 19, now, Brain: "awake"),
        ], tree, now);

        var rows = bodies.Rows();
        Assert.Equal(["maid_chan", Hive, "maid_chan_102", "maid_chan_114", "maid_chan_124", "maid_chan_131"], rows.Select(r => r.Name));
        Assert.Equal(("main", (string?)null, true), (rows[0].Kind, rows[0].Owner, rows[0].IsMain));
        Assert.Equal(("hive", "maid_chan", "awake"), (rows[1].Kind, rows[1].Owner, rows[1].Brain));
        var b102 = rows[2];
        Assert.Equal(("102", "subhive", Hive, true, "driven by 109"), (b102.Short, b102.Kind, b102.Owner, b102.Online, b102.Brain));
        Assert.Equal((17, 12, 21.5, "script chop_crew"), (b102.Hp, b102.Food, b102.X, b102.Job));
        Assert.Equal(new BodyScriptBrief("chop_crew", "running", 14, false, 3), b102.Script);
        var spawning = rows[5];
        Assert.Equal(("subhive", Hive, false, false, "spawning"), (spawning.Kind, spawning.Owner, spawning.Online, spawning.HasPos, spawning.State));
        Assert.Equal("maid_chan_114", bodies.Find("114")!.Name);
        Assert.True(bodies.Find("main")!.IsMain);
        Assert.Null(bodies.Find("999"));
    }

    [Fact]
    public void A_hive_briefs_its_own_bodies_for_its_snapshot_main_left_out()
    {
        var tools = new MineClaw.Agent.Tools.ToolRegistry(400);
        var events = new MineClaw.Agent.Events.EventQueue();
        MineClaw.Bot.Bodies.Body Make(string name, bool main, bool playing)
        {
            var fake = new FakeGameClient { Username = name, IsPlaying = playing, CurrentAction = "walking" };
            return new MineClaw.Bot.Bodies.Body(name, main, fake, fake, new MineClaw.Agent.Tools.ActivityManager(tools, events));
        }
        var at = DateTimeOffset.UnixEpoch;
        var briefs = BodyBriefs.Of([Make(Hive, true, true), Make("maid_chan_102", false, true), Make("maid_chan_114", false, false)],
            new ManualClock(at));
        Assert.NotNull(briefs);
        Assert.Equal(["maid_chan_102", "maid_chan_114"], briefs!.Select(b => b.Name));
        Assert.Equal((0.5, 65.0, 0.5, 20, 20, "walking", true), (briefs[0].X, briefs[0].Y, briefs[0].Z, briefs[0].Hp, briefs[0].Food, briefs[0].Task, briefs[0].Playing));
        Assert.False(briefs[1].Playing);
        Assert.Null(briefs[0].Script);
        Assert.Equal(at, briefs[0].At);
        Assert.Null(BodyBriefs.Of(null));
    }

    [Fact]
    public void Without_a_tree_the_hive_owns_the_bodies_it_streams_and_main_owns_the_hive()
    {
        var now = DateTimeOffset.UtcNow;
        var bodies = new HiveBodyList("maid_chan", () => [new HiveBotState("maid_chan", 0, 0, 64, 0, null, 20, 20, now, IsMain: true)],
            slots: () => [new SlotView(Hive, true, HiveSnapshot(now)), new SlotView("maid_chan_150", false, null, "build a hut", Starting: true)]);
        var rows = bodies.Rows();
        // 109 is connected without being in the hub's list (no position yet in its vision): listed offline, then its bodies
        Assert.Equal(["maid_chan", Hive, "maid_chan_102", "maid_chan_114", "maid_chan_124", "maid_chan_150"], rows.Select(r => r.Name));
        Assert.All(rows.Skip(2).Take(3), r => Assert.Equal(("subhive", Hive), (r.Kind, r.Owner)));
        Assert.Equal(("hive", "maid_chan", false, "build a hut"), (rows[5].Kind, rows[5].Owner, rows[5].Online, rows[5].Job));
    }

    internal static (BotHost Host, FakeGameClient Client, HiveBodyList Bodies, FakeHive Hive) Live()
    {
        var o = TestBot.Options();
        o.Llm.Perception.Mode = "direct";
        var bot = new BotOptions();
        bot.Vision.Textures = false;
        var c = new FakeGameClient();
        var h = BotHost.Create(c, o, bot, new NoLlm(), persist: false);
        var hive = new FakeHive();
        var hub = h.Vision!.Hub!;
        hub.Hive = hive;
        var now = DateTimeOffset.UtcNow;
        hive.Bots.Add(new HiveBotState(Hive, 0, 20, 64, 20, "chop the forest", 20, 19, now, Brain: "awake"));
        return (h, c, Bodies(hub.HiveBots, Tree(c.Username), now), hive);
    }

    [Fact]
    public void Bots_and_status_list_every_body_with_kind_owner_and_script()
    {
        var (h, c, bodies, _) = Live();
        using var _h = h;
        var data = new LiveSupervisorData(h.Vision!.Hub!, () => c.World, c.Username, [], () => null, new SupervisorFeed(), new SupervisorOptions())
        {
            Bodies = bodies,
        };
        var tiles = data.Bots();
        Assert.Equal(6, tiles.Count);
        var t102 = tiles.Single(t => t.Short == "102");
        Assert.Equal(("subhive", Hive, true, "chop_crew"), (t102.Kind, t102.Owner, t102.Online, t102.Script!.Name));
        var t131 = tiles.Single(t => t.Short == "131");
        Assert.Equal((false, "spawning"), (t131.Online, t131.State));
        var json = JsonSerializer.Serialize(tiles, SupervisorJson.Options);
        Assert.Contains("\"kind\":\"subhive\",\"owner\":\"maid_chan_109\",\"script\":{\"name\":\"chop_crew\",\"status\":\"running\",\"line\":14,\"paused\":false,\"version\":3},\"online\":true", json);

        using var status = JsonDocument.Parse(JsonSerializer.Serialize(data.Status(null), SupervisorJson.Options));
        var rows = status.RootElement.GetProperty("bots").EnumerateArray().ToList();
        Assert.Equal(6, rows.Count);
        Assert.Equal(c.Username, rows[0].GetProperty("name").GetString());
        Assert.Equal("main", rows[0].GetProperty("short").GetString());
        Assert.Equal("main", rows[0].GetProperty("kind").GetString());
        var s102 = rows.Single(r => r.GetProperty("name").GetString() == "maid_chan_102");
        Assert.Equal("online", s102.GetProperty("status").GetString());
        Assert.Equal("21,64,18", s102.GetProperty("pos").GetString());
        Assert.Equal(("102", "subhive", Hive), (s102.GetProperty("short").GetString(), s102.GetProperty("kind").GetString(), s102.GetProperty("owner").GetString()));
        Assert.Equal(14, s102.GetProperty("script").GetProperty("line").GetInt32());
        Assert.Equal("Normal", s102.GetProperty("budget_mode").GetString());
        Assert.Equal(0, s102.GetProperty("chat").GetArrayLength());
        var s131 = rows.Single(r => r.GetProperty("name").GetString() == "maid_chan_131");
        Assert.Equal("connecting", s131.GetProperty("status").GetString());
        Assert.False(s131.TryGetProperty("pos", out _));
    }

    [Fact]
    public async Task Viewer_now_world_bots_and_running_scripts_answer_for_a_hives_body()
    {
        var (h, c, bodies, _) = Live();
        using var _h = h;
        var scripts = new LiveScriptData(h, c.Username, () => null) { Bodies = bodies };
        var controller = new SupervisorControllerData(h, c.Username, null, () => null, () => null);
        var viewer = new LiveViewerData(h, c.Username, [], () => null, controller, scripts) { Bodies = bodies };

        Assert.Equal("maid_chan_102", viewer.Resolve("102"));
        var now = await viewer.NowAsync("maid_chan_102", default);
        Assert.NotNull(now);
        Assert.Equal(("driven by 109", "script chop_crew", "subhive", Hive), (now!.BrainState, now.Task, now.Kind, now.Owner));
        Assert.Equal(new ViewerBody(17, 12, "21,64,18", 0, null), now.Body);
        Assert.Equal(("chop_crew", 14, 3), (now.Script!.Name, now.Script.Line, now.Script.Version));
        Assert.Null(now.InFlight);
        var json = JsonSerializer.Serialize(now, SupervisorJson.Options);
        Assert.Contains("\"kind\":\"subhive\",\"owner\":\"maid_chan_109\"", json);

        var running = scripts.Running();
        var r102 = Assert.Single(running, r => r.Body == "maid_chan_102");
        Assert.Equal(("102", "chop_crew", "running", 14, Hive), (r102.Short, r102.Script, r102.Status, r102.Line, r102.Owner));
        var live = await scripts.LiveAsync("102", false, default);
        Assert.NotNull(live);
        Assert.True(live!.Running);

        var world = new LiveSupervisorWorld(h.Vision!.Hub!, () => c.World, c.Username, () => [new LiveBody(c.Username, "main", true, c.World)], h.Runtime,
            () => null, new SupervisorOptions()) { AllBodies = bodies };
        var wb = world.Bots();
        Assert.Equal(5, wb.Count); // main, 109, 102, 114, 124: the spawning 131 has no position yet
        var w102 = wb.Single(b => b.Name == "maid_chan_102");
        Assert.Equal(("subhive", Hive, false, 21.5), (w102.Kind, w102.Owner, w102.Live, w102.X));
        Assert.Equal(("hive", c.Username), (wb.Single(b => b.Name == Hive).Kind, wb.Single(b => b.Name == Hive).Owner));
        Assert.Equal("main", wb.Single(b => b.Main).Kind);
    }
}
