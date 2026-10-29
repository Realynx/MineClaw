using System.Text.Json;
using MineClaw.Data;
using MineClaw.Scripting;
using MineClaw.Scripting.Crew;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

public class JsLintTests
{
    private static readonly string[] Acts = ["goto", "go", "mine", "gen", "harvest_generator", "deposit", "take", "withdraw", "dig", "eat", "say", "chop", "chop_tree", "attack"];

    private static readonly Dictionary<string, BindingSignature> Sigs = new()
    {
        ["harvest_generator"] = new(["at", "count", "store", "stand"], new HashSet<string> { "at" }),
        ["withdraw"] = new(["item", "count", "at"], new HashSet<string> { "item" }),
        ["goto"] = new(["to", "dist"], new HashSet<string> { "to" }),
    };

    private static LintReport L(string code) => ScriptLint.Check(code, Acts, 6, Sigs, ScriptLanguage.Js);

    [Theory]
    [InlineData("const x = ;", "syntax")]
    [InlineData("minee('dirt')", "unknown-global")]
    [InlineData("eval('1')", "forbidden")]
    [InlineData("require('fs')", "forbidden")]
    [InlineData("setTimeout(() => mine('dirt'), 10)", "forbidden")]
    [InlineData("__h.wait(1)", "forbidden")]
    [InlineData("import('x')", "forbidden")]
    [InlineData("while (true) { const h = me.hp }", "busy-loop")]
    [InlineData("for (;;) { inv('dirt') }", "busy-loop")]
    [InlineData("gen()", "missing-arg")]
    [InlineData("take({count: 3})", "missing-arg")]
    [InlineData("gen = 5", "readonly-global")]
    [InlineData("machine({ initial: 'a', states: { a: { tick: () => 'b' , on: { danger: 'nope' } } } })", "machine-state")]
    [InlineData("machine({ initial: 'x', states: { a: {} } })", "machine-state")]
    [InlineData("function miner() {}\nmine('dirt', 1)\nparallel([['67', 'miner']])", "crew-toplevel")]
    [InlineData("function main() { parallel([['67', 'digger']]) }", "crew-unknown-role")]
    [InlineData("function m() {}\nfunction main() { parallel([['67', 'm'], ['67', 'm']]) }", "crew-dup-body")]
    public void Errors_reject_the_script(string code, string rule)
    {
        var r = L(code);
        Assert.True(r.Errors.Any(f => f.Rule == rule), $"{rule}? got: {string.Join(" | ", r.Findings)}");
    }

    [Theory]
    [InlineData("for (let i = 0; i < 9; i++) { mine('dirt', 1) }", "unchecked")]
    [InlineData("for (let i = 0; i < 3; i++) say('hi')", "say-in-loop")]
    [InlineData("gen('auto', 8)", "no-ensure")]
    [InlineData("while (true) { mine('dirt', 1) }", "unbounded")]
    [InlineData("mine('a',1); mine('a',1); mine('a',1); mine('a',1); mine('a',1); mine('a',1); mine('a',1)", "no-cp")]
    [InlineData("machine({ initial: 'a', states: { a: { on: { danjer: 'a' } } } })", "machine-event")]
    [InlineData("function miner() { say('hi') }\nfunction main() { parallel([['67', 'miner']]) }", "crew-say")]
    [InlineData("await new Promise(r => r(1))", "promise")]
    public void Warnings_ride_along(string code, string rule)
    {
        var r = L(code);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
        Assert.True(r.Warnings.Any(f => f.Rule == rule), $"{rule}? got: {string.Join(" | ", r.Findings)}");
    }

    [Theory]
    [InlineData("const r = gen('auto', 8)\nexpect(r)")]
    [InlineData("gen({ at: 'auto' })")]
    [InlineData("const p = { to: 'home' }\ngo(p)")]
    [InlineData("go(1, 64, 2)")]
    [InlineData("for (let i = 0; i < 3; i++) { const r = mine('dirt', 1); if (!r.ok) break }")]
    [InlineData("while (true) { if (!mine('dirt', 1).ok) break }")]
    [InlineData("const { a, b: [c] } = { a: 1, b: [2] }\nlet d = a + c\nlabel: for (const x of [1]) { continue label }\nreturn d")]
    [InlineData("class K { m() { return this.v } }\nconst k = new K()\nreturn String(k.m()) + Math.max(1, 2) + JSON.stringify({ n: 1 })")]
    public void Clean_scripts_pass(string code)
    {
        var r = L(code);
        Assert.False(r.HasErrors, string.Join(" | ", r.Errors));
    }

