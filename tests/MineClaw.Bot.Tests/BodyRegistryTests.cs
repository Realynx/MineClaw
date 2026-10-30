using System.Collections.Concurrent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Bodies;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>A registry host with fake bodies: a tab list, the hive's slot rules, a deposit that empties the inventory.</summary>
internal sealed class FakeBodyHost : IBodyHost
{
    public readonly ToolRegistry Tools = new(400);
    public readonly EventQueue Events = new();
    public readonly List<string> Online = ["maid_chan", "PoofimaFox"];
    public int MaxPlayers = 20;
    public string[] Regulars = ["PoofimaFox"];
    public readonly ConcurrentDictionary<string, TaskCompletionSource<BodyExit>> Sessions = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentQueue<(string Name, DateTime At)> Connects = new();
    public readonly ConcurrentQueue<string> Calls = new();
    public readonly List<(string Name, string Reason, int Items)> StrandedCalls = new();
    public readonly Dictionary<string, Vec3d> Voids = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Said = new();
    public readonly List<GameEvent> Posted = new();
    public Func<IBody, BodyDeposit>? Deposit;
    public int Created;

    public static FakeGameClient Fake(IBody b) => (FakeGameClient)b.Adapter;

    public Body Create(string name)
    {
        Interlocked.Increment(ref Created);
        var fake = new FakeGameClient { Username = name, IsPlaying = false };
        return new Body(name, isMain: false, fake, fake, new ActivityManager(Tools, Events));
    }

    public Task ConnectAsync(IBody body, CancellationToken ct)
    {
        Connects.Enqueue((body.Name, DateTime.UtcNow));
        Sessions[body.Name] = new TaskCompletionSource<BodyExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        Fake(body).IsPlaying = true;
        lock (Online) if (!Online.Contains(body.Name)) Online.Add(body.Name);
        return Task.CompletedTask;
    }

    public async Task<BodyExit> RunAsync(IBody body, CancellationToken ct)
    {
        var tcs = Sessions[body.Name];
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return await tcs.Task;
    }

    /// <summary>The server ends a body's session (a kick, someone else's login).</summary>
    public void End(string name, BodyExit exit, IBody body)
    {
        Fake(body).IsPlaying = false;
        lock (Online) Online.Remove(name);
        Sessions[name].TrySetResult(exit);
    }

    public Task DisconnectAsync(IBody body, string reason)
    {
        Calls.Enqueue($"disconnect {body.Name} items={Items(body)}");
        Fake(body).IsPlaying = false;
        lock (Online) Online.Remove(body.Name);
        if (Sessions.TryGetValue(body.Name, out var t)) t.TrySetResult(BodyExit.Stopped());
        return Task.CompletedTask;
    }

    public BodySlots Slots() { lock (Online) return new BodySlots(MaxPlayers, Online.ToArray()); }

    private (int Free, int Reserved) Room()
    {
        lock (Online) return (MaxPlayers - Online.Count, Regulars.Count(r => !Online.Contains(r, StringComparer.OrdinalIgnoreCase)));
    }

    public BodySlotDecision CanSpawn(int pending)
    {
        var (free, reserved) = Room();
        free -= pending;
        return free - 1 >= reserved ? new(true, $"{free} free") : new(false, free <= 0 ? "server full" : $"only {free} slot(s) free and {reserved} kept for offline regulars");
    }

    public IReadOnlyList<string> MustYield(IReadOnlyList<BodyProgress> running)
    {
        var (free, reserved) = Room();
        int need = reserved - free;
        return need <= 0 ? [] : running.OrderBy(r => r.Progress).ThenByDescending(r => r.Started).Take(need).Select(r => r.Name).ToList();
    }

    public Task<BodyDeposit> DepositAsync(IBody body, TimeSpan timeout, CancellationToken ct)
    {
        Calls.Enqueue($"deposit {body.Name} items={Items(body)}");
        if (Deposit is { } d) return Task.Run(() => d(body));
        int n = Items(body);
        for (int i = 9; i < 45; i++) body.Adapter.World.Inventory.SetSlot(0, i, ItemStack.Empty);
        return Task.FromResult(new BodyDeposit(true, 0, $"inventory empty (deposited {n})"));
    }

