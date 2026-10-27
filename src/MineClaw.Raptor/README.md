# MineClaw.Raptor

A C# implementation of **RAPTOR** (Sarthi et al., *Recursive Abstractive Processing for Tree-Organized Retrieval*,
ICLR 2024, arXiv 2401.18059). It follows the paper and the authors' reference code
(github.com/parthsarthi03/raptor, `raptor/*.py`). The library is pure: no SQLite, no HTTP, and no NuGet dependencies.

```
documents ─split_text→ leaves(≤100 tok) ─IEmbedder→ layer 0
   for layer < num_layers, while |layer| > reduction_dimension+1:
      RAPTOR_Clustering: UMAP(global, sqrt(n-1) nbrs, cosine) → GMM/BIC soft labels (p>0.1)
                         → per global cluster: UMAP(local, 10 nbrs) → GMM/BIC → (global,local) clusters
                         → recluster any cluster whose text > 3500 tokens
      ISummarizer(paper prompt, ≤100 tok) per cluster → IEmbedder → next layer
retrieve: collapsed tree (all nodes by cosine, fill 2000 tokens)  |  tree traversal (top-k per layer, descend)
```

## Public API

| Type | Role |
|---|---|
| `RaptorIndex` | Thread-safe facade. `BuildAsync(docs, budget)` is the faithful static build. Also `RetrieveAsync(RaptorQuery)` / `Retrieve(vector, query)`, `InsertAsync(docs)`, `RemoveAsync(keys)`, `RefreshDirtyAsync(budget)`, `ShouldRebuild()`, `RebuildAsync(budget)`, `LoadAsync`/`SaveAsync`, and `Read(t => …)`. |
| `RaptorTreeBuilder` | Stateless build pipeline (`BuildAsync`, `BuildFromTextAsync`, `BuildFromLeavesAsync`, `GetText`). |
| `RaptorRetriever` | `retrieve_information` (traversal) and `retrieve_information_collapse_tree` (collapsed) over a tree. |
| `RaptorTree` / `RaptorNode` | The tree model (`tree_structures.py`), plus layer, parents, token count, leaf metadata (Key, Kind, Source, Pinned) and maintenance flags (Dirty, SummaryPending). |
| `RaptorDocument`, `RaptorQuery`, `RaptorFilter`, `RaptorResult`, `RetrievedNode` | Inputs and outputs. |
| `RaptorOptions` | Every parameter, each with a comment citing its source. |
| `RaptorBudget` | Caps a build, refresh or rebuild by summary count, tokens or USD. |
| `IEmbedder`, `ISummarizer`, `ITokenCounter` | Seams for the production models. |
| `IRaptorStore` (+ `InMemoryRaptorStore`, `FileRaptorStore`) | Persistence seam: `LoadAsync` → `RaptorSnapshot`, and `ApplyAsync(RaptorChangeSet)` (upserts and deletes, or `ReplaceAll` after a build). |
| `RaptorClustering`, `GaussianMixture`, `KMeans`, `UmapReducer` | The algorithms, public so they can be tested and reused. |

## Parameters (defaults and sources)

| Parameter | Default | Source |
|---|---|---|
| Chunk size `ChunkMaxTokens` | 100 | `TreeBuilderConfig.max_tokens`; paper §3 |
| Chunk overlap | 0 | `utils.split_text(overlap=0)` |
| `NumLayers` | 5 | `TreeBuilderConfig.num_layers` |
| `ReductionDimension` (UMAP dim; stop when a layer has ≤ dim+1 nodes) | 10 | `ClusterTreeConfig.reduction_dimension`; `construct_tree` |
| `SummarizationLength` | 100 | `TreeBuilderConfig.summarization_length` |
| Summary prompt | system "You are a Summarizing Text Portal"; user "Write a summary of the following, including as many key details as possible: {context}:" | paper App. D (verbatim); same user prompt in `SummarizationModels.py` |
| `ClusterThreshold` (soft membership) | 0.1 | `RAPTOR_Clustering(threshold=0.1)` |
| `MaxLengthInCluster` | 3500 | `RAPTOR_Clustering(max_length_in_cluster=3500)`; paper §3 |
| `MaxClusters` (BIC over k = 1 … min(50, n) − 1) | 50 | `get_optimal_clusters` |
| BIC search seed / final fit seed | 224 / 0 | `RANDOM_SEED = 224`; `GMM_cluster(random_state=0)` |
| Global neighbors | int(sqrt(n − 1)) | `global_cluster_embeddings` |
| Global dim | min(dim, n − 2) | `perform_clustering` |
| Local neighbors | 10 | `local_cluster_embeddings` |
| Local skip (one cluster) | size ≤ dim + 1 | `perform_clustering` |
| UMAP min_dist / spread / epochs / lr / neg. rate / mix / local conn. / init | 0.1 / 1.0 / 500 if n ≤ 10k else 200 / 1.0 / 5 / 1.0 / 1.0 / spectral | umap-learn `UMAP.__init__` and `simplicial_set_embedding` (RAPTOR leaves these at their defaults) |
| UMAP a, b | fitted (≈1.577, 0.895) | `find_ab_params` |
| GMM | full covariance, reg_covar 1e-6, tol 1e-3, max_iter 100, n_init 1, k-means init | sklearn `GaussianMixture` defaults |
| k-means (GMM init) | k-means++ (2 + ln k trials), max_iter 300, tol 1e-4 · mean var | sklearn `KMeans` |
| Default retrieval | collapsed | paper §3/§4; `RetrievalAugmentation.retrieve(collapse_tree=True)` |
| `CollapsedMaxTokens` | 2000 | paper §4 (the code default is 3500) |
| `CollapsedTopK` | none | paper Alg. 2 (the code caps at `top_k=10`) |
| `TraversalTopK` | 5 | `TreeRetrieverConfig.top_k` |
| Traversal start layer / depth | root layer / num_layers + 1 | `TreeRetriever.__init__` |
| Selection mode / threshold | top_k / 0.5 | `TreeRetrieverConfig` |

