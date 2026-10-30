using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Scripting.Crew;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Hive scripts (live 10-29: a 4–20 body hive driven by per-body brain jobs stood idle, sent several bodies to one tree and dropped
/// jobs at the first interruption): ONE coordinator over every body. Offline: K bodies (each a rig with its own world view of the same
/// trees) share one lease board; <c>everyBody</c> runs the role on each body, including one that logs in late; one body is cut off
/// mid-tree (a logout) and its lease goes back at once, so another body chops that tree; no tree is chopped twice.
/// </summary>
public sealed class HiveScriptTests(ITestOutputHelper output)
{
    private static readonly (int X, int Z)[] Trees = [(-5, -5), (-5, 0), (-5, 5), (0, -5), (0, 5), (5, -5), (5, 0), (5, 5), (3, 3)];

    private static string Example() => ScriptPrompts.Unindent(ScriptPrompts.Examples.Single(e => e.Title.StartsWith("a hive script")).Code);

    /// <summary>Three bodies over one world: a chop removes the log from every body's view (the server's one world).</summary>
    /// <param name="axes">Each body already carries an axe (the example's role ensures one first).</param>
    private static (FakeCrew Crew, MineClaw.Hive.Claims.ClaimBoard Board, ConcurrentDictionary<string, string> Chopped) World(ScriptOptions? o = null, bool axes = true)
    {
        var crew = new FakeCrew(o, "67", "69", "105");
        var board = new MineClaw.Hive.Claims.ClaimBoard(TimeSpan.FromSeconds(60));
        var chopped = new ConcurrentDictionary<string, string>();
        var dup = new ConcurrentQueue<string>();
        foreach (var (name, rig) in crew.Rigs)
        {
            if (axes) rig.Give(9, Items.ByName("stone_axe")!.Id, 1);
            var view = board.ViewFor(name);
            rig.Host.Claims = () => view;
            foreach (var (x, z) in Trees) rig.World.SetBlock(x, 65, z, Blocks.Log, 0);
            rig.On["chop_tree"] = async (args, ct) =>
            {
                var at = args.GetProperty("at");
                var key = at.ValueKind == System.Text.Json.JsonValueKind.String ? at.GetString()!
                    : $"{at.GetProperty("x").GetInt32()},{at.GetProperty("y").GetInt32()},{at.GetProperty("z").GetInt32()}";
                await Task.Delay(40, ct);
                if (!chopped.TryAdd(key, name)) return ActResult.Fail($"no log at {key} (chopped by {chopped[key]})");
                var p = key.Split(',').Select(int.Parse).ToArray();
                foreach (var r in crew.Rigs.Values) lock (r.World) r.World.SetBlock(p[0], p[1], p[2], 0, 0);
                return ActResult.Success("chopped oak tree (4 logs)");
            };
        }
        return (crew, board, chopped);
    }

    [Fact]
    public async Task Every_body_takes_the_free_trees_late_joiners_too_and_none_is_chopped_twice()
    {
        var (crew, board, chopped) = World();
        using var _ = crew;
        crew.Online = ["67", "69"]; // 105 logs in a moment later (logins come seconds apart)
        crew.Coming = ["105"];
        using var coordinator = Rig.Js(crew: crew, body: "hive");
        var s = coordinator.Start(Example(), "deforest", @params: """{"rounds":20}""");
        await Rig.Until(() => chopped.Count >= 2, 10000);
        lock (crew) crew.Online.Add("105");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine(s.Outcome);
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", coordinator.Logs));
        Assert.Equal(Trees.Length, chopped.Count); // every tree, each exactly once (a second chop of one would fail and not count)
        Assert.Contains("3/3 bodies done", s.Outcome);
        Assert.Contains(chopped.Values, v => v == "105"); // the late body got work too
        foreach (var b in new[] { "67", "69", "105" })
            Assert.Equal(chopped.Count(kv => kv.Value == b), crew.Rigs[b].CallList.Count(c => c.StartsWith("chop_tree")));
        Assert.DoesNotContain(board.Leases(), l => l.Key.Id.StartsWith("target:", StringComparison.Ordinal) && !chopped.ContainsKey(l.Key.Id["target:".Length..]));
    }

    /// <summary>
    /// Fox 10-30: "the script could even include gathering resources or crafting materials needed or getting the tools necessary". The
    /// example's role prepares itself before it works: 67 takes an axe from a chest, 69 crafts one (chest materials), 105 already has
    /// one; nobody chops before it holds an axe, and the trees are still split one body per tree.
    /// </summary>
    [Fact]
    public async Task Every_body_equips_itself_with_an_axe_first_then_works_the_free_trees()
    {
        var (crew, _, chopped) = World(axes: false);
        using var _ = crew;
        int axe = Items.ByName("stone_axe")!.Id;
        crew.Rigs["105"].Give(9, axe, 1);
        crew.Rigs["67"].On["withdraw"] = (args, _) =>
        {
            if (args.GetProperty("item").GetString() == "stone_axe") crew.Rigs["67"].Give(9, axe, 1);
            return Task.FromResult(ActResult.Success("took"));
        };
        crew.Rigs["69"].On["craft"] = (args, _) =>
        {
            if (args.GetProperty("item").GetString() == "stone_axe") crew.Rigs["69"].Give(9, axe, 1);
            return Task.FromResult(ActResult.Success("crafted"));
        };
        foreach (var (name, rig) in crew.Rigs)
        {
            var chop = rig.On["chop_tree"];
            rig.On["chop_tree"] = (args, ct) => rig.World.Inventory.CountItem(axe, null) > 0 ? chop(args, ct)
                : Task.FromResult(ActResult.Fail($"{name} has no axe")); // a body that skipped its prep would show here
        }
        using var coordinator = Rig.Js(crew: crew, body: "hive");
        var s = coordinator.Start(Example(), "deforest", @params: """{"rounds":20}""");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine(s.Outcome);
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", coordinator.Logs));
        Assert.Equal(Trees.Length, chopped.Count);
        Assert.Contains("3/3 bodies done", s.Outcome);
        Assert.Contains(crew.Rigs["67"].CallList, c => c.StartsWith("withdraw") && c.Contains("stone_axe"));
        Assert.Contains(crew.Rigs["69"].CallList, c => c.StartsWith("craft") && c.Contains("stone_axe"));
        Assert.DoesNotContain(crew.Rigs["105"].CallList, c => c.StartsWith("withdraw") || c.StartsWith("craft"));
        foreach (var rig in crew.Rigs.Values)
        {
            var calls = rig.CallList;
            int firstChop = calls.FindIndex(c => c.StartsWith("chop_tree"));
            if (firstChop >= 0) Assert.DoesNotContain(calls.Skip(firstChop), c => c.StartsWith("withdraw") && c.Contains("_axe") || c.StartsWith("craft"));
        }
    }

    /// <summary>The mining-crew example: each body gets its pickaxe and sword (chests, else crafted) and its torches, then mines its quota.</summary>
    [Fact]
    public async Task The_mining_crew_example_equips_each_body_then_mines()
    {
        var code = ScriptPrompts.Unindent(ScriptPrompts.Examples.Single(e => e.Title.StartsWith("a crew whose bodies equip")).Code);
        using var crew = new FakeCrew("67", "69");
        int pick = Items.ByName("stone_pickaxe")!.Id, sword = Items.ByName("stone_sword")!.Id, torch = Items.ByName("torch")!.Id, cobble = Items.ByName("cobblestone")!.Id;
        foreach (var (name, rig) in crew.Rigs)
        {
            int slot = 12;
            rig.On["withdraw"] = (args, _) =>
            {
                var item = args.GetProperty("item").GetString();
                if (item == "stone_pickaxe") rig.Give(9, pick, 1);
                else if (item == "torch") rig.Give(11, torch, 8);
                return Task.FromResult(ActResult.Success("took"));
            };
            rig.On["craft"] = (args, _) =>
            {
                if (args.GetProperty("item").GetString() == "stone_sword") rig.Give(10, sword, 1);
                return Task.FromResult(ActResult.Success("crafted"));
            };
            rig.On["mine"] = (args, _) =>
            {
                if (rig.World.Inventory.CountItem(pick, null) == 0) return Task.FromResult(ActResult.Fail("no pickaxe"));
                int n = args.GetProperty("count").GetInt32();
                rig.Give(slot++, cobble, n);
                return Task.FromResult(ActResult.Success($"mined {n} stone"));
            };
        }
        using var coordinator = Rig.Js(crew: crew, body: "hive");
        var s = await coordinator.Run(code, "mining_crew", @params: """{"n":16}""");
        output.WriteLine(s.Outcome);
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", coordinator.Logs));
        Assert.Contains("2/2 bodies done (67 16 stone, 69 16 stone)", s.Outcome);
        foreach (var rig in crew.Rigs.Values)
        {
            var calls = rig.CallList;
            int firstMine = calls.FindIndex(c => c.StartsWith("mine"));
            Assert.True(firstMine > calls.FindIndex(c => c.StartsWith("withdraw") && c.Contains("stone_pickaxe")), string.Join(";", calls));
            Assert.True(firstMine > calls.FindIndex(c => c.StartsWith("craft") && c.Contains("stone_sword")), string.Join(";", calls));
            Assert.True(firstMine > calls.FindIndex(c => c.StartsWith("withdraw") && c.Contains("torch")), string.Join(";", calls));
            Assert.Equal(8, rig.World.Inventory.CountItem(torch, null));
        }
    }

    [Fact]
    public async Task A_body_cut_off_mid_tree_gives_its_lease_back_and_another_body_chops_that_tree()
    {
        var (crew, board, chopped) = World();
        using var _ = crew;
        crew.Online = ["67", "69", "105"];
        // 69's first chop hangs (interruptible: walking to the trunk) until it is logged out.
        var stuck = new TaskCompletionSource();
        string? held = null;
        var inner = crew.Rigs["69"].On["chop_tree"];
        crew.Rigs["69"].On["chop_tree"] = async (args, ct) =>
        {
            if (held == null)
            {
                held = args.GetProperty("at").ToString();
                try { await stuck.Task.WaitAsync(ct); } catch (OperationCanceledException) { return ActResult.Fail("cancelled"); }
            }
            return await inner(args, ct);
        };
        using var coordinator = Rig.Js(crew: crew, body: "hive");
        var s = coordinator.Start(Example(), "deforest", @params: """{"rounds":40}""");
        // The others chop everything else and keep looking (69's tree is leased: taken, not exhausted).
        await Rig.Until(() => held != null && chopped.Count == Trees.Length - 1, 15000);
        Assert.Equal(Trees.Length - 1, chopped.Count);
        // 69 logs out: its lane ends mid-tree; the lease goes back at once, not after its TTL.
        lock (crew) crew.Online.Remove("69");
        crew.Rigs["69"].C.Current!.Stop("logged out");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine(s.Outcome + " | " + string.Join(";", coordinator.Logs));
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(Trees.Length, chopped.Count);
        Assert.DoesNotContain(chopped.Values, v => v == "69");
        Assert.Contains("2/2 bodies done", s.Outcome); // 69 left: dropped, not waited for
        Assert.Contains(coordinator.Logs, l => l.Contains("lane 69:"));
    }

    [Fact]
    public async Task A_failed_lane_runs_again_on_its_body_and_until_ends_the_crew()
    {
        using var crew = new FakeCrew("67", "69");
        int tries = 0;
        crew.Rigs["69"].On["mine"] = (_, _) => Task.FromResult(Interlocked.Increment(ref tries) < 3 ? ActResult.Fail("no free cell") : ActResult.Success("mined 1"));
        using var coordinator = Rig.Js(crew: crew, body: "hive");
        var s = await coordinator.Run("""
            function digger() { expect(mine('cobblestone', 1)); crew.inc('n'); return me.body + ' dug' }
            function main() { const r = everyBody('digger', null, { retries: 3, pause: 0.3 }); return `${r.n} ${r.failed} ${JSON.stringify(r.by)}` }
            """, "crew_retry");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("2 0", s.Outcome);
        Assert.Contains("\"69\":\"69 dug\"", s.Outcome);
        Assert.Equal(3, tries); // 69 failed twice, ran again after a pause each time, then dug

        using var crew2 = new FakeCrew("67");
        crew2.Rigs["67"].On["mine"] = Rig.Blocking(new TaskCompletionSource());
        using var c2 = Rig.Js(crew: crew2, body: "hive");
        var u = await c2.Run("""
            function digger() { expect(mine('cobblestone', 1)); return 'never' }
            function main() { let i = 0; return everyBody('digger', null, { until: () => ++i > 2 }).msg }
            """, "crew_until");
        Assert.True(u.Ok, u.Outcome);
        await Rig.Until(() => crew2.Rigs["67"].C.Current == null); // cancelled with the crew
    }

    [Fact]
    public async Task Bodies_still_logging_in_keep_the_crew_going_after_the_early_ones_are_done()
    {
        using var crew = new FakeCrew("67", "69");
        crew.Online = ["67"];
        crew.Coming = ["69"]; // allocated, still logging in
        using var coordinator = Rig.Js(crew: crew, body: "hive");
        var s = coordinator.Start("function digger() { expect(mine('cobblestone', 1)); return me.body + ' dug' }\n" +
            "function main() { return everyBody('digger', null, { wait: 20 }).msg }", "crew_late");
        await Rig.Until(() => crew.Rigs["67"].CallList.Any(c => c.StartsWith("mine")));
        await Task.Delay(2500); // 67 is long done; the crew waits for 69 instead of ending
        Assert.True(s.IsLive, s.Outcome);
        lock (crew) crew.Online.Add("69");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("2/2 bodies done", s.Outcome);
    }

    [Fact]
    public async Task One_body_answers_everyBody_without_waiting()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("function r() {}\nfunction main() { return everyBody('r').msg }", "solo");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("no crew here", s.Outcome);
        var lint = ScriptLint.Check("function main() { return everyBody('nope') }", ["mine"], 6, null, ScriptLanguage.Js);
        Assert.Contains(lint.Errors, e => e.Rule == "crew-unknown-role");
    }

    [Fact]
    public async Task An_unattended_lane_carries_on_once_its_body_is_safe_and_gives_up_if_it_never_is()
    {
        var o = new ScriptOptions { LaneSafeSeconds = 0.3, LaneDangerMaxSeconds = 2, MaxPauseMinutes = 1 };
        using var rig = Rig.Js(o, body: "67");
        bool fighting = true;
        rig.Host.SafetyBusy = () => fighting;
        int walks = 0;
        var gate = new TaskCompletionSource();
        rig.On["goto"] = async (_, ct) =>
        {
            Interlocked.Increment(ref walks);
            try { await gate.Task.WaitAsync(ct); } catch (OperationCanceledException) { return ActResult.Fail("cancelled"); }
            return ActResult.Success("arrived");
        };
        var s = rig.Start("function worker() { expect(go('5,65,5')); return 'there' }", "lane", role: "worker");
        await Rig.Until(() => walks == 1);
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "[67 DANGER] zombie hit, hp 12/20"));
        await Rig.Until(() => s.Status == ScriptStatus.Paused);
        Assert.True(s.AutoResumes);
        await Task.Delay(700);
        Assert.Equal(ScriptStatus.Paused, s.Status); // its reflexes still fight: it waits
        fighting = false;
        await Rig.Until(() => walks == 2, 3000); // safe: the cut walk runs again
        gate.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("there", s.Outcome);
        Assert.Empty(rig.Wakes);

        // Never safe: the lane fails (its coordinator re-runs it later) instead of waiting for a brain that isn't there.
        fighting = true;
        walks = 0;
        gate = new TaskCompletionSource();
        var t = rig.Start("function worker() { expect(go('5,65,5')); return 'there' }", "lane2", role: "worker");
        await Rig.Until(() => walks == 1);
        t.Deliver(new ScriptEvent(ScriptEventKind.Danger, "[67 DANGER] creeper"));
        await t.Completion.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(ScriptStatus.Failed, t.Status);
        Assert.Contains("interrupted: danger", t.Outcome);

        // A run the brain attends (no role) still pauses for the brain.
        walks = 0;
        var m = rig.Start("expect(go('5,65,5'))\nreturn 'x'", "brainy");
        await Rig.Until(() => walks == 1);
        m.Deliver(new ScriptEvent(ScriptEventKind.Danger, "zombie"));
        await Rig.Until(() => m.Status == ScriptStatus.Paused);
        Assert.False(m.AutoResumes);
        fighting = false;
        await Task.Delay(700);
        Assert.Equal(ScriptStatus.Paused, m.Status);
        m.Stop("test over");
        await m.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_crew_is_live_while_its_coordinator_runs()
    {
        Crews.Open("t:1");
        Crews.Open("t:1");
        Crews.Close("t:1");
        Assert.True(Crews.IsLive("t:1"));
        Crews.Close("t:1");
        Assert.False(Crews.IsLive("t:1"));
        Assert.False(Crews.IsLive(null));
    }
}