    [Fact]
    public void Error_format_is_the_compact_author_feedback()
    {
        var r = L("const a = 1\nminee('dirt')\ngen()");
        var text = r.Format(LintSeverity.Error, 300);
        Assert.StartsWith("lint errors (fix and resubmit): L2 unknown name 'minee' (did you mean mine", text);
        Assert.Contains("L3 gen() needs at=", text);
    }

    [Fact]
    public void Library_facts_purpose_params_similarity()
    {
        const string a = "// purpose: mine the gen\nconst P = params({ n: 64, at: 'auto' })\nmineGen({ n: P.n, at: P.at })\nreturn args.extra";
        Assert.Equal("mine the gen", ScriptLint.Purpose(a));
        Assert.Equal(["n", "at", "extra"], ScriptLint.ArgNames(a));
        Assert.Equal([("n", "64"), ("at", "'auto'")], ScriptLint.Params(a));
        var b = a.Replace("64", "32");
        Assert.True(ScriptLint.Similarity(a, b) > 0.9);
        Assert.True(ScriptLint.Similarity(a, "say('hi')") < 0.3);
        Assert.NotEqual(ScriptLint.ExactKey(a), ScriptLint.ExactKey(b));
        Assert.Equal(ScriptLint.ExactKey(a), ScriptLint.ExactKey(a.Replace("// purpose: mine the gen", "// other comment")));
    }

    [Fact]
    public void Language_detection()
    {
        Assert.Equal(ScriptLanguage.Lua, ScriptLanguages.Detect("ensure\"pickaxe\"\nlocal ok,m=deposit{} expect(ok,m)"));
        Assert.Equal(ScriptLanguage.Lua, ScriptLanguages.Detect("-- purpose: x\nupto(20,function() gen{\"auto\",1} end)"));
        Assert.Equal(ScriptLanguage.Js, ScriptLanguages.Detect("// purpose: x\nconst r = gen('auto', 1)\nexpect(r)"));
        Assert.Equal(ScriptLanguage.Js, ScriptLanguages.Detect("wait(2)"));
    }
}

/// <summary>A fake multi-body port: each body is its own rig (controller + world), lanes start on that body's controller.</summary>
public sealed class FakeCrew : ICrewPort, IDisposable
{
    public Dictionary<string, Rig> Rigs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ICrewBoard Board { get; } = new InMemoryCrewBoard();

    public FakeCrew(params string[] bodies) : this(null, bodies) { }

    public FakeCrew(ScriptOptions? options, params string[] bodies)
    {
        foreach (var b in bodies) Rigs[b] = Rig.Js(options, crew: this, body: b);
    }

    /// <summary>Bodies in game now (null = every rig): tests log bodies in late and out mid-job.</summary>
    public HashSet<string>? Online { get; set; }

    public IReadOnlyList<string> Bodies() { lock (this) return Rigs.Keys.Where(b => Online?.Contains(b) != false).ToList(); }

    /// <summary>Bodies allocated to this crew and still logging in (a hive script's late logins): those of them not online yet.</summary>
    public HashSet<string>? Coming { get; set; }

    public int Pending() { lock (this) return Coming?.Count(b => Online?.Contains(b) != true) ?? 0; }

    public BodyInfo? Info(string body) => Rigs.TryGetValue(body, out var r)
        ? new BodyInfo(body, r.World.Player.Health, r.World.Player.Food, new BlockPos(0, 65, 0), r.C.Current != null, r.C.Current?.Name, r.World.Inventory.EmptySlotCount())
        : null;

    public int Count(string body, string item) => Rigs.TryGetValue(body, out var r) ? r.World.Inventory.CountItem(Items.ByName(item)!.Id, null) : -1;

    public ILane Start(LaneSpec spec)
    {
        if (!Rigs.TryGetValue(spec.Body, out var rig)) return new Lane(spec.Body, null, Task.FromResult(ActResult.Fail($"no body {spec.Body}")));
        var (s, err) = rig.C.Start(new ScriptRequest
        {
            Name = spec.Script, Code = spec.Code, Language = spec.Language, Role = spec.Role, ArgsJson = spec.ArgsJson, CrewId = spec.CrewId,
        });
        if (s == null) return new Lane(spec.Body, null, Task.FromResult(ActResult.Fail($"body {spec.Body} busy: {err}")));
        return new Lane(spec.Body, s, s.Completion.ContinueWith(_ => new ActResult(s.Ok, s.LaneResult)));
    }

