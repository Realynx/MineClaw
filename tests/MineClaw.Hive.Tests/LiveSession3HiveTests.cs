using System.Text.Json;
using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Human;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Tools;
using MineClaw.Client;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Inventory;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Supervisor;
using MineClaw.Hive.Testing;
using MineClaw.World;
using DataItems = MineClaw.Data.Items;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live session 3 (2026-10-27 14:05, data/alice-logs-s3/mineclaw-20261027.log): clone-number addressing. Player lines are
/// the real ones.
/// </summary>
public class LiveSession3ArbiterTests
{
    private static readonly string[] Clones = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private static ChatArbiter Main(params string[] live)
    {
        var a = new ChatArbiter("maid_chan", "maid_chan", Clones, ["maid-chan", "maid_chan"]);
        a.SetLive(live);
        return a;
    }

    private static ChatMessage Fox(string text) => new(ChatKind.Public, "PoofimaFox", text, text);

    [Theory] // 14:16:29 / 14:27:07 / 14:32:33: a clone as the object of a request to main goes to MAIN (it delegates), not to the clone
    [InlineData("and have clone 69 harvest and replant trees")]
    [InlineData("have 69 come in and do the same procedure with you in parallel")]
    [InlineData("have 69 spawn in and do the other side of the cobble")]
    public void A_clone_named_as_the_object_of_a_request_is_mains_line(string text)
    {
        foreach (var live in new[] { new[] { "maid_chan_69" }, Array.Empty<string>() })
        {
            var main = Main(live);
            var r = main.Route(Fox(text));
            Assert.Equal("maid_chan", r.Owner);
            Assert.True(r.Explicit);              // addressed to main: never "(not to you)" context
            Assert.False(r.Group);
            Assert.True(main.Decide(Fox(text), locallyAddressed: false));

            var c69 = new ChatArbiter("maid_chan_69", "maid_chan", Clones);
            c69.Apply(main.ToRoster());
            Assert.False(c69.Decide(Fox(text), locallyAddressed: true));
            Assert.False(c69.AddressesSelf(Fox(text)));
            Assert.True(c69.OwnedElsewhere(Fox(text)));
        }
    }

    [Theory] // 14:13:32 "69 bring me a water pale" (and "69?" / "hello? maid 69?!"): a line-initial number or an address word
    [InlineData("69 bring me a water pale")]
    [InlineData("69?")]
    [InlineData("hello? maid 69?!")]
    [InlineData("69, come here")]
    [InlineData("maid 69 come here")]
    [InlineData("clone 69 come here")]
    [InlineData("lol hey 69 come here")]
    [InlineData("ok so 69 maid can you come")]
    public void A_number_next_to_an_address_word_or_starting_the_line_addresses_the_clone(string text)
    {
        var r = Main("maid_chan_69").Route(Fox(text));
        Assert.Equal("maid_chan_69", r.Owner);
        Assert.True(r.Explicit);
        // 69 logged out: the line is a request to main (nobody answered "69 bring me a water pale" at 14:13:32).
        var off = Main().Route(Fox(text));
        Assert.Equal("maid_chan", off.Owner);
        Assert.True(off.Explicit);
    }

    [Theory] // ordinary numbers in a sentence never address a clone
    [InlineData("go farm the trees")]
    [InlineData("maid why are you not farming trees, arer you confused who I meant?")]
    [InlineData("get 67 cobble")]
    [InlineData("can you come with 67?")]
    [InlineData("go stand at 6,67,8 please")]
    [InlineData("I counted 69!")]
    [InlineData("we still need 67 more logs")]
    [InlineData("I have 69 logs")]
    public void An_ordinary_number_is_not_an_address(string text)
    {
        var main = Main("maid_chan_67", "maid_chan_69");
        var r = main.Route(Fox(text));
        Assert.Equal("maid_chan", r.Owner);
        Assert.False(r.Group);
        foreach (var c in new[] { "maid_chan_67", "maid_chan_69" })
        {
            var a = new ChatArbiter(c, "maid_chan", Clones);
            a.Apply(main.ToRoster());
            Assert.False(a.AddressesSelf(Fox(text)));
            Assert.False(a.Decide(Fox(text), locallyAddressed: true));
        }
    }

