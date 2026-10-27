using MineClaw.Raptor.Clustering;
using MineClaw.Raptor.Umap;

namespace MineClaw.Raptor;

public enum SelectionMode
{
    /// <summary>tree_retriever.py selection_mode="top_k".</summary>
    TopK,
    /// <summary>tree_retriever.py selection_mode="threshold" (cosine similarity ≥ threshold; see README deviation D9).</summary>
    Threshold,
}

public enum SplitMode
{
    /// <summary>utils.py split_text exactly: delimiters ". ! ? \n" are removed and sentences re-joined with spaces.</summary>
    Reference,
    /// <summary>Same boundaries and packing, but the delimiter stays on its sentence and "." inside numbers does not split (README D6).</summary>
    PreservePunctuation,
}

/// <summary>
/// Every RAPTOR parameter, with the source each default was taken from. "paper" = Sarthi et al., ICLR 2024
/// (arXiv 2401.18059); file names refer to github.com/parthsarthi03/raptor/raptor/*.py.
/// </summary>
public sealed record RaptorOptions
{
    // ---------------- chunking (tree_builder.py TreeBuilderConfig, utils.py split_text) ----------------

    /// <summary>TreeBuilderConfig max_tokens=100; paper §3 "chunks of length 100 tokens".</summary>
    public int ChunkMaxTokens { get; init; } = 100;
    /// <summary>utils.py split_text overlap=0.</summary>
    public int ChunkOverlap { get; init; }
    /// <summary>Deviation D6: <see cref="SplitMode.PreservePunctuation"/> by default (coordinates like "12.5" survive).</summary>
    public SplitMode Split { get; init; } = SplitMode.PreservePunctuation;

    // ---------------- tree construction (tree_builder.py, cluster_tree_builder.py) ----------------

    /// <summary>TreeBuilderConfig num_layers=5 (maximum number of summary layers).</summary>
    public int NumLayers { get; init; } = 5;
    /// <summary>
    /// ClusterTreeConfig reduction_dimension=10: UMAP target dimension, and construction stops when a layer has
    /// ≤ reduction_dimension + 1 nodes (cluster_tree_builder.py construct_tree).
    /// </summary>
    public int ReductionDimension { get; init; } = 10;
    /// <summary>TreeBuilderConfig summarization_length=100 (max_tokens passed to the summarizer).</summary>
    public int SummarizationLength { get; init; } = 100;
    /// <summary>Paper Appendix D system prompt (the code uses "You are a helpful assistant."; we keep the paper's).</summary>
    public string SummarySystemPrompt { get; init; } = "You are a Summarizing Text Portal";
    /// <summary>Paper Appendix D / SummarizationModels.py user prompt, verbatim. "{context}" is replaced by the cluster text.</summary>
    public string SummaryPromptTemplate { get; init; } = "Write a summary of the following, including as many key details as possible: {context}:";
    /// <summary>Concurrent summarizer calls (construct_tree use_multithreading; node indices stay deterministic).</summary>
    public int SummaryConcurrency { get; init; } = 4;
    /// <summary>Texts per IEmbedder call.</summary>
    public int EmbedBatchSize { get; init; } = 64;

    // ---------------- clustering (cluster_utils.py) ----------------

    /// <summary>RAPTOR_Clustering threshold=0.1: a node joins every GMM component with probability &gt; 0.1 (soft clustering).</summary>
    public double ClusterThreshold { get; init; } = 0.1;
    /// <summary>RAPTOR_Clustering max_length_in_cluster=3500 tokens; larger clusters are re-clustered recursively (paper §3).</summary>
    public int MaxLengthInCluster { get; init; } = 3500;
    /// <summary>get_optimal_clusters max_clusters=50: BIC is evaluated for k in [1, min(50, n) − 1].</summary>
    public int MaxClusters { get; init; } = 50;
    /// <summary>cluster_utils.py RANDOM_SEED=224, used as random_state of every GMM in the BIC search.</summary>
    public long BicSearchSeed { get; init; } = 224;
    /// <summary>GMM_cluster random_state=0 for the final fit at the chosen k.</summary>
    public long ClusterFitSeed { get; init; }
    /// <summary>global_cluster_embeddings n_neighbors=None → int(sqrt(n − 1)). Null keeps that rule.</summary>
    public int? GlobalNeighbors { get; init; }
    /// <summary>local_cluster_embeddings num_neighbors=10.</summary>
    public int LocalNeighbors { get; init; } = 10;
    /// <summary>Recursion guard for re-clustering (absent in the reference, which can recurse forever; README D4).</summary>
    public int MaxReclusterDepth { get; init; } = 16;
    /// <summary>sklearn GaussianMixture defaults (full covariance, reg_covar 1e-6, tol 1e-3, max_iter 100, n_init 1, kmeans init).</summary>
    public GmmOptions Gmm { get; init; } = new();
    /// <summary>umap-learn defaults; n_neighbors / n_components are set per call by the clustering code.</summary>
    public UmapOptions Umap { get; init; } = new();

    // ---------------- retrieval (tree_retriever.py) ----------------

    /// <summary>Paper §3/§4: collapsed tree performed best; RetrievalAugmentation.retrieve collapse_tree=True.</summary>
    public bool DefaultCollapsed { get; init; } = true;
    /// <summary>Paper §4 "collapsed tree with 2000 maximum tokens" (code default is 3500).</summary>
    public int CollapsedMaxTokens { get; init; } = 2000;
    /// <summary>Paper Algorithm 2 has no count cap (budget only); the code's retrieve() caps at top_k=10. Null = paper.</summary>
    public int? CollapsedTopK { get; init; }
    /// <summary>TreeRetrieverConfig top_k=5 (nodes selected per layer during traversal).</summary>
    public int TraversalTopK { get; init; } = 5;
    /// <summary>TreeRetrieverConfig selection_mode="top_k".</summary>
    public SelectionMode Selection { get; init; } = SelectionMode.TopK;
    /// <summary>TreeRetrieverConfig threshold=0.5.</summary>
    public double SelectionThreshold { get; init; } = 0.5;

    // ---------------- incremental maintenance (MineClaw extension; not in the paper) ----------------

    /// <summary>Soft-attach threshold for inserted leaves; reuses the clustering threshold 0.1.</summary>
    public double InsertThreshold { get; init; } = 0.1;
    /// <summary>Layer-1 candidates (by cosine) considered when attaching an inserted leaf.</summary>
    public int InsertCandidates { get; init; } = 8;
    /// <summary>Rebuild when (inserted + removed leaves since build) / leaves at build exceeds this.</summary>
    public double RebuildChangedFraction { get; init; } = 0.25;
    /// <summary>Rebuild when the mean inserted-leaf→parent cosine falls at least this far below the build-time baseline…</summary>
    public double RebuildSimilarityDrift { get; init; } = 0.05;
    /// <summary>…and by more than this many build-time standard deviations of the leaf→parent cosine (scale-free guard).</summary>
    public double RebuildDriftStdDevs { get; init; } = 1.0;
    /// <summary>Minimum inserted leaves before the drift rule can fire.</summary>
    public int RebuildDriftMinSamples { get; init; } = 16;
    /// <summary>RefreshDirty splits a node whose children exceed MaxLengthInCluster by re-running RAPTOR clustering on them.</summary>
    public bool SplitOverCapOnRefresh { get; init; } = true;

    /// <summary>Upper bound on worker threads for kNN / GMM search.</summary>
    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount;

    internal string UserPrompt(string context) => SummaryPromptTemplate.Replace("{context}", context);
}
