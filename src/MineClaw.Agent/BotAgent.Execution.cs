using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Telemetry;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// Tool execution for the brain loop: instant tools in parallel; long-running ones (and <c>do</c> chains) as the bot's
/// activity; the body lock (a running script answers body tools "body busy" unless queued); non-blocking moves
/// (goto returns at once, jobs queue behind it); streaming execution (a movement or read-only lookup starts the moment its
/// tool_use block is complete, before the message ends).
/// </summary>
public sealed partial class BotAgent
{
    /// <summary>Read-only lookups: safe to start early (streaming) and always free while a script holds the body.</summary>
    public static readonly IReadOnlySet<string> ReadOnlyTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "status", "inventory", "look", "where_is", "recall", "recall_detail", "list_memory", "see", "window", "combat_status",
        "activity", "describe_structure",
    };

    /// <summary>
    /// Tools that drive the body (movement, dig/place, containers, combat, gestures, worker jobs). While a script holds the
    /// body they answer "body busy" unless queued; everything else (chat, memory, look/see/status, hive, ask_player, script
    /// ops, profiles) stays free.
    /// </summary>
    public static bool IsBodyTool(string name) =>
        LatencyTracker.BodyTools.Contains(name) || name is "delegate" or "smelt" or "collect_drops" or "stop_spar";

    /// <summary>
    /// Who holds the body when it isn't free for brain body tools, e.g. "script cobble_loop 40%" (null = free). A running
    /// (not paused) script; the brain sees it as "[body: ...]" in each turn header.
    /// </summary>
    public Func<string?>? BodyHolder { get; set; }

    private string? BodyHolderNow()
    {
        try { return BodyHolder?.Invoke() is { Length: > 0 } h ? h : null; }
        catch (Exception ex) { _log.LogDebug(ex, "body holder failed"); return null; }
    }

    private sealed class Planned
    {
        public required LlmToolUse Use;
        public ToolResult? Result;
        public readonly List<(IAgentTool Tool, JsonElement Input)> Steps = new();
        /// <summary>A <c>do</c> (or a direct macro call): its step results are combined into one.</summary>
        public bool Compound;
        public bool Queue;
        public string? Prefix;
        public bool AllInstant => Steps.Count > 0 && Steps.All(s => !s.Tool.IsLongRunning);
    }

    private readonly ConcurrentDictionary<string, Task<ToolResult>> _early = new();
    private int _earlyChains;

    private Planned Plan(LlmToolUse u, ModelTier tier, bool retry, bool stopping, ICollection<string> queuedSigs)
    {
        var p = new Planned { Use = u };
        bool isDo = u.Name.Equals(DoTool.ToolName, StringComparison.OrdinalIgnoreCase);
        // A brain macro called directly (its definition lives in `do` now): run it the same way.
        bool direct = !isDo && tier == ModelTier.Main && DoTool.Actions.Contains(u.Name, StringComparer.OrdinalIgnoreCase)
            && _tools.TryGet(u.Name, out var dt) && !_tools.Allows(dt, tier);
        if (isDo || direct)
        {
            if (isDo && (!_tools.TryGet(DoTool.ToolName, out var doTool) || !_tools.Allows(doTool, tier)))
            {
                p.Result = ToolResult.Error(tier == ModelTier.Cheap ? "not available in quick mode" : "'do' is not available here");
                return p;
            }
            string? err = null;
            var steps = isDo ? DoTool.Expand(u.Input, out err) : [(u.Name, u.Input)];
            if (err != null) { p.Result = ToolResult.Error(err); return p; }
            p.Compound = true;
            p.Queue = isDo && DoTool.Queue(u.Input);
            foreach (var (name, input) in steps)
            {
                if (!_tools.TryGet(name, out var t)) { p.Result = ToolResult.Error($"do: '{name}' isn't available here"); return p; }
                p.Steps.Add((t, input));
            }
        }
        else
        {
            if (!_tools.TryGet(u.Name, out var tool))
            {
                p.Result = ToolResult.Error(u.Name == EscalateToolName ? "can't escalate right now" : $"unknown tool '{u.Name}'");
                return p;
            }
            if (!_tools.Allows(tool, tier))
            {
                p.Result = ToolResult.Error(tier == ModelTier.Cheap ? "not available in quick mode" : $"'{u.Name}' is a worker tool: use do or delegate");
                return p;
            }
            p.Steps.Add((tool, u.Input));
        }

        // Body lock: a running script owns the body; the brain decides explicitly (pause/replace it, or queue behind it).
        if (!p.Queue && p.Steps.Any(s => IsBodyTool(s.Tool.Name)) && BodyHolderNow() is { } holder)
        {
            p.Result = ToolResult.Error($"body busy: {holder}. Use script op=pause or op=replace, or queue it: do{{...,queue:true}} runs after it");
            return p;
        }
        if (p.AllInstant) return p;

        // Same call (or same target) already running, or it just failed there: say so instead of re-issuing.
        int first = p.Steps.FindIndex(s => s.Tool.IsLongRunning);
        var (ft, fi) = p.Steps[first];
        ToolResult? held = null;
        try { held = Guard.Check(ft.Name, fi, retry, stopping); } catch (Exception ex) { _log.LogDebug(ex, "tool guard failed"); }
        if (held == null && queuedSigs.Contains(ToolCallGuard.Signature(ft.Name, fi))) held = ToolResult.Success("already queued in this turn");
        if (held != null)
        {
            // The walk is already under way (a reflex, an earlier call): keep it and queue the rest behind it.
            if (held.Ok && p.Steps.Count > first + 1 && ActivityManager.MovementTools.Contains(ft.Name))
            {
                p.Prefix = held.Text;
                p.Steps.RemoveAt(first);
            }
            else p.Result = held;
        }
        return p;
    }

    private async Task<IReadOnlyList<LlmToolResult>> ExecuteToolsAsync(List<LlmToolUse> uses, ModelTier tier, bool retry, CancellationToken ct)
    {
        bool stopping = uses.Any(u => u.Name == "stop");
        if (stopping) Orders?.Snooze();
        var results = new ToolResult?[uses.Count];
        var tasks = new List<Task>();
        var chain = new List<(int Index, Planned P)>();
        var sigs = new List<string>();

        for (int i = 0; i < uses.Count; i++)
        {
            var u = uses[i];
            int idx = i;
            if (!ReadOnlyTools.Contains(u.Name) && !IsBodyTool(u.Name) && u.Name != DoTool.ToolName) Latency?.NoteSequence(u.Name);
            if (_early.TryRemove(u.Id, out var early))
            {
                tasks.Add(Task.Run(async () => results[idx] = await early.ConfigureAwait(false), ct));
                continue;
            }
            var p = Plan(u, tier, retry, stopping, sigs);
            if (p.Result != null) { results[i] = p.Result; continue; }
            if (p.AllInstant)
            {
                tasks.Add(Task.Run(async () => results[idx] = await RunInstantAsync(p, ct).ConfigureAwait(false), ct));
                continue;
            }
            chain.Add((i, p));
            sigs.AddRange(p.Steps.Where(s => s.Tool.IsLongRunning).Select(s => ToolCallGuard.Signature(s.Tool.Name, s.Input)));
        }

        if (chain.Count > 0)
        {
            tasks.Add(Task.Run(async () =>
            {
                var r = await RunChainAsync(chain.Select(c => c.P).ToList(), ct).ConfigureAwait(false);
                for (int k = 0; k < chain.Count; k++) results[chain[k].Index] = r[k];
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return uses.Select((u, i) =>
        {
            var r = results[i] ?? ToolResult.Error("no result");
            _log.LogDebug("tool {Name} → {Result}", u.Name, r);
            return new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok) { Image = r.Image };
        }).ToList();
    }

    private async Task<ToolResult> RunInstantAsync(Planned p, CancellationToken ct)
    {
        if (!p.Compound && p.Steps.Count == 1) return await _tools.ExecuteAsync(p.Steps[0].Tool, p.Steps[0].Input, ct).ConfigureAwait(false);
        var parts = new List<ToolResult>();
        foreach (var (tool, input) in p.Steps)
        {
            var r = await _tools.ExecuteAsync(tool, input, ct).ConfigureAwait(false);
            parts.Add(r);
            if (!r.Ok) break;
        }
        return Combine(parts, p.Prefix);
    }

    private static readonly Regex BackgroundTask = new(@"^running in background as task #(?<id>\d+)", RegexOptions.Compiled);

    /// <summary>One activity for every long-running call of the round, in order; a pure walk runs non-blocking.</summary>
    private async Task<IReadOnlyList<ToolResult>> RunChainAsync(IReadOnlyList<Planned> parts, CancellationToken ct)
    {
        var steps = parts.SelectMany(p => p.Steps).ToList();
        var bs = _options.Brainstem;
        bool walk = bs is { Enabled: true, NonBlockingMoves: true } && steps.All(s => s.Tool.Name.Equals("goto", StringComparison.OrdinalIgnoreCase));
        var kind = walk ? ActivityKind.Move : ActivityKind.Job;
        bool queue = parts.Any(p => p.Queue);
        // Walking, queued behind a walk, or queued behind a busy script: answer quickly; the brain keeps planning.
        bool behind = bs.Enabled && (queue || _activities.CurrentKind is ActivityKind.Move or ActivityKind.Speculative
            || steps[0].Tool.Name.Equals("goto", StringComparison.OrdinalIgnoreCase)); // "walk there, then work" too
        // Long jobs (chop, harvest, delegate, script...) answer "running in background" at once instead of holding the turn
        // (and every chat line after them) for the old 20 s; short jobs (craft, withdraw) get a few seconds to finish inline.
        bool longJob = steps.Any(s => bs.LongJobTools.Contains(s.Tool.Name, StringComparer.OrdinalIgnoreCase));
        var wait = !bs.Enabled ? TimeSpan.FromSeconds(_options.InlineToolWaitSeconds)
            : walk || behind || longJob ? TimeSpan.FromMilliseconds(Math.Max(0, bs.MoveInlineWaitMs))
            : TimeSpan.FromMilliseconds(Math.Max(0, Math.Min(bs.JobInlineWaitMs, _options.InlineToolWaitSeconds * 1000)));
        var r = await _activities.RunAsync(steps, wait, ct, kind, queue).ConfigureAwait(false);

        var output = new List<ToolResult>(parts.Count);
        int k = 0;
        foreach (var p in parts)
        {
            var slice = r.Skip(k).Take(p.Steps.Count).Select(x => walk ? MoveText(x) : x).ToList();
            k += p.Steps.Count;
            output.Add(p.Compound || slice.Count > 1 || p.Prefix != null ? Combine(slice, p.Prefix) : slice[0]);
        }
        return output;
    }

    /// <summary>A walk that outlived the short inline wait: say so, and that the brain may keep planning.</summary>
    private static ToolResult MoveText(ToolResult r) =>
        BackgroundTask.Match(r.Text) is { Success: true } m
            ? ToolResult.Success($"walking there (task #{m.Groups["id"].Value}): keep planning; job actions you call now run on arrival; a failure wakes you")
            : r;

    private static ToolResult Combine(IReadOnlyList<ToolResult> parts, string? prefix)
    {
        var texts = parts.Select(x => x.Text).Where(t => t.Length > 0).ToList();
        if (prefix != null) texts.Insert(0, prefix);
        var text = string.Join("; then ", texts);
        return parts.All(x => x.Ok) ? ToolResult.Success(text) : ToolResult.Error(text);
    }

    /// <summary>
    /// Streaming execution: the hook handed to the provider for this call. Read-only lookups start at once; the first
    /// body call of the message starts too when it is a walk (a <c>goto</c>/<c>follow</c>, or a <c>do</c> led by one):
    /// walking begins while the model is still writing. Anything else waits for the message (order is kept).
    /// </summary>
    private Action<LlmToolUse>? EarlyStarter(ModelTier tier, bool retry, string model, CancellationToken ct)
    {
        var bs = _options.Brainstem;
        if (bs is not { Enabled: true, StreamingExecution: true } || tier != ModelTier.Main) return null;
        bool blocked = false;
        return use =>
        {
            try
            {
                if (blocked || use.Name is EscalateToolName or "stop") { blocked = true; return; }
                using (ToolCaller.Push(tier, model))
                {
                    var p = Plan(use, tier, retry, stopping: false, Array.Empty<string>());
                    if (p.Result != null) { blocked |= !ReadOnlyTools.Contains(use.Name); return; }
                    if (p.AllInstant && p.Steps.All(s => ReadOnlyTools.Contains(s.Tool.Name)))
                    {
                        _early[use.Id] = Task.Run(() => RunInstantAsync(p, ct), ct);
                        return;
                    }
                    blocked = true; // from here on nothing starts early: tool order is the model's order
                    if (p.Steps.Count > 0 && p.Steps[0].Tool.IsLongRunning && ActivityManager.MovementTools.Contains(p.Steps[0].Tool.Name))
                    {
                        Interlocked.Increment(ref _earlyChains);
                        _log.LogDebug("streaming: started {Tool} before the message ended", use.Name);
                        _early[use.Id] = Task.Run(async () => (await RunChainAsync([p], ct).ConfigureAwait(false))[0], ct);
                    }
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "early tool start failed"); blocked = true; }
        };
    }

    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);

    /// <summary>The exact event instance (its queue time included): a player re-typing the same line is a new event.</summary>
    private static string HandledKey(Events.GameEvent e) => $"{(int)e.Kind}|{e.Player}|{e.Text}|{e.At.UtcTicks}";

    /// <summary>Every waking event of the batch was already in the previous turn (e.g. injected mid-turn): nothing new.</summary>
    private bool AlreadyHandled(Events.EventBatch batch)
    {
        var waking = batch.Waking.ToList();
        if (waking.Count == 0 || batch.HasDanger) return false;
        lock (_handled) return _handled.Count > 0 && waking.All(e => _handled.Contains(HandledKey(e)));
    }

    /// <summary>A cancelled (restarted) call may have started a walk early: stop it; the restarted turn decides again.</summary>
    private void DropEarlyStarts(string reason)
    {
        if (Interlocked.Exchange(ref _earlyChains, 0) > 0) _activities.Cancel(reason);
        _early.Clear();
    }
}
