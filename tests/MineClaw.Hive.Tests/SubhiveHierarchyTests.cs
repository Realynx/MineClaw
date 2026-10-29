using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.Hive.Slots;
using Xunit.Abstractions;

namespace MineClaw.Hive.Tests;

/// <summary>
/// Subhive bodies (live 10-28 02:03, Fox: "spawn up a deepseek hive, to deforest this whole area, 15 agents" = ONE DeepSeek hive
/// driving 15 bodies): main's own bodies in its process, a hive's bodies in the hive's process (accounts allocated and whitelisted
/// by main, hosted over the bus), the release cascade with deposit before logout, the resource-aware ceiling and the player cap.
/// </summary>
public class SubhiveHierarchyTests(ITestOutputHelper output)
{
    /// <summary>Main's in-process host as the hierarchy sees it: logs in after the whitelist add, deposits before logout.</summary>
    private sealed class FakeLocalHost(HierarchyKit kit) : ISubhiveHost
    {
        public List<(string Account, string? Task)> Spawned { get; } = [];

        public string Spawn(string owner, string account, string? task, string? script)
        {
            lock (Spawned) Spawned.Add((account, task));
            _ = Task.Run(async () =>
            {
                await kit.H.BeforeLaunchAsync(account); // InProcBodies.BeforeConnect: whitelisted before the first login
                kit.Note("login " + account);
            });
            return $"ok: {HiveNode.ShortOf(account)} spawning";
        }

        public string Release(string account)
        {
            _ = Task.Run(async () =>
            {
                kit.Note("deposit " + account);
                await Task.Delay(20);
                kit.Note("logout " + account);
                kit.H.Released(account, "released"); // InProcBodies.Gone
            });
            return "ok: logging out";
        }
    }

    [Fact]
    public async Task Main_spawns_its_own_bodies_whitelisted_before_login_and_releases_them_after_a_deposit()
    {
        var k = new HierarchyKit();
        var host = new FakeLocalHost(k);
        k.H.Subhives = host;
        // "use 3 agents to chop the oaks": no kind, no brain = bodies main's own brain drives (not three clone processes).
        var r = k.H.Spawn(new SpawnRequest(Task: "chop the oaks", Bodies: 3));
        output.WriteLine(r);
        Assert.StartsWith("ok: 3 subhive bodies (101-103) your brain drives", r);
        Assert.Contains("none in game yet", r); // the reply can't claim more than exists
        Assert.Empty(k.Bodies.Spawns);           // no clone process at all
        Assert.Equal(3, k.H.Tree.All().Count(n => n is { Kind: CloneKind.Subhive, Owner: HiveTree.Root }));
        Assert.StartsWith("3 subhive bodies: 101-103", k.H.Tree.Render());
        await Wait.Until(() => k.LogCopy().Count(l => l.StartsWith("login")) == 3, 10, "3 logins");
        var log = k.LogCopy();
        foreach (var n in new[] { "101", "102", "103" })
            Assert.True(log.IndexOf($"/whitelist add maid_chan_{n}") is >= 0 and var i && i < log.IndexOf($"login maid_chan_{n}"), n + " whitelisted first");
        Assert.All(k.Managed.All(), a => Assert.True(a.Active && a.Whitelisted));

        Assert.Contains("releasing 101-103", k.H.Release("all"));
        await Wait.Until(() => k.H.Tree.Count == 0, 10, "all released");
        await Wait.Until(() => k.LogCopy().Count(l => l.StartsWith("/whitelist remove")) == 3, 10);
        log = k.LogCopy();
        output.WriteLine(string.Join("\n", log));
        foreach (var n in new[] { "101", "102", "103" })
        {
            Assert.True(log.IndexOf($"deposit maid_chan_{n}") < log.IndexOf($"logout maid_chan_{n}"), n + " deposits before logout");
            Assert.True(log.IndexOf($"logout maid_chan_{n}") < log.IndexOf($"/whitelist remove maid_chan_{n}"), n + " off the whitelist after logout");
        }
        Assert.All(k.Managed.All(), a => Assert.False(a.Active || a.Whitelisted));
        // Without a host it says so instead of pretending.
        var off = new HierarchyKit();
        Assert.Contains("subhive bodies aren't available here", off.H.Spawn(new SpawnRequest(Kind: "subhive", Task: "x")));
        Assert.StartsWith("ok", off.H.Spawn(new SpawnRequest(Task: "x"))); // no host: the old path (a clone process) still works
    }

