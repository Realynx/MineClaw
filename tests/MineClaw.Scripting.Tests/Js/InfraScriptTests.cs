using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Data;
using MineClaw.Scripting;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// The infrastructure scripts for the giant pyramid (docs/infra-plan.md): the tool depot, the iron run and the far sand run. These are
/// the exact texts given to the brain; the tests run them on the offline rig with the game's actions mocked, so they prove the
/// script's logic (what it asks for, in what order, what it leaves in the chest), not the game's physics.
/// </summary>
public class InfraScriptTests
{
    internal const string ToolDepot = """
        // purpose: keep the tool depot chest stocked: stone tools, plus cobblestone, sticks and tables for bodies to craft their own on site
        const P = params({ chest: '10,64,12', shovel: 24, pickaxe: 4, axe: 0, cobblestone: 512, sticks: 256, tables: 4, batch: 16, rounds: 1, every: 300 })
        const HEAD = { shovel: 1, pickaxe: 3, axe: 3 }, STICKS = { shovel: 2, pickaxe: 2, axe: 2 }
        // what the known chests hold of an item: where() says "name x12: chest@3,64,2 (...); x5: ... | total 17"
        function inChests(item) {
          const w = where(item)
          if (!w) return 0
          const t = /\| total (\d+)/.exec(w)
          if (t) return Number(t[1])
          let n = 0
          for (const m of w.matchAll(/\bx(\d+):/g)) n += Number(m[1])
          return n
        }
        const held = item => inChests(item) + inv(item)
        function tools(kind, target) {
          const item = 'stone_' + kind
          let made = 0, need = target - held(item)
          while (need > 0) {
            const n = Math.min(need, P.batch)
            gather('cobblestone', HEAD[kind] * n)                       // raw materials into the bag: chests first, then the quarry
            gather('log', Math.ceil(STICKS[kind] * n / 8) + 1)          // a log is 4 planks is 8 sticks (+1 spare for a table)
            const c = craft(item, n)
            const k = inv(item)
            if (!ok(c) || k === 0) { log(`depot: ${item}: ${c.msg}`); break }
            stash([item], { to: P.chest })
            made += k
            need -= k
          }
          return made
        }
        function kitItem(item, target) {
          const need = target - held(item)
          if (need <= 0) return 0
          if (item === 'stick') gather('log', Math.ceil(need / 8) + 1)
          if (item === 'crafting_table') gather('log', need + 1)
          if (item === 'cobblestone') return gather(item, need, { stash: P.chest }).n   // quarried as the bag fills, stored each time
          const before = inv(item), c = craft(item, need)                               // gather's craft route is for "have n", not "n more"
          if (!ok(c)) { log(`depot: ${item}: ${c.msg}`); return 0 }
          const made = inv(item) - before
          stash([item], { to: P.chest })
          return made
        }
        function pass() {
          const made = []
          for (const kind of ['shovel', 'pickaxe', 'axe']) if (P[kind] > 0) made.push(`${tools(kind, P[kind])} ${kind}`)
          for (const [item, key] of [['cobblestone', 'cobblestone'], ['stick', 'sticks'], ['crafting_table', 'tables']])
            if (P[key] > 0) made.push(`${kitItem(item, P[key])} ${item}`)
          return made.join(', ')
        }
        function main() {
          let last = pass()
          for (let i = 1; i < P.rounds; i++) { wait(Math.min(P.every, 600)); last = pass() }
          return `depot stocked: made ${last}`
        }
        """;

