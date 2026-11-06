using MineClaw.Agent.Chat;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Slots;
using MineClaw.Hive.Story;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Fox 11-05: "Don't set an upper limit at all for how many pawns I can spawn in. Allow me to do as many as I want." A colony takes
/// any count; the real limits (the server's seats, the seats kept for offline regulars, the memory ceiling) decide how many are in
/// now, the rest wait with the reason shown and come as room frees up.
/// </summary>
public sealed class StoryPawnCountTests
{
    /// <summary>A server with <see cref="MaxPlayers"/> slots and two regulars offline; pawns take seats; a memory ceiling optional.</summary>
    private sealed class SlotServer(FakeStoryWorld w) : IStoryHost
    {
        public IStoryWorld? World => w;
        public int MaxPlayers = 20;
        public List<string> Regulars { get; } = ["poofimafox", "kiwi"];
        public HashSet<string> Online { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Bodies the memory ceiling still takes (null = no ceiling).</summary>
        public int? Memory;
        public int Next = 101;
        public Dictionary<string, PawnLive> Lives { get; } = new(StringComparer.OrdinalIgnoreCase);

        private SlotDecision Decide() => SlotPolicy.CanSpawn(new SlotFacts(MaxPlayers, Online.ToList()), Regulars);

        public (IReadOnlyList<string> Accounts, string? Why) Spawn(string colony, int n, IReadOnlyList<string>? prefer)
        {
            var got = new List<string>();
            while (got.Count < n)
            {
                if (Decide() is { Ok: false } d) return (got, "player cap: " + d.Reason);
                if (Memory is <= 0) return (got, MemoryWhy);
                if (Memory is { } m) Memory = m - 1;
                var a = "maid_chan_" + Next++;
                Online.Add(a);
                Lives[a] = FakeStoryHost.Live(0, 64, 0);
                got.Add(a);
            }
            return (got, null);
        }

        private const string MemoryWhy = "memory: 30 MB free of 4096 MB (3666 used, 400 kept), 1 body need 40 MB";

        public SeatFacts? SeatInfo()
        {
            var d = Decide();
            return new SeatFacts(Math.Max(0, d.Free), d.Reserved, MaxPlayers, Regulars.Where(r => !Online.Contains(r)).ToList(), Memory,
                Memory is <= 0 ? MemoryWhy : null);
        }

        public (int Free, int Kept)? Seats() => SeatInfo() is { } s ? (s.Free, s.Kept) : null;
        public PawnSeat Seat(string account) => new(Online.Contains(account), null);
        public int Release(string colony) => 0;
        public PawnLive Live(string account) => Lives.TryGetValue(account, out var l) ? l : PawnLive.Offline;
        public string? StartPawn(string account, string colony) => null;
        public void Interrupt(string account, string why) { }
    }

    [Fact]
    public void Founding_and_adding_forty_pawns_has_no_cap()
    {
        var h = new FakeStoryHost(new FakeStoryWorld());
        var all = new StoryColonies(h);
        var (c, err) = all.Create("Big", 40, new P3(0, 64, 0));
        Assert.Null(err);
        Assert.Equal(40, c!.Pawns.Count);
        Assert.Equal(40, c.Meta.Wanted);

        var (res, addErr) = all.AddPawns(c, 40);
        Assert.Null(addErr);
        Assert.Equal(80, c.Pawns.Count);
        Assert.Equal(80, res!["wanted"]!.GetValue<int>());
        Assert.Empty(res["pending"]!.AsArray());
        Assert.Equal(80, c.State()["seats"]!["online"]!.GetValue<int>());

        // Only a count below one, or one past what an int holds, is refused.
        Assert.NotNull(all.AddPawns(c, 0).Error);
        Assert.Contains("too many", all.AddPawns(c, int.MaxValue).Error);
        Assert.NotNull(all.Create("None", 0, null).Error);
    }

    [Fact]
    public void A_twenty_slot_server_takes_eighteen_the_rest_wait_with_the_reason_and_join_as_room_frees_up()
    {
        var h = new SlotServer(new FakeStoryWorld());
        var all = new StoryColonies(h) { RetryEvery = TimeSpan.Zero };
        var (c, err) = all.Create("Big", 40, new P3(0, 64, 0));
        Assert.Null(err);
        // 20 slots, poofimafox and kiwi offline: two seats stay theirs, 18 pawns come.
        Assert.Equal(18, c!.Pawns.Count);
        Assert.Contains(c.Alerts, a => a.Text.StartsWith("18 of 40 pawns came now; the other 22 wait", StringComparison.Ordinal));
        var seats = c.State()["seats"]!;
        Assert.Equal(18, seats["online"]!.GetValue<int>());
        Assert.Equal(40, seats["wanted"]!.GetValue<int>());
        Assert.Equal(22, seats["pending"]!.GetValue<int>());
        Assert.Equal(22, seats["waiting"]!.GetValue<int>());
        Assert.Equal("server full (20 slots, 2 kept for poofimafox and kiwi)", seats["waiting_reason"]!.GetValue<string>());
        Assert.Equal(20, seats["slots"]!.GetValue<int>());
        Assert.Equal(["poofimafox", "kiwi"], seats["kept_for"]!.AsArray().Select(n => n!.GetValue<string>()));

        // + more while full: accepted, all wait, with the same reason.
        var (res, addErr) = all.AddPawns(c, 2);
        Assert.Null(addErr);
        var pending = Assert.Single(res!["pending"]!.AsArray())!;
        Assert.Equal(24, pending["count"]!.GetValue<int>());
        Assert.Equal("server full (20 slots, 2 kept for poofimafox and kiwi)", pending["reason"]!.GetValue<string>());

        // The owner raises the server to 30 slots, but the memory ceiling takes only 4 more: 4 come, memory is the reason now.
        h.MaxPlayers = 30;
        h.Memory = 4;
        all.RetrySeats(c, force: true);
        Assert.Equal(22, c.Pawns.Count);
        seats = c.State()["seats"]!;
        Assert.Equal(0, seats["memory_room"]!.GetValue<int>());
        Assert.StartsWith("memory: 30 MB free", seats["waiting_reason"]!.GetValue<string>());

        // Memory frees up: the seats bind again (30 − 2 kept = 28 in), the other 14 wait for the server.
        h.Memory = 100;
        all.RetrySeats(c, force: true);
        Assert.Equal(28, c.Pawns.Count);
        seats = c.State()["seats"]!;
        Assert.Equal(14, seats["waiting"]!.GetValue<int>());
        Assert.Equal("server full (30 slots, 2 kept for poofimafox and kiwi)", seats["waiting_reason"]!.GetValue<string>());
        Assert.Equal(94, seats["memory_room"]!.GetValue<int>());
    }

