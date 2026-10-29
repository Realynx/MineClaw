using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Perception;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.GameTools;
using MineClaw.World;
using Microsoft.Extensions.Logging;

namespace MineClaw.Bot.Perception.Vision;

/// <summary>
/// Auto-vision (vision v2): spatial job steps get a focused perception answer attached to the brain's next turn (via
/// <c>TurnPerception</c>) and the worker's next round (<c>WorkerAgent.RoundNote</c>) without anyone calling <c>see</c>:
/// gen mining (gen cells vs floor), placing/digging (faces, roles, void), builds and repairs (a before/after check ending the
/// step: "floor patched, 1 gap at x,y,z"), and "this one"/"the middle one" with a recent punch-point. One answer per job step;
/// a step whose spot hasn't changed since the last answer is skipped (view hash), the perception cache dedups the rest, and
/// <see cref="AutoVisionOptions.PerHour"/> caps it. The brain never waits for it; the worker waits at most
/// <see cref="AutoVisionOptions.WorkerWaitMs"/> for an answer already in flight.
/// </summary>
public sealed partial class AutoVision
{
    private readonly VisionHub _hub;
    private readonly IGameClient _c;
    private readonly AutoVisionOptions _o;
    private readonly ILogger? _log;
    private readonly Func<BlockPos, IReadOnlyList<BlockPos>>? _genCells;
    private readonly object _lock = new();
    private int _stepId;
    private Step? _build;               // a build/repair step waiting for its after-check
    private Task? _inflight;
    private int _inflightStep = -1;
    private string? _ready;
    private (ulong Hash, string Question)? _last;

    public AutoVision(VisionHub hub, IGameClient client, AutoVisionOptions options, Func<BlockPos, IReadOnlyList<BlockPos>>? genCells = null, ILogger? log = null)
    {
        _hub = hub;
        _c = client;
        _o = options;
        _genCells = genCells;
        _log = log;
    }

    /// <summary>Answers delivered (tests/telemetry).</summary>
    public int Delivered { get; private set; }
    public int SkippedUnchanged { get; private set; }

    public bool Enabled => _o.Enabled && _hub.Perception is { Available: true };

    private sealed record Step(int Id, string Kind, BlockPos Focus, string Question, bool Build, ulong StartHash);

