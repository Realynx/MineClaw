using System.Diagnostics;
using MineClaw.Agent.Authoring;
using MineClaw.Agent.Llm;
using MineClaw.Scripting;
using Xunit.Abstractions;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Before (Lua, the old prelude: what the author/brain had to write) vs after (JS + SDK) for five representative jobs, measured
/// deterministically on hand-written equivalents: lines, chars, estimated output tokens (the repo's chars/3.5), plus the author
/// loop replayed with a fake LLM (one call, lint + dry run) and the engine's per-run start cost. The numbers go in ARCHITECTURE.md.
/// </summary>
public class SpeedComparisonTests(ITestOutputHelper output)
{
    public static readonly (string Task, string Lua, string Js)[] Tasks =
    [
        ("gen mining loop", """
            -- purpose: mine n cobble at the gen, store it, re-tool, stay fed
            local n, at, chest = args.n or 64, args.at or "auto", args.chest
            ensure"pickaxe" room(2)
            local got = 0
            cp"mine"
            upto(n * 3, function()
              if inv"stone_pickaxe" + inv"wood_pickaxe" + inv"iron_pickaxe" == 0 then ensure"pickaxe" end
              local b = inv"cobblestone"
              local ok, m = retry(3, function() return gen{at, math.min(8, n - got)} end)
              if not ok then if m:find("no free cell") then wait(3) return true end fail(m) end
              got = got + math.max(0, inv"cobblestone" - b)
              if me.food < 14 then eat{} end
              if inv.free() < 2 then local d, dm = deposit{at = chest, items = {"cobblestone"}} expect(d, dm) end
              prog(100 * got / n)
              return got < n
            end)
            cp"store"
            local ok, m = deposit{at = chest, items = {"cobblestone"}} expect(ok, m)
            return "mined " .. got .. " cobble"
            """, """
            // purpose: mine n cobble at the gen, store it, re-tool, stay fed
            const P = params({ n: 64, at: 'auto', chest: null })
            use(keepFed(), replaceToolWhenBroken('pickaxe'))
            ensure('pickaxe')
            return `mined ${gather('cobblestone', P.n, { from: 'gen', at: P.at, stash: P.chest }).n} cobble`
            """),
        ("wheat harvest", """
            -- purpose: harvest ripe wheat, replant, store the wheat; n rounds, waiting for regrowth
            local rounds, chest = args.rounds or 3, args.chest
            ensure"hoe"
            for r = 1, rounds do
              cp("round" .. r)
              local ok, m = harvest_wheat{radius = args.radius or 8, replant = true}
              if not ok and not m:find("nothing ripe") then fail(m) end
              if inv"wheat" > 0 then local d, dm = deposit{at = chest, items = {"wheat", "seeds"}} expect(d, dm) end
              if r < rounds then wait(args.wait or 120) end
            end
            return "harvested " .. rounds .. " rounds"
            """, """
            // purpose: harvest ripe wheat, replant, store the wheat; n rounds, waiting for regrowth
            const P = params({ rounds: 3, radius: 8, chest: null, waitS: 120 })
            ensure('hoe')
            for (let r = 1; r <= P.rounds; r++) {
              gather('wheat', 64, { from: 'harvest', r: P.radius, stash: P.chest })
              if (r < P.rounds) wait(P.waitS)
            }
            return `harvested ${P.rounds} rounds`
            """),
        ("deposit run", """
            -- purpose: store everything but tools and food in the chest, then come back
            local chest, back = args.chest or "14,64,-5", me.pos
            local ok, m = go{chest} expect(ok, m)
            ok, m = deposit{at = chest} expect(ok, m)
            ensure{"pickaxe", "food"}
            ok, m = go{back} expect(ok, m)
            return "stored"
            """, """
            // purpose: store everything but tools and food in the chest, then come back
            const P = params({ chest: '14,64,-5' })
            const back = me.pos
            expect(stash('all-but-tools', { to: P.chest }))
            ensure(['pickaxe', 'food'])
            expect(go(back))
            return 'stored'
            """),
        ("guard the gen at night", """
            -- purpose: mine the gen by day, guard it at night, retreat when hurt; stop after n cobble
            local at, home, n = args.at or "auto", args.home or "home", args.n or 128
            ensure{"pickaxe", "sword"}
            local got = 0
            upto(400, function()
              if me.hp <= 6 then go{home} wait(30) return true end
              if night() then
                local mob = near("zombie", 8) or near("skeleton", 8) or near("spider", 8)
                if mob then local ok, m = attack{mob.name} if not ok then log(m) end else wait(3) end
              else
                local b = inv"cobblestone"
                local ok, m = gen{at, 8}
                if not ok and not m:find("no free cell") then fail(m) end
                got = got + math.max(0, inv"cobblestone" - b)
                if inv.free() < 2 then local d, dm = deposit{} expect(d, dm) end
              end
              if me.food < 14 then eat{} end
              return got < n
            end)
            return "mined " .. got .. ", guarded the nights"
            """, ScriptPrompts.Examples[1].Code),
        ("2-body gen mining", """
            -- purpose: n cobble per body at the gen, auto cells, stored
            function miner(a)
              ensure"pickaxe" room(2)
              local got = 0
              cp"mine"
              upto(a.n, function()
                local ok, m, n = gen{"auto", 8}
                if not ok then
                  if m:find("no free cell") then wait(3) return true end
                  fail(m)
                end
                got = got + (n or 0); crew.inc("cobble", n or 0)
                prog(crew.get("cobble") / a.total)
                if inv.free() < 2 then local ok2, m2 = deposit{} expect(ok2, m2) end
                return got < a.n
              end)
              local ok, m = deposit{} expect(ok, m)
              return me.name .. " " .. got
            end
            function main(a)
              local names, n, jobs = a.bodies or {"67", "69"}, a.n or 64, {}
              for _, b in ipairs(names) do jobs[#jobs + 1] = {body(b), "miner", {n = n, total = n * #names}} end
              local r = parallel(jobs)
              return "gen crew: " .. crew.get("cobble") .. " cobble (" .. table.concat(r, ", ") .. ")"
            end
            """, ScriptPrompts.Examples[2].Code),
    ];

