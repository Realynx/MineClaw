using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>The SDK's composable primitives (find, item, gather, quota, forEach, patrol, stash, handTo, makeRoom, waitFor(fn)), one by one, on the offline rig's fakes.</summary>
public class PrimitiveTests
{
    private static int Id(string name) => Items.ByName(name)!.Id;

    [Fact]
    public async Task Find_lists_blocks_mobs_players_and_places_nearest_first_with_a_filter()
    {
        using var rig = Rig.Js();
        rig.World.SetBlock(4, 65, 4, Blocks.Log, 0);
        rig.World.SetBlock(-2, 65, 0, Blocks.Log, 2);
        rig.World.SetBlock(2, 65, -2, Blocks.Crops, 3);
        rig.World.SetBlock(3, 65, -2, Blocks.Crops, 7);
        rig.World.Entities.Spawn(new Entity(77, EntityKind.Mob, new Vec3d(2.5, 65, 0.5), TypeId: MobTypes.Zombie));
        rig.World.Entities.Spawn(new Entity(78, EntityKind.Mob, new Vec3d(5.5, 65, 0.5), TypeId: MobTypes.Cow));
        rig.World.Entities.Spawn(new Entity(79, EntityKind.Player, new Vec3d(-3.5, 65, 0.5), Name: "kiwi"));
        var s = await rig.Run("""
            const logs = find('log')
            const ripe = find('wheat', { filter: t => t.meta >= 7 })
            const all = find('wheat')
            const z = find('zombie'), h = find('hostile'), mobs = find('mob'), p = find('player'), d = find('kiwi')
            const home = find('home'), none = find('diamond_ore', { r: 8 })
            return [logs.map(t => `${t.x},${t.z}:${t.kind}`).join(' '), logs[0].d <= logs[1].d, ripe.length, ripe[0].x, all.length,
              z[0].kind + ':' + z[0].name, h.length, mobs.length, p[0].name, d.length, home[0].kind + '@' + home[0].x, none.length].join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("-2,0:block 4,4:block|true|1|3|2|mob:zombie|1|2|kiwi|1|place@5|0", s.Outcome);
    }

    [Fact]
    public async Task Item_resolves_names_through_the_version_game_data()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            const p = item('wood_pickaxe'), st = item('stick'), c = item('cobblestone')
            return [p.name, p.version, st.craftable, c.craftable, c.block, item('no_such_thing')].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("wooden_pickaxe,b1.8.1,true,false,true,", s.Outcome);
    }

    [Fact]
    public async Task Gather_uses_the_inventory_first_then_chests_then_craft()
    {
        using var rig = Rig.Js();
        rig.Give(9, Id("cobblestone"), 20);
        rig.On["withdraw"] = (args, _) => { rig.Give(20, Id(args.GetProperty("item").GetString()!), args.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("took")); };
        rig.On["craft"] = (args, _) => { rig.Give(21, Id("stick"), args.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("crafted")); };
        var s = await rig.Run("""
            const held = gather('cobblestone', 16)
            const fromChest = gather('log', 8, { chest: '14,64,-5' })
            const crafted = gather('stick', 4, { from: 'craft' })
            const noRecipe = gather('cobblestone', 64, { from: 'craft' })
            return [held.ok, held.n, held.have, fromChest.ok, fromChest.n, crafted.ok, crafted.n, noRecipe.ok].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true,0,20,true,8,true,4,false", s.Outcome);
        var calls = rig.Norm();
        Assert.Equal("""withdraw {"at":"14,64,-5","count":8,"item":"log"}""", calls[0]);
        Assert.Contains("""craft {"count":4,"item":"stick"}""", calls);
        Assert.Single(calls, c => c.StartsWith("craft")); // cobblestone has no recipe: the craft route is skipped
    }

    [Fact]
    public async Task Gather_finds_the_source_in_the_world_mine_harvest_kill_or_your_own_route()
    {
        using var rig = Rig.Js();
        rig.On["mine"] = (args, _) => { rig.Give(10, Id("cobblestone"), args.GetProperty("count").GetInt32() + rig.World.Inventory.CountItem(Id("cobblestone"))); return Task.FromResult(ActResult.Success("mined")); };
        int harvests = 0;
        rig.On["skill:harvest_wheat"] = (_, _) => { if (harvests++ == 0) rig.Give(11, Id("wheat"), 5); return Task.FromResult(ActResult.Success("harvested")); };
        rig.World.Entities.Spawn(new Entity(80, EntityKind.Mob, new Vec3d(3.5, 65, 0.5), TypeId: MobTypes.Cow));
        rig.On["attack"] = (_, _) => { rig.Give(12, Id("leather"), 1 + rig.World.Inventory.CountItem(Id("leather"))); return Task.FromResult(ActResult.Success("killed cow")); };
        var s = await rig.Run("""
            const c = gather('cobblestone', 8, { from: 'source' })
            const w = gather('wheat', 10, { from: 'harvest', more: true, r: 6 })
            const l = gather('leather', 2, { from: 'kill' })
            const d = gather('cobblestone', 4, { from: left => mine('cobblestone', left), more: true })
            return [c.ok, c.n, w.ok, w.n, l.ok, l.have, d.ok, d.n].join(',') + ' | ' + w.msg
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true,8,false,5,true,2,true,4 | wheat 5/10", s.Outcome);
        var calls = rig.Norm();
        Assert.Equal("""mine {"block":"stone","count":8}""", calls[0]); // stone (it drops cobble) before cobblestone itself
        Assert.Contains("""skill:harvest_wheat {"radius":6,"replant":true}""", calls);
        Assert.Contains("""attack {"target":"cow"}""", calls);
        Assert.Contains("""mine {"block":"cobblestone","count":4}""", calls);
    }

    [Fact]
    public async Task Gather_stashes_as_the_bag_fills_and_reports_every_chest_full_as_data()
    {
        using var rig = Rig.Js();
        int slot = 10;
        rig.On["harvest_generator"] = (args, _) => { rig.Give(slot++, Id("cobblestone"), args.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("dug")); };
        rig.On["deposit"] = (_, _) => { for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default); slot = 10; return Task.FromResult(ActResult.Success("stored")); };
        var s = await rig.Run("return gather('cobblestone', 20, { from: 'gen', stash: '14,64,-5' }).msg");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("got 20 cobblestone", s.Outcome);
        Assert.Equal(["""harvest_generator {"at":"auto","count":8}""", """harvest_generator {"at":"auto","count":8}""", """harvest_generator {"at":"auto","count":4}"""],
            rig.Norm().Where(c => c.StartsWith("harvest_generator")));

        rig.On["harvest_generator"] = (_, _) => { for (int i = 10; i < 45; i++) rig.Give(i, Id("cobblestone"), 64); return Task.FromResult(ActResult.Success("dug")); };
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag"));
        var f = await rig.Run("const r = gather('cobblestone', 640, { from: 'gen', stash: true })\nreturn [r.ok, r.full, r.need].join(',') + ' ' + r.msg", "full");
        Assert.True(f.Ok, f.Outcome);
        Assert.Contains("false,true,141 cobblestone", f.Outcome);
        Assert.Contains("all 2 known chests full", f.Outcome);

        var bad = await rig.Run("gather('log', 2, { from: 'teleport' })", "bad");
        Assert.Equal(ScriptStatus.Failed, bad.Status);
        Assert.Contains("unknown route 'teleport'", bad.Outcome);
    }

    [Fact]
    public async Task Quota_counts_until_n_and_stops_on_failure_or_no_progress()
    {
        using var rig = Rig.Js();
        rig.On["mine"] = (_, _) => { rig.Give(13, Id("dirt"), 8 + rig.World.Inventory.CountItem(Id("dirt"))); return Task.FromResult(ActResult.Success("mined")); };
        var s = await rig.Run("""
            const a = quota(5, () => 2)
            const b = quota(10, () => 0, { idle: 2 })
            const c = quota(3, () => ({ ok: false, msg: 'nope' }))
            const d = quota(16, () => mine('dirt', 8), { item: 'dirt' })
            return [a.ok, a.n, b.ok, b.msg, c.msg, d.ok, d.n].join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true|6|false|0/10: nothing added in 2 rounds|0/3: nope|true|16", s.Outcome);
    }

    [Fact]
    public async Task ForEach_walks_the_nearest_target_next_within_a_budget()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            const seen = []
            const all = forEach([{ x: 5, y: 65, z: 5 }, 'home', '1,65,1', { x: 3, y: 65, z: 0 }], t => { seen.push(typeof t === 'string' ? t : `${t.x},${t.z}`); return go(t) })
            const two = forEach(['4,65,4', '1,65,1', '2,65,2'], t => { seen.push(t) }, { budget: 2 })
            const stop = forEach(['1,65,1', '2,65,2'], () => 'stop')
            const bad = forEach(['1,65,1'], () => ({ ok: false, msg: 'blocked' }))
            return [seen.join(' '), all.n, two.n, stop.n, bad.ok, bad.failed, forEach([], () => 1).ok].join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("1,65,1 3,0 5,5 home 1,65,1 2,65,2|4|2|0|false|1|true", s.Outcome);
    }

    [Fact]
    public async Task Patrol_walks_an_area_corners_in_rounds_until_told_to_stop()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            const a = patrol({ at: '0,65,0', r: 4 }, null, { rounds: 2 })
            let k = 0
            const b = patrol(['1,65,1', '2,65,2', '3,65,3'], () => (++k === 2 ? 'stop' : undefined), { rounds: 5 })
            const c = patrol(['1,65,1'], null, { rounds: 9, until: () => true })
            return [a.n, b.n, c.n, c.ok].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("8,2,0,false", s.Outcome);
        var gotos = rig.Norm().Where(c => c.StartsWith("goto")).Take(4).ToList();
        Assert.Equal(["""goto {"to":"-4,65,-4"}""", """goto {"to":"4,65,-4"}""", """goto {"to":"4,65,4"}""", """goto {"to":"-4,65,4"}"""], gotos);
    }

