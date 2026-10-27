using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Supervisor;
using MineClaw.Protocol;
using MineClaw.Protocol.Packets;
using MineClaw.Protocol.Recording;
using MineClaw.Replay;

namespace MineClaw.Hive.Tests;

/// <summary>
/// The first live hive test (logs/live-hive, 2026-10-26): "cut down a tree" went to main but 67 answered too, "both of you"
/// was never split, both bots reacted to "kiwi fell out of the world", 67 narrated hive status. These pin the fixes.
/// </summary>
public class LiveHiveChatTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Main = "maid_chan", Clone = "maid_chan_67";
    private static readonly string[] MainNames = ["claudechan", "claude chan", "claude", "maid-chan", "maid chan", "maid", Main];
    private static readonly string[] Clones = [Clone, "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>One hive process' chat side: router + queue + arbiter, wired like MainHive (main) or ClonePolicy (clone).</summary>
    private sealed class Proc
    {
        public required ChatRouter Chat;
        public required EventQueue Queue;
        public required ChatArbiter Arbiter;
        public required CommandGate Gate;

        /// <summary>Feeds a raw line; returns the waking events it produced (taken from the queue).</summary>
        public List<GameEvent> Feed(string raw)
        {
            Chat.OnServerChat(raw);
            return Queue.TryTakeBatch()?.Waking.ToList() ?? [];
        }
    }

    private static Proc Build(string self, TimeProvider time)
    {
        bool isMain = self == Main;
        var arb = new ChatArbiter(self, Main, Clones, mainNames: MainNames, time: time);
        var num = ChatArbiter.NumberOf(self);
        var opts = new ChatOptions
        {
            Username = self, LineDelayMs = 0,
            DisplayName = isMain ? "claudechan" : "claudechan " + num,
            Aliases = isMain ? ["claude chan", "claude", "maid-chan", "maid chan", "maid"] : ["claudechan " + num],
        };
        var q = new EventQueue(new EventQueueOptions { CoalesceWindowMs = 1, MaxBatchDelayMs = 1 }, time);
        var chat = new ChatRouter(opts, q, (_, _) => Task.CompletedTask, time);
        var gate = new CommandGate(new CommandOptions(), () => ["kiwi", "poofimafox"], time);
        chat.Commands = gate;
        if (isMain) { chat.IsHiveBot = arb.IsHiveBot; chat.Arbiter = arb.Decide; }
        else ClonePolicy.Apply(chat, q, arb);
        chat.SetOnlinePlayers(["kiwi", Main, Clone]);
        return new Proc { Chat = chat, Queue = q, Arbiter = arb, Gate = gate };
    }

    /// <summary>Main + live clone 67 with the roster/sticky sync main pushes over IPC.</summary>
    private static (Proc Main, Proc Clone) Pair(TimeProvider time)
    {
        var m = Build(Main, time);
        var c = Build(Clone, time);
        m.Arbiter.Changed += () => c.Arbiter.Apply(m.Arbiter.ToRoster());
        c.Chat.Accepted += msg => { if (msg.Player != null) m.Arbiter.Accept(msg.Player, Clone); }; // chat_ok
        m.Arbiter.SetLive([Clone], MainNames);
        return (m, c);
    }

    // ---------------------------------------------------------------- arbiter

    [Fact]
    public void Group_asks_go_to_main_and_main_vocative_breaks_a_clone_sticky()
    {
        var (m, c) = Pair(TimeProvider.System);
        foreach (var text in new[] { "both of you", "both of you wave pls", "everyone come here", "all maids follow me", "you two, chop trees" })
        {
            var msg = m.Chat.Parse("<kiwi> " + text);
            Assert.Equal(new ChatRoute(Main, true, true), m.Arbiter.Route(msg));
            Assert.True(m.Arbiter.IsGroupAsk(msg));
            Assert.False(c.Arbiter.Decide(msg, true));
        }
        // No clone online: a group word is just main's ordinary chat.
        var solo = Build(Main, TimeProvider.System);
        Assert.False(solo.Arbiter.IsGroupAsk(solo.Chat.Parse("<kiwi> both of you")));

        Assert.Single(c.Feed("<kiwi> 67 i'm here!"));                  // 67 now sticky for kiwi
        Assert.Equal(Clone, m.Arbiter.OwnerOf(m.Chat.Parse("<kiwi> no, from above")));
        Assert.Equal(new ChatRoute(Main, true, false), m.Arbiter.Route(m.Chat.Parse("<kiwi> main do a little hop")));
        Assert.Empty(c.Feed("<kiwi> main do a little hop"));
        Assert.False(ChatArbiter.IsGroupAddress("the main island is both big and pretty"));
    }

    [Fact]
    public void Clone_never_executes_an_authorization_addressed_to_main()
    {
        var (m, c) = Pair(TimeProvider.System);
        const string line = "<kiwi> claudechan run /time set day";
        m.Chat.OnServerChat(line);
        c.Chat.OnServerChat(line);
        Assert.False(c.Gate.Authorize("/time set day").Ok);
        Assert.True(m.Gate.Authorize("/time set day").Ok);
        // Addressed to the clone by number: the clone may run it, main may not.
        m.Chat.OnServerChat("<kiwi> 67 run /weather clear");
        c.Chat.OnServerChat("<kiwi> 67 run /weather clear");
        Assert.False(m.Gate.Authorize("/weather clear").Ok);
        Assert.True(c.Gate.Authorize("/weather clear").Ok);
    }

    [Fact]
    public void Clone_speech_gate_only_when_addressed_or_a_task_ack_and_never_hive_status()
    {
        var addressed = new EventBatch([GameEvent.Chat("kiwi", "67 come w me")]);
        Assert.Equal("yes ma'am, right behind you uwu", ClonePolicy.Speech(addressed, "yes ma'am, right behind you uwu", false));
        Assert.Equal("omw robin.", ClonePolicy.Speech(addressed, "omw robin. My hive task isn't marked done yet.", false));
        var task = new EventBatch([GameEvent.Notice(ClonePolicy.TaskTag + " say hi to whoever is online, then wait — take what you need", EventPriority.Normal)]);
        Assert.Equal("Hi robin!", ClonePolicy.Speech(task, "Hi robin! maid-chan 67 reporting for duty", false));
        Assert.Null(ClonePolicy.Speech(task, "I said hi and I'm waiting near the middle now.", true)); // one ack, once
        var quietTask = new EventBatch([GameEvent.Notice(ClonePolicy.TaskTag + " chop the second tree by the cobble gen", EventPriority.Normal)]);
        Assert.Null(ClonePolicy.Speech(quietTask, "on it!", false));
        var noticed = new EventBatch([GameEvent.TaskFail("#2 chop_tree: 5 log(s) wouldn't break")]);
        Assert.Null(ClonePolicy.Speech(noticed, "sowwy robin, none of the logs broke", false));
        Assert.Null(ClonePolicy.Intake(GameEvent.Activity("kiwi hit the ground too hard at the unknown build", "kiwi")));
        Assert.Null(ClonePolicy.Intake(GameEvent.Joined("kiwi")));
        Assert.NotNull(ClonePolicy.Intake(GameEvent.VoidFall(10)));
    }

    // ---------------------------------------------------------------- replay of the live recordings

    private static string Fixture(string file)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (; d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "tests", "fixtures", "live-hive", file);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("tests/fixtures/live-hive/" + file);
    }

    private static IEnumerable<(DateTimeOffset At, string Who, string Line)> ChatOf(string file, string who)
    {
        var rec = PacketRecording.Load(Fixture(file));
        foreach (var f in rec.Frames)
        {
            if (f.Direction != PacketDirection.ClientBound) continue;
            IPacket p;
            try { p = f.Parse(); } catch (ProtocolException) { continue; }
            if (p is ChatPacket c) yield return (rec.Start + TimeSpan.FromMilliseconds(f.OffsetMs), who, c.Message);
        }
    }

    [Fact]
    public void Live_recordings_replayed_give_each_player_line_exactly_one_handler()
    {
        // Both processes' real server chat, merged on the wall clock (the clone joined 20 s after main).
        var timeline = ChatOf("main-maid_chan.mcrec", Main).Concat(ChatOf("clone-maid_chan_67.mcrec", Clone)).OrderBy(x => x.At).ToList();
        var clock = new Clock(timeline[0].At);
        var (m, c) = Pair(clock);
        var handled = new Dictionary<string, List<string>>();
        bool cloneOnline = false;
        foreach (var (at, who, raw) in timeline)
        {
            clock.Now = at;
            var line = ChatText.StripColors(raw);
            if (who == Main && line.StartsWith(Clone + " joined")) cloneOnline = true;
            var p = who == Main ? m : c;
            if (p == c && !cloneOnline) continue;
            var woke = p.Feed(raw);
            if (!line.StartsWith("<kiwi>")) { Assert.Empty(woke); continue; } // system lines, bots' own chat: nobody wakes
            var text = line["<kiwi> ".Length..];
            if (!handled.TryGetValue(text, out var who2)) handled[text] = who2 = [];
            if (woke.Count > 0) who2.Add(who);
        }
        foreach (var (text, who) in handled) output.WriteLine($"{text,-32} -> {string.Join(",", who)}");

        Assert.Contains("chop down one tree pls", handled.Keys);
        foreach (var (text, who) in handled) Assert.True(who.Count <= 1, $"'{text}' handled by {string.Join(",", who)}");
        Assert.Equal([Main], handled["chop down one tree pls"]);
        Assert.Equal([Main], handled["both of you"]);
        Assert.Equal([Main], handled["both of you wave pls"]);
        Assert.Equal([Clone], handled["67 come w me"]);
        Assert.Equal([Clone], handled["67 i'm here!"]);
        Assert.Equal([Main], handled["main do a little hop"]);
        // The clone only ever woke for lines naming it or its own follow-ups right after.
        var cloneLines = handled.Where(kv => kv.Value.Contains(Clone)).Select(kv => kv.Key).ToList();
        Assert.All(cloneLines, t => Assert.Contains(t, new[] { "67 come w me", "67 i'm here!", "no", "from above", "climb to the top" }));
    }

    [Fact]
    public void Live_clone_recording_world_noticing_never_reaches_the_clone_brain()
    {
        // The clone was woken by [noticed] events (new builds, "kiwi hit the ground too hard", "kiwi is falling into the void").
        var replay = ReplayHarness.Run(Fixture("clone-maid_chan_67.mcrec"), new ReplayOptions { SelfName = Clone });
        var wakes = replay.Timeline.Where(e => e.Source == "wake").ToList();
        output.WriteLine($"{wakes.Count} wake events in the clone recording");
        var q = new EventQueue();
        q.Intake = ClonePolicy.Intake;
        foreach (var w in wakes) q.Post(GameEvent.Activity(w.Text, w.Player, EventPriority.Normal));
        foreach (var w in wakes.Where(w => w.Priority == "high")) q.Post(GameEvent.Activity(w.Text, w.Player, EventPriority.High));
        Assert.False(q.HasWaking);
        Assert.Equal(0, q.Count);
    }

    // ---------------------------------------------------------------- real processes

    [Fact]
    public async Task Real_clone_process_wakes_only_for_its_name_and_main_tasks()
    {
        await using var h = new TestHive(mode: "chat");
        var main = Build(Main, TimeProvider.System);
        main.Chat.IsHiveBot = h.Arbiter.IsHiveBot;
        main.Chat.Arbiter = h.Arbiter.Decide;
        main.Chat.SetOnlinePlayers(["kiwi"]); // as live: kiwi was the only human online
        h.Arbiter.SetLive([], MainNames);
        var slot = h.Supervisor.Slot(Clone)!;
        string Turns() { lock (h.Notices) return string.Join(" || ", h.Notices.Select(n => n.Text).Where(t => t.Contains("TURN") || t.Contains("CMD"))); }
        int TurnCount() => Turns().Split("TURN", StringSplitOptions.None).Length - 1;

        // The spawn task asks it to say hi: one ack allowed.
        Assert.StartsWith("ok", h.Supervisor.Spawn("say hi to whoever is online, then stay near the island center and wait for orders", "67"));
        await Wait.Until(() => slot.IsLive && h.Arbiter.Live.Contains(Clone), 30, "clone live");
        await Wait.Until(() => TurnCount() >= 1, 20, "task turn");
        Assert.Contains("TURN SAY: [note] [hive task] say hi", Turns());
        await Task.Delay(500); // roster push reaches the clone

        // Player lines: main and the clone each see the same line (main forwards it to the clone over IPC).
        async Task<List<GameEvent>> Say(string raw)
        {
            await h.Supervisor.InjectAsync(raw);
            return main.Feed(raw);
        }
        Assert.NotEmpty(await Say("<kiwi> chop down one tree pls"));
        var group = await Say("<kiwi> both of you");
        Assert.NotEmpty(group);
        Assert.True(h.Arbiter.IsGroupAsk(main.Chat.Parse("<kiwi> both of you")));
        // Its task never reports a result: pending, so main has nothing to report about it yet (live 2: no fabricated results).
        Assert.Equal("[hive: 67 saying (pending)] pending = no result yet, don't report it group ask: split it, hive spawn name+task for the clone's part; reply once",
            h.Supervisor.HintLine(groupAsk: true));
        Assert.Empty(await Say("kiwi fell out of the world"));
        Assert.Empty(await Say("kiwi hit the ground too hard"));
        Assert.Empty(await Say("<kiwi> 67 come w me"));          // main stays out of it
        await Wait.Until(() => TurnCount() >= 2, 20, "clone turn for its name");
        var turns = Turns();
        Assert.Contains("TURN SAY: [chat] kiwi: 67 come w me", turns);
        foreach (var bad in new[] { "cut down a tree", "both of you", "fell out of the world", "hit the ground" })
            Assert.DoesNotContain(bad, turns);

        // Main splits the group ask: the clone gets its part as an IPC task and acts without chatting.
        Assert.StartsWith("ok: 67 got the new task", h.Supervisor.Spawn("chop the second tree by the cobble gen", "67"));
        await Wait.Until(() => TurnCount() >= 3, 20, "delegated task turn");
        Assert.Contains("TURN MUTE: [note] [hive task] chop the second tree", Turns());

        // An exact command addressed to main never authorises the clone.
        Assert.NotEmpty(await Say("<kiwi> claudechan run /time set day"));
        await Wait.Until(() => Turns().Contains("CMD"), 20, "clone command check");
        Assert.Contains("CMD refused", Turns());
        Assert.True(main.Gate.Authorize("/time set day").Ok);
        await Task.Delay(800);
        Assert.Equal(3, TurnCount());
        output.WriteLine(Turns());

        Assert.StartsWith("ok: recalling 67", h.Supervisor.Recall("67"));
        await Wait.Until(() => slot.State == CloneState.Idle, 30, "logout");
    }
}
