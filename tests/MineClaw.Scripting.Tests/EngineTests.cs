using System.Collections.Concurrent;
using MineClaw.Data;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests;

public class SandboxTests
{
    [Theory]
    [InlineData("load(\"go{'escaped'}\")()")]
    [InlineData("loadstring(\"go{'escaped'}\")()")]
    [InlineData("dofile('evil.lua')")]
    [InlineData("loadfile('evil.lua')()")]
    [InlineData("require('os')")]
    [InlineData("package.loadlib('x', 'y')")]
    [InlineData("io.open('secrets.json')")]
    [InlineData("os.execute('ls')")]
    [InlineData("os.exit(1)")]
    [InlineData("os.getenv('ANTHROPIC_API_KEY')")]
    [InlineData("os.remove('x')")]
    [InlineData("debug.sethook()")]
    [InlineData("debug.getinfo(1)")]
    [InlineData("collectgarbage('stop')")]
    [InlineData("string.dump(print)")]
    [InlineData("rawset(me, 'hp', 1)")]
    [InlineData("rawget(inv, 'free')")]
    [InlineData("setmetatable(me, {})")]
    [InlineData("setmetatable(inv, {__call = function() return 99 end})")]
    [InlineData("inv.free = nil")]
    [InlineData("me.hp = 1")]
    [InlineData("os.time = nil")]
    [InlineData("pcall(function() end)")]
    [InlineData("xpcall(function() end, print)")]
    [InlineData("coroutine.create(function() end)")]
    [InlineData("string.rep('x', 1e9)")]
    [InlineData("('x'):rep(1e9)")]
    [InlineData("string.format('%999999999d', 1)")]
    [InlineData("local s = 'x' for i = 1, 40 do s = s .. s end")]
    [InlineData("local t = {} for i = 1, 1e8 do t[i] = i end")]
    public async Task Escapes_and_bombs_fail_without_touching_the_host(string code)
    {
        using var rig = new Rig();
        var s = await rig.Run(code + "\ngo{'escaped'}");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.DoesNotContain(rig.CallList, c => c.Contains("escaped"));
        Assert.StartsWith("failed: script t", s.Outcome);
    }

    [Fact]
    public async Task Only_safe_globals_exist()
    {
        using var rig = new Rig();
        var s = await rig.Run("""
            local names = {"load", "loadstring", "dofile", "loadfile", "require", "package", "io", "debug", "collectgarbage", "rawset",
              "rawget", "pcall", "xpcall", "coroutine", "run_command", "tool", "llm", "ask_player", "goto_"}
            local bad = {}
            for _, k in ipairs(names) do if _G[k] ~= nil then bad[#bad + 1] = k end end
            if string.dump ~= nil or os.execute ~= nil or os.getenv ~= nil then bad[#bad + 1] = "string/os" end
            return "bad:" .. table.concat(bad, ",") .. "."
            """);
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Contains("(bad:.,", s.Outcome);
        var t = await rig.Run("return type(os.time()) .. type(os.clock()), getmetatable(me), getmetatable(inv)", "t2");
        Assert.Contains("numbernumber locked locked", t.Outcome);
    }
}

public class LimitTests
{
    [Theory]
    [InlineData("while true do end")]
    [InlineData("repeat until false")]
    [InlineData("while inv'dirt' < 5 do end")] // queries don't count as actions
    [InlineData("for i = 1, 1e12 do local x = me.hp end")]
    public async Task Busy_loops_are_stopped(string code)
    {
        using var rig = new Rig(new ScriptOptions { MaxInstructionsWithoutAction = 20_000 });
        var s = await rig.Run(code);
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("busy loop", s.Outcome);
    }

    [Fact]
    public async Task Instruction_limit()
    {
        using var rig = new Rig(new ScriptOptions { MaxInstructions = 50_000, MaxActions = 100_000 });
        var s = await rig.Run("for i = 1, 1e9 do if i % 50 == 0 then mine{'dirt', 1} end end");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("instruction limit", s.Outcome);
    }

    [Fact]
    public async Task Wall_time_limit_counts_active_time()
    {
        using var rig = new Rig(new ScriptOptions { MaxMinutes = 0.01 }); // 0.6 s
        var s = await rig.Run("while true do wait(0.1) end");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("time limit", s.Outcome);
    }

