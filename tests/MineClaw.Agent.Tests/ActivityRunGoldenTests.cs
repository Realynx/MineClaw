using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Tests;

/// <summary>
/// Pins <see cref="ActivityManager.RunAsync"/> (WP06 refactor): how a new chain meets what holds the body (free, a filler, a
/// job, a move with a job queued behind it, a speculative walk adopted / retargeted / cancelled, queue-behind-current), the
/// inline results the model reads, the speculation and start events, and the task events posted once every chain finished.
/// Every tool blocks on a gate the test opens, so each run is deterministic.
/// </summary>
public class ActivityRunGoldenTests
{
    private const string Expected = "62C5125307E4F4B26882BF641B9D2E9F3415EEF80B7FC25A9C92E0C5A958AAA2";

    private sealed class Rig
    {
        public readonly ToolRegistry Tools = new();
        public readonly EventQueue Events = new(new EventQueueOptions { CoalesceWindowMs = 10, MaxBatchDelayMs = 20, DedupeWindowSeconds = 0 });
        public readonly ActivityManager Activities;
        public readonly StringBuilder Log = new();
        private readonly Dictionary<string, TaskCompletionSource<ToolResult>> _open = new();
        private int _entered;

        public Rig()
        {
            Activities = new ActivityManager(Tools, Events);
            Activities.SpeculationResolved += (a, why) => Note($"resolved {a} {why}");
            Activities.Started += (id, kind, steps) => Note($"started #{id} {kind} {string.Join(">", steps.Select(s => s.Tool + s.Input.GetRawText()))}");
            foreach (var name in new[] { "goto", "mine", "craft" })
                Tools.Register(name, name, ToolSchema.Create().Str("at").Build(), async (i, ct) =>
                {
                    var gate = new TaskCompletionSource<ToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var reg = ct.Register(() => gate.TrySetResult(ToolResult.Error("cancelled")));
                    lock (_open)
                    {
                        _open[name + " " + i.Str("at")] = gate;
                        _entered++;
                    }
                    return await gate.Task;
                }, longRunning: true);
        }

        public void Note(string s) { lock (Log) Log.Append(s).Append('\n'); }

        private bool Pending(string key) { lock (_open) return _open.TryGetValue(key, out var g) && !g.Task.IsCompleted; }

        private int Entered { get { lock (_open) return _entered; } }

        public (IAgentTool, JsonElement) S(string tool, string at) =>
            (Tools.TryGet(tool, out var t) ? t : throw new InvalidOperationException(tool), JsonSerializer.SerializeToElement(new { at }));

        public async Task Run(string tag, ActivityKind kind, bool queue, params (IAgentTool, JsonElement)[] steps)
        {
            var res = await Activities.RunAsync(steps, TimeSpan.FromMilliseconds(40), default, kind, queue);
            Note($"{tag}: " + string.Join(" | ", res.Select(r => (r.Ok ? "ok " : "err ") + r.Text)));
            var cur = Activities.Current;
            Note($"  current: {(cur == null ? "-" : $"{cur.Label} #{cur.Id} {cur.Kind}")}; busy {Activities.IsBusy}; filler {Activities.Filler?.Label ?? "-"}");
        }

        /// <summary>A filler/walk whose tool must be running before the next chain arrives.</summary>
        public async Task Spec(string tag, string at)
        {
            await Run(tag, ActivityKind.Speculative, false, S("goto", at));
            await TestWaits.UntilAsync(() => Pending("goto " + at));
        }

        /// <summary>Ends the running tool <paramref name="key"/>, then waits for the next tool to start or the body to be free.</summary>
        public async Task Open(string key, ToolResult r)
        {
            await TestWaits.UntilAsync(() => Pending(key));
            int before = Entered;
            TaskCompletionSource<ToolResult> g;
            lock (_open) g = _open[key];
            g.TrySetResult(r);
            await TestWaits.UntilAsync(() => Entered > before || !Activities.HasAnyActivity);
            await Task.Delay(20);
        }

        public async Task<string> Finish()
        {
            await TestWaits.UntilAsync(() => !Activities.HasAnyActivity);
            await Task.Delay(30);
            foreach (var e in Events.Snapshot()) Note($"event {e.Kind} {e.Priority} wakes={e.Wakes} must={e.MustWake}: {e.Text}");
            lock (Log) return Log.ToString();
        }
    }