## Implementation choices

* **UMAP is implemented here, not taken from NuGet.** curiosity-ai/umap-sharp supports cosine, n_neighbors, n_components
  and a seedable RNG. But its source (`Umap.cs`) hard-codes random initialization ("We're not computing the spectral
  initialization"), uses its own epoch heuristic, always uses NN-descent, and throws for any min_dist/spread other than
  0.1/1. That doesn't match umap-learn's defaults, which RAPTOR relies on. `UmapReducer` follows umap-learn's code
  path instead:
  - exact cosine kNN with self included
  - `smooth_knn_dist`, membership strengths, and the fuzzy union
  - pruning at max/n_epochs
  - spectral init, including `multi_component_layout` and `component_layout`
  - `noisy_scale_coords`, then the [0, 10] rescale
  - `optimize_layout_euclidean` with `tau_rand` negative sampling, move_other, clip ±4 and linear α decay
* **GMM** is our own EM with full covariance, matching sklearn's `_base.py` and `_gaussian_mixture.py`:
  - k-means responsibilities as init
  - nk + 10·eps, covariance + reg_covar·I, and precision Cholesky factors
  - a log-sum-exp E-step and convergence on |Δ mean log-likelihood| < tol, with a final E-step
  - `bic = −2·score·n + p·ln n`, where p = k·d(d+1)/2 + k·d + k − 1
  - `random_state` seeding per fit

  A test checks the BIC against the closed-form single-Gaussian likelihood.
* **Determinism.** Every random draw uses a seeded xoshiro256** (`Numerics.Rng`). Parallel work (kNN rows, GMM fits
  per k, local clusters, E-step rows, M-step components) is split so that results never depend on thread scheduling.
  Builds with 1 thread and with N threads are bit-identical (tested).

## Deviations from the paper and reference code

| # | Deviation | Why |
|---|---|---|
| D1 | Embeddings go through `IEmbedder` (production: Azure `text-embedding-3-large` at 1024 dims) instead of SBERT. Vectors are L2-normalized and cosine = dot. | Allowed deviation; the model is wired by the orchestrator. |
| D2 | Summaries go through `ISummarizer` (production: Claude Haiku) instead of gpt-3.5-turbo. The prompt is the paper's verbatim; its system line ("Summarizing Text Portal") replaces the code's "helpful assistant". | Allowed; the paper's prompt is authoritative. |
| D2b | Budget-aware. Each summary call is reserved in deterministic cluster order against `RaptorBudget` (count, tokens, USD), then settled with the usage the provider reports. | Token budget is a hard requirement. |
| D3 | A node whose GMM probabilities are all ≤ 0.1 joins its arg-max cluster. | The reference silently drops it from every upper layer. |
| D4 | Re-clustering guards. A cluster with < 5 nodes can't be UMAP'd (n_neighbors would be < 2), so it is token-packed. A re-cluster that makes no progress is retried with k ≥ 2, then token-packed. Depth is capped at 16. Recursion passes threshold and reduction_dimension explicitly. | The reference crashes on tiny clusters and can recurse forever once UMAP is seeded. It drops those two arguments on recursion (identical under defaults). |
| D5 | A GMM fit whose covariance isn't positive definite scores BIC = +∞ for that k. The final fit retries with reg_covar × 10. | sklearn raises ValueError and the build would abort. |
| D6 | `split_text` defaults to `PreservePunctuation`: same boundaries and packing, but delimiters stay on their sentence and "12.5" isn't split. `SplitMode.Reference` reproduces the original exactly. | The reference deletes every "." (so "x=12.5" becomes "x=12 5"), which hurts coordinates. |
| D7 | Tokens are counted by `ITokenCounter`. The default `ApproxTokenCounter` approximates cl100k_base. | No dependencies; the integration can plug in a real tokenizer. |
| D8 | UMAP specifics: exact kNN at every size (umap-learn switches to pynndescent at ≥ 4096 points); always seeded with single-threaded SGD (RAPTOR passes no random_state; `UmapOptions.ParallelSgd` restores hogwild); eigenvectors from a dense solver (n ≤ 400) or thick-restart Lanczos instead of ARPACK, sign-fixed. The RNG streams differ from numpy and numba. | Determinism, plus exact-or-better neighbors. Same algorithm, so numbers differ but behavior matches. |
| D9 | Threshold selection keeps nodes with cosine **similarity** ≥ threshold. | `tree_retriever.py` keeps nodes whose **distance** > threshold, which inverts the paper's intent. |
| D10 | When the budget is exhausted, a cluster gets an extractive placeholder flagged `SummaryPending`, and `RefreshDirtyAsync` replaces it later. | The build degrades instead of failing. |
| D11 | A rebuild reuses a previous summary and its embedding when the cluster's exact `get_text` is unchanged (SHA-256 key). | Saves Haiku calls. Output equals a fresh summary from a deterministic summarizer. |
| D12 | **Incremental maintenance** (not in the paper). See the section below. | Allowed. |
| D13 | Global→local membership is mapped by position. | The reference matches rows by embedding equality, so duplicate texts get each other's clusters. |
| D14 | Ties break on the lower node index, and children are sorted sets. | Reproducible output. |
| D15 | When traversal's start layer is overridden, the depth defaults to min(num_layers + 1, start + 1). | The reference raises for start < root unless the depth is passed too. |
| D16 | Many documents with metadata (Key, Kind, Source, Pinned) instead of a single text string. Filters are supported. | Memory-index use. |

### Incremental maintenance (D12)

The static build path stays exactly as in the paper.

* `InsertAsync(docs)` chunks and embeds new leaves. It scores layer-1 nodes with
  `r_j = softmax_j(cos(leaf, node_j)/τ)` over the top 8 by cosine, and attaches the leaf to the most similar node plus
  every other node with r_j > 0.1 (GMM_cluster's rule). τ is fitted after each build by maximum likelihood to the
  memberships that UMAP+GMM actually produced (`InsertCalibration`), so inserts are exactly as soft as the build.
  The new leaf's parents and all their ancestors become `Dirty`. New leaves are retrievable immediately.
* `RemoveAsync(keys)` deletes leaves and prunes summaries left without children. Survivors are marked dirty.
* `RefreshDirtyAsync(budget)` works bottom-up from layer 1. It re-summarizes dirty or placeholder nodes from their
  current children and re-embeds them, stopping when the budget runs out (the rest stay dirty). A dirty node whose
  children now exceed max_length_in_cluster is first split by running `RaptorClustering` on its children; the new
  siblings share its parents.
* `ShouldRebuild()` returns true in three cases:
  - the tree has no summary layers yet but now has more than dim + 1 leaves
  - (inserted + removed) / leaves-at-build exceeds 25%
  - the mean inserted-leaf→parent cosine has fallen below the build-time mean by more than max(0.05, 1σ of the
    build's leaf→parent cosines), once there are at least 16 inserts

  `RebuildAsync` then runs the faithful build over the current leaves (embeddings kept, summaries reused where
  clusters are identical).

### Filters

`RaptorFilter` (Kinds, Sources, PinnedOnly, Predicate) is evaluated on **leaves**. A summary is eligible when at least
one descendant leaf passes.
* **Collapsed mode:** ineligible nodes are skipped while filling the token budget.
* **Traversal:** the candidates at every layer, and the children followed downward, are restricted to eligible nodes.

`RaptorQuery.IncludePinned` offers pinned leaves (that pass the filter) before anything else, and they count against
the budget.

## Integration (what the Agent layer must implement)

1. **`IRaptorStore` over `AgentDb`.** For example, `raptor_meta` (one row holding `RaptorTreeMeta`) and
   `raptor_nodes(idx PK, layer, text, vec BLOB float32, tokens, children BLOB/JSON, key, chunk, kind, source, flags,
   seq)`. Apply each `RaptorChangeSet` in one transaction: on `ReplaceAll`, delete all nodes first; then upsert and
   delete by index. Leaf `Key` should be `docs.ref`.
2. **`IEmbedder` adapter** over the Azure `IEmbeddingProvider` (`Model`, `Dimensions` = 1024, batch embed, report
   tokens). Embed the query once and pass it as `RaptorQuery.Vector`; `RagIndex.EmbedQueryAsync` can share it.
3. **`ISummarizer` adapter** over Claude Haiku. Send `SystemPrompt` and `UserPrompt` with `max_tokens = MaxTokens`, and
   return the text plus input/output tokens (and cost if known). Retry transient errors; anything thrown becomes a
   `SummaryPending` placeholder.
4. Optionally an `ITokenCounter` (cl100k or Claude counts).
5. An **`IRetrievalIndex` adapter.** Map `RagQuery.Kinds`→`RaptorFilter.Kinds`, `Limit`→collapsed `TopK` or
   `MaxTokens`, and `RetrievedNode`→`RagHit` (leaves via Key/ref; summaries as `raptor:{index}`).
6. **Scheduling:** `InsertAsync` and `RemoveAsync` on doc changes, `RefreshDirtyAsync(budget)` on idle, and
   `ShouldRebuild()`→`RebuildAsync(budget)` off-peak, all under the daily summary budget.
