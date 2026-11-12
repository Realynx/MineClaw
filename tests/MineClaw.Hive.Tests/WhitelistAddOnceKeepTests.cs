using MineClaw.Hive.Hierarchy;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Incident 2026-11-12 (1.12.2, slow-disk server): the server's watchdog killed it after a 60 s tick spent saving whitelist.json,
/// which it writes synchronously for every <c>/whitelist add|remove</c>. The colony's account manager sent an add on every pawn login
/// (46 in 8 minutes, connect retries included), a remove on every logout, and 30 + 30 on every deploy. Now: add once, keep. An
/// account is added only when it isn't known to be listed and stays listed while the hive manages it; it is removed only when the
/// hive gives it up for good.
/// </summary>
public class WhitelistAddOnceKeepTests
{
    private const string Pawn = "maid_chan_105";

    /// <summary>The story host's side as the hierarchy sees it: a spawn is recorded; the test drives connect and logout.</summary>
    private sealed class Host : ISubhiveHost
    {
        public List<string> Spawned { get; } = [];
        public List<string> Retried { get; } = [];
        public string Spawn(string owner, string account, string? task, string? script) { lock (Spawned) Spawned.Add(account); return "ok: spawning"; }
        public string Release(string account) => "ok";
        public bool RetryNow(string account) { lock (Retried) Retried.Add(account); return true; }
    }

    /// <summary>Main's whitelist as production wires it, with the server's replies fed back (an add counts once confirmed).</summary>
    private sealed class Rig
    {
        public HierarchyKit K { get; }
        public WhitelistManager W { get; }
        public HiveHierarchy H { get; }
        public Host Host { get; } = new();
        public HashSet<string> Colony { get; } = new(StringComparer.OrdinalIgnoreCase) { Pawn };
        private readonly List<string> _sent = [];

        /// <param name="reply">The server's answer to a line (null = none); <paramref name="delayMs"/> later on main's chat.</param>
        public Rig(string? path = null, Func<string, string?>? reply = null, int delayMs = 20)
        {
            K = new HierarchyKit(path);
            reply ??= Confirm;
            WhitelistManager? w = null;
            w = new WhitelistManager(K.Managed, K.Cfg, HierarchyKit.Main, K.Gate.ArmManagedWhitelist, (line, ct) =>
            {
                if (!K.Gate.AllowOutgoing(line)) return Task.FromResult(false);
                lock (_sent) _sent.Add(line);
                if (reply(line) is { } answer) _ = Task.Run(async () => { await Task.Delay(delayMs); w!.NoteServerLine(answer); });
                return Task.FromResult(true);
            }) { ConfirmTimeout = TimeSpan.FromSeconds(3), RetryDelay = TimeSpan.FromMinutes(10) };
            W = w;
            K.Gate.ManagedAccount = w.Accepts;
            w.NoteServerLine(""); // main's chat feed is wired
            H = new HiveHierarchy(K.Cfg, HierarchyKit.Main, K.Managed, w) { Bodies = K.Bodies, Subhives = Host };
            H.AddOwner(a => { lock (Colony) return Colony.Contains(a); }); // Story Mode: an active colony claims its pawns
        }

        public List<string> Sent { get { lock (_sent) return _sent.ToList(); } }
        public int Adds => Sent.Count(l => l.StartsWith("/whitelist add ", StringComparison.Ordinal));
        public int Removes => Sent.Count(l => l.StartsWith("/whitelist remove ", StringComparison.Ordinal));

        public static string? Confirm(string line)
        {
            var p = line.Split(' ');
            return p[1] == "add" ? $"Added {p[2]} to the whitelist" : $"Removed {p[2]} from the whitelist";
        }

        /// <summary>The colony's seat retry brings the pawn in; its body's first connect asks main to whitelist it (InProcBodies.BeforeConnect).</summary>
        public async Task ConnectAsync(string account = Pawn)
        {
            var r = H.BringGroup("story-1", [account], "story_pawn");
            Assert.Null(Assert.Single(r).Why);
            await H.BeforeLaunchAsync(account);
        }
    }

