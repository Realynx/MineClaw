using MineClaw.Raptor.Numerics;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>
/// PCA for the memory cloud: the top two principal axes of the unit-normalised leaf embeddings, by seeded block (subspace) power
/// iteration on Xcᵀ Xc without forming the 1024×1024 covariance. Linear and global: x = PC1, y = PC2, and every leaf (new ones too)
/// is an exact projection. Axis signs are fixed (largest |entry| positive), so the picture is deterministic.
/// </summary>
public static class CloudPca
{
    private const int Block = 4;

    public sealed record Result(float[] Mean, float[][] Axes, double[] VarianceExplained)
    {
        private readonly double[] _meanDot = Axes.Select(a => (double)VectorMath.Dot(Mean, a)).ToArray();

        public (double X, double Y) Project(float[] v) => (VectorMath.Dot(v, Axes[0]) - _meanDot[0], VectorMath.Dot(v, Axes[1]) - _meanDot[1]);
    }

    public static Result Fit(float[][] vecs, long seed, CancellationToken ct = default, int maxIter = 150)
    {
        int n = vecs.Length, dims = vecs[0].Length;
        var (mean, total) = MeanAndVariance(vecs, dims);
        var meanF = mean.Select(m => (float)m).ToArray();
        int b = Math.Min(Block, Math.Max(2, Math.Min(n, dims)));
        var rng = new Rng(seed);
        var q = new double[b][];
        for (int c = 0; c < b; c++) q[c] = Enumerable.Range(0, dims).Select(_ => rng.NextGaussian()).ToArray();
        Orthonormalise(q);
        var z = new double[n * b];
        var lambda = new double[b];
        for (int it = 0; it < maxIter; it++)
        {
            ct.ThrowIfCancellationRequested();
            var old = q;
            q = PowerStep(vecs, q, mean, meanF, z, lambda, ct);
            if (Converged(q, old, dims)) break;
        }
        var axes = new float[2][];
        for (int c = 0; c < 2; c++) axes[c] = SignFixed(q[c], dims);
        var ve = lambda.Take(2).Select(l => total > 0 ? Math.Round(Math.Max(0, l) / total, 4) : 0).ToArray();
        return new Result(meanF, axes, ve);
    }

    /// <summary>The mean vector and the total variance (mean squared distance from it).</summary>
    private static (double[] Mean, double Total) MeanAndVariance(float[][] vecs, int dims)
    {
        int n = vecs.Length;
        var mean = new double[dims];
        foreach (var v in vecs) for (int k = 0; k < dims; k++) mean[k] += v[k];
        for (int k = 0; k < dims; k++) mean[k] /= n;
        double total = 0;
        foreach (var v in vecs) for (int k = 0; k < dims; k++) { double e = v[k] - mean[k]; total += e * e; }
        return (mean, total / n);
    }

    /// <summary>One block power iteration: Z = Xc Q, W = Xcᵀ Z / n (the Rayleigh quotients into <paramref name="lambda"/>), then W
    /// orthonormalised as the next Q.</summary>
    private static double[][] PowerStep(float[][] vecs, double[][] q, double[] mean, float[] meanF, double[] z, double[] lambda, CancellationToken ct)
    {
        int n = vecs.Length, b = q.Length, dims = mean.Length;
        var qf = q.Select(col => col.Select(x => (float)x).ToArray()).ToArray();
        var meanDot = qf.Select(col => (double)VectorMath.Dot(meanF, col)).ToArray();
        // Z = Xc Q
        Parallel.For(0, n, new ParallelOptions { CancellationToken = ct, TaskScheduler = TaskScheduler.Current }, i =>
        {
            for (int c = 0; c < b; c++) z[i * b + c] = VectorMath.Dot(vecs[i], qf[c]) - meanDot[c];
        });
        var (wf, zsum) = Accumulate(vecs, z, b, dims);
        var w = wf.Select(col => col.Select(x => (double)x).ToArray()).ToArray();
        for (int c = 0; c < b; c++)
        {
            double lam = 0;
            for (int k = 0; k < dims; k++)
            {
                w[c][k] = (w[c][k] - zsum[c] * mean[k]) / n;
                lam += w[c][k] * q[c][k];
            }
            lambda[c] = lam;
        }
        Orthonormalise(w);
        return w;
    }

    /// <summary>Xᵀ Z (sequential: a fixed summation order; SIMD axpy in float) and the column sums of Z.</summary>
    private static (float[][] Wf, double[] ZSum) Accumulate(float[][] vecs, double[] z, int b, int dims)
    {
        var wf = new float[b][];
        for (int c = 0; c < b; c++) wf[c] = new float[dims];
        var zsum = new double[b];
        for (int i = 0; i < vecs.Length; i++)
        {
            var v = vecs[i];
            for (int c = 0; c < b; c++)
            {
                double zc = z[i * b + c];
                if (zc == 0) continue;
                zsum[c] += zc;
                Axpy((float)zc, v, wf[c]);
            }
        }
        return (wf, zsum);
    }

    /// <summary>The top two directions moved by under 1e-9 (|cos| to the previous ones).</summary>
    private static bool Converged(double[][] q, double[][] old, int dims)
    {
        for (int c = 0; c < 2; c++)
        {
            double dot = 0;
            for (int k = 0; k < dims; k++) dot += q[c][k] * old[c][k];
            if (1 - Math.Abs(dot) > 1e-9) return false;
        }
        return true;
    }

    /// <summary>The axis as floats, its sign fixed so the largest |entry| is positive.</summary>
    private static float[] SignFixed(double[] axis, int dims)
    {
        int arg = 0;
        for (int k = 0; k < dims; k++) if (Math.Abs(axis[k]) > Math.Abs(axis[arg])) arg = k;
        double sign = axis[arg] < 0 ? -1 : 1;
        return axis.Select(x => (float)(sign * x)).ToArray();
    }

    private static void Axpy(float a, float[] x, float[] y)
    {
        int k = 0, w = System.Numerics.Vector<float>.Count;
        var va = new System.Numerics.Vector<float>(a);
        for (; k + w <= x.Length; k += w)
            (new System.Numerics.Vector<float>(y, k) + va * new System.Numerics.Vector<float>(x, k)).CopyTo(y, k);
        for (; k < x.Length; k++) y[k] += a * x[k];
    }

    /// <summary>Modified Gram–Schmidt, in column order (column 0 stays the plain power iterate).</summary>
    private static void Orthonormalise(double[][] cols)
    {
        for (int c = 0; c < cols.Length; c++)
        {
            for (int p = 0; p < c; p++)
            {
                double dot = 0;
                for (int k = 0; k < cols[c].Length; k++) dot += cols[c][k] * cols[p][k];
                for (int k = 0; k < cols[c].Length; k++) cols[c][k] -= dot * cols[p][k];
            }
            double norm = Math.Sqrt(cols[c].Sum(x => x * x));
            if (norm < 1e-300) { cols[c][c % cols[c].Length] = 1; norm = 1; }
            for (int k = 0; k < cols[c].Length; k++) cols[c][k] /= norm;
        }
    }
}
