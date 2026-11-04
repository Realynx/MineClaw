using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using MineClaw.Raptor;
using MineClaw.Raptor.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Self;

/// <summary>One upper-layer node of the self subtree: a "who I am" abstraction.</summary>
public sealed record SelfAbstraction(int Layer, string Text, int Leaves);

/// <summary>
/// The RAPTOR self subtree. Self entries, milestone signals and relationship notes are indexed as their own doc kind
/// (<see cref="Kind"/>, tag = scope; outside the main tree's kinds) and clustered into a small separate RAPTOR tree whose
/// upper layers are the "who I am" abstractions that reflection reads instead of raw logs. Built only during reflection
/// (cheap-tier summaries capped by <see cref="SelfOptions.SelfTreeMaxSummaries"/> / <see cref="SelfOptions.SelfTreeMaxUsd"/>);
/// the abstractions are stored per scope in <c>self_abstractions</c>.
/// </summary>
public sealed class SelfTree
{
    public const string Kind = "selfnote";

    public const string SummaryPrompt =
        "These are notes about a Minecraft bot's own personality, habits, feelings, relationships and memories. Write a short " +
        "\"who I am\" summary of them in the second person (you ...), at most 50 words, descriptive only: {context}";

    private readonly RagIndex _index;
    private readonly IEmbedder _embedder;
    private readonly ISummarizer _summarizer;
    private readonly SelfOptions _o;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public SelfTree(RagIndex index, IEmbedder embedder, ISummarizer summarizer, SelfOptions options, TimeProvider? time = null, ILogger? log = null)
    {
        _index = index;
        _embedder = embedder;
        _summarizer = summarizer;
        _o = options;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        SelfSchema.Ensure(index.Db);
    }

    private static string Prefix(string scope) => $"{Kind}:{scope}:";

    /// <summary>Writes the current self notes of <paramref name="growth"/>'s scope as docs (stale ones removed). Zero tokens.</summary>
    public int SyncNotes(SelfGrowth growth)
    {
        var scope = growth.Scope;
        var now = _time.GetUtcNow();
        var docs = new List<RagDoc>();
        foreach (var e in growth.Profile.Entries) docs.Add(EntryDoc(scope, e, now));
        foreach (var p in growth.KnownPlayers(8))
        {
            var line = growth.PersonLine(p);
            if (line.Length > 0) docs.Add(Doc(scope, "rel:" + p.ToLowerInvariant(), "relationship " + p, line, now));
        }
        foreach (var s in growth.Store.Recent(scope, null, 400).Where(s => SignalKinds.IsMilestone(s.Kind)).Take(40))
            docs.Add(Doc(scope, "m" + s.Id, "memory", $"{s.At:MM-dd}: {s.Note ?? s.Kind} (s{s.Id})", s.At));
        foreach (var p in growth.Preferences().Where(p => p.IsLike || p.IsDislike || p.IsPro).Take(6)) docs.Add(PreferenceDoc(scope, p, now));
        _index.UpsertMany(docs);
        _index.DeleteByPrefix(Prefix(scope), docs.Select(d => d.Ref).ToHashSet());
        return docs.Count;
    }

    private static RagDoc EntryDoc(string scope, SelfEntry e, DateTimeOffset now) =>
        Doc(scope, "e" + e.Id, $"{SelfProfile.Name(e.Section)}{(e.Player != null ? " " + e.Player : "")}",
            $"{SelfProfile.Name(e.Section)}{(e.Player != null ? " (" + e.Player + ")" : "")}: {e.Text} ({e.Observations} observations)", now);

    private static RagDoc PreferenceDoc(string scope, PreferenceState p, DateTimeOffset now) =>
        Doc(scope, "pref:" + p.Subject, "preference " + p.Subject,
            $"{TaskTypes.Label(p.Subject)}: done {p.Done:0}, ok {p.Ok:0}, failed {p.Fail:0}, praised {p.Praise:0.#}" + (p.IsLike ? " (likes it)" : p.IsDislike ? " (meh about it)" : ""), now);

    private static RagDoc Doc(string scope, string id, string title, string body, DateTimeOffset at) =>
        new($"{Prefix(scope)}{id}", Kind, title, TextUtil.Truncate(body, 240)) { Tag = scope, Source = Kind, At = at, Weight = 0.6 };

    /// <summary>Builds the scope's subtree and stores its upper-layer summaries. Returns them (top layer first).</summary>
    public async Task<List<SelfAbstraction>> BuildAsync(string scope, CancellationToken ct = default)
    {
        var notes = _index.Recent(Kind, null, 200, Prefix(scope));
        if (notes.Count < 4) return Abstractions(scope);
        using var raptor = new RaptorIndex(_embedder, _summarizer, new RaptorOptions
        {
            NumLayers = 3, ReductionDimension = 2, SummarizationLength = 70, MaxDegreeOfParallelism = 1, SummaryConcurrency = 2,
            SummaryPromptTemplate = SummaryPrompt,
        });
        var budget = new RaptorBudget { MaxSummaries = Math.Max(1, _o.SelfTreeMaxSummaries), MaxCostUsd = (double)_o.SelfTreeMaxUsd };
        await raptor.BuildAsync(notes.OrderBy(n => n.Id).Select(n => new RaptorDocument(n.Title + "\n" + n.Body, n.Ref, Kind, scope) { Chunk = false }),
            budget, ct).ConfigureAwait(false);
        var nodes = raptor.Read(t => t.AllNodes.Values.Where(n => n.Layer > 0 && !n.SummaryPending)
            .OrderByDescending(n => n.Layer).ThenBy(n => n.Index).Select(n => (n.Layer, n.Index, n.Text, Leaves: CountLeaves(t, n))).ToList());
        var db = _index.Db;
        long built = AgentDb.ToUnixMs(_time.GetUtcNow());
        db.Tx(() =>
        {
            db.Exec("DELETE FROM self_abstractions WHERE scope = $s", ("$s", scope));
            foreach (var (layer, idx, text, leaves) in nodes)
                db.Exec("INSERT INTO self_abstractions(scope, layer, idx, text, leaves, built) VALUES ($s, $l, $i, $t, $n, $b)",
                    ("$s", scope), ("$l", layer), ("$i", idx), ("$t", TextUtil.Truncate(TextUtil.OneLine(text), 400)), ("$n", leaves), ("$b", built));
        });
        _log.LogInformation("self subtree {Scope}: {Notes} notes, {Summaries} summaries", scope, notes.Count, nodes.Count);
        return Abstractions(scope);
    }

    private static int CountLeaves(RaptorTree t, RaptorNode n)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>(n.Children);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (!seen.Add(i) || !t.TryGet(i, out var c)) continue;
            foreach (var cc in c.Children) stack.Push(cc);
        }
        return seen.Count(i => t.TryGet(i, out var c) && c.IsLeaf);
    }

    /// <summary>Stored abstractions of a scope, top layer first.</summary>
    public List<SelfAbstraction> Abstractions(string scope) => Read(_index.Db, scope);

    public static List<SelfAbstraction> Read(AgentDb db, string scope) =>
        db.Query("SELECT layer, text, leaves FROM self_abstractions WHERE scope = $s ORDER BY layer DESC, leaves DESC, idx",
            r => new SelfAbstraction((int)r.GetInt64(0), r.GetString(1), (int)r.GetInt64(2)), ("$s", scope));
}
