using MineClaw.Agent.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Memory.Grooming;

/// <summary>One grooming stage: maps a doc to zero (dropped), one (rewritten) or several (chunked) docs. Deterministic.</summary>
public interface IMemoryGroomer
{
    string Name { get; }
    IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx);
}

/// <summary>A stage that needs I/O (the LLM rewrite). In the synchronous write path it passes docs through unchanged.</summary>
public interface IAsyncMemoryGroomer : IMemoryGroomer
{
    Task<IReadOnlyList<RagDoc>> ApplyAsync(RagDoc doc, GroomContext ctx, CancellationToken ct);
}

/// <summary>Free-text rewrite into memory style (the optional LLM stage). Null result = keep the text.</summary>
public interface IGroomRewriter
{
    Task<string?> RewriteAsync(string source, string text, CancellationToken ct);
}

/// <summary>What the stages may look at besides the doc.</summary>
public sealed class GroomContext
{
    public required GroomOptions Options { get; init; }
    /// <summary>The db the docs go to (merge lookups, the activity episode state). Null = stateless (preview of pasted text).</summary>
    public AgentDb? Db { get; init; }
    /// <summary>Preview: stages read state but never write it.</summary>
    public bool DryRun { get; init; }
    public int ChunkMaxTokens { get; init; } = 300;
    /// <summary>The main bot's login ("main" in crew labels).</summary>
    public string? MainName { get; init; }
    /// <summary>Hive bots (main + clones), case-insensitive.</summary>
    public IReadOnlySet<string> Crew { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    /// <summary>Known players/bots in their canonical spelling (case fixes).</summary>
    public IReadOnlyCollection<string> KnownNames { get; init; } = [];
    public IGroomRewriter? Rewriter { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>The activity episode state (db-backed, or an in-memory overlay for a dry run).</summary>
    public required ActivityEpisodeState Episodes { get; init; }
    /// <summary>Docs already produced in this groom call (the merge stage dedupes against them too).</summary>
    public List<RagDoc> Emitted { get; } = [];
}

/// <summary>One stage's output, for the Explorer's groom preview.</summary>
public sealed record GroomStep(string Stage, IReadOnlyList<RagDoc> Docs);

/// <summary>Raw → groomed, stage by stage.</summary>
public sealed record GroomTrace(RagDoc Input, IReadOnlyList<GroomStep> Steps, IReadOnlyList<RagDoc> Output)
{
    public bool Dropped => Output.Count == 0;
}

/// <summary>Where the ambient pieces of a <see cref="GroomContext"/> come from (built once per process).</summary>
public sealed class GroomEnvironment
{
    public AgentDb? Db { get; init; }
    public string? MainName { get; init; }
    public IEnumerable<string> Crew { get; init; } = [];
    public IEnumerable<string> Players { get; init; } = [];
    public int ChunkMaxTokens { get; init; } = 300;
    public IGroomRewriter? Rewriter { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The environment of a bot (or an admin context) from its options.</summary>
    public static GroomEnvironment From(AgentOptions o, AgentDb? db, IGroomRewriter? rewriter = null, TimeProvider? time = null)
    {
        var main = o.Hive.MainUsername ?? o.Chat.Username;
        var crew = o.Hive.AllClones().Select(c => c.Username).Append(main).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        var players = o.Self.TrustedPlayers.Concat(o.Self.RegularPlayers).ToList();
        return new GroomEnvironment
        {
            Db = db, MainName = main, Crew = crew!, Players = players, Rewriter = rewriter, Time = time ?? TimeProvider.System,
            ChunkMaxTokens = o.Memory.Groom.ChunkMaxTokens > 0 ? o.Memory.Groom.ChunkMaxTokens : o.Rag.ChunkMaxTokens,
        };
    }
}

/// <summary>
/// The memory groomer: the configured stage chain per doc source (<see cref="GroomOptions.Sources"/>), hooked into
/// <see cref="RagIndex.Upsert"/> so every write is groomed the same way (the one-off activity compaction runs the same chain).
/// Deterministic, no LLM unless the rewrite stage is on and the async path is used. Thread-safe (one groom at a time: the
/// activity episode state is read-modify-write).
/// </summary>
public sealed class MemoryGroomer : IDocGroomer
{
    private readonly GroomOptions _o;
    private readonly GroomEnvironment _env;
    private readonly Dictionary<string, IMemoryGroomer> _stages;
    private readonly ActivityEpisodeState _state;
    private readonly ILogger _log;
    // The db gate itself: UpsertMany grooms inside a transaction (gate held), so a separate lock could invert the order.
    private readonly object _lock;

    public MemoryGroomer(GroomOptions? options = null, GroomEnvironment? env = null, ILogger? log = null)
    {
        _o = options ?? new GroomOptions();
        _env = env ?? new GroomEnvironment();
        _log = log ?? NullLogger.Instance;
        _state = new ActivityEpisodeState(_env.Db);
        _lock = _env.Db?.Gate ?? new object();
        _stages = new IMemoryGroomer[]
        {
            new NormalizeStage(), new DenoiseStage(_o.DropPatterns), new RedactStage(), new PhraseStage(), new MergeStage(),
            new ActivityCoalesceStage(), new SizeStage(), new RewriteStage(),
        }.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
    }

    public GroomOptions Options => _o;
    public ActivityEpisodeState Episodes => _state;
    public IReadOnlyCollection<string> StageNames => _stages.Keys;

    /// <summary>Stage instance by name (tests).</summary>
    public IMemoryGroomer Stage(string name) => _stages[name];

    /// <summary>A context over this groomer's environment.</summary>
    public GroomContext Context(bool dryRun = false) => new()
    {
        Options = _o, Db = _env.Db, DryRun = dryRun, ChunkMaxTokens = _env.ChunkMaxTokens, MainName = _env.MainName,
        Crew = new HashSet<string>(_env.Crew, StringComparer.OrdinalIgnoreCase),
        KnownNames = _env.Crew.Concat(_env.Players).Concat(_o.Names.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Rewriter = _env.Rewriter, Time = _env.Time,
        Episodes = dryRun ? _state.Overlay() : _state,
    };

    public IReadOnlyList<string> StagesFor(string? source) => _o.StagesFor(source);

    /// <summary>The write path: groomed docs to upsert (empty = dropped). Never throws: a failing stage passes its input on.</summary>
    public IReadOnlyList<RagDoc> Groom(RagDoc doc)
    {
        if (!_o.Enabled) return [doc];
        lock (_lock) return Run(doc, Context(), null, async: false, CancellationToken.None).GetAwaiter().GetResult().Output;
    }

    /// <summary>Grooms with an explicit context (the compaction, which shares one context across the whole run).</summary>
    public IReadOnlyList<RagDoc> Groom(RagDoc doc, GroomContext ctx)
    {
        if (!_o.Enabled) return [doc];
        lock (_lock) return Run(doc, ctx, null, async: false, CancellationToken.None).GetAwaiter().GetResult().Output;
    }

    /// <summary>Raw → groomed with every stage's output; nothing is written (dry-run state).</summary>
    public GroomTrace Preview(RagDoc doc, GroomContext? ctx = null)
    {
        lock (_lock) return Run(doc, ctx ?? Context(dryRun: true), [], async: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The async path (log ingestion, the Explorer preview): also runs the LLM rewrite stage when it is on.</summary>
    public Task<GroomTrace> GroomAsync(RagDoc doc, GroomContext ctx, CancellationToken ct = default) =>
        Run(doc, ctx, [], async: true, ct);

    private async Task<GroomTrace> Run(RagDoc doc, GroomContext ctx, List<GroomStep>? steps, bool async, CancellationToken ct)
    {
        IReadOnlyList<RagDoc> docs = [doc];
        foreach (var name in StagesFor(doc.Source))
        {
            if (!_stages.TryGetValue(name, out var stage)) continue;
            var next = new List<RagDoc>();
            foreach (var d in docs)
            {
                try
                {
                    if (async && stage is IAsyncMemoryGroomer a) next.AddRange(await a.ApplyAsync(d, ctx, ct).ConfigureAwait(false));
                    else next.AddRange(stage.Apply(d, ctx));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "groom stage {Stage} failed on {Ref}; passing it on", name, d.Ref);
                    next.Add(d);
                }
            }
            docs = next;
            steps?.Add(new GroomStep(name, docs));
            if (docs.Count == 0) break;
        }
        // One output per ref (a merge may have rewritten an earlier doc of the same batch).
        var output = docs.GroupBy(d => d.Ref, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        foreach (var d in output)
        {
            ctx.Emitted.RemoveAll(e => e.Ref == d.Ref);
            ctx.Emitted.Add(d);
        }
        return new GroomTrace(doc, steps ?? [], output);
    }
}
