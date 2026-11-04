using MineClaw.Agent;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
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
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Night review 2026-10-27 (docs/reviews/2026-10-27-night-log-review.md), hive items. Player lines are the real ones from
/// data/alice-logs/mineclaw-20261027.log.
/// </summary>
public class NightReviewArbiterTests
{
    private static readonly string[] Clones = ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private static ChatArbiter Main(params string[] live)
    {
        var a = new ChatArbiter("maid_chan", "maid_chan", Clones, ["maid-chan", "maid_chan"]);
        a.SetLive(live.Length > 0 ? live : ["maid_chan_67", "maid_chan_69"]);
        return a;
    }

    private static ChatRoute Route(ChatArbiter a, string text) => a.Route(new ChatMessage(ChatKind.Public, "PoofimaFox", text, text));

    [Theory] // B2: a clone named mid-sentence must get the line (06:58:07, 06:59:21, 07:37:09)
    [InlineData("also 67, this block I'm punching is your cell to mine", "maid_chan_67")]
    [InlineData("hello 67? did you hear me about mining the right cell???", "maid_chan_67")]
    [InlineData("hey 69, just deliver tools to any farming clone, use hivemind to collab", "maid_chan_69")]
    [InlineData("67 you need to be working the other cell of this cobble gen with a pickaxe all night.", "maid_chan_67")]
    [InlineData("good job 67!", "maid_chan_67")]
    [InlineData("thanks 69", "maid_chan_69")]
    [InlineData("ok 67 go mine the west cell", "maid_chan_67")]
    [InlineData("nice. 69 can you craft me a pick", "maid_chan_69")]
    public void A_clone_named_anywhere_as_a_vocative_gets_the_line(string text, string owner)
    {
        var r = Route(Main(), text);
        Assert.Equal(owner, r.Owner);
        Assert.True(r.Explicit);
        Assert.False(r.Group);
    }

    [Theory] // ...but a mention, a quantity or a coordinate is not an address (real lines from the same night + live2)
    [InlineData("maid, can you get 67 and go work the cobble gen for me all night?")]
    [InlineData("and 69 will be on crafting duty, when you need new tools they will craft it and deliver it to you")]
    [InlineData("yes that is 69's sole job tonight, item crafter hive mind should notify him and create the tool")]
    [InlineData("can you make 420 and 69 have a spar please? I'd like to see your combat system. craft them two sword")]
    [InlineData("yea :3, in the meantime can you harvest trees with 67? have 69 on tool crafting duty")]
    [InlineData("have 420 respawn again to get in the map properly atleast")]
    [InlineData("hmmm did 420 get on island?")]
    [InlineData("go stand at 6,67,8 please")]
    [InlineData("get 67 cobble")]
    [InlineData("tell maid chan 67 to come back")]
    public void A_mention_quantity_or_coordinate_stays_with_main(string text)
    {
        var r = Route(Main("maid_chan_67", "maid_chan_69", "maid_chan_420"), text);
        Assert.Equal("maid_chan", r.Owner);
        Assert.False(r.Group);
    }

    [Theory] // B2 ambiguity: a line naming two bots goes to both, main coordinating
    [InlineData("67 and 69, come here", "maid_chan_67,maid_chan_69")]
    [InlineData("hey 67, 69, stop fighting", "maid_chan_67,maid_chan_69")]
    [InlineData("maid chan and 67, come look at this", "maid_chan,maid_chan_67")]
    public void A_line_naming_two_bots_goes_to_both_with_main_coordinating(string text, string named)
    {
        var main = Main();
        var r = Route(main, text);
        Assert.Equal("maid_chan", r.Owner);
        Assert.True(r.Group);
        Assert.Equal(named, string.Join(",", r.Addressed!));
        var msg = new ChatMessage(ChatKind.Public, "PoofimaFox", text, text);
        Assert.True(main.IsGroupAsk(msg));        // main's turn hint: split it, the named clones answer their part

        // Every process decides the same way: main handles (coordinates), each named clone handles, the rest don't.
        var handled = new List<string>();
        if (main.Decide(msg, locallyAddressed: false)) handled.Add("maid_chan");
        foreach (var c in Clones)
        {
            var a = new ChatArbiter(c, "maid_chan", Clones);
            a.Apply(main.ToRoster());
            if (a.Decide(msg, locallyAddressed: false)) handled.Add(c);
            Assert.Equal(r.Names(c), a.AddressesSelf(msg));
            Assert.Equal(!r.Names(c), a.OwnedElsewhere(msg)); // never eats another bot's reply
        }
        Assert.Equal(new[] { "maid_chan" }.Concat(named.Split(',').Where(n => n != "maid_chan")).ToList(), handled);
        // The named clones' chat_ok doesn't move the conversation off main.
        main.AcceptFromClone("PoofimaFox", "maid_chan_67");
        Assert.Equal("maid_chan", main.ConversationOwner("PoofimaFox"));
    }
}