    [Fact] // "I have 69 logs" is a player's statement, not an imperative to the hive: main isn't woken by it
    public void A_statement_mentioning_a_number_is_not_addressed_to_main_either()
    {
        Assert.False(Main("maid_chan_69").Route(Fox("I have 69 logs")).Explicit);
        Assert.False(Main("maid_chan_69").Route(Fox("go farm the trees")).Explicit);
    }
}

/// <summary>Live session 3, 14:33: 69 respawned with a cobble task resumed its old tree-farm order instead.</summary>
public class LiveSession3OrderTests
{
    private const string Clone = "maid_chan_69";
    private const string Cobble = "Mine the cobble generator on main island at 4,64,6 / 5,64,7 / 6,64,8 (stand 3,64,4) from the other side, with a stone pickaxe.";

    private sealed class Rig
    {
        public MemoryStore Memory { get; } = new((string?)null);
        public ToolRegistry Tools { get; } = new();
        public EventQueue Events { get; } = new();
        public ActivityManager Activities { get; }
        public StandingOrders Orders { get; }
        public int TreeRuns, DelegateRuns;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Rig()
        {
            Activities = new ActivityManager(Tools, Events);
            Tools.Register(new DelegateTool("chop_trees", "Chop the tree farm.", JsonSerializer.SerializeToElement(new { type = "object" }), async (_, ct) =>
            {
                Interlocked.Increment(ref TreeRuns);
                await Release.Task.WaitAsync(ct);
                return ToolResult.Success("chopped");
            }, longRunning: true));
            Tools.Register(new DelegateTool("delegate", "Worker.", JsonSerializer.SerializeToElement(new { type = "object" }), (_, _) =>
            {
                Interlocked.Increment(ref DelegateRuns);
                return Task.FromResult(ToolResult.Success("ok"));
            }, longRunning: true));
            Orders = new StandingOrders(Memory, Tools, Activities, Events, Clone);
            // The order main set for this clone earlier (standing_order for=69): the tree farm.
            Memory.SetStandingOrder(new StandingOrder
            {
                Bot = Clone, Player = "PoofimaFox", Job = "work the tree farm", Tool = "chop_trees", Input = JsonSerializer.SerializeToElement(new { }),
            });
        }
    }

    [Fact] // the task frame arrives after the brain loop started (loadout walk first): the old order must not resume meanwhile
    public void A_spawn_with_a_task_holds_the_old_order_and_the_task_replaces_it()
    {
        var r = new Rig();
        var orders = new CloneTaskOrders(r.Orders, r.Memory, r.Activities);
        orders.HoldForTask();                                  // process start: welcome carried a task
        Assert.False(r.Orders.ResumeIfIdle("start"));          // BotAgent's start resume: nothing re-issued
        Assert.Equal(0, r.TreeRuns);

        Assert.Equal("work the tree farm", orders.Apply(new TaskMsg("81977f1c", Cobble)));
        var cur = r.Orders.Current!;
        Assert.Equal(CloneScriptRunner.OrderPlayer, cur.Player);
        Assert.StartsWith("Mine the cobble generator", cur.Job);
        Assert.Contains("cobble", r.Orders.HeaderTag());       // the turn header shows the NEW job
        Assert.DoesNotContain("tree", r.Orders.HeaderTag());
        // The brain runs its task; C# never re-issues it (no double work), and the trees stay replaced.
        Assert.False(r.Orders.ResumeIfIdle("idle"));
        Assert.Equal(0, r.TreeRuns);
        Assert.Equal(0, r.DelegateRuns);
        // The same task again (crash restart) changes nothing.
        Assert.Null(orders.Apply(new TaskMsg("81977f1c", Cobble, Restarted: true)));
    }