    private static readonly string[] Acts = ["mine", "gen", "harvest_generator", "deposit", "take", "withdraw", "go", "goto", "eat", "attack", "dig", "harvest_wheat"];

    private static int Lines(string code) => code.Split('\n').Count(l => l.Trim().Length > 0);
    private static int Tok(string s) => (int)Math.Ceiling(s.Length / 3.5);

    [Fact]
    public void Js_with_the_sdk_is_shorter_and_every_after_script_is_clean()
    {
        int luaLines = 0, jsLines = 0, luaTok = 0, jsTok = 0;
        output.WriteLine("| task | Lua lines | JS lines | Lua chars | JS chars | Lua out tok | JS out tok |");
        output.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var (task, luaRaw, jsRaw) in Tasks)
        {
            string lua = ScriptPrompts.Unindent(luaRaw), js = ScriptPrompts.Unindent(jsRaw);
            var lint = ScriptLint.Check(js, Acts, 6, null, ScriptLanguage.Js);
            Assert.False(lint.HasErrors, task + ": " + string.Join(" | ", lint.Errors));
            Assert.True(Lines(js) <= Lines(lua), $"{task}: {Lines(js)} vs {Lines(lua)} lines");
            output.WriteLine($"| {task} | {Lines(lua)} | {Lines(js)} | {lua.Length} | {js.Length} | {Tok(lua)} | {Tok(js)} |");
            luaLines += Lines(lua); jsLines += Lines(js); luaTok += Tok(lua); jsTok += Tok(js);
        }
        output.WriteLine($"| total | {luaLines} | {jsLines} | | | {luaTok} | {jsTok} |");
        output.WriteLine($"lines saved {luaLines - jsLines} ({100.0 * (luaLines - jsLines) / luaLines:0}%), output tokens saved {luaTok - jsTok} ({100.0 * (luaTok - jsTok) / luaTok:0}%)");
        Assert.True(jsTok < luaTok * 0.6, $"JS {jsTok} vs Lua {luaTok} tokens");
    }

    /// <summary>The author loop replayed: one fake Opus call returning the after-script, then lint + dry run, as for real.</summary>
    [Fact]
    public async Task Author_loop_replayed_with_the_hand_written_answers()
    {
        var host = new ScriptHost
        {
            World = new World.WorldState(),
            Actions = Acts.Select(a => new ScriptAction(a, a switch { "gen" or "harvest_generator" => ["at", "count"], "deposit" => ["at", "items"], "go" or "goto" => ["to"], _ => ["block", "count"] })).ToList(),
            Skills = () => ["harvest_wheat"],
        };
        output.WriteLine("| task | author calls | output tok | check ms (lint + dry run) |");
        output.WriteLine("|---|---|---|---|");
        foreach (var (task, _, jsRaw) in Tasks)
        {
            var js = ScriptPrompts.Unindent(jsRaw);
            var llm = new Replay("```js\n" + js + "\n```");
            var author = new AuthorAgent(llm, new LlmOptions());
            var sw = new Stopwatch();
            var outcome = await author.RunAsync(new AuthorJob
            {
                Kind = "script", System = ScriptPrompts.AuthorSystem, Brief = "Goal: " + task,
                Check = code =>
                {
                    sw.Start();
                    var lint = ScriptLint.Check(code, Acts, 6, null, ScriptLanguage.Js);
                    var dry = lint.HasErrors ? lint.Format(LintSeverity.Error) : ScriptDryRun.Run(code, host);
                    sw.Stop();
                    return dry is { Length: > 0 } ? AuthorCheck.Fail(dry) : AuthorCheck.Pass;
                },
            }, CancellationToken.None);
            Assert.True(outcome.Ok, task + ": " + outcome.Blocker);
            Assert.Equal(1, outcome.Versions);
            output.WriteLine($"| {task} | {llm.Calls} | {Tok(js)} | {sw.ElapsedMilliseconds} |");
        }
        output.WriteLine($"author system prompt (cached): {Tok(ScriptPrompts.AuthorSystem)} tok; brain-side now: tool + {Tok(ScriptPrompts.AuthorLine)} tok line");
    }

    [Fact]
    public async Task Engine_start_cost_per_run()
    {
        using var lua = new Rig();
        using var js = Rig.Js();
        for (int i = 0; i < 3; i++) { await lua.Run("return 1", "w" + i); await js.Run("return 1", "w" + i); } // warm
        var l = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) await lua.Run("return 1", "l" + i);
        l.Stop();
        var j = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) await js.Run("return 1", "j" + i);
        j.Stop();
        output.WriteLine($"start-to-end of a trivial run: Lua {l.ElapsedMilliseconds / 20.0:0.0} ms, JS + SDK {j.ElapsedMilliseconds / 20.0:0.0} ms");
        Assert.True(j.ElapsedMilliseconds / 20.0 < 250, "JS run start too slow");
    }

    private sealed class Replay(string text) : ILlmClient
    {
        public int Calls;
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResponse([new LlmText(text)], StopReasons.EndTurn, new LlmUsage(500, (int)Math.Ceiling(text.Length / 3.5), 0, 0), "claude-opus-5-5"));
        }
    }
}
