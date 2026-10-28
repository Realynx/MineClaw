using System.Diagnostics;
using MineClaw.Agent.Events;
using MineClaw.Agent.Memory;
using MineClaw.Bot;
using MineClaw.Bot.GameTools;
using MineClaw.Bot.Tests;

namespace MineClaw.Scripting.Tests;

/// <summary>
/// Script standing orders after the Lua → JS migration (live 10-28 08:21) and the body lock: the stored call re-resolves to the
/// script's newest (JS) version by name; a call that can't run wakes the brain with why; a live script holds the order instead
/// of a resume that answers "already running" (live 03:44-03:48, which paused auto-resume for good).
/// </summary>
public class StandingOrderScriptTests
{
    private static (BotHost H, ScriptBridge B) Bot(ScriptOptions? scripts = null)
    {
        var h = BotHost.Create(new FakeGameClient(), TestBot.Options(), new BotOptions { Scripts = scripts ?? new ScriptOptions { MigrateLua = false } },
            new NoLlm(), persist: false);
        return (h, ScriptTools.Of(h.Runtime)!);
    }

    private static async Task WaitFor(Func<bool> cond, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    /// <summary>What the migration leaves behind: the Lua v1 kept as history, the translated JS v2 active.</summary>
    private static void SeedMigrated(ScriptBridge b)
    {
        b.Library.SaveVersion("hello", "-- purpose: say hi\nreturn 'hi from lua'", author: "main/claude-opus-5-5", lang: null);
        b.Library.SaveVersion("hello", "// purpose: say hi\nreturn 'hi from js'", author: "migrate/claude-opus-5-5", lang: "js");
    }

    private static StandingOrder Order(BotHost h, object input) => new()
    {
        Bot = h.Runtime.Agent.Orders!.Bot, Player = "PoofimaFox", Job = "say hi for Fox", Tool = ScriptTools.ToolName, Input = TestBot.J(input),
    };

    [Fact]
    public async Task An_order_pinned_to_the_Lua_version_resumes_on_the_migrated_JS_by_name()
    {
        var (h, b) = Bot();
        SeedMigrated(b);
        var orders = h.Runtime.Agent.Orders!;
        h.Runtime.Memory.SetStandingOrder(Order(h, new { op = "run", name = "hello@v1" }));

        Assert.True(orders.ResumeIfIdle("start"));
        var stored = h.Runtime.Memory.GetStandingOrder(orders.Bot)!;
        Assert.Equal("hello", stored.Input.GetProperty("name").GetString()); // persisted: newest by name from now on
        await WaitFor(() => h.Runtime.Events.Snapshot().Any(e => e.Text.Contains("ok: script hello done (hi from js")));
        Assert.Equal(0, h.Runtime.Memory.GetStandingOrder(orders.Bot)!.FailuresInRow);

        // The bare name (the live order: script gen_loop) runs as stored, on the active JS.
        var check = orders.Prepare!(Order(h, new { op = "run", name = "hello" }));
        Assert.Null(check.Input);
        Assert.Null(check.Problem);
    }

    [Fact]
    public void A_script_order_that_cant_run_any_more_says_why_instead_of_staying_silent()
    {
        var (h, b) = Bot(new ScriptOptions { MigrateLua = false, LegacyLua = false });
        var orders = h.Runtime.Agent.Orders!;
        b.Library.SaveVersion("old_loop", "-- purpose: lua forever\nreturn 'lua'", lang: null);

        // Lua that never migrated (LegacyLua off).
        h.Runtime.Memory.SetStandingOrder(Order(h, new { op = "run", name = "old_loop" }));
        Assert.False(orders.ResumeIfIdle("start"));
        var e = Assert.Single(h.Runtime.Events.Snapshot(), x => x.Text.Contains("can't resume"));
        Assert.Equal(EventPriority.Normal, e.Priority);
        Assert.True(e.MustWake);
        Assert.Contains("old_loop v1 is legacy Lua and wasn't migrated to JS", e.Text);

        // Inline Lua code with no saved script of that name; a script that's gone; a goal (would commission the author each time).
        Assert.Contains("inline legacy Lua", orders.Prepare!(Order(h, new { op = "run", name = "ad_hoc", code = "-- purpose: x\nlocal n=1\nreturn n" })).Problem);
        Assert.Contains("no saved script 'gone_loop'", orders.Prepare!(Order(h, new { op = "run", name = "gone_loop" })).Problem);
        Assert.Contains("commissions a new script", orders.Prepare!(Order(h, new { op = "run", goal = "mine the gen" })).Problem);

        // Inline code next to a saved script's name: the saved script runs.
        SeedMigrated(b);
        var fixedUp = orders.Prepare!(Order(h, new { op = "run", name = "hello", code = "-- purpose: say hi\nreturn 'hi from lua'" }));
        Assert.Null(fixedUp.Problem);
        Assert.False(fixedUp.Input!.Value.TryGetProperty("code", out _));
    }

    [Fact]
    public async Task A_live_script_holds_the_order_and_already_running_never_counts_as_a_failure()
    {
        var (h, b) = Bot();
        var orders = h.Runtime.Agent.Orders!;
        // Live 03:44: gen_loop kept running on the script lane after its activity ended (a do chain took over the activity).
        b.Library.SaveVersion("waiter", "// purpose: wait a while\nwait(30)\nreturn 'done'", lang: "js");
        var (s, err) = b.Controller.Start(new ScriptRequest { Name = "waiter", Code = "// purpose: wait a while\nwait(30)\nreturn 'done'", Language = ScriptLanguage.Js });
        Assert.True(s is { IsLive: true }, err);
        h.Runtime.Memory.SetStandingOrder(Order(h, new { op = "run", name = "waiter" }));

        Assert.False(orders.ResumeIfIdle("idle")); // the body is the script's: nothing re-issued
        Assert.False(h.Runtime.Activities.IsBusy);
        Assert.Equal(0, h.Runtime.Memory.GetStandingOrder(orders.Bot)!.Resumes);

        // Even past the body lock (e.g. another lane), the "already running" answer is not a failed pass.
        orders.BodyBusy = null;
        for (int i = 0; i < 3; i++)
        {
            h.Runtime.Memory.UpdateStandingOrder(orders.Bot, o => o.LastResumed = null);
            Assert.True(orders.ResumeIfIdle("idle"));
            await WaitFor(() => !h.Runtime.Activities.IsBusy && h.Runtime.Memory.GetStandingOrder(orders.Bot)!.LastOutcome?.StartsWith("already running") == true);
            h.Runtime.Memory.UpdateStandingOrder(orders.Bot, o => o.LastOutcome = null);
            h.Runtime.Events.TryTakeBatch();
        }
        Assert.Equal(0, h.Runtime.Memory.GetStandingOrder(orders.Bot)!.FailuresInRow);
        b.Controller.Current?.Stop("test over");
    }
}