    [Fact]
    public async Task A_pawn_that_logs_out_and_reconnects_five_times_is_added_once_and_never_removed()
    {
        var r = new Rig();
        r.K.Managed.Upsert(Pawn, a => a);
        await r.ConnectAsync();
        for (int i = 0; i < 5; i++)
        {
            r.H.Released(Pawn, "logged out"); // the body's Gone hook: "hive: maid_chan_105 released (logged out)"
            Assert.Null(r.H.Tree.Get(Pawn));
            await r.ConnectAsync();
        }
        r.H.Released(Pawn, "logged out");
        await Task.Delay(100);
        Assert.Equal(["/whitelist add " + Pawn], r.Sent);
        Assert.True(r.K.Managed.Get(Pawn)!.Whitelisted);
        Assert.Equal(6, r.Host.Spawned.Count);
    }

    [Fact]
    public async Task A_connect_retry_loop_joins_the_add_in_flight_and_sends_no_more()
    {
        var r = new Rig(delayMs: 150); // a lagging server: the confirmation comes late
        r.K.Managed.Upsert(Pawn, a => a);
        r.H.Tree.Put(new HiveNode(Pawn, CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        // The runner retries the login while the first add is still waiting for the server; a relaunch fires the sync hook too.
        var tries = Enumerable.Range(0, 4).Select(_ => r.H.BeforeLaunchAsync(Pawn)).ToList();
        r.H.BeforeLaunch(Pawn);
        await Task.WhenAll(tries);
        for (int i = 0; i < 3; i++) await r.H.BeforeLaunchAsync(Pawn); // later retries: it is on the list
        Assert.Equal(1, r.Adds);
        Assert.True(r.K.Managed.Get(Pawn)!.Whitelisted);
    }

    [Fact]
    public async Task A_restart_with_the_saved_whitelisted_flag_sends_no_add_and_no_remove()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mineclaw-wl-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "hive-accounts.json");
        try
        {
            // The file an earlier build wrote (same shape: the flag is the old "whitelisted" field).
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, $$"""{"accounts":[{"name":"{{Pawn}}","whitelisted":true,"active":true,"kind":"script","owner":"main"}]}""");
            var r = new Rig(path);
            Assert.True(r.K.Managed.Get(Pawn)!.Whitelisted);
            Assert.Equal(0, await r.H.StartAsync([], _ => false)); // the colony claims it: the reconcile keeps it
            await r.ConnectAsync();
            r.H.Released(Pawn, "logged out");
            await r.ConnectAsync();
            await Task.Delay(100);
            Assert.Empty(r.Sent);
            Assert.True(new ManagedAccounts(path).Get(Pawn)!.Whitelisted, "still saved as listed");
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task A_pool_account_reused_while_its_removal_is_in_flight_is_added_again_after_it()
    {
        // Review F1: the brain's body finishes a short job and goes back to the pool (removed for good); the brain spawns again at once
        // and the allocator hands it the same account. Its add must go out after the queued removal, not be skipped as "listed".
        var r = new Rig(delayMs: 300); // the server answers each line 300 ms later
        const string body = "maid_chan_111";
        r.K.Managed.Upsert(body, a => a);
        r.H.Tree.Put(new HiveNode(body, CloneKind.Subhive, null, HiveTree.Root));
        await r.H.BeforeLaunchAsync(body);
        r.H.Released(body, "released");                       // the removal goes out and waits for the server
        Assert.True(r.K.Managed.Get(body)!.Whitelisted);       // not confirmed yet
        r.H.Tree.Put(new HiveNode(body, CloneKind.Subhive, null, HiveTree.Root));
        await r.H.BeforeLaunchAsync(body);
        Assert.Equal(["/whitelist add " + body, "/whitelist remove " + body, "/whitelist add " + body], r.Sent);
        Assert.True(r.K.Managed.Get(body)!.Whitelisted);
        await r.H.BeforeLaunchAsync(body);                     // a later retry: listed, nothing more
        Assert.Equal(2, r.Adds);
    }

    [Fact]
    public async Task A_removal_the_lagging_server_never_confirmed_does_not_block_the_next_add_or_the_kick_re_add()
    {
        // Review F1, the incident's server: the removal's confirmation never arrives in time, so the record still says listed and
        // the add was under two minutes ago. The reused account is added again before its login, and a whitelist kick re-adds.
        var r = new Rig(reply: l => l.StartsWith("/whitelist remove ", StringComparison.Ordinal) ? null : Rig.Confirm(l));
        r.W.ConfirmTimeout = TimeSpan.FromMilliseconds(200);
        const string body = "maid_chan_111";
        r.K.Managed.Upsert(body, a => a);
        r.H.Tree.Put(new HiveNode(body, CloneKind.Subhive, null, HiveTree.Root));
        await r.H.BeforeLaunchAsync(body);
        Assert.False(await r.W.RemoveAsync(body));             // sent, never confirmed
        Assert.True(r.K.Managed.Get(body)!.Whitelisted);       // the reconcile may still retry it
        await r.H.BeforeLaunchAsync(body);
        Assert.Equal(2, r.Adds);
        Assert.True(r.K.Managed.Get(body)!.Whitelisted);
        await r.H.BeforeLaunchAsync(body);                     // the add was confirmed: trusted again
        Assert.Equal(2, r.Adds);

        // Same again, but the login goes ahead and is kicked "not white-listed": the cooldown of the first add doesn't apply.
        Assert.False(await r.W.RemoveAsync(body));
        Assert.Null(await r.H.RewhitelistAsync(body));
        Assert.Equal(3, r.Adds);
        Assert.Equal(2, r.Removes);

        // A late "Removed" for an unconfirmed removal clears the record.
        Assert.False(await r.W.RemoveAsync(body));
        r.W.NoteServerLine("Removed " + body + " from the whitelist");
        Assert.False(r.K.Managed.Get(body)!.Whitelisted);
    }

    [Fact]
    public async Task Main_shutting_down_keeps_every_body_listed()
    {
        var r = new Rig();
        // A body main's brain drives (no colony claims it) and a pawn, both out when the deploy stops main.
        const string body = "maid_chan_111";
        r.K.Managed.Upsert(body, a => a with { Whitelisted = true, Active = true });
        r.K.Managed.Upsert(Pawn, a => a with { Whitelisted = true, Active = true });
        r.H.Tree.Put(new HiveNode(body, CloneKind.Subhive, null, HiveTree.Root));
        r.H.Tree.Put(new HiveNode(Pawn, CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        r.H.Stopping();
        r.H.Released(body, "main shutting down");
        r.H.Released(Pawn, "main shutting down");
        await Task.Delay(100);
        Assert.Empty(r.Sent);
        Assert.True(r.K.Managed.Get(body)!.Whitelisted && r.K.Managed.Get(Pawn)!.Whitelisted);
    }

    [Fact]
    public async Task An_account_given_up_for_good_is_removed_once()
    {
        var r = new Rig();
        // 1. A body of main's brain whose job ended goes back to the pool: nobody claims it, so it leaves the list (once).
        const string body = "maid_chan_111";
        r.K.Managed.Upsert(body, a => a);
        r.H.Tree.Put(new HiveNode(body, CloneKind.Subhive, null, HiveTree.Root));
        await r.H.BeforeLaunchAsync(body);
        r.H.Released(body, "released");
        await Wait.Until(() => !r.K.Managed.Get(body)!.Whitelisted, 5, "the pool account's removal");
        r.H.Released(body, "released"); // a late second report changes nothing
        await Task.Delay(100);
        Assert.Equal(1, r.Sent.Count(l => l == "/whitelist remove " + body));

        // 2. A pawn removed from its colony: the owner gives it up, then it logs out; the colony's claim is dropped after.
        r.K.Managed.Upsert(Pawn, a => a);
        await r.ConnectAsync();
        r.H.GiveUp(Pawn);
        r.H.Released(Pawn, "logged out");
        lock (r.Colony) r.Colony.Remove(Pawn);
        await Wait.Until(() => !r.K.Managed.Get(Pawn)!.Whitelisted, 5, "the departed pawn's removal");
        await Task.Delay(100);
        Assert.Equal(1, r.Sent.Count(l => l == "/whitelist remove " + Pawn));

        // 3. Given up while not hosted at all: removed at once; again, nothing more.
        const string idle = "maid_chan_114";
        r.K.Managed.Upsert(idle, a => a with { Whitelisted = true });
        r.H.GiveUp(idle);
        await Wait.Until(() => !r.K.Managed.Get(idle)!.Whitelisted, 5, "the idle pawn's removal");
        r.H.GiveUp(idle);
        await Task.Delay(100);
        Assert.Equal(1, r.Sent.Count(l => l == "/whitelist remove " + idle));
        Assert.Equal(3, r.Removes);
    }

    [Fact]
    public async Task A_not_whitelisted_kick_re_adds_once_even_when_the_record_says_listed()
    {
        var r = new Rig();
        r.K.Managed.Upsert(Pawn, a => a with { Whitelisted = true, Active = true }); // someone took it off the server's file by hand
        r.H.Tree.Put(new HiveNode(Pawn, CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        await r.H.BeforeLaunchAsync(Pawn);
        Assert.Empty(r.Sent); // the record says listed: the login goes ahead without a line
        Assert.Null(await r.H.RewhitelistAsync(Pawn)); // kicked "You are not white-listed on this server!"
        Assert.Equal(["/whitelist add " + Pawn], r.Sent);
        // The login loop is kicked again right away (attempts 1 and 2): the server just took the name, so no second save.
        Assert.Null(await r.H.RewhitelistAsync(Pawn));
        await r.H.BeforeLaunchAsync(Pawn);
        Assert.Equal(1, r.Adds);
        Assert.True(r.K.Managed.Get(Pawn)!.Whitelisted);
    }

    [Fact]
    public async Task Bring_online_on_a_listed_hosted_pawn_only_retries_its_login()
    {
        var r = new Rig();
        r.K.Managed.Upsert(Pawn, a => a with { Whitelisted = true, Active = true });
        r.H.Tree.Put(new HiveNode(Pawn, CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        Assert.Null(Assert.Single(r.H.BringGroup("story-1", [Pawn], "story_pawn")).Why);
        await Wait.Until(() => { lock (r.Host.Retried) return r.Host.Retried.Contains(Pawn); }, 5, "retry now");
        Assert.Empty(r.Sent);
        // After a whitelist kick (the body's note says so) it is added again.
        r.H.NoteBody(Pawn, "not whitelisted — re-adding");
        Assert.Null(Assert.Single(r.H.BringGroup("story-1", [Pawn], "story_pawn")).Why);
        await Wait.Until(() => r.Adds == 1, 5, "the re-add");
    }

    [Fact]
    public async Task The_servers_already_whitelisted_answer_and_a_late_confirmation_count_as_listed()
    {
        // Newer servers: "Player is already whitelisted" (no name) answers the only add waiting.
        var r = new Rig(reply: _ => "Player is already whitelisted");
        r.K.Managed.Upsert(Pawn, a => a);
        Assert.True(await r.W.AddAsync(Pawn));
        Assert.True(r.K.Managed.Get(Pawn)!.Whitelisted);
        Assert.True(await r.W.AddAsync(Pawn));
        Assert.Equal(1, r.Adds);

        // A lagging server confirms after main stopped waiting: the account is on the list after all; the next login sends nothing.
        var slow = new Rig(delayMs: 400);
        slow.W.ConfirmTimeout = TimeSpan.FromMilliseconds(100);
        slow.K.Managed.Upsert(Pawn, a => a);
        Assert.False(await slow.W.AddAsync(Pawn));
        await Wait.Until(() => slow.K.Managed.Get(Pawn)!.Whitelisted, 5, "the late confirmation");
        Assert.True(await slow.W.AddAsync(Pawn));
        Assert.Equal(1, slow.Adds);
    }

    [Fact]
    public async Task The_reconcile_never_adds_and_never_repeats_a_removal()
    {
        var r = new Rig();
        r.K.Managed.Upsert(Pawn, a => a with { Whitelisted = true, Active = true });      // a colony's: kept
        r.K.Managed.Upsert("maid_chan_67", a => a with { Whitelisted = true });           // stale: removed once
        r.K.Managed.Upsert("maid_chan_69", a => a);                                       // never listed: nothing
        Assert.Equal(1, await r.H.StartAsync([], _ => false));
        Assert.Equal(0, await r.W.ReconcileAsync(a => r.H.OwnedElsewhere(a)));
        Assert.Equal(["/whitelist remove maid_chan_67"], r.Sent);
        Assert.True(r.K.Managed.Get(Pawn)!.Whitelisted);
    }
}