    public async Task<ActResult> ActAsync(string body, string action, JsonElement args, CancellationToken ct)
    {
        if (!Rigs.TryGetValue(body, out var r)) return ActResult.Fail("no body " + body);
        if (r.C.Current != null) return ActResult.Fail($"body {body} busy: {r.C.Current.State()}");
        r.Calls.Enqueue($"{action} {args.GetRawText()}");
        return await (r.On.TryGetValue(action, out var f) ? f(args, ct) : Task.FromResult(ActResult.Success($"{action} ok")));
    }

    private sealed class Lane(string body, ScriptSession? s, Task<ActResult> done) : ILane
    {
        public string Body => body;
        public string Status => s?.State() ?? "not started";
        public Task<ActResult> Completion => done;
        public void Cancel(string reason) => s?.Stop(reason);
    }

    public void Dispose() { foreach (var r in Rigs.Values) r.Dispose(); }
}

public class CrewTests
{
    private static void Miner(Rig rig)
    {
        int cobble = Items.ByName("cobblestone")!.Id;
        rig.Give(9, Items.ByName("stone_pickaxe")!.Id, 1);
        int slot = 10;
        rig.On["harvest_generator"] = (args, _) => { int n = args.GetProperty("count").GetInt32(); rig.Give(slot++, cobble, n); return Task.FromResult(ActResult.Success($"+{n} cobblestone")); };
    }

    private static void Chopper(Rig rig)
    {
        int log = Items.ByName("log")!.Id;
        int slot = 10;
        rig.On["chop_tree"] = (_, _) => { rig.Give(slot++, log, 4); return Task.FromResult(ActResult.Success("chopped oak tree (4 logs, 1 sapling), replanted")); };
    }

    [Fact]
    public async Task Parallel_runs_roles_on_each_body_and_shares_the_board()
    {
        using var crew = new FakeCrew("67", "69");
        foreach (var r in crew.Rigs.Values) Chopper(r);
        using var main = Rig.Js(crew: crew);
        var s = await main.Run(ScriptPrompts.Unindent(ScriptPrompts.Examples[2].Code), "wood_crew", @params: """{"n":16}""");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", main.Logs));
        Assert.Contains("wood crew: 32 logs (67 16, 69 16)", s.Outcome);
        foreach (var b in new[] { "67", "69" })
            Assert.Contains(crew.Rigs[b].Norm(), c => c.StartsWith("chop"));
        Assert.DoesNotContain(main.CallList, c => c.StartsWith("chop")); // the coordinator itself never chopped
    }

    [Fact]
    public async Task The_gen_crew_runs_roles_on_each_body()
    {
        using var crew = new FakeCrew("67", "69");
        foreach (var r in crew.Rigs.Values) Miner(r);
        using var main = Rig.Js(crew: crew);
        const string code = """
            const P = params({ n: 64, bodies: ['67', '69'] })
            function miner(a) {
              const got = gather('cobblestone', a.n, { from: 'gen', stash: true }).n
              crew.inc('cobble', got)
              return `${me.body} ${got}`
            }
            function main() {
              const r = parallel(P.bodies.map(b => [b, 'miner', { n: P.n }]))
              return `gen crew: ${crew.get('cobble')} cobble (${r.join(', ')})`
            }
            """;
        var s = await main.Run(code, "gen_crew", @params: """{"n":16}""");
        Assert.True(s.Ok, s.Outcome + " | " + string.Join(";", main.Logs));
        Assert.Contains("gen crew: 32 cobble (67 16, 69 16)", s.Outcome);
        Assert.DoesNotContain(main.CallList, c => c.StartsWith("harvest_generator"));
    }

    [Fact]
    public async Task First_failing_lane_cancels_its_siblings_unless_keepGoing()
    {
        using var crew = new FakeCrew("67", "69");
        crew.Rigs["69"].On["mine"] = (_, _) => Task.FromResult(ActResult.Fail("no free cell: held by 1337"));
        crew.Rigs["67"].On["mine"] = Rig.Blocking(new TaskCompletionSource());
        using var main = Rig.Js(crew: crew);
        const string code = """
            function digger() { expect(mine('cobblestone', 1)); return me.body + ' dug' }
            function main(a) { return parallel([['67', 'digger'], ['69', 'digger']], { keepGoing: a.keep }).join(' | ') }
            """;
        var s = await main.Run(code, "crew1");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("lane 69 failed", s.Outcome);
        await Rig.Until(() => crew.Rigs["67"].C.Current == null); // sibling cancelled
        crew.Rigs["67"].On["mine"] = (_, _) => Task.FromResult(ActResult.Success("mined 1"));
        var k = await main.Run(code, "crew2", args: """{"keep":true}""");
        Assert.True(k.Ok, k.Outcome);
        Assert.Contains("67 dug", k.Outcome);
        Assert.Contains("failed:", k.Outcome);
    }

