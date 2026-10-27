using MineClaw.Raptor.Numerics;

namespace MineClaw.Raptor.Umap;

/// <summary>umap-learn's <c>umap_.py</c> graph construction: kNN, smooth_knn_dist, membership strengths, fuzzy union.</summary>
public static class FuzzySimplicialSet
{
    private const double SmoothKTolerance = 1e-5;   // umap_.py SMOOTH_K_TOLERANCE
    private const double MinKDistScale = 1e-3;      // umap_.py MIN_K_DIST_SCALE
    private const double CosineDisconnection = 2.0; // umap_.py DISCONNECTION_DISTANCES["cosine"]

    /// <summary>
    /// Exact cosine kNN (self included at column 0 with distance 0). umap-learn computes exact distances below 4096
    /// points and uses pynndescent above; exact is used here at every size (see README).
    /// </summary>
    public static (int[][] Indices, float[][] Distances) NearestNeighbors(float[][] x, int k)
    {
        int n = x.Length;
        var idx = new int[n][];
        var dst = new float[n][];
        void Row(int i)
        {
            // bounded max-heap of (dist, index); ties broken by lower index
            var heapD = new float[k - 1];
            var heapI = new int[k - 1];
            int size = 0;
            var xi = x[i];
            for (int j = 0; j < n; j++)
            {
                if (j == i || k == 1) continue;
                float d = 1f - VectorMath.Dot(xi, x[j]);
                if (d < 0) d = 0;
                if (size < k - 1)
                {
                    heapD[size] = d; heapI[size] = j; size++;
                    SiftUp(heapD, heapI, size - 1);
                }
                else if (Less(d, j, heapD[0], heapI[0]))
                {
                    heapD[0] = d; heapI[0] = j;
                    SiftDown(heapD, heapI, size, 0);
                }
            }
            var order = new int[size];
            for (int t = 0; t < size; t++) order[t] = t;
            Array.Sort(order, (a, b) => Less(heapD[a], heapI[a], heapD[b], heapI[b]) ? -1 : Less(heapD[b], heapI[b], heapD[a], heapI[a]) ? 1 : 0);
            idx[i] = new int[k];
            dst[i] = new float[k];
            idx[i][0] = i;
            dst[i][0] = 0;
            for (int t = 0; t < size; t++)
            {
                idx[i][t + 1] = heapI[order[t]];
                dst[i][t + 1] = heapD[order[t]];
            }
        }
        if ((long)n * n > 4_000_000) Parallel.For(0, n, Row);
        else for (int i = 0; i < n; i++) Row(i);
        return (idx, dst);
    }

    private static bool Less(float d1, int i1, float d2, int i2) => d1 < d2 || (d1 == d2 && i1 < i2);

    private static void SiftUp(float[] d, int[] ix, int c)
    {
        while (c > 0)
        {
            int p = (c - 1) / 2;
            if (!Less(d[p], ix[p], d[c], ix[c])) break; // max-heap: parent must be "greater"
            (d[p], d[c]) = (d[c], d[p]);
            (ix[p], ix[c]) = (ix[c], ix[p]);
            c = p;
        }
    }

    private static void SiftDown(float[] d, int[] ix, int size, int c)
    {
        while (true)
        {
            int l = 2 * c + 1, r = l + 1, m = c;
            if (l < size && Less(d[m], ix[m], d[l], ix[l])) m = l;
            if (r < size && Less(d[m], ix[m], d[r], ix[r])) m = r;
            if (m == c) return;
            (d[m], d[c]) = (d[c], d[m]);
            (ix[m], ix[c]) = (ix[c], ix[m]);
            c = m;
        }
    }

