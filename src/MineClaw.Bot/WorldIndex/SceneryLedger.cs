using MineClaw.World;

namespace MineClaw.Bot.WorldIndex;

/// <summary>
/// Persisted "what have I already told/asked about" for auto-detected scenery (unknown structures). Each thing gets a
/// content-anchored key — min corner plus a signature of its dominant blocks ("-9,64,-1~3fa2c1d0") — and is matched
/// fuzzily on later sightings (overlapping box + similar block mix), so small edits, a one-block shift of the corner or
/// a restart keep the same record. The bot announces/asks once; again only when the thing changed materially.
/// </summary>
public sealed class SceneryLedger(IWorldMemoryStore store, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public enum Verdict { New, Changed, Known }

    /// <summary>Signature of a block mix: the 3 most common block names, order-independent (stable under small edits).</summary>
    public static string Signature(IReadOnlyDictionary<string, int> blocks)
    {
        var top = blocks.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(3)
            .Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal);
        uint h = 2166136261;
        foreach (var ch in string.Join("|", top)) { h ^= ch; h *= 16777619; }
        return h.ToString("x8");
    }

    public static string KeyFor(BlockPos min, IReadOnlyDictionary<string, int> blocks) => $"{min.X},{min.Y},{min.Z}~{Signature(blocks)}";

    public static string Encode(IReadOnlyDictionary<string, int> blocks) =>
        string.Join(",", blocks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}"));

    public static Dictionary<string, int> Decode(string s)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.LastIndexOf('=');
            if (eq > 0 && int.TryParse(part[(eq + 1)..], out var n)) d[part[..eq]] = n;
        }
        return d;
    }

    /// <summary>Cosine similarity of two block mixes (1 = same proportions).</summary>
    public static double Similarity(IReadOnlyDictionary<string, int> a, IReadOnlyDictionary<string, int> b)
    {
        double dot = 0, na = 0, nb = 0;
        foreach (var (k, v) in a) { na += (double)v * v; if (b.TryGetValue(k, out var w)) dot += (double)v * w; }
        foreach (var v in b.Values) nb += (double)v * v;
        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    private static bool Near(SceneryRow r, BlockPos min, BlockPos max, int pad = 2) =>
        r.MinX - pad <= max.X && r.MaxX + pad >= min.X && r.MinY - pad <= max.Y && r.MaxY + pad >= min.Y &&
        r.MinZ - pad <= max.Z && r.MaxZ + pad >= min.Z;

    /// <summary>The stored record for this thing (overlapping box, similar block mix), or null.</summary>
    public SceneryRow? Match(int dim, BlockPos min, BlockPos max, IReadOnlyDictionary<string, int> blocks)
    {
        SceneryRow? best = null;
        double bestSim = 0;
        foreach (var r in store.Scenery(dim))
        {
            if (!Near(r, min, max)) continue;
            double sim = Similarity(Decode(r.Blocks), blocks);
            bool sameSig = r.Key.EndsWith("~" + Signature(blocks), StringComparison.Ordinal);
            if (sim < 0.5 && !sameSig) continue;
            if (sim > bestSim || best == null) { best = r; bestSim = sim; }
        }
        return best;
    }

    /// <summary>Material change: the block mix shifted (similarity &lt; 0.7) or the size changed by more than half (≥ 8 blocks).</summary>
    public static bool Material(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> now)
    {
        int a = before.Values.Sum(), b = now.Values.Sum();
        return Similarity(before, now) < 0.7 || Math.Abs(b - a) > Math.Max(8, a / 2);
    }

    /// <summary>
    /// Records a sighting and says whether to announce it. <paramref name="unchangedSinceLastSeen"/>: its chunks match the
    /// stored baseline (it was there, untouched, last time) — recorded silently when the ledger doesn't know it yet.
    /// </summary>
    public Verdict Observe(int dim, string kind, BlockPos min, BlockPos max, IReadOnlyDictionary<string, int> blocks, bool unchangedSinceLastSeen)
    {
        var now = _time.GetUtcNow();
        var m = Match(dim, min, max, blocks);
        if (m != null)
        {
            bool material = Material(Decode(m.Blocks), blocks);
            if (!material)
            {
                // Keep the original key (stable); follow small edits of the box/mix.
                store.PutScenery(m with { MinX = min.X, MinY = min.Y, MinZ = min.Z, MaxX = max.X, MaxY = max.Y, MaxZ = max.Z,
                    Blocks = Encode(blocks), LastSeen = now });
                return Verdict.Known;
            }
            store.PutScenery(Row(dim, kind, min, max, blocks, now, announce: true), replacesKey: m.Key);
            return Verdict.Changed;
        }
        store.PutScenery(Row(dim, kind, min, max, blocks, now, announce: !unchangedSinceLastSeen));
        return unchangedSinceLastSeen ? Verdict.Known : Verdict.New;
    }

    /// <summary>The bot asked a player about the thing at this box (kept so it isn't asked again after a restart).</summary>
    public void MarkAsked(int dim, BlockPos min, BlockPos max, IReadOnlyDictionary<string, int> blocks)
    {
        var now = _time.GetUtcNow();
        var m = Match(dim, min, max, blocks) ?? Row(dim, IndexKinds.UnknownStructure, min, max, blocks, now, announce: false);
        store.PutScenery(m with { AskedAt = now, AnnouncedAt = m.AnnouncedAt ?? now, LastSeen = now });
    }

    private static SceneryRow Row(int dim, string kind, BlockPos min, BlockPos max, IReadOnlyDictionary<string, int> blocks, DateTimeOffset now,
        bool announce) =>
        new(dim, KeyFor(min, blocks), kind, min.X, min.Y, min.Z, max.X, max.Y, max.Z, Encode(blocks),
            announce ? now : null, announce ? now : null, now);
}