    [Fact]
    public async Task Body_handles_send_recv_barrier_and_locks()
    {
        using var crew = new FakeCrew("67", "69");
        using var main = Rig.Js(crew: crew);
        var s = await main.Run("""
            function pinger() {
              crew.send('69', { item: 'log', n: 4 })
              expect(crew.barrier('b1', 2, 10))
              return 'pinged ' + crew.lock('chest', 30)
            }
            function ponger() {
              const m = crew.recv(10)
              expect(crew.barrier('b1', 2, 10))
              return `got ${m.n} ${m.item} lock ${crew.lock('chest', 30)}`
            }
            function main() {
              const b = body('67')
              const hp = b.hp, free = b.free(), held = b.inv('cobblestone')
              const r = b.dig('1,64,1')
              const l = spawn('69', 'ponger')
              const out = parallel([['67', 'pinger']])
              return [hp, free > 0, held, r.ok, out[0], l.wait(10).msg].join(' ; ')
            }
            """, "talk");
        Assert.True(s.Ok, s.Outcome);
        var m = System.Text.RegularExpressions.Regex.Match(s.Outcome!, @"20 ; true ; 0 ; true ; pinged (\w+) ; got 4 log lock (\w+)");
        Assert.True(m.Success, s.Outcome);
        Assert.NotEqual(m.Groups[1].Value, m.Groups[2].Value); // exactly one lane holds the "chest" lease
        // 69 got the message, both met at the barrier, and 67 holds the "chest" lease.
        Assert.Contains("""dig {"at":"1,64,1"}""", crew.Rigs["67"].Norm());
    }

    [Fact]
    public async Task One_body_has_no_crew()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("return bodies().length + ' ' + crew.inc('x', 2)");
        Assert.Contains("0 2", s.Outcome); // the board is local; there are no other bodies
        var t = await rig.Run("function r() {}\nfunction main() { return parallel([['67', 'r']]) }", "solo");
        Assert.Contains("no crew here", t.Outcome);
    }
}

public class ExampleTests
{
    [Fact]
    public void Examples_are_lint_clean()
    {
        string[] acts = ["mine", "gen", "harvest_generator", "deposit", "take", "withdraw", "go", "goto", "eat", "attack", "dig", "chop", "chop_tree", "place"];
        foreach (var (title, code) in ScriptPrompts.Examples)
        {
            var r = ScriptLint.Check(ScriptPrompts.Unindent(code), acts, 6, null, ScriptLanguage.Js);
            Assert.False(r.HasErrors, title + ": " + string.Join(" | ", r.Errors));
            Assert.Empty(r.Warnings);
        }
    }

    [Fact]
    public async Task The_gen_and_machine_examples_run()
    {
        using var rig = Rig.Js();
        int cobble = Items.ByName("cobblestone")!.Id;
        rig.Give(9, Items.ByName("stone_pickaxe")!.Id, 1);
        int slot = 10;
        rig.On["harvest_generator"] = (args, _) => { int n = args.GetProperty("count").GetInt32(); rig.Give(slot++, cobble, n); return Task.FromResult(ActResult.Success($"+{n} cobblestone")); };
        rig.On["deposit"] = (_, _) => { for (int i = 10; i < 45; i++) rig.World.Inventory.SetSlot(0, i, default); slot = 10; return Task.FromResult(ActResult.Success("deposited")); };
        var a = await rig.Run(ScriptPrompts.Exemplar, "gen", @params: """{"n":24}""");
        Assert.True(a.Ok, a.Outcome);
        Assert.Contains("mined 24 cobble", a.Outcome);
        // The world-neutral machine: mine stone (no ore in view) by day until n blocks.
        rig.On["mine"] = (args, _) => { rig.Give(slot++, cobble, 8); return Task.FromResult(ActResult.Success("mined 8 stone")); };
        var b = await rig.Run(ScriptPrompts.Unindent(ScriptPrompts.Examples[1].Code), "mine_machine", @params: """{"n":32}""");
        Assert.True(b.Ok, b.Outcome + " | " + string.Join(";", rig.Logs.TakeLast(5)));
        Assert.Contains("done", b.Outcome);
        Assert.True(rig.CallList.Any(c => c.StartsWith("mine ") && c.Contains("\"stone\"")), string.Join(";", rig.CallList.Take(8)));
    }
}
