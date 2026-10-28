using MineClaw.Data;
using MineClaw.Scripting;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>machine(): transitions, guards, events, timers, conditions, history and persistence across a restart.</summary>
public class MachineTests
{
    [Fact]
    public async Task Tick_targets_enter_exit_order_history_and_final_states()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("""
            const order = []
            const bb = { n: 0 }
            const end = machine({
              initial: 'a', bb,
              states: {
                a: { enter: () => { order.push('enter a') }, tick: b => (++b.n >= 3 ? 'b' : undefined), exit: () => { order.push('exit a') } },
                b: { enter: (b, m) => { order.push('enter b from ' + m.history[m.history.length - 1].from); return 'c' } },
                c: { final: true, enter: () => { order.push('enter c') } },
              },
            })
            return end + ' ' + bb.n + ' ' + order.join('|')
            """);
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("c 3 enter a|exit a|enter b from a|enter c", s.Outcome);
        Assert.Equal(2, s.Transitions);
        Assert.Equal("c", s.MachineState);
    }

    [Fact]
    public async Task An_event_cuts_the_running_action_and_moves_the_machine()
    {
        using var rig = Rig.Js();
        var release = new TaskCompletionSource();
        int gotos = 0;
        rig.On["goto"] = (args, ct) => args.GetProperty("to").GetString() == "far"
            ? Rig.Blocking(release, () => Interlocked.Increment(ref gotos))(args, ct) : Task.FromResult(ActResult.Success("at home"));
        bool paused = false;
        rig.C.Paused += _ => paused = true;
        var s = rig.Start("""
            return machine({
              initial: 'walk',
              states: {
                walk: { tick: () => { go('far') }, on: { danger: 'flee' } },
                flee: { enter: () => { go('home'); return 'done' } },
              },
            })
            """);
        await Rig.Until(() => gotos == 1);
        var ev = new ScriptEvent(ScriptEventKind.Danger, "hp 5/20 zombie", Topic: "lowhp");
        Assert.True(s.HasHandler(ev)); // the machine takes it: the brain stays asleep
        s.Deliver(ev);
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.Ok, s.Outcome);
        Assert.False(paused);
        Assert.Equal(1, gotos); // the walk was abandoned, not re-run
        Assert.Contains("""goto {"to":"home"}""", rig.Norm());
        Assert.Contains("done", s.Outcome);
    }

    [Fact]
    public async Task Guards_named_and_inline_decide_and_an_untaken_danger_still_pauses()
    {
        using var rig = Rig.Js(new ScriptOptions { MaxPauseMinutes = 0.05 });
        var s = rig.Start("""
            const bb = { brave: true, seen: 0 }
            return machine({
              initial: 'idle', bb,
              guards: { scared: b => !b.brave },
              states: {
                idle: { on: {
                  danger: { to: 'flee', guard: 'scared' },
                  'chat:count': { to: 'idle', if: (b) => { b.seen++; return false } },
                  'chat:quit': 'done',
                } },
                flee: { final: true },
              },
            })
            """);
        await Rig.Until(() => s.MachineState == "idle");
        s.Deliver(new ScriptEvent(ScriptEventKind.Chat, "count me", "kiwi"));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "hp 4/20"));
        await s.WhenSettled().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseKind.Danger, s.PausedFor); // the guard said no: default danger policy
        s.Resume();
        s.Deliver(new ScriptEvent(ScriptEventKind.Chat, "ok quit now", "kiwi"));
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal("done", s.MachineState);
    }

    [Fact]
    public async Task Timers_after_and_every_and_when_conditions()
    {
        using var rig = Rig.Js();
        int cobble = Items.ByName("cobblestone")!.Id;
        rig.On["mine"] = (_, _) => { rig.Give(12, cobble, 1); return Task.FromResult(ActResult.Success("mined 1 cobblestone")); };
        var s = await rig.Run("""
            const bb = { ticks: 0 }
            return machine({
              initial: 'wait', bb,
              states: {
                wait: { on: { [after(300)]: 'count' } },
                count: { on: { [every(2)]: (ev, b) => { b.ticks++ }, [when(() => bb.ticks >= 3)]: 'mine' } },
                mine: { tick: () => { mine('cobblestone', 1) }, on: { [when(() => inv('cobblestone') > 0)]: 'done' } },
              },
            }) + ' ' + bb.ticks
            """);
        Assert.True(s.Ok, s.Outcome + " " + string.Join(";", rig.Logs));
        Assert.Matches(@"done [3-5],", s.Outcome); // every(2) ticks until the when() condition (≥ 3) is seen
        _ = cobble;
    }

    [Fact]
    public async Task Builtin_signals_drive_transitions()
    {
        using var rig = Rig.Js();
        var s = rig.Start("""
            return machine({
              initial: 'work', thresholds: { hungry: 10 },
              states: {
                work: { tick: () => { wait(0.05) }, on: { hungry: 'eat', night: 'sleep' } },
                eat: { enter: () => { eat(); return 'work' } },
                sleep: { final: true },
              },
            })
            """);
        await Rig.Until(() => s.MachineState == "work");
        rig.World.Player.SetHealth(20, 12, 0); // not below 10: nothing
        await Task.Delay(300);
        Assert.DoesNotContain(rig.CallList, c => c.StartsWith("eat"));
        rig.World.Player.SetHealth(20, 8, 0);
        await Rig.Until(() => rig.CallList.Any(c => c.StartsWith("eat")));
        rig.World.Time = 15000;
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("sleep", s.MachineState);
        Assert.Contains("work -> eat (on hungry)", string.Join("\n", rig.Logs));
    }

    [Fact]
    public async Task State_and_blackboard_are_checkpointed_and_a_restart_resumes_there()
    {
        var store = new MemoryCheckpoints();
        const string code = """
            const bb = { mined: 0 }
            return machine({
              initial: 'mine', bb,
              states: {
                mine: { tick: b => { expect(mine('cobblestone', 1)); b.mined++; if (b.mined >= 2) return 'store' } },
                store: { tick: b => { expect(deposit()); return 'done' } },
              },
            }) + ' after ' + bb.mined
            """;
        string? snapshot = null;
        using (var rig = Rig.Js(checkpoints: store))
        {
            rig.On["deposit"] = (_, _) => Task.FromResult(ActResult.Fail("no chest"));
            var r1 = await rig.Run(code, "miner");
            Assert.Equal(ScriptStatus.Failed, r1.Status);
            Assert.Equal("@m", r1.Checkpoint);
            snapshot = store.Load("miner", "maid_chan", "@m");
        }
        Assert.Contains("\"state\":\"store\"", snapshot);
        Assert.Contains("\"mined\":2", snapshot);
        using var fresh = Rig.Js(checkpoints: store); // a new process (restart): resume_from = the machine label
        var r2 = await fresh.Run(code, "miner", resumeFrom: "@m");
        Assert.True(r2.Ok, r2.Outcome);
        Assert.Contains("done after 2", r2.Outcome);
        Assert.DoesNotContain(fresh.CallList, c => c.StartsWith("mine")); // resumed in 'store', not from the start
        Assert.Single(fresh.CallList, c => c.StartsWith("deposit"));
    }

    [Fact]
    public async Task Transitions_requested_while_exiting_are_deferred()
    {
        using var rig = Rig.Js();
        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.On["deposit"] = async (_, ct) => { inside.TrySetResult(); await release.Task.WaitAsync(ct); return ActResult.Success("deposited"); };
        var s = rig.Start("""
            return machine({
              initial: 'a',
              states: {
                a: { tick: () => 'b', exit: () => { deposit() }, on: { danger: 'c' } },
                b: { final: true }, c: { final: true },
              },
            })
            """);
        await inside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        s.Deliver(new ScriptEvent(ScriptEventKind.Danger, "hp 5/20"));
        await Task.Delay(100);
        release.SetResult();
        await s.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.Ok, s.Outcome);
        Assert.Equal("c", s.MachineState); // a's exit finished first, then the danger's target won
    }

    [Fact]
    public async Task Unknown_states_fail_clearly()
    {
        using var rig = Rig.Js();
        var s = await rig.Run("machine({ initial: 'a', states: { a: { tick: () => 'nowhere' } } })");
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("no state 'nowhere'", s.Outcome);
        var t = await rig.Run("machine({ initial: 'x', states: { a: {} } })", "t2");
        Assert.Contains("initial state 'x' isn't in states", t.Outcome);
    }
}
