using System.Globalization;
using System.Text.Json;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Rag.Beliefs;
using MineClaw.Agent.Rules;
using MineClaw.Agent.Runtime;
using MineClaw.Agent.Self;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Admin.Shards;

/// <summary>
/// Imports a shard into a db through its own stores (the same API the bot's tools use, so FTS triggers, vectors, in-memory
/// mirrors and RAPTOR dirty flags stay consistent): a local file (<see cref="MemoryAdminContext.OpenLocal"/>) or the running bot
/// (<see cref="MemoryAdminContext.FromRuntime"/>).
/// <list type="bullet">
/// <item><see cref="Preview"/>: the diff (added / changed / conflicts / removed / unchanged / skipped), the vectors it can reuse, the
/// scoped RAPTOR rebuilds and their estimate. Writes nothing.</item>
/// <item><see cref="ApplyAsync"/>: merge (dedupe by stable id; conflicts by policy), replace-section (remove the target's records that
/// match the shard's selection, then insert) or swap-pack (unload the version-tagged game data, load the shard's, set the active
/// pack). Vectors of the same model + dims are reused (embed_cache), the rest re-embedded; the tree takes the new leaves (zero
/// tokens) and the returned rebuild requests re-cluster/re-summarize only the kinds that changed. One audit entry.</item>
/// </list>
/// Never touched: observations/containers (world + inventory ledger), world chunks, build plans, chat, routines. Places and
/// structures only with <see cref="ShardImportOptions.IncludeWorld"/>; standing rules only with
/// <see cref="ShardImportOptions.IncludeRules"/> and only from players trusted by the target.
/// </summary>
public sealed class ShardImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxUnchangedItems = 200;

    private readonly MemoryAdminContext _ctx;
    private readonly AdminAudit _audit;
    private readonly string _target;
    private readonly ILogger _log;

    public ShardImporter(MemoryAdminContext ctx, AdminAudit audit, string target)
    {
        _ctx = ctx;
        _audit = audit;
        _target = target;
        _log = ctx.Logger("MineClaw.Admin");
    }

    private sealed record PlanItem(ShardRecord Record, ShardRecord? Existing, string Change, bool Apply);
    private sealed record Plan(ShardDiff Diff, List<PlanItem> Writes, List<ShardRecord> Removals, bool ReuseVectors);

    public ShardDiff Preview(Shard shard, ShardImportOptions o) => MakePlan(shard, o).Diff;

    /// <summary>The shard's summaries cache for <see cref="RaptorSectionRebuilder.ReuseSummaries"/>.</summary>
    public static IReadOnlyDictionary<string, string> ReuseMap(Shard shard) =>
        shard.Summaries.GroupBy(s => s.ContextHash, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.Ordinal);

    private DirectiveStore Rules()
    {
        var rules = DirectiveStore.For(_ctx.Db, _ctx.Rag);
        if (rules.IsTrusted == null)
        {
            var trusted = _ctx.Options.Self.TrustedPlayers;
            rules.IsTrusted = p => trusted.Any(t => t.Equals(p, StringComparison.OrdinalIgnoreCase));
        }
        return rules;
    }

    private Plan MakePlan(Shard shard, ShardImportOptions o)
    {
        var m = shard.Manifest;
        var notes = new List<string>();
        var active = KnowledgePack.Active(_ctx.Db);
        string? refused = null;
        bool isPack = m.Kind == "pack";
        string? mode = isPack ? m.Pack?.Mode ?? m.Selection.PackMode : null;
        bool isMode = mode != null;
        IReadOnlyList<string> packFiles = PackFiles(m);
        bool modeOp = o.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode;
        var collector = new ShardCollector(_ctx.Db);
        if (o.Mode == ShardImportMode.SwapPack && !isPack) refused = "swap needs a knowledge-pack shard; this one is a " + m.Kind + " shard";
        else if (o.Mode == ShardImportMode.SwapPack && isMode) refused = $"this is the {mode} mode pack: add or remove it; swap takes a version pack";
        else if (modeOp && !isMode) refused = $"{ShardImportOptions.Name(o.Mode)} needs a mode pack (e.g. skyblock); this one is a " + (isPack ? "version pack: swap it" : m.Kind + " shard");
        else if (isMode && !modeOp) refused = $"the {mode} mode pack is added or removed, not {ShardImportOptions.Name(o.Mode)}: choose add mode or remove mode";
        else if (isPack && o.Mode == ShardImportMode.ReplaceSection) refused = "a knowledge pack is swapped, not section-replaced: choose swap";
        else if (isPack && o.Mode == ShardImportMode.Merge && m.PackVersion is { } pv && !pv.Equals(active.Version, StringComparison.OrdinalIgnoreCase)
                 && collector.All(ShardKinds.GameData).Count > 0)
            refused = $"this is a {pv} knowledge pack and the db holds {active.Version}: merging would mix versions; swap it instead";
        else if (isPack && !KnowledgePack.ValidVersion(m.PackVersion)) refused = "the knowledge pack has no valid version tag";

        var rules = Rules();
        var shardIds = shard.Records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var existing = new Dictionary<string, Dictionary<string, ShardRecord>>(StringComparer.Ordinal);
        Dictionary<string, ShardRecord> Target(string kind)
        {
            if (!existing.TryGetValue(kind, out var map))
                existing[kind] = map = collector.All(kind).GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            return map;
        }

        bool replace = o.Mode is ShardImportMode.ReplaceSection or ShardImportMode.SwapPack or ShardImportMode.AddMode;
        var items = new List<ShardDiffItem>();
        var writes = new List<PlanItem>();
        int added = 0, changed = 0, conflicts = 0, unchanged = 0, skipped = 0;
        // Removing a mode writes nothing: it only unloads the mode's chunks.
        foreach (var r in o.Mode == ShardImportMode.RemoveMode ? [] : ShardFile.Canonical(shard.Records))
        {
            string? skip = r.Kind switch
            {
                _ when ShardKinds.IsWorld(r.Kind) && !o.IncludeWorld => "world memory not chosen",
                ShardKinds.Rule when !o.IncludeRules => "standing rules not chosen",
                ShardKinds.Rule when !rules.Trusted(r.Get("player")) => $"{r.Get("player")} isn't a trusted player here",
                ShardKinds.Trait when _ctx.Self == null => "the self profile is off in the target",
                _ => null,
            };
            if (skip != null)
            {
                skipped++;
                items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Skipped, r.Label, false, skip));
                continue;
            }
            var mine = Target(r.Kind).GetValueOrDefault(r.Id);
            if (mine == null)
            {
                added++;
                writes.Add(new PlanItem(r, null, ShardChange.Added, true));
                items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Added, r.Label, true));
            }
            else if (mine.Hash == r.Hash)
            {
                unchanged++;
                if (unchanged <= MaxUnchangedItems) items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Unchanged, r.Label, false));
            }
            else if (r.Updated > mine.Updated)
            {
                changed++;
                bool apply = replace || o.Conflict != ShardConflictPolicy.KeepMine;
                writes.Add(new PlanItem(r, mine, ShardChange.Changed, apply));
                items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Changed, r.Label, apply, "was: " + mine.Label));
            }
            else
            {
                conflicts++;
                bool apply = replace || o.Conflict == ShardConflictPolicy.PreferShard;
                writes.Add(new PlanItem(r, mine, ShardChange.Conflict, apply));
                items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Conflict, r.Label, apply,
                    (apply ? "shard wins over the newer local: " : "keeping the newer local: ") + mine.Label));
            }
        }

        var removals = new List<ShardRecord>();
        if (o.Mode == ShardImportMode.ReplaceSection && refused == null)
        {
            var sel = m.Selection with
            {
                IncludeRules = m.Selection.IncludeRules && o.IncludeRules,
                IncludeWorld = m.Selection.IncludeWorld && o.IncludeWorld,
            };
            if (sel.Mode == "subtree")
                notes.Add("a RAPTOR subtree has no section in another db: its records are replaced, nothing else is removed");
            else
            {
                if (sel.Semantic && !string.IsNullOrWhiteSpace(sel.Query)) notes.Add("the semantic selection is re-applied here as a text filter");
                try { removals.AddRange(collector.Select(sel with { Semantic = false }).Where(r => !shardIds.Contains(r.Id))); }
                catch (ArgumentException ex) { refused = ex.Message; }
            }
        }
        else if (o.Mode == ShardImportMode.SwapPack && refused == null)
        {
            removals.AddRange(collector.All(ShardKinds.GameData).Where(r => !shardIds.Contains(r.Id)));
            // The outgoing version pack's docs and the incoming one's files: chunks the new pack doesn't carry go.
            var files = packFiles.Concat(active.Files ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count > 0) removals.AddRange(collector.All(ShardKinds.Knowledge).Where(r => ShardCollector.InFiles(r, files) && !shardIds.Contains(r.Id)));
            else if (m.Selection.IncludeKnowledgeDocs) notes.Add("knowledge chunks in the pack are merged; docs/knowledge files re-index them when they change");
        }
        else if (modeOp && refused == null)
        {
            removals.AddRange(collector.All(ShardKinds.Knowledge)
                .Where(r => (ShardCollector.InFiles(r, packFiles) || shardIds.Contains(r.Id)) && (o.Mode == ShardImportMode.RemoveMode || !shardIds.Contains(r.Id))));
        }

        // Active modes before / after (a legacy db has no list: every mode doc on disk counted until now).
        var modesFrom = active.Modes?.Select(x => x.Mode).ToList();
        List<string>? modesTo = modesFrom;
        if (isMode && refused == null && modeOp)
        {
            var list = (modesFrom ?? []).Where(x => !x.Equals(mode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (o.Mode == ShardImportMode.AddMode) list.Add(mode!);
            modesTo = list;
            if (modesFrom == null)
                notes.Add($"this db had no mode list (every mode doc on disk counted); after this its modes are exactly: {Modes(modesTo)}");
            if (o.Mode == ShardImportMode.AddMode && m.Pack?.Version is { } mv && !KnowledgePack.Matches(mv, active.Version))
                notes.Add($"the {mode} pack was written for {mv}; this db plays {active.Version}, so its docs stay out until the versions match");
            notes.Add(o.Mode == ShardImportMode.AddMode
                ? $"mode {mode} on: its core lines join the cached prompt and its {packFiles.Count} doc(s) join retrieval"
                : $"mode {mode} off: its core lines leave the cached prompt and its chunks leave retrieval");
        }
        foreach (var r in removals) items.Add(new ShardDiffItem(r.Id, r.Kind, ShardChange.Removed, r.Label, true));

        // Vectors: reused when the target embeds with the shard's model + dims (or, offline, when its stored vectors are those).
        bool reuse = false;
        if (m.Embeddings is { } ei && shard.Embeddings.Count > 0)
        {
            var emb = _ctx.Embedder;
            reuse = emb.IsAvailable
                ? emb.Model == ei.Model && emb.Dimensions == ei.Dims
                : _ctx.Db.ScalarLong("SELECT count(*) FROM vectors WHERE model <> $m OR dims <> $d", ("$m", ei.Model), ("$d", ei.Dims)) == 0;
            if (!reuse) notes.Add($"the shard's vectors are {ei.Model}@{ei.Dims}, the target embeds with {emb.Model}@{emb.Dimensions}: its docs are re-embedded");
        }
        var applied = writes.Where(w => w.Apply).ToList();
        int reusable = reuse ? applied.Count(w => w.Record.DocHash is { } h && shard.Embeddings.ContainsKey(h)) : 0;
        int embeddable = applied.Count(w => w.Record.Kind != ShardKinds.Trait);
        int toEmbed = Math.Max(0, embeddable - reusable);

        // Scoped rebuilds: only tree kinds that actually change.
        var rebuild = new List<RebuildRequest>();
        RebuildEstimate? estimate = null;
        if (_ctx.Raptor != null && refused == null)
        {
            var kinds = applied.Select(w => w.Record.Kind).Concat(removals.Select(r => r.Kind)).Distinct()
                .Select(ShardKinds.RebuildKind).OfType<string>().Distinct().ToList();
            rebuild.AddRange(kinds.Select(k => new RebuildRequest("kind", Kind: k, MaxUsd: o.RebuildMaxUsd, Who: o.Who)));
            if (rebuild.Count > 0) estimate = Estimate(rebuild, applied, reusable);
        }
        else if (_ctx.Raptor == null && applied.Count > 0) notes.Add("no RAPTOR tree here (" + (_ctx.RaptorNote ?? "off") + "): nothing to rebuild");

        if (isPack) notes.Add($"knowledge pack {m.PackVersion}: the db holds {active.Version}" + (o.Mode == ShardImportMode.SwapPack ? $" → {m.PackVersion} after the swap" : ""));
        if (isPack && _ctx.IsLive && o.Mode is ShardImportMode.SwapPack or ShardImportMode.AddMode or ShardImportMode.RemoveMode)
            notes.Add("retrieval switches now; the bot's cached prompt (core lines, version) follows after its next restart");
        if (m.Pack is { NeedsEmbedding: > 0 } np && o.Mode != ShardImportMode.RemoveMode)
            notes.Add($"{np.NeedsEmbedding} pack doc(s) carry no vector: they are embedded on import");
        if (m.Summaries is { Count: > 0 } && rebuild.Count > 0) notes.Add($"{m.Summaries.Count} cached summaries: identical clusters are reused without a model call");
        if (applied.Count == 0 && removals.Count == 0 && refused == null) notes.Add("identical to what's here: nothing to write, no rebuild");

        var diff = new ShardDiff
        {
            Shard = m.Name, Mode = o.Mode, Conflict = o.Conflict, Items = items,
            Added = added, Changed = changed, Conflicts = conflicts, Removed = removals.Count, Unchanged = unchanged, Skipped = skipped,
            Writes = applied.Count, Rebuild = rebuild, Estimate = estimate, ReusableVectors = reusable, ToEmbed = toEmbed,
            PackFrom = isPack ? active.Version : null, PackTo = isPack ? (isMode ? active.Version : m.PackVersion) : null,
            ModesFrom = isPack ? modesFrom : null, ModesTo = isPack ? modesTo : null, Notes = notes, Refused = refused,
        };
        return new Plan(diff, applied, removals, reuse);
    }

    /// <summary>The queued kind rebuilds priced now, plus embedding the docs that bring no reusable vector.</summary>
    private RebuildEstimate Estimate(IReadOnlyList<RebuildRequest> rebuild, IReadOnlyList<PlanItem> applied, int reusable)
    {
        var rb = new RaptorSectionRebuilder(_ctx, _audit, _target);
        int summaries = 0;
        long input = 0, output = 0, embed = 0;
        decimal usd = 0;
        string model = _ctx.Options.Llm.CheapModel;
        foreach (var r in rebuild)
        {
            var e = rb.Estimate(r);
            summaries += e.Summaries; input += e.InputTokens; output += e.OutputTokens; embed += e.EmbeddingTokens; usd += e.Usd;
            model = e.Model;
        }
        // Docs without a reusable vector: their text once through the embedder (≈ chars / 4).
        long newTokens = applied.Where(w => w.Record.Kind != ShardKinds.Trait).Skip(reusable)
            .Sum(w => (long)(w.Record.Fields.Values.Sum(v => v?.Length ?? 0) / 4));
        embed += newTokens;
        usd += newTokens * _ctx.Options.Embeddings.PricePerMTok / 1_000_000m;
        return new RebuildEstimate(summaries, input, output, embed, Math.Round(usd, 6), model,
            $"{rebuild.Count} section rebuild(s): {string.Join(", ", rebuild.Select(r => r.Kind))}; ~{summaries} summaries");
    }

    // ======================= apply =======================

    public async Task<ShardImportResult> ApplyAsync(Shard shard, ShardImportOptions o, CancellationToken ct = default)
    {
        var who = string.IsNullOrWhiteSpace(o.Who) ? "admin" : o.Who.Trim();
        ShardImportResult result;
        await _ctx.WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var plan = MakePlan(shard, o);
            if (plan.Diff.Refused is { } why) result = new ShardImportResult { Ok = false, Message = "refused: " + why, Diff = plan.Diff };
            else if (_ctx.ReadOnly) result = new ShardImportResult { Ok = false, Message = "the database is open read-only; nothing was changed", Diff = plan.Diff };
            else result = await ApplyPlanAsync(shard, plan, o, who, ct).ConfigureAwait(false);
        }
        finally { _ctx.WriteGate.Release(); }

        var m = shard.Manifest;
        var d = result.Diff;
        var after = JsonSerializer.Serialize(new
        {
            content_hash = m.ContentHash, kind = m.Kind, mode = ShardImportOptions.Name(o.Mode), conflict = ShardImportOptions.Name(o.Conflict),
            added = d?.Added, changed = d?.Changed, conflicts = d?.Conflicts, removed = result.Removed, written = result.Written,
            reused_vectors = result.ReusedVectors, embedded = result.Embedded, pack = result.ActivePack,
            rebuild = result.Rebuild.Select(r => r.Kind).ToList(),
        }, AdminAudit.Json);
        _audit.Write(new AuditEntry(DateTimeOffset.UtcNow, who, _target, "import", "shard", m.Name, null, result.Ok ? after : null,
            m.Description, result.Ok, result.Message));
        return result;
    }

    private async Task<ShardImportResult> ApplyPlanAsync(Shard shard, Plan plan, ShardImportOptions o, string who, CancellationToken ct)
    {
        var m = shard.Manifest;
        var errors = new List<string>();
        int written = 0, removed = 0;
        bool lessons = false;

        // 1) Vectors (and cached summary vectors) into embed_cache: the flush below then reuses them without an API call.
        if (plan.ReuseVectors && m.Embeddings is { } ei)
        {
            long now = AgentDb.ToUnixMs(DateTimeOffset.UtcNow);
            _ctx.Db.Tx(() =>
            {
                foreach (var (hash, vec) in shard.Embeddings)
                    _ctx.Db.Exec("INSERT OR IGNORE INTO embed_cache(hash, model, dims, vec, created) VALUES ($h, $m, $d, $v, $t)",
                        ("$h", hash), ("$m", ei.Model), ("$d", ei.Dims), ("$v", VectorMath.ToBlob(vec)), ("$t", now));
            });
        }

        // 2) Removals (replace-section / swap), then 3) writes, each through its store.
        foreach (var r in plan.Removals)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Remove(r)) removed++;
                lessons |= r.Kind == ShardKinds.Lesson;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"remove {r.Id}: {ex.Message}"); }
        }
        var source = $"shard {m.Name}";
        foreach (var w in plan.Writes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Write(w.Record, w.Existing, who, source)) written++;
                lessons |= w.Record.Kind == ShardKinds.Lesson;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{w.Record.Id}: {ex.Message}"); }
        }
        if (lessons) KnowledgeCorrections.Apply(_ctx.Rag, _ctx.Options.KnowledgeDirectory, _ctx.Logger("MineClaw.Rag"));

        string? pack = null;
        var installed = DateTimeOffset.UtcNow;
        var pi = m.Pack;
        var packFiles = PackFiles(m);
        if (o.Mode == ShardImportMode.SwapPack && m.PackVersion is { } pv)
        {
            var before = KnowledgePack.Active(_ctx.Db);
            KnowledgePack.SetActive(_ctx.Db, new KnowledgePackInfo(pv, pi?.Name ?? m.Name, installed, m.ContentHash)
            {
                Modes = before.Modes, Core = pi?.Core, Files = packFiles.Count > 0 ? packFiles : null,
            });
            pack = pv;
        }
        else if (o.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode && (pi?.Mode ?? m.Selection.PackMode) is { } mode)
        {
            var before = KnowledgePack.Active(_ctx.Db);
            var modes = (before.Modes ?? []).Where(x => !x.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (o.Mode == ShardImportMode.AddMode)
                modes.Add(new KnowledgeModeInfo(mode, pi?.Name ?? m.Name, pi?.Version ?? m.PackVersion, installed, m.ContentHash)
                {
                    Core = pi?.Core, Files = packFiles.Count > 0 ? packFiles : null,
                });
            KnowledgePack.SetActive(_ctx.Db, before with { Modes = modes });
            pack = before.Version;
        }
        else if (m.Kind == "pack") pack = KnowledgePack.ActiveVersion(_ctx.Db);
        // The pack's doc files re-sync with docs/knowledge at the next start (the files on disk stay the editable truth).
        if (pack != null && packFiles.Count > 0 && o.Mode is ShardImportMode.SwapPack or ShardImportMode.AddMode or ShardImportMode.RemoveMode)
            foreach (var file in packFiles)
                try { _ctx.Db.Exec("DELETE FROM knowledge_files WHERE path = $p", ("$p", file)); }
                catch (Microsoft.Data.Sqlite.SqliteException) { /* an older db without the table */ }

        // 4) Embeddings: cache hits for reused vectors, the provider for the rest (offline: shard vectors go straight in).
        int reused = 0, embedded = 0;
        var hashes = plan.Writes.Select(w => w.Record.DocHash).OfType<string>().Where(shard.Embeddings.ContainsKey).ToHashSet(StringComparer.Ordinal);
        try
        {
            if (_ctx.Rag.Embedder.IsAvailable)
            {
                int n = await _ctx.Rag.FlushEmbeddingsAsync(ct).ConfigureAwait(false);
                reused = plan.ReuseVectors ? CountCurrent(hashes) : 0;
                embedded = Math.Max(0, n - reused);
            }
            else if (plan.ReuseVectors && m.Embeddings is { } ei2)
            {
                _ctx.Db.Tx(() =>
                {
                    foreach (var h in hashes)
                        reused += _ctx.Db.Exec("""
                            INSERT OR REPLACE INTO vectors(doc_id, model, dims, hash, vec)
                            SELECT id, $m, $d, hash, $v FROM docs WHERE hash = $h AND embed = 1 AND level = 0
                            """, ("$m", ei2.Model), ("$d", ei2.Dims), ("$h", h), ("$v", VectorMath.ToBlob(shard.Embeddings[h])));
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add("embedding deferred: " + ex.Message); }

        // 5) The tree takes the new leaves now (zero tokens; the live bot's maintenance loop does it itself).
        string tree = "";
        try
        {
            if (_ctx.RaptorMaintenance != null) _ctx.RaptorMaintenance.Signal();
            else if (_ctx.Raptor is { Ready: true } raptor)
            {
                int ins = 0, rem = 0;
                for (int i = 0; i < 1000; i++)
                {
                    var s = await raptor.SyncAsync(ct).ConfigureAwait(false);
                    if (!s.Any) break;
                    ins += s.Inserted + s.Reinserted;
                    rem += s.Removed;
                }
                if (ins + rem > 0) tree = $"; tree +{ins} -{rem} leaves";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add("tree sync deferred: " + ex.Message); }

        var d = plan.Diff;
        var msg = $"imported {m.Name} ({ShardImportOptions.Name(o.Mode)}): {written} written ({d.Added} new), {removed} removed, " +
                  $"{d.Unchanged} unchanged, {d.Skipped} skipped; vectors {reused} reused, {embedded} embedded{tree}" +
                  (pack != null && o.Mode == ShardImportMode.SwapPack ? $"; knowledge pack {d.PackFrom} → {pack}" : "") +
                  (pack != null && o.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode ? $"; modes {Modes(d.ModesFrom)} → {Modes(d.ModesTo)}" : "") +
                  (errors.Count > 0 ? $"; {errors.Count} failed: {string.Join("; ", errors.Take(3))}" : "");
        _log.LogInformation("admin shard {Message}", msg);
        return new ShardImportResult
        {
            Ok = errors.Count == 0 || written + removed > 0, Message = msg, Diff = d, Written = written, Removed = removed, ReusedVectors = reused,
            Embedded = embedded, Rebuild = d.Rebuild, ActivePack = pack,
        };
    }

    private static string Modes(IReadOnlyList<string>? m) => m == null ? "(every mode doc on disk)" : m.Count == 0 ? "none" : string.Join(", ", m);

    /// <summary>The knowledge doc files a pack carries (manifest pack info, else the selection's file list).</summary>
    private static IReadOnlyList<string> PackFiles(ShardManifest m) =>
        m.Pack?.Files is { Count: > 0 } pf ? pf : m.Selection.KnowledgeFiles ?? [];

    /// <summary>How many of these material hashes now have a current vector.</summary>
    private int CountCurrent(HashSet<string> hashes)
    {
        int n = 0;
        foreach (var h in hashes)
            n += (int)_ctx.Db.ScalarLong("SELECT count(*) FROM docs d JOIN vectors v ON v.doc_id = d.id AND v.hash = d.hash WHERE d.hash = $h AND d.level = 0", ("$h", h));
        return n;
    }

    private static long RowId(string? reference, string prefix) =>
        reference != null && reference.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(reference[prefix.Length..], NumberStyles.Integer, Inv, out var id)
            ? id : throw new InvalidOperationException($"no local row for {reference}");

    private bool Remove(ShardRecord r)
    {
        var mem = _ctx.Memory;
        switch (r.Kind)
        {
            case ShardKinds.Fact: return mem.Forget("note", RowId(r.Ref, "note:").ToString(Inv)) > 0;
            case ShardKinds.Claim: return _ctx.Beliefs.AdminDelete(RowId(r.Ref, "claim:"));
            case ShardKinds.Rule: return Rules().Revoke(RowId(r.Ref, "rule:"), "shard import");
            case ShardKinds.Trait:
                if (_ctx.Self is not { } self || !SelfProfile.TryParseSection(r.Get("section"), out var sec)) return false;
                return self.Apply(new SelfChange("remove", sec, Id: RowId(r.Ref, "self#"), Reason: "replaced by a shard import"), SelfProfile.SourceAdmin).Ok;
            case ShardKinds.Lesson: return KnowledgeCorrections.DeleteAdmin(_ctx.Db, r.Get("id")!);
            case ShardKinds.Place: return mem.Forget("waypoint", r.Get("name")!) > 0;
            case ShardKinds.Structure: return mem.Forget("structure", r.Get("name")!) > 0;
            default: return _ctx.Rag.Delete(r.Id);
        }
    }

    private static double? Num(string? s) => double.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : null;
    private static int Int(string? s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : 0;

    /// <summary>One record through its store. False when the store declined it (a duplicate it already holds counts as written).</summary>
    private bool Write(ShardRecord r, ShardRecord? existing, string who, string source)
    {
        var mem = _ctx.Memory;
        switch (r.Kind)
        {
            case ShardKinds.Fact:
                mem.AddNote(r.Get("subject") ?? "world", r.Get("text") ?? "");
                return true;
            case ShardKinds.Claim:
                return WriteClaim(r, existing, who);
            case ShardKinds.Rule:
                return Rules().Add(r.Get("player")!, r.Get("text")!, r.Created).Stored;
            case ShardKinds.Trait:
            {
                if (_ctx.Self is not { } self || !SelfProfile.TryParseSection(r.Get("section"), out var sec)) return false;
                if (existing != null) return true; // same id = same section/player/text: already here
                var reason = r.Info?.GetValueOrDefault("reason") is { Length: > 0 } rs ? rs : "imported from " + source;
                var res = self.Apply(new SelfChange("add", sec, r.Get("text"), r.Get("player") is { Length: > 0 } p ? p : null, Reason: reason), SelfProfile.SourceAdmin);
                if (!res.Ok) throw new InvalidOperationException(res.Message);
                return true;
            }
            case ShardKinds.Lesson:
            {
                var c = KnowledgeCorrections.FromRow(r.Get("id")!, r.Get("title")!, r.Get("correction")!, r.Get("patterns")!);
                KnowledgeCorrections.SaveAdmin(_ctx.Db, c);
                return true;
            }
            case ShardKinds.Place:
                mem.SetWaypoint(r.Get("name")!, Int(r.Get("x")), Int(r.Get("y")), Int(r.Get("z")), r.Get("note"), r.Dimension ?? 0);
                if (r.Get("note") == null && existing?.Get("note") != null) mem.EditWaypoint(r.Get("name")!, w => w.Note = null);
                return true;
            case ShardKinds.Structure:
            {
                var data = JsonSerializer.Deserialize<Structure>(r.Get("data")!, AtomicFile.Json) ?? throw new InvalidOperationException("unreadable structure data");
                var name = r.Get("name")!;
                mem.UpsertStructure(name, s =>
                {
                    s.Name = name;
                    s.Kind = data.Kind;
                    s.Min = data.Min;
                    s.Max = data.Max;
                    s.Dimension = r.Dimension ?? data.Dimension;
                    s.KeyBlocks = data.KeyBlocks;
                    s.Steps = data.Steps;
                    s.TaughtBy = data.TaughtBy;
                    s.Confidence = data.Confidence;
                    s.Note = data.Note;
                });
                return true;
            }
            default:
                _ctx.Rag.Upsert(ToDoc(r));
                return true;
        }
    }

    private bool WriteClaim(ShardRecord r, ShardRecord? existing, string who)
    {
        var b = _ctx.Beliefs;
        var status = Enum.TryParse<ClaimStatus>(r.Get("status"), true, out var st) ? st : ClaimStatus.Unverified;
        double? conf = Num(r.Get("confidence"));
        var reason = r.Get("reason");
        long id;
        if (existing != null) id = RowId(existing.Ref, "claim:");
        else
        {
            BeliefClaim? claim = null;
            var claimants = (r.Get("claimants") ?? r.Get("claimant") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var c in claimants.Length > 0 ? claimants : [r.Get("claimant") ?? "unknown"])
            {
                var res = b.Ingest(c, r.Get("text") ?? "");
                if (!res.Stored || res.Claim == null) throw new InvalidOperationException(res.Text);
                claim = res.Claim;
            }
            if (claim == null) return false;
            id = claim.Id;
            if (claim.Status == status && (conf == null || Math.Abs(claim.Confidence - conf.Value) < 0.001) && claim.Reason == reason) return true;
        }
        return b.AdminSet(id, status, conf, reason, "shard:" + who) != null;
    }

    private static RagDoc ToDoc(ShardRecord r)
    {
        Box? bounds = null;
        if (r.Get("bounds") is { Length: > 0 } bs)
        {
            var p = bs.Split(',').Select(x => double.Parse(x, NumberStyles.Float, Inv)).ToArray();
            if (p.Length == 6) bounds = new Box(p[0], p[1], p[2], p[3], p[4], p[5]);
        }
        return new RagDoc(r.Id, r.Get("kind") ?? r.Kind, r.Get("title") ?? "", r.Get("body") ?? "")
        {
            Tag = r.Get("tag"), Source = r.Get("source"), Weight = Num(r.Get("weight")) ?? 1.0, Embed = r.Get("embed") != "0",
            Bounds = bounds, Dimension = r.Dimension ?? 0, At = r.Updated,
        };
    }
}