    [Fact]
    public void The_reason_names_the_limit_that_binds()
    {
        Assert.Null(new SeatFacts(10, 2, 20).WhyWaiting(5)); // room for 8: they're on their way
        Assert.Equal("server full (20 slots, 2 kept for poofimafox and kiwi)", new SeatFacts(2, 2, 20, ["poofimafox", "kiwi"]).WhyWaiting(3));
        // Room for 3 of 9: the other 6 find it full once those are in.
        Assert.Equal("server full (20 slots, 2 kept for poofimafox and kiwi)", new SeatFacts(5, 2, 20, ["poofimafox", "kiwi"]).WhyWaiting(9));
        Assert.Equal("server full (20 slots, 1 kept for regulars)", new SeatFacts(1, 1, 20).WhyWaiting(1));
        Assert.Equal("server full (player cap: 0 free)", new SeatFacts(0, 0).WhyWaiting(1));
        Assert.Equal("memory: room for 3 more", new SeatFacts(12, 2, 20, MemoryRoom: 3).WhyWaiting(9));
        Assert.Equal("memory: room for 3 more", new SeatFacts(12, 2, 20, MemoryRoom: 3).MemoryLine());
        Assert.Equal("server full (20 slots, 3 kept for a, b and c)", new SeatFacts(3, 3, 20, ["a", "b", "c"]).WhyWaiting(1));
    }

    // ================================================================ the hive: group spawns and the whitelist at scale

    private sealed class Subhives : ISubhiveHost
    {
        public List<string> Spawned { get; } = [];
        public string Spawn(string owner, string account, string? task, string? script) { lock (Spawned) Spawned.Add(account); return "ok: spawning"; }
        public string Release(string account) => "ok";
        public bool RetryNow(string account) => true;
        public void AllowOverCap(string account) { }
    }

    [Fact]
    public void A_group_spawn_has_no_per_spawn_cap_and_stops_only_at_the_player_cap()
    {
        var k = new HierarchyKit(maxClones: 4) { MaxPlayers = 20 };
        k.H.Subhives = new Subhives();
        k.H.MaxBodiesPerSpawn = 4; // a brain's per-spawn knob: not a colony's
        lock (k.Online) { k.Online.Remove("poofimafox"); k.Online.Remove("kiwi"); } // both regulars offline: two seats kept
        var (got, why) = k.H.SpawnGroup("story-1", 40, "story_pawn");
        // main is online: 20 − 1 − 2 kept = 17 bodies, each a fresh managed account.
        Assert.Equal(17, got.Count);
        Assert.Equal(17, got.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("player cap: only 2 slot(s) free and 2 kept for offline regulars", why);
        var s = k.H.SeatInfo()!;
        Assert.Equal((2, 2, 20), (s.Free, s.Kept, s.Slots));
        Assert.Equal(["poofimafox", "kiwi"], s.KeptFor);
        Assert.Null(s.MemoryRoom); // no memory ceiling here
        Assert.Equal("server full (20 slots, 2 kept for poofimafox and kiwi)", s.WhyWaiting(23));
    }

    [Fact]
    public async Task Forty_whitelist_adds_go_out_one_at_a_time_and_none_outlives_its_arm()
    {
        var t = new ManualTime();
        var managed = new ManagedAccounts(null);
        var cfg = new HiveAccountsConfig();
        var gate = new CommandGate(new CommandOptions(), () => ["poofimafox"], t);
        var sent = new List<string>();
        int inFlight = 0, most = 0;
        var wl = new WhitelistManager(managed, cfg, "maid_chan", gate.ArmManagedWhitelist, async (line, _) =>
        {
            int now = Interlocked.Increment(ref inFlight);
            lock (sent) most = Math.Max(most, now);
            await Task.Delay(2);
            bool ok = gate.AllowOutgoing(line);
            lock (sent) { if (ok) sent.Add(line); t.Now += TimeSpan.FromSeconds(1); } // main's chat pacing: a line a second
            Interlocked.Decrement(ref inFlight);
            return ok;
        });
        gate.ManagedAccount = wl.Accepts;
        var names = Enumerable.Range(0, 40).Select(i => AccountNames.For(cfg, 101 + i)).ToList();
        foreach (var n in names) managed.Upsert(n, a => a);

        var adds = names.Select(n => wl.AddAsync(n)).ToList();
        Assert.True(wl.Queued > 1); // the burst waits its turn
        var ok = await Task.WhenAll(adds);
        // Before: every line was armed at once and those still queued 15 s later were refused by the gate.
        Assert.All(ok, Assert.True);
        Assert.Equal(40, sent.Count);
        Assert.Equal(1, most);
        Assert.All(names, n => Assert.True(managed.Get(n)!.Whitelisted, n));
        Assert.Equal(0, wl.Queued);
    }
}
