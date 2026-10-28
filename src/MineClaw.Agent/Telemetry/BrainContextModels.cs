namespace MineClaw.Agent.Telemetry;

// Wire shapes of the brain context capture (BrainContextCapture): the Supervisor's /api/brains/{id}/* routes, the clones'
// brain_ctx IPC answer and the Explorer's Controller panel all use these records (snake_case JSON). Flat on purpose: the
// provider-neutral LlmBlock hierarchy isn't polymorphic JSON, so every block becomes one CtxBlock with a Kind.

/// <summary>Capture settings (host section <c>Telemetry:Capture</c>).</summary>
public sealed class BrainCaptureOptions
{
    /// <summary>Off = nothing is kept (the routes answer "capture off").</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Brain turns kept (the ring); worker/author/memory/reply sub-entries keep the same number in their own ring.</summary>
    public int Turns { get; set; } = 30;
    /// <summary>Background calls kept (reflection, RAPTOR summaries, grooming, address classification, line prefetch...).</summary>
    public int BackgroundTurns { get; set; } = 10;
    /// <summary>Calls kept per turn (a long worker run keeps its newest ones).</summary>
    public int CallsPerTurn { get; set; } = 48;
}

/// <summary>
/// One content block as the viewer shows it. <see cref="Kind"/>: <c>text</c> | <c>tool_use</c> | <c>tool_result</c> | <c>thinking</c> |
/// <c>redacted_thinking</c> | <c>reasoning</c> (a non-Claude model's reasoning text, e.g. DeepSeek through Ollama) | <c>image</c>.
/// </summary>
/// <param name="Label">What the block is, for the viewer: a turn section ("memory", "state", "journal"...), or the thinking label
/// ("Claude thinking (summarized by the API)", "redacted thinking (encrypted)").</param>
/// <param name="Input">A tool call's input as JSON text.</param>
/// <param name="Bytes">Redacted thinking / image: the payload size (the payload itself is never sent).</param>
/// <param name="Target">The subhive filter: this tool call (or its result) targeted the body being viewed.</param>
public sealed record CtxBlock(string Kind, string? Text = null, string? Label = null, string? Name = null, string? Id = null, string? Input = null,
    bool IsError = false, int? Bytes = null, bool Target = false);

/// <param name="Index">Position in the request's message list.</param>
/// <param name="Role">user | assistant.</param>
/// <param name="Tag">"turn" = a turn's first user message (events + per-turn data), "journal" = carries the compaction summary,
/// "tool_results", "response" = the assistant message of the call (not yet in a request), null = plain.</param>
public sealed record CtxMessage(int Index, string Role, List<CtxBlock> Blocks, string? Tag = null);

/// <param name="Tokens">Estimated tokens of this block (≈3.5 chars/token, scaled to the call's real prompt size when known).</param>
/// <param name="InCachedPrefix">This block is inside the cached prefix (tools + system up to the last cache breakpoint).</param>
public sealed record CtxSystemBlock(int Index, string Text, bool Cache, string? Ttl, int Tokens, bool InCachedPrefix);

/// <summary>A call's real API usage and cost.</summary>
public sealed record CtxUsage(long In, long Out, long CacheRead, long CacheWrite, long CacheWrite1h, decimal Cost, long LatencyMs, long PromptTokens);

/// <summary>A call in flight: "now thinking…".</summary>
public sealed record CtxInFlight(long Seq, long Turn, string Lane, string Purpose, string Model, DateTimeOffset Since, string? Body = null);

/// <summary>One turn: a brain episode (all its tool rounds), a worker run, an author job, a memory question, a compaction...</summary>
/// <param name="Lane">brain | reply | compaction | worker | author | memory | perception | background.</param>
/// <param name="Parent">Sub-entries: the brain turn that started them (via the turn's ambient context), else the brain turn
/// running when they began.</param>
/// <param name="Targets">The subhive filter: some call of this turn worked for the viewed body or a tool call named it.</param>
/// <param name="Version">The capture version of the last change (the routes' <c>since</c>).</param>
public sealed record CtxTurnSummary(long Id, string Lane, string Purpose, DateTimeOffset Start, DateTimeOffset? End, bool InFlight, int Calls,
    string Model, string Tier, string? Provider, long In, long Out, long CacheRead, long CacheWrite, decimal Cost, long LatencyMs, string Preview,
    List<string> Tools, bool HasThinking, long? Parent, string? Body, bool Targets, long Version, string? Error = null, int DroppedCalls = 0);

