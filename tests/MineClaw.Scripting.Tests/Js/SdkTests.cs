using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>The JS SDK, function by function, against the offline rig (fake actions).</summary>
public class SdkFlowTests
{
    [Fact]
    public async Task Retry_upto_until_seq_sel()
    {
        using var rig = Rig.Js();
        int tries = 0;
        rig.On["mine"] = (_, _) => Task.FromResult(++tries < 3 ? ActResult.Fail("busy") : ActResult.Success("mined 1 dirt"));
        var s = await rig.Run("""
            const r = retry(5, () => mine('dirt', 1))
            const passes = upto(10, i => i < 4)
            let k = 0
            const met = until(() => k >= 3, () => { k++ })
            const a = seq(() => true, () => ({ ok: false, msg: 'stop' }), () => fail('never'))
            const b = sel(() => false, () => ({ ok: false }), () => 'third')
            return [r.ok, passes, met, k, a.msg, b, ok(undefined), ok(null), ok({ ok: false })].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true,4,true,3,stop,third,true,false,false", s.Outcome);
        Assert.Equal(3, tries);
    }

    [Fact]
    public async Task Timeout_cuts_a_long_action_and_unwinds()
    {
        using var rig = Rig.Js();
        rig.On["goto"] = Rig.Blocking(new TaskCompletionSource());
        var s = await rig.Run("""
            const r = timeout(0.4, () => { go('far'); return 'arrived' })
            const q = timeout(5, () => 'quick')
            return r.ok + ' ' + r.msg + ' / ' + q
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("false timeout after 0.4s / quick", s.Outcome);
    }

    [Fact]
    public async Task Room_and_ensure()
    {
        using var rig = Rig.Js();
        int stone = Items.ByName("stone_pickaxe")!.Id;
        rig.On["withdraw"] = (args, _) =>
        {
            if (args.GetProperty("item").GetString() == "stone_pickaxe") rig.Give(10, stone, 1);
            return Task.FromResult(ActResult.Success("took"));
        };
        var s = await rig.Run("room(2)\nreturn ensure(['pickaxe'])");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("stone_pickaxe", s.Outcome);
        var f = await rig.Run("ensure('sword')", "nosword");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.Contains("ensure: no sword", f.Outcome);
    }

    [Fact]
    public async Task Params_merge_defaults_saved_and_run_values()
    {
        var saved = new Dictionary<string, string> { ["tuned"] = """{"n":32,"minFood":10}""" };
        using var rig = Rig.Js(storedParams: n => saved.GetValueOrDefault(n));
        var s = await rig.Run("const P = params({ n: 64, at: 'auto', minFood: 14 })\nreturn `${P.n} ${P.at} ${P.minFood}`", "tuned", @params: """{"at":"6,64,8"}""");
        Assert.Contains("32 6,64,8 10", s.Outcome);
        Assert.Equal("""{"n":32,"at":"6,64,8","minFood":10}""", s.ParamsJson);
        Assert.Contains("\"n\":32", rig.C.Runs.Last().Params); // telemetry records them
        var frozen = await rig.Run("const P = params({ n: 1 })\nP.n = 5\nreturn P.n", "frozen");
        Assert.Equal(ScriptStatus.Failed, frozen.Status); // read-only (strict mode)
    }
}

public class SdkPolicyTests
{
    [Fact]
    public async Task KeepFed_eats_when_hunger_drops_and_the_action_carries_on()
    {
        using var rig = Rig.Js();
        var release = new TaskCompletionSource();
        int gotos = 0;
        rig.On["goto"] = Rig.Blocking(release, () => Interlocked.Increment(ref gotos));
        rig.On["eat"] = (_, _) => { rig.World.Player.SetHealth(20, 20, 5); return Task.FromResult(ActResult.Success("ate bread")); };
        var s = rig.Start("use(keepFed({ below: 14 }))\ngo('far')\nreturn 'arrived'");
        await Rig.Until(() => gotos == 1);
        rig.World.Player.SetHealth(20, 9, 0);
        await Rig.Until(() => rig.CallList.Any(c => c.StartsWith("eat")));
        await Rig.Until(() => gotos == 2); // the walk was cut for the meal and runs again
        release.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.Ok, s.Outcome);
    }

