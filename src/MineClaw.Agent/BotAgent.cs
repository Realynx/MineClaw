using System.Text;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Context;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent;

/// <summary>Result of handling one event batch (for logs and tests).</summary>
public sealed record EpisodeOutcome(ModelTier? Tier, int Calls, LlmUsage Usage, decimal CostUsd, IReadOnlyList<string> Said,
    bool Escalated, string? Skipped = null);

/// <summary>
/// The agent loop: wait for a coalesced event batch → triage to a tier → call the model with a compact turn
/// (state header + events) → run tool calls (instant ones in parallel, long-running ones as the bot's
/// activity) → feed results back → repeat until the model ends its turn → commit to bounded context → sleep.
/// </summary>
public sealed class BotAgent
{
    public const string EscalateToolName = "escalate";

    private static readonly LlmToolDefinition EscalateTool = new(EscalateToolName,
        "Hand this turn to the planning model (needs game actions, planning, or tools you lack).",
        ToolSchema.Create().Str("reason").Build());

    private readonly AgentOptions _options;
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly EventQueue _events;
    private readonly ChatRouter _chat;
    private readonly UsageTracker _usage;
    private readonly ContextManager _context;
    private readonly ActivityManager _activities;
    private readonly Triage _triage;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly LlmSystemBlock _staticSystem;
    private DateTimeOffset _lastEpisode;
    private DateTimeOffset _lastPausedNotice = DateTimeOffset.MinValue;

    public BotAgent(AgentOptions options, ILlmClient llm, ToolRegistry tools, EventQueue events, ChatRouter chat,
        UsageTracker usage, ContextManager context, ActivityManager activities, string knowledge = "",
        TimeProvider? time = null, ILogger? log = null)
    {
        _options = options;
        _llm = llm;
        _tools = tools;
        _events = events;
        _chat = chat;
        _usage = usage;
        _context = context;
        _activities = activities;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _triage = new Triage(options.Triage);
        _staticSystem = new LlmSystemBlock(Prompts.System(options.Persona, options.PersonaExtra, knowledge), Cache: true);
        _lastEpisode = _time.GetUtcNow();

        _events.Posted += OnEventPosted;
        _usage.ModeChanged += OnBudgetModeChanged;
    }

    /// <summary>Compact one-line game state ("pos 12,64,-3 hp 18/20 food 17 held stone_pickaxe"), supplied by the client layer.</summary>
    public Func<string>? StateHeader { get; set; }

    public ContextManager Context => _context;
    /// <summary>
    /// Current system prompt: the cached static block (instructions + knowledge; tools render before it and share
    /// its breakpoint) followed by a tiny uncached identity block, so renames never bust the cached prefix.
    /// </summary>
    public IReadOnlyList<LlmSystemBlock> SystemPrompt =>
        [_staticSystem, new LlmSystemBlock(Prompts.Identity(_chat.Username, _chat.DisplayName, _chat.Aliases))];

    /// <summary>Raised after each handled batch.</summary>
    public event Action<EpisodeOutcome>? EpisodeCompleted;

