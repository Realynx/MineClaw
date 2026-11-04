using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Pins the JS SDK helpers saved scripts call (story.place/pour/escape/go/dig/fetch/work/idle/till, forEach, gather, scout,
/// ensure, quota, patrol, stash, makeRoom, handTo and the compat helpers): every action and skill answers from a seeded script of
/// successes and failures (the messages the helpers branch on), and each run's full call sequence (hashed) and outcome must stay
/// the same. A refactor of <c>Sdk/js/*.js</c> must leave all of them unchanged. Regenerate only for an intended behaviour change:
/// <c>WP09_GOLDEN_OUT=&lt;file&gt;</c> writes the actual lines.
/// </summary>
public sealed class SdkChaosGoldenTests
{
    private static readonly string[] Msgs =
    [
        "ok", "no path to within 1 of (1, 2, 3) (no route)", "unreachable", "partway", "from my eyes", "couldn't get closer", "in reach",
        "can't reach", "out of reach", "no walkable spot", "standing in it", "in the way", "wrong rotation", "chest busy",
        "would lose the drop", "can't get near", "no block face", "didn't appear", "all 3 known chests full (x); 141 cobblestone in bag; y",
        "no known chest", "no free cell", "claimed", "", "got 3",
    ];

    private static readonly string[] ActionNames = ["goto", "mine", "chop_tree", "deposit", "dig", "escape_dig", "craft", "smelt", "withdraw", "place",
        "place_turned", "use_block", "eat", "harvest_generator", "attack", "equip", "face"];

    private static readonly string[] SkillNames = ["move_items", "harvest_cane", "log", "give_items", "drop_items", "harvest_wheat", "excavate",
        "enter_portal", "strip_mine", "smelt_batch", "collect_furnace", "find_biome", "scout_biome", "hold_item", "fill_bucket", "empty_bucket", "move_lava"];

    private sealed class Port : IStoryPort
    {
        public JsonObject? Next(string body) => null;
        public bool Report(string body, string job, string status, string? note, double? pct) => true;
        public string Stuck(string body, string why) => "help is on its way";
    }

    /// <summary>Every action and skill answers by a seeded hash of (seed, call number): ok about half the time, a message from <see cref="Msgs"/>.</summary>
    private static void Chaos(Rig rig, int seed)
    {
        int n = 0;
        Task<ActResult> Answer()
        {
            uint h = Mix((uint)seed * 2654435761u ^ (uint)Interlocked.Increment(ref n) * 40503u);
            var msg = Msgs[h % (uint)Msgs.Length];
            return Task.FromResult((h >> 8) % 100 < 55 ? ActResult.Success(msg) : ActResult.Fail(msg));
        }
        foreach (var a in ActionNames) rig.On[a] = (_, _) => Answer();
        foreach (var s in SkillNames) rig.On["skill:" + s] = (_, _) => Answer();
    }

    private static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    /// <summary>The Rig's flat stone ground; <paramref name="pit"/>: dirt y 64..66 with a 1×1 pit at 0,0 and the body in it.</summary>
    private static Rig Make(int seed, bool pit)
    {
        var rig = Rig.Js();
        var w = rig.World;
        if (pit)
        {
            for (int x = -12; x <= 12; x++)
                for (int z = -12; z <= 12; z++)
                    for (int y = 64; y <= 66; y++)
                        w.SetBlock(x, y, z, x == 0 && z == 0 && y > 64 ? 0 : Blocks.Dirt, 0);
            w.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        }
        w.SetBlock(2, 65, 2, Blocks.Water, 0);
        w.SetBlock(-2, 65, 2, Blocks.Lava, 0);
        if (seed % 2 == 0) rig.Give(36, Blocks.Cobblestone, 3);
        if (seed % 3 == 0) rig.Give(37, Items.ByName("bucket")!.Id, 2);
        if (seed % 4 == 1) rig.Give(38, Items.ByName("water_bucket")!.Id, 1);
        rig.Give(39, Blocks.Dirt, seed % 5);
        rig.Host.Story = new Port();
        Chaos(rig, seed);
        return rig;
    }

    private static readonly (string Name, bool Pit, string Code)[] Scenarios =
    [
        ("place.single", false, "return story.place({id:'j1', block:'cobblestone', want:'cobblestone', at:'1,65,1', stand:'0,65,0'})"),
        ("place.batch", false, "return story.place({id:'j1', block:'cobblestone', want:'cobblestone', cells:['1,65,1','2,65,1','3,65,1','1,66,1'], clear:['2,65,1'], stand:'0,65,0'})"),
        ("place.turned", false, "return story.place({id:'j1', block:'cobblestone', want:'stone', cells:['1,65,1','-1,65,1','5,65,5'], metas:[3,null,2], clear:true, at:'1,65,1'})"),
        ("place.far", false, "return story.place({id:'j1', block:'cobblestone', cells:['9,65,9','8,65,9'], stand:'6,65,6'})"),
        ("pour.water", false, "return story.pour({id:'p1', cells:['2,65,2','3,65,3','4,65,4'], source:'2,65,2', buckets:2, stand:'1,65,1'})"),
        ("pour.lava", false, "return story.pour({id:'p1', liquid:'lava', at:'-2,65,2', source:'-2,65,2'})"),
        ("pour.nosource", false, "return story.pour({id:'p1', cells:['5,65,5','6,65,6']})"),
        ("escape.pit", true, "return story.escape('9,67,0')"),
        ("escape.climb", true, "return story.escape('9,67,0', true)"),
        ("escape.flat", false, "return story.escape()"),
        ("go.pit", true, "return story.go('9,67,0', 1)"),
        ("go.high", false, "return story.go('1,70,1', 2, {fast:true})"),
        ("dig.reach", false, "return story.dig('1,70,1', {id:'d', pillar:{item:'dirt', n:16, from:'3,64,3'}})"),
        ("dig.plain", false, "return story.dig('1,64,1')"),
        ("ready", false, "return [story.ready({pillar:{item:'dirt', n:16, from:'3,64,3'}}), story.ready(null)]"),
        ("till", false, "return story.till({at:'5,64,5', hoe:'stone_hoe'})"),
        ("idle", false, "return story.idle({id:'i', at:'3,65,3', wander:true, look:['1,65,1','2,65,2'], s:2, act:'stargazing'})"),
        ("fetch", false, "return story.fetch({id:'f', block:'cobblestone', cells:['1,65,1'], fetch:[{item:'cobblestone', n:8, from:'3,64,3'}, {item:'iron_sword', n:1, from:'3,64,3', ench:'sharp'}], store:[{item:'dirt', to:'3,64,3', keep:2}], tool:'stone_pickaxe'})"),
        ("work.kinds", false, """
            const out = []
            for (const kind of ['equip', 'look', 'dump', 'escape', 'other'])
              out.push(kind + '=' + JSON.stringify(story.work({id:'w', kind, at:'3,65,3', unwear:true, wear:['iron_helmet'], hold:'iron_sword', drop:[{item:'dirt', n:2}, {item:'sand'}], s:1})))
            return out
            """),
        ("forEach", false, "return forEach(['1,65,1', {x:3,y:65,z:3}, '5,65,5', 'nowhere'], (t, i) => i % 3 === 0 ? false : dig(typeof t === 'string' ? t : '3,65,3'))"),
        ("forEach.given", false, "return forEach(['1,65,1', '2,65,2', '3,65,3'], t => dig(t), {order:'given', budget:2})"),
        ("forEach.none", false, "const t = []; t.beyond = [{name:'log', x:1, y:2, z:3, d:40}]; return forEach(t, () => true)"),
        ("gather.default", false, "return gather('cobblestone', 4)"),
        ("gather.routes", false, "return gather('dirt', 3, {from:['chests', 'craft', 'mine', 'gen'], chest:'3,64,3', rounds:3, per:2})"),
        ("gather.stash", false, "return gather('cobblestone', 5, {from:'gen', stash:'3,64,3', rounds:2})"),
        ("gather.fn", false, "return gather('log', 2, {from: k => dig('1,65,1'), near:'2,65,2'})"),
        ("scout", false, "return scout('cow', {dir:90, max:48, step:16})"),
        ("ensure", false, "try { return ensure(['pickaxe', 'cobblestone']) } catch (e) { return 'threw ' + e }"),
        ("quota", false, "let k = 0; return quota(5, left => (++k % 2 ? dig('1,65,1') : 1), {idle:2})"),
        ("patrol", false, "return patrol({at:'0,65,0', r:3}, (p, i) => (i > 2 ? 'stop' : null), {rounds:2})"),
        ("stash", false, "return [stash(['dirt']), stash('all-but-tools', {to:'3,64,3'}), makeRoom(3, {drop:['dirt']})]"),
        ("handTo", false, "return handTo('kiwi', 'dirt', 2)"),
        ("chopTrees", false, "return chopTrees({spots:['1,65,1'], rounds:2, waitS:0})"),
        ("guardArea", false, "return guardArea({rounds:3, until:'never'})"),
    ];

    private static async Task<string> RunOne(string name, bool pit, string code, int seed)
    {
        using var rig = Make(seed, pit);
        var s = await rig.Run("const __r = (() => {\n" + code + "\n})()\nreturn JSON.stringify(__r === undefined ? null : __r)");
        var trace = string.Join("\n", rig.Norm());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace)))[..16];
        var outcome = s.Ok ? s.Result : "FAILED " + s.Outcome;
        return $"{name}#{seed} {rig.CallList.Count} {hash} {outcome}";
    }

    [Fact]
    public async Task Sdk_helpers_make_the_golden_calls_and_results()
    {
        var lines = new List<string>();
        foreach (var (name, pit, code) in Scenarios)
            for (int seed = 1; seed <= 3; seed++)
                lines.Add(await RunOne(name, pit, code, seed));
        var actual = string.Join("\n", lines);
        if (Environment.GetEnvironmentVariable("WP09_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        Assert.Equal(SdkChaosGoldenData.Expected.ReplaceLineEndings("\n").Trim(), actual);
    }
}
