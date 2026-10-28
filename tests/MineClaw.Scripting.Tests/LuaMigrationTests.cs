using System.Collections.Concurrent;
using MineClaw.Agent.Llm;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// The one-time Lua → JS migration (Scripting:MigrateLua), tunable params through the tool, and durable checkpoints in the
/// saved script's row — all on a real BotHost with a fake author.
/// </summary>
public class LuaMigrationTests
{
    /// <summary>Three saved scripts from the live library (data/alice-snap, 10-28), as they were written in Lua.</summary>
    internal static readonly (string Name, string Lua, string Js)[] Samples =
    [
        ("gen_loop", """
            -- purpose: keep mining the cobble gen patiently, deposit when full
            ensure"pickaxe"
            for round=1,20 do
              cp"mine"
              local ok,m=harvest_generator{at="6,64,8",count=20}
              if inv.free()<3 or inv"cobblestone">=128 then local d,dm=deposit{at="14,64,-5",items={"cobblestone"}} expect(d,dm) end
              wait(3)
              ensure"pickaxe"
            end
            return "done 20 rounds"
            """, """
            // purpose: keep mining the cobble gen patiently, deposit when full
            const P = params({ at: '6,64,8', chest: '14,64,-5', rounds: 20, per: 20 })
            use(replaceToolWhenBroken('pickaxe'))
            ensure('pickaxe')
            return `mined ${gather('cobblestone', P.rounds * P.per, { from: 'gen', at: P.at, per: P.per, stash: P.chest }).n} cobble`
            """),
        ("hive_e4b353", """
            -- dump inventory then fight mobs at night
            local ok,m=deposit{"14,64,-5"} log(m)
            on("danger",function() if me.hp<=4 then go{"8,64,-8"} end end)
            for i=1,20 do
             if me.hp<=4 then go{"8,64,-8"} return "retreated low hp" end
             local ok,m=skill("fight",{}) log(m)
             if inv"rotten_flesh">0 then eat{} end
             wait(3)
            end
            return "patrol done"
            """, """
            // purpose: dump inventory then fight mobs at night
            const P = params({ chest: '14,64,-5', home: '8,64,-8' })
            stash('all-but-tools', { to: P.chest })
            let fights = 0
            patrol([me.pos], () => {
              if (me.hp <= 4) { expect(go(P.home)); return 'stop' }
              const m = hostile(8)
              if (m && ok(skill('fight', {}))) fights++; else wait(3)
            }, { rounds: 20, until: () => !night() })
            return 'patrol done: ' + fights
            """),
        ("west_trees", """
            -- wait for west tree farm trees to grow, chop+replant them
            local spots={"9,65,0","9,65,3","13,65,0","13,65,3"}
            local n=0
            upto(20,function()
              for _,s in ipairs(spots) do
                local x,y,z=s:match("(-?%d+),(-?%d+),(-?%d+)")
                local b=block(tonumber(x),tonumber(y),tonumber(z))
                if b and b:find("log") then local ok,m=chop{s} if ok then n=n+1 end end
              end
              if n>=4 then return false end
              wait(30) return true
            end)
            if inv"log">0 then deposit{"14,64,-5",{"log"}} end
            return "chopped "..n
            """, """
            // purpose: wait for west tree farm trees to grow, chop+replant them
            const P = params({ spots: ['9,65,0', '9,65,3', '13,65,0', '13,65,3'], chest: '14,64,-5' })
            const r = quota(4, left => {
              const done = forEach(P.spots.filter(s => /log/.test(block(s))), s => chop(s), { budget: left })
              if (done.n < left) wait(30)
              return done.n
            }, { max: 20, idle: 20 })
            stash(['log'], { to: P.chest })
            return 'chopped ' + r.n
            """),
    ];

    /// <summary>The author: translates each sample by name ("migrate" jobs); a name in <see cref="Broken"/> always gets a lint-failing draft.</summary>
    private sealed class FakeAuthor : ILlmClient
    {
        public ConcurrentQueue<LlmRequest> Requests { get; } = new();
        public HashSet<string> Broken { get; } = new();
        public LlmUsage Usage { get; init; } = new(2000, 1500, 0, 0);

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Requests.Enqueue(request);
            var brief = string.Join("\n", request.Messages.First().Content.OfType<LlmText>().Select(t => t.Text));
            var sample = Samples.FirstOrDefault(x => brief.Contains($"\"{x.Name}\"", StringComparison.Ordinal));
            var js = sample.Name == null || Broken.Contains(sample.Name) ? "// purpose: broken\nminee('dirt')" : ScriptPrompts.Unindent(sample.Js);
            return Task.FromResult(new LlmResponse([new LlmText("```js\n" + js + "\n```")], StopReasons.EndTurn, Usage, request.Model ?? "claude-opus-5-5"));
        }