    public async Task RunAsync(CancellationToken ct)
    {
        _activities.Shutdown = ct;
        using var idleTimer = StartIdleTimer(ct);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            EventBatch batch;
            try { batch = await _events.WaitBatchAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                await HandleBatchAsync(batch, ct).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                _log.LogError(ex, "agent turn failed ({Failures} in a row)", failures);
                var backoff = TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(failures, 5))));
                try { await Task.Delay(backoff, _time, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Handles one batch end-to-end (public for tests and for hosts that drive the loop themselves).</summary>
    public async Task<EpisodeOutcome> HandleBatchAsync(EventBatch batch, CancellationToken ct)
    {
        _lastEpisode = _time.GetUtcNow();
        var mode = _usage.CheckMode();
        if (mode == BudgetMode.Paused)
        {
            await NotifyPausedAsync(batch, ct).ConfigureAwait(false);
            return Done(new EpisodeOutcome(null, 0, LlmUsage.Zero, 0, [], false, "paused: " + _usage.Reason));
        }

        var decision = _triage.Choose(batch, mode);
        _log.LogDebug("triage → {Tier} ({Why})", decision.Tier, decision.Why);
        var outcome = await RunEpisodeAsync(batch, decision.Tier, decision.CanEscalate, mode, null, ct).ConfigureAwait(false);

        if (outcome.Escalation is { } esc)
        {
            var prelude = $"(escalated from quick mode: {esc.Reason}" + (esc.Said.Count > 0 ? $"; you already said: \"{string.Join(" ", esc.Said)}\")" : ")");
            var main = await RunEpisodeAsync(batch, ModelTier.Main, false, mode, prelude, ct).ConfigureAwait(false);
            outcome = main with
            {
                Calls = main.Calls + outcome.Calls,
                Usage = main.Usage.Add(outcome.Usage),
                Cost = main.Cost + outcome.Cost,
                Said = [.. outcome.Said, .. main.Said],
                Escalation = esc,
            };
        }

        if (_context.NeedsCompaction)
        {
            try
            {
                int folded = await _context.CompactAsync(_usage.Mode == BudgetMode.Paused ? null : SummarizeAsync, ct).ConfigureAwait(false);
                _log.LogDebug("compacted {Count} episodes into journal", folded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "compaction failed"); }
        }

        return Done(new EpisodeOutcome(outcome.Tier, outcome.Calls, outcome.Usage, outcome.Cost, outcome.Said, outcome.Escalation != null));
    }

    private EpisodeOutcome Done(EpisodeOutcome o)
    {
        _log.LogInformation("turn: tier {Tier} calls {Calls} {Usage} ${Cost:0.0000} | {Budget}", o.Tier, o.Calls, o.Usage, o.CostUsd, _usage.Summary());
        EpisodeCompleted?.Invoke(o);
        return o;
    }

    // ---------------- episode ----------------

    private sealed record Escalation(string Reason, IReadOnlyList<string> Said);

    private sealed record RunResult(ModelTier Tier, int Calls, LlmUsage Usage, decimal Cost, IReadOnlyList<string> Said, Escalation? Escalation);

    private async Task<RunResult> RunEpisodeAsync(EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string? prelude, CancellationToken ct)
    {
        var episode = new Episode(tier, _time.GetUtcNow());
        episode.Messages.Add(LlmMessage.User(BuildTurnText(batch, tier, canEscalate, mode, prelude)));

        string? whisperTo = batch.WhisperOnlyFrom;
        var audience = batch.Waking.Where(e => e.Player != null).Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var said = new List<string>();
        var usage = LlmUsage.Zero;
        decimal cost = 0;
        int calls = 0;
        var tools = ToolsFor(tier, canEscalate);
        // Snapshot per episode: a rename mid-turn applies from the next turn, keeping this turn's prefix byte-stable.
        var system = SystemPrompt;

        for (int round = 0; round < _options.MaxToolRounds; round++)
        {
            var nowMode = _usage.CheckMode();
            if (nowMode == BudgetMode.Paused || (tier == ModelTier.Main && nowMode == BudgetMode.CheapOnly && round > 0)) break;

            var request = new LlmRequest
            {
                Tier = tier,
                System = system,
                Tools = tools,
                Messages = _context.BuildMessages(tier, episode.Messages),
                Purpose = tier == ModelTier.Cheap ? "chat" : "task",
            };
            var response = await _llm.CompleteAsync(request, ct).ConfigureAwait(false);
            calls++;
            usage = usage.Add(response.Usage);
            cost += _usage.Record(response.Model, response.Usage);

            var content = response.Content.Where(b => b is not LlmText t || !string.IsNullOrWhiteSpace(t.Text)).ToList();
            if (content.Count == 0) content.Add(new LlmText("(silent)"));
            episode.Messages.Add(new LlmMessage(LlmRole.Assistant, content));

            var text = response.Text.Trim();
            if (!IsSilent(text))
            {
                var lines = await _chat.SayAsync(text, whisperTo, audience, ct).ConfigureAwait(false);
                said.AddRange(lines);
            }

            if (response.StopReason == StopReasons.Refusal)
            {
                _log.LogWarning("model refused");
                break;
            }

            var uses = response.ToolUses.ToList();
            if (uses.Count == 0) break;

            var esc = uses.FirstOrDefault(u => u.Name == EscalateToolName);
            if (esc != null && canEscalate)
            {
                // Discard the cheap exchange (its escalate call never enters history) and let the main tier redo the turn.
                return new RunResult(tier, calls, usage, cost, said, new Escalation(esc.Input.Str("reason") ?? "needs planning", said));
            }

            var results = await ExecuteToolsAsync(uses, tier, ct).ConfigureAwait(false);
            var blocks = new List<LlmBlock>(results);

            // Let urgent/new events reach the model mid-turn (appended after the tool results — append-only).
            var fresh = _events.TryTakeUrgent(EventPriority.Normal);
            if (fresh != null)
            {
                blocks.Add(new LlmText("[new events]\n" + fresh.Format()));
                foreach (var p in fresh.Waking.Where(e => e.Player != null).Select(e => e.Player!))
                    if (!audience.Contains(p, StringComparer.OrdinalIgnoreCase)) audience.Add(p);
                if (fresh.WhisperOnlyFrom == null && fresh.Waking.Any()) whisperTo = null;
            }
            episode.Messages.Add(new LlmMessage(LlmRole.User, blocks));
        }

        _context.Commit(episode);
        return new RunResult(tier, calls, usage, cost, said, null);
    }

    private string BuildTurnText(EventBatch batch, ModelTier tier, bool canEscalate, BudgetMode mode, string? prelude)
    {
        var sb = new StringBuilder();
        string header = "";
        try { header = StateHeader?.Invoke() ?? ""; } catch (Exception ex) { _log.LogWarning(ex, "state header failed"); }
        var activity = _activities.Describe();
        var status = string.Join(" | ", new[] { header.Trim(), activity != null ? "task: " + activity : (header.Contains("task:") ? "" : "task: idle") }
            .Where(s => s.Length > 0));
        sb.Append(status.TrimStart('[')).Append('\n');
        if (mode == BudgetMode.CheapOnly) sb.Append(Prompts.CheapOnlyHint).Append('\n');
        else if (tier == ModelTier.Cheap && canEscalate) sb.Append(Prompts.CheapTierHint).Append('\n');
        if (prelude != null) sb.Append(prelude).Append('\n');
        sb.Append(batch.Format());
        return sb.ToString().TrimEnd();
    }

    private IReadOnlyList<LlmToolDefinition> ToolsFor(ModelTier tier, bool canEscalate)
    {
        if (tier == ModelTier.Main) return _tools.Definitions();
        var defs = _tools.Definitions(t => t.AvailableToCheapTier).ToList();
        if (canEscalate) defs.Add(EscalateTool);
        return defs;
    }

    private async Task<IReadOnlyList<LlmToolResult>> ExecuteToolsAsync(List<LlmToolUse> uses, ModelTier tier, CancellationToken ct)
    {
        var results = new ToolResult?[uses.Count];
        var instant = new List<Task>();
        var chain = new List<(int Index, IAgentTool Tool, LlmToolUse Use)>();

        for (int i = 0; i < uses.Count; i++)
        {
            var u = uses[i];
            if (!_tools.TryGet(u.Name, out var tool))
            {
                results[i] = ToolResult.Error(u.Name == EscalateToolName ? "can't escalate right now" : $"unknown tool '{u.Name}'");
                continue;
            }
            if (tier == ModelTier.Cheap && !tool.AvailableToCheapTier)
            {
                results[i] = ToolResult.Error("not available in quick mode");
                continue;
            }
            if (tool.IsLongRunning) chain.Add((i, tool, u));
            else
            {
                int idx = i;
                instant.Add(Task.Run(async () => results[idx] = await _tools.ExecuteAsync(tool, u.Input, ct).ConfigureAwait(false), ct));
            }
        }

        Task chainTask = Task.CompletedTask;
        if (chain.Count > 0)
        {
            chainTask = Task.Run(async () =>
            {
                var r = await _activities.RunAsync(chain.Select(c => (c.Tool, c.Use.Input)).ToList(),
                    TimeSpan.FromSeconds(_options.InlineToolWaitSeconds), ct).ConfigureAwait(false);
                for (int k = 0; k < chain.Count; k++) results[chain[k].Index] = r[k];
            }, ct);
        }

        await Task.WhenAll(instant.Append(chainTask)).ConfigureAwait(false);

        return uses.Select((u, i) =>
        {
            var r = results[i] ?? ToolResult.Error("no result");
            _log.LogDebug("tool {Name} → {Result}", u.Name, r);
            return new LlmToolResult(u.Id, r.Text.Length == 0 ? (r.Ok ? "ok" : "error") : r.Text, !r.Ok);
        }).ToList();
    }

    private static bool IsSilent(string text) =>
        text.Length == 0 || text is "." or "-" or "..." || text.Equals("(silent)", StringComparison.OrdinalIgnoreCase);

    // ---------------- support ----------------

    private async Task<string> SummarizeAsync(string prompt, CancellationToken ct)
    {
        var response = await _llm.CompleteAsync(new LlmRequest
        {
            Tier = ModelTier.Cheap,
            System = [new LlmSystemBlock(ContextManager.CompactionInstructions)],
            Messages = [LlmMessage.User(prompt)],
            MaxTokens = 500,
            CacheHistory = false,
            Purpose = "compact",
        }, ct).ConfigureAwait(false);
        _usage.Record(response.Model, response.Usage);
        return response.Text;
    }

    private void OnEventPosted(GameEvent e)
    {
        if (e.Wakes && e.Priority >= _options.InterruptPriority)
        {
            var cancelled = _activities.Cancel("interrupted by " + e.Kind);
            if (cancelled != null) _log.LogInformation("interrupted {Activity} due to {Event}", cancelled, e.Format());
        }
    }

    private void OnBudgetModeChanged(BudgetMode mode, string reason)
    {
        string? notice = mode switch
        {
            BudgetMode.Paused => $"heads up: pausing my brain ({reason}). i'll be back when it resets",
            BudgetMode.CheapOnly => $"heads up: {reason}, so i'm in chat-only mode for now",
            BudgetMode.Normal => "budget's fine again, back to full brain",
            _ => null,
        };
        _log.LogWarning("budget mode → {Mode}: {Reason}", mode, reason);
        if (notice != null) _ = SafeSayAsync(notice);
    }

    private async Task NotifyPausedAsync(EventBatch batch, CancellationToken ct)
    {
        if (!batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper)) return;
        var now = _time.GetUtcNow();
        if (now - _lastPausedNotice < TimeSpan.FromMinutes(_options.PausedNoticeMinutes)) return;
        _lastPausedNotice = now;
        await _chat.SayAsync($"zzz... my brain's paused ({_usage.Reason}). talk soon!", batch.WhisperOnlyFrom, ct: ct).ConfigureAwait(false);
    }

    private async Task SafeSayAsync(string text)
    {
        try { await _chat.SayAsync(text).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogWarning(ex, "chat send failed"); }
    }

    private ITimer? StartIdleTimer(CancellationToken ct)
    {
        if (_options.IdleCheckInMinutes <= 0) return null;
        var period = TimeSpan.FromMinutes(1);
        return _time.CreateTimer(_ =>
        {
            if (ct.IsCancellationRequested || _activities.IsBusy) return;
            if (_time.GetUtcNow() - _lastEpisode >= TimeSpan.FromMinutes(_options.IdleCheckInMinutes))
            {
                _lastEpisode = _time.GetUtcNow();
                _events.Post(GameEvent.Idle());
            }
        }, null, period, period);
    }
}