    [Fact] // the old order was already resumed when the new task landed: it stops
    public async Task A_new_task_stops_an_old_order_that_already_resumed()
    {
        var r = new Rig();
        var orders = new CloneTaskOrders(r.Orders, r.Memory, r.Activities);
        Assert.True(r.Orders.ResumeIfIdle("start"));           // no task with the spawn: the stored order resumes (by design)
        await Wait.Until(() => r.TreeRuns == 1 && r.Activities.IsBusy, 5, "trees running");

        orders.Apply(new TaskMsg("81977f1c", Cobble));
        await Wait.Until(() => !r.Activities.IsBusy, 5, "old order stopped");
        Assert.StartsWith("Mine the cobble generator", r.Orders.Current!.Job);
        Assert.False(r.Orders.ResumeIfIdle("after turn"));
        Assert.Equal(1, r.TreeRuns);
    }

    [Fact] // an order main sets for the clone later (standing_order for=69) still resumes when idle
    public void A_later_order_from_main_resumes_normally()
    {
        var r = new Rig();
        var orders = new CloneTaskOrders(r.Orders, r.Memory, r.Activities);
        orders.HoldForTask();
        orders.Apply(new TaskMsg("t1", Cobble));
        r.Memory.SetStandingOrder(new StandingOrder
        {
            Bot = Clone, Player = "PoofimaFox", Job = "chop trees all night", Tool = "chop_trees", Input = JsonSerializer.SerializeToElement(new { }),
        });
        Assert.True(r.Orders.ResumeIfIdle("idle"));
        r.Release.TrySetResult();
    }
}

