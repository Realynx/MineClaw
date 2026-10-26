using System.Text.Json;
using MineClaw.Agent.Human;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Routines;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

public class MemoryTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void RoundTrip_AllKinds()
    {
        using var dir = new TempDir();
        var path = dir.File("memory.json");
        var a = new MemoryStore(path);
        a.SetWaypoint("spawn", 0, 64, 0, "island center");
        a.UpsertStructure("gen1", s =>
        {
            s.Kind = StructureKinds.CobbleGenerator;
            s.Min = new BlockPos(3, 64, -2);
            s.KeyBlocks.Add(new KeyBlock { Label = "mine_here", X = 4, Y = 65, Z = -2 });
            s.Steps.Add("stand at 3,65,-2");
            s.TaughtBy = "Alice";
            s.Confidence = 0.9;
        });
        a.AddNote("Alice", "likes birch");
        a.SaveRoutine(new Routine { Name = "cobble_loop", Repeat = 0, StopWhen = "full", Steps = [new RoutineStep { Tool = "mine", Args = J(new { block = "cobblestone" }) }] });

        var b = new MemoryStore(path);
        Assert.Equal(64, b.GetWaypoint("SPAWN")!.Y);
        var s = b.GetStructure("gen1")!;
        Assert.Equal((StructureKinds.CobbleGenerator, 4, "Alice", 0.9), (s.Kind, s.KeyBlocks[0].X, s.TaughtBy, s.Confidence));
        Assert.Equal("cobblestone", b.GetRoutine("cobble_loop")!.Steps[0].Args.GetProperty("block").GetString());
        Assert.Contains("likes birch", b.Recall("alice"));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp")); // atomic write leaves no temp files
    }

    [Fact]
    public void Recall_IsBounded_AndRanked()
    {
        var m = new MemoryStore(null);
        for (int i = 0; i < 50; i++) m.AddNote("world", $"fact number {i} about trees and stuff that goes on for a while");
        m.SetWaypoint("tree farm", 10, 64, 10);
        var r = m.Recall("tree farm", maxHits: 5, maxChars: 300);
        Assert.True(r.Length <= 300);
        Assert.StartsWith("wp tree farm", r);
        Assert.Equal("no memories match", m.Recall("zzzz"));
    }

    [Fact]
    public async Task Tools_RememberRecallForgetList()
    {
        var reg = new ToolRegistry();
        var mem = new MemoryStore(null);
        MemoryTools.RegisterAll(reg, mem, () => new BlockPos(1, 2, 3));

        Assert.True((await reg.ExecuteAsync("remember_waypoint", J(new { name = "home" }), default)).Ok);
        Assert.Equal(3, mem.GetWaypoint("home")!.Z);
        var mark = await reg.ExecuteAsync("mark_structure", J(new { name = "gen", kind = "cobble_generator", x1 = 0, y1 = 64, z1 = 0, x2 = 2, y2 = 65, z2 = -1, keys = new[] { "lava@0,65,0" } }), default);
        Assert.True(mark.Ok, mark.Text);
        Assert.Equal(new BlockPos(0, 64, -1), mem.GetStructure("gen")!.Min);
        await reg.ExecuteAsync("describe_structure", J(new { name = "gen", steps = new[] { "mine the cobble at 1,64,0", "wait 1s" }, taught_by = "Bob" }), default);
        Assert.Contains("1) mine the cobble", (await reg.ExecuteAsync("recall", J(new { query = "gen" }), default)).Text);
        Assert.Contains("gen(cobble_generator)", (await reg.ExecuteAsync("list_memory", J(new { }), default)).Text);
        Assert.True((await reg.ExecuteAsync("forget", J(new { name = "gen" }), default)).Ok);
        Assert.Null(mem.GetStructure("gen"));
        var bad = await reg.ExecuteAsync("describe_structure", J(new { name = "x" }), default);
        Assert.False(bad.Ok);
    }

    [Fact]
    public void DemonstrationSummary_IsCompact()
    {
        var t = DateTimeOffset.UtcNow;
        var actions = new List<DemoAction>
        {
            new(t, DemoActionKind.Move, 5, 64, 5), new(t, DemoActionKind.Move, 4, 64, 3),
            new(t, DemoActionKind.BlockBroken, 4, 65, 2, "cobblestone"),
            new(t, DemoActionKind.BlockBroken, 4, 65, 2, "cobblestone"),
            new(t, DemoActionKind.BlockBroken, 4, 65, 2, "cobblestone"),
            new(t, DemoActionKind.UseBlock, 6, 64, 2, "chest"),
            new(t, DemoActionKind.ClickSlot, 0, 0, 0, Detail: "put 3 cobblestone in chest"),
        };
        var steps = DemonstrationSummarizer.Summarize(actions, new BlockPos(3, 64, 2));
        Assert.Equal(["stand at +1,+0,+1", "break cobblestone at +1,+1,+0 (x3)", "use chest at +3,+0,+0", "put 3 cobblestone in chest"], steps);
    }
}

