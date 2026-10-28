using MineClaw.Agent.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Hive.Hierarchy.Bus;

/// <summary>RagDoc ↔ bus DTO.</summary>
public static class MemDocs
{
    public static MemDocMsg ToMsg(RagDoc d) => new(d.Ref, d.Kind, d.Title, d.Body)
    {
        Tag = d.Tag, Source = d.Source, Dimension = d.Dimension, Embed = d.Embed, Weight = d.Weight, Level = d.Level,
        Bounds = d.Bounds is { } b ? [b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ] : null,
        AtMs = d.At?.ToUnixTimeMilliseconds(),
        Meta = d.Meta?.ToDictionary(kv => kv.Key, kv => kv.Value),
    };

    public static RagDoc FromMsg(MemDocMsg m) => new(m.Ref, m.Kind, m.Title, m.Body)
    {
        Tag = m.Tag, Source = m.Source, Dimension = m.Dimension, Embed = m.Embed, Weight = m.Weight, Level = m.Level,
        Bounds = m.Bounds is { Length: 6 } b ? new Box(b[0], b[1], b[2], b[3], b[4], b[5]) : null,
        At = m.AtMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
        Meta = m.Meta,
    };
}

/// <summary>
/// Main's side of the shared memory: a hive's raw docs are groomed by main's one groomer chain (its episode state, merges and
/// optional LLM stage are single-writer), stored in the one db, stamped with who wrote them (<c>docs.clone_id</c>, the same
/// provenance column every process already fills), and every other hive is told to reload at once (<see cref="BusTypes.MemChanged"/>).
/// </summary>
public sealed class MemoryBusHost(RagIndex rag, AgentDb db, HiveBus bus, ILogger? log = null)
{
    private readonly ILogger _log = log ?? NullLogger.Instance;

    public long Writes { get; private set; }

    public async Task<BusReply> HandleAsync(BusEnvelope e, CancellationToken ct)
    {
        if (e.As<MemWriteMsg>() is not { } w || w.Docs is not { Count: > 0 }) return BusReply.Fail("empty mem_write");
        var by = w.By ?? new MemProvenance(e.From);
        if (!by.Hive.Equals(e.From, StringComparison.OrdinalIgnoreCase)) by = by with { Hive = e.From }; // provenance is the sender
        var ids = new List<long>();
        var refs = new List<string>();
        foreach (var m in w.Docs)
        {
            var doc = MemDocs.FromMsg(m);
            long id = rag.Upsert(doc); // main's groomer runs here, once
            ids.Add(id);
            if (id > 0) { refs.Add(doc.Ref); Stamp(id, by); }
        }
        Writes += ids.Count;
        if (refs.Count > 0)
            _ = bus.PublishAsync(BusTypes.MemChanged, new MemChangedMsg(refs.ToArray(), ids.Where(i => i > 0).ToArray(), by.Tag), "*", ct, except: e.From);
        await Task.CompletedTask.ConfigureAwait(false);
        return BusReply.Of(new MemWriteReply(ids.ToArray()));
    }

    private void Stamp(long id, MemProvenance by)
    {
        try
        {
            lock (db.Gate)
                if (db.HasColumn("docs", "clone_id"))
                    db.Exec("UPDATE docs SET clone_id = $c WHERE id = $id", ("$c", by.Tag), ("$id", id));
        }
        catch (Exception ex) { _log.LogDebug(ex, "hive memory: provenance of doc {Id} not stamped", id); }
    }
}

/// <summary>
/// A hive process's side of the shared memory: installed as <see cref="RagIndex.WriteThrough"/>, it sends raw docs to main and
/// returns main's ids; reads stay direct reads of the shared WAL file (no RPC per recall), and main's
/// <see cref="BusTypes.MemChanged"/> invalidates this process's caches at once (the 2 s data_version poll stays as the backstop).
/// Inside a local transaction, or with main unreachable, it returns null: the write happens locally (still the same store).
/// </summary>
public sealed class MemoryBusClient(Func<BusEnvelope, TimeSpan, Task<BusReply?>> send, string self, string? token, AgentDb db,
    MemProvenance by, ILogger? log = null)
{
    private readonly ILogger _log = log ?? NullLogger.Instance;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);
    private int _reloadPending;

    /// <summary>Called on main's mem_changed (reload memory + vectors). Coalesced: a burst of writes (world-index batches from
    /// several hives) reloads once, at most <see cref="Coalesce"/> after the first notice, off the IPC reader thread.</summary>
    public Action? Invalidate { get; set; }
    public long Forwarded { get; private set; }
    public long Fallbacks { get; private set; }

    /// <summary>Docs per mem_write frame (a world-index batch can be large; frames stay far below the 4 MB IPC limit).</summary>
    public const int Chunk = 64;

    public long[]? Write(IReadOnlyList<RagDoc> docs)
    {
        if (db.InTransaction) { Fallbacks++; return null; } // never wait on main while holding the write lock
        if (docs.Count <= Chunk) return WriteChunk(docs);
        var ids = new List<long>(docs.Count);
        for (int i = 0; i < docs.Count; i += Chunk)
        {
            // A chunk main didn't take: the caller writes the whole batch locally (upserts by ref, so the chunks main already
            // stored are just touched again, never duplicated).
            var part = docs.Skip(i).Take(Chunk).ToList();
            if (WriteChunk(part) is not { } got) return null;
            ids.AddRange(got);
        }
        return [.. ids];
    }

    private long[]? WriteChunk(IReadOnlyList<RagDoc> docs)
    {
        var env = BusEnvelope.Make(BusTypes.MemWrite, self, HiveTree.Root, new MemWriteMsg(docs.Select(MemDocs.ToMsg).ToList(), by), token);
        try
        {
            var task = Task.Run(() => send(env, Timeout));
            if (!task.Wait(Timeout + TimeSpan.FromMilliseconds(500)) || task.Result is not { Ok: true } r || r.As<MemWriteReply>() is not { } reply)
            {
                Fallbacks++;
                _log.LogDebug("hive memory: main didn't take {N} doc(s); writing locally", docs.Count);
                return null;
            }
            Forwarded += docs.Count;
            return reply.Ids;
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or ObjectDisposedException)
        {
            Fallbacks++;
            _log.LogDebug(ex, "hive memory: forward failed; writing locally");
            return null;
        }
    }

    /// <summary>The hive's bus inbox calls this for <see cref="BusTypes.MemChanged"/>.</summary>
    public void OnChanged()
    {
        if (Interlocked.Exchange(ref _reloadPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(Coalesce).ConfigureAwait(false); } catch (Exception) { }
            Interlocked.Exchange(ref _reloadPending, 0);
            try { Invalidate?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "hive memory: invalidate failed"); }
        });
    }

    public TimeSpan Coalesce { get; init; } = TimeSpan.FromMilliseconds(200);
}