    [Fact]
    public async Task Action_limit()
    {
        using var rig = new Rig(new ScriptOptions { MaxActions = 5 });
        var s = await rig.Run("while true do mine{'dirt', 1} end");
        Assert.Contains("action limit (5)", s.Outcome);
        Assert.Equal(5, rig.CallList.Count);
    }

    [Fact]
    public async Task Call_depth_limit_and_syntax_errors()
    {
        using var rig = new Rig();
        var r = await rig.Run("local function f() return f() + 1 end\nf()");
        Assert.Contains("call depth limit", r.Outcome);
        var s = await rig.Run("local a = 1\nlocal x = ", "syn");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("syntax", s.Outcome);
        Assert.Contains("line 2", s.Outcome);
        var e = await rig.Run("local a = 1\nlocal b = nil + 1", "err");
        Assert.StartsWith("failed: script err at line 2 (error: attempt to", e.Outcome);
    }

    [Fact]
    public void Oversized_code_and_double_runs_are_refused()
    {
        using var rig = new Rig(new ScriptOptions { MaxCodeChars = 50 });
        var (_, err) = rig.C.Start(new ScriptRequest { Name = "big", Code = new string('x', 60) });
        Assert.Contains("too long", err);
        rig.Start("wait(2)");
        var (s2, err2) = rig.C.Start(new ScriptRequest { Name = "b", Code = "return 1" });
        Assert.Null(s2);
        Assert.StartsWith("already running", err2);
        rig.C.Cancel();
    }

    [Fact]
    public async Task Budget_veto_stops_scripts()
    {
        using var rig = new Rig();
        rig.Veto = "budget low";
        Assert.Contains("budget low", rig.C.Start(new ScriptRequest { Name = "x", Code = "mine{'dirt',1}" }).Error);
        rig.Veto = null;
        var s = rig.Start("wait(0.2)\nmine{'dirt', 1}");
        await Task.Delay(50);
        rig.Veto = "budget low";
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Cancelled, s.Status);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("mine"));
    }
}

public class BindingTests
{
    [Fact]
    public async Task Action_failure_is_a_result_not_a_throw()
    {
        using var rig = new Rig();
        rig.On["goto"] = (_, _) => Task.FromResult(ActResult.Fail("no route"));
        var s = await rig.Run("local ok, m = go{'home'}\nif ok then return 'bad' end return 'handled ' .. m");
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Contains("handled no route", s.Outcome);

        var f = await rig.Run("mine{'cobblestone', 4}\nif not go{'home'} then fail() end", "trip");
        Assert.Equal(ScriptStatus.Failed, f.Status);
        Assert.StartsWith("failed: script trip at line 2 (goto: no route): 2 acts", f.Outcome);
    }

    [Fact]
    public async Task Table_and_positional_args_map_to_tool_input()
    {
        using var rig = new Rig();
        var s = await rig.Run("""
            go{1, 64, 2} go(3, 4, 5) go{me.pos} go{"home", dist = 2} go"spawn"
            mine{"cobblestone", 16} mine{"dirt", n = 3}
            deposit{items = {"cobblestone"}, at = {x = 1, y = 2, z = 3}}
            dig{"4,5,6"} chop{} chop()
            """);
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Equal([
            """goto {"to":"1,64,2"}""", """goto {"to":"3,4,5"}""", """goto {"to":"0,65,0"}""", """goto {"dist":2,"to":"home"}""", """goto {"to":"spawn"}""",
            """mine {"block":"cobblestone","count":16}""", """mine {"block":"dirt","count":3}""", """deposit {"at":"1,2,3","items":["cobblestone"]}""",
            """dig {"at":"4,5,6"}""", "chop_tree {}", "chop_tree {}",
        ], rig.Norm());
    }

    [Fact]
    public async Task Actions_return_ok_msg_n()
    {
        Assert.Equal(12, ScriptSession.CountOf("mined 12 cobblestone (inv 44)"));
        Assert.Equal(0, ScriptSession.CountOf("arrived at 3,64,-2"));
        using var rig = new Rig();
        rig.On["mine"] = (_, _) => Task.FromResult(ActResult.Success("mined 12 cobblestone (inv 44)"));
        var s = await rig.Run("local ok, m, n = mine{'cobblestone', 12} return tostring(ok) .. '/' .. m .. '/' .. n");
        Assert.Contains("true/mined 12 cobblestone (inv 44)/12", s.Outcome);
    }