    internal const string IronRun = """
        // purpose: get iron for a portal: mine ore, smelt it, craft the buckets and flint and steel, leave the spare ingots in the chest
        const P = params({ ore: 16, chest: '10,64,12', r: 32, buckets: 2, flint: 1 })
        function main() {
          ensure('pickaxe')                                              // stone or better mines iron
          const need = P.buckets * 3 + P.flint                           // a bucket is 3 ingots, flint and steel 1
          if (inv('iron_ingot') < need) {
            let got = gather('iron_ore', P.ore, { from: 'mine', r: P.r })
            if (got.n === 0) {                                           // none showing: strip-mine a corridor, then look again
              skill('strip_mine', { length: 32 })
              got = gather('iron_ore', P.ore, { from: 'mine', r: P.r })
            }
            if (inv('iron_ore') === 0) fail(`no iron ore found (${got.msg})`)
            skill('smelt_batch', { item: 'iron_ore', count: inv('iron_ore'), wait: true })
            skill('collect_furnace')
          }
          if (inv('iron_ingot') < need) fail(`only ${inv('iron_ingot')} iron ingots, need ${need}`)
          expect(craft('bucket', P.buckets))
          if (P.flint > 0) {
            gather('flint', P.flint, { from: 'source' })                 // 1 in 10 gravel drops flint
            expect(craft('flint_and_steel', P.flint))
          }
          const spare = inv('iron_ingot')
          if (spare > 0) stash(['iron_ingot'], { to: P.chest })
          return `iron: ${inv('bucket')} buckets, ${inv('flint_and_steel')} flint and steel, ${spare} ingots stored`
        }
        """;

    internal const string FarSandRun = """
        // purpose: dig sand at a far pit and carry it home in bag loads, on foot or through the portals; every body does n
        const P = params({ n: 2500, pit: '0,64,0', home: '0,64,0', r: 40, load: 1800, portal: '', homePortal: '' })
        const at = s => { const [x, y, z] = String(s).split(',').map(Number); return { x, y, z } }
        // one go() is one 60 s walk; a trip of hundreds of blocks takes several
        function travel(to, tries = 60) {
          let last = Infinity, stuck = 0
          for (let i = 0; i < tries; i++) {
            const d = dist(at(to))
            if (d <= 6) return { ok: true, msg: 'there' }
            stuck = d > last - 4 ? stuck + 1 : 0
            if (stuck >= 3) return { ok: false, msg: `stuck ${Math.round(d)} m short of ${to}` }
            last = d
            go(to, 4)
          }
          return { ok: false, msg: `${Math.round(dist(at(to)))} m short of ${to} after ${tries} legs` }
        }
        // portals: walk to the frame, step in (nether), step into the nether portal there (overworld again)
        function viaPortal(frame) {
          const t = travel(frame)
          if (!ok(t)) return t
          const a = skill('enter_portal'), b = skill('enter_portal')
          return ok(a) && ok(b) && me.dim === 0 ? { ok: true, msg: 'through' } : { ok: false, msg: `portal: ${a.msg} / ${b.msg}` }
        }
        // what a body needs out there: a shovel, and the kit to make more (a table, 64 cobblestone, 64 sticks = 32 stone shovels)
        function kit() {
          ensure('shovel')
          gather('crafting_table', 1)
          gather('cobblestone', 64)
          gather('stick', 64)
        }
        function hauler(a) {
          use(replaceToolWhenBroken('shovel'), keepFed({ below: 14, fetch: false }))   // a broken shovel is made again on site; food is carried
          kit()
          let done = 0, trips = 0
          while (done < a.n) {
            ensure('shovel')
            const r = gather('sand', Math.min(a.n - done, P.load), { from: 'mine', r: P.r })
            if (r.n <= 0) return `${me.body} ${done} sand, then none near ${P.pit} (${r.msg})`
            const home = P.portal ? viaPortal(P.portal) : { ok: true }
            if (ok(home)) { const t = travel(P.home); if (!ok(t)) return `${me.body} ${done} sand, then ${t.msg}` } else return `${me.body} ${done} sand, then ${home.msg}`
            const s = stash(['sand'], { to: P.home })
            if (!s.ok) return `${me.body} ${done} sand, then ${s.msg}`
            done += r.n
            trips++
            crew.inc('sand', r.n)
            if (done < a.n) { const back = P.homePortal ? viaPortal(P.homePortal) : travel(P.pit); if (!ok(back)) return `${me.body} ${done} sand, then ${back.msg}` }
          }
          return `${me.body} ${done} in ${trips} trips`
        }
        function main() {
          const r = everyBody('hauler', { n: P.n })
          if (!r.ok && /no crew/.test(r.msg)) return hauler({ n: P.n })
          return `far sand run: ${crew.get('sand')} sand (${r.msg})`
        }
        """;

