using MineClaw.Agent.Llm;
using MineClaw.Bot.Tests;
using MineClaw.Data;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Opus-only authorship, the reliability layers (helpers, pre-run lint, per-version stats and promotion) and saved-script
/// versioning (update, near-duplicate refusal, args, auto-revert, rollback, housekeeping).
/// </summary>
public class ScriptReliabilityTests
{
    private const string Opus = "main/claude-opus-5-5";

    // ------------------------------------------------------------------ authorship

    [Fact]
    public async Task Worker_code_is_refused_but_a_saved_script_runs_by_name()
    {
        var (h, c, b) = BotScriptTests.Bot();
        var saved = await h.Run("script", new { op = "run", name = "dig2", code = "expect(mine('dirt', 2))\nreturn 'dug'" });
        Assert.True(saved.Ok, saved.Text);

        foreach (var tier in new ModelTier?[] { ModelTier.Worker, ModelTier.Cheap, null })
        {
            var r = await h.RunAs(tier, "script", new { op = "run", code = "mine('dirt', 1)" });
            Assert.False(r.Ok);
            Assert.Equal(ScriptPrompts.BrainOnly, r.Text);
            Assert.Equal(ScriptPrompts.BrainOnly, (await h.RunAs(tier, "script", new { op = "save", name = "x", code = "return 1" })).Text);
            Assert.Equal(ScriptPrompts.BrainOnly, (await h.RunAs(tier, "script", new { op = "rollback", name = "dig2" })).Text);
        }
        // Economy mode: the brain's turn on the worker model is not the Opus brain either.
        Assert.Equal(ScriptPrompts.BrainOnly, (await h.RunAs(ModelTier.Main, "script", new { code = "return 1" }, model: "claude-sonnet-5-5")).Text);

        var byName = await h.RunAs(ModelTier.Worker, "script", new { op = "run", name = "dig2" });
        Assert.True(byName.Ok, byName.Text);
        Assert.True((await h.RunAs(ModelTier.Worker, "script", new { op = "status" })).Ok);
        Assert.Equal(2, c.CallList.Count(x => x.StartsWith("mine dirt 2")));
        Assert.Null(b.Library.Get("x"));
    }

    [Fact]
    public async Task Opus_code_is_allowed_and_authorship_is_tagged_on_runs_and_versions()
    {
        var (h, _, b) = BotScriptTests.Bot();
        var r = await h.Run("script", new { op = "run", name = "tagged", code = "// purpose: say hi to the log\nlog('hi')\nreturn 'done'" });
        Assert.True(r.Ok, r.Text);
        var s = b.Library.Get("tagged")!.Script!;
        Assert.Equal(Opus, s.Author);
        Assert.Equal(Opus, Assert.Single(s.Versions).Author);
        Assert.Equal("say hi to the log", s.Purpose);
        var run = b.Controller.Runs[^1];
        Assert.Equal(Opus, run.Author);
        Assert.Equal(1, run.Version);

        // A worker running it by name: the run is tagged with the code's author (the brain), not the runner.
        await h.RunAs(ModelTier.Worker, "script", new { op = "run", name = "tagged" });
        Assert.Equal(Opus, b.Controller.Runs[^1].Author);
    }

    [Fact]
    public void The_brain_gets_one_author_line_the_sdk_lives_with_the_author_and_the_worker_gets_one_line()
    {
        var (h, _, _) = BotScriptTests.Bot();
        var brain = h.Runtime.Agent.SystemPrompt[0].Text;
        var worker = h.Runtime.Worker!.SystemPrompt[0].Text;
        Assert.Contains(ScriptPrompts.AuthorLine.Trim(), brain);
        Assert.DoesNotContain("# SDK reference", brain);
        Assert.DoesNotContain(ScriptPrompts.AuthorLine.Trim(), worker);
        Assert.Contains(ScriptPrompts.WorkerLine.Trim(), worker);
        Assert.True(Math.Ceiling(ScriptPrompts.WorkerLine.Trim().Length / 3.5) <= 20, "worker line ≤ 20 tokens");
        Assert.True(Math.Ceiling(ScriptPrompts.AuthorLine.Length / 3.5) <= 40, $"author line ≈{ScriptPrompts.AuthorLine.Length / 3.5:0} tok");
        Assert.StartsWith("You write JavaScript job scripts", ScriptPrompts.AuthorSystem);
        Assert.Contains("# SDK reference", ScriptPrompts.AuthorSystem);
        Assert.Contains("# Examples", ScriptPrompts.AuthorSystem);
        Assert.True(ScriptPrompts.Exemplar.Split('\n').Length <= 8);
    }