    [Fact]
    public async Task Chains_meet_whatever_holds_the_body()
    {
        var sb = new StringBuilder();

        // Fillers: free body → started; then a filler over a job → busy.
        var r = new Rig();
        await r.Run("social", ActivityKind.Social, false, r.S("goto", "fox"));
        await r.Run("social busy", ActivityKind.Social, false, r.S("goto", "kiwi"));
        await r.Run("job over social", ActivityKind.Job, false, r.S("mine", "dirt"));
        await r.Run("spec over job", ActivityKind.Speculative, false, r.S("goto", "gen"));
        await r.Open("mine dirt", ToolResult.Success("mined 8 dirt"));
        sb.Append("== fillers\n").Append(await r.Finish());

        // A move, a job queued behind it, then an unrelated job supersedes both.
        r = new Rig();
        await r.Run("move", ActivityKind.Move, false, r.S("goto", "gen"));
        await r.Run("queued", ActivityKind.Job, false, r.S("mine", "cobble"));
        await r.Run("move again", ActivityKind.Job, false, r.S("goto", "tree"), r.S("craft", "planks"));
        await r.Open("goto tree", ToolResult.Success("arrived"));
        await r.Open("craft planks", ToolResult.Error("no logs"));
        sb.Append("== supersede\n").Append(await r.Finish());

        // queue:true behind a running job, which then fails (the queued one still runs).
        r = new Rig();
        await r.Run("job", ActivityKind.Job, false, r.S("mine", "sand"));
        await r.Run("queue behind", ActivityKind.Job, true, r.S("craft", "glass"));
        await r.Open("mine sand", ToolResult.Error("no sand left"));
        await r.Open("craft glass", ToolResult.Success("crafted 4 glass"));
        sb.Append("== queue behind job\n").Append(await r.Finish());

        // A move then a job that starts with a move: supersedes (no queue).
        r = new Rig();
        await r.Run("move", ActivityKind.Move, false, r.S("goto", "gen"));
        await r.Run("move job", ActivityKind.Job, false, r.S("goto", "chest"), r.S("mine", "x"));
        await r.Open("goto chest", ToolResult.Success("arrived"));
        await r.Open("mine x", ToolResult.Success("done"));
        sb.Append("== move then move-job\n").Append(await r.Finish());

        foreach (var (verdict, tag) in new[] { (SpeculationVerdict.Adopt, "adopt"), (SpeculationVerdict.Retarget, "retarget"), (SpeculationVerdict.Cancel, "cancel") })
        {
            // Single-step move adopting a speculative walk; then a multi-step chain; then a non-move job.
            r = new Rig();
            r.Activities.SpeculationPolicy = (_, _) => verdict;
            await r.Spec("spec", "gen");
            await r.Run("single move", ActivityKind.Move, false, r.S("goto", "gen"));
            await r.Open("goto gen", ToolResult.Success("arrived at gen"));
            await r.Run("again single", ActivityKind.Move, false, r.S("goto", "gen"));
            await r.Open("goto gen", ToolResult.Success("arrived again"));
            sb.Append("== ").Append(tag).Append(" single\n").Append(await r.Finish());

            r = new Rig();
            r.Activities.SpeculationPolicy = (_, _) => verdict;
            await r.Spec("spec", "gen");
            await r.Run("chain", ActivityKind.Job, false, r.S("goto", "gen"), r.S("mine", "cobble"));
            await r.Open("goto gen", ToolResult.Success("arrived at gen"));
            await r.Open("mine cobble", ToolResult.Success("mined 12 cobble"));
            sb.Append("== ").Append(tag).Append(" chain\n").Append(await r.Finish());

            r = new Rig();
            r.Activities.SpeculationPolicy = (_, _) => verdict;
            await r.Spec("spec", "gen");
            await r.Run("job", ActivityKind.Job, false, r.S("mine", "dirt"));
            await r.Open("mine dirt", ToolResult.Success("mined"));
            sb.Append("== ").Append(tag).Append(" job\n").Append(await r.Finish());

            r = new Rig();
            r.Activities.SpeculationPolicy = (_, _) => verdict;
            await r.Spec("spec", "gen");
            await r.Run("failing chain", ActivityKind.Job, false, r.S("goto", "gen"), r.S("mine", "cobble"));
            await r.Open("goto gen", ToolResult.Error("no path"));
            sb.Append("== ").Append(tag).Append(" failed walk\n").Append(await r.Finish());
        }

        // A throwing policy counts as cancel; an empty chain returns nothing.
        r = new Rig();
        r.Activities.SpeculationPolicy = (_, _) => throw new InvalidOperationException("boom");
        await r.Spec("spec", "gen");
        await r.Run("move", ActivityKind.Move, false, r.S("goto", "gen"));
        await r.Run("empty", ActivityKind.Job, false);
        await r.Open("goto gen", ToolResult.Success("arrived"));
        sb.Append("== throwing policy\n").Append(await r.Finish());

        var dump = sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)));
        if (hash != Expected)
        {
            var dir = Path.Combine(Path.GetTempPath(), "mineclaw-golden");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "activity-run.txt"), dump);
        }
        Assert.Equal(Expected, hash);
    }
}