    [Fact]
    public async Task DepositWhenFull_ReplaceToolWhenBroken_PauseAtNight()
    {
        using var rig = Rig.Js();
        int pick = Items.ByName("stone_pickaxe")!.Id, cobble = Items.ByName("cobblestone")!.Id;
        rig.Give(9, pick, 1);
        int mines = 0;
        rig.On["mine"] = (_, _) =>
        {
            mines++;
            if (mines == 2) for (int i = 9; i < 45; i++) rig.Give(i, cobble, 64);            // inventory fills
            if (mines == 4) rig.World.Inventory.SetSlot(0, 9, default);                       // the pickaxe breaks
            if (mines == 6) rig.World.Time = 14000;                                            // night falls
            return Task.FromResult(ActResult.Success("mined 1 cobblestone"));
        };
        rig.On["deposit"] = (_, _) => { for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default); return Task.FromResult(ActResult.Success("deposited")); };
        rig.On["withdraw"] = (_, _) => { rig.Give(11, pick, 1); return Task.FromResult(ActResult.Success("took a pickaxe")); };
        var night = Task.Run(async () => { await Rig.Until(() => rig.World.IsNight, 10000); await Task.Delay(400); rig.World.Time = 1000; });
        var s = await rig.Run("""
            use(depositWhenFull('14,64,-5', ['cobblestone']), replaceToolWhenBroken('pickaxe'), pauseAtNight({ shelter: 'home' }))
            for (let i = 0; i < 8; i++) { expect(mine('cobblestone', 1)); wait(0.25) }
            return 'mined ' + inv('stone_pickaxe')
            """);
        await night;
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(" ; ", rig.Logs));
        Assert.Contains("""deposit {"at":"14,64,-5","items":["cobblestone"]}""", rig.Norm());
        Assert.Contains(rig.CallList, c => c.StartsWith("withdraw") && c.Contains("pickaxe"));
        Assert.Contains("""goto {"to":"home"}""", rig.Norm());
    }

    [Fact]
    public async Task DefendIfAttacked_and_fleeVoidEdges()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxPauseMinutes = 0.05 });
        rig.World.Entities.Spawn(new Entity(77, EntityKind.Mob, new Vec3d(2.5, 65, 0.5), TypeId: MobTypes.Zombie));
        var release = new TaskCompletionSource();
        rig.On["goto"] = (args, ct) => args.GetProperty("to").GetString() == "far" ? Rig.Blocking(release)(args, ct) : Task.FromResult(ActResult.Success("arrived"));
        var s = rig.Start("use(defendIfAttacked(), fleeVoidEdges({ to: 'home' }))\ngo('far')\nreturn 'done'");
        await Rig.Until(() => s.Signals.Has("hurt"));
        rig.World.Player.SetHealth(15, 20, 5);
        await Rig.Until(() => rig.CallList.Any(c => c.StartsWith("attack")));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "falling toward the void", Topic: "void"));
        await Rig.Until(() => rig.Norm().Contains("""goto {"to":"home"}"""));
        release.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("""attack {"target":"zombie"}""", rig.Norm());
    }
}