    public void Stranded(IBody body, string reason, int remaining) { lock (StrandedCalls) StrandedCalls.Add((body.Name, reason, remaining)); }
    public string? VoidSpot(string name) { lock (Voids) return Voids.TryGetValue(name, out var v) ? $"{(int)Math.Floor(v.X)},{(int)Math.Floor(v.Y)},{(int)Math.Floor(v.Z)}" : null; }
    public void MarkVoid(string name, Vec3d at) { lock (Voids) Voids[name] = at; }
    public void ClearVoid(string name) { lock (Voids) Voids.Remove(name); }
    public IReadOnlyList<string> Trusted() => ["poofimafox", "kiwi"];
    public Task SayAsync(string text) { lock (Said) Said.Add(text); return Task.CompletedTask; }
    public void Post(GameEvent e) { lock (Posted) Posted.Add(e); }

    public string Notices() { lock (Posted) return string.Join(" || ", Posted.Select(p => p.Text)); }

    public static int Items(IBody body) => body.Adapter.World.Inventory.PlayerSlots().Skip(1).Where(s => !s.IsEmpty).Sum(s => s.Count);

    public static void Give(IBody body, int slot, int id, int count) => body.Adapter.World.Inventory.SetSlot(0, slot, new ItemStack((short)id, (byte)count, 0));
}

/// <summary>
/// One brain, many bodies, step 2: the registry spawns clone bodies in-process. Spawn respects the slot policy (main's tab
/// list), spaces logins by the launch gap and never fights someone else's login (Occupied); yield logs out the body furthest
/// from done when an offline regular needs a slot; despawn pauses, deposits (verified empty), then logs out, and leftovers
/// are stranded; shutdown recalls every clone in parallel.
/// </summary>
public class BodyRegistryTests : IAsyncLifetime
{
    private readonly FakeBodyHost _host = new();
    private BodyRegistry _reg = null!;
    private Body _main = null!;

