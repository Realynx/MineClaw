using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.Render;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Main (the hive mind) sees anywhere: a landmark in any dimension from the shared world memory in the db (every hive bot's
/// remembered columns, with who saw them when), a clone's live eye view ("send me your view" over IPC), and the hive
/// overview (positions, jobs, claims, dangers: data, no model).
/// </summary>
public class VisionHiveTests
{
    private sealed class FakeHive : IHiveVision
    {
        public List<HiveBotState> Bots { get; } = new();
        public List<HiveLease> Claims { get; } = new();
        public HiveEyePose? Pose { get; set; }
        public int ViewCalls;
        public IReadOnlyList<HiveBotState> Clones() => Bots;
        public IReadOnlyList<HiveLease> Leases() => Claims;
        public Task<HiveEyePose?> ViewAsync(string clone, CancellationToken ct)
        {
            Interlocked.Increment(ref ViewCalls);
            return Task.FromResult(Pose is { } p && p.Name == clone ? p : null);
        }
    }

    /// <summary>A bot in direct mode (the sheet comes back as a picture) near 0,65,0; the shared memory is its own db.</summary>
    private static (BotHost Host, FakeGameClient Client, FakeHive Hive) Bot(bool direct = true)
    {
        var o = TestBot.Options();
        if (direct) o.Llm.Perception.Mode = "direct";
        else o.Llm.ApiKey = "sk-ant-test"; // perception on (Claude to Claude); the overview never calls it
        var bot = new BotOptions();
        bot.Vision.Textures = false;
        var c = new FakeGameClient();
        var h = BotHost.Create(c, o, bot, new NoLlm(), persist: false);
        for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) c.World.SetBlock(x, 64, z, Blocks.Cobblestone, 0);
        var hive = new FakeHive();
        h.Vision!.Hub!.Hive = hive;
        return (h, c, hive);
    }

    /// <summary>One chunk column (cx, cz) with a cobble floor at y63 and a chest at (cx*16+7, 64, cz*16+7), as a db row.</summary>
    private static ChunkRow Column(int dim, int cx, int cz, DateTimeOffset seen, string by)
    {
        var w = new WorldState { Dimension = dim };
        w.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        for (int x = 0; x < 16; x++) for (int z = 0; z < 16; z++) w.SetBlock(cx * 16 + x, 63, cz * 16 + z, Blocks.Cobblestone, 0);
        w.SetBlock(cx * 16 + 7, 64, cz * 16 + 7, Blocks.Chest, 0);
        var snap = ChunkSnapshot.Capture(w.GetColumn(cx, cz)!);
        return new ChunkRow(dim, cx, cz, snap.Hash, snap.Summary(), snap.Encode(), seen) { SeenBy = by };
    }

    [Fact]
    public async Task A_remote_landmark_renders_from_the_shared_db_memory_with_its_age()
    {
        var (h, c, _) = Bot();
        var hub = h.Vision!.Hub!;
        var now = DateTimeOffset.UtcNow;
        // The mob chamber: 300 blocks away, never loaded by main; 67 saw it 40 min ago (its columns are in the shared db).
        hub.WorldStore!.PutChunks([Column(0, 20, 20, now.AddMinutes(-40.5), "maid_chan_67"), Column(0, 21, 20, now.AddSeconds(-12), "maid_chan_69")]);
        h.Runtime.Memory.UpsertStructure("mob chamber", s => { s.Min = new(320, 63, 320); s.Max = new(334, 66, 334); s.Dimension = 0; });
        Assert.False(c.World.PlanningView.IsLoaded(327, 327));

        var target = hub.ResolveTarget("mob chamber");
        Assert.Equal((new BlockPos(327, 64, 327), 0, "mob chamber"), (target.Focus, target.Dim, target.Landmark));
        var r = await h.Run("see", new { focus = "mob chamber", views = new[] { "top", "layers" }, radius = 12 });
        Assert.True(r.Ok, r.Text);
        Assert.NotNull(r.Image);
        Assert.Contains("see sheet focus 327,64,327", r.Text);
        Assert.Contains("seen: mob chamber seen by 67 40 min ago", r.Text);
        Assert.Contains("E side seen by 69 12 s ago", r.Text);
        Assert.InRange(r.Image!.Tokens, 150, 400);
        // The blocks really come from the db: the chest 67 saw is at the chamber's centre.
        var scene = hub.SceneFor(target.Focus, 0, 6);
        Assert.False(scene.Local);
        Assert.Equal(Blocks.Chest, scene.Blocks.GetBlock(327, 64, 327));
        Assert.StartsWith("[vision] exact: 327,64,327 chest", VisionRouter.Answer("what block is at the focus?", scene.Blocks, scene.Roles.Map, (327, 64, 327), 6));
        // Another dimension, any distance: the nether column is found by its dimension, not main's.
        hub.WorldStore!.PutChunks([Column(-1, -3, 2, now.AddHours(-3), "maid_chan_1337")]);
        h.Runtime.Memory.SetWaypoint("nether farm", -41, 64, 39, dimension: -1);
        var nether = await h.Run("see", new { focus = "nether farm", views = new[] { "top" } });
        Assert.Contains("see sheet focus -41,64,39", nether.Text);
        Assert.Contains("nether farm seen by 1337 3 h ago", nether.Text);
    }

    [Fact]
    public void Staleness_is_labelled_per_region()
    {
        var now = DateTimeOffset.UtcNow;
        var region = new RegionWorld { World = new WorldState() };
        region.Seen[(0, 0)] = new ColumnSource("maid_chan", now, true);
        for (int z = -1; z <= 1; z++)
        {
            region.Seen[(1, z)] = new ColumnSource("maid_chan_67", now.AddSeconds(-12), false);      // east: 67 just walked by
            region.Seen[(-1, z)] = new ColumnSource("maid_chan", now.AddMinutes(-40 - z), false);  // west: the oldest decides
        }
        region.Seen[(0, -1)] = new ColumnSource(null, now.AddHours(-2), false);
        var s = region.Staleness((8, 64, 8), 16, now);
        Assert.Equal("here live (maid_chan); N side seen 2 h ago; E side seen by 67 12 s ago; S side never seen; W side seen by maid_chan 41 min ago", s);
        Assert.StartsWith("cobble gen live", region.Staleness((8, 64, 8), 4, now, "cobble gen"));
    }

    [Fact]
    public async Task A_clone_eye_view_asks_the_clone_for_its_view_and_renders_from_its_pose()
    {
        var (h, _, hive) = Bot();
        var hub = h.Vision!.Hub!;
        var now = DateTimeOffset.UtcNow;
        hive.Bots.Add(new HiveBotState("maid_chan_67", 0, 328.5, 64, 326.5, "mine the gen", 18, 15, now.AddSeconds(-5)));
        hive.Pose = new HiveEyePose("maid_chan_67", 0, 327.5, 64, 325.5, 180, 20, now, 9);   // facing north
        hub.WorldStore!.PutChunks([Column(0, 20, 20, now.AddSeconds(-1), "maid_chan_67")]);

        foreach (var phrase in new[] { "67", "at 67", "what 67 sees", "67's view", "maid_chan_67" })
            Assert.Equal("maid_chan_67", hub.ResolveTarget(phrase).Bot?.Name);
        var r = await h.Run("see", new { focus = "what 67 sees", views = new[] { "top" } });
        Assert.True(r.Ok, r.Text);
        Assert.Equal(1, hive.ViewCalls);                                  // "send me your view" over IPC
        Assert.StartsWith("67 live view @327,64,325 facing N, job mine the gen", r.Text);
        Assert.Contains("EYE (first person of 67)", r.Text);              // the eye panel from the clone's own pose
        Assert.Contains("see sheet focus 327,64,325", r.Text);
        Assert.Contains("seen: here seen by 67 1 s ago", r.Text);
        Assert.InRange(r.Image!.Tokens, 150, 420);
        // A clone that doesn't answer: its last snapshot, said so.
        hive.Pose = null;
        var stale = await h.Run("see", new { focus = "67", views = new[] { "top" } });
        Assert.StartsWith("67 didn't answer: its last snapshot @328,64,326", stale.Text);
    }

    [Fact]
    public async Task The_hive_overview_lists_every_bot_job_claim_and_danger_from_data()
    {
        var (h, _, hive) = Bot(direct: false); // perception mode: text only unless a picture is asked for
        var now = DateTimeOffset.UtcNow;
        hive.Bots.Add(new HiveBotState("maid_chan_67", 0, 5.5, 64, 7.5, "mine the cobble gen", 5, 14, now.AddSeconds(-3)));
        hive.Bots.Add(new HiveBotState("maid_chan_1337", -1, 2.5, 66, 0.5, "chop nether trees", 20, 20, now.AddSeconds(-30)));
        hive.Claims.Add(new HiveLease(MineClaw.World.Claims.ClaimKey.Block(4, 64, 6).Id, "maid_chan_67"));
        hive.Claims.Add(new HiveLease(MineClaw.World.Claims.ClaimKey.Chest(new BlockPos(14, 64, -5)).Id, "maid_chan_69"));

        var r = await h.Run("see", new { focus = "hive" });
        Assert.True(r.Ok, r.Text);
        Assert.Null(r.Image);
        Assert.StartsWith("[hive] 3 bots: main @0,65,0 idle hp20 food20; ", r.Text);
        Assert.Contains("1337 nether @2,66,0 chop nether trees hp20 food20, 30 s ago", r.Text);
        Assert.Contains("67 @5,64,7 mine the cobble gen (claims block 4,64,6) hp5 food14", r.Text);
        Assert.Contains("other claims: 69 chest 14,64,-5", r.Text);
        Assert.Contains("danger: 67 hp 5", r.Text);
        Assert.Contains("map: here live", r.Text);
        Assert.True(r.Text.Length / 3.5 <= 150, $"~{r.Text.Length / 3.5:0} tokens: {r.Text}");

        // The picture on request: one top-down panel, bots and claims drawn, ≈300 tokens.
        var p = await h.Run("see", new { focus = "hive", views = new[] { "top" } });
        Assert.NotNull(p.Image);
        Assert.InRange(p.Image!.Tokens, 80, 400);
    }
}
