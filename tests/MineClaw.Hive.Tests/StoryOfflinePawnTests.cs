using MineClaw.Bot;
using MineClaw.Bot.Memory;
using MineClaw.Hive.Claims;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Story;
using MineClaw.World;
using MineClaw.World.Claims;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Live 11-03 (CT 114, colony story-1, 10 pawns, 1.12.2): after a restart the startup reconcile took the colony's pawns off the
/// whitelist while they respawned, every reconnect was kicked "You are not white-listed" for good, and the pawns the player cap kept
/// out never came back. And the one pawn online couldn't cut a single plant: the shared protection saw its own dig lease through
/// main's claim view and refused it "claimed".
/// </summary>
public class StoryOfflinePawnTests
{
    // ================================================================ the startup reconcile

    [Fact]
    public async Task The_startup_reconcile_never_removes_accounts_a_colony_or_the_tree_owns()
    {
        var k = new HierarchyKit();
        foreach (var n in new[] { "maid_chan_102", "maid_chan_105", "maid_chan_109", "maid_chan_67" })
            k.Managed.Upsert(n, a => a with { Whitelisted = true, Active = true });
        // 102 is a pawn body logging in (in the tree, not a supervisor slot); 105 a colony pawn still waiting for a seat; 109 too.
        k.H.Tree.Put(new HiveNode("maid_chan_102", CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        var colony = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "maid_chan_102", "maid_chan_105", "maid_chan_109" };
        k.H.AddOwner(colony.Contains);

        int removed = await k.H.StartAsync([], _ => false); // the supervisor runs no clone process at all
        Assert.Equal(1, removed);
        Assert.Equal(["/whitelist remove maid_chan_67"], k.Sent);
        foreach (var n in colony)
        {
            Assert.True(k.Managed.Get(n)!.Whitelisted, n);
            Assert.True(k.Managed.Get(n)!.Active, n + " stays active (not released)");
        }
        Assert.False(k.Managed.Get("maid_chan_67")!.Active);
    }

    [Fact]
    public async Task The_startup_reconcile_waits_until_the_owners_have_loaded()
    {
        var k = new HierarchyKit();
        k.Managed.Upsert("maid_chan_105", a => a with { Whitelisted = true, Active = true });
        k.H.ExpectClaims();
        var run = k.H.StartAsync([], _ => false);
        await Task.Delay(150);
        Assert.False(run.IsCompleted); // the colonies haven't loaded yet: nothing is judged stale
        Assert.Empty(k.Sent);
        k.H.AddOwner(a => a == "maid_chan_105"); // the colony loads and claims its pawn
        k.H.ClaimsReady();
        Assert.Equal(0, await run);
        Assert.Empty(k.Sent);
    }

    // ================================================================ a "not white-listed" kick

    [Fact]
    public async Task A_kicked_pawn_is_whitelisted_again_through_the_gate_and_only_a_hive_account()
    {
        var k = new HierarchyKit();
        k.H.Tree.Put(new HiveNode("maid_chan_114", CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        k.Managed.Upsert("maid_chan_114", a => a with { Whitelisted = false, Active = true });
        Assert.Null(await k.H.RewhitelistAsync("maid_chan_114"));
        Assert.Equal(["/whitelist add maid_chan_114"], k.Sent);
        Assert.True(k.Managed.Get("maid_chan_114")!.Whitelisted);
        Assert.Equal("connecting", k.H.BodyNote("maid_chan_114"));

        // Scoped: never main, never a stranger, never a managed account nobody in the hive owns.
        Assert.NotNull(await k.H.RewhitelistAsync(HierarchyKit.Main));
        Assert.NotNull(await k.H.RewhitelistAsync("poofimafox"));
        k.Managed.Upsert("maid_chan_420", a => a);
        Assert.Equal("not one of the hive's bodies", await k.H.RewhitelistAsync("maid_chan_420"));
        Assert.Single(k.Sent);
    }

    [Fact]
    public async Task When_main_isnt_op_the_pawn_says_so()
    {
        var k = new HierarchyKit();
        var w = new WhitelistManager(k.Managed, k.Cfg, HierarchyKit.Main, k.Gate.ArmManagedWhitelist, (line, _) =>
            Task.FromResult(k.Gate.AllowOutgoing(line))) { ConfirmTimeout = TimeSpan.FromMilliseconds(300) };
        w.NoteServerLine(""); // replies are fed: the add waits for the server's word, which never comes (main isn't op)
        var h = new HiveHierarchy(k.Cfg, HierarchyKit.Main, k.Managed, w) { MainIsOp = () => false };
        h.Tree.Put(new HiveNode("maid_chan_111", CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        k.Managed.Upsert("maid_chan_111", a => a);
        var why = await h.RewhitelistAsync("maid_chan_111");
        Assert.NotNull(why);
        Assert.Contains("isn't op", why);
        Assert.StartsWith("not whitelisted — main (maid_chan) isn't op", h.BodyNote("maid_chan_111"));
    }

    [Fact]
    public void The_kick_text_is_recognised_and_the_retry_backoff_is_bounded()
    {
        Assert.True(WhitelistManager.IsNotWhitelistedKick("You are not white-listed on this server!"));
        Assert.True(WhitelistManager.IsNotWhitelistedKick("kicked: You are not whitelisted on this server"));
        Assert.False(WhitelistManager.IsNotWhitelistedKick("Server is full"));
    }

    // ================================================================ the player cap: bring exactly the colony's accounts

    private sealed class Subhives : ISubhiveHost
    {
        public List<string> Spawned { get; } = [];
        public List<string> Retried { get; } = [];
        public HashSet<string> Over { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Spawn(string owner, string account, string? task, string? script) { lock (Spawned) Spawned.Add(account); return "ok: spawning"; }
        public string Release(string account) => "ok";
        public bool RetryNow(string account) { lock (Retried) Retried.Add(account); return true; }
        public void AllowOverCap(string account) => Over.Add(account);
    }

    [Fact]
    public void BringGroup_respects_the_cap_and_the_regulars_seats_unless_the_owner_says_over_cap()
    {
        var k = new HierarchyKit { MaxPlayers = 6 }; // online: main, poofimafox, kiwi = 3; no regular offline → 3 free
        var host = new Subhives();
        k.H.Subhives = host;
        lock (k.Online) k.Online.Remove("kiwi"); // kiwi is a regular, offline: one seat kept → 4 free, 1 kept
        foreach (var n in new[] { "maid_chan_105", "maid_chan_109", "maid_chan_111", "maid_chan_114", "maid_chan_120" }) k.Managed.Upsert(n, a => a);
        var res = k.H.BringGroup("story-1", ["maid_chan_105", "maid_chan_109", "maid_chan_111", "maid_chan_114"], "story_pawn");
        // 4 free, 1 kept: three come (free − 1 ≥ kept), the fourth is refused with the cap's reason.
        Assert.Equal(["maid_chan_105", "maid_chan_109", "maid_chan_111"], res.Where(r => r.Why == null).Select(r => r.Account));
        var blocked = Assert.Single(res, r => r.Why != null);
        Assert.Equal("maid_chan_114", blocked.Account);
        Assert.Equal("server full (player cap: 1 free, 1 kept for regulars)", blocked.Why);
        Assert.All(new[] { "maid_chan_105", "maid_chan_109", "maid_chan_111" }, a => Assert.Equal("story-1", k.H.Tree.Get(a)!.Crew));
        Assert.Equal((1, 1), k.H.Seats());

        // The owner's explicit choice: the regulars' seat too.
        var over = k.H.BringGroup("story-1", ["maid_chan_114"], "story_pawn", overCap: true);
        Assert.Null(Assert.Single(over).Why);
        Assert.Contains("maid_chan_114", host.Over);
        // Nothing left at all now: even over the cap it's refused.
        var full = k.H.BringGroup("story-1", ["maid_chan_120"], "story_pawn", overCap: true);
        Assert.Equal("server full (6/6 seats taken)", Assert.Single(full).Why);
        Assert.DoesNotContain("maid_chan_120", host.Spawned);
    }

    [Fact]
    public async Task BringGroup_on_a_hosted_pawn_re_adds_it_and_retries_now()
    {
        var k = new HierarchyKit();
        var host = new Subhives();
        k.H.Subhives = host;
        k.H.Tree.Put(new HiveNode("maid_chan_105", CloneKind.Subhive, null, HiveTree.Root) { Crew = "story-1" });
        k.Managed.Upsert("maid_chan_105", a => a with { Active = true });
        var res = k.H.BringGroup("story-1", ["maid_chan_105"], "story_pawn");
        Assert.Null(Assert.Single(res).Why);
        await Wait.Until(() => { lock (host.Retried) return host.Retried.Contains("maid_chan_105"); }, 5, "retry now");
        Assert.Contains("/whitelist add maid_chan_105", k.Sent);
        Assert.Empty(host.Spawned); // not a second body
        // Another group's account is never taken.
        k.H.Tree.Put(new HiveNode("maid_chan_130", CloneKind.Subhive, null, HiveTree.Root) { Crew = "chop_crew" });
        k.Managed.Upsert("maid_chan_130", a => a);
        Assert.NotNull(Assert.Single(k.H.BringGroup("story-1", ["maid_chan_130"], "story_pawn")).Why);
    }

    // ================================================================ the colony: offline reasons, the seat retry, "Bring online"

    /// <summary>The story host over seats: pawns not hosted are brought in while seats are free.</summary>
    private sealed class SeatHost(FakeStoryWorld w) : IStoryHost
    {
        public IStoryWorld? World => w;
        public HashSet<string> Hosted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PawnLive> Lives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Free = 0, Kept = 2;
        public List<(List<string> Accounts, bool Over)> Brings { get; } = [];
        /// <summary>What the founding spawn answers (the colony's pawns), once.</summary>
        public List<string>? Initial;
        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer)
        {
            var r = Initial;
            Initial = null;
            return r == null ? ([], "no room") : (r, null);
        }
        public int Release(string colony) => 0;
        public PawnLive Live(string account) => Lives.TryGetValue(account, out var l) ? l : PawnLive.Offline;
        public string? StartPawn(string account, string colony) => null;
        public void Interrupt(string account, string why) { }
        public PawnSeat Seat(string account) => Hosted.Contains(account) ? new(true, Lives.ContainsKey(account) ? null : "connecting") : new(false, null);
        public (int Free, int Kept)? Seats() => (Free, Kept);
        public IReadOnlyList<(string Account, string? Why)> Bring(string colony, IReadOnlyList<string> accounts, bool overCap)
        {
            Brings.Add((accounts.ToList(), overCap));
            var res = new List<(string, string?)>();
            foreach (var a in accounts)
            {
                if (Free - 1 >= (overCap ? 0 : Kept)) { Free--; Hosted.Add(a); res.Add((a, null)); }
                else res.Add((a, $"server full (player cap: {Free} free, {Kept} kept for regulars)"));
            }
            return res;
        }
    }

    [Fact]
    public void Cap_blocked_pawns_show_why_and_come_online_as_seats_free_up()
    {
        var h = new SeatHost(new FakeStoryWorld()) { Initial = ["maid_chan_102", "maid_chan_105", "maid_chan_109"] };
        h.Hosted.Add("maid_chan_102");
        h.Lives["maid_chan_102"] = FakeStoryHost.Live(0, 64, 0);
        var all = new StoryColonies(h) { RetryEvery = TimeSpan.Zero };
        var (c, err) = all.Create("Riverside", 3, new P3(0, 64, 0));
        Assert.Null(err);

        all.Tick(); // no seat: both stay out, each with the cap's reason, one alert
        var st = c!.State();
        var pawns = st["pawns"]!.AsArray().ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!);
        Assert.Null(pawns["maid_chan_102"]["offline_reason"]);
        Assert.Equal("server full (player cap: 0 free, 2 kept for regulars)", pawns["maid_chan_105"]["offline_reason"]!.GetValue<string>());
        Assert.Equal("offline", pawns["maid_chan_105"]["state"]!.GetValue<string>());
        Assert.Single(c.Alerts, a => a.Text.Contains("can't come online"));
        var seats = st["seats"]!;
        Assert.Equal(1, seats["online"]!.GetValue<int>());
        Assert.Equal(3, seats["wanted"]!.GetValue<int>());
        Assert.Equal(0, seats["cap_free"]!.GetValue<int>());
        Assert.Equal(2, seats["kept_for_regulars"]!.GetValue<int>());

        // A seat frees up: the next retry brings one pawn in (the same account, never a new one); the other keeps waiting.
        h.Free = 3;
        all.Tick();
        Assert.Contains("maid_chan_105", h.Hosted);
        Assert.DoesNotContain("maid_chan_109", h.Hosted);
        Assert.Equal(["maid_chan_105", "maid_chan_109"], h.Brings[^1].Accounts);
        st = c.State();
        pawns = st["pawns"]!.AsArray().ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!);
        Assert.Equal("connecting", pawns["maid_chan_105"]["offline_reason"]!.GetValue<string>());
        Assert.StartsWith("server full", pawns["maid_chan_109"]["offline_reason"]!.GetValue<string>());
        Assert.Single(c.Alerts, a => a.Text.Contains("can't come online")); // a changing seat count doesn't repeat the alert

        // "Bring online" with the regulars' seats: the owner's explicit choice.
        var (started, blocked) = all.BringOnline(c, null, overCap: true);
        Assert.Equal(["maid_chan_105", "maid_chan_109"], started);
        Assert.Empty(blocked);
        Assert.True(h.Brings[^1].Over);

        // A released colony's pawns say so, and Bring online refuses them.
        all.Release(c);
        Assert.Equal("released", c.State()["pawns"]![1]!["offline_reason"]!.GetValue<string>());
        var (s2, b2) = all.BringOnline(c, ["maid_chan_105"], false);
        Assert.Empty(s2);
        Assert.Equal("the colony is released: resume it first", Assert.Single(b2).Reason);
        Assert.False(all.Owns("maid_chan_105"));
    }

    // ================================================================ the pawn's own claim (live 11-03 22:08)

    private sealed class NoRegions : IProtectedRegionSource { public IReadOnlyList<(BlockPos Min, BlockPos Max)> ProtectedRegions() => []; }
    private sealed class NoReserved : IReservedItemSource { public IReadOnlyCollection<string> ReservedItemNames() => []; }

    [Fact]
    public async Task A_bodys_own_dig_lease_never_protects_the_block_from_that_body()
    {
        var board = new ClaimBoard(TimeSpan.FromSeconds(60));
        var protection = new Protection(new BotOptions(), new NoRegions(), new NoReserved()) { Claims = board.ViewFor("maid_chan") }; // main's view
        var pawn = board.ViewFor("maid_chan_102");
        var at = new BlockPos(208, 64, 412);
        const int doublePlant = 175;
        Assert.True((await pawn.TryClaimAsync(ClaimKey.Block(at))).Ok); // ClaimAwareGameClient.DigBlockAsync: the pawn's lease
        Assert.False(protection.IsProtected(at, doublePlant, pawn));     // its own lease: free to it
        Assert.False(protection.Check(at, doublePlant, pawn).Protected);
        Assert.True(protection.IsProtected(at, doublePlant));            // main (and any other body) must keep off
        Assert.Equal("claimed", protection.Check(at, doublePlant, board.ViewFor("maid_chan_105")).Reason);
        await pawn.ReleaseAsync(ClaimKey.Block(at));
        Assert.False(protection.IsProtected(at, doublePlant));
    }

    private static StoryColony TallPlants(out FakeStoryHost h, out FakeStoryWorld w)
    {
        var (c, host, world) = StoryRig.Make(1, MineClaw.Data.Versioning.Release1122GameData.Instance);
        h = host;
        w = world;
        foreach (var x in new[] { 5, 6 })
        {
            w.Set(x, 64, 2, 175, 0); // the lower half
            w.Set(x, 65, 2, 175, 10); // the upper half
        }
        return c;
    }

    [Fact]
    public void A_cut_over_tall_plants_is_one_target_per_plant_on_its_lower_half_and_finishes()
    {
        var c = TallPlants(out _, out var w);
        var r = c.Designate("add", "cut", Area.Of(5, 64, 1, 6, 65, 3), null, null);
        Assert.Equal(2, r.Result!["count"]!.GetValue<int>()); // two plants, not four halves
        Assert.All(c.Designations, d => Assert.Equal(64, d.At!.Value.Y));
        var job = c.Next();
        Assert.Equal("dig", job["kind"]!.GetValue<string>());
        Assert.EndsWith(",64,2", job["at"]!.GetValue<string>());
        // Breaking the lower half drops the whole plant.
        var at = c.CurrentOf(c.P())!.At!.Value;
        w.Set(at.X, 64, at.Z, 0);
        w.Set(at.X, 65, at.Z, 0);
        Assert.True(c.Report(c.P(), job["id"]!.GetValue<string>(), "done", null, null));
        Assert.Single(c.Designations);
    }

    [Fact]
    public void A_saved_upper_half_designation_still_targets_the_lower_half()
    {
        var c = TallPlants(out _, out _);
        c.Designations.Add(new DesignationState { Id = "d9", Kind = "cut", At = new P3(5, 65, 2), Created = DateTimeOffset.UtcNow });
        c.Designations.Add(new DesignationState { Id = "d10", Kind = "cut", At = new P3(5, 64, 2), Created = DateTimeOffset.UtcNow });
        var job = c.Next();
        Assert.Equal("5,64,2", job["at"]!.GetValue<string>());
        Assert.Single(c.Leases()); // one lease for the plant: no second pawn can take its other half
    }

    [Fact]
    public void A_failed_job_and_an_offline_pawn_give_their_leases_back()
    {
        var c = TallPlants(out var h, out _);
        c.Designate("add", "cut", Area.Of(5, 64, 1, 6, 65, 3), null, null);
        var job = c.Next();
        Assert.Single(c.Leases());
        Assert.True(c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", "test", null));
        Assert.Empty(c.Leases());

        c.Next();
        Assert.Single(c.Leases());
        h.Lives[c.P()] = PawnLive.Offline; // the pawn logs out mid-job
        c.Tick();
        Assert.Empty(c.Leases());
        Assert.Null(c.CurrentOf(c.P()));
    }

    [Fact]
    public void A_failed_designation_says_when_it_is_retried_and_retry_now_opens_it()
    {
        var c = TallPlants(out _, out _);
        c.Designate("add", "cut", null, [new P3(5, 64, 2)], null);
        var id = c.Designations.Single().Id;
        var job = c.Next();
        Assert.True(c.Report(c.P(), job["id"]!.GetValue<string>(), "failed", "double_plant at 5,64,2 didn't break", null));
        var d = c.State()["designations"]!.AsArray().Single()!;
        Assert.Equal("double_plant at 5,64,2 didn't break", d["reason"]!.GetValue<string>());
        Assert.Equal(1, d["fails"]!.GetValue<int>());
        Assert.NotNull(d["retry_at"]);
        Assert.NotEqual("dig", c.Next()["kind"]!.GetValue<string>()); // resting: not picked again at once

        var r = c.Designate("retry", null, null, null, null, [id]);
        Assert.Null(r.Error);
        Assert.Equal(1, r.Result!["count"]!.GetValue<int>());
        d = c.State()["designations"]!.AsArray().Single()!;
        Assert.Null(d["retry_at"]);
        Assert.Equal("open", d["status"]!.GetValue<string>());
        Assert.NotNull(c.Designate("retry", null, null, null, null, []).Error);
    }
}
