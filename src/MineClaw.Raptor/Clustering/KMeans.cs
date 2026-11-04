using MineClaw.Raptor.Numerics;

namespace MineClaw.Raptor.Clustering;

/// <summary>
/// sklearn <c>KMeans(n_clusters, n_init=1, random_state)</c> as used by <c>GaussianMixture(init_params="kmeans")</c>:
/// greedy k-means++ seeding (n_local_trials = 2 + ln k), Lloyd iterations (max_iter=300, tol=1e-4 scaled by the
/// mean feature variance), strict-convergence check on unchanged labels, empty clusters relocated to the farthest
/// points, and a final assignment pass when not strictly converged.
/// </summary>
public static class KMeans
{
    public static int[] FitLabels(double[] x, int n, int d, int k, Rng rng, int maxIter = 300, double tol = 1e-4)
    {
        var centers = PlusPlus(x, n, d, k, rng);
        double tolAbs = Tolerance(x, n, d, tol);
        var labels = new int[n];
        var labelsOld = new int[n];
        Array.Fill(labelsOld, -1);
        var dist = new double[n];
        bool strict = false;
        var newCenters = new double[k * d];
        var counts = new int[k];
        for (int it = 0; it < maxIter; it++)
        {
            Assign(x, n, d, k, centers, labels, dist);
            UpdateCenters(x, n, d, k, labels, dist, newCenters, counts);
            double shift = SquaredShift(newCenters, centers, k * d);
            Array.Copy(newCenters, centers, k * d);
            if (labels.AsSpan().SequenceEqual(labelsOld)) { strict = true; break; }
            if (shift <= tolAbs) break;
            Array.Copy(labels, labelsOld, n);
        }
        if (!strict) Assign(x, n, d, k, centers, labels, dist);
        return labels;
    }

    /// <summary>_tolerance: mean(var(X, axis=0)) * tol.</summary>
    private static double Tolerance(double[] x, int n, int d, double tol)
    {
        double meanVar = 0;
        for (int j = 0; j < d; j++)
        {
            double mu = 0, s = 0;
            for (int i = 0; i < n; i++) mu += x[i * d + j];
            mu /= n;
            for (int i = 0; i < n; i++) { double t = x[i * d + j] - mu; s += t * t; }
            meanVar += s / n;
        }
        return meanVar / d * tol;
    }

    /// <summary>Cluster means of the current labels, empty clusters relocated first.</summary>
    private static void UpdateCenters(double[] x, int n, int d, int k, int[] labels, double[] dist, double[] newCenters, int[] counts)
    {
        Array.Clear(newCenters);
        Array.Clear(counts);
        for (int i = 0; i < n; i++)
        {
            int c = labels[i];
            counts[c]++;
            for (int j = 0; j < d; j++) newCenters[c * d + j] += x[i * d + j];
        }
        RelocateEmpty(x, n, d, k, labels, dist, newCenters, counts);
        for (int c = 0; c < k; c++)
            if (counts[c] > 0)
                for (int j = 0; j < d; j++) newCenters[c * d + j] /= counts[c];
    }

    private static double SquaredShift(double[] newCenters, double[] centers, int len)
    {
        double shift = 0;
        for (int t = 0; t < len; t++) { double s = newCenters[t] - centers[t]; shift += s * s; }
        return shift;
    }

    private static void Assign(double[] x, int n, int d, int k, double[] centers, int[] labels, double[] dist)
    {
        void Row(int i)
        {
            double best = double.PositiveInfinity;
            int arg = 0;
            for (int c = 0; c < k; c++)
            {
                double s = 0;
                for (int j = 0; j < d; j++) { double t = x[i * d + j] - centers[c * d + j]; s += t * t; }
                if (s < best) { best = s; arg = c; }
            }
            labels[i] = arg;
            dist[i] = best;
        }
        if ((long)n * k > 200_000) Parallel.For(0, n, Row);
        else for (int i = 0; i < n; i++) Row(i);
    }

    private static void RelocateEmpty(double[] x, int n, int d, int k, int[] labels, double[] dist, double[] sums, int[] counts)
    {
        var empty = Enumerable.Range(0, k).Where(c => counts[c] == 0).ToArray();
        if (empty.Length == 0) return;
        var far = Enumerable.Range(0, n).OrderByDescending(i => dist[i]).ThenBy(i => i).Take(empty.Length).ToArray();
        for (int t = 0; t < empty.Length && t < far.Length; t++)
        {
            int i = far[t], old = labels[i], c = empty[t];
            if (counts[old] <= 1) continue;
            for (int j = 0; j < d; j++)
            {
                sums[old * d + j] -= x[i * d + j];
                sums[c * d + j] = x[i * d + j];
            }
            counts[old]--;
            counts[c] = 1;
            labels[i] = c;
        }
    }

    /// <summary>sklearn _kmeans_plusplus with uniform sample weights.</summary>
    public static double[] PlusPlus(double[] x, int n, int d, int k, Rng rng)
    {
        var centers = new double[k * d];
        int trials = 2 + (int)Math.Log(k);
        int first = rng.NextInt(n);
        Array.Copy(x, first * d, centers, 0, d);
        var closest = new double[n];
        for (int i = 0; i < n; i++) closest[i] = SqDist(x, i, x, first, d);
        double pot = closest.Sum();
        var cand = new int[trials];
        var candDist = new double[trials][];
        for (int c = 1; c < k; c++)
        {
            SampleCandidates(rng, closest, pot, n, cand);
            var (bestT, bestPot) = BestCandidate(x, d, n, closest, cand, candDist);
            pot = bestPot;
            Array.Copy(candDist[bestT], closest, n);
            Array.Copy(x, cand[bestT] * d, centers, c * d, d);
        }
        return centers;
    }

    /// <summary>searchsorted(stable_cumsum(closest), uniform * pot) for each trial.</summary>
    private static void SampleCandidates(Rng rng, double[] closest, double pot, int n, int[] cand)
    {
        var cum = new double[n];
        double acc = 0;
        for (int i = 0; i < n; i++) { acc += closest[i]; cum[i] = acc; }
        for (int t = 0; t < cand.Length; t++)
        {
            double r = rng.NextDouble() * pot;
            int lo = 0, hi = n;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (cum[mid] < r) lo = mid + 1; else hi = mid; }
            cand[t] = Math.Min(lo, n - 1);
        }
    }

    /// <summary>The trial whose new closest-distance potential is smallest (first on ties).</summary>
    private static (int BestT, double BestPot) BestCandidate(double[] x, int d, int n, double[] closest, int[] cand, double[][] candDist)
    {
        int bestT = 0;
        double bestPot = double.PositiveInfinity;
        for (int t = 0; t < cand.Length; t++)
        {
            var dd = candDist[t] ??= new double[n];
            double s = 0;
            for (int i = 0; i < n; i++) { dd[i] = Math.Min(closest[i], SqDist(x, i, x, cand[t], d)); s += dd[i]; }
            if (s < bestPot) { bestPot = s; bestT = t; }
        }
        return (bestT, bestPot);
    }

    private static double SqDist(double[] a, int ia, double[] b, int ib, int d)
    {
        double s = 0;
        for (int j = 0; j < d; j++) { double t = a[ia * d + j] - b[ib * d + j]; s += t * t; }
        return s;
    }
}