/// <summary>The deprecated compat helpers (99-compat.js): saved scripts on them keep their exact behaviour.</summary>
public class CompatJobTests
{
    [Fact]
    public async Task MineGen_mines_n_deposits_when_nearly_full_and_at_the_end()
    {
        using var rig = Rig.Js();
        int cobble = Items.ByName("cobblestone")!.Id, pick = Items.ByName("stone_pickaxe")!.Id;
        rig.Give(9, pick, 1);
        int slot = 10;
        rig.On["harvest_generator"] = (args, _) =>
        {
            int n = args.GetProperty("count").GetInt32();
            rig.Give(slot++, cobble, n);
            return Task.FromResult(ActResult.Success($"generator: dug {n}, +{n} cobblestone"));
        };
        rig.On["deposit"] = (_, _) => { for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default); slot = 10; return Task.FromResult(ActResult.Success("deposited")); };
        var s = await rig.Run("return 'mined ' + mineGen({ n: 20, per: 8, chest: '14,64,-5' })");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("mined 20", s.Outcome);
        var gens = rig.Norm().Where(c => c.StartsWith("harvest_generator")).ToList();
        Assert.Equal(["""harvest_generator {"at":"auto","count":8}""", """harvest_generator {"at":"auto","count":8}""", """harvest_generator {"at":"auto","count":4}"""], gens);
        Assert.Contains("""deposit {"at":"14,64,-5","items":["cobblestone"]}""", rig.Norm());
    }

    [Fact]
    public async Task MineGen_waits_out_busy_cells_and_fails_on_real_errors()
    {
        using var rig = Rig.Js();
        rig.Give(9, Items.ByName("stone_pickaxe")!.Id, 1);
        rig.On["harvest_generator"] = (_, _) => Task.FromResult(ActResult.Fail("isn't regenerating cobble for me"));
        var s = await rig.Run("mineGen({ n: 8 })");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("mineGen: isn't regenerating", s.Outcome);
    }

    [Fact]
    public async Task Store_restock_deliver_chopTrees_harvestFarm_waitForDay()
    {
        using var rig = Rig.Js();
        int cobble = Items.ByName("cobblestone")!.Id, log = Items.ByName("log")!.Id;
        rig.On["withdraw"] = (args, _) => { rig.Give(20, cobble, args.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("took")); };
        rig.On["chop_tree"] = (_, _) => { rig.Give(30, log, 4); return Task.FromResult(ActResult.Success("chopped 4 log")); };
        rig.World.SetBlock(3, 65, 3, Blocks.ByName("log")!.Id, 0);
        var s = await rig.Run("""
            store('1,64,1', ['dirt'])
            const held = restock('cobblestone', 16, '14,64,-5')
            const given = deliver('PoofimaFox', 'cobblestone', 16)
            const chopped = chopTrees({ spots: ['3,65,3', '9,65,9'], n: 1, chest: '2,64,2' })
            const farm = harvestFarm({ radius: 6, store: true })
            return [held, given, chopped, farm.ok, waitForDay(5)].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("16,16,1,true,true", s.Outcome);
        var calls = rig.Norm();
        Assert.Contains("""deposit {"at":"1,64,1","items":["dirt"]}""", calls);
        Assert.Contains("""withdraw {"at":"14,64,-5","count":16,"item":"cobblestone"}""", calls);
        Assert.Contains("""skill:give_items {}""", calls.Select(c => c.StartsWith("skill:give_items") ? "skill:give_items {}" : c));
        Assert.Contains("""chop_tree {"at":"3,65,3"}""", calls);
        Assert.DoesNotContain("""chop_tree {"at":"9,65,9"}""", calls); // no log there: skipped
        Assert.Contains(calls, c => c.StartsWith("skill:harvest_wheat"));
    }

    private const string AllFull = "all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag; craft+place a chest (have 300 logs) or keep carrying";

    /// <summary>Live 10-28: every known chest full is the script's decision (place a chest, keep carrying), not a generic throw.</summary>
    [Fact]
    public async Task Store_with_every_chest_full_returns_a_typed_result_and_other_errors_still_fail()
    {
        using var rig = Rig.Js();
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail(AllFull));
        var s = await rig.Run("""
            const r = store('14,64,-5', ['cobblestone'])
            return [r.ok, r.full, r.need].join(',') + ' | ' + r.msg
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("false,true,141 | all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag", s.Outcome);

        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("no path to 14,64,-5"));
        var f = await rig.Run("store('14,64,-5')", "nopath");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.Contains("store: no path to 14,64,-5", f.Outcome);
    }

    [Fact]
    public async Task MineGen_with_every_chest_full_fails_with_the_actionable_line_once_the_bag_is_full_or_hands_it_to_onFull()
    {
        using var rig = Rig.Js();
        int cobble = Items.ByName("cobblestone")!.Id;
        rig.Give(9, Items.ByName("stone_pickaxe")!.Id, 1);
        rig.On["harvest_generator"] = (_, _) =>
        {
            for (int i = 10; i < 45; i++) rig.Give(i, cobble, 64); // the bag fills
            return Task.FromResult(ActResult.Success("generator: dug 8, +8 cobblestone"));
        };
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail(AllFull));
        var f = await rig.Run("mineGen({ n: 64, per: 8, chest: '14,64,-5' })");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.Contains("mineGen: all 2 known chests full", f.Outcome);
        Assert.Contains("or keep carrying", f.Outcome);

        for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default);
        var s = await rig.Run("""
            let seen = null
            const got = mineGen({ n: 8, per: 8, chest: '14,64,-5', onFull: r => { seen = r.need } })
            return 'got ' + got + ' need ' + seen
            """, "onfull");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("need 141", s.Outcome);
    }

    [Fact]
    public async Task GuardArea_fights_at_night_and_stops_at_day()
    {
        using var rig = Rig.Js();
        rig.World.Time = 14000;
        rig.World.Entities.Spawn(new Entity(78, EntityKind.Mob, new Vec3d(3.5, 65, 0.5), TypeId: MobTypes.Skeleton));
        int attacks = 0;
        rig.On["attack"] = (_, _) => { if (++attacks == 2) rig.World.Time = 1000; return Task.FromResult(ActResult.Success("killed skeleton")); };
        var s = await rig.Run("return 'fights ' + guardArea({ radius: 8 })");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("fights 2", s.Outcome);
    }
}