/// <summary>
/// Supervisor + real CloneHost over IPC (fake bodies): recall and respawn in one turn, and the loadout hand-off from main.
/// </summary>
[Collection("processes")]
public class LiveSession3HiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class FakeProcess(int id, Task<int> run, CancellationTokenSource kill) : ICloneProcess
    {
        public int Id => id;
        public bool HasExited => run.IsCompleted;
        public int? ExitCode => run.IsCompletedSuccessfully ? run.Result : run.IsCompleted ? 1 : null;
        public async Task WaitForExitAsync(CancellationToken ct = default) { try { await run.WaitAsync(ct); } catch (Exception) when (!ct.IsCancellationRequested) { } }
        public void Kill() { try { kill.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() { }
    }

    private sealed class InProcLauncher(HiveConfig cfg) : IProcessLauncher
    {
        private int _ids = 2000;
        public readonly List<string> Starts = new();
        public readonly List<FakeBody> Bodies = new();
        public Action<FakeBody>? Configure { get; set; }

        public ICloneProcess Start(string clone, string pipeName)
        {
            var kill = new CancellationTokenSource();
            var body = new FakeBody(clone, new FakePort());
            Configure?.Invoke(body);
            lock (Starts) { Starts.Add(clone); Bodies.Add(body); }
            var host = new CloneHost(clone, pipeName, cfg, body);
            return new FakeProcess(Interlocked.Increment(ref _ids), Task.Run(() => host.RunAsync(kill.Token)), kill);
        }

        public FakeBody Last(string clone) { lock (Starts) return Bodies.Last(b => b.Name == clone); }
        public int Count(string clone) { lock (Starts) return Starts.Count(s => s == clone); }
    }

    private sealed class Hive : IAsyncDisposable
    {
        public HiveConfig Cfg { get; } = TestHive.Config();
        public HiveSupervisor Sup { get; }
        public InProcLauncher Launcher { get; }
        public List<string> Notices { get; } = new();

        public Hive()
        {
            Cfg.LaunchGapSeconds = 0;
            Cfg.DepositTimeoutSeconds = 5;
            Launcher = new InProcLauncher(Cfg);
            var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, Cfg.Budget, () => (0m, 0m));
            Sup = new HiveSupervisor(Cfg, "maid_chan", Launcher, new ClaimBoard(TimeSpan.FromSeconds(60)), ledger,
                new ChatArbiter("maid_chan", "maid_chan", Cfg.Clones()), null, () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"]);
            Sup.Notice += e => { lock (Notices) Notices.Add(e.Text); };
        }

        public string NoticeText() { lock (Notices) return string.Join(" || ", Notices); }

        public async ValueTask DisposeAsync()
        {
            try { await Sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await Sup.DisposeAsync();
        }
    }

    /// <summary>Main's live inventory at 14:16:58; a toss lands in the asking clone's fake inventory (its pick-up).</summary>
    private sealed class Donor : IHiveDonor
    {
        public readonly List<ItemMsg> Items_ =
        [
            new(17, 0, 64, "oak_log"), new(17, 0, 4, "oak_log"), new(6, 0, 7, "oak_sapling"), new(4, 0, 3, "cobblestone"),
            new(DataItems.StonePickaxe, 0, 1, "stone_pickaxe"), new(DataItems.StonePickaxe, 0, 1, "stone_pickaxe"),
            new(295, 0, 5, "seeds"), new(290, 0, 1, "wooden_hoe"), new(DataItems.StoneAxe, 0, 1, "stone_axe"),
        ];
        public FakePort? Target;
        public int Holding, Tosses;
        public Vec3d? Position => new Vec3d(6.5, 64, -5.5);
        public IReadOnlyList<ItemMsg> Items() { lock (Items_) return Items_.ToList(); }
        public int HeldId => DataItems.StoneAxe; // chopping
        public IDisposable? HoldStill(TimeSpan max) { Interlocked.Increment(ref Holding); return new Hold(this); }
        public Task<int> TossAsync(int id, int count, Vec3d toward, CancellationToken ct)
        {
            Interlocked.Increment(ref Tosses);
            lock (Items_)
            {
                var stack = Items_.First(i => i.Id == id);
                Items_.Remove(stack);
                Target?.Add(stack with { Count = count });
            }
            return Task.FromResult(count);
        }

        private sealed class Hold(Donor d) : IDisposable
        {
            private int _done;
            public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref d.Holding); }
        }
    }

    private static readonly TurnInfo FoxTurn = new("PoofimaFox", ["PoofimaFox"], false);
    private const string Trees = "Work the tree farm on main island (spots 9,65,0/3, 13,65,0/3): cut all logs, replant, deposit logs in chest 14,64,-5.";
    private const string Cobble = "Mine the cobble generator on main island at 4,64,6 / 5,64,7 / 6,64,8 from the other side, with a stone pickaxe.";

    [Fact] // 14:33:40 "recalling 69" then the cobble task in the same turn: 69 logs back in with the COBBLE task
    public async Task Recall_then_a_new_task_in_one_turn_relaunches_with_the_new_task_only()
    {
        await using var h = new Hive();
        TurnContext.Current = FoxTurn;
        try { Assert.StartsWith("ok", h.Sup.Spawn(Trees, "69")); }
        finally { TurnContext.Current = null; }
        await Wait.Until(() => h.Sup.Slot("maid_chan_69")!.Connected, 20, "69 up");

        TurnContext.Current = new TurnInfo("kiwi", ["kiwi"], false);
        try
        {
            Assert.StartsWith("ok: recalling 69", h.Sup.Recall("69"));
            var r = h.Sup.Spawn(Cobble, "69");
            output.WriteLine(r);
            Assert.Contains("logs straight back in with this task", r);
        }
        finally { TurnContext.Current = null; }

        await Wait.Until(() => h.Launcher.Count("maid_chan_69") == 2, 20, "69 relaunched");
        var body = h.Launcher.Last("maid_chan_69");
        await Wait.Until(() => { lock (body.Calls) return body.Calls.Any(c => c.StartsWith("task ")); }, 20, "task applied");
        lock (body.Calls)
        {
            var tasks = body.Calls.Where(c => c.StartsWith("task ")).ToList();
            Assert.Single(tasks);
            Assert.Contains("cobble generator", tasks[0]);
            Assert.DoesNotContain("tree farm", tasks[0]);
        }
        Assert.Contains("cobble generator", h.Sup.Slot("maid_chan_69")!.Task!.Text);
    }

    [Fact] // 14:16:58 67 "loadout short: no pickaxe, no axe, no food" while main held 2 stone pickaxes
    public async Task Loadout_gets_a_spare_tool_from_main_and_main_keeps_the_one_it_uses()
    {
        await using var h = new Hive();
        var donor = new Donor();
        h.Sup.Donor = donor;
        h.Launcher.Configure = b =>
        {
            donor.Target = b.Port;
            b.Loadout = want => new LoadoutReport("no pickaxe, no axe, no food in my inventory or storage", want.ToList());
        };
        TurnContext.Current = FoxTurn;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("Tool duty: stay by the crafting table at 15,64,-11; craft stone pickaxes and axes when asked; mine cobble and chop trees for them", "67"));
        }
        finally { TurnContext.Current = null; }

        var body = h.Launcher.Last("maid_chan_67");
        await Wait.Until(() => { lock (body.Calls) return body.Calls.Any(c => c.StartsWith("task ")); }, 20, "task applied");
        string task;
        lock (body.Calls) task = body.Calls.First(c => c.StartsWith("task "));
        output.WriteLine(task);
        output.WriteLine(h.NoticeText());

        Assert.Equal(1, donor.Tosses);
        Assert.Contains(body.Port.Items(), i => i.Id == DataItems.StonePickaxe);              // 67 has a pickaxe now
        Assert.Single(donor.Items(), i => i.Id == DataItems.StonePickaxe);                     // main kept one
        Assert.Contains(donor.Items(), i => i.Id == DataItems.StoneAxe);                       // its only axe (in hand) stays
        Assert.Equal(0, donor.Holding);                                                        // main moves again
        Assert.Contains("main handed me 1 stone_pickaxe", task);
        Assert.Contains("handed 67 1 stone_pickaxe", h.NoticeText());
        await Wait.Until(() => h.NoticeText().Contains("loadout short"), 10, "short report");
        Assert.Contains("loadout short: no axe, no food", h.NoticeText());
        Assert.DoesNotContain("no pickaxe", h.NoticeText());
    }

    [Fact] // 14:34:35 main: "69, robin wants you on the cobble gen, not the trees": clones ignore hive chat, so it goes over IPC
    public async Task Mains_chat_instruction_to_a_clone_becomes_a_hive_task_or_note()
    {
        await using var h = new Hive();
        TurnContext.Current = FoxTurn;
        try { Assert.StartsWith("ok", h.Sup.Spawn(Trees, "69")); }
        finally { TurnContext.Current = null; }
        await Wait.Until(() => h.Sup.Slot("maid_chan_69")!.Connected, 20, "69 up");
        var body = h.Launcher.Last("maid_chan_69");
        await Wait.Until(() => { lock (body.Calls) return body.Calls.Any(c => c.StartsWith("task ")); }, 20, "first task");
        var guard = new ChatRelayGuard(h.Sup);

        const string line = "Master, the 64 cobble and 20 slabs are in your inventory now uwu. 69, robin wants you on the cobble gen, not the trees.";
        TurnContext.Current = new TurnInfo("kiwi", ["kiwi"], false);
        try
        {
            Assert.Equal(line, guard.Filter(line));          // the chat line stays (flavour)
            Assert.Equal(line, guard.Filter(line));          // the gate may run twice for one line: one task only
            Assert.Equal("69, good job!", guard.Filter("69, good job!"));
            guard.Filter("ok 69: bring the spare saplings to the chest when you're done");
        }
        finally { TurnContext.Current = null; }

        await Wait.Until(() => { lock (body.Calls) return body.Calls.Count(c => c.StartsWith("task ")) == 2 && body.Calls.Any(c => c.StartsWith("note ")); }, 10, "relayed");
        lock (body.Calls)
        {
            Assert.Contains(body.Calls, c => c == "task From main: robin wants you on the cobble gen, not the trees.");
            var note = Assert.Single(body.Calls, c => c.StartsWith("note "));
            Assert.Contains("bring the spare saplings to the chest", note);
            Assert.DoesNotContain(body.Calls, c => c.Contains("good job"));
        }
        Assert.Contains("cobble gen", h.Sup.Slot("maid_chan_69")!.Task!.Text);
    }

    [Theory] // 14:35:08 69 posted "went with Stay on the tree farm standing order; skip the cobble gen unless told. since you're busy,"
    [InlineData("Stay on the tree farm standing order; skip the cobble gen unless told.", "Fox, gonna stay on the tree farm for now since you're busy")]
    [InlineData("keep chopping the east trees", "Fox, gonna keep chopping the east trees for now since you're busy")]
    [InlineData("mine 4,64,6 first, then the cell at 5,64,7 next to the lava", "Fox, going with my best guess for now")]
    [InlineData("left cell", "Fox, went with left cell since you're busy")]
    public void A_question_timeout_never_posts_the_raw_default(string dflt, string starts)
    {
        var line = new MineClaw.Agent.Human.QuestionOptions().TimeoutText("Fox", dflt, risky: false);
        Assert.StartsWith(starts, line);
        if (dflt.Contains(';') || dflt.Contains(',')) Assert.DoesNotContain(dflt, line); // instructions and coordinates never go out raw
        Assert.DoesNotContain("standing order", line);
        Assert.DoesNotContain("unless told", line);
    }

    [Fact]
    public void Spare_keeps_the_tool_in_hand_and_names_kinds_or_items()
    {
        var items = new List<ItemMsg> { new(DataItems.IronPickaxe, 0, 1, "iron_pickaxe"), new(DataItems.StonePickaxe, 0, 1, "stone_pickaxe") };
        // Main uses the iron one: the stone one is spare.
        Assert.Equal(DataItems.StonePickaxe, HiveLending.Spare(items, "pickaxe", 1, heldId: DataItems.IronPickaxe)!.Id);
        // Holding the stone one: the iron one may go (the kept one is the one in hand).
        Assert.Equal(DataItems.IronPickaxe, HiveLending.Spare(items, "pickaxe", 1, heldId: DataItems.StonePickaxe)!.Id);
        // Only one: none spare; reserved items never go.
        Assert.Null(HiveLending.Spare([items[1]], "pickaxe", 1));
        Assert.Null(HiveLending.Spare(items, "pickaxe", 1, reserved: new HashSet<int> { DataItems.StonePickaxe }));
        // Item names and counts: keep one, give the rest asked for.
        var sticks = new List<ItemMsg> { new(DataItems.ByName("stick")!.Id, 0, 10, "stick") };
        Assert.Equal(4, HiveLending.Spare(sticks, "stick", 4)!.Count);
        Assert.Equal(9, HiveLending.Spare(sticks, "stick", 64)!.Count);
        Assert.Null(HiveLending.Spare(sticks, "no_such_item", 1));
    }

    [Fact] // main has none spare: another live clone's spare is named (hive inventory), nothing is tossed
    public async Task No_spare_on_main_names_a_clone_that_has_one()
    {
        await using var h = new Hive();
        var donor = new Donor();
        lock (donor.Items_) donor.Items_.RemoveAll(i => i.Id == DataItems.StonePickaxe);
        h.Sup.Donor = donor;
        var s69 = h.Sup.Slot("maid_chan_69")!;
        s69.State = CloneState.Running;
        s69.Connected = true;
        s69.Inventory = [new(DataItems.StonePickaxe, 0, 1, "stone_pickaxe"), new(DataItems.StonePickaxe, 0, 1, "stone_pickaxe")];
        var s67 = h.Sup.Slot("maid_chan_67")!;
        var reply = h.Sup.Lend(s67, new LendMsg("pickaxe", 1));
        Assert.False(reply.Ok);
        Assert.Equal("main has no pickaxe", reply.Reason);
        Assert.Equal(["69 (2 stone_pickaxe)"], reply.Holders!);
        Assert.Equal(0, donor.Holding);
        await Task.CompletedTask;
    }
}