    public Task InitializeAsync()
    {
        var mc = new FakeGameClient { Username = "maid_chan" };
        _main = new Body("maid_chan", isMain: true, mc, mc, new ActivityManager(_host.Tools, _host.Events));
        _reg = new BodyRegistry(_main);
        _reg.Configure(_host, ["maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"], Options(), timers: false);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _reg.StopAllAsync();

    private static BodiesOptions Options() => new()
    {
        LaunchGapSeconds = 0, OwnLogoutGraceSeconds = 0, BackoffBaseSeconds = 0.01, BackoffMaxSeconds = 0.05, ReconnectSeconds = 0,
        DepositTimeoutSeconds = 5, VoidHoldMinutes = 5,
    };

    private static async Task Until(Func<bool> cond, int ms = 5000, string what = "condition")
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return; await Task.Delay(5); }
        Assert.True(cond(), "timed out waiting for " + what);
    }

    private async Task<IBody> SpawnOnline(string name)
    {
        var r = _reg.Spawn(name);
        Assert.True(r.Ok, r.Text);
        Assert.True(await _reg.WaitOnlineAsync(r.Body!, TimeSpan.FromSeconds(5)), "online");
        return r.Body!;
    }

    [Fact]
    public async Task Spawn_adds_an_online_body_the_router_can_scope_and_reuses_it()
    {
        var r = _reg.Spawn("67");
        Assert.StartsWith("ok: 67 spawning", r.Text);
        Assert.True(await _reg.WaitOnlineAsync(r.Body!, TimeSpan.FromSeconds(5)));
        Assert.Equal(BodyState.Online, r.Body!.State);
        Assert.Same(r.Body, _reg.Find("67"));
        Assert.Equal(2, _reg.Count);
        Assert.StartsWith("ok: 67 is already online", _reg.Spawn("maid_chan_67").Text);
        // Unnamed: the first free account.
        var next = _reg.Spawn();
        Assert.Equal("maid_chan_69", next.Body!.Name);
        Assert.StartsWith("error: no body called 77", _reg.Spawn("77").Text);
        Assert.Contains("67 job", _reg.List().Replace("67 idle", "67 job"));
    }

    [Fact]
    public async Task Logins_are_spaced_by_the_launch_gap()
    {
        var o = Options();
        o.LaunchGapSeconds = 0.3;
        o.LaunchBatch = 1; // b1.8.1's throttle: one at a time (JoinPacerTests has the 1.12.2 batches)
        await _reg.StopAllAsync();
        var mc = new FakeGameClient { Username = "maid_chan" };
        var reg = new BodyRegistry(new Body("maid_chan", true, mc, mc, new ActivityManager(_host.Tools, _host.Events)));
        reg.Configure(_host, ["maid_chan_67", "maid_chan_69", "maid_chan_420"], o, timers: false);
        try
        {
            foreach (var n in new[] { "67", "69", "420" }) Assert.True(reg.Spawn(n).Ok);
            await Until(() => _host.Connects.Count == 3, what: "three logins");
            var at = _host.Connects.Select(c => c.At).OrderBy(t => t).ToList();
            Assert.True((at[1] - at[0]).TotalSeconds >= 0.25, $"gap 1: {(at[1] - at[0]).TotalSeconds:0.00}s");
            Assert.True((at[2] - at[1]).TotalSeconds >= 0.25, $"gap 2: {(at[2] - at[1]).TotalSeconds:0.00}s");
        }
        finally { await reg.StopAllAsync(); }
    }

    [Fact]
    public void Spawn_is_refused_when_the_slot_policy_keeps_the_last_slots_for_offline_regulars()
    {
        _host.MaxPlayers = 4;
        _host.Regulars = ["PoofimaFox", "kiwi"]; // kiwi offline: one slot kept; 2 online + 1 more = 3, 1 left for kiwi
        Assert.True(_reg.Spawn("67").Ok);
        var no = _reg.Spawn("69");
        Assert.False(no.Ok);
        Assert.StartsWith("no: ", no.Text);
        Assert.Contains("kept for offline regulars", no.Text);
        Assert.Equal(1, _host.Created);
    }

    [Fact]
    public async Task Yield_logs_out_the_body_furthest_from_done_when_a_regular_needs_a_slot()
    {
        _host.MaxPlayers = 5;
        _host.Regulars = ["PoofimaFox", "kiwi"];
        var b67 = await SpawnOnline("67");
        await Task.Delay(20);
        var b69 = await SpawnOnline("69"); // 4 online + kiwi reserved = full, nothing to yield yet
        Assert.Empty(_reg.YieldTick());
        lock (_host.Online) _host.Online.Add("randomguy"); // the server filled up while kiwi is offline
        var yielded = _reg.YieldTick();
        Assert.Equal(["maid_chan_69"], yielded); // both at 0 % progress: the most recently started goes first
        await Until(() => _reg.Find("69") == null, what: "69 logged out");
        Assert.Equal(BodyState.Offline, b69.State);
        Assert.NotNull(_reg.Find("67"));
        await Until(() => _host.Notices().Contains("[bodies] 69 logged out (making room for a player)"), what: "logout notice");
        Assert.Equal(BodyState.Online, b67.State);
    }

    [Fact]
    public async Task Despawn_pauses_deposits_verifies_empty_then_logs_out()
    {
        var body = await SpawnOnline("420");
        FakeBodyHost.Give(body, 36, Blocks.Cobblestone, 64);
        FakeBodyHost.Give(body, 37, Items.StonePickaxe, 1);
        var r = await _reg.DespawnAsync("420", "recalled");
        Assert.StartsWith("ok: 420 logged out (recalled): inventory empty (deposited 65)", r);
        // The deposit ran while it was still online, the logout came after with nothing left on it.
        Assert.Equal(["deposit maid_chan_420 items=65", "disconnect maid_chan_420 items=0"], _host.Calls.Where(c => c.Contains("420")).Take(2).ToList());
        Assert.Null(_reg.Find("420"));
        Assert.Equal(BodyState.Offline, _reg.StateOf("420"));
        lock (_host.StrandedCalls) Assert.Equal(("maid_chan_420", "logout: recalled", 0), Assert.Single(_host.StrandedCalls));
        // It can be spawned again (the same body object, no second build).
        Assert.True((await SpawnOnline("420")) is var again && ReferenceEquals(again, body));
        Assert.Equal(1, _host.Created);
    }

    [Fact]
    public async Task Despawn_with_items_it_could_not_put_away_records_them_as_stranded()
    {
        var body = await SpawnOnline("1337");
        FakeBodyHost.Give(body, 36, Blocks.Cobblestone, 48);
        _host.Deposit = b => new BodyDeposit(true, 0, "inventory empty (stale)"); // a stale "empty": the registry checks itself
        var r = await _reg.DespawnAsync("1337", "task failed");
        Assert.Contains("48 item(s) still held", r);
        lock (_host.StrandedCalls) Assert.Equal(("maid_chan_1337", "logout: task failed", 48), Assert.Single(_host.StrandedCalls));
        Assert.Null(_reg.Find("1337"));
    }

    [Fact]
    public async Task Shutdown_recalls_every_clone_in_parallel()
    {
        var gate = new TaskCompletionSource();
        int inDeposit = 0, maxParallel = 0;
        _host.Deposit = b =>
        {
            int n = Interlocked.Increment(ref inDeposit);
            lock (_host) maxParallel = Math.Max(maxParallel, n);
            gate.Task.Wait(2000);
            Interlocked.Decrement(ref inDeposit);
            return new BodyDeposit(true, 0, "inventory empty (deposited 0)");
        };
        foreach (var n in new[] { "67", "69", "420" }) await SpawnOnline(n);
        var all = _reg.DespawnAllAsync("main shutting down", TimeSpan.FromSeconds(10));
        await Until(() => Volatile.Read(ref inDeposit) == 3, what: "three deposits at once");
        gate.SetResult();
        await all;
        Assert.Equal(3, maxParallel);
        Assert.Equal(1, _reg.Count);
        Assert.StartsWith("error: shutting down", _reg.Spawn("67").Text);
    }

    // ---------------------------------------------------------------- someone else's login (ported from ExternalLoginTests)

    [Fact]
    public void A_clone_name_on_mains_tab_list_that_isnt_ours_is_skipped_before_login()
    {
        lock (_host.Online) _host.Online.Add("maid_chan_67"); // Fox's viewer
        var named = _reg.Spawn("67");
        Assert.False(named.Ok);
        Assert.StartsWith("no: 67 is in use by a player's viewer (maid_chan_67 is online but isn't one of ours)", named.Text);
        Assert.Contains("69, 420, 1337 are free", named.Text);
        Assert.Equal("maid_chan_69", _reg.Spawn().Body!.Name);
        Assert.Contains("[bodies] 67 is in use by a player's viewer", _host.Notices());
        _reg.Spawn("67");
        lock (_host.Posted) Assert.Single(_host.Posted, p => p.Text.Contains("67 is in use")); // told once
        lock (_host.Online) _host.Online.Remove("maid_chan_67");
        Assert.True(_reg.Spawn("67").Ok); // the viewer left: free again, no cooldown (ours was never kicked)
    }

    [Fact]
    public async Task A_duplicate_login_kick_makes_the_body_Occupied_for_the_cooldown_and_never_reconnects()
    {
        var body = await SpawnOnline("1337");
        FakeBodyHost.Give(body, 36, Blocks.Cobblestone, 10);
        body.Activities.GetType(); // a job would be cancelled here; the order is held
        int connects = _host.Connects.Count;
        _host.End("maid_chan_1337", new BodyExit(BodyExitKind.ExternalLogin, "You logged in from another location"), body);
        await Until(() => _reg.StateOf("1337") == BodyState.Occupied && _reg.Find("1337") == null, what: "occupied");
        Assert.Empty(_reg.RunnerOf("1337")!.Crashes);
        Assert.InRange(_reg.OccupiedUntil("1337") - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9.5), TimeSpan.FromMinutes(10.1));
        await Until(() => _host.Notices().Contains("[bodies] 1337 is in use by a player's viewer: someone logged in as maid_chan_1337"), what: "notice");
        Assert.Contains("not using it for 10 min (10 item(s) still on it)", _host.Notices());
        lock (_host.StrandedCalls) Assert.Contains(_host.StrandedCalls, s => s.Name == "maid_chan_1337" && s.Items == 10 && s.Reason.StartsWith("kicked: someone else"));
        await Task.Delay(100);
        Assert.Equal(connects, _host.Connects.Count); // no fight for the account
        Assert.StartsWith("no: 1337 is in use by a player's viewer (someone else logged in as maid_chan_1337)", _reg.Spawn("1337").Text);
        Assert.Contains("1337 in use by a player's viewer", _reg.List());
    }

    [Fact]
    public async Task A_plain_kick_reconnects_the_body_alone()
    {
        var b67 = await SpawnOnline("67");
        var b69 = await SpawnOnline("69");
        _host.End("maid_chan_67", BodyExit.Lost("kicked: Flying is not enabled on this server"), b67);
        await Until(() => _host.Connects.Count(c => c.Name == "maid_chan_67") == 2 && b67.State == BodyState.Online, 5000, "67 reconnected");
        Assert.Equal(1, _host.Connects.Count(c => c.Name == "maid_chan_69"));
        Assert.Equal(BodyState.Online, b69.State);
    }

    // ---------------------------------------------------------------- the void hold (ported from LiveSession5VoidSpawnTests)

    [Fact]
    public async Task A_body_holding_over_the_void_asks_once_tells_the_brain_pauses_and_resumes_after_the_tp()
    {
        var body = await SpawnOnline("420");
        _reg.OnVoidHold(body, new Vec3d(-5.5, 0.594, -8.5));
        _reg.OnVoidHold(body, new Vec3d(-5.5, 0.594, -8.5)); // the same hold again (a second 0x0D over the void): no second ask
        lock (_host.Said)
            Assert.Equal("poofimafox, kiwi: 420 spawned over the void at -6,0,-9, can someone /tp maid_chan_420 to me?", Assert.Single(_host.Said));
        Assert.Contains("[bodies] 420 spawned over the void at -6,0,-9 and is holding still", _host.Notices());
        Assert.Contains("/tp maid_chan_420 maid_chan", _host.Notices());
        Assert.NotNull(_host.VoidSpot("maid_chan_420")); // the saved login spot is marked
        Assert.Equal(new Vec3d(-5.5, 0.594, -8.5), _reg.VoidHoldOf("420"));
        Assert.Contains("HOLDING over the void", _reg.List());

        _reg.OnVoidHoldEnded(body, new Vec3d(8.5, 64, -5.5)); // Fox: /tp maid_chan_420 maid_chan
        Assert.Null(_host.VoidSpot("maid_chan_420"));
        Assert.Null(_reg.VoidHoldOf("420"));
        Assert.Contains("[bodies] 420 landed on ground at 8,64,-6", _host.Notices());
        lock (_host.Said) Assert.Single(_host.Said);
        lock (_host.Posted) Assert.All(_host.Posted.Where(p => p.Text.Contains("void")), p => Assert.Equal("420", p.Body));
    }

    [Fact]
    public void A_body_is_never_logged_in_over_its_saved_void_spot_before_a_player_confirms()
    {
        _host.MarkVoid("maid_chan_420", new Vec3d(-5.5, 0.62, -8.5)); // 07:49:41 it logged out over the void
        var no = _reg.Spawn("420");
        Assert.False(no.Ok);
        Assert.Contains("would log in over the void at -6,0,-9", no.Text);
        Assert.Contains("/tp maid_chan_420", no.Text);
        Assert.NotEqual("maid_chan_420", _reg.Spawn().Body!.Name); // unnamed: another free body, never 420
        Assert.True(_reg.ConfirmVoid("420")); // a trusted player: "log 420 in, I'll tp it to you"
        Assert.True(_reg.Spawn("420").Ok);
        Assert.True(_reg.Spawn("1337", confirmVoid: true).Ok);
    }

    [Fact]
    public async Task A_body_stuck_over_the_void_too_long_logs_out_and_wakes_the_brain()
    {
        var o = Options();
        o.VoidHoldMinutes = 0.005; // 0.3 s
        await _reg.StopAllAsync();
        var mc = new FakeGameClient { Username = "maid_chan" };
        var reg = new BodyRegistry(new Body("maid_chan", true, mc, mc, new ActivityManager(_host.Tools, _host.Events)));
        reg.Configure(_host, ["maid_chan_420"], o, timers: false);
        try
        {
            var r = reg.Spawn("420");
            Assert.True(await reg.WaitOnlineAsync(r.Body!, TimeSpan.FromSeconds(5)));
            reg.OnVoidHold(r.Body!, new Vec3d(-5.5, 0.6, -8.5));
            await Until(() => reg.Find("420") == null, 5000, "logged out");
            Assert.Contains("stuck over the void at -6,0,-9", _host.Notices());
            lock (_host.Posted) Assert.Contains(_host.Posted, p => p.MustWake && p.Text.Contains("stuck over the void"));
        }
        finally { await reg.StopAllAsync(); }
    }
}
