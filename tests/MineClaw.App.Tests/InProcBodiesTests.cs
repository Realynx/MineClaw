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
/// One brain, many bodies, steps 2-3 in the App: <c>Bodies:Mode</c> defaults to <c>hive</c> (today's clone processes, unchanged);
/// <c>inproc</c> hosts the clone accounts as bodies in main's process, with the brain's <c>bodies</c> tool (step 3: replaces
/// <c>hive</c>) served by the registry, the multi-body brain interface on, and stranded items / void spots in the shared HiveStore.
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
        Assert.Contains(Agent.Prompts.BodiesHint, agent.PersonaExtra); // step 3: one mind in several bodies (cached, static)
        Assert.DoesNotContain(HiveTools.ScriptHint, agent.PersonaExtra);
        var b = InProcBodies.Create(Hive(), new BodiesOptions { Mode = "InProc", Accounts = ["maid_chan_420"] }, Mc(), new AgentOptions(), NullLoggerFactory.Instance)!;
        Assert.Equal(["maid_chan_420"], b.Accounts);
        Assert.Null(InProcBodies.Create(new HiveConfig { Enabled = false, CloneUsernames = ["maid_chan_67"] }, new BodiesOptions { Mode = "inproc" }, Mc(),
            new AgentOptions(), NullLoggerFactory.Instance));
    }

    [Fact]
    public async Task Attached_the_bodies_tool_lists_the_registry_and_stranded_items_go_to_the_hive_store()
    {
        var (bot, _) = TestBot.Create();
        using var _bot = bot;
        await using var main = new MinecraftClient(new ClientOptions { Username = "maid_chan" }); // never connected: an empty tab list
        await using var inproc = InProcBodies.Create(Hive(), new BodiesOptions { Mode = "inproc", YieldCheckSeconds = 0, TickReportSeconds = 0 }, Mc(),
            bot.Runtime.Options, NullLoggerFactory.Instance)!;
        inproc.Attach(bot, main, CancellationToken.None);
        Assert.True(bot.Bodies!.HasLifecycle);
        Assert.Equal(["maid_chan_67", "maid_chan_69"], bot.Bodies.Accounts);

        Assert.False(bot.Runtime.Tools.Contains(HiveTools.MainTool)); // step 3: the bodies tool replaces hive in this mode
        Assert.NotNull(inproc.Directory);
        Assert.Same(inproc.Directory, bot.Runtime.Agent.Bodies);
        var list = await bot.Run(InProcBodies.BodiesTool, new { op = "list" });
        Assert.True(list.Ok, list.Text);
        Assert.Equal("67 off | 69 off", list.Text);
        var empty = await bot.Run(InProcBodies.BodiesTool, new { op = "spawn", name = "67" });
        Assert.False(empty.Ok);
        Assert.Contains("needs task", empty.Text);
        Assert.Equal("no clone is online", (await bot.Run(InProcBodies.BodiesTool, new { op = "despawn", name = "all" })).Text);
        Assert.Equal("error: 67 isn't in game (bodies op=spawn name=67)", (await bot.Run(InProcBodies.BodiesTool, new { op = "focus", name = "67" })).Text);
        Assert.Equal("ok: focus is main", (await bot.Run(InProcBodies.BodiesTool, new { op = "focus" })).Text);
        // Inline Lua isn't a bodies field: Lua is written with the script tool, where only the Opus brain may author.
        var worker = await bot.RunAs(Agent.Llm.ModelTier.Worker, InProcBodies.BodiesTool, new { op = "spawn", name = "67", code = "say'hi'" });
        Assert.False(worker.Ok);
        Assert.DoesNotContain("\"code\"", bot.Runtime.Tools.Definitions(Agent.Llm.ModelTier.Main).Single(d => d.Name == InProcBodies.BodiesTool).InputSchema.GetRawText());

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
        Assert.StartsWith("no: 69 would log in over the void", (await bot.Run(InProcBodies.BodiesTool, new { op = "spawn", name = "69", task = "mine" })).Text);
    }

    private static int Tok(Agent.Llm.LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;

    [Fact]
    public async Task Inproc_keeps_the_brain_and_worker_tool_budgets_and_puts_body_in_the_prompt_not_the_schemas()
    {
        var (bot, _) = TestBot.Create();
        using var _bot = bot;
        var tools = bot.Runtime.Tools;
        var workerBefore = tools.Definitions(Agent.Llm.ModelTier.Worker).Select(d => d.Name + d.Description + d.InputSchema.GetRawText()).ToList();
        await using var main = new MinecraftClient(new ClientOptions { Username = "maid_chan" });
        var agent = bot.Runtime.Options;
        await using var inproc = InProcBodies.Create(Hive(), new BodiesOptions { Mode = "inproc", YieldCheckSeconds = 0, TickReportSeconds = 0 }, Mc(),
            agent, NullLoggerFactory.Instance)!;
        inproc.Attach(bot, main, CancellationToken.None);

        var brain = tools.Definitions(Agent.Llm.ModelTier.Main);
        var worker = tools.Definitions(Agent.Llm.ModelTier.Worker);
        int bodies = Tok(brain.Single(d => d.Name == InProcBodies.BodiesTool));
        int total = brain.Sum(Tok);
        Assert.True(bodies <= Agent.Tools.ToolBudgets.HiveTool, $"bodies tool ≈{bodies} tok");
        Assert.True(total <= Agent.Tools.ToolBudgets.Brain, $"brain tools ≈{total} tok");
        Assert.True(worker.Sum(Tok) <= Agent.Tools.ToolBudgets.Worker, $"worker tools ≈{worker.Sum(Tok)} tok");
        // The worker's tool set doesn't change (a worker run is bound to one body), and no schema grows a body field.
        Assert.Equal(workerBefore, worker.Select(d => d.Name + d.Description + d.InputSchema.GetRawText()).ToList());
        Assert.DoesNotContain(brain.Concat(worker), d => d.InputSchema.GetRawText().Contains("\"body\"", StringComparison.Ordinal));
        // The body parameter is one static cached hint (one cold cache write when the mode flips; never per turn).
        int hint = (int)Math.Ceiling(Agent.Prompts.BodiesHint.Length / 3.5);
        Assert.True(hint <= 200, $"bodies hint ≈{hint} tok");
        Assert.Contains(Agent.Prompts.BodiesHint, agent.PersonaExtra);
        Assert.Equal(agent.Worker.MaxConcurrent, inproc.Directory!.Workers!.MaxConcurrent);
    }
}
