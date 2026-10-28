using System.Text.Json;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Hive.Chat;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Slots;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Main's whitelist path as production wires it: a real CommandGate (the scoped exception) in front of a recording sender, plus
/// a hierarchy over fake bodies that log what the supervisor would do (deposit before logout, then gone).
/// </summary>
public sealed class HierarchyKit
{
    public const string Main = "maid_chan";
    public HiveAccountsConfig Cfg { get; } = new();
    public ManagedAccounts Managed { get; }
    public CommandGate Gate { get; } = new(new CommandOptions(), () => ["poofimafox"]);
    public List<string> Sent { get; } = [];
    public List<string> Log { get; } = [];
    public WhitelistManager Whitelist { get; }
    public HiveHierarchy H { get; }
    public FakeBodies Bodies { get; }
    public List<string> Online { get; } = [Main, "poofimafox", "kiwi"];
    public List<string> Known { get; } = [];
    public int MaxPlayers { get; set; } = 20;

    public HierarchyKit(string? path = null, int maxClones = 0)
    {
        Managed = new ManagedAccounts(path);
        Whitelist = new WhitelistManager(Managed, Cfg, Main, Gate.ArmManagedWhitelist, (line, _) =>
        {
            bool ok = Gate.AllowOutgoing(line);
            lock (Log) { if (ok) { Sent.Add(line); Log.Add(line); } }
            return Task.FromResult(ok);
        });
        Gate.ManagedAccount = Whitelist.Accepts;
        H = new HiveHierarchy(Cfg, Main, Managed, Whitelist, maxClones, () => ["poofimafox", "kiwi"], () => Known);
        Bodies = new FakeBodies(this);
        H.Bodies = Bodies;
    }

    public void Note(string s) { lock (Log) Log.Add(s); }
    public List<string> LogCopy() { lock (Log) return Log.ToList(); }
}

