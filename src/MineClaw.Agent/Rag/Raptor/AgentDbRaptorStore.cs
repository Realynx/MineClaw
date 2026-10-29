using System.Text.Json;
using MineClaw.Raptor;
using MineClaw.Raptor.Storage;

namespace MineClaw.Agent.Rag.Raptor;

/// <summary>
/// <see cref="IRaptorStore"/> over <see cref="AgentDb"/>. Every tree node is a <c>docs</c> row (<c>raptor_idx</c> =
/// node index): a leaf is the doc it came from (Key = <c>docs.ref</c>), a summary is a doc of kind <c>summary</c>
/// with ref <c>raptor:{idx}</c> and level = layer. Parent→child links are <c>doc_edges</c> (multi-parent for soft
/// clustering), embeddings are <c>vectors</c> rows (leaves reuse the vector the flat index already has), and the
/// tree metadata is <c>meta('raptor_meta')</c>. Each change set is applied in one transaction.
/// </summary>
public sealed class AgentDbRaptorStore : IRaptorStore
{
    public const string MetaKey = "raptor_meta";
    public const string SummaryRefPrefix = "raptor:";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly AgentDb _db;
    private volatile Dictionary<int, long> _idxToDoc = new();
    private volatile HashSet<long> _treeDocs = new();

    public AgentDbRaptorStore(AgentDb db) => _db = db;

    /// <summary>
    /// False in a process that only reads the shared tree (a hive clone, maintenance off): a load never writes back
    /// (unmapping leaves whose doc text changed would make main re-insert them: churn from every clone reload, B23).
    /// </summary>
    public bool LoadWritesBack { get; set; } = true;

    /// <summary>Test hook: throw while applying this record (checks that a change set is all-or-nothing).</summary>
    internal Func<RaptorNodeRecord, bool>? FailOn { get; set; }

    /// <summary>Leaves the last apply/load could not map to a doc (doc vanished).</summary>
    public int Orphans { get; private set; }

    public int MappedNodes => _idxToDoc.Count;

    /// <summary>Doc id of a tree node, if mapped.</summary>
    public bool TryGetDocId(int index, out long docId) => _idxToDoc.TryGetValue(index, out docId);

    /// <summary>True if the doc is a node of the tree (leaf or summary).</summary>
    public bool ContainsDoc(long docId) => _treeDocs.Contains(docId);

    private static int Flags(RaptorNodeRecord r) => (r.Pinned ? 1 : 0) | (r.Dirty ? 2 : 0) | (r.SummaryPending ? 4 : 0);

    // ======================= load =======================