public class RoutineTests
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    private static (ToolRegistry Reg, List<string> Log) Registry(Func<int, ToolResult>? mineResult = null)
    {
        var reg = new ToolRegistry();
        var log = new List<string>();
        int n = 0;
        reg.Register("mine", "m", ToolSchema.Empty, (i, _) =>
        {
            n++;
            log.Add("mine " + i.Str("block"));
            return Task.FromResult(mineResult?.Invoke(n) ?? ToolResult.Success($"mined 1 (inv {n})"));
        });
        reg.Register("wait", "w", ToolSchema.Empty, (_, _) => { log.Add("wait"); return Task.FromResult(ToolResult.Success("waited")); });
        return (reg, log);
    }

    private static Routine Loop(int repeat, string? stopWhen = null) => new()
    {
        Name = "cobble_loop",
        Repeat = repeat,
        StopWhen = stopWhen,
        Steps = [new() { Tool = "mine", Args = J(new { block = "cobblestone" }) }, new() { Tool = "wait", Args = J(new { }) }],
    };

    [Fact]
    public async Task RunsAllIterations()
    {
        var (reg, log) = Registry();
        var r = await new RoutineExecutor(reg).RunAsync(Loop(3), null, default);
        Assert.True(r.Ok);
        Assert.Equal(6, log.Count);
        Assert.Equal("mine cobblestone", log[0]);
        Assert.Contains("3 iteration(s) ok", r.Text);
    }

    [Fact]
    public async Task StopsOnFirstError()
    {
        var (reg, log) = Registry(n => n == 2 ? ToolResult.Error("no pickaxe") : ToolResult.Success("ok"));
        var r = await new RoutineExecutor(reg).RunAsync(Loop(10), null, default);
        Assert.False(r.Ok);
        Assert.Contains("iter 2 step 1 (mine): no pickaxe", r.Text);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public async Task StopCondition_EndsSuccessfully()
    {
        var (reg, _) = Registry(n => ToolResult.Success(n >= 4 ? "inventory full" : "mined"));
        var r = await new RoutineExecutor(reg).RunAsync(Loop(0, "inventory full"), null, default);
        Assert.True(r.Ok);
        Assert.Contains("stop condition", r.Text);
        Assert.Contains("iter 4", r.Text);
    }

    [Fact]
    public async Task Cancellation_Stops()
    {
        var reg = new ToolRegistry();
        using var cts = new CancellationTokenSource();
        int n = 0;
        reg.Register("tick", "t", ToolSchema.Empty, (_, _) => { if (++n == 3) cts.Cancel(); return Task.FromResult(ToolResult.Success("t")); });
        var routine = new Routine { Name = "r", Repeat = 0, Steps = [new() { Tool = "tick", Args = J(new { }) }] };
        var r = await new RoutineExecutor(reg).RunAsync(routine, null, cts.Token);
        Assert.False(r.Ok);
        Assert.Contains("cancelled", r.Text);
        Assert.Equal(3, n);
    }

    [Fact]
    public async Task SaveAndRunTools_ValidateSteps()
    {
        var (reg, log) = Registry();
        var mem = new MemoryStore(null);
        var exec = new RoutineExecutor(reg);
        RoutineTools.RegisterAll(reg, mem, exec);

        var bad = await reg.ExecuteAsync("save_routine", J(new { name = "x", steps = new[] { new { tool = "run_routine" } } }), default);
        Assert.False(bad.Ok);
        var unknown = await reg.ExecuteAsync("save_routine", J(new { name = "x", steps = new[] { new { tool = "fly" } } }), default);
        Assert.False(unknown.Ok);

        var ok = await reg.ExecuteAsync("save_routine", J(new
        {
            name = "loop",
            steps = new object[] { new { tool = "mine", args = new { block = "cobblestone" } }, new { tool = "wait" } },
            repeat = 2,
        }), default);
        Assert.True(ok.Ok, ok.Text);
        Assert.True(reg.TryGet("run_routine", out var run) && run.IsLongRunning);
        var r = await reg.ExecuteAsync("run_routine", J(new { name = "loop" }), default);
        Assert.True(r.Ok, r.Text);
        Assert.Equal(4, log.Count);
    }
}

public class ToolRegistryTests
{
    [Fact]
    public async Task Errors_Unknown_AndTruncation()
    {
        var reg = new ToolRegistry(maxResultLength: 20);
        reg.Register("boom", "b", ToolSchema.Empty, (_, _) => throw new InvalidOperationException("kaboom"));
        reg.Register("long", "l", ToolSchema.Empty, (_, _) => Task.FromResult(ToolResult.Success(new string('a', 100))));
        reg.Register("needs", "n", ToolSchema.Create().Str("x").Build(), (i, _) => Task.FromResult(ToolResult.Success(i.ReqStr("x"))));

        var boom = await reg.ExecuteAsync("boom", ToolSchema.Empty, default);
        Assert.False(boom.Ok);
        Assert.StartsWith("InvalidOperation", boom.Text);
        Assert.False((await reg.ExecuteAsync("nope", ToolSchema.Empty, default)).Ok);
        var l = await reg.ExecuteAsync("long", ToolSchema.Empty, default);
        Assert.Equal(20, l.Text.Length);
        Assert.EndsWith("...", l.Text);
        Assert.StartsWith("bad input", (await reg.ExecuteAsync("needs", ToolSchema.Empty, default)).Text);
    }

    [Fact]
    public void Schema_Builds()
    {
        var s = ToolSchema.Create().Str("block", "what").Int("count", required: false).Build();
        Assert.Equal("object", s.GetProperty("type").GetString());
        Assert.Equal("integer", s.GetProperty("properties").GetProperty("count").GetProperty("type").GetString());
        Assert.Equal(["block"], s.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }
}
