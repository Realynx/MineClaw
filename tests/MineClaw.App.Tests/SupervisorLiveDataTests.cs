using System.Text.Json;
using MineClaw.App.Supervisor;
using MineClaw.Bot;
using MineClaw.Bot.Perception.Vision;
using MineClaw.Bot.Tests;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// The supervisor's live data against a real bot + VisionHub (direct mode, flat colours): main and a clone as tiles, the brain
/// view byte-identical to what <c>see</c> returns, clone poses over the hive view seam (rate-limited), the camera and jumps.
/// </summary>
public class SupervisorLiveDataTests
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

    private static (BotHost Host, FakeGameClient Client, FakeHive Hive, LiveSupervisorData Data, RenderGate Gate) Make()
    {
        var o = TestBot.Options();
        o.Llm.Perception.Mode = "direct";
        var bot = new BotOptions();
        bot.Vision.Textures = false;
        var c = new FakeGameClient();
        var h = BotHost.Create(c, o, bot, new NoLlm(), persist: false);
        for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) c.World.SetBlock(x, 64, z, Blocks.Cobblestone, 0);
        var hive = new FakeHive();
        var hub = h.Vision!.Hub!;
        hub.Hive = hive;
        var now = DateTimeOffset.UtcNow;
        hive.Bots.Add(new HiveBotState("maid_chan_67", 0, 3.5, 65, 2.5, "mine the gen", 18, 15, now.AddSeconds(-2), Brain: "awake"));
        hive.Claims.Add(new HiveLease(MineClaw.World.Claims.ClaimKey.Block(3, 64, 2).Id, "maid_chan_67"));
        hive.Pose = new HiveEyePose("maid_chan_67", 0, 3.5, 65, 2.5, 180, 25, now);
        var data = new LiveSupervisorData(hub, () => c.World, c.Username, [], () => null, new SupervisorFeed(), new SupervisorOptions());
        return (h, c, hive, data, new RenderGate(0));
    }

    [Fact]
    public void Tiles_list_main_and_the_online_clones_with_jobs_vitals_and_claims()
    {
        var (h, c, _, data, gate) = Make();
        using var _g = gate;
        using var _h = h;
        var tiles = data.Bots();
        Assert.Equal(2, tiles.Count);
        Assert.True(tiles[0].Main);
        Assert.Equal(c.Username, tiles[0].Name);
        var t67 = tiles.Single(t => t.Short == "67");
        Assert.Equal(("mine the gen", 18, 15), (t67.Job, t67.Hp, t67.Food));
        Assert.Equal(["block 3,64,2"], t67.Claims);
        var json = JsonSerializer.Serialize(tiles, SupervisorJson.Options);
        Assert.Contains("\"age_seconds\":", json);
    }

    [Fact]
    public async Task The_brain_view_is_byte_identical_to_what_see_returns()
    {
        var (h, _, _, data, gate) = Make();
        using var _g = gate;
        using var _h = h;
        var see = await h.Run("see", new { radius = 6 });
        Assert.True(see.Ok, see.Text);
        var view = await data.ViewAsync("main", "brain", 6, gate, default);
        Assert.NotNull(view);
        Assert.Equal(see.Image!.Data, view!.Png);
        Assert.Equal(see.Image.Tokens, view.Tokens);
        Assert.StartsWith("see sheet focus", view.Legend);
        Assert.Contains(view.Facts, f => f.StartsWith("FocusBlock: [vision] exact:", StringComparison.Ordinal));
        Assert.Contains(view.Facts, f => f.StartsWith("Void:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clone_views_use_its_pose_from_the_hive_and_ask_at_most_once_per_window()
    {
        var (h, _, hive, data, gate) = Make();
        using var _g = gate;
        using var _h = h;
        foreach (var mode in new[] { "eye", "top", "iso", "sheet" })
        {
            var v = await data.ViewAsync("67", mode, 5, gate, default);
            Assert.NotNull(v);
            Assert.Equal(0x89, v!.Png[0]);
            Assert.Null(v.Note);
        }
        Assert.Equal(1, hive.ViewCalls); // PoseCacheMs: one "send me your view" for all four renders
        var eye = await data.ViewAsync("maid_chan_67", "eye", 5, gate, default);
        Assert.Equal((480, 360), (eye!.Width, eye.Height));
        var sheet = await data.ViewAsync("67", "sheet", 5, gate, default);
        Assert.Contains("EYE", sheet!.Legend); // a clone's sheet carries its eye panel, as main's see of it does
        Assert.Null(await data.ViewAsync("maid_chan_420", "eye", 5, gate, default));
    }

    [Fact]
    public async Task Camera_and_jumps_work_over_the_live_world_and_the_hive()
    {
        var (h, _, _, data, gate) = Make();
        using var _g = gate;
        using var _h = h;
        var frame = await data.CameraAsync(new CameraQuery(0.5, 72, 0.5, 0, 60, 0, 200, 150), gate, default);
        Assert.Equal((200, 150), (frame.Width, frame.Height));
        Assert.NotNull(frame.Seen);
        Assert.Equal("maid_chan_67", data.Locate("67")!.Name);
        Assert.Equal((4.5, 64 + 1.62, 6.5), (data.Locate("4,64,6")!.X, data.Locate("4,64,6")!.Y, data.Locate("4,64,6")!.Z));
        h.Runtime.Memory.SetWaypoint("cobble gen spot", 2, 64, 2);
        Assert.Equal("cobble gen spot", data.Locate("cobble gen spot")!.Name);
        Assert.Null(data.Locate("narnia"));
        var hive = await data.HiveAsync(gate, default);
        Assert.Equal(0x89, hive.Png[0]);
        Assert.Contains("67", hive.Legend);
    }
}
