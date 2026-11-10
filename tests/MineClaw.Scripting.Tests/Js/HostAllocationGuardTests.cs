using System.Text.Json.Nodes;
using Jint.Native;
using Jint.Runtime;
using MineClaw.Scripting.Story;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// The memory guard (<see cref="ScriptOptions.MaxSegmentAllocBytes"/>) counts what the script allocates between actions, not the host's
/// work behind an SDK call: a big colony's story.next() made ~100 MB of transient garbage and killed pawns that allocated little.
/// </summary>
public sealed class HostAllocationGuardTests
{
    private const long Mb = 1024 * 1024;

    private static ScriptOptions Small() => new() { MaxSegmentAllocBytes = 8 * Mb, ChatAutoResumeSeconds = 1, MaxPauseMinutes = 1 };

    /// <summary>~<paramref name="bytes"/> of short-lived garbage on the calling (script) thread.</summary>
    private static void Garbage(long bytes)
    {
        for (long n = 0; n < bytes; n += 1024) GC.KeepAlive(new byte[1000]);
    }

    /// <summary>A colony whose every call churns through far more than the limit before it answers (or throws).</summary>
    private sealed class HeavyColony : IStoryPort
    {
        public bool ReportThrows { get; init; }
        public int Calls;
        public JsonObject? Next(string body)
        {
            Calls++;
            Garbage(100 * Mb);
            return new JsonObject { ["id"] = "j" + Calls, ["kind"] = "end" };
        }
        public bool Report(string body, string job, string status, string? note, double? pct)
        {
            Calls++;
            Garbage(100 * Mb);
            if (ReportThrows) throw new JavaScriptException(new JsString("colony busy"));
            return true;
        }
    }

    [Fact]
    public async Task A_host_call_that_allocates_far_more_than_the_limit_does_not_trip_the_memory_guard()
    {
        var port = new HeavyColony();
        using var rig = Rig.Js(Small());
        rig.Host.Story = port;
        var s = await rig.Run("""
            const a = story.next(0);
            let n = 0;
            for (let i = 0; i < 2000; i++) n += i;
            const b = story.next(0);
            for (let i = 0; i < 2000; i++) n += i;
            return { ok: true, msg: a.id + ' ' + b.id + ' ' + n };
            """);
        Assert.True(s.Status == ScriptStatus.Done, s.Outcome);
        Assert.Equal(2, port.Calls);
    }

    [Fact]
    public async Task A_host_call_that_throws_still_leaves_its_allocations_uncounted()
    {
        var port = new HeavyColony { ReportThrows = true };
        using var rig = Rig.Js(Small());
        rig.Host.Story = port;
        var s = await rig.Run("""
            let caught = '';
            try { story.report('j1', 'done'); } catch (e) { caught = String(e); }
            let n = 0;
            for (let i = 0; i < 2000; i++) n += i;
            return { ok: true, msg: caught + ' ' + n };
            """);
        Assert.True(s.Status == ScriptStatus.Done, s.Outcome);
        Assert.Equal(1, port.Calls);
        Assert.Contains("colony busy", s.Outcome);
    }

    [Fact]
    public async Task A_script_that_allocates_over_the_limit_between_actions_still_hits_the_memory_limit()
    {
        var port = new HeavyColony();
        using var rig = Rig.Js(Small());
        rig.Host.Story = port;
        var s = await rig.Run("""
            story.next(0);
            const keep = [];
            for (let i = 0; i < 20000; i++) keep.push('x'.repeat(500) + i);
            return { ok: true, msg: 'built ' + keep.length };
            """);
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("memory limit (8 MB between actions)", s.Outcome);
    }

    [Fact]
    public async Task Copies_made_by_params_are_the_script_s_own_memory_and_still_count()
    {
        using var rig = Rig.Js(Small());
        var s = await rig.Run("""
            const big = { text: 'x'.repeat(200000) };
            const keep = [];
            for (let i = 0; i < 200; i++) keep.push(params(big));
            return { ok: true, msg: 'copied ' + keep.length };
            """);
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("memory limit (8 MB between actions)", s.Outcome);
    }

    [Fact]
    public async Task Lua_a_host_call_that_allocates_far_more_than_the_limit_does_not_trip_the_memory_guard()
    {
        using var rig = new Rig(Small());
        int calls = 0;
        rig.Where = _ => { calls++; Garbage(100 * Mb); return null; };
        var s = await rig.Run("""
            local a = where('iron')
            local n = 0
            for i = 1, 2000 do n = n + i end
            local b = where('iron')
            for i = 1, 2000 do n = n + i end
            return 'done ' .. n
            """);
        Assert.True(s.Status == ScriptStatus.Done, s.Outcome);
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// The sandbox's string.rep/format overrides are host functions too, but what they build is the script's own memory: a loop keeping
    /// their results (no VM instructions run inside them) must still hit the guard.
    /// </summary>
    [Theory]
    [InlineData("for i = 1, 2000 do keep[#keep + 1] = string.rep('x', 10000) end")]
    [InlineData("local base = string.rep('x', 10000) for i = 1, 2000 do keep[#keep + 1] = string.format('%s%d', base, i) end")]
    public async Task Lua_a_script_that_builds_strings_over_the_limit_between_actions_still_hits_the_memory_limit(string build)
    {
        using var rig = new Rig(Small());
        var s = await rig.Run($"""
            local keep = {"{}"}
            {build}
            return 'built ' .. #keep
            """);
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("memory limit (8 MB between actions)", s.Outcome);
    }

    /// <summary>Script code run inside a host call (a timeout/story.during callback) is the script's: it is never credited back.</summary>
    [Fact]
    public async Task Allocations_in_callbacks_run_by_a_host_call_still_count()
    {
        using var rig = Rig.Js(Small());
        var s = await rig.Run("""
            const keep = [];
            for (let k = 0; k < 10; k++)
              timeout(30, () => { for (let i = 0; i < 1000; i++) keep.push('x'.repeat(500) + i + k); });
            return { ok: true, msg: 'built ' + keep.length };
            """);
        Assert.Equal(ScriptStatus.Failed, s.Status);
        Assert.Contains("memory limit (8 MB between actions)", s.Outcome);
    }
}