/// <summary>Stands in for the supervisor: allocates through the hierarchy, logs launches, deposits, logouts.</summary>
public sealed class FakeBodies(HierarchyKit kit) : IHiveBodies
{
    private readonly Dictionary<string, TaskCompletionSource> _gone = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, string Task, HiveSpawnOptions Opts)> Spawns { get; } = [];
    public HashSet<string> Live { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>How long a recalled body takes to deposit before it logs out.</summary>
    public TimeSpan DepositTime { get; set; } = TimeSpan.FromMilliseconds(30);

    public string Spawn(string task, string? name, bool handover, HiveSpawnOptions options)
    {
        string acc;
        if (name != null) acc = kit.H.Resolve(name) ?? name;
        else
        {
            var (n, why) = kit.H.Allocate(options);
            if (n == null) return "no: " + why;
            acc = n;
        }
        lock (Spawns) Spawns.Add((acc, task, options));
        var t = new TaskMsg("t" + Spawns.Count, task, Profile: options.Profile, Script: options.Script, Kind: options.Kind);
        kit.H.Assigned(acc, t, options);
        if (Live.Add(acc))
        {
            kit.H.BeforeLaunch(acc);
            kit.Note("launch " + acc);
            lock (_gone) _gone[acc] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return $"ok: {HiveNode.ShortOf(acc)} spawning";
    }

    public string Recall(string name, string reason)
    {
        var acc = kit.H.Resolve(name) ?? name;
        if (!Live.Contains(acc)) return "no: not out";
        _ = Task.Run(async () =>
        {
            kit.Note("deposit " + acc);
            await Task.Delay(DepositTime);
            kit.Note("logout " + acc);
            Live.Remove(acc);
            kit.H.Released(acc, reason); // the supervisor's OnGone → FinallyReleased
            TaskCompletionSource? g;
            lock (_gone) _gone.TryGetValue(acc, out g);
            g?.TrySetResult();
        });
        return "ok: recalling " + HiveNode.ShortOf(acc);
    }

    public async Task WaitGoneAsync(string name, TimeSpan timeout)
    {
        TaskCompletionSource? g;
        lock (_gone) _gone.TryGetValue(name, out g);
        if (g != null) await Task.WhenAny(g.Task, Task.Delay(timeout));
    }

    public IReadOnlyCollection<string> Running() => Live.ToList();
    public SlotFacts Slots() { lock (kit.Online) return new SlotFacts(kit.MaxPlayers, [.. kit.Online, .. Live]); }
    public int Pending() => 0;
    public string List() => string.Join(" | ", Live.Select(HiveNode.ShortOf));
}

public class HiveHierarchyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static AllocationFacts Facts(int max = 20, string[]? online = null, string[]? known = null, string[]? running = null, string[]? regulars = null) =>
        new(new SlotFacts(max, online ?? ["maid_chan", "poofimafox", "kiwi"]), regulars ?? ["poofimafox", "kiwi"], known ?? [], running ?? []);

    // ================================================================ allocator

    [Fact]
    public void Allocator_names_follow_the_rules_and_skip_quantity_like_ids()
    {
        var cfg = new HiveAccountsConfig();
        var managed = new ManagedAccounts(null);
        var a = new AccountAllocator(cfg, managed, "maid_chan");
        var first = a.Allocate(Facts());
        Assert.Equal("maid_chan_101", first.Name);
        Assert.True(first.Name!.Length <= 16);
        managed.MarkActive("maid_chan_101", "hive", "opus", "main");
        Assert.Equal("maid_chan_102", a.Allocate(Facts(running: ["maid_chan_101"])).Name);
        // 104 = 8×13, 110 = 10×11, 125 = 25×5: a player could read them as a quantity ("104 cobble"), so they're skipped.
        Assert.True(AccountNames.QuantityLike(104) && AccountNames.QuantityLike(110) && AccountNames.QuantityLike(125) && AccountNames.QuantityLike(64));
        Assert.False(AccountNames.QuantityLike(101));
        Assert.True(AccountNames.IsManagedShape("maid_chan_101", cfg, "maid_chan"));
        Assert.False(AccountNames.IsManagedShape("maid_chan", cfg, "maid_chan"));          // main itself, never
        Assert.False(AccountNames.IsManagedShape("PoofimaFox", cfg, "maid_chan"));         // not ours
        Assert.False(AccountNames.IsManagedShape("maid_chan_1234567", cfg, "maid_chan"));  // 17 chars
        Assert.False(AccountNames.IsManagedShape("maid_chan_10 1", cfg, "maid_chan"));     // illegal char
        // A prefix that leaves no room for a 16-char name: nothing allocated.
        var tight = new AccountAllocator(new HiveAccountsConfig { Prefix = "maid_chan_hives_", FirstId = 1 }, new ManagedAccounts(null), "maid_chan");
        Assert.Null(tight.Allocate(Facts()).Name);
    }

    [Fact]
    public void Allocator_never_collides_with_players_and_reuses_released_accounts_first()
    {
        var managed = new ManagedAccounts(null);
        var a = new AccountAllocator(new HiveAccountsConfig(), managed, "maid_chan");
        // Someone is on the server as maid_chan_101, and maid_chan_102 was seen in chat: both belong to someone else.
        var r = a.Allocate(Facts(online: ["maid_chan", "maid_chan_101", "poofimafox", "kiwi"], known: ["maid_chan_102"]));
        Assert.Equal("maid_chan_103", r.Name);
        // A released managed account is reused before a fresh one (its saved spot and history stay useful).
        managed.MarkActive("maid_chan_107", "hive", "opus", "main");
        managed.MarkReleased("maid_chan_107");
        var re = a.Allocate(Facts());
        Assert.Equal(("maid_chan_107", true), (re.Name, re.Reused));
        // ...unless someone else is on it right now.
        Assert.NotEqual("maid_chan_107", a.Allocate(Facts(online: ["maid_chan", "maid_chan_107"])).Name);
    }

    [Fact]
    public void Allocator_respects_the_player_cap_the_regulars_and_max_clones()
    {
        var a = new AccountAllocator(new HiveAccountsConfig(), new ManagedAccounts(null), "maid_chan", maxClones: 2);
        // 7 slots, 4 online, 2 regulars offline: one more clone would leave 2 free for the 2 offline regulars.
        var ok = a.Allocate(Facts(max: 7, online: ["maid_chan", "a", "b", "c"], regulars: ["poofimafox", "kiwi"]));
        Assert.True(ok.Ok, ok.Error);
        var full = a.Allocate(Facts(max: 7, online: ["maid_chan", "a", "b", "c", "d"], regulars: ["poofimafox", "kiwi"]));
        Assert.False(full.Ok);
        Assert.Contains("player cap", full.Error);
        var cap = a.Allocate(Facts(running: ["maid_chan_101", "maid_chan_102"]));
        Assert.Contains("Hive:MaxClones", cap.Error);
    }

    // ================================================================ whitelist: only main, only managed names

    [Fact]
    public async Task Whitelist_goes_only_through_main_and_only_for_managed_accounts()
    {
        var k = new HierarchyKit();
        // The model can never whitelist, even with a trusted player's exact line: /whitelist stays blocked for run_command.
        k.Gate.Observe(new ChatMessage(ChatKind.Whisper, "poofimafox", "/whitelist add maid_chan_101", "poofimafox whispers: /whitelist add maid_chan_101"), true, true);
        Assert.False(k.Gate.Authorize("/whitelist add maid_chan_101").Ok);
        Assert.False(k.Gate.AllowOutgoing("/whitelist add maid_chan_101"));
        // Not managed yet: the manager refuses, and so does the gate's exception.
        Assert.False(await k.Whitelist.AddAsync("maid_chan_101"));
        Assert.False(k.Gate.ArmManagedWhitelist("/whitelist add maid_chan_101"));
        k.Managed.Upsert("maid_chan_101", a => a);
        Assert.True(await k.Whitelist.AddAsync("maid_chan_101"));
        Assert.Equal(["/whitelist add maid_chan_101"], k.Sent);
        // Armed once = sent once; other shapes, other names and main are never armed.
        Assert.False(k.Gate.AllowOutgoing("/whitelist add maid_chan_101"));
        Assert.False(k.Gate.ArmManagedWhitelist("/whitelist off"));
        Assert.False(k.Gate.ArmManagedWhitelist("/whitelist add poofimafox"));
        Assert.False(k.Gate.ArmManagedWhitelist("/whitelist remove maid_chan"));
        Assert.False(k.Gate.ArmManagedWhitelist("/whitelist add maid_chan_101 extra"));
        Assert.False(await k.Whitelist.RemoveAsync("maid_chan"));
        Assert.True(k.Managed.Get("maid_chan_101")!.Whitelisted);
    }

    [Fact]
    public async Task A_hive_asks_main_over_the_bus_and_only_for_its_own_clones()
    {
        var k = new HierarchyKit();
        k.H.Spawn(new SpawnRequest(Task: "mine cobble"));                 // 101: main's hive
        var bus = new HiveBus("maid_chan") { MainHandler = k.H.HandleBusAsync };
        var tok101 = bus.Register("maid_chan_101", (_, _) => Task.FromResult(new BusReply(true)));
        var tok103 = bus.Register("maid_chan_103", (_, _) => Task.FromResult(new BusReply(true)));
        var r = await bus.RouteAsync(BusEnvelope.Make(BusTypes.Spawn, "maid_chan_101", "main", new SpawnReq(Kind: "script", Script: "cobble_loop"), tok101), "maid_chan_101");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("maid_chan_101", k.H.Tree.Get("maid_chan_102")!.Owner);
        // 101 may ask main to (re)whitelist its own child; 103 (not in the tree, not the owner) may not, nor touch main's 101.
        k.Managed.SetWhitelisted("maid_chan_102", false);
        Assert.True((await bus.RouteAsync(BusEnvelope.Make(BusTypes.Whitelist, "maid_chan_101", "main", new WhitelistReq("add", "102"), tok101), "maid_chan_101")).Ok);
        Assert.False((await bus.RouteAsync(BusEnvelope.Make(BusTypes.Whitelist, "maid_chan_103", "main", new WhitelistReq("remove", "101"), tok103), "maid_chan_103")).Ok);
        Assert.False((await bus.RouteAsync(BusEnvelope.Make(BusTypes.Whitelist, "maid_chan_101", "main", new WhitelistReq("add", "poofimafox"), tok101), "maid_chan_101")).Ok);
        Assert.DoesNotContain(k.Sent, l => l.Contains("poofimafox") || l.Contains("remove maid_chan_101"));
    }

    // ================================================================ reconcile after a crash

    [Fact]
    public async Task Startup_reconcile_removes_stale_managed_accounts_and_adopts_the_old_roster()
    {
        var path = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"), "hive-accounts.json");
        {
            // Before the crash: two clones out (whitelisted, active), one released properly.
            var before = new HierarchyKit(path);
            foreach (var n in new[] { "maid_chan_101", "maid_chan_102", "maid_chan_103" }) before.Managed.MarkActive(n, "hive", "opus", "main");
            before.Managed.SetWhitelisted("maid_chan_101", true);
            before.Managed.SetWhitelisted("maid_chan_102", true);
            before.Managed.MarkReleased("maid_chan_103");
        }
        // Main restarts (the file survives; a memory reset wouldn't touch it). 102 is somehow still running (a body that survived).
        var k = new HierarchyKit(path);
        int removed = await k.H.StartAsync(["maid_chan_67", "maid_chan_69", "poofimafox"], n => n == "maid_chan_102");
        Assert.Contains("/whitelist remove maid_chan_101", k.Sent);
        Assert.DoesNotContain("/whitelist remove maid_chan_102", k.Sent);
        Assert.DoesNotContain(k.Sent, l => l.Contains("maid_chan_103"));            // never whitelisted
        // The old fixed names were whitelisted by hand: adopted, then taken off (the empty state is main alone).
        Assert.Contains("/whitelist remove maid_chan_67", k.Sent);
        Assert.Contains("/whitelist remove maid_chan_69", k.Sent);
        Assert.DoesNotContain(k.Sent, l => l.Contains("poofimafox"));
        Assert.Equal(3, removed);
        Assert.False(k.Managed.Get("maid_chan_101")!.Active);
        Assert.True(new ManagedAccounts(path).Get("maid_chan_67")!.Adopted); // persisted
    }

    [Fact]
    public async Task Empty_state_is_main_alone_and_nothing_assumes_clone_accounts()
    {
        var k = new HierarchyKit();
        Assert.Equal(0, await k.H.StartAsync([], _ => false));
        Assert.Empty(k.Sent);
        Assert.Empty(k.Managed.All());
        Assert.Equal("main alone", k.H.Tree.Render());
        Assert.Equal("", k.H.List());
        var cfg = new HiveConfig { CloneUsernames = ["maid_chan_67"] };
        Assert.Equal(["maid_chan_67"], cfg.IgnoreLegacyRoster());
        Assert.Empty(cfg.Clones()); // old configs load, their names are never slots
    }

    // ================================================================ kinds, ownership, release cascade

    [Fact]
    public void Spawn_of_each_kind()
    {
        var k = new HierarchyKit();
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "opus", Task: "chop trees")));
        var (acc, _, opts) = k.Bodies.Spawns[^1];
        Assert.Equal(("maid_chan_101", null, "hive", "main"), (acc, opts.Profile, opts.Kind, opts.Owner)); // Opus = the clone's own 5.5 brain
        Assert.Equal(BrainKind.Opus, k.H.Tree.Get(acc)!.Brain);

        // DeepSeek: available only with a usable Ollama profile; otherwise a clear "not available".
        Assert.Contains("brain deepseek not available", k.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "x")));
        k.H.ProfileCheck = p => p == "deepseek" ? null : "no profile";
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Brain: "deepseek", Task: "sort chests")));
        Assert.Equal(("deepseek", "hive"), (k.Bodies.Spawns[^1].Opts.Profile, k.Bodies.Spawns[^1].Opts.Kind));
        Assert.Equal(BrainKind.DeepSeek, k.H.Tree.Get(k.Bodies.Spawns[^1].Name)!.Brain);

        // Script-only: needs a script; the kind defaults to script when one is given.
        Assert.StartsWith("error: kind script needs", k.H.Spawn(new SpawnRequest(Kind: "script", Task: "x")));
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Script: "cobble_loop")));
        Assert.Equal(CloneKind.Script, k.H.Tree.Get(k.Bodies.Spawns[^1].Name)!.Kind);

        // Subhive: only where a body host exists (inproc); here it says so, then works with one.
        Assert.Contains("subhive bodies aren't available here", k.H.Spawn(new SpawnRequest(Kind: "subhive", Task: "x")));
        var sub = new FakeSubhives();
        k.H.Subhives = sub;
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Kind: "subhive", Task: "carry logs")));
        Assert.Equal(("main", "carry logs"), (sub.Calls[0].Owner, sub.Calls[0].Task));
        Assert.Equal("error: kind 'blob' is hive|subhive|script", k.H.Spawn(new SpawnRequest(Kind: "blob")));
        // A script body can't own clones (only main and hive-kind clones do).
        var script = k.Bodies.Spawns.First(s => s.Opts.Kind == "script").Name;
        Assert.StartsWith("no:", k.H.Spawn(new SpawnRequest(Task: "x"), owner: script));
        output.WriteLine(k.H.List());
    }

    private sealed class FakeSubhives : ISubhiveHost
    {
        public List<(string Owner, string Account, string? Task)> Calls { get; } = [];
        public string Spawn(string owner, string account, string? task, string? script) { Calls.Add((owner, account, task)); return "ok: subhive " + account; }
        public string Release(string account) => "ok";
    }

    [Fact]
    public async Task Releasing_a_hive_releases_its_children_first_and_each_deposits_before_logout()
    {
        var k = new HierarchyKit();
        k.H.Spawn(new SpawnRequest(Task: "run the farm"));                                  // 101 (main's hive)
        k.H.Spawn(new SpawnRequest(Kind: "script", Script: "wheat_loop"), owner: "maid_chan_101"); // 102 (101's)
        k.H.Spawn(new SpawnRequest(Kind: "hive", Task: "fish"), owner: "maid_chan_101");      // 103 (101's hive)
        k.H.Spawn(new SpawnRequest(Script: "cobble_loop"), owner: "maid_chan_103");           // 105 (103's; 104 is quantity-like)
        Assert.Equal("101 hive opus; 102 script wheat_loop (of 101); 103 hive opus (of 101); 105 script cobble_loop (of 103)", k.H.Tree.Render());
        Assert.True(k.H.Tree.IsUnder("maid_chan_105", "maid_chan_101"));
        // A hive may only release what's under it.
        Assert.StartsWith("no:", k.H.Release("101", requester: "maid_chan_103"));

        var r = k.H.Release("101");
        output.WriteLine(r);
        Assert.Contains("after 3 child clone(s)", r);
        await Wait.Until(() => k.H.Tree.Count == 0, 10, "the whole subtree released");
        var log = k.LogCopy();
        output.WriteLine(string.Join("\n", log));
        int I(string s) => log.IndexOf(s);
        foreach (var n in new[] { "101", "102", "103", "105" })
        {
            Assert.True(I($"deposit maid_chan_{n}") < I($"logout maid_chan_{n}"), n + " deposits first");
            Assert.True(I($"logout maid_chan_{n}") < I($"/whitelist remove maid_chan_{n}"), n + " leaves the whitelist after logging out");
        }
        Assert.True(I("logout maid_chan_105") < I("deposit maid_chan_103"), "grandchild before its owner");
        Assert.True(I("logout maid_chan_102") < I("deposit maid_chan_101") && I("logout maid_chan_103") < I("deposit maid_chan_101"), "children before the hive");
        Assert.All(k.Managed.All(), a => Assert.False(a.Active || a.Whitelisted));
    }

    [Fact]
    public async Task An_owner_that_goes_offline_takes_its_clones_with_it()
    {
        var k = new HierarchyKit();
        k.H.Spawn(new SpawnRequest(Task: "explore"));
        k.H.Spawn(new SpawnRequest(Script: "torch_line"), owner: "maid_chan_101");
        k.Bodies.Recall("101", "its task is done"); // 101 logs out by itself (task done), not via release
        await Wait.Until(() => !k.Bodies.Live.Contains("maid_chan_102"), 10, "102 released with its owner");
        var log = k.LogCopy();
        Assert.True(log.IndexOf("deposit maid_chan_102") >= 0 && log.IndexOf("deposit maid_chan_102") < log.IndexOf("logout maid_chan_102"));
    }

    // ================================================================ the bus: routing, auth, never chat

    [Fact]
    public async Task Bus_routes_through_main_authenticates_and_never_touches_chat()
    {
        var chat = new List<string>(); // anything that would reach game chat
        var k = new HierarchyKit();
        k.H.Notice = e => { if (e.Kind == EventKind.Chat) chat.Add(e.Text); };
        var bus = new HiveBus("maid_chan") { MainHandler = k.H.HandleBusAsync };
        var gotA = new List<BusEnvelope>();
        var gotB = new List<BusEnvelope>();
        var tokA = bus.Register("maid_chan_101", (e, _) => { lock (gotA) gotA.Add(e); return Task.FromResult(new BusReply(true)); });
        var tokB = bus.Register("maid_chan_102", (e, _) => { lock (gotB) gotB.Add(e); return Task.FromResult(new BusReply(true)); });

        // A hive → another hive: main routes it; tokens never travel onward.
        var r = await bus.RouteAsync(BusEnvelope.Make(BusTypes.Observation, "maid_chan_101", "maid_chan_102", new ObservationMsg("creeper near the farm"), tokA), "maid_chan_101");
        Assert.True(r.Ok);
        Assert.Single(gotB, e => e.From == "maid_chan_101" && e.Token == null);
        Assert.Empty(gotA);
        // Broadcast reaches every other hive (and main), not the sender.
        await bus.RouteAsync(BusEnvelope.Make(BusTypes.NeedHelp, "maid_chan_102", "*", new NeedHelpMsg("stuck in a hole"), tokB), "maid_chan_102");
        Assert.Single(gotA, e => e.Type == BusTypes.NeedHelp);
        Assert.DoesNotContain(gotB, e => e.Type == BusTypes.NeedHelp);

        // Authentication: a wrong token, another hive's identity on my pipe, main-only types from a hive.
        Assert.False((await bus.RouteAsync(BusEnvelope.Make(BusTypes.Status, "maid_chan_101", "main", new StatusMsg("x"), "bad"), "maid_chan_101")).Ok);
        Assert.False((await bus.RouteAsync(BusEnvelope.Make(BusTypes.Status, "maid_chan_102", "main", new StatusMsg("x"), tokB), "maid_chan_101")).Ok);
        Assert.False((await bus.RouteAsync(BusEnvelope.Make(BusTypes.MemChanged, "maid_chan_101", "*", new MemChangedMsg([], [], "x"), tokA), "maid_chan_101")).Ok);
        Assert.False((await bus.RouteAsync(BusEnvelope.Make("chat", "maid_chan_101", "main", "hi", tokA), "maid_chan_101")).Ok);
        Assert.Equal(4, bus.Rejected);

        // Status/results/help reach main's brain as quiet notices, never as chat.
        var notices = new List<GameEvent>();
        k.H.Notice = e => notices.Add(e);
        await bus.RouteAsync(BusEnvelope.Make(BusTypes.Status, "maid_chan_101", "main", new StatusMsg("half the wheat cut", 0.5), tokA), "maid_chan_101");
        await bus.RouteAsync(BusEnvelope.Make(BusTypes.NeedHelp, "maid_chan_101", "main", new NeedHelpMsg("no bonemeal"), tokA), "maid_chan_101");
        Assert.All(notices, n => Assert.Equal(EventKind.Notice, n.Kind));
        Assert.Contains(notices, n => n.MustWake && n.Text.Contains("needs help"));
        Assert.Empty(chat);

        // Over the IPC frame the envelope survives the round trip.
        var frame = new HiveMessage { T = Msg.Bus, D = HiveJson.ToElement(BusEnvelope.Make(BusTypes.Spawn, "maid_chan_101", "main", new SpawnReq(Kind: "script", Script: "s"), tokA)) };
        var back = JsonSerializer.Deserialize<HiveMessage>(FrameCodec.Encode(frame).AsSpan(4), HiveJson.Options)!.As<BusEnvelope>()!;
        Assert.Equal(("script", "s"), (back.As<SpawnReq>()!.Kind, back.As<SpawnReq>()!.Script));
    }

    [Fact]
    public void Hive_speech_scrubs_internal_coordination_and_keeps_player_lines()
    {
        Assert.Equal("", HiveSpeech.Scrub("Whitelisting maid_chan_101 now."));
        Assert.Equal("", HiveSpeech.Scrub("[hive bus] 101: half done"));
        Assert.Equal("On it!", HiveSpeech.Scrub("On it! Spawning a script-only clone for the cobble."));
        Assert.Equal("Sure, 101 will chop the oaks.", HiveSpeech.Scrub("Sure, 101 will chop the oaks."));
        Assert.Equal("Here's your bread.", HiveSpeech.Scrub("Here's your bread. I'll tell the subhive to keep farming."));
    }

    // ================================================================ chat verbosity budget

    [Fact]
    public void Chat_budget_limits_unasked_lines_per_minute_and_never_drops_answers()
    {
        var t = new ManualTime();
        var b = new ChatVerbosityBudget(3, t);
        Assert.True(b.TryTake(false));
        Assert.True(b.TryTake(false));
        Assert.True(b.TryTake(false));
        Assert.False(b.TryTake(false));          // a 4th unasked line in the minute: dropped
        Assert.True(b.TryTake(addressed: true)); // a player asked: always answered (and counted)
        Assert.Equal(1, b.Dropped);
        t.Now += TimeSpan.FromSeconds(61);
        Assert.True(b.TryTake(false));           // the window slid

        var batchQuiet = new EventBatch([GameEvent.Notice("[hive] 101 done", EventPriority.Low)]);
        var batchAsked = new EventBatch([GameEvent.Chat("poofimafox", "maid, how's it going?")]);
        var muted = new List<string>();
        var gate = HiveSpeech.Wrap(null, new ChatVerbosityBudget(1, t), muted.Add);
        Assert.Equal("Done with the wheat!", gate(batchQuiet, "Done with the wheat!", false));
        Assert.Null(gate(batchQuiet, "Also the sugar cane!", false));   // over budget, unasked
        Assert.Equal("Going well!", gate(batchAsked, "Going well! Whitelisting maid_chan_103.", false));
        Assert.Equal(["Also the sugar cane!"], muted);
    }

    // ================================================================ brain tool budget

    private static int Tok(MineClaw.Agent.Llm.LlmToolDefinition d) => (int)Math.Ceiling((d.Name.Length + d.Description.Length + d.InputSchema.GetRawText().Length) / 3.5) + 6;

    [Fact]
    public async Task Hive_tools_stay_within_the_pinned_budget_with_the_hierarchy()
    {
        await using var h = new TestHive(launcher: _ => null!);
        var llm = new MineClaw.Agent.Llm.LlmOptions { ApiKey = "sk-ant-test" };
        h.Supervisor.ScriptPrep = (script, code) => (script, null, code);
        var k = new HierarchyKit();
        var main = new MineClaw.Agent.Tools.ToolRegistry();
        HiveTools.RegisterMain(main, h.Supervisor, llm, k.H);
        int mainTok = Tok(main.Definitions(MineClaw.Agent.Llm.ModelTier.Main).Single());
        // A hive-kind clone's own hive tool (bus-backed) is smaller than main's, so a hive clone's brain tools (main's set minus
        // hive, plus hive_report and this) never exceed main's: one shared cap (ToolBudgets.Brain).
        var clone = new MineClaw.Agent.Tools.ToolRegistry();
        CloneHiveTool.Register(clone, new Clone.CloneHost("maid_chan_101", "no-pipe", new HiveConfig()));
        int cloneTok = Tok(clone.Definitions(MineClaw.Agent.Llm.ModelTier.Main).Single());
        output.WriteLine($"main hive tool ≈{mainTok}, clone hive tool ≈{cloneTok}");
        Assert.True(mainTok <= MineClaw.Agent.Tools.ToolBudgets.HiveTool, $"main hive tool ≈{mainTok}");
        Assert.True(cloneTok < mainTok, $"clone hive tool ≈{cloneTok}");
        Assert.True(HiveTools.KindHint.Length <= 220, "the kind hint is one short cached line");
        // The hierarchy is what the tool talks to: release/list/spawn kinds go through it.
        var r = await main.ExecuteAsync("hive", JsonSerializer.SerializeToElement(new { op = "spawn", kind = "subhive", task = "x" }), default);
        Assert.Contains("subhive bodies aren't available here", r.Text);
        var l = await main.ExecuteAsync("hive", JsonSerializer.SerializeToElement(new { op = "list" }), default);
        Assert.True(l.Ok);
        // A clone with no bus answer gets a clear error, not a hang.
        var c = await clone.ExecuteAsync("hive", JsonSerializer.SerializeToElement(new { op = "list" }), default);
        Assert.False(c.Ok);
        Assert.Contains("hive bus", c.Text);
    }

    // ================================================================ supervisor: dynamic accounts end to end (no stub)

    private sealed class DeadLauncher(List<string> log) : Supervisor.IProcessLauncher
    {
        public Supervisor.ICloneProcess Start(string clone, string pipeName) { lock (log) log.Add("launch " + clone); return new Dead(); }
        private sealed class Dead : Supervisor.ICloneProcess
        {
            public int Id => 1;
            public bool HasExited => true;
            public int? ExitCode => 1;
            public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
            public void Kill() { }
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Supervisor_allocates_whitelists_before_login_and_removes_after_release()
    {
        var k = new HierarchyKit();
        var cfg = TestHive.Config();
        cfg.CloneUsernames = []; // the empty state: no roster
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var db = MineClaw.Agent.Rag.AgentDb.Open(Path.Combine(dir, "mineclaw.db"));
        var arbiter = new ChatArbiter("maid_chan", "maid_chan", cfg.Clones());
        var ledger = new Budget.BudgetLedger(new MineClaw.Agent.Budget.BudgetOptions { DailyCapUsd = 10, MonthlyCapUsd = 190 }, cfg.Budget, () => (0m, 0m));
        await using var sup = new Supervisor.HiveSupervisor(cfg, "maid_chan", new DeadLauncher(k.Log), new Claims.ClaimBoard(TimeSpan.FromSeconds(60)),
            ledger, arbiter, new Db.HiveStore(db, "maid_chan"), () => new SlotFacts(20, ["maid_chan", "poofimafox", "kiwi"]), ["poofimafox", "kiwi"]);
        k.H.Bodies = new SupervisorBodies(sup);
        sup.Accounts = k.H;
        Assert.Empty(sup.Slots);

        var r = sup.Spawn("mine cobble", options: new HiveSpawnOptions { Kind = "hive", Owner = "main" });
        output.WriteLine(r);
        Assert.StartsWith("ok: 101 spawning", r);
        Assert.Contains("maid_chan_101", arbiter.Clones); // "101, come here" addresses it
        await Wait.Until(() => k.LogCopy().Contains("launch maid_chan_101"), 10);
        var log = k.LogCopy();
        Assert.True(log.IndexOf("/whitelist add maid_chan_101") >= 0 && log.IndexOf("/whitelist add maid_chan_101") < log.IndexOf("launch maid_chan_101"), string.Join(", ", log));
        Assert.Equal(CloneKind.Hive, k.H.Tree.Get("maid_chan_101")!.Kind);

        // It never connected (a crash: backing off); a recall makes it final: off the whitelist, out of the tree.
        await Wait.Until(() => sup.Slot("maid_chan_101")!.State == Supervisor.CloneState.Backoff, 10);
        sup.Recall("101");
        await Wait.Until(() => k.LogCopy().Contains("/whitelist remove maid_chan_101"), 10);
        Assert.Null(k.H.Tree.Get("maid_chan_101"));
        Assert.False(k.Managed.Get("maid_chan_101")!.Whitelisted);
        // The account stays known for reuse: the next unnamed spawn takes the same slot, no new name.
        Assert.StartsWith("ok: 101", sup.Spawn("chop", options: new HiveSpawnOptions()));
    }
}