    [Fact]
    public async Task Queries_read_the_world()
    {
        using var rig = new Rig();
        rig.Give(36, Items.ByName("cobblestone")!.Id, 5);
        var s = await rig.Run("""
            local n, p = near"stone", players()
            local got = table.concat({inv"cobblestone", tostring(inv.has("cobblestone", 5)), tostring(inv.has("cobblestone", 6)), inv.free(),
              me.hp, me.food, me.pos.y, me.dim, block(0, 64, 0), block{x = 0, y = 70, z = 0}, tostring(n ~= nil and n.d < 3),
              where"iron", tostring(where"gold"), type(time()), tostring(night()), p[1].name .. "+" .. p[2].name, near"home".x}, "|")
            local want = "5|true|false|35|20|20|65|0|stone|air|true|iron: chest 3,64,2 (x12)|nil|number|false|kiwi+robin|5"
            if got == want then return "same" end
            log(got)
            return "diff"
            """);
        Assert.True(s.Outcome!.Contains("(same,"), string.Join(" ", rig.Logs));
    }

    [Fact]
    public async Task Unknown_items_are_script_bugs()
    {
        using var rig = new Rig();
        var s = await rig.Run("local a = 1\nreturn inv'unobtainium'");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("(unknown item 'unobtainium')", s.Outcome); // a metamethod call (inv"x") has no Lua frame for the line
    }

    [Fact]
    public async Task Skills_have_aliases_and_skill_fallback()
    {
        using var rig = new Rig();
        var s = await rig.Run("""
            move{{item = "log", n = "all", to = "wood"}}
            harvest_cane{4} skill("harvest_cane", {radius = 6}) skill("log") log("hi")
            return "ok"
            """);
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Equal([
            """skill:move_items {"moves":[{"count":"all","item":"log","to":"wood"}]}""", """skill:harvest_cane {"radius":4}""",
            """skill:harvest_cane {"radius":6}""", "skill:log {}",
        ], rig.Norm());
        Assert.Contains("script t: hi", rig.Logs);
    }

    [Fact]
    public async Task No_server_commands_from_scripts()
    {
        using var rig = new Rig(new ScriptOptions { MaxSayPerRun = 10, SayMinIntervalSeconds = 0 });
        var s = await rig.Run("say'/op maid_chan' say'hi /kill @a' emote'/gamemode 1 waves' ask('kiwi', '/stop now?', 5)");
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Equal(["op maid_chan", "hi kill @a", "kiwi: stop now?"], rig.Said);
        Assert.Equal(["gamemode 1 waves"], rig.Emoted);
        Assert.All(rig.Said.Concat(rig.Emoted), t => Assert.DoesNotMatch(@"(^|\s)/", t));
    }

    [Fact]
    public async Task Chat_is_rate_limited_and_ask_returns_the_reply()
    {
        using var rig = new Rig(new ScriptOptions { MaxSayPerRun = 10, SayMinIntervalSeconds = 0 });
        var s = await rig.Run("local ok = 0 for i = 1, 8 do if say('hi ' .. i) then ok = ok + 1 end end\nreturn ok .. ' ' .. ask('kiwi', 'which chest?', 10)");
        Assert.Contains("(6 yes,", s.Outcome);
    }

    [Fact]
    public async Task Outcome_reports_gains()
    {
        using var rig = new Rig();
        int cobble = Items.ByName("cobblestone")!.Id;
        rig.On["mine"] = (_, _) => { rig.Give(36, cobble, 64); return Task.FromResult(ActResult.Success("mined 64 cobblestone")); };
        var s = await rig.Run("prog(50, 'half') mine{'cobblestone', 64}", "cobble_loop");
        Assert.Matches(@"^ok: script cobble_loop done \(\+64 cobblestone, \d+s\)$", s.Outcome);
    }

    [Fact]
    public async Task Wake_asks_for_the_brain_and_is_rate_limited()
    {
        using var rig = new Rig();
        var s = await rig.Run("local a = wake'no saplings left' local b = wake'again' return tostring(a) .. tostring(b)");
        Assert.Contains("truefalse", s.Outcome);
        Assert.Equal(["no saplings left"], rig.Wakes);
    }