    // ------------------------------------------------------------------ helpers (engine level)

    [Fact]
    public async Task Retry_retries_failed_actions_with_backoff()
    {
        using var rig = new Rig();
        int n = 0;
        rig.On["mine"] = (_, _) => Task.FromResult(++n < 2 ? ActResult.Fail("tool broke") : ActResult.Success("mined 1 dirt"));
        var s = await rig.Run("local ok, m = retry(3, function() return mine{'dirt', 1} end) return tostring(ok) .. ' ' .. m");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("true mined 1 dirt", s.Outcome);
        Assert.Equal(2, rig.CallList.Count(x => x.StartsWith("mine")));

        rig.On["mine"] = (_, _) => Task.FromResult(ActResult.Fail("no dirt"));
        var f = await rig.Run("local ok, m = retry(2, function() return mine{'dirt', 1} end) expect(ok, m)");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.Contains("no dirt", f.Reason);
    }

    [Fact]
    public async Task Upto_is_a_bounded_loop_that_stops_early()
    {
        using var rig = new Rig();
        var s = await rig.Run("local n = upto(10, function(i) mine{'dirt', 1} return i < 3 end) return 'passes ' .. n");
        Assert.Contains("passes 3", s.Outcome);
        Assert.Equal(3, rig.CallList.Count(x => x.StartsWith("mine")));
        var all = await rig.Run("return 'passes ' .. upto(4, function() end)");
        Assert.Contains("passes 4", all.Outcome);
    }

    [Fact]
    public async Task Expect_fails_with_the_reason()
    {
        using var rig = new Rig();
        var s = await rig.Run("expect(inv'cobblestone' > 0, 'no cobble to place')\nreturn 'x'");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Equal("no cobble to place", s.Reason);
        Assert.True((await rig.Run("return expect(1 + 1 == 2, 'math') and 'fine'")).Ok);
    }