    private static readonly string[] Acts = ["goto", "go", "mine", "deposit", "take", "withdraw", "craft", "smelt", "say"];

    private static int Id(string name) => Items.ByName(name)?.Id ?? Blocks.ByName(name)!.Id;

    [Theory]
    [InlineData(nameof(ToolDepot))]
    [InlineData(nameof(IronRun))]
    [InlineData(nameof(FarSandRun))]
    public void The_infra_scripts_lint_clean(string which)
    {
        string code = which switch { nameof(ToolDepot) => ToolDepot, nameof(IronRun) => IronRun, _ => FarSandRun };
        var r = ScriptLint.Check(code, Acts, language: ScriptLanguage.Js);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
    }

    // ---------------------------------------------------------------- rig helpers

    private static void Add(Rig rig, string item, int count)
    {
        int id = Id(item), max = Math.Max(1, Items.Get(id).MaxStack);
        for (int slot = 9; slot < 45 && count > 0; slot++)
        {
            var st = rig.World.Inventory.GetPlayerSlot(slot);
            if (!st.IsEmpty && st.Id != id) continue;
            int room = st.IsEmpty ? max : max - st.Count;
            int k = Math.Min(room, count);
            if (k <= 0) continue;
            rig.World.Inventory.SetSlot(0, slot, new ItemStack((short)id, (byte)(k + (st.IsEmpty ? 0 : st.Count)), 0));
            count -= k;
        }
    }

    private static void Remove(Rig rig, string item)
    {
        for (int slot = 9; slot < 45; slot++)
            if (rig.World.Inventory.GetPlayerSlot(slot) is { IsEmpty: false } st && st.Id == Id(item)) rig.World.Inventory.SetSlot(0, slot, default);
    }

    /// <summary>Crafting works whenever <paramref name="canCraft"/> says so: it adds the count to the bag; the chest takes whatever a deposit names.</summary>
    private static ConcurrentDictionary<string, int> Depot(Rig rig, Func<string, bool>? canCraft = null)
    {
        var chest = new ConcurrentDictionary<string, int>();
        rig.On["craft"] = (a, _) =>
        {
            string item = a.GetProperty("item").GetString()!;
            if (canCraft?.Invoke(item) == false) return Task.FromResult(ActResult.Fail($"need more material for {item}"));
            Add(rig, item, a.GetProperty("count").GetInt32());
            return Task.FromResult(ActResult.Success("crafted"));
        };
        rig.On["deposit"] = (a, _) =>
        {
            foreach (var it in a.GetProperty("items").EnumerateArray())
            {
                string item = it.GetString()!;
                chest.AddOrUpdate(item, rig.World.Inventory.CountItem(Id(item)), (_, old) => old + rig.World.Inventory.CountItem(Id(item)));
                Remove(rig, item);
            }
            return Task.FromResult(ActResult.Success("stored"));
        };
        return chest;
    }

    private static string Held(string item, int n) => $"{item} x{n}: chest@10,64,12 (opened 2m ago, may have changed)";

    // ---------------------------------------------------------------- tool depot

