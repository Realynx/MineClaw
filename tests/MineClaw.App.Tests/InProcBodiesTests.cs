using System.Text;
using MineClaw.Agent;
using MineClaw.App.Hosting;
using MineClaw.Bot.Bodies;
using MineClaw.Bot.Tests;
using MineClaw.Client;
using MineClaw.Hive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.App.Tests;

/// <summary>
/// One brain, many bodies, step 2 in the App: <c>Bodies:Mode</c> defaults to <c>hive</c> (today's clone processes, unchanged);
/// <c>inproc</c> hosts the clone accounts as bodies in main's process, with the brain's hive tool served by the registry and
/// stranded items / void spots in the shared HiveStore.
/// </summary>
public class InProcBodiesTests
{
    private static HiveConfig Hive() => new() { CloneUsernames = ["maid_chan_67", "maid_chan_69", "maid_chan"] };
    private static MinecraftSettings Mc() => new() { Host = "127.0.0.1", Port = 1, Username = "maid_chan" };

    [Fact]
    public void The_default_mode_is_hive_and_creates_nothing()
    {
        var o = new BodiesOptions();
        Assert.Equal("hive", o.Mode);
        Assert.False(o.IsInProc);
        Assert.Null(InProcBodies.Create(Hive(), o, Mc(), new AgentOptions(), NullLoggerFactory.Instance));
        // Bound from config the way AppHost does.
        var cfg = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{"Bodies":{"Mode":"inproc","MaxCrashes":5}}"""))).Build();
        var bound = cfg.GetSection("Bodies").Get<BodiesOptions>()!;
        Assert.True(bound.IsInProc);
        Assert.Equal(5, bound.MaxCrashes);
        Assert.Equal(4.5, bound.LaunchGapSeconds);
    }

    [Fact]
    public void Inproc_takes_the_hive_clone_names_without_main_unless_Bodies_Accounts_is_set()
    {
        var agent = new AgentOptions();
        var a = InProcBodies.Create(Hive(), new BodiesOptions { Mode = "inproc" }, Mc(), agent, NullLoggerFactory.Instance)!;
        Assert.Equal(["maid_chan_67", "maid_chan_69"], a.Accounts);
        Assert.Equal(["maid_chan", "maid_chan_67", "maid_chan_69"], a.HiveBots());
        Assert.Contains(HiveTools.ScriptHint, agent.PersonaExtra); // the hive tool keeps working: its cached hint stays
        var b = InProcBodies.Create(Hive(), new BodiesOptions { Mode = "InProc", Accounts = ["maid_chan_420"] }, Mc(), new AgentOptions(), NullLoggerFactory.Instance)!;
        Assert.Equal(["maid_chan_420"], b.Accounts);
        Assert.Null(InProcBodies.Create(new HiveConfig { Enabled = false, CloneUsernames = ["maid_chan_67"] }, new BodiesOptions { Mode = "inproc" }, Mc(),
            new AgentOptions(), NullLoggerFactory.Instance));
    }

    [Fact]
    public async Task Attached_the_hive_tool_lists_the_registry_and_stranded_items_go_to_the_hive_store()
    {
        var (bot, _) = TestBot.Create();
        using var _bot = bot;
        await using var main = new MinecraftClient(new ClientOptions { Username = "maid_chan" }); // never connected: an empty tab list
        await using var inproc = InProcBodies.Create(Hive(), new BodiesOptions { Mode = "inproc", YieldCheckSeconds = 0, TickReportSeconds = 0 }, Mc(),
            bot.Runtime.Options, NullLoggerFactory.Instance)!;
        inproc.Attach(bot, main, CancellationToken.None);
        Assert.True(bot.Bodies!.HasLifecycle);
        Assert.Equal(["maid_chan_67", "maid_chan_69"], bot.Bodies.Accounts);

        var list = await bot.Run(HiveTools.MainTool, new { op = "list" });
        Assert.True(list.Ok, list.Text);
        Assert.Equal("67 off | 69 off", list.Text);
        var empty = await bot.Run(HiveTools.MainTool, new { op = "spawn", name = "67" });
        Assert.False(empty.Ok);
        Assert.Contains("needs task", empty.Text);
        Assert.Equal("no clone is online", (await bot.Run(HiveTools.MainTool, new { op = "recall", name = "all" })).Text);
        // Only the Opus brain writes Lua, also for a clone.
        var worker = await bot.RunAs(Agent.Llm.ModelTier.Worker, HiveTools.MainTool, new { op = "spawn", name = "67", code = "say'hi'" });
        Assert.False(worker.Ok);

        // Stranded items and void spots: the shared hive store (what the hive's main-side status reads).
        var fake = new FakeGameClient { Username = "maid_chan_67" };
        fake.World.Inventory.SetSlot(0, 36, new MineClaw.Data.ItemStack(4, 20, 0));
        var body = new Body("maid_chan_67", false, fake, fake, new MineClaw.Agent.Tools.ActivityManager(bot.Runtime.Tools, bot.Runtime.Events));
        IBodyHost host = inproc;
        host.Stranded(body, "logout: test", 20);
        var store = new MineClaw.Hive.Db.HiveStore(bot.Runtime.Db, "maid_chan");
        Assert.Equal(20, store.Stranded("maid_chan_67")!.Count);
        host.Stranded(body, "logout: test", 0);
        Assert.Null(store.Stranded("maid_chan_67"));
        host.MarkVoid("maid_chan_69", new MineClaw.World.Vec3d(-5.5, 0.6, -8.5));
        Assert.Equal("-6,0,-9", host.VoidSpot("maid_chan_69"));
        Assert.Contains("69 off (login spot over the void at -6,0,-9)", bot.Bodies.List());
        Assert.StartsWith("no: 69 would log in over the void", (await bot.Run(HiveTools.MainTool, new { op = "spawn", name = "69", task = "mine" })).Text);
    }
}