    /// <summary>umap_.py smooth_knn_dist (n_iter=64, bandwidth=1).</summary>
    public static (double[] Sigmas, double[] Rhos) SmoothKnnDist(float[][] distances, double k, double localConnectivity, int nIter = 64, double bandwidth = 1.0)
    {
        int n = distances.Length;
        double target = Math.Log2(k) * bandwidth;
        var rho = new double[n];
        var result = new double[n];
        double meanDistances = 0;
        long count = 0;
        foreach (var row in distances)
            foreach (var d in row) { meanDistances += d; count++; }
        meanDistances /= Math.Max(1, count);

        for (int i = 0; i < n; i++)
        {
            double lo = 0.0, hi = double.PositiveInfinity, mid = 1.0;
            var ith = distances[i];
            var nonZero = ith.Where(d => d > 0.0).ToArray();
            if (nonZero.Length >= localConnectivity)
            {
                int index = (int)Math.Floor(localConnectivity);
                double interpolation = localConnectivity - index;
                if (index > 0)
                {
                    rho[i] = nonZero[index - 1];
                    if (interpolation > SmoothKTolerance)
                        rho[i] += interpolation * (nonZero[index] - nonZero[index - 1]);
                }
                else
                {
                    rho[i] = interpolation * nonZero[0];
                }
            }
            else if (nonZero.Length > 0)
            {
                rho[i] = nonZero.Max();
            }

            for (int it = 0; it < nIter; it++)
            {
                double psum = 0.0;
                for (int j = 1; j < ith.Length; j++)
                {
                    double d = ith[j] - rho[i];
                    psum += d > 0 ? Math.Exp(-(d / mid)) : 1.0;
                }
                if (Math.Abs(psum - target) < SmoothKTolerance) break;
                if (psum > target)
                {
                    hi = mid;
                    mid = (lo + hi) / 2.0;
                }
                else
                {
                    lo = mid;
                    if (double.IsPositiveInfinity(hi)) mid *= 2;
                    else mid = (lo + hi) / 2.0;
                }
            }
            result[i] = mid;
            if (rho[i] > 0.0)
            {
                double meanIth = ith.Average(d => (double)d);
                if (result[i] < MinKDistScale * meanIth) result[i] = MinKDistScale * meanIth;
            }
            else if (result[i] < MinKDistScale * meanDistances)
            {
                result[i] = MinKDistScale * meanDistances;
            }
        }
        return (result, rho);
    }

    /// <summary>
    /// umap_.py fuzzy_simplicial_set: membership strengths then the fuzzy union
    /// <c>mix·(A + Aᵀ − A∘Aᵀ) + (1 − mix)·(A∘Aᵀ)</c>, zeros eliminated.
    /// </summary>
    public static SparseGraph Build(float[][] x, int nNeighbors, double setOpMixRatio, double localConnectivity)
    {
        int n = x.Length;
        var (knnI, knnD) = NearestNeighbors(x, nNeighbors);
        var (sigmas, rhos) = SmoothKnnDist(knnD, nNeighbors, localConnectivity);

        // A in CSR (sorted columns)
        var aRp = new int[n + 1];
        var aCol = new List<int>(n * nNeighbors);
        var aVal = new List<double>(n * nNeighbors);
        var rowBuf = new List<(int c, double v)>(nNeighbors);
        for (int i = 0; i < n; i++)
        {
            rowBuf.Clear();
            for (int j = 0; j < nNeighbors; j++)
            {
                int c = knnI[i][j];
                double d = knnD[i][j];
                double val;
                if (c == i) val = 0.0;
                else if (d >= CosineDisconnection) val = 0.0;
                else if (d - rhos[i] <= 0.0 || sigmas[i] == 0.0) val = 1.0;
                else val = Math.Exp(-((d - rhos[i]) / sigmas[i]));
                if (val != 0.0) rowBuf.Add((c, val));
            }
            rowBuf.Sort((p, q) => p.c.CompareTo(q.c));
            foreach (var (c, v) in rowBuf) { aCol.Add(c); aVal.Add(v); }
            aRp[i + 1] = aCol.Count;
        }

        // Aᵀ in CSR
        var tCount = new int[n + 1];
        foreach (var c in aCol) tCount[c + 1]++;
        for (int i = 0; i < n; i++) tCount[i + 1] += tCount[i];
        var tRp = (int[])tCount.Clone();
        var tCol = new int[aCol.Count];
        var tVal = new double[aCol.Count];
        var fill = (int[])tCount.Clone();
        for (int i = 0; i < n; i++)
            for (int p = aRp[i]; p < aRp[i + 1]; p++)
            {
                int c = aCol[p];
                int q = fill[c]++;
                tCol[q] = i; // rows visited ascending → sorted
                tVal[q] = aVal[p];
            }

        var rp = new int[n + 1];
        var col = new List<int>(aCol.Count * 2);
        var val2 = new List<double>(aCol.Count * 2);
        for (int i = 0; i < n; i++)
        {
            int p = aRp[i], pe = aRp[i + 1], q = tRp[i], qe = tRp[i + 1];
            while (p < pe || q < qe)
            {
                int c;
                double a = 0, at = 0;
                if (q >= qe || (p < pe && aCol[p] < tCol[q])) { c = aCol[p]; a = aVal[p]; p++; }
                else if (p >= pe || tCol[q] < aCol[p]) { c = tCol[q]; at = tVal[q]; q++; }
                else { c = aCol[p]; a = aVal[p]; at = tVal[q]; p++; q++; }
                double prod = a * at;
                double w = setOpMixRatio * (a + at - prod) + (1.0 - setOpMixRatio) * prod;
                if (w != 0.0) { col.Add(c); val2.Add(w); }
            }
            rp[i + 1] = col.Count;
        }
        return new SparseGraph(n, rp, col.ToArray(), val2.ToArray());
    }
}
