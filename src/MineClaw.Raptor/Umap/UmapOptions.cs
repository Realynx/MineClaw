namespace MineClaw.Raptor.Umap;

/// <summary>
/// UMAP hyper-parameters. Every default is umap-learn's <c>UMAP.__init__</c> default; RAPTOR overrides only
/// <c>n_neighbors</c>, <c>n_components</c> and <c>metric="cosine"</c> (cluster_utils.py). RAPTOR passes no
/// <c>random_state</c>; we always seed for determinism (umap-learn then also runs its SGD single-threaded).
/// </summary>
public sealed record UmapOptions
{
    /// <summary>umap-learn n_neighbors (default 15; RAPTOR passes sqrt(n-1) globally, 10 locally).</summary>
    public int NNeighbors { get; init; } = 15;
    /// <summary>umap-learn n_components (default 2; RAPTOR passes reduction_dimension = 10).</summary>
    public int NComponents { get; init; } = 2;
    /// <summary>umap-learn min_dist default.</summary>
    public double MinDist { get; init; } = 0.1;
    /// <summary>umap-learn spread default.</summary>
    public double Spread { get; init; } = 1.0;
    /// <summary>umap-learn n_epochs=None → 500 when n ≤ 10000, else 200 (simplicial_set_embedding).</summary>
    public int? NEpochs { get; init; }
    /// <summary>umap-learn learning_rate default (initial alpha).</summary>
    public double LearningRate { get; init; } = 1.0;
    /// <summary>umap-learn negative_sample_rate default.</summary>
    public int NegativeSampleRate { get; init; } = 5;
    /// <summary>umap-learn repulsion_strength default (gamma).</summary>
    public double RepulsionStrength { get; init; } = 1.0;
    /// <summary>umap-learn set_op_mix_ratio default (pure fuzzy union).</summary>
    public double SetOpMixRatio { get; init; } = 1.0;
    /// <summary>umap-learn local_connectivity default.</summary>
    public double LocalConnectivity { get; init; } = 1.0;
    /// <summary>umap-learn init="spectral" default; false = "random" (uniform in [-10, 10]).</summary>
    public bool SpectralInit { get; init; } = true;
    /// <summary>Seed for every stochastic step (umap-learn random_state).</summary>
    public long Seed { get; init; } = 224;
    /// <summary>Hogwild-parallel SGD (umap-learn's behaviour when random_state is None). Non-deterministic; off by default.</summary>
    public bool ParallelSgd { get; init; }
}
