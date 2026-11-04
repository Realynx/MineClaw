using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Runtime;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Admin.Shards;

public sealed partial class ShardImporter
{
    private async Task<ShardImportResult> ApplyPlanAsync(Shard shard, Plan plan, ShardImportOptions o, string who, CancellationToken ct)
    {
        var m = shard.Manifest;
        var errors = new List<string>();
        CacheShardVectors(shard, plan);
        var (written, removed) = ApplyRecords(m, plan, who, errors, ct);
        var packFiles = PackFiles(m);
        string? pack = SetActivePack(m, o, packFiles);
        // The pack's doc files re-sync with docs/knowledge at the next start (the files on disk stay the editable truth).
        if (pack != null && packFiles.Count > 0 && o.Mode is ShardImportMode.SwapPack or ShardImportMode.AddMode or ShardImportMode.RemoveMode)
            ForgetFileSync(packFiles);
        var (reused, embedded) = await EmbedAsync(shard, plan, errors, ct).ConfigureAwait(false);
        string tree = await SyncTreeAsync(errors, ct).ConfigureAwait(false);

        var d = plan.Diff;
        var msg = ImportMessage(m, o, d, written, removed, $"vectors {reused} reused, {embedded} embedded{tree}", pack, errors);
        _log.LogInformation("admin shard {Message}", msg);
        return new ShardImportResult
        {
            Ok = errors.Count == 0 || written + removed > 0, Message = msg, Diff = d, Written = written, Removed = removed, ReusedVectors = reused,
            Embedded = embedded, Rebuild = d.Rebuild, ActivePack = pack,
        };
    }

    /// <summary>1) Vectors (and cached summary vectors) into embed_cache: the flush below then reuses them without an API call.</summary>
    private void CacheShardVectors(Shard shard, Plan plan)
    {
        if (!plan.ReuseVectors || shard.Manifest.Embeddings is not { } ei) return;
        long now = AgentDb.ToUnixMs(DateTimeOffset.UtcNow);
        _ctx.Db.Tx(() =>
        {
            foreach (var (hash, vec) in shard.Embeddings)
                _ctx.Db.Exec("INSERT OR IGNORE INTO embed_cache(hash, model, dims, vec, created) VALUES ($h, $m, $d, $v, $t)",
                    ("$h", hash), ("$m", ei.Model), ("$d", ei.Dims), ("$v", VectorMath.ToBlob(vec)), ("$t", now));
        });
    }

    /// <summary>2) Removals (replace-section / swap), then 3) writes, each through its store. Lessons re-apply their corrections.</summary>
    private (int Written, int Removed) ApplyRecords(ShardManifest m, Plan plan, string who, List<string> errors, CancellationToken ct)
    {
        int written = 0, removed = 0;
        bool lessons = false;
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
        return (written, removed);
    }

    /// <summary>The active knowledge pack after this import (swap: the new version; a mode change: the mode list), null = untouched.</summary>
    private string? SetActivePack(ShardManifest m, ShardImportOptions o, IReadOnlyList<string> packFiles)
    {
        var installed = DateTimeOffset.UtcNow;
        if (o.Mode == ShardImportMode.SwapPack && m.PackVersion is { } pv)
        {
            SwapActivePack(m, pv, installed, packFiles);
            return pv;
        }
        if (o.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode && (m.Pack?.Mode ?? m.Selection.PackMode) is { } mode)
            return SetActiveModes(m, o, mode, installed, packFiles);
        return m.Kind == "pack" ? KnowledgePack.ActiveVersion(_ctx.Db) : null;
    }

    private void SwapActivePack(ShardManifest m, string version, DateTimeOffset installed, IReadOnlyList<string> packFiles)
    {
        var pi = m.Pack;
        var before = KnowledgePack.Active(_ctx.Db);
        KnowledgePack.SetActive(_ctx.Db, new KnowledgePackInfo(version, pi?.Name ?? m.Name, installed, m.ContentHash)
        {
            Modes = before.Modes, Core = pi?.Core, Files = packFiles.Count > 0 ? packFiles : null,
        });
    }

    private string SetActiveModes(ShardManifest m, ShardImportOptions o, string mode, DateTimeOffset installed, IReadOnlyList<string> packFiles)
    {
        var pi = m.Pack;
        var before = KnowledgePack.Active(_ctx.Db);
        var modes = (before.Modes ?? []).Where(x => !x.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (o.Mode == ShardImportMode.AddMode)
            modes.Add(new KnowledgeModeInfo(mode, pi?.Name ?? m.Name, pi?.Version ?? m.PackVersion, installed, m.ContentHash)
            {
                Core = pi?.Core, Files = packFiles.Count > 0 ? packFiles : null,
            });
        KnowledgePack.SetActive(_ctx.Db, before with { Modes = modes });
        return before.Version;
    }

    private void ForgetFileSync(IReadOnlyList<string> packFiles)
    {
        foreach (var file in packFiles)
            try { _ctx.Db.Exec("DELETE FROM knowledge_files WHERE path = $p", ("$p", file)); }
            catch (Microsoft.Data.Sqlite.SqliteException) { /* an older db without the table */ }
    }

    /// <summary>4) Embeddings: cache hits for reused vectors, the provider for the rest (offline: shard vectors go straight in).</summary>
    private async Task<(int Reused, int Embedded)> EmbedAsync(Shard shard, Plan plan, List<string> errors, CancellationToken ct)
    {
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
            else if (plan.ReuseVectors && shard.Manifest.Embeddings is { } ei2)
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
        return (reused, embedded);
    }

    /// <summary>5) The tree takes the new leaves now (zero tokens; the live bot's maintenance loop does it itself).</summary>
    private async Task<string> SyncTreeAsync(List<string> errors, CancellationToken ct)
    {
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
        return tree;
    }

    private static string ImportMessage(ShardManifest m, ShardImportOptions o, ShardDiff d, int written, int removed, string vectors, string? pack,
        List<string> errors) =>
        $"imported {m.Name} ({ShardImportOptions.Name(o.Mode)}): {written} written ({d.Added} new), {removed} removed, " +
        $"{d.Unchanged} unchanged, {d.Skipped} skipped; {vectors}" +
        (pack != null && o.Mode == ShardImportMode.SwapPack ? $"; knowledge pack {d.PackFrom} → {pack}" : "") +
        (pack != null && o.Mode is ShardImportMode.AddMode or ShardImportMode.RemoveMode ? $"; modes {Modes(d.ModesFrom)} → {Modes(d.ModesTo)}" : "") +
        (errors.Count > 0 ? $"; {errors.Count} failed: {string.Join("; ", errors.Take(3))}" : "");
}
