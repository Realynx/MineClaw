using MineClaw.Raptor.Model;

namespace MineClaw.Raptor.Storage;

/// <summary>Storage form of one node. Parents are not stored; they are derived from children on load.</summary>
public sealed record RaptorNodeRecord(
    int Index,
    int Layer,
    string Text,
    float[] Embedding,
    int TokenCount,
    int[] Children,
    string? Key,
    int Chunk,
    string? Kind,
    string? Source,
    bool Pinned,
    bool Dirty,
    bool SummaryPending,
    long InsertedSeq);

/// <summary>Tree-level metadata (one row).</summary>
public sealed record RaptorTreeMeta(
    int NumLayers,
    int NextIndex,
    int Dimensions,
    string EmbeddingModel,
    long BuildId,
    DateTimeOffset BuiltAt,
    int LeavesAtBuild,
    int InsertedSinceBuild,
    int RemovedSinceBuild,
    double BaselineLeafParentSimilarity,
    double InsertedSimilaritySum,
    int InsertedSimilarityCount,
    long InsertSeq,
    double BaselineLeafParentSimilarityStd = 0,
    double InsertTemperature = InsertCalibration.DefaultTemperature);

public sealed record RaptorSnapshot(RaptorTreeMeta Meta, IReadOnlyList<RaptorNodeRecord> Nodes);

/// <summary>
/// Changes since the last persist. <see cref="ReplaceAll"/> (after a build/rebuild) means: delete every stored node
/// first, then write <see cref="Upserts"/>. Otherwise upsert by index and delete <see cref="Deletes"/>.
/// The meta row is always replaced.
/// </summary>
public sealed record RaptorChangeSet(RaptorTreeMeta Meta, IReadOnlyList<RaptorNodeRecord> Upserts, IReadOnlyList<int> Deletes, bool ReplaceAll);

public static class RaptorSnapshotMapper
{
    public static RaptorNodeRecord ToRecord(RaptorNode n) => new(n.Index, n.Layer, n.Text, n.Embedding, n.TokenCount, n.Children.ToArray(),
        n.Key, n.Chunk, n.Kind, n.Source, n.Pinned, n.Dirty, n.SummaryPending, n.InsertedSeq);

    public static RaptorTreeMeta ToMeta(RaptorTree t) => new(t.NumLayers, t.NextIndex, t.Dimensions, t.EmbeddingModel, t.BuildId, t.BuiltAt,
        t.LeavesAtBuild, t.InsertedSinceBuild, t.RemovedSinceBuild, t.BaselineLeafParentSimilarity, t.InsertedSimilaritySum,
        t.InsertedSimilarityCount, t.InsertSeq, t.BaselineLeafParentSimilarityStd, t.InsertTemperature);

    public static RaptorSnapshot ToSnapshot(RaptorTree t) => new(ToMeta(t), t.AllNodes.Values.OrderBy(n => n.Index).Select(ToRecord).ToList());

    public static RaptorTree FromSnapshot(RaptorSnapshot s)
    {
        var m = s.Meta;
        var t = new RaptorTree
        {
            NumLayers = m.NumLayers,
            Dimensions = m.Dimensions,
            EmbeddingModel = m.EmbeddingModel,
            BuildId = m.BuildId,
            BuiltAt = m.BuiltAt,
            LeavesAtBuild = m.LeavesAtBuild,
            InsertedSinceBuild = m.InsertedSinceBuild,
            RemovedSinceBuild = m.RemovedSinceBuild,
            BaselineLeafParentSimilarity = m.BaselineLeafParentSimilarity,
            BaselineLeafParentSimilarityStd = m.BaselineLeafParentSimilarityStd,
            InsertTemperature = m.InsertTemperature,
            InsertedSimilaritySum = m.InsertedSimilaritySum,
            InsertedSimilarityCount = m.InsertedSimilarityCount,
            InsertSeq = m.InsertSeq,
        };
        foreach (var r in s.Nodes)
        {
            var n = new RaptorNode(r.Index, r.Layer, r.Text, r.Embedding, r.TokenCount)
            {
                Key = r.Key, Chunk = r.Chunk, Kind = r.Kind, Source = r.Source, Pinned = r.Pinned,
                Dirty = r.Dirty, SummaryPending = r.SummaryPending, InsertedSeq = r.InsertedSeq,
            };
            foreach (var c in r.Children) n.Children.Add(c);
            t.Add(n);
        }
        t.NextIndex = Math.Max(t.NextIndex, m.NextIndex);
        t.RebuildParents();
        return t;
    }
}