    [Fact]
    public async Task The_depot_crafts_only_what_the_chest_and_the_bag_are_short_of_and_stores_it()
    {
        using var rig = Rig.Js();
        Add(rig, "cobblestone", 300); Add(rig, "log", 20);
        rig.Where = item => item == "stone_shovel" ? Held(item, 20) : null;
        var chest = Depot(rig);
        var s = await rig.Run(ToolDepot, "depot", @params: """{"cobblestone":0,"sticks":0,"tables":0}""");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs));
        var calls = rig.Norm();
        Assert.Contains("""craft {"count":4,"item":"stone_shovel"}""", calls);          // 24 wanted, 20 in the chest
        Assert.Contains("""craft {"count":4,"item":"stone_pickaxe"}""", calls);         // none in the chest
        Assert.DoesNotContain(calls, c => c.Contains("stone_axe"));                      // axe: 0 wanted
        Assert.Equal(4, chest["stone_shovel"]);
        Assert.Equal(4, chest["stone_pickaxe"]);
        Assert.Contains("""deposit {"at":"10,64,12","items":["stone_shovel"]}""", calls);  // into the depot chest, tools only
        Assert.Contains("made 4 shovel, 4 pickaxe", s.Outcome);
    }

    [Fact]
    public async Task The_depot_crafts_in_batches_and_does_nothing_when_stocked()
    {
        using var rig = Rig.Js();
        Add(rig, "cobblestone", 600); Add(rig, "log", 20);
        var chest = Depot(rig);
        var s = await rig.Run(ToolDepot, "depot", @params: """{"shovel":40,"pickaxe":0,"cobblestone":0,"sticks":0,"tables":0}""");
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(["""craft {"count":16,"item":"stone_shovel"}""", """craft {"count":16,"item":"stone_shovel"}""", """craft {"count":8,"item":"stone_shovel"}"""],
            rig.Norm().Where(c => c.StartsWith("craft")));                               // 16 + 16 + 8: a batch the bag can hold
        Assert.Equal(40, chest["stone_shovel"]);

        using var full = Rig.Js();
        Add(full, "cobblestone", 300); Add(full, "log", 20);
        full.Where = item => item == "stone_shovel" ? Held(item, 10) + " | total 30" : null;   // the total wins over the parts
        Depot(full);
        var t = await full.Run(ToolDepot, "depot", @params: """{"shovel":24,"pickaxe":0,"cobblestone":0,"sticks":0,"tables":0}""");
        Assert.True(t.Ok, t.Outcome);
        Assert.DoesNotContain(full.Norm(), c => c.StartsWith("craft"));
    }

    [Fact]
    public async Task The_depot_stops_a_tool_it_cannot_make_instead_of_looping()
    {
        using var rig = Rig.Js();
        Add(rig, "cobblestone", 300); Add(rig, "log", 20);
        var chest = Depot(rig, item => item != "stone_pickaxe");
        var s = await rig.Run(ToolDepot, "depot", @params: """{"shovel":2,"pickaxe":4,"cobblestone":0,"sticks":0,"tables":0}""");
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(1, rig.Norm().Count(c => c.Contains("stone_pickaxe")));              // one try, then on
        Assert.Contains("0 pickaxe", s.Outcome);
        Assert.Equal(2, chest["stone_shovel"]);
        Assert.Contains(rig.Logs, l => l.Contains("stone_pickaxe"));
    }

    [Fact]
    public async Task The_depot_keeps_the_kit_bodies_craft_their_own_shovels_from()
    {
        using var rig = Rig.Js();
        Add(rig, "log", 30);
        rig.Where = item => item == "stick" ? Held(item, 40) : null;                     // 40 sticks already in the chest
        rig.On["mine"] = (a, _) => { Add(rig, "cobblestone", a.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("mined")); };
        var chest = Depot(rig);
        var s = await rig.Run(ToolDepot, "depot", @params: """{"shovel":0,"pickaxe":0,"cobblestone":100,"sticks":100,"tables":2}""");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs));
        var calls = rig.Norm();
        Assert.Contains("""craft {"count":60,"item":"stick"}""", calls);                // 100 wanted, 40 there
        Assert.Contains("""craft {"count":2,"item":"crafting_table"}""", calls);
        Assert.Equal(100, chest["cobblestone"]);
        Assert.Equal(60, chest["stick"]);
        Assert.Equal(2, chest["crafting_table"]);
        Assert.Contains("""mine {"block":"stone","count":100}""", calls);               // cobble comes from stone
    }

    [Fact]
    public async Task The_depot_checks_again_after_the_wait_when_asked_to_keep_stocked()
    {
        using var rig = Rig.Js();
        Add(rig, "cobblestone", 300); Add(rig, "log", 20);
        int shovels = 0;
        rig.Where = item => item == "stone_shovel" && shovels > 0 ? Held(item, shovels) : null;
        var chest = Depot(rig);
        var inner = rig.On["deposit"];
        rig.On["deposit"] = async (a, ct) => { var r = await inner(a, ct); shovels = chest.GetValueOrDefault("stone_shovel"); return r; };
        // The chest is emptied by the crew between rounds (a body took 6 shovels).
        var s = rig.Start(ToolDepot, "depot", @params: """{"shovel":8,"pickaxe":0,"cobblestone":0,"sticks":0,"tables":0,"rounds":2,"every":1}""");
        await Rig.Until(() => shovels == 8);
        shovels -= 6;
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(["""craft {"count":8,"item":"stone_shovel"}""", """craft {"count":6,"item":"stone_shovel"}"""], rig.Norm().Where(c => c.StartsWith("craft")));
    }

    // ---------------------------------------------------------------- iron run

    private static void Take(Rig rig, string item, int n)
    {
        for (int slot = 9; slot < 45 && n > 0; slot++)
            if (rig.World.Inventory.GetPlayerSlot(slot) is { IsEmpty: false } st && st.Id == Id(item))
            {
                int k = Math.Min(n, st.Count);
                rig.World.Inventory.SetSlot(0, slot, k == st.Count ? default : st with { Count = (byte)(st.Count - k) });
                n -= k;
            }
    }

    /// <summary>A world with iron ore in reach (or not): mine gives ore, and flint from gravel; a furnace turns ore to ingots when collected; recipes consume what they should.</summary>
    private static (ConcurrentDictionary<string, int> Chest, List<string> Order) IronWorld(Rig rig, bool ore = true)
    {
        var chest = new ConcurrentDictionary<string, int>();
        var order = new List<string>();
        rig.On["mine"] = (a, _) =>
        {
            string block = a.GetProperty("block").GetString()!;
            order.Add("mine " + block);
            if (block == "iron_ore" && ore) Add(rig, "iron_ore", a.GetProperty("count").GetInt32());
            if (block == "gravel") Add(rig, "flint", 1);
            return Task.FromResult(ActResult.Success("mined"));
        };
        int furnace = 0;
        rig.On["skill:smelt_batch"] = (a, _) =>
        {
            order.Add("smelt " + a.GetProperty("count").GetInt32());
            furnace += rig.World.Inventory.CountItem(Id("iron_ore"));
            Take(rig, "iron_ore", 999);
            return Task.FromResult(ActResult.Success("smelting"));
        };
        rig.On["skill:collect_furnace"] = (_, _) => { order.Add("collect"); Add(rig, "iron_ingot", furnace); furnace = 0; return Task.FromResult(ActResult.Success("took")); };
        rig.On["skill:strip_mine"] = (_, _) => { order.Add("strip"); return Task.FromResult(ActResult.Success("corridor")); };
        rig.On["craft"] = (a, _) =>
        {
            string item = a.GetProperty("item").GetString()!; int n = a.GetProperty("count").GetInt32();
            order.Add($"craft {item} {n}");
            int cost = item == "bucket" ? 3 * n : n;
            if (rig.World.Inventory.CountItem(Id("iron_ingot")) < cost) return Task.FromResult(ActResult.Fail("need more iron_ingot"));
            Take(rig, "iron_ingot", cost);
            if (item == "flint_and_steel") Take(rig, "flint", n);
            Add(rig, item, n);
            return Task.FromResult(ActResult.Success("crafted"));
        };
        rig.On["deposit"] = (a, _) =>
        {
            foreach (var it in a.GetProperty("items").EnumerateArray()) { string name = it.GetString()!; chest[name] = rig.World.Inventory.CountItem(Id(name)); Take(rig, name, 999); }
            return Task.FromResult(ActResult.Success("stored"));
        };
        return (chest, order);
    }

    [Fact]
    public async Task The_iron_run_mines_smelts_crafts_the_portal_tools_and_stores_the_rest()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_pickaxe", 1);
        var (chest, order) = IronWorld(rig);
        var s = await rig.Run(IronRun, "iron");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs) + " | " + string.Join(";", order) + " | " + string.Join(";", rig.Norm()));
        Assert.Equal(["mine iron_ore", "smelt 16", "collect", "craft bucket 2", "mine gravel", "craft flint_and_steel 1"], order);
        Assert.Equal(2, rig.World.Inventory.CountItem(Id("bucket")));
        Assert.Equal(1, rig.World.Inventory.CountItem(Id("flint_and_steel")));
        Assert.Equal(16 - 6 - 1, chest["iron_ingot"]);                         // the rest is kept, not carried around
        Assert.Contains("2 buckets, 1 flint and steel, 9 ingots stored", s.Outcome);
    }

    [Fact]
    public async Task The_iron_run_skips_the_mine_when_it_already_holds_enough_ingots()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_pickaxe", 1); Add(rig, "iron_ingot", 7);
        var (chest, order) = IronWorld(rig);
        var s = await rig.Run(IronRun, "iron");
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal(["craft bucket 2", "mine gravel", "craft flint_and_steel 1"], order);
        Assert.False(chest.ContainsKey("iron_ingot"), "nothing spare to store");
    }

    [Fact]
    public async Task The_iron_run_tries_a_strip_mine_once_then_fails_honestly_without_ore()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_pickaxe", 1);
        var (_, order) = IronWorld(rig, ore: false);
        var s = await rig.Run(IronRun, "iron");
        Assert.False(s.Ok);
        Assert.Contains("no iron ore found", s.Outcome);
        Assert.Equal(1, order.Count(o => o == "strip"));
        Assert.DoesNotContain(order, o => o.StartsWith("smelt") || o.StartsWith("craft"));
    }

    // ---------------------------------------------------------------- far sand run

    private sealed class Walker
    {
        public double Step = 40;                                 // blocks one goto covers (a 60 s walk is ~250; short here so legs repeat)
        public bool Frozen;
        public readonly List<string> Trail = new();
        public int Sand, Stored, Trips;
        public readonly List<string> Violations = new();
    }

    private static Walker FarWorld(Rig rig, double pitX, double homeX = 0, Walker? w = null)
    {
        w ??= new Walker();
        int sand = Id("sand");
        rig.World.Player.ServerTeleport(new Vec3d(pitX + 0.5, 65, 0.5), 66.62, 0, 0, true);
        rig.On["goto"] = (a, _) =>
        {
            var to = a.GetProperty("to").GetString()!.Split(',').Select(double.Parse).ToArray();
            double x = rig.World.Player.Position.X, dx = to[0] - x;
            if (!w.Frozen) x += Math.Sign(dx) * Math.Min(Math.Abs(dx), w.Step);
            w.Trail.Add($"{x:0}");
            rig.World.Player.ServerTeleport(new Vec3d(x + 0.5, 65, 0.5), 66.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("walked"));
        };
        rig.On["mine"] = (a, _) =>
        {
            if (a.GetProperty("block").GetString() != "sand") { Add(rig, "cobblestone", a.GetProperty("count").GetInt32()); return Task.FromResult(ActResult.Success("mined")); }   // the kit's cobble
            if (Math.Abs(rig.World.Player.Position.X - pitX) > 30) w.Violations.Add($"dug away from the pit at x={rig.World.Player.Position.X:0}");
            int n = Math.Min(a.GetProperty("count").GetInt32(), 256);
            Add(rig, "sand", n);
            w.Sand += n;
            return Task.FromResult(ActResult.Success("mined"));
        };
        rig.On["deposit"] = (a, _) =>
        {
            if (Math.Abs(rig.World.Player.Position.X - homeX) > 12) w.Violations.Add($"deposited away from home at x={rig.World.Player.Position.X:0}");
            int n = rig.World.Inventory.CountItem(sand);
            w.Stored += n; w.Trips++;
            Take(rig, "sand", n);
            return Task.FromResult(ActResult.Success("stored"));
        };
        rig.On["skill:enter_portal"] = (_, _) => Task.FromResult(ActResult.Success("through"));
        return w;
    }

    private const string Far = """{"n":500,"load":200,"pit":"400,64,0","home":"0,64,0"}""";

    [Fact]
    public async Task The_far_sand_run_digs_a_bag_walks_it_home_stores_it_and_walks_back()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_shovel", 1);
        var w = FarWorld(rig, pitX: 400);
        var s = await rig.Run(FarSandRun, "far", @params: Far);
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs));
        Assert.Equal(500, w.Stored);
        Assert.Equal(3, w.Trips);                                                    // 200 + 200 + 100: a bag at a time
        Assert.Empty(w.Violations);                                                  // dug only at the pit, stored only at home
        Assert.Contains("500 in 3 trips", s.Outcome);
        var calls = rig.Norm();
        Assert.Contains("""craft {"count":1,"item":"crafting_table"}""", calls);      // the kit: a table, cobblestone and sticks for shovels made on site
        Assert.Contains("""craft {"count":64,"item":"stick"}""", calls);
        Assert.Contains(calls, c => c.Contains("cobblestone") || c.StartsWith("""mine {"block":"stone"""));
        Assert.True(w.Trail.Count > 3 * 9, "400 m at 40 m a go() is ten legs each way: " + string.Join(",", w.Trail));
        Assert.InRange(rig.World.Player.Position.X, -10, 10);                        // the last trip ends at home: no walk back with nothing left to dig
    }

    [Fact]
    public async Task The_far_sand_run_stops_when_it_cannot_get_anywhere_and_keeps_the_sand_it_holds()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_shovel", 1);
        var w = FarWorld(rig, pitX: 400);
        w.Frozen = true;                                                             // every go() returns ok and nothing moves
        var s = await rig.Run(FarSandRun, "far", @params: Far);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("stuck", s.Outcome);
        Assert.Equal(0, w.Trips);
        Assert.Equal(200, rig.World.Inventory.CountItem(Id("sand")));                // nothing was thrown away
        Assert.True(w.Trail.Count <= 6, $"gave up after a few legs, not 60: {w.Trail.Count}");
    }

    [Fact]
    public async Task The_far_sand_run_goes_through_the_portal_pair_when_given_one()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_shovel", 1);
        var w = FarWorld(rig, pitX: 400);
        var through = new List<string>();
        rig.On["skill:enter_portal"] = (_, _) =>
        {
            through.Add($"x={Math.Floor(rig.World.Player.Position.X)}");
            if (through.Count % 2 == 1) { rig.World.Dimension = -1; rig.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true); }       // into the nether portal
            else { rig.World.Dimension = 0; rig.World.Player.ServerTeleport(new Vec3d(3.5, 65, 0.5), 66.62, 0, 0, true); }                                  // out at the home portal
            return Task.FromResult(ActResult.Success("through"));
        };
        var s = await rig.Run(FarSandRun, "far", @params: """{"n":200,"load":200,"pit":"400,64,0","home":"0,64,0","portal":"380,64,0"}""");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", rig.Logs));
        Assert.Equal(["x=380", "x=0"], through);                                      // walked to the far frame (not to the home one), then the nether portal
        Assert.Equal(200, w.Stored);
        Assert.Empty(w.Violations);
        Assert.True(w.Trail.Count < 9, "the portal replaced the long walk home: " + string.Join(",", w.Trail));
    }

    [Fact]
    public async Task The_far_sand_run_fails_a_trip_that_does_not_come_out_in_the_overworld()
    {
        using var rig = Rig.Js();
        Add(rig, "stone_shovel", 1);
        var w = FarWorld(rig, pitX: 400);
        rig.On["skill:enter_portal"] = (_, _) => { rig.World.Dimension = -1; return Task.FromResult(ActResult.Success("through")); };     // stays in the nether
        var s = await rig.Run(FarSandRun, "far", @params: """{"n":200,"load":200,"pit":"400,64,0","home":"0,64,0","portal":"380,64,0"}""");
        Assert.Contains("portal", s.Outcome);
        Assert.Equal(0, w.Trips);
    }

    /// <summary>The doc the brain reads carries these exact scripts: they cannot drift from the tested text.</summary>
    [Fact]
    public void The_plan_doc_carries_the_tested_scripts_verbatim()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MineClaw.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "infra-plan.md")).Replace("\r\n", "\n");
        foreach (var script in new[] { ToolDepot, IronRun, FarSandRun })
            Assert.Contains(script.Replace("\r\n", "\n"), doc);
    }
}