/// <summary>Supervisor items: in-process clones (real CloneHost + IPC, fake body) behind a fake process launcher.</summary>
[Collection("processes")]
public class NightReviewSupervisorTests(Xunit.Abstractions.ITestOutputHelper output)
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

    /// <summary>Starts each clone as an in-process CloneHost with a <see cref="FakeBody"/> (or exits with <see cref="ExitCode"/>).</summary>
    private sealed class InProcLauncher(HiveConfig cfg, Microsoft.Extensions.Logging.ILogger? log = null) : IProcessLauncher
    {
        private int _ids = 1000;
        public readonly List<(string Clone, DateTimeOffset At)> Starts = new();
        public readonly Dictionary<string, FakeBody> Bodies = new(StringComparer.OrdinalIgnoreCase);
        public int? ExitCode { get; set; }
        /// <summary>The process counts as exited when its body disconnects, and its link stays open this long after (main sees the
        /// exit before the link's close).</summary>
        public TimeSpan? ExitBeforeLinkCloses { get; set; }

        public ICloneProcess Start(string clone, string pipeName)
        {
            lock (Starts) Starts.Add((clone, DateTimeOffset.UtcNow));
            var kill = new CancellationTokenSource();
            if (ExitCode is { } code) return new FakeProcess(Interlocked.Increment(ref _ids), Task.FromResult(code), kill);
            var body = new FakeBody(clone, new FakePort());
            lock (Bodies) Bodies[clone] = body;
            var host = new CloneHost(clone, pipeName, cfg, body, log: log);
            var run = Task.Run(() => host.RunAsync(kill.Token));
            if (ExitBeforeLinkCloses is { } hold)
            {
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                body.OnDisconnect = async () => { exited.TrySetResult(0); await Task.Delay(hold); };
                run = Task.WhenAny(run, exited.Task).Unwrap();
            }
            return new FakeProcess(Interlocked.Increment(ref _ids), run, kill);
        }

        public FakeBody Body(string clone) { lock (Bodies) return Bodies[clone]; }
    }

    private sealed class Hive : IAsyncDisposable
    {
        public HiveConfig Cfg { get; } = TestHive.Config();
        public ChatArbiter Arbiter { get; }
        public HiveSupervisor Sup { get; }
        public InProcLauncher Launcher { get; }
        public List<string> Notices { get; } = new();
        /// <summary>The supervisor's and the clones' log lines (timestamped), for a failure's output.</summary>
        public LineLog Log { get; } = new();

        public Hive(double launchGap = 0)
        {
            Cfg.LaunchGapSeconds = launchGap;
            Cfg.LaunchBatch = 1; // the b1.8.1 throttle (JoinPacerTests has the 1.12.2 batches)
            Cfg.DepositTimeoutSeconds = 5;
            Launcher = new InProcLauncher(Cfg, Log);
            Arbiter = new ChatArbiter("maid_chan", "maid_chan", Cfg.Clones());
            var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, Cfg.Budget, () => (0m, 0m));
            Sup = new HiveSupervisor(Cfg, "maid_chan", Launcher, new ClaimBoard(TimeSpan.FromSeconds(60)), ledger, Arbiter, null,
                () => new SlotFacts(20, ["maid_chan", "PoofimaFox"]), ["PoofimaFox"], Log);
            Sup.Notice += e => { lock (Notices) Notices.Add(e.Text); };
        }

        public string NoticeText() { lock (Notices) return string.Join(" || ", Notices); }
        public async ValueTask DisposeAsync()
        {
            try { await Sup.RecallAllAsync("test over", TimeSpan.FromSeconds(10)); } catch (Exception) { }
            await Sup.DisposeAsync();
        }
    }

    private static readonly TurnInfo Fox = new("PoofimaFox", ["PoofimaFox"], false);

    /// <summary>Collects log lines with a timestamp and the thread; <see cref="Text"/> for a failing test's output.</summary>
    private sealed class LineLog : Microsoft.Extensions.Logging.ILogger
    {
        private readonly List<string> _lines = new();
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"{_sw.Elapsed.TotalMilliseconds,8:0} [{Environment.CurrentManagedThreadId,3}] {logLevel}: {formatter(state, exception)}" + (exception != null ? " | " + exception.GetType().Name + ": " + exception.Message : "");
            lock (_lines) _lines.Add(line);
        }
        public string Text() { lock (_lines) return string.Join(Environment.NewLine, _lines); }
    }

    [Fact] // B6: 06:55:30 tree clone, 07:49:13 spar clones, 07:50:20 420 for the spar: all refused as "67 already owns..."
    public async Task A_second_clone_for_the_same_players_new_or_parallel_request_goes_ahead()
    {
        await using var h = new Hive();
        TurnContext.Current = Fox;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("Work the OTHER cell of the dual cobble generator all night; deposit cobble in chest 14,64,-5", "67"));
            // 06:55: the unnamed tree clone for the same player
            Assert.StartsWith("ok", h.Sup.Spawn("chop trees and replant all night; take food from chest 14,64,-5 first"));
            // 07:49: a spar needs two clones, both for PoofimaFox, while 67 is still on its job: no recall needed
            Assert.StartsWith("ok", h.Sup.Spawn("craft a stone sword and spar with 1337 inside the base, away from the edges", "420"));
            Assert.StartsWith("ok", h.Sup.Spawn("craft a stone sword and spar with 420 inside the base, away from the edges", "1337"));
            Assert.Equal(4, h.Sup.Slots.Count(s => s.Pending));
            Assert.DoesNotContain(h.Sup.Slots, s => s.Recalled);
        }
        finally { TurnContext.Current = null; }
    }

    [Fact] // smaller observation: two unnamed parallel spawns in one round (06:55:05 both landed on 67, two pids)
    public async Task Two_unnamed_parallel_spawns_take_different_clones()
    {
        await using var h = new Hive();
        TurnContext.Current = Fox;
        try
        {
            var go = new ManualResetEventSlim();
            var a = Task.Run(() => { go.Wait(); return h.Sup.Spawn("mine cobble at the west cell of the gen all night"); });
            var b = Task.Run(() => { go.Wait(); return h.Sup.Spawn("chop trees and replant all night"); });
            go.Set();
            var r = await Task.WhenAll(a, b);
            Assert.All(r, x => Assert.StartsWith("ok", x));
            Assert.Equal(2, h.Sup.Slots.Count(s => s.Pending));
            await Wait.Until(() => h.Launcher.Starts.Count == 2, 10, "two launches");
            Assert.Equal(2, h.Launcher.Starts.Select(s => s.Clone).Distinct().Count());
        }
        finally { TurnContext.Current = null; }
    }

    [Fact] // L2 still holds: an unnamed duplicate of the request a clone is TALKING about is refused; asking = refused even named
    public async Task A_clone_holding_the_players_conversation_keeps_that_request()
    {
        await using var h = new Hive();
        TurnContext.Current = Fox;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("build 2 more rows of planks on the west edge", "67"));
            await Wait.Until(() => h.Sup.Slot("maid_chan_67")!.Connected, 20, "67 up");
            h.Arbiter.Accept("PoofimaFox", "maid_chan_67");           // 67 is the player's sticky partner
            Assert.StartsWith("no: 67 is talking with PoofimaFox about this request", h.Sup.Spawn("build 2 more rows of cobble on the west edge"));
            h.Arbiter.OpenThread("PoofimaFox", "maid_chan_67", TimeSpan.FromSeconds(60)); // 67 asked "how far?"
            Assert.StartsWith("no: 67 is asking PoofimaFox about this request", h.Sup.Spawn("build 2 more rows of cobble on the west edge", "69"));
            // A different job for the same player is fine even now.
            Assert.StartsWith("ok", h.Sup.Spawn("chop trees and replant", "69"));
            // Explicit handover still moves it.
            Assert.StartsWith("ok", h.Sup.Spawn("build 2 more rows of cobble on the west edge", "420", handover: true));
            Assert.Contains("handed PoofimaFox's request from 67 to 420", h.NoticeText());
        }
        finally { TurnContext.Current = null; }
    }

    [Fact] // B13: 420 fell, main sent 67 as 69's partner, 69 kept "waiting at the base for 420 to log in"
    public async Task A_clone_joining_a_multi_clone_job_is_announced_to_the_others()
    {
        await using var h = new Hive();
        TurnContext.Current = Fox;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("craft a stone sword, then spar with 420 inside the base", "69"));
            await Wait.Until(() => h.Sup.Slot("maid_chan_69")!.Connected, 20, "69 up");
            Assert.StartsWith("ok", h.Sup.Spawn("craft a stone sword, then spar with 69 as its sparring partner inside the base", "67"));
            var b69 = h.Launcher.Body("maid_chan_69");
            await Wait.Until(() => { lock (b69.Calls) return b69.Calls.Any(c => c.StartsWith("note ")); }, 10, "69 told about 67");
            string note;
            lock (b69.Calls) note = b69.Calls.First(c => c.StartsWith("note "));
            output.WriteLine(note);
            Assert.Contains("67 is on your job too", note);
            Assert.Contains("420 is offline, so 67 takes that part", note);
            lock (b69.Calls) Assert.Single(b69.Calls, c => c.StartsWith("task ")); // its task wasn't replaced
        }
        finally { TurnContext.Current = null; }
    }

    [Fact] // B16: 07:19:20 "ok: recalling 69" + "ok: 69 got the new task" in one turn; the task was lost
    public async Task Recall_and_a_new_task_in_the_same_turn_log_the_clone_back_in_with_the_task()
    {
        await using var h = new Hive();
        TurnContext.Current = Fox;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("craft stone pickaxes from the chest's cobble", "69"));
            await Wait.Until(() => h.Sup.Slot("maid_chan_69")!.Connected, 20, "69 up");
        }
        finally { TurnContext.Current = null; }

        // The next turn: the brain sends both calls in parallel (here: task first, then recall).
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("craft maid_chan a new stone pickaxe and a spare, deliver it", "69"));
            var r = h.Sup.Recall("69");
            Assert.Contains("logs back in with the task given this turn", r);
            // ...and the other order: recall first, then a task for the clone that is logging out.
            Assert.StartsWith("ok", h.Sup.Spawn("craft maid_chan a new stone pickaxe and a spare, deliver it", "69"));
        }
        finally { TurnContext.Current = null; }
        try
        {
            await Wait.Until(() => h.Launcher.Starts.Count(s => s.Clone == "maid_chan_69") == 2, 20, "69 relaunched");
            var s69 = h.Sup.Slot("maid_chan_69")!;
            await Wait.Until(() => s69.Connected, 20, "69 back");
            Assert.True(s69.Pending);
            Assert.Contains("new stone pickaxe", s69.Task!.Text);
        }
        catch (Exception)
        {
            output.WriteLine($"starts: {string.Join(", ", h.Launcher.Starts.Select(s => s.Clone + "@" + s.At.ToString("HH:mm:ss.fff")))}");
            output.WriteLine($"slot: {h.Sup.Slot("maid_chan_69")?.State} connected={h.Sup.Slot("maid_chan_69")?.Connected} notices: {h.NoticeText()}");
            output.WriteLine(h.Log.Text());
            throw;
        }
    }

    [Fact] // the flake of the test above, made deterministic: main saw the process exit before the link closed
    public async Task A_logout_seen_as_the_process_exit_first_still_relaunches_with_the_queued_task()
    {
        await using var h = new Hive();
        h.Launcher.ExitBeforeLinkCloses = TimeSpan.FromMilliseconds(600);
        TurnContext.Current = Fox;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("craft stone pickaxes from the chest's cobble", "69"));
            await Wait.Until(() => h.Sup.Slot("maid_chan_69")!.Connected, 20, "69 up");
        }
        finally { TurnContext.Current = null; }
        TurnContext.Current = new TurnInfo("PoofimaFox", ["PoofimaFox"], false);
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("craft maid_chan a new stone pickaxe and a spare, deliver it", "69"));
            Assert.Contains("logs back in with the task given this turn", h.Sup.Recall("69"));
        }
        finally { TurnContext.Current = null; }
        var s69 = h.Sup.Slot("maid_chan_69")!;
        try
        {
            await Wait.Until(() => h.Launcher.Starts.Count(s => s.Clone == "maid_chan_69") == 2, 10, "69 relaunched");
            // Disposing the old link raised its Closed inside the (re-entrant) slot lock: that nested exit relaunched 69, then the
            // outer one killed the new process and left the slot Idle.
            await Wait.Until(() => s69.Connected, 10, "69 back");
            await Task.Delay(TimeSpan.FromMilliseconds(800)); // past the old link's close
            Assert.True(s69.Connected);
            Assert.Equal(CloneState.Running, s69.State);
            Assert.Contains("new stone pickaxe", s69.Task!.Text);
            Assert.Equal(2, h.Launcher.Starts.Count(s => s.Clone == "maid_chan_69"));
        }
        catch (Exception)
        {
            output.WriteLine($"slot: {s69.State} connected={s69.Connected} notices: {h.NoticeText()}");
            output.WriteLine(h.Log.Text());
            throw;
        }
    }

    [Fact] // B19: two clones launched in the same millisecond hit the server's login throttle
    public async Task Clone_launches_are_spaced_for_the_server_login_throttle()
    {
        await using var h = new Hive(launchGap: 1.5);
        TurnContext.Current = Fox;
        try
        {
            Assert.StartsWith("ok", h.Sup.Spawn("craft a stone sword and spar with 420", "69"));
            Assert.StartsWith("ok", h.Sup.Spawn("craft a stone sword and spar with 69", "420"));
        }
        finally { TurnContext.Current = null; }
        await Wait.Until(() => h.Launcher.Starts.Count == 2, 10, "both launched");
        var gap = h.Launcher.Starts[1].At - h.Launcher.Starts[0].At;
        output.WriteLine($"gap {gap.TotalSeconds:0.00}s");
        Assert.True(gap >= TimeSpan.FromSeconds(1.4), $"launches only {gap.TotalSeconds:0.00}s apart");
    }

    [Fact] // L7: a config error (exit 2) was retried 3x as a crash
    public async Task A_config_error_exit_is_not_retried()
    {
        await using var h = new Hive();
        h.Launcher.ExitCode = HiveSupervisor.ConfigErrorExitCode;
        Assert.StartsWith("ok", h.Sup.Spawn("mine cobble", "67"));
        var s = h.Sup.Slot("maid_chan_67")!;
        await Wait.Until(() => s.State == CloneState.Retired, 10, "retired");
        Assert.Single(h.Launcher.Starts);
        Assert.Contains("configuration error", h.NoticeText());
    }

    [Fact] // L5: live2 00:52:46 deposit from inside the gen hole: "can't reach chest", then Fox's pickaxe tossed at main
    public async Task An_unreachable_chest_makes_the_clone_step_out_and_retry_before_any_handoff()
    {
        var port = new HoleBoundPort();
        var handoff = new CountingHandoff();
        var r = await new DepositProcedure(port, NullClaimBoard.Instance, "hive_storage_67", handoff) { RetryDelay = TimeSpan.FromMilliseconds(10) }
            .RunAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(r.Empty, r.Text);
        Assert.Equal(1, port.StepOuts);
        Assert.Equal(0, handoff.Offers);                         // the pickaxe went into storage, not to main's feet
        Assert.Contains("stepped out to reach storage", r.Text);
    }

    private sealed class HoleBoundPort : IInventoryPort
    {
        private readonly List<ItemMsg> _items = [new(270, 0, 1, "wooden_pickaxe"), new(4, 0, 12, "cobblestone")];
        private bool _out;
        public int StepOuts;
        public IReadOnlyList<ItemMsg> Items() { lock (_items) return _items.ToList(); }
        public bool CanActSafely => true;
        public IReadOnlyList<StorageChest> Chests() => [new StorageChest(new BlockPos(14, 64, -5), 20, 9)];
        public Task<ActionResult> DepositToAsync(BlockPos chest, CancellationToken ct)
        {
            if (!_out) return Task.FromResult(ActionResult.Fail("can't reach chest"));
            lock (_items) _items.Clear();
            return Task.FromResult(ActionResult.Success("deposited 2 stacks", 2));
        }
        public Task<bool> StepOutAsync(CancellationToken ct) { StepOuts++; _out = true; return Task.FromResult(true); }
        public Task<ActionResult> TossAllAtAsync(Vec3d at, CancellationToken ct) => Task.FromResult(ActionResult.Fail("no"));
        public Task<(ActionResult Result, BlockPos? Chest)> PlaceLabelledChestAsync(string label, CancellationToken ct) =>
            Task.FromResult<(ActionResult, BlockPos?)>((ActionResult.Fail("no chest item"), null));
    }

    private sealed class CountingHandoff : IHandoffTarget
    {
        public int Offers;
        public Task<Vec3d?> OfferAsync(int items, CancellationToken ct) { Offers++; return Task.FromResult<Vec3d?>(new Vec3d(0, 64, 0)); }
        public Task<int> ConfirmAsync(CancellationToken ct) => Task.FromResult(0);
    }
}