    [Fact]
    public async Task Stash_handTo_makeRoom()
    {
        using var rig = Rig.Js();
        rig.On["withdraw"] = (args, _) => { rig.Give(20, Id("cobblestone"), args.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("took")); };
        var s = await rig.Run("""
            const a = stash(['log'], { to: '1,64,1' }), b = stash(), c = stash('dirt')
            const g = handTo('kiwi', 'cobblestone', 5, { chest: '14,64,-5' })
            return [a.ok, b.ok, c.ok, g.ok, g.n].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true,true,true,true,5", s.Outcome);
        var calls = rig.Norm();
        Assert.Contains("""deposit {"at":"1,64,1","items":["log"]}""", calls);
        Assert.Contains("deposit {}", calls);
        Assert.Contains("""deposit {"items":["dirt"]}""", calls);
        Assert.Contains("""withdraw {"at":"14,64,-5","count":5,"item":"cobblestone"}""", calls);
        Assert.Contains(calls, c => c.StartsWith("skill:give_items"));

        // A failed deposit is data (ok:false), never a script failure; makeRoom drops the named junk when the chests can't take it.
        for (int i = 9; i < 45; i++) rig.Give(i, Id("dirt"), 64);
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("no path to 14,64,-5"));
        rig.On["skill:drop_items"] = (_, _) => { for (int i = 30; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default); return Task.FromResult(ActResult.Success("dropped")); };
        var m = await rig.Run("""
            const f = stash(['dirt'])
            const tight = makeRoom(2)
            const r = makeRoom(2, { drop: ['dirt'] })
            return [f.ok, f.full, tight.ok, tight.msg, r.ok, r.n].join('|')
            """, "room");
        Assert.True(m.Ok, m.Outcome);
        Assert.Contains("false||false|need 2 free slots, have 0 (deposit: no path to 14,64,-5)|true|15", m.Outcome);
    }

    [Fact]
    public async Task WaitFor_takes_a_condition()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            let k = 0
            const a = waitFor(() => ++k >= 2, 10)
            const b = waitFor(() => false, 1)
            return [a.ok, k, b.ok, b.msg].join(',')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true,2,false,timeout after 1s", s.Outcome);
    }

    /// <summary>A version whose game data lacks some b1.8.1 names: the primitives follow the host's data, never hard-coded b1.8.1 ids.</summary>
    private sealed class OtherVersion(params string[] missing) : IGameData
    {
        private readonly IGameData _b = Beta181GameData.Instance;
        public string VersionId => "test-9.9";
        public BlockInfo Block(int id) => _b.Block(id);
        public bool BlockExists(int id) => _b.BlockExists(id);
        public BlockInfo? BlockByName(string name) => missing.Contains(name) ? null : _b.BlockByName(name);
        public ItemInfo Item(int id) => _b.Item(id);
        public bool ItemExists(int id) => _b.ItemExists(id);
        public ItemInfo? ItemByName(string name) => missing.Contains(name) ? null : _b.ItemByName(name);
        public Recipe? MatchRecipe(IReadOnlyList<ItemStack> grid, int gridSize) => _b.MatchRecipe(grid, gridSize);
        public IReadOnlyList<Recipe> RecipesFor(int itemId, int damage = -1) => _b.RecipesFor(itemId, damage);
        public SmeltingRecipe? SmeltingFor(int inputId) => _b.SmeltingFor(inputId);
        public int FuelTicks(int itemId) => _b.FuelTicks(itemId);
        public bool CanHarvest(int blockId, int heldItemId) => _b.CanHarvest(blockId, heldItemId);
        public int DigTicks(int blockId, int heldItemId, bool onGround = true, bool inWater = false) => _b.DigTicks(blockId, heldItemId, onGround, inWater);
    }

    [Fact]
    public async Task Names_resolve_through_the_hosts_game_data_not_b181_tables()
    {
        using var rig = Rig.Js(data: new OtherVersion("raw_porkchop", "stone_pickaxe", "rotten_flesh"));
        rig.Give(9, Id("wooden_pickaxe"), 1);
        rig.World.Entities.Spawn(new Entity(81, EntityKind.Mob, new Vec3d(3.5, 65, 0.5), TypeId: MobTypes.Zombie));
        var s = await rig.Run("""
            const g = gather('rotten_flesh', 1, { from: 'source' })
            return [item('stick').version, item('stone_pickaxe'), ensure('pickaxe'), g.ok, g.msg].join('|')
            """, "v");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("test-9.9||wooden_pickaxe|false|rotten_flesh 0/1 (source: no known source of rotten_flesh in this world)", s.Outcome);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("attack")); // this version has no such drop: no route to it
    }
}

/// <summary>The jobs the old SDK baked in (mineGen, chopTrees, harvestFarm, guardArea), as the worked example scripts composed from the primitives, on the same fixtures as the old helpers' tests.</summary>
public class ExampleScriptTests
{
    private static int Id(string name) => Items.ByName(name)!.Id;
    private static string Example(string title) => ScriptPrompts.Unindent(ScriptPrompts.Examples.Single(e => e.Title == title).Code);

    /// <summary>The gen fixture of the old <c>mineGen</c> test: each call digs its count into a new slot; a deposit empties the bag.</summary>
    private static Rig GenRig()
    {
        var rig = Rig.Js();
        rig.Give(9, Id("stone_pickaxe"), 1);
        int slot = 10;
        rig.On["harvest_generator"] = (args, _) =>
        {
            int n = args.GetProperty("count").GetInt32();
            rig.Give(slot++, Id("cobblestone"), n);
            return Task.FromResult(ActResult.Success($"generator: dug {n}, +{n} cobblestone"));
        };
        rig.On["deposit"] = (_, _) => { for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default); slot = 10; return Task.FromResult(ActResult.Success("deposited")); };
        return rig;
    }

    [Fact]
    public async Task Gen_example_yields_the_same_counts_as_mineGen_did()
    {
        using var rig = GenRig();
        var s = await rig.Run(ScriptPrompts.Exemplar, "gen", @params: """{"n":20,"chest":"14,64,-5"}""");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("mined 20 cobble", s.Outcome);
        var gens = rig.Norm().Where(c => c.StartsWith("harvest_generator")).ToList();
        Assert.Equal(["""harvest_generator {"at":"auto","count":8}""", """harvest_generator {"at":"auto","count":8}""", """harvest_generator {"at":"auto","count":4}"""], gens);
        Assert.Contains("""deposit {"at":"14,64,-5","items":["cobblestone"]}""", rig.Norm());

        // Old mineGen: the same counts through the compat module.
        using var old = GenRig();
        var o = await old.Run("return 'mined ' + mineGen({ n: 20, per: 8, chest: '14,64,-5' })", "old");
        Assert.Contains("mined 20", o.Outcome);
        Assert.Equal(gens, old.Norm().Where(c => c.StartsWith("harvest_generator")));
    }

    [Fact]
    public async Task Gen_example_fails_on_a_dead_gen_and_on_every_chest_full_with_the_actionable_line()
    {
        using var rig = GenRig();
        rig.On["harvest_generator"] = (_, _) => Task.FromResult(ActResult.Fail("isn't regenerating cobble for me"));
        var s = await rig.Run(ScriptPrompts.Exemplar, "dead", @params: """{"n":8}""");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("isn't regenerating", s.Outcome);

        rig.On["harvest_generator"] = (_, _) => { for (int i = 10; i < 45; i++) rig.Give(i, Id("cobblestone"), 64); return Task.FromResult(ActResult.Success("generator: dug 8, +8 cobblestone")); };
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("all 2 known chests full (14,64,-5, 5,64,4); 141 cobblestone in bag; craft+place a chest (have 300 logs) or keep carrying"));
        var f = await rig.Run(ScriptPrompts.Exemplar, "full", @params: """{"n":64,"chest":"14,64,-5"}""");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.Contains("all 2 known chests full", f.Outcome);
        Assert.Contains("or keep carrying", f.Outcome);
    }

    [Fact]
    public async Task Chop_example_chops_only_grown_spots_stores_the_logs_and_replants()
    {
        using var rig = Rig.Js();
        rig.On["chop_tree"] = (_, _) => { rig.Give(30, Id("log"), 4); return Task.FromResult(ActResult.Success("chopped 4 log")); };
        rig.World.SetBlock(3, 65, 3, Blocks.Log, 0);
        var code = Example("chop trees, replant, wait for regrowth");
        var s = await rig.Run(code, "chop", @params: """{"spots":["3,65,3","9,65,9"],"n":1,"chest":"2,64,2"}""");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("chopped 1", s.Outcome);
        var calls = rig.Norm();
        Assert.Contains("""chop_tree {"at":"3,65,3"}""", calls);
        Assert.DoesNotContain("""chop_tree {"at":"9,65,9"}""", calls); // no log there: skipped
        Assert.Contains("""deposit {"at":"2,64,2","items":["log"]}""", calls);

        // The tree came down but its spot wasn't replanted: the script plants a carried sapling there.
        using var r2 = Rig.Js();
        r2.Give(12, Id("sapling"), 2);
        r2.World.SetBlock(3, 65, 3, Blocks.Log, 0);
        r2.On["chop_tree"] = (_, _) => { r2.Give(30, Id("log"), 4); return Task.FromResult(ActResult.Success("chopped 4 log, not replanted: no sapling spot")); };
        var t = await r2.Run(code, "replant", @params: """{"spots":["3,65,3"],"n":1}""");
        Assert.True(t.Ok, t.Outcome);
        Assert.Contains("""place {"at":"3,65,3","block":"sapling"}""", r2.Norm());
    }

    [Fact]
    public async Task Harvest_example_harvests_replanting_and_stores()
    {
        using var rig = Rig.Js();
        int calls = 0;
        rig.On["skill:harvest_wheat"] = (_, _) => { if (calls++ == 0) rig.Give(11, Id("wheat"), 5); return Task.FromResult(ActResult.Success("harvested")); };
        var s = await rig.Run(Example("harvest a farm in rounds"), "farm", @params: """{"rounds":1,"r":6}""");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("harvested 5 wheat", s.Outcome);
        Assert.Contains("""skill:harvest_wheat {"radius":6,"replant":true}""", rig.Norm());
        Assert.Contains("""deposit {"items":["wheat"]}""", rig.Norm());
    }

    [Fact]
    public async Task Guard_example_fights_at_night_and_stops_at_day()
    {
        using var rig = Rig.Js();
        rig.World.Time = 14000;
        rig.World.Entities.Spawn(new Entity(78, EntityKind.Mob, new Vec3d(3.5, 65, 0.5), TypeId: MobTypes.Skeleton));
        int attacks = 0;
        rig.On["attack"] = (_, _) => { if (++attacks == 2) rig.World.Time = 1000; return Task.FromResult(ActResult.Success("killed skeleton")); };
        var s = await rig.Run(Example("guard an area at night"), "guard");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs.TakeLast(5)));
        Assert.Contains("fights 2", s.Outcome);
    }

    [Fact]
    public void No_example_or_reference_line_depends_on_the_compat_module()
    {
        Assert.NotEmpty(Sdk.SdkModules.Compat);
        foreach (var (title, code) in ScriptPrompts.Examples)
            Assert.True(ScriptLint.CompatCalls(ScriptPrompts.Unindent(code)).Count == 0, title);
        var reference = Sdk.SdkReference.Build();
        foreach (var name in Sdk.SdkModules.Compat.Keys) Assert.DoesNotContain(name + "(", reference);
        Assert.DoesNotContain("## compat", reference);
        Assert.Contains("- gather(what, n=1", reference);
        Assert.Contains("- find(query", reference);
        Assert.DoesNotContain("skyblock", reference);
    }
}

/// <summary>Saved b1.8.1 scripts that call the old helpers keep running (99-compat.js); lint flags every such call with its replacement.</summary>
public class CompatScriptTests
{
    private static int Id(string name) => Items.ByName(name)!.Id;

    /// <summary>The JS the library held for three live scripts (data/alice-snap 10-28) before the primitives.</summary>
    internal static readonly (string Name, string Js)[] Saved =
    [
        ("gen_loop", """
            // purpose: keep mining the cobble gen patiently, deposit when full
            const P = params({ at: '6,64,8', chest: '14,64,-5', rounds: 2, per: 8 })
            use(replaceToolWhenBroken('pickaxe'))
            return `mined ${mineGen({ at: P.at, n: P.rounds * P.per, per: P.per, chest: P.chest })} cobble`
            """),
        ("hive_e4b353", """
            // purpose: dump inventory then fight mobs at night
            const P = params({ chest: '14,64,-5', home: '8,64,-8' })
            store(P.chest)
            return 'patrol done: ' + guardArea({ home: P.home, retreatBelow: 4, rounds: 20 })
            """),
        ("west_trees", """
            // purpose: wait for west tree farm trees to grow, chop+replant them
            const P = params({ spots: ['9,65,0', '9,65,3', '13,65,0', '13,65,3'], chest: '14,64,-5' })
            return 'chopped ' + chopTrees({ spots: P.spots, n: 4, rounds: 20, waitS: 30, chest: P.chest })
            """),
    ];

    [Fact]
    public async Task Saved_scripts_on_the_old_helpers_still_run()
    {
        using var rig = Rig.Js();
        rig.Give(9, Id("stone_pickaxe"), 1);
        int slot = 10;
        rig.On["harvest_generator"] = (args, _) => { rig.Give(slot++, Id("cobblestone"), args.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("dug")); };
        rig.On["chop_tree"] = (_, _) => { rig.Give(30, Id("log"), 4); return Task.FromResult(ActResult.Success("chopped 4 log")); };
        foreach (var spot in new[] { (9, 0), (9, 3), (13, 0), (13, 3) }) rig.World.SetBlock(spot.Item1, 65, spot.Item2, Blocks.Log, 0);
        var outcomes = new List<string>();
        foreach (var (name, js) in Saved)
        {
            var s = await rig.Run(ScriptPrompts.Unindent(js), name);
            Assert.True(s.Ok, name + ": " + s.Outcome);
            outcomes.Add(s.Outcome!);
        }
        Assert.Contains("mined 16 cobble", outcomes[0]);
        Assert.Contains("patrol done: 0", outcomes[1]); // day: nothing to guard
        Assert.Contains("chopped 4", outcomes[2]);
        Assert.Contains("""deposit {"at":"14,64,-5","items":["log"]}""", rig.Norm());
    }

    [Fact]
    public void Lint_flags_every_compat_call_with_what_replaces_it()
    {
        string[] acts = ["gen", "harvest_generator", "deposit", "withdraw", "take", "go", "goto", "chop", "chop_tree", "attack"];
        foreach (var (name, js) in Saved)
        {
            var r = ScriptLint.Check(ScriptPrompts.Unindent(js), acts, language: ScriptLanguage.Js);
            Assert.False(r.HasErrors, name + ": " + string.Join(" | ", r.Errors));
            Assert.Contains(r.Warnings, w => w.Rule == ScriptLint.CompatRule);
        }
        var g = ScriptLint.Check("mineGen({ n: 8 })\nwaitForDay(5)", acts, language: ScriptLanguage.Js).Warnings.Where(w => w.Rule == "compat").ToList();
        Assert.Equal(2, g.Count);
        Assert.Contains("mineGen() is a deprecated compat helper: use gather('cobblestone'", g[0].Message);
        Assert.Contains("waitFor('day'", g[1].Message);
        Assert.Equal(["chopTrees", "store"], ScriptLint.CompatCalls("store('1,2,3')\nchopTrees({ n: 1 })").Select(c => c.Name));
        // Your own function of the same name is yours, not the compat helper.
        Assert.DoesNotContain(ScriptLint.Check("function store(x) { return x }\nstore(1)", acts, language: ScriptLanguage.Js).Warnings, w => w.Rule == "compat");
    }
}