    [Fact]
    public void A_deepseek_hive_with_15_bodies_is_one_process_or_an_honest_no()
    {
        var k = new HierarchyKit();
        k.H.Subhives = new FakeLocalHost(k);
        k.H.ProfileCheck = _ => "ollama at 10.1.2.44 unreachable";
        var no = k.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "deforest this whole area", Bodies: 15));
        Assert.Equal("no: brain deepseek not available (ollama at 10.1.2.44 unreachable); use brain opus; spawned nothing", no);
        Assert.Empty(k.Bodies.Spawns);

        k.H.ProfileCheck = p => p == "deepseek" ? null : "no such profile";
        var r = k.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "deforest this whole area", Bodies: 15));
        output.WriteLine(r);
        Assert.Contains("ONE DeepSeek hive whose brain will drive 15 subhive bodies", r);
        Assert.Contains("none in game yet", r);
        var (acc, task, opts) = Assert.Single(k.Bodies.Spawns); // one process, not 15
        Assert.Equal(("deepseek", "hive", 15, "deforest this whole area"), (opts.Profile, opts.Kind, opts.Bodies, task));
        Assert.Equal(BrainKind.DeepSeek, k.H.Tree.Get(acc)!.Brain);
    }

    [Fact]
    public void The_resource_ceiling_and_the_player_cap_say_how_many_bodies_fit()
    {
        // Memory: 4096 MB limit, 3000 MB used → 696 MB free; the hive process takes 380 → 316 MB = 7 bodies of 40 MB.
        var k = new HierarchyKit();
        k.H.ProfileCheck = _ => null;
        var cfg = new HiveResourceConfig { BodyMb = 40, HiveProcessMb = 380, MainProcessMb = 650, ReserveMb = 400 };
        k.H.Resources = new ResourceCeiling(cfg, k.H.Use, () => new MemoryProbe(4096, 3000, "test"));
        var r = k.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "deforest", Bodies: 15));
        output.WriteLine(r);
        Assert.Contains("will drive 7 subhive bodies", r);
        Assert.Contains("only 7 of 15 bodies fit (memory: room for 7)", r);
        Assert.Equal(7, k.Bodies.Spawns.Single().Opts.Bodies);
        // Full memory: nothing spawns, and the answer says why.
        var full = new HierarchyKit();
        full.H.ProfileCheck = _ => null;
        full.H.Resources = new ResourceCeiling(cfg, full.H.Use, () => new MemoryProbe(4096, 3700, "test"));
        Assert.StartsWith("no: no room for 15 bodies (memory:", full.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "x", Bodies: 15)));
        Assert.Empty(full.Bodies.Spawns);
        // A body counts in the estimate: 3 bodies at 40 MB on top of main (650) with 400 kept → room shrinks by 3.
        var subs = new HierarchyKit();
        subs.H.Subhives = new FakeLocalHost(subs);
        subs.H.Resources = new ResourceCeiling(cfg, subs.H.Use, () => new MemoryProbe(1250, 0, "test")); // 1250-400-650 = 200 MB = 5 bodies
        var five = subs.H.Spawn(new SpawnRequest(Task: "x", Bodies: 8));
        output.WriteLine(five);
        Assert.StartsWith("ok: 5 subhive bodies", five);
        Assert.Contains("only 5 of 8: memory:", five);

        // The player cap (max 10, 3 online → 7 free; the hive takes one, so 6 bodies), and a regular's slot is kept.
        var cap = new HierarchyKit { MaxPlayers = 10 };
        cap.H.ProfileCheck = _ => null;
        var c = cap.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "deforest", Bodies: 15));
        output.WriteLine(c);
        Assert.Contains("only 6 of 15 bodies fit (player cap: 6 slot(s) after the hive)", c);
    }

    [Fact]
    public void Cgroup_limits_are_read_v2_then_v1()
    {
        var v2 = new Dictionary<string, string>
        {
            ["/sys/fs/cgroup/memory.max"] = "4294967296\n",
            ["/sys/fs/cgroup/memory.current"] = "1073741824\n",
            ["/sys/fs/cgroup/memory.stat"] = "anon 900000000\ninactive_file 104857600\nactive_file 5\n",
        };
        var p = ResourceCeiling.ProbeFrom("/sys/fs/cgroup", v2.ContainsKey, f => v2[f]);
        Assert.Equal(("cgroup v2", 4096.0, 924.0), (p.Source, p.LimitMb, p.UsedMb));
        var v1 = new Dictionary<string, string>
        {
            ["/sys/fs/cgroup/memory.max"] = "max",
            ["/sys/fs/cgroup/memory/memory.limit_in_bytes"] = "2147483648",
            ["/sys/fs/cgroup/memory/memory.usage_in_bytes"] = "536870912",
        };
        p = ResourceCeiling.ProbeFrom("/sys/fs/cgroup", v1.ContainsKey, f => v1[f]);
        Assert.Equal(("cgroup v1", 2048.0, 512.0), (p.Source, p.LimitMb, p.UsedMb));
        // An LXC container (alice's CT 114): memory.max is "max" inside, lxcfs puts the real limit in /proc/meminfo.
        var lxc = new Dictionary<string, string>
        {
            ["/sys/fs/cgroup/memory.max"] = "max",
            ["/proc/meminfo"] = "MemTotal:        4194304 kB\nMemFree:         3318364 kB\nMemAvailable:    4088368 kB\n",
        };
        p = ResourceCeiling.ProbeFrom("/sys/fs/cgroup", lxc.ContainsKey, f => lxc[f]);
        Assert.Equal(("meminfo", 4096.0), (p.Source, p.LimitMb));
        Assert.Equal(103.5, p.UsedMb, 1);
        // Nothing there: the machine's view (never throws).
        Assert.Equal("machine", ResourceCeiling.ProbeFrom("/nope", _ => false, _ => "", "/nope/meminfo").Source);
    }

    [Fact]
    public async Task A_hive_hosts_its_bodies_over_the_bus_and_its_release_cascades_bodies_first()
    {
        var k = new HierarchyKit();
        var bus = new HiveBus(HierarchyKit.Main) { MainHandler = k.H.HandleBusAsync };
        k.H.Bus = bus;
        k.H.RemoteSubhives = true;
        k.H.ProfileCheck = _ => null;
        Assert.StartsWith("ok", k.H.Spawn(new SpawnRequest(Kind: "hive", Brain: "deepseek", Task: "deforest", Bodies: 3))); // 101, its own process
        const string Hive = "maid_chan_101";
        string token = "";
        // The hive process's side (CloneMode + SubhiveWiring): host = log the allocated account in; release = deposit, log out, report gone.
        token = bus.Register(Hive, (e, ct) =>
        {
            switch (e.Type)
            {
                case BusTypes.Host when e.As<HostReq>() is { } h:
                    k.Note("host " + h.Account);
                    return Task.FromResult(BusReply.Of($"ok: {HiveNode.ShortOf(h.Account)} spawning"));
                case BusTypes.Release when e.As<ReleaseReq>() is { } rr:
                    _ = PutAwayAsync(rr.Id);
                    return Task.FromResult(BusReply.Of("ok"));
            }
            return Task.FromResult(new BusReply(true));
        });

        async Task PutAwayAsync(string id)
        {
            await Task.Yield();
            k.Note("deposit " + id);
            await Task.Delay(20);
            k.Note("logout " + id);
            await bus.RouteAsync(BusEnvelope.Make(BusTypes.SubhiveGone, Hive, "main", new SubhiveGoneMsg(id, "released"), token), Hive);
        }

        // In game, the hive asks main for the bodies its task came with.
        var reply = await bus.RouteAsync(BusEnvelope.Make(BusTypes.Spawn, Hive, "main", new SpawnReq(Kind: "subhive", Bodies: 3, Reset: true), token), Hive);
        var text = reply.As<string>()!;
        output.WriteLine(text);
        Assert.StartsWith("ok: 3 subhive bodies (102-105) for 101's brain", text); // 104 is quantity-like, never handed out
        await Wait.Until(() => k.LogCopy().Count(l => l.StartsWith("host ")) == 3, 10, "3 hosted");
        var log = k.LogCopy();
        foreach (var n in new[] { "102", "103", "105" })
            Assert.True(log.IndexOf($"/whitelist add maid_chan_{n}") is >= 0 and var i && i < log.IndexOf($"host maid_chan_{n}"), n + ": main whitelists, then the hive logs in");
        Assert.All(new[] { "maid_chan_102", "maid_chan_103", "maid_chan_105" }, a => Assert.Equal(Hive, k.H.Tree.Get(a)!.Owner));
        Assert.Equal("101 hive deepseek; 3 subhive bodies of 101: 102-105", k.H.Tree.Render().Replace(" (of 101)", ""));
        Assert.Equal("3 subhive bodies: 102-105", (await bus.RouteAsync(BusEnvelope.Make(BusTypes.List, Hive, "main", null, token), Hive)).As<string>());

        // Only the hosting hive may report a body gone (and so get it off the whitelist).
        var otherTok = bus.Register("maid_chan_106", (_, _) => Task.FromResult(new BusReply(true)));
        Assert.False((await bus.RouteAsync(BusEnvelope.Make(BusTypes.SubhiveGone, "maid_chan_106", "main", new SubhiveGoneMsg("102", "x"), otherTok), "maid_chan_106")).Ok);

        // A crash restart asks again with fewer: the old accounts are reused, the extra one released.
        var again = await bus.RouteAsync(BusEnvelope.Make(BusTypes.Spawn, Hive, "main", new SpawnReq(Kind: "subhive", Bodies: 2, Reset: true), token), Hive);
        Assert.StartsWith("ok: 2 subhive bodies (102, 103)", again.As<string>());
        await Wait.Until(() => k.H.Tree.Get("maid_chan_105") == null && k.LogCopy().Contains("/whitelist remove maid_chan_105"), 10);

        // Releasing the hive: its bodies deposit and log out first (over the bus), then the hive itself.
        var rel = k.H.Release("101");
        output.WriteLine(rel);
        Assert.Contains("after 2 child clone(s) (102, 103)", rel);
        await Wait.Until(() => k.H.Tree.Count == 0, 15, "all released");
        await Wait.Until(() => k.LogCopy().Contains("/whitelist remove maid_chan_101"), 10);
        log = k.LogCopy();
        output.WriteLine(string.Join("\n", log));
        foreach (var n in new[] { "102", "103" })
        {
            Assert.True(log.IndexOf($"deposit maid_chan_{n}") < log.IndexOf($"logout maid_chan_{n}"), n + " deposits before logout");
            Assert.True(log.IndexOf($"logout maid_chan_{n}") < log.IndexOf($"/whitelist remove maid_chan_{n}"), n + " off the whitelist after logout");
            Assert.True(log.IndexOf($"logout maid_chan_{n}") < log.IndexOf("deposit maid_chan_101"), n + " is gone before its hive deposits");
        }
        Assert.All(k.Managed.All(), a => Assert.False(a.Active || a.Whitelisted));
    }

    [Fact]
    public void The_hive_tool_maps_agents_onto_bodies_within_its_budget()
    {
        // The cached line teaches the two shapes the live request needed.
        Assert.Contains("hive spawn bodies=N: N bodies YOUR brain drives", HiveTools.KindHint);
        Assert.Contains("kind=hive brain=deepseek bodies=15: ONE DeepSeek hive driving 15 bodies", HiveTools.KindHint);
        Assert.Contains("Report only what spawned", HiveTools.KindHint);
        Assert.True(HiveTools.KindHint.Length <= 220);
        Assert.Contains("bodies=N", CloneHierarchyDescription());
    }

    private static string CloneHierarchyDescription() => CloneHiveTool.Description;
}
