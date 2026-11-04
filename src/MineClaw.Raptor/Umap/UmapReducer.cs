using MineClaw.Raptor.Numerics;

namespace MineClaw.Raptor.Umap;

/// <summary>
/// UMAP (McInnes et al.), re-implemented to follow umap-learn's <c>fit_transform</c> code path with the defaults
/// RAPTOR relies on: cosine metric, fuzzy simplicial set, graph pruning by n_epochs, spectral initialization
/// (multi-component aware) + noisy rescale, and the single-threaded seeded SGD of
/// <c>optimize_layout_euclidean</c> with a/b fitted from (spread, min_dist).
/// </summary>
public sealed class UmapReducer
{
    private readonly UmapOptions _o;

    public UmapReducer(UmapOptions options) => _o = options;

    /// <summary>Embeds <paramref name="data"/> (n vectors) into n × NComponents (row-major doubles).</summary>
    public double[] FitTransform(IReadOnlyList<float[]> data)
    {
        int n = data.Count;
        int dim = _o.NComponents;
        if (n == 0) return [];
        if (dim < 1) throw new ArgumentException("n_components must be ≥ 1");
        if (_o.NNeighbors < 2) throw new ArgumentException("n_neighbors must be greater than 1");
        var x = new float[n][];
        for (int i = 0; i < n; i++) x[i] = VectorMath.Normalized(data[i]);
        // umap-learn: "n_neighbors is larger than the dataset size; truncating to X.shape[0] - 1"
        int k = Math.Min(_o.NNeighbors, n - 1);
        if (k < 1) return new double[n * dim];

        var rng = new Rng(_o.Seed);
        var graph = FuzzySimplicialSet.Build(x, k, _o.SetOpMixRatio, _o.LocalConnectivity);

        int defaultEpochs = n <= 10000 ? 500 : 200;
        int nEpochs = _o.NEpochs ?? defaultEpochs;
        double gmax = graph.MaxValue();
        double cut = gmax / (nEpochs > 10 ? nEpochs : defaultEpochs);
        graph = graph.Where(v => v >= cut);

        var emb = InitialEmbedding(x, graph, dim, rng);
        var (head, tail, weights) = Coo(graph);
        var eps = MakeEpochsPerSample(weights, nEpochs);
        var tau = rng.NextTauState();
        RescaleToTen(emb, n, dim);

        var (a, b) = AbParams.Find(_o.Spread, _o.MinDist);
        OptimizeLayoutEuclidean(emb, n, dim, head, tail, nEpochs, eps, (float)a, (float)b, tau, (float)_o.RepulsionStrength, (float)_o.LearningRate, _o.NegativeSampleRate, _o.ParallelSgd);

        var result = new double[n * dim];
        for (int i = 0; i < result.Length; i++) result[i] = emb[i];
        return result;
    }

    /// <summary>Spectral layout plus noise, or uniform random in [-10, 10) when spectral init is off.</summary>
    private float[] InitialEmbedding(float[][] x, SparseGraph graph, int dim, Rng rng)
    {
        if (_o.SpectralInit) return NoisyScaleCoords(SpectralLayout(x, graph, dim, rng), rng, 10.0, 0.0001);
        var emb = new float[graph.N * dim];
        for (int i = 0; i < emb.Length; i++) emb[i] = (float)rng.Uniform(-10, 10);
        return emb;
    }

    /// <summary>epochs_per_sample input: head/tail/weight per stored edge, row-major (COO).</summary>
    private static (int[] Head, int[] Tail, double[] Weights) Coo(SparseGraph graph)
    {
        int nnz = graph.Nnz;
        var head = new int[nnz];
        var tail = new int[nnz];
        var weights = new double[nnz];
        for (int i = 0, p = 0; i < graph.N; i++)
            for (int q = graph.RowPtr[i]; q < graph.RowPtr[i + 1]; q++, p++)
            {
                head[p] = i;
                tail[p] = graph.Col[q];
                weights[p] = graph.Val[q];
            }
        return (head, tail, weights);
    }