/// <summary>
/// The brain's live context window: the newest brain call's exact request (system blocks, tools, messages in order) and, once
/// it returned, its response as the last message.
/// </summary>
/// <param name="ToolTokens">Estimated tokens of the tool definitions (they lead the cached prefix).</param>
/// <param name="PromptTokens">The call's real prompt size (input + cache read + cache write); 0 while in flight.</param>
/// <param name="Limit">The brain's context limit (<c>Agent:Context:MaxTokens</c>).</param>
/// <param name="CompactAt">History size that triggers compaction (<c>Agent:Context:CompactAtTokens</c>).</param>
/// <param name="CachedPrefixTokens">Tools + system up to the last cache breakpoint (estimate, scaled).</param>
public sealed record CtxWindow(long CallSeq, long TurnId, DateTimeOffset At, string Model, string Tier, List<CtxSystemBlock> System, int ToolCount,
    int ToolTokens, List<string> ToolNames, List<CtxMessage> Messages, long PromptTokens, long EstimatedTokens, long Limit, long CompactAt,
    long CachedPrefixTokens, CtxUsage? Usage, bool InFlight);

/// <summary><c>/api/brains/{id}/context?since=</c>: turn summaries changed after <c>since</c>, and the window when it changed.</summary>
/// <param name="Version">Pass it back as <c>since</c>; equal = nothing changed (turns empty, window null).</param>
/// <param name="Filter">The body the targets were computed for (subhive view), null = none.</param>
public sealed record BrainContextPage(string Brain, long Version, DateTimeOffset At, int Capacity, bool Enabled, CtxInFlight? InFlight,
    List<CtxTurnSummary> Turns, CtxWindow? Window, List<long> Evicted, string? Filter = null, string? Note = null);

/// <summary>One call of a turn in full: what it added to the conversation, its response, usage.</summary>
/// <param name="Added">Messages this call's request added to the conversation (the first call: from the turn's first user message;
/// later calls: the tool results since the previous call).</param>
/// <param name="Context">Messages before <see cref="Added"/> in this request (the history it carried).</param>
public sealed record CtxCall(long Seq, DateTimeOffset At, string Purpose, string Tier, string Model, string? Provider, string? Body, int Round,
    bool InFlight, string? StopReason, CtxUsage? Usage, string? Error, int Context, List<CtxMessage> Added, List<CtxBlock> Response,
    int? MaxTokens = null, string? Effort = null, string? Thinking = null);

/// <summary><c>/api/brains/{id}/turn/{n}</c>: one turn in full (the system of its first call, every call, linked sub-entries).</summary>
public sealed record BrainTurnDetail(string Brain, CtxTurnSummary Turn, List<CtxSystemBlock> System, List<string> ToolNames, List<CtxCall> Calls,
    List<CtxTurnSummary> Children, string? Filter = null);

/// <summary>The clones' <c>brain_ctx</c> IPC request (and the in-process query): a page since a version, or one turn.</summary>
public sealed record BrainContextQuery(long Since = 0, long? Turn = null, string? Body = null);

/// <summary>The answer to <see cref="BrainContextQuery"/>: a page, or a turn (null = no such turn).</summary>
public sealed record BrainContextReply(BrainContextPage? Page, BrainTurnDetail? Turn);

/// <summary>
/// <c>/api/bodies/{name}/controller</c>: what drives a body. <see cref="Kind"/>: <c>main</c> (the root hive's own body), <c>hive</c> (a
/// clone with its own brain), <c>subhive</c> (a body its owning hive's brain drives: <see cref="Filter"/> = the body to highlight in
/// that brain's turns) or <c>script</c> (a body running one script, brain asleep).
/// </summary>
/// <param name="Brain">The brain id for <c>/api/brains/{id}/*</c> (null = script-only).</param>
/// <param name="BrainKind">opus | deepseek | a model profile, when known.</param>
/// <param name="Tabs">What the panel shows: "brain", "script" (a hive running a script shows both).</param>
public sealed record BodyControllerInfo(string Body, string Kind, string? Owner, string? Brain, string? BrainKind, string? Model, string? Provider,
    bool HasScript, string? Script, List<string> Tabs, string? Filter, ControllerLinks Links, string? Note = null);

/// <param name="Context">The brain's context route (<c>api/brains/{id}/context</c>).</param>
/// <param name="Script">The script debugger route (<c>api/bodies/{name}/script</c>).</param>
/// <param name="Owner">The owning hive's body name, for "open the full hive view".</param>
public sealed record ControllerLinks(string? Context, string? Script, string? Owner);