    [GeneratedRegex(@"\b(?:(?:this|that) (?:one|block|cell|spot)|the (?:middle|left|right|other|first|last|second|third) one|right here)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DeicticRx();

    [GeneratedRegex(@"build|floor|wall|platform|pillar|place_line|bridge|repair|patch|fill_floor|roof|stair|extend", RegexOptions.IgnoreCase)]
    private static partial Regex BuildSkillRx();

    public void Attach(AgentRuntime rt, GestureTracker? gestures)
    {
        rt.Tools.Executing += (name, input) =>
        {
            try { OnToolStarted(name, input); } catch (Exception ex) { _log?.LogDebug(ex, "auto-vision trigger failed"); }
        };
        rt.Activities.StepFinished += (name, input, result, _) =>
        {
            try { if (result.Ok) OnStepFinished(name); } catch (Exception ex) { _log?.LogDebug(ex, "auto-vision after-check failed"); }
        };
        if (gestures != null)
            rt.Chat.Accepted += m =>
            {
                try
                {
                    if (m.Player is not { Length: > 0 } who || !DeicticRx().IsMatch(m.Text ?? "")) return;
                    var g = gestures.Recent().LastOrDefault(x => x.Player.Equals(who, StringComparison.OrdinalIgnoreCase));
                    var phrase = DeicticRx().Match(m.Text!).Value.ToLowerInvariant();
                    if (g != null)
                        Start(NewStep("pointing", g.Pos, $"{who} pointed (pink box) and said \"{phrase}\": which block is it, its role, and where is it relative to its neighbours (e.g. the middle of three gen cells)?", false));
                    else if (phrase.Contains("middle") || phrase.Contains("left") || phrase.Contains("right"))
                        Start(NewStep("which-one", GenFocus(_c.World.Player.Position.ToBlockPos()),
                            $"{who} said \"{phrase}\": list the candidates in a row (gen cells, chests...) with coords, and which one is {phrase}.", false));
                }
                catch (Exception ex) { _log?.LogDebug(ex, "auto-vision pointing failed"); }
            };
    }

    /// <summary>
    /// The kind, focus and question of a spatial tool step, or null for a tool that needs no eyes. Exposed for tests.
    /// </summary>
    public (string Kind, BlockPos Focus, string Question, bool Build)? Classify(string tool, JsonElement input)
    {
        var feet = _c.World.Player.Position.ToBlockPos();
        BlockPos? At(params string[] keys)
        {
            if (input.ValueKind != JsonValueKind.Object) return null;
            foreach (var k in keys)
                if (input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && Locations.ParseCoords(v.GetString()) is { } p) return p;
            if (input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object)
                foreach (var k in keys)
                    if (a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && Locations.ParseCoords(v.GetString()) is { } p) return p;
            return null;
        }
        switch (tool)
        {
            // Stone means the gen only where stone comes from one (skyblock); a normal world mines natural stone.
            case "mine" when MineClaw.Data.Versioning.WorldMode.Current.GeneratorStone && input.Str("block") is { } b
                && MineClaw.Data.Blocks.ByName(b) is { Id: MineClaw.Data.Blocks.Cobblestone or MineClaw.Data.Blocks.Stone }:
            case "harvest_generator":
            {
                var f = At("at") ?? GenFocus(feet);
                return ("gen", f, MineClaw.Bot.Perception.Vision.Eval.VisionEval.GenQuestion, false);
            }
            case "place":
            {
                var f = At("at") ?? feet;
                return ("place", f, $"Is {f.X},{f.Y},{f.Z} free, which solid blocks next to it can a block be placed against, and is there a fall under it?", false);
            }
            case "dig":
            {
                var f = At("at") ?? feet;
                return ("dig", f, "What is the block at the focus: its role (gen cell, floor, wall, protected) and what is under it (air, a fall?)", false);
            }
            case "skill":
            {
                var name = input.Str("name") ?? input.Str("skill") ?? "";
                if (!BuildSkillRx().IsMatch(name)) return null;
                var f = At("at", "to", "pos", "from", "start", "corner", "origin") ?? feet;
                return ("build", f, $"Check the {name.Replace('_', ' ')} at the focus: is it complete? List gaps or missing blocks with coords, and any open edge.", true);
            }
            default:
                return null;
        }
    }

    private BlockPos GenFocus(BlockPos near)
    {
        try
        {
            var cells = _genCells?.Invoke(near) ?? [];
            if (cells.Count > 0) return cells[0];
        }
        catch (Exception) { }
        return near;
    }

    private Step NewStep(string kind, BlockPos focus, string question, bool build) =>
        new(Interlocked.Increment(ref _stepId), kind, focus, question, build, _hub.ViewHash(focus, _o.Radius));

    /// <summary>A tool is about to run (the registry's Executing event: brain, worker, script or reflex).</summary>
    public void OnToolStarted(string tool, JsonElement input)
    {
        if (!Enabled || Classify(tool, input) is not { } c) return;
        var step = NewStep(c.Kind, c.Focus, c.Question, c.Build);
        if (c.Build)
        {
            // Builds are checked when they end (before/after); also a quick look before, when nothing was seen there yet.
            lock (_lock) _build = step;
            return;
        }
        Start(step);
    }

    /// <summary>A brain activity step finished: the pending build's after-check runs now (background; next turn gets it).</summary>
    public void OnStepFinished(string tool)
    {
        if (!_o.VerifyBuilds || tool is not ("skill" or "place")) return;
        Step? b;
        lock (_lock) { b = _build; _build = null; }
        if (b != null) Start(b with { Question = b.Question }, after: true);
    }

    /// <summary>Starts the look in the background unless the spot is unchanged since the last answer to the same question.</summary>
    private void Start(Step step, bool after = false)
    {
        if (!Enabled) return;
        var hash = _hub.ViewHash(step.Focus, _o.Radius);
        lock (_lock)
        {
            if (after && hash == step.StartHash) { SkippedUnchanged++; _hub.Perception?.Telemetry.Skip(); return; } // the build changed nothing
            if (_last is { } l && l.Hash == hash && l.Question == step.Question) { SkippedUnchanged++; _hub.Perception?.Telemetry.Skip(); return; }
            if (_inflight is { IsCompleted: false }) { _hub.Perception?.Telemetry.Skip(); return; } // one at a time
            _last = (hash, step.Question);
            _inflightStep = step.Id;
            _inflight = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, (_hub.Perception?.Options.TimeoutSeconds ?? 30) + 5)));
                    var a = await _hub.LookAsync(step.Focus, step.Question, _o.Radius, null, "auto:" + step.Kind + (after ? ":after" : ""), cts.Token)
                        .ConfigureAwait(false);
                    if (a is { Error: null })
                        lock (_lock) _ready = (after ? "[vision check] " + a.Text["[vision] ".Length..] : a.Text);
                }
                catch (Exception ex) { _log?.LogDebug(ex, "auto-vision look failed"); }
            });
        }
    }

    /// <summary>The brain's turn: the latest answer, once (non-blocking: an answer still in flight comes with the next turn).</summary>
    public string? TakeReady()
    {
        lock (_lock)
        {
            var r = _ready;
            _ready = null;
            if (r != null) Delivered++;
            return r;
        }
    }

    /// <summary>The worker's next round: runs a pending build's after-check, waits a bounded time for an answer in flight.</summary>
    public async Task<string?> ForWorkerAsync(CancellationToken ct)
    {
        if (!Enabled) return null;
        Step? b = null;
        if (_o.VerifyBuilds) lock (_lock) { b = _build; _build = null; }
        if (b != null) Start(b, after: true);
        Task? t;
        lock (_lock) t = _inflight;
        if (t is { IsCompleted: false } && _o.WorkerWaitMs > 0)
            await Task.WhenAny(t, Task.Delay(_o.WorkerWaitMs, ct)).ConfigureAwait(false);
        return TakeReady();
    }

    /// <summary>Waits for the look in flight (tests).</summary>
    public Task Idle() { lock (_lock) return _inflight ?? Task.CompletedTask; }
}