public class SdkLightTests
{
    [Fact]
    public async Task Light_and_canSpawn_read_the_light_model()
    {
        using var rig = Rig.Js();
        // Light updates need every column within 17 blocks loaded (mc-dev areChunksLoaded): add a ring around the rig's 2×2.
        for (int cx = -3; cx <= 2; cx++) for (int cz = -3; cz <= 2; cz++)
            if (rig.World.GetColumn(cx, cz) is not { HasData: true })
                rig.World.ApplyChunkRegion(cx * 16, 0, cz * 16, 16, 128, 16, new byte[16 * 128 * 16], null, null, null);
        rig.World.SetBlock(0, 65, 0, Blocks.ByName("torch")!.Id, 5); // on the rig's stone floor (y64)
        rig.World.Time = 6000;
        var s = await rig.Run("""
            const t = light(0, 65, 0), f = light(pos('5,65,2'))
            return [t.block, t.sky, f.block, f.now, f.night, canSpawn(5, 65, 2), canSpawn(pos('5,65,2'), true), canSpawn(1, 65, 1), block(0, 65, 0)].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        // Manhattan 7 from the torch: block 7; open sky 15 by day (now 15), 15 - 11 = 4 after dark → max 7; spawnable only after dark.
        Assert.Contains("14,15,7,15,7,true,false,false,torch", s.Outcome);
    }
}

public class SdkReferenceTests
{
    [Fact]
    public void Every_public_sdk_global_is_documented_in_the_generated_reference()
    {
        var reference = Sdk.SdkReference.Build();
        var documented = Sdk.SdkReference.Documented();
        // print/console/waitfor are aliases documented with their main name; pos/dist share one line.
        // Compat helpers (99-compat.js, deprecated) run but are never taught.
        var undocumented = Sdk.SdkModules.Globals.Where(g => !documented.Contains(g) && !Sdk.SdkModules.Compat.ContainsKey(g)
            && g is not ("print" or "console" or "waitfor" or "args")).ToList();
        Assert.True(undocumented.Count == 0, "undocumented: " + string.Join(", ", undocumented));
        foreach (var m in Sdk.SdkModules.Current) Assert.Contains("## " + m.Name + ":", reference);
        Assert.Equal(["compat"], Sdk.SdkModules.All.Where(m => m.Deprecated).Select(m => m.Name));
        Assert.Equal(["chopTrees", "deliver", "guardArea", "harvestFarm", "mineGen", "restock", "store", "waitForDay"], Sdk.SdkModules.Compat.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("machine({initial, states:", reference);
        Assert.Contains("- light(x,y,z) or light(pos)", reference);
        Assert.Contains("- canSpawn(x,y,z, now=false)", reference);
        Assert.True(reference.Length < 14_000, $"reference {reference.Length} chars");
    }
}