    public Task<RaptorSnapshot?> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_db.Gate)
        {
            var json = _db.GetMeta(MetaKey);
            if (string.IsNullOrEmpty(json)) return Task.FromResult<RaptorSnapshot?>(null);
            var meta = JsonSerializer.Deserialize<RaptorTreeMeta>(json, Json);
            if (meta == null) return Task.FromResult<RaptorSnapshot?>(null);

            var rows = _db.Query("""
                SELECT d.id, d.raptor_idx, d.level, d.ref, d.kind, d.source, d.title, d.body, d.token_count, d.raptor_flags, d.raptor_seq, v.vec
                FROM docs d LEFT JOIN vectors v ON v.doc_id = d.id AND v.model = $m AND v.dims = $d AND v.hash = d.hash
                WHERE d.raptor_idx IS NOT NULL
                """, r => (Id: r.GetInt64(0), Idx: (int)r.GetInt64(1), Layer: (int)r.GetInt64(2), Ref: r.GetString(3), Kind: r.GetString(4),
                    Source: AgentDb.StrOrNull(r, 5), Title: r.GetString(6), Body: r.GetString(7), Tokens: (int)r.GetInt64(8),
                    Flags: (int)r.GetInt64(9), Seq: r.GetInt64(10), Vec: r.IsDBNull(11) ? null : (byte[])r.GetValue(11)),
                ("$m", meta.EmbeddingModel), ("$d", meta.Dimensions));
            var children = new Dictionary<int, SortedSet<int>>();
            foreach (var (p, c) in _db.Query("""
                SELECT p.raptor_idx, c.raptor_idx FROM doc_edges e JOIN docs p ON p.id = e.parent JOIN docs c ON c.id = e.child
                WHERE p.raptor_idx IS NOT NULL AND c.raptor_idx IS NOT NULL
                """, r => ((int)r.GetInt64(0), (int)r.GetInt64(1))))
            {
                if (!children.TryGetValue(p, out var set)) children[p] = set = new SortedSet<int>();
                set.Add(c);
            }

            // A leaf whose vector is gone (its text changed materially) or a summary without a vector can't be loaded:
            // drop it, prune summaries left without children and mark the survivors above them dirty.
            var byIdx = rows.Where(r => r.Vec != null && (r.Layer == 0 || r.Kind == RagIndex.SummaryKind)).ToDictionary(r => r.Idx);
            int droppedLeaves = rows.Count(r => r.Layer == 0 && !byIdx.ContainsKey(r.Idx));
            var dirty = new HashSet<int>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (p, set) in children)
                {
                    if (!byIdx.ContainsKey(p)) continue;
                    int before = set.Count;
                    set.RemoveWhere(c => !byIdx.ContainsKey(c));
                    if (set.Count != before) dirty.Add(p);
                }
                foreach (var s in byIdx.Values.Where(r => r.Layer > 0 && (!children.TryGetValue(r.Idx, out var cs) || cs.Count == 0)).ToList())
                {
                    byIdx.Remove(s.Idx);
                    changed = true;
                }
            }
            if (dirty.Count > 0)
            {
                var parents = new Dictionary<int, List<int>>();
                foreach (var (p, set) in children)
                    foreach (var c in set) (parents.TryGetValue(c, out var l) ? l : parents[c] = new List<int>()).Add(p);
                var stack = new Stack<int>(dirty);
                while (stack.Count > 0)
                    if (parents.TryGetValue(stack.Pop(), out var ps))
                        foreach (var p in ps) if (byIdx.ContainsKey(p) && dirty.Add(p)) stack.Push(p);
            }
            var dropped = rows.Where(r => !byIdx.ContainsKey(r.Idx)).ToList();
            if (dropped.Count > 0 && LoadWritesBack)
                _db.Tx(() =>
                {
                    foreach (var d in dropped)
                    {
                        if (d.Kind == RagIndex.SummaryKind && d.Layer > 0) _db.Exec("DELETE FROM docs WHERE id = $id", ("$id", d.Id));
                        else _db.Exec("UPDATE docs SET raptor_idx = NULL, raptor_flags = 0 WHERE id = $id", ("$id", d.Id));
                    }
                });

            var nodes = new List<RaptorNodeRecord>(byIdx.Count);
            foreach (var r in byIdx.Values.OrderBy(r => r.Idx))
            {
                bool leaf = r.Layer == 0;
                int flags = r.Flags | (dirty.Contains(r.Idx) ? 2 : 0);
                nodes.Add(new RaptorNodeRecord(r.Idx, r.Layer, leaf ? RaptorTreePolicy.LeafText(r.Title, r.Body) : r.Body,
                    VectorMath.FromBlob(r.Vec!), r.Tokens, children.TryGetValue(r.Idx, out var cs) ? cs.ToArray() : [],
                    leaf ? r.Ref : null, 0, leaf ? r.Kind : null, leaf ? r.Source : null,
                    (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, r.Seq));
            }
            int numLayers = Math.Min(meta.NumLayers, nodes.Count == 0 ? 0 : nodes.Max(n => n.Layer));
            // A leaf dropped here is a doc whose text changed while it was out of the tree: the sync re-inserts it and that
            // insert is its one change. Counting the drop as well doubled every restart's edits toward the rebuild (B23).
            if (numLayers != meta.NumLayers)
                meta = meta with { NumLayers = numLayers };

            Publish(byIdx.Values.ToDictionary(r => r.Idx, r => r.Id));
            Orphans = droppedLeaves;
            return Task.FromResult<RaptorSnapshot?>(new RaptorSnapshot(meta, nodes));
        }
    }

    // ======================= apply =======================

    public Task ApplyAsync(RaptorChangeSet changes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var map = changes.ReplaceAll ? new Dictionary<int, long>() : new Dictionary<int, long>(_idxToDoc);
        int orphans = 0;
        _db.Tx(() =>
        {
            var m = changes.Meta;
            long now = AgentDb.ToUnixMs(DateTimeOffset.UtcNow);
            if (changes.ReplaceAll)
            {
                _db.Exec("DELETE FROM doc_edges WHERE parent IN (SELECT id FROM docs WHERE kind = $k)", ("$k", RagIndex.SummaryKind));
                _db.Exec("UPDATE docs SET raptor_idx = NULL, raptor_flags = 0, raptor_seq = 0 WHERE raptor_idx IS NOT NULL");
            }

            foreach (var idx in changes.Deletes)
            {
                var row = _db.Query("SELECT id, kind, level FROM docs WHERE raptor_idx = $i", r => (Id: r.GetInt64(0), Kind: r.GetString(1), Level: r.GetInt64(2)), ("$i", idx))
                    .FirstOrDefault();
                map.Remove(idx);
                if (row.Id == 0) continue;
                if (row.Level > 0 && row.Kind == RagIndex.SummaryKind) _db.Exec("DELETE FROM docs WHERE id = $id", ("$id", row.Id));
                else
                {
                    _db.Exec("DELETE FROM doc_edges WHERE child = $id", ("$id", row.Id));
                    _db.Exec("UPDATE docs SET raptor_idx = NULL, raptor_flags = 0 WHERE id = $id", ("$id", row.Id));
                }
            }

            var summaryEdges = new List<(long Parent, int[] Children)>();
            foreach (var r in changes.Upserts.OrderBy(r => r.Layer).ThenBy(r => r.Index))
            {
                if (FailOn?.Invoke(r) == true) throw new InvalidOperationException($"injected store failure at node #{r.Index}");
                if (r.Layer == 0)
                {
                    if (r.Key == null) { orphans++; continue; }
                    _db.Exec("UPDATE docs SET raptor_idx = NULL WHERE raptor_idx = $i AND ref <> $k", ("$i", r.Index), ("$k", r.Key));
                    long id = _db.ScalarLong("SELECT id FROM docs WHERE ref = $k AND level = 0", ("$k", r.Key));
                    if (id == 0) { orphans++; map.Remove(r.Index); continue; }
                    // The leaf is synced when its text is the doc's current text (else the doc changed again meanwhile).
                    _db.Exec("""
                        UPDATE docs SET raptor_idx = $i, raptor_flags = $f, raptor_seq = $s, token_count = $tok,
                            dirty = CASE WHEN title || char(10) || body = $text THEN 0 ELSE dirty END
                        WHERE id = $id
                        """, ("$i", r.Index), ("$f", Flags(r)), ("$s", r.InsertedSeq), ("$tok", r.TokenCount), ("$text", r.Text), ("$id", id));
                    // Vectors normally exist already (the flat index embedded the doc); write one only if missing.
                    _db.Exec("""
                        INSERT OR REPLACE INTO vectors(doc_id, model, dims, hash, vec)
                        SELECT d.id, $m, $d, d.hash, $v FROM docs d
                        WHERE d.id = $id AND d.title || char(10) || d.body = $text
                          AND NOT EXISTS (SELECT 1 FROM vectors v WHERE v.doc_id = d.id AND v.model = $m AND v.dims = $d AND v.hash = d.hash)
                        """, ("$id", id), ("$m", m.EmbeddingModel), ("$d", m.Dimensions), ("$v", VectorMath.ToBlob(r.Embedding)), ("$text", r.Text));
                    map[r.Index] = id;
                }
                else
                {
                    string reference = SummaryRefPrefix + r.Index;
                    string title = $"summary L{r.Layer}";
                    string hash = TextTokens.MaterialHash(title, r.Text);
                    _db.Exec("UPDATE docs SET raptor_idx = NULL WHERE raptor_idx = $i AND ref <> $r", ("$i", r.Index), ("$r", reference));
                    _db.Exec("""
                        INSERT INTO docs(ref, kind, tag, title, body, hash, source, embed, weight, created, updated, level, token_count, dirty,
                            raptor_idx, raptor_flags, raptor_seq)
                        VALUES ($r, $k, $tag, $title, $body, $h, 'raptor', 0, 1.0, $t, $t, $l, $tok, $dirty, $i, $f, 0)
                        ON CONFLICT(ref) DO UPDATE SET kind = excluded.kind, tag = excluded.tag, title = excluded.title, body = excluded.body,
                            hash = excluded.hash, source = excluded.source, embed = 0, level = excluded.level, token_count = excluded.token_count,
                            dirty = excluded.dirty, raptor_idx = excluded.raptor_idx, raptor_flags = excluded.raptor_flags, updated = excluded.updated
                        """, ("$r", reference), ("$k", RagIndex.SummaryKind), ("$tag", "L" + r.Layer), ("$title", title), ("$body", r.Text), ("$h", hash),
                        ("$t", now), ("$l", r.Layer), ("$tok", r.TokenCount), ("$dirty", r.Dirty || r.SummaryPending ? 1 : 0), ("$i", r.Index), ("$f", Flags(r)));
                    long id = _db.ScalarLong("SELECT id FROM docs WHERE ref = $r", ("$r", reference));
                    _db.Exec("INSERT OR REPLACE INTO vectors(doc_id, model, dims, hash, vec) VALUES ($id, $m, $d, $h, $v)",
                        ("$id", id), ("$m", m.EmbeddingModel), ("$d", m.Dimensions), ("$h", hash), ("$v", VectorMath.ToBlob(r.Embedding)));
                    map[r.Index] = id;
                    summaryEdges.Add((id, r.Children));
                }
            }

            foreach (var (parent, kids) in summaryEdges)
            {
                _db.Exec("DELETE FROM doc_edges WHERE parent = $p", ("$p", parent));
                // Only to a child doc that still exists: another writer of the shared db (a clone, the groomer's merge) may have
                // deleted it since it was mapped, and INSERT OR IGNORE does not ignore a foreign key (live 10-29, task #103).
                foreach (var c in kids)
                    if (map.TryGetValue(c, out var child))
                        _db.Exec("INSERT OR IGNORE INTO doc_edges(parent, child, weight) SELECT $p, id, 1.0 FROM docs WHERE id = $c", ("$p", parent), ("$c", child));
            }
            if (changes.ReplaceAll)
                _db.Exec("DELETE FROM docs WHERE kind = $k AND raptor_idx IS NULL", ("$k", RagIndex.SummaryKind));
            _db.SetMeta(MetaKey, JsonSerializer.Serialize(m, Json));
        });
        Orphans = orphans;
        Publish(map);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tree nodes mapped to a doc that no longer carries that node: deleted or replaced (delete + insert under the same ref) by
    /// another writer of the shared db, which raises no <see cref="RagIndex.Deleted"/> event in this process. The sync removes
    /// these leaves before anything re-upserts their parents.
    /// </summary>
    public IReadOnlyList<int> VanishedNodes()
    {
        var map = _idxToDoc;
        if (map.Count == 0) return [];
        var live = new HashSet<(long, int)>(_db.Query("SELECT id, raptor_idx FROM docs WHERE raptor_idx IS NOT NULL",
            r => (r.GetInt64(0), (int)r.GetInt64(1))));
        var gone = new List<int>();
        foreach (var (idx, id) in map)
            if (!live.Contains((id, idx))) gone.Add(idx);
        return gone;
    }

    /// <summary>Forgets the stored tree (summary docs, node mapping, metadata); leaf docs stay.</summary>
    public void Clear()
    {
        _db.Tx(() =>
        {
            _db.Exec("DELETE FROM docs WHERE kind = $k", ("$k", RagIndex.SummaryKind));
            _db.Exec("UPDATE docs SET raptor_idx = NULL, raptor_flags = 0, raptor_seq = 0, dirty = 1 WHERE raptor_idx IS NOT NULL");
            _db.Exec("DELETE FROM meta WHERE key = $k", ("$k", MetaKey));
        });
        Publish(new Dictionary<int, long>());
    }

    private void Publish(Dictionary<int, long> map)
    {
        _treeDocs = map.Values.ToHashSet();
        _idxToDoc = map;
    }
}