    [Fact]
    public async Task Room_deposits_when_full_and_fails_clearly_when_it_cannot()
    {
        using var rig = new Rig();
        Assert.True((await rig.Run("room(3) return 'ok'")).Ok);
        Assert.DoesNotContain(rig.CallList, x => x.StartsWith("deposit"));

        for (int slot = 9; slot < 45; slot++) rig.Give(slot, Blocks.Cobblestone, 64);
        rig.On["deposit"] = (_, _) => { for (int slot = 9; slot < 12; slot++) rig.World.Inventory.SetSlot(0, slot, ItemStack.Empty); return Task.FromResult(ActResult.Success("deposited 192")); };
        Assert.True((await rig.Run("room(2) return 'ok'")).Ok);
        Assert.Single(rig.CallList, x => x.StartsWith("deposit"));

        for (int slot = 9; slot < 45; slot++) rig.Give(slot, Blocks.Cobblestone, 64);
        rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("no chest with room"));
        var f = await rig.Run("room(1) return 'ok'");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.StartsWith("room: need 1 free slots, have 0 (deposit: no chest with room)", f.Reason);
    }

    [Fact]
    public async Task Ensure_uses_inventory_then_chests_then_crafting_then_fails_clearly()
    {
        using var rig = new Rig();
        rig.Give(36, Items.StonePickaxe, 1);
        var have = await rig.Run("local ok, got = ensure'pickaxe' return got");
        Assert.Contains("stone_pickaxe", have.Outcome);
        Assert.Empty(rig.CallList);

        rig.World.Inventory.SetSlot(0, 36, ItemStack.Empty);
        rig.On["withdraw"] = (args, _) =>
        {
            if (args.ToString().Contains("iron_pickaxe")) { rig.Give(37, Items.IronPickaxe, 1); return Task.FromResult(ActResult.Success("took 1 iron_pickaxe")); }
            return Task.FromResult(ActResult.Fail("none in chests"));
        };
        rig.Give(38, Items.Bread, 3);
        var fetched = await rig.Run("local ok, got = ensure{'pickaxe', 'food'} return got");
        Assert.True(fetched.Ok, fetched.Outcome);
        Assert.Contains("iron_pickaxe,bread", fetched.Outcome);

        using var rig2 = new Rig();
        rig2.On["withdraw"] = (_, _) => Task.FromResult(ActResult.Fail("none in chests"));
        rig2.On["craft"] = (args, _) =>
        {
            if (args.ToString().Contains("wood_pickaxe")) { rig2.Give(36, Items.WoodenPickaxe, 1); return Task.FromResult(ActResult.Success("crafted 1 wood_pickaxe")); }
            return Task.FromResult(ActResult.Fail("missing cobblestone"));
        };
        var crafted = await rig2.Run("local ok, got = ensure'pickaxe' return got");
        Assert.True(crafted.Ok, crafted.Outcome);
        Assert.Contains("wood_pickaxe", crafted.Outcome);

        using var rig3 = new Rig();
        rig3.On["withdraw"] = (_, _) => Task.FromResult(ActResult.Fail("none"));
        rig3.On["craft"] = (_, _) => Task.FromResult(ActResult.Fail("missing sticks"));
        var none = await rig3.Run("ensure'axe' return 'x'");
        Assert.Equal(ScriptStatus.Failed, none.Status);
        Assert.Equal("ensure: no axe (not in inventory or chests, can't craft)", none.Reason);
    }

    // ------------------------------------------------------------------ lint

    private static readonly string[] Acts = ["mine", "gen", "deposit", "take", "craft", "go", "dig", "place", "chop"];

    [Theory]
    [InlineData("while true do local x = inv'log' end", "busy-loop", 1)]
    [InlineData("local a = 1\nrepeat a = a + 1 until false", "busy-loop", 2)]
    [InlineData("while 1 do log('x') end", "busy-loop", 1)]
    [InlineData("pcall(function() mine{'dirt',1} end)", "removed-global", 1)]
    [InlineData("local f = loadstring('x')", "removed-global", 1)]
    [InlineData("os.execute('rm')", "removed-global", 1)]
    [InlineData("coroutine.yield()", "removed-global", 1)]
    public void Lint_rejects_hard_errors(string code, string rule, int line)
    {
        var r = ScriptLint.Check(code, Acts, language: ScriptLanguage.Lua);
        Assert.True(r.HasErrors);
        var e = Assert.Single(r.Errors);
        Assert.Equal(rule, e.Rule);
        Assert.Equal(line, e.Line);
    }

    [Theory]
    [InlineData("for i = 1, 3 do mine{'dirt', 1} end", "unchecked")]
    [InlineData("upto(5, function() dig{'1,64,1'} end)", "unchecked")]
    [InlineData("for i = 1, 3 do say'hi' wait(1) end", "say-in-loop")]
    [InlineData("mine{'a',1} mine{'b',1} mine{'c',1} dig{'1,1,1'} dig{'1,1,2'} place{'torch','1,1,1'} go{'1,1,1'}", "no-cp")]
    public void Lint_warns(string code, string rule)
    {
        var r = ScriptLint.Check(code, Acts, language: ScriptLanguage.Lua);
        Assert.False(r.HasErrors, r.Format(LintSeverity.Error));
        Assert.Contains(r.Warnings, w => w.Rule == rule);
    }

    [Theory]
    [InlineData("while true do local ok, m = mine{'dirt', 1} expect(ok, m) end")]
    [InlineData("local function step() wait(1) end\nwhile true do step() end")]
    [InlineData("while inv'log' < 32 do local ok, m = chop{} if not ok then fail(m) end end")]
    [InlineData("local s = 'while true do end' -- while true do\nreturn s")]
    [InlineData("for i = 1, 4 do local ok = mine{'dirt', 1} if not ok then break end end")]
    public void Lint_lets_good_code_through(string code)
    {
        var r = ScriptLint.Check(code, Acts, language: ScriptLanguage.Lua);
        Assert.False(r.HasErrors, r.Format(LintSeverity.Error));
        Assert.DoesNotContain(r.Warnings, w => w.Rule == "unchecked");
    }

    [Fact]
    public void Lint_passes_every_author_example_cleanly()
    {
        Assert.Equal(ScriptLanguage.Js, ScriptLanguages.Detect(ScriptPrompts.Exemplar));
        var r = ScriptLint.Check(ScriptPrompts.Exemplar, Acts, language: ScriptLanguage.Js);
        Assert.Empty(r.Findings);
        Assert.StartsWith("mine n cobble at the gen and store it", ScriptLint.Purpose(ScriptPrompts.Exemplar));
        Assert.Equal(["n", "at", "chest"], ScriptLint.ArgNames(ScriptPrompts.Exemplar));
        foreach (var (title, code) in ScriptPrompts.Examples)
            Assert.False(ScriptLint.Check(code, Acts, language: ScriptLanguage.Js).HasErrors, title);
    }

    [Fact]
    public async Task Lint_errors_reject_the_run_and_warnings_ride_on_the_result()
    {
        var (h, c, b) = BotScriptTests.Bot();
        var bad = await h.Run("script", new { op = "run", name = "spin", code = "while (true) { const x = inv('log') }" });
        Assert.False(bad.Ok);
        Assert.StartsWith("lint errors (fix and resubmit): L1 unbounded while loop", bad.Text);
        Assert.Null(b.Library.Get("spin"));
        Assert.Null(b.Controller.Last);

        var warned = await h.Run("script", new { op = "run", code = "for (let i = 1; i <= 2; i++) mine('dirt', 1)\nreturn 'x'" });
        Assert.True(warned.Ok, warned.Text);
        Assert.Contains("lint: L1 mine() result ignored", warned.Text);
        Assert.Equal(2, c.CallList.Count(x => x.StartsWith("mine dirt 1")));
    }

    // ------------------------------------------------------------------ reliability stats, promotion

    [Fact]
    public async Task Promotion_needs_ninety_percent_over_three_runs()
    {
        var (h, _, b) = BotScriptTests.Bot();
        const string code = "// purpose: flaky job\nif (args.boom) fail('boom at 3,64,2')\nexpect(mine('dirt', 1))\nreturn 'ok'";
        await h.Run("script", new { op = "run", name = "flaky", code });
        await h.Run("script", new { op = "run", name = "flaky", args = new { boom = true } });
        await h.Run("script", new { op = "run", name = "flaky" });
        var s = b.Library.Get("flaky")!.Script!;
        var v = Agent.Routines.ScriptLibrary.Active(s);
        Assert.Equal(3, v.Runs);
        Assert.Equal(2, v.Successes);
        Assert.Null(s.PromotedAs); // 67% < 90%
        Assert.Equal("boom at #,#,# x1", Assert.Single(v.TopFailures()));
        Assert.True(v.AvgYield >= 0);

        await h.Run("script", new { op = "run", name = "steady", code = "expect(mine('dirt', 1))\nreturn 'ok'" });
        await h.Run("script", new { op = "run", name = "steady" });
        Assert.Null(b.Library.Get("steady")!.Script!.PromotedAs); // 2 runs: not yet
        await h.Run("script", new { op = "run", name = "steady" });
        Assert.Equal("steady", b.Library.Get("steady")!.Script!.PromotedAs);
        Assert.Contains("flaky v1 (boom) 67%/3", b.Library.Catalog());
    }

    // ------------------------------------------------------------------ versions, duplicates, args, revert, rollback

    [Fact]
    public async Task Save_on_an_existing_name_creates_a_version_and_the_old_one_stays_callable()
    {
        var (h, c, b) = BotScriptTests.Bot();
        var v1 = await h.Run("script", new { op = "save", name = "cobble_loop", code = "// purpose: cobble\nexpect(mine('cobblestone', 2))\nreturn 'v1'" });
        Assert.True(v1.Ok, v1.Text);
        Assert.StartsWith("ok: saved cobble_loop v1", v1.Text); // + the no-ensure lint note (mine cobble needs a pickaxe)
        Assert.Empty(c.CallList); // save doesn't run

        var v2 = await h.Run("script", new { op = "save", name = "cobble_loop", code = "// purpose: cobble\nexpect(mine('cobblestone', args.n ?? 3))\nreturn 'v2'" });
        Assert.StartsWith("ok: saved cobble_loop v2 args(n); v1 stays callable as cobble_loop@v1", v2.Text);
        var s = b.Library.Get("cobble_loop")!.Script!;
        Assert.Equal(2, s.Version);
        Assert.Equal(2, s.Versions.Count);
        Assert.Equal(["n"], s.ArgNames);

        Assert.Contains("v1", (await h.Run("script", new { op = "run", name = "cobble_loop@v1" })).Text);
        Assert.Contains("v2", (await h.Run("script", new { op = "run", name = "cobble_loop", args = new { n = 5 } })).Text);
        Assert.Contains(c.CallList, x => x.StartsWith("mine cobblestone 5"));
        Assert.StartsWith("ok: cobble_loop unchanged (v2)",
            (await h.Run("script", new { op = "save", name = "cobble_loop", code = "// purpose: cobble\nexpect(mine('cobblestone', args.n ?? 3))\nreturn 'v2'" })).Text);
        Assert.Contains("cobble_loop v2 (n)", (await h.Run("script", new { op = "list" })).Text);
    }

    [Fact]
    public async Task Near_duplicate_save_warns_unless_forced()
    {
        var (h, _, b) = BotScriptTests.Bot();
        await h.Run("script", new { op = "save", name = "cobble_loop", code = "// purpose: mine cobble at the gen\nupto(16, () => { expect(mine('cobblestone', 1)) })\nexpect(deposit())" });
        var dup = await h.Run("script", new { op = "save", name = "cobble_loop2", code = "// purpose: mine cobble at the gen\nupto(32, () => { expect(mine('cobblestone', 2)) })\nexpect(deposit())" });
        Assert.False(dup.Ok);
        Assert.Matches(@"^not saved: similar to cobble_loop \(\d\.\d\d\) - update it instead\? \(use op=save name=cobble_loop", dup.Text);
        Assert.Null(b.Library.Get("cobble_loop2"));

        // A named run of a near-duplicate still runs, unsaved, and says so.
        var ran = await h.Run("script", new { op = "run", name = "cobble_loop3", code = "// purpose: mine cobble at the gen\nupto(8, () => { expect(mine('cobblestone', 1)) })\nexpect(deposit())" });
        Assert.Contains("not saved: similar to cobble_loop", ran.Text);
        Assert.Null(b.Library.Get("cobble_loop3"));

        var forced = await h.Run("script", new { op = "save", name = "cobble_loop2", force = "deposits to the far chest", code = "// purpose: mine cobble at the gen\nupto(32, () => { expect(mine('cobblestone', 2)) })\nexpect(deposit())" });
        Assert.True(forced.Ok, forced.Text);
        Assert.Equal("deposits to the far chest", b.Library.Get("cobble_loop2")!.Script!.ForcedReason);

        // Something genuinely different saves silently.
        Assert.True((await h.Run("script", new { op = "save", name = "torches", code = "expect(craft('torch', 4))" })).Ok);
    }

    [Fact]
    public async Task A_worse_new_version_auto_reverts_and_rollback_works()
    {
        var (h, _, b) = BotScriptTests.Bot();
        const string good = "expect(mine('dirt', 1))\nreturn 'good'";
        await h.Run("script", new { op = "save", name = "job", code = good });
        for (int i = 0; i < 3; i++) Assert.True((await h.Run("script", new { op = "run", name = "job" })).Ok);

        await h.Run("script", new { op = "save", name = "job", code = "expect(mine('dirt', 1))\nfail('v2 is broken')\nreturn 'bad'" });
        Assert.Equal(2, b.Library.Get("job")!.Script!.Version);
        for (int i = 0; i < 3; i++) Assert.False((await h.Run("script", new { op = "run", name = "job" })).Ok);

        var s = b.Library.Get("job")!.Script!;
        Assert.Equal(1, s.Version);           // back to the reliable one
        Assert.Equal(good, s.Code);
        Assert.Contains("auto-reverted job v2 (0%/3) -> v1 (100%/3)", s.RevertNote);
        Assert.Equal(2, s.Versions.Count);    // v2 kept for the record
        Assert.Contains(h.Runtime.Events.Snapshot(), e => e.Text.Contains("auto-reverted job"));

        var back = await h.Run("script", new { op = "rollback", name = "job@v2" });
        Assert.True(back.Ok, back.Text);
        Assert.Equal(2, b.Library.Get("job")!.Script!.Version);
        var prev = await h.Run("script", new { op = "rollback", name = "job" });
        Assert.Equal("ok: job v1 active again (was v2; v2 kept)", prev.Text);
        Assert.False((await h.Run("script", new { op = "rollback", name = "job@v9" })).Ok);
    }

    [Fact]
    public async Task Housekeeping_merges_exact_duplicates_and_flags_stale_scripts()
    {
        var (h, _, b) = BotScriptTests.Bot();
        const string code = "expect(mine('dirt', 1))\nreturn 'same'";
        await h.Run("script", new { op = "run", name = "a_job", code });
        await h.Run("script", new { op = "run", name = "a_job" });
        await h.Run("script", new { op = "save", name = "b_job", code = code + " // copy", force = "test copy" });
        await h.Run("script", new { op = "run", name = "b_job" });

        var note = b.Library.Housekeep(14, ScriptLint.ExactKey);
        Assert.Contains("merged duplicates b_job=a_job", note);
        var a = Agent.Routines.ScriptLibrary.Active(b.Library.Get("a_job")!.Script!);
        Assert.Equal(3, a.Runs);
        Assert.Equal("a_job", b.Library.Get("b_job")!.Script!.DuplicateOf);
        Assert.NotNull(b.Library.Get("b_job")); // never deleted

        var start = DateTimeOffset.UtcNow;
        b.Library.Now = () => start.AddDays(15);
        Assert.Contains("flagged for archive (unused 14d): a_job", b.Library.Housekeep(14, ScriptLint.ExactKey));
        Assert.DoesNotContain("a_job", b.Library.Catalog());
        Assert.NotNull(b.Library.Get("a_job"));
        Assert.True((await h.Run("script", new { op = "run", name = "a_job" })).Ok); // still runnable; running un-flags it
        Assert.Null(b.Library.Get("a_job")!.Script!.ArchiveFlagged);
    }
}