    [Fact]
    public async Task Ask_brain_is_needs_input_and_resume_answers_it()
    {
        using var rig = new Rig();
        var s = rig.Start("local a = ask('brain', 'which chest?')\nreturn 'got ' .. a");
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Script, s.PausedFor);
        Assert.Contains("needs_input: which chest?", s.State());
        s.Resume("chest 2");
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("got chest 2", s.Outcome);
    }
}

public class TypicalScriptTests
{
    public static TheoryData<string, string> Scripts => new()
    {
        { "chop32", """
            while inv"log" < 32 do local ok, m = chop{} if not ok then say(m) break end end
            move{{item = "log", n = "all", to = "storage:wood"}}
            """ },
        { "cobble", """
            for i = 1, 4 do mine{"cobblestone", 16} prog(i * 25) end
            deposit{}
            """ },
        { "iron", """
            take{"iron_ore", 8} smelt{"iron_ore", 8, "coal"} wait(0.1)
            craft{"iron_pickaxe"}
            """ },
        { "cane", """
            on("danger", function() eat{} end)
            for i = 1, 3 do skill("harvest_cane", {}) wait(0.05) end
            """ },
        { "saplings", """
            if inv"sapling" == 0 then wake"no saplings left" return "waiting" end
            place{"sapling", me.x + 2, me.y, me.z}
            """ },
        { "guarded", """
            cp"start"
            local ok, m = go{"cobble gen"}
            if not ok then fail(m) end
            for i = 1, 3 do mine{"cobblestone", 8} end
            cp"stored" deposit{}
            """ },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Typical_jobs_fit_in_8_lines_and_run(string name, string code)
    {
        Assert.True(code.Split('\n').Length <= 8, $"{name} is {code.Split('\n').Length} lines");
        using var rig = new Rig();
        int log = Items.ByName("log")!.Id;
        int slot = 9;
        rig.On["chop_tree"] = (_, _) => { rig.Give(slot++, log, 8); return Task.FromResult(ActResult.Success("chopped 8 log")); };
        var s = await rig.Run(code, name);
        Assert.True(s.Status == ScriptStatus.Done, s.Outcome);
        if (name == "chop32")
        {
            Assert.Equal(4, rig.CallList.Count(c => c.StartsWith("chop_tree")));
            Assert.Contains("""skill:move_items {"moves":[{"count":"all","item":"log","to":"storage:wood"}]}""", rig.Norm());
            Assert.StartsWith("ok: script chop32 done (+32 oak wood", s.Outcome);
        }
    }
}

public class InterruptTests
{
    [Fact]
    public async Task Pause_waits_for_the_click_sequence_then_stops_between_actions()
    {
        using var rig = new Rig();
        var clicks = new ConcurrentQueue<int>();
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        bool cancelled = false;
        rig.On["deposit"] = async (_, ct) =>
        {
            for (int i = 0; i < 5; i++)
            {
                clicks.Enqueue(i);
                if (i == 1) { inside.TrySetResult(); await release.Task; }
                cancelled |= ct.IsCancellationRequested;
            }
            return ActResult.Success("deposited 5 stacks");
        };
        var s = rig.Start("deposit{}\nmine{'dirt', 1}\nreturn 'done'");
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "hp 5/20"));
        rig.C.Pause();
        await Task.Delay(100);
        Assert.Equal(ScriptStatus.Running, s.Status); // still inside the atomic action
        release.SetResult();
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Paused, s.Status);
        Assert.Equal([0, 1, 2, 3, 4], clicks);
        Assert.False(cancelled);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("mine"));
        s.Resume();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Single(rig.CallList, c => c.StartsWith("mine"));
    }

    [Fact]
    public async Task Cancel_also_lets_the_atomic_action_finish()
    {
        using var rig = new Rig();
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int clicks = 0;
        rig.On["craft"] = async (_, _) =>
        {
            Interlocked.Increment(ref clicks);
            inside.TrySetResult();
            await release.Task;
            Interlocked.Increment(ref clicks);
            return ActResult.Success("crafted 4 planks");
        };
        var s = rig.Start("craft{'planks', 4}\nmine{'dirt', 1}");
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.C.Cancel("stop");
        release.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, clicks);
        Assert.Equal(ScriptStatus.Cancelled, s.Status);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("mine"));
        Assert.StartsWith("cancelled: script t at line 1 (stop)", s.Outcome);
    }

    [Fact]
    public async Task Danger_aborts_the_walk_pauses_with_state_and_reruns_it_on_resume()
    {
        using var rig = new Rig();
        int gotos = 0;
        var release = new TaskCompletionSource();
        var started = new SemaphoreSlim(0);
        rig.On["goto"] = Rig.Blocking(release, () => { Interlocked.Increment(ref gotos); started.Release(); });
        string? notice = null;
        rig.C.Paused += p => notice = p.State();
        var s = rig.Start("cp'walk'\nn, who = 3, 'kiwi'\nprog(40, 'walking')\ngo{'home'}\nreturn 'arrived'", "fetch");
        Assert.True(await started.WaitAsync(5000));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "[DANGER] hp 4/20 skeleton"));
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Paused, s.Status);
        Assert.Equal(PauseKind.Danger, s.PausedFor);
        Assert.NotNull(notice);
        foreach (var part in new[] { "script fetch paused (danger: [DANGER] hp 4/20", "L4", "`go{'home'}`", "cp 'walk' 1/1", "40% walking", "n=3", "who='kiwi'", "1 acts" })
            Assert.Contains(part, notice);
        Assert.True(notice!.Length <= 400);
        release.SetResult();
        s.Resume();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Done, s.Status);
        Assert.Equal(2, gotos); // aborted through its own cancellation, re-run after resume
        Assert.Contains("arrived", s.Outcome);
    }

    [Fact]
    public async Task Stop_from_a_trusted_player_is_a_hard_pause()
    {
        using var rig = new Rig(new ScriptOptions { ChatAutoResumeSeconds = 1, PauseOnChat = true }); // opt-in: chat no longer pauses by default
        var s = rig.Start("while true do wait(0.05) end");
        s.Deliver(new ScriptEvent(ScriptEventKind.Stop, "stop!", "kiwi"));
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Stop, s.PausedFor);
        await Task.Delay(1500);
        Assert.Equal(ScriptStatus.Paused, s.Status); // no auto-resume
        rig.C.Cancel();
    }

    [Fact]
    public async Task Danger_handler_runs_first_and_the_script_continues()
    {
        using var rig = new Rig();
        int gotos = 0;
        var release = new TaskCompletionSource();
        rig.On["goto"] = Rig.Blocking(release, () => Interlocked.Increment(ref gotos));
        bool paused = false;
        rig.C.Paused += _ => paused = true;
        var s = rig.Start("hits = 0\non('danger', function(e) hits = hits + 1 carry.last = e.text eat{} end)\ngo{'home'}\nreturn 'h' .. hits .. ' ' .. carry.last");
        await Rig.Until(() => gotos == 1);
        var ev = new ScriptEvent(ScriptEventKind.Danger, "hp 6/20");
        Assert.True(s.HasHandler(ev));
        s.Deliver(ev);
        await Rig.Until(() => gotos == 2);
        release.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(paused);
        Assert.Contains("h1 hp 6/20", s.Outcome);
        Assert.Contains(rig.CallList, c => c.StartsWith("eat"));
        Assert.Empty(rig.Wakes);
    }

    [Fact]
    public async Task Chat_handlers_take_matching_chat_and_other_chat_pauses_with_auto_resume()
    {
        using var rig = new Rig(new ScriptOptions { ChatAutoResumeSeconds = 1, PauseOnChat = true }); // opt-in: chat no longer pauses by default
        var resumed = new TaskCompletionSource<bool>();
        rig.C.Resumed += (_, auto) => resumed.TrySetResult(auto);
        var s = rig.Start("on('chat', {from = 'kiwi'}, function(e) carry.said = e.text end)\nwait(0.4)\nwait(0.4)\nwait(0.2)\nreturn carry.said");
        await Task.Delay(50);
        var mine = new ScriptEvent(ScriptEventKind.Chat, "grab 32 logs", "KIWI");
        Assert.True(s.HasHandler(mine));
        s.Deliver(mine);
        await Task.Delay(20);
        Assert.Equal(ScriptStatus.Running, s.Status);
        var other = new ScriptEvent(ScriptEventKind.Chat, "hey maid", "robin");
        Assert.False(s.HasHandler(other));
        s.Deliver(other);
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Chat, s.PausedFor);
        Assert.True(await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("grab 32 logs", s.Outcome);
    }

    [Fact]
    public async Task Waitfor_chat_consumes_the_line()
    {
        using var rig = new Rig();
        var s = rig.Start("local ok, m = waitfor('chat', {from = 'kiwi', text = 'go'}, 5)\nreturn tostring(ok) .. ':' .. m");
        await Rig.Until(() => s.Snippet?.Contains("waitfor") == true);
        var ev = new ScriptEvent(ScriptEventKind.Chat, "ok go now", "kiwi");
        Assert.True(s.HasHandler(ev)); // the script is listening: the brain stays asleep
        s.Deliver(ev);
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("true:ok go now", s.Outcome);
        var t = await rig.Run("local ok, m = waitfor('item', {item = 'cobblestone', n = 99}, 0.3) return m", "w2");
        Assert.Contains("timeout", t.Outcome);
    }
}