        public LlmRequest[] Migrations => Requests.Where(r => r.Purpose == "author-migrate").ToArray();
    }

    private static (BotHost H, ScriptBridge B) Bot(ILlmClient llm, ScriptOptions? scripts = null)
    {
        var h = BotHost.Create(new FakeGameClient(), TestBot.Options(), new BotOptions { Scripts = scripts ?? new ScriptOptions() }, llm, persist: false);
        return (h, ScriptTools.Of(h.Runtime)!);
    }

    private static void SeedLua(ScriptBridge b)
    {
        foreach (var (name, lua, _) in Samples)
            b.Library.SaveVersion(name, ScriptPrompts.Unindent(lua), author: "main/claude-opus-5-5", purpose: ScriptLint.Purpose(ScriptPrompts.Unindent(lua)), lang: null);
    }

    [Fact]
    public async Task Translates_each_saved_Lua_script_once_into_its_next_version_keeping_the_Lua()
    {
        var llm = new FakeAuthor();
        var (h, b) = Bot(llm);
        SeedLua(b);
        Assert.Equal(3, new LuaMigration(b).Pending().Count);
        var items = await b.StartLuaMigrationAsync(CancellationToken.None);
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.StartsWith("ok v2 from Lua v1", i.Verdict));
        foreach (var (name, lua, js) in Samples)
        {
            var sc = b.Library.Get(name)!.Script!;
            Assert.Equal(2, sc.Version);
            Assert.Equal("js", sc.Lang);
            Assert.Equal(ScriptPrompts.Unindent(js), sc.Code);
            Assert.Equal(ScriptPrompts.Unindent(lua), sc.Versions.Single(v => v.V == 1).Code); // the Lua stays as history
            Assert.True(sc.Versions.Single(v => v.V == 1).IsLua);
            Assert.StartsWith("ok v2", sc.Migration);
        }
        var job = llm.Migrations[0];
        Assert.Equal(ScriptPrompts.AuthorSystem, job.System[0].Text);
        Assert.Contains("```lua", job.Messages[0].Content.OfType<LlmText>().First().Text); // the brief carries the Lua
        Assert.All(llm.Migrations, r => Assert.True(r.MaxUsd <= 0.25m, $"per-script cap {r.MaxUsd}"));
        // Once: nothing pending, no more author calls.
        int calls = llm.Requests.Count;
        Assert.Empty(await b.StartLuaMigrationAsync(CancellationToken.None));
        Assert.Equal(calls, llm.Requests.Count);
        // The migrated script runs as JS.
        var run = await h.Run("script", new { op = "run", name = "west_trees" });
        Assert.True(run.Ok, run.Text);
    }

    [Fact]
    public async Task A_failed_translation_gets_a_verdict_and_the_total_cap_stops_the_run()
    {
        var llm = new FakeAuthor { Usage = new LlmUsage(200_000, 30_000, 0, 0) };
        llm.Broken.Add("gen_loop");
        var (_, b) = Bot(llm, new ScriptOptions { MigrateMaxUsd = 0.30m });
        SeedLua(b);
        var items = await b.StartLuaMigrationAsync(CancellationToken.None);
        var first = Assert.Single(items); // the most used first; then the $0.30 total is gone
        Assert.Equal("gen_loop", first.Name);
        Assert.StartsWith("failed:", first.Verdict);
        Assert.StartsWith("failed:", b.Library.Get("gen_loop")!.Script!.Migration);
        Assert.True(ScriptLibrary_Active(b, "gen_loop").IsLua); // still the Lua, still runnable (LegacyLua)
        Assert.Null(b.Library.Get("west_trees")!.Script!.Migration); // untouched: next start
    }

    private static Agent.Routines.ScriptVersion ScriptLibrary_Active(ScriptBridge b, string name) => Agent.Routines.ScriptLibrary.Active(b.Library.Get(name)!.Script!);

    [Fact]
    public async Task Migration_is_off_by_config_and_legacy_lua_can_be_switched_off()
    {
        var llm = new FakeAuthor();
        var (h, b) = Bot(llm, new ScriptOptions { MigrateLua = false, LegacyLua = false });
        SeedLua(b);
        Assert.Empty(await b.StartLuaMigrationAsync(CancellationToken.None));
        Assert.Empty(llm.Requests);
        var r = await h.Run("script", new { op = "run", name = "gen_loop" });
        Assert.False(r.Ok);
        Assert.Contains("legacy Lua and not migrated", r.Text);
    }

    [Fact]
    public async Task Params_are_tuned_without_new_code_and_runs_record_them()
    {
        var (h, b) = Bot(new FakeAuthor());
        var save = await h.Run("script", new { op = "save", name = "tunable", code = "// purpose: say the params\nconst P = params({ n: 64, at: 'auto' })\nreturn `n=${P.n} at=${P.at}`" });
        Assert.True(save.Ok, save.Text);
        var tune = await h.Run("script", new { op = "save", name = "tunable", @params = new { n = 32, extra = 1 } });
        Assert.True(tune.Ok, tune.Text);
        Assert.Contains("tuned {\"n\":32,\"extra\":1}", tune.Text);
        Assert.Contains("not declared by its params(): extra", tune.Text);
        var run = await h.Run("script", new { name = "tunable", @params = new { at = "6,64,8" } }); // a name alone runs it
        Assert.True(run.Ok, run.Text);
        Assert.Contains("n=32 at=6,64,8", run.Text);
        var v = ScriptLibrary_Active(b, "tunable");
        Assert.Equal(1, v.Version());
        var pr = Assert.Single(v.ParamRuns);
        Assert.Contains("\"n\":32", pr.Params);
        Assert.Contains("\"at\":\"6,64,8\"", pr.Params);
        Assert.Contains("tuned", b.Library.Catalog());
    }

    [Fact]
    public async Task Checkpoints_of_saved_scripts_are_durable_in_their_row()
    {
        var (h, b) = Bot(new FakeAuthor());
        var r = await h.Run("script", new { op = "run", name = "cp_job", code = "// purpose: checkpoint\ncarry.logs = 9\ncp('store')\nreturn 'ok ' + carry.logs" });
        Assert.True(r.Ok, r.Text);
        var row = b.Library.Get("cp_job")!.Script!.Checkpoints;
        var (key, snap) = Assert.Single(row);
        Assert.EndsWith("/store", key); // "<lane>/<label>": the lane's body or this process's account
        Assert.Contains("\"logs\":9", snap);
    }

}

internal static class ScriptVersionTestExtensions
{
    public static int Version(this Agent.Routines.ScriptVersion v) => v.V;
}