    /// <summary>embedding = 10 * (embedding - min) / (max - min), per dimension.</summary>
    private static void RescaleToTen(float[] emb, int n, int dim)
    {
        for (int d = 0; d < dim; d++)
        {
            float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < n; i++) { var v = emb[i * dim + d]; if (v < mn) mn = v; if (v > mx) mx = v; }
            float range = mx - mn;
            for (int i = 0; i < n; i++) emb[i * dim + d] = range > 0 ? 10f * (emb[i * dim + d] - mn) / range : 0f;
        }
    }

    internal static double[] MakeEpochsPerSample(double[] weights, int nEpochs)
    {
        var result = new double[weights.Length];
        double max = 0;
        foreach (var w in weights) if (w > max) max = w;
        for (int i = 0; i < weights.Length; i++)
        {
            double nSamples = nEpochs * (weights[i] / max);
            result[i] = nSamples > 0 ? nEpochs / nSamples : -1.0;
        }
        return result;
    }

    private static float[] NoisyScaleCoords(double[] coords, Rng rng, double maxCoord, double noise)
    {
        double amax = 0;
        foreach (var c in coords) amax = Math.Max(amax, Math.Abs(c));
        double expansion = amax > 0 ? maxCoord / amax : 1.0;
        var res = new float[coords.Length];
        for (int i = 0; i < coords.Length; i++)
            res[i] = (float)(coords[i] * expansion) + (float)(rng.NextGaussian() * noise);
        return res;
    }

    // ---------- spectral initialization (umap/spectral.py) ----------

    private double[] SpectralLayout(float[][] x, SparseGraph graph, int dim, Rng rng)
    {
        int n = graph.N;
        var (labels, nComponents) = graph.ConnectedComponents();
        if (nComponents > 1) return MultiComponentLayout(x, graph, nComponents, labels, dim, rng);

        int k = dim + 1;
        var vecs = k < n ? SpectralSolver.TopNormalizedAdjacency(graph, k) : null;
        var result = new double[n * dim];
        if (vecs == null)
        {
            // "Spectral initialisation failed! ... Falling back to random initialisation!"
            for (int i = 0; i < result.Length; i++) result[i] = rng.Uniform(-10, 10);
            return result;
        }
        // order = argsort(eigenvalues of L)[1:k] → skip the trivial eigenvector
        for (int c = 1; c < k; c++)
            for (int i = 0; i < n; i++) result[i * dim + (c - 1)] = vecs[c][i];
        return result;
    }

    private static double[] MultiComponentLayout(float[][] x, SparseGraph graph, int nComponents, int[] labels, int dim, Rng rng)
    {
        int n = graph.N;
        var result = new double[n * dim];
        double[] meta = nComponents > 2 * dim ? ComponentLayout(x, nComponents, labels, dim) : AxisMeta(nComponents, dim);
        for (int label = 0; label < nComponents; label++)
        {
            int lab = label;
            var members = Enumerable.Range(0, n).Where(i => labels[i] == lab).ToArray();
            double dataRange = DataRange(meta, nComponents, label, dim);
            int m = members.Length;
            double[][]? vecs = null;
            if (!(m < 2 * dim || m <= dim + 1))
                vecs = SpectralSolver.TopNormalizedAdjacency(graph.Subgraph(members), dim + 1);
            if (vecs == null) RandomComponent(result, members, meta, label, dim, dataRange, rng);
            else PlaceComponent(result, members, meta, label, dim, dataRange, vecs);
        }
        return result;
    }

    /// <summary>Few components: one per axis direction, +1 or -1 on axis c % ceil(n/2).</summary>
    private static double[] AxisMeta(int nComponents, int dim)
    {
        int kk = (int)Math.Ceiling(nComponents / 2.0);
        var meta = new double[nComponents * dim];
        for (int c = 0; c < nComponents; c++)
        {
            int axis = c % kk;
            meta[c * dim + axis] = c < kk ? 1.0 : -1.0;
        }
        return meta;
    }

    /// <summary>Half the distance to the nearest other component's meta position (1 when there is none).</summary>
    private static double DataRange(double[] meta, int nComponents, int label, int dim)
    {
        double minDist = double.PositiveInfinity;
        for (int o = 0; o < nComponents; o++)
        {
            double s = 0;
            for (int d = 0; d < dim; d++) { double t = meta[label * dim + d] - meta[o * dim + d]; s += t * t; }
            double dist = Math.Sqrt(s);
            if (dist > 0 && dist < minDist) minDist = dist;
        }
        return double.IsPositiveInfinity(minDist) ? 1.0 : minDist / 2.0;
    }

    private static void RandomComponent(double[] result, int[] members, double[] meta, int label, int dim, double dataRange, Rng rng)
    {
        foreach (var i in members)
            for (int d = 0; d < dim; d++)
                result[i * dim + d] = rng.Uniform(-dataRange, dataRange) + meta[label * dim + d];
    }

    private static void PlaceComponent(double[] result, int[] members, double[] meta, int label, int dim, double dataRange, double[][] vecs)
    {
        double amax = 0;
        for (int c = 1; c <= dim; c++) foreach (var v in vecs[c]) amax = Math.Max(amax, Math.Abs(v));
        double expansion = amax > 0 ? dataRange / amax : 1.0;
        for (int t = 0; t < members.Length; t++)
            for (int c = 1; c <= dim; c++)
                result[members[t] * dim + (c - 1)] = vecs[c][t] * expansion + meta[label * dim + (c - 1)];
    }

    /// <summary>
    /// spectral.py component_layout: centroids → cosine distances → affinity exp(-d²) → sklearn SpectralEmbedding
    /// (normalized Laplacian eigenmaps, diffusion-map scaling by 1/sqrt(degree), first vector dropped).
    /// </summary>
    private static double[] ComponentLayout(float[][] x, int nComponents, int[] labels, int dim)
    {
        var centroids = Centroids(x, nComponents, labels);
        int nc = nComponents;
        var aff = new double[nc * nc];
        for (int i = 0; i < nc; i++)
            for (int j = 0; j < nc; j++)
                aff[i * nc + j] = i == j ? 0 : Affinity(centroids[i], centroids[j]); // csgraph laplacian ignores the diagonal
        var deg = new double[nc];
        for (int i = 0; i < nc; i++) for (int j = 0; j < nc; j++) deg[i] += aff[i * nc + j];
        var sq = deg.Select(v => Math.Sqrt(Math.Max(v, 1e-300))).ToArray();
        var m = new double[nc * nc];
        for (int i = 0; i < nc; i++) for (int j = 0; j < nc; j++) m[i * nc + j] = aff[i * nc + j] / (sq[i] * sq[j]);
        var vecs = SpectralSolver.TopDense(m, nc, dim + 1);
        var meta = new double[nc * dim];
        for (int c = 1; c <= dim; c++)
        {
            var v = vecs[c].Select((val, i) => val / sq[i]).ToArray();
            SpectralSolver.SignFlip(v);
            for (int i = 0; i < nc; i++) meta[i * dim + (c - 1)] = v[i];
        }
        return meta;
    }

    /// <summary>exp(-d*d) of the clamped cosine distance between two centroids.</summary>
    private static double Affinity(float[] a, float[] b)
    {
        double dist = Math.Clamp(1.0 - VectorMath.Cosine(a, b), 0, 2);
        return Math.Exp(-(dist * dist));
    }

    private static float[][] Centroids(float[][] x, int nComponents, int[] labels)
    {
        int d0 = x[0].Length;
        var centroids = new float[nComponents][];
        var counts = new int[nComponents];
        for (int c = 0; c < nComponents; c++) centroids[c] = new float[d0];
        for (int i = 0; i < x.Length; i++)
        {
            counts[labels[i]]++;
            var cen = centroids[labels[i]];
            for (int j = 0; j < d0; j++) cen[j] += x[i][j];
        }
        for (int c = 0; c < nComponents; c++)
            for (int j = 0; j < d0; j++) centroids[c][j] /= counts[c];
        return centroids;
    }

    // ---------- SGD (umap/layouts.py optimize_layout_euclidean) ----------

    private static float Clip(float v) => v > 4f ? 4f : v < -4f ? -4f : v;

    private static long TauRandInt(long[] s)
    {
        s[0] = (((s[0] & 4294967294L) << 12) & 0xffffffffL) ^ ((((s[0] << 13) & 0xffffffffL) ^ s[0]) >> 19);
        s[1] = (((s[1] & 4294967288L) << 4) & 0xffffffffL) ^ ((((s[1] << 2) & 0xffffffffL) ^ s[1]) >> 25);
        s[2] = (((s[2] & 4294967280L) << 17) & 0xffffffffL) ^ ((((s[2] << 3) & 0xffffffffL) ^ s[2]) >> 11);
        return s[0] ^ s[1] ^ s[2];
    }

    private static void OptimizeLayoutEuclidean(float[] emb, int nVertices, int dim, int[] head, int[] tail, int nEpochs,
        double[] epochsPerSample, float a, float b, long[] rngState, float gamma, float initialAlpha, int negativeSampleRate, bool parallel)
    {
        int m = epochsPerSample.Length;
        var epochsPerNeg = new double[m];
        var nextNeg = new double[m];
        var nextSample = new double[m];
        for (int i = 0; i < m; i++)
        {
            epochsPerNeg[i] = epochsPerSample[i] / negativeSampleRate;
            nextNeg[i] = epochsPerNeg[i];
            nextSample[i] = epochsPerSample[i];
        }
        float alpha = initialAlpha;

        float Dist2(int cj, int ck)
        {
            float dist2 = 0;
            for (int d = 0; d < dim; d++) { float t = emb[cj + d] - emb[ck + d]; dist2 += t * t; }
            return dist2;
        }

        // Pull head and tail together (move_other=True: head == tail embedding).
        void Attract(int cj, int ck)
        {
            float dist2 = Dist2(cj, ck);
            float gradCoeff = 0f;
            if (dist2 > 0f)
            {
                gradCoeff = -2.0f * a * b * MathF.Pow(dist2, b - 1.0f);
                gradCoeff /= a * MathF.Pow(dist2, b) + 1.0f;
            }
            for (int d = 0; d < dim; d++)
            {
                float g = Clip(gradCoeff * (emb[cj + d] - emb[ck + d]));
                emb[cj + d] += g * alpha;
                emb[ck + d] += -g * alpha;
            }
        }

        // Push the head away from one random vertex (negative sample); a self-sample at distance 0 is skipped.
        void Repel(int j, int cj, long[] state)
        {
            int k = (int)(TauRandInt(state) % nVertices);
            int ck = k * dim;
            float dist2 = Dist2(cj, ck);
            float gradCoeff;
            if (dist2 > 0f)
            {
                gradCoeff = 2.0f * gamma * b;
                gradCoeff /= (0.001f + dist2) * (a * MathF.Pow(dist2, b) + 1f);
            }
            else if (j == k) return;
            else gradCoeff = 0f;
            for (int d = 0; d < dim; d++)
            {
                float g = gradCoeff > 0f ? Clip(gradCoeff * (emb[cj + d] - emb[ck + d])) : 0f;
                emb[cj + d] += g * alpha;
            }
        }

        void Edges(int from, int to, int epoch, long[] state)
        {
            for (int i = from; i < to; i++)
            {
                if (epochsPerSample[i] < 0 || nextSample[i] > epoch) continue;
                int j = head[i];
                int cj = j * dim;
                Attract(cj, tail[i] * dim);
                nextSample[i] += epochsPerSample[i];

                int nNeg = (int)((epoch - nextNeg[i]) / epochsPerNeg[i]);
                for (int p = 0; p < nNeg; p++) Repel(j, cj, state);
                nextNeg[i] += nNeg * epochsPerNeg[i];
            }
        }

        int partitions = parallel ? Math.Max(1, Environment.ProcessorCount) : 1;
        var states = new long[partitions][];
        for (int p = 0; p < partitions; p++)
            states[p] = p == 0 ? rngState : [rngState[0] + 0xAFFE * p, rngState[1] ^ (0x1234L * p), rngState[2] + 977L * p];

        for (int epoch = 0; epoch < nEpochs; epoch++)
        {
            if (partitions == 1)
            {
                Edges(0, m, epoch, states[0]);
            }
            else
            {
                int ep = epoch;
                Parallel.For(0, partitions, p =>
                {
                    int from = (int)((long)m * p / partitions), to = (int)((long)m * (p + 1) / partitions);
                    Edges(from, to, ep, states[p]);
                });
            }
            alpha = initialAlpha * (1.0f - (float)epoch / nEpochs);
        }
    }
}