public class RescriptTests
{
    [Fact]
    public async Task Replace_stops_at_a_safe_point_and_hands_over_carry()
    {
        using var rig = new Rig();
        var s1 = rig.Start("while true do carry.n = (carry.n or 0) + 1 wait(0.05) end", "loop");
        await Rig.Until(() => s1.Actions >= 3);
        var (s2, err) = await rig.C.ReplaceAsync(new ScriptRequest { Name = "loop2", Code = "return 'n=' .. carry.n" });
        Assert.Null(err);
        await s2!.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ScriptStatus.Cancelled, s1.Status);
        Assert.Equal("replaced", s1.Reason);
        var n = int.Parse(System.Text.RegularExpressions.Regex.Match(s2.Outcome!, @"n=(\d+)").Groups[1].Value);
        Assert.True(n >= 3, s2.Outcome);
    }

    [Fact]
    public async Task Resume_from_a_checkpoint_skips_finished_stages_and_restores_carry()
    {
        using var rig = new Rig();
        bool broken = true;
        rig.On["goto"] = (_, _) => Task.FromResult(broken ? ActResult.Fail("no route") : ActResult.Success("arrived"));
        const string code = """
            cp("chop", function() mine{"log", 4} carry.logs = 4 end)
            cp("store", function() if not go{"chest"} then fail() end deposit{} end)
            return "stored " .. carry.logs
            """;
        var r1 = await rig.Run(code, "job");
        Assert.Equal(ScriptStatus.Failed, r1.Status);
        Assert.StartsWith("failed: script job at line 2 (goto: no route): cp 2/2", r1.Outcome);
        broken = false;
        var r2 = await rig.Run(code, "job", resumeFrom: "store");
        Assert.Equal(ScriptStatus.Done, r2.Status);
        Assert.Contains("stored 4", r2.Outcome);
        Assert.Single(rig.CallList, c => c.StartsWith("mine")); // stage 'chop' not run again

        // Marker form: actions before the checkpoint are skipped.
        var r3 = await rig.Run("mine{'dirt', 1}\ncp'here'\ngo{'x'}\nreturn 'ok'", "marker", resumeFrom: "here");
        Assert.Equal(ScriptStatus.Done, r3.Status);
        Assert.DoesNotContain(rig.CallList, c => c.Contains("dirt"));
        Assert.Contains("no checkpoint('nope')", rig.C.Start(new ScriptRequest { Name = "m", Code = "return 1", ResumeFrom = "nope" }).Error);
    }

    [Fact]
    public async Task Args_reach_the_script()
    {
        using var rig = new Rig();
        var s = await rig.Run("mine{'cobblestone', args.n} return args.n * 2", args: """{"n":7}""");
        Assert.Contains("(14,", s.Outcome);
        Assert.Contains("""mine {"block":"cobblestone","count":7}""", rig.CallList);
    }

    [Fact]
    public async Task Telemetry_records_actions_and_duration()
    {
        using var rig = new Rig();
        await rig.Run("mine{'dirt', 1} mine{'dirt', 1} wait(0.1)", "tele");
        var rec = Assert.Single(rig.C.Runs);
        Assert.Equal("tele", rec.Name);
        Assert.Equal(3, rec.Actions);
        Assert.True(rec.Duration >= TimeSpan.FromMilliseconds(90));
    }
}
