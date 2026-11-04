using System.Numerics;
using MineClaw.Raptor.Numerics;

namespace MineClaw.ViewerBridge.Graph;

/// <summary>
/// Metric MDS for the memory cloud's default ("Distances") arrangement: a 2-D layout whose Euclidean distances approximate the
/// leaves' cosine distances (δ = 1 − cos of the unit-normalised embeddings), by minimising raw stress Σ (δᵢⱼ − dᵢⱼ)² over all pairs.
/// <list type="number">
/// <item>Start: landmark (classical) MDS — L max-min landmarks (deterministic: from the point farthest from the first), Torgerson
/// on their δ², every other point triangulated from its δ² to them (de Silva &amp; Tenenbaum).</item>
/// <item>Refine: SMACOF (Guttman transform, unit weights) over all n² pairs until an iteration lowers the stress by less than the
/// tolerance; one row per task, SIMD over the row, so the result does not depend on the thread count.</item>
/// <item>Place: a memory outside the fit minimises its own stress against the fixed anchors (the one-point Guttman update),
/// started at the softmax mean of its nearest anchors.</item>
/// </list>
/// Why cosine distance: it is what RAPTOR and the bot's retrieval rank by, so the numbers the UI shows (scale bar, hover) are the
/// ones the bot "sees", and the scale bar is linear in it. It is not a true metric (it is half the squared chord), which only
/// shows as a little extra stress; text embeddings have cos ≥ 0 almost everywhere, so δ stays in [0, 1].
/// </summary>
public static class CloudMds
{
    public sealed record Result(double[] Xy, double Stress, int Iterations);

    private static ParallelOptions Po(CancellationToken ct) => new() { CancellationToken = ct, TaskScheduler = TaskScheduler.Current };

    /// <summary>Layout of unit vectors in cosine-distance units (x, y interleaved), its normalised stress and the SMACOF iterations run.</summary>
    public static Result Fit(float[][] vecs, CloudLayoutOptions o, CancellationToken ct = default)
    {
        int n = vecs.Length;
        // The full dissimilarity matrix (n ≤ 3000: ≤ 36 MB, freed when the fit returns).
        var d = new float[(long)n * n];
        Parallel.For(0, n, Po(ct), i =>
        {
            var vi = vecs[i];
            long row = (long)i * n;
            for (int j = i + 1; j < n; j++) d[row + j] = Math.Max(0f, 1f - VectorMath.Dot(vi, vecs[j]));
        });
        Parallel.For(0, n, Po(ct), i =>
        {
            long row = (long)i * n;
            for (int j = 0; j < i; j++) d[row + j] = d[(long)j * n + i];
        });

        var (x, y) = LandmarkStart(d, n, Math.Min(n, Math.Max(3, o.Landmarks)));
        var (stress, iterations) = Smacof(d, n, x, y, o.SmacofIterations, o.SmacofTolerance, ct);
        var xy = new double[2 * n];
        for (int i = 0; i < n; i++) { xy[2 * i] = x[i]; xy[2 * i + 1] = y[i]; }
        return new Result(xy, stress, iterations);
    }

    /// <summary>Landmark MDS: classical MDS on max-min landmarks, every point triangulated from its squared dissimilarities to them.</summary>
    internal static (float[] X, float[] Y) LandmarkStart(float[] d, int n, int l)
    {
        // Max-min landmarks: start at the point farthest from point 0 (ties → lowest index), then repeatedly the point farthest from the set.
        var chosen = new bool[n];
        var minD = new float[n];
        Array.Fill(minD, float.PositiveInfinity);
        int cur = 0;
        for (int j = 1; j < n; j++) if (d[j] > d[cur]) cur = j;
        var lm = new int[l];
        for (int k = 0; k < l; k++)
        {
            lm[k] = cur;
            chosen[cur] = true;
            long row = (long)cur * n;
            int next = -1;
            for (int j = 0; j < n; j++)
            {
                if (d[row + j] < minD[j]) minD[j] = d[row + j];
                if (!chosen[j] && (next < 0 || minD[j] > minD[next])) next = j;
            }
            if (next < 0) { Array.Resize(ref lm, k + 1); l = k + 1; break; }
            cur = next;
        }

        // Torgerson on the landmarks: B = −½ J Δ² J.
        var d2 = new double[l * l];
        for (int a = 0; a < l; a++)
            for (int b = 0; b < l; b++)
            {
                double v = d[(long)lm[a] * n + lm[b]];
                d2[a * l + b] = v * v;
            }
        var colMean = new double[l];
        double all = 0;
        for (int a = 0; a < l; a++)
            for (int b = 0; b < l; b++) { colMean[b] += d2[a * l + b] / l; all += d2[a * l + b]; }
        all /= (double)l * l;
        var bm = new double[l * l];
        for (int a = 0; a < l; a++)
            for (int b = 0; b < l; b++)
                bm[a * l + b] = -0.5 * (d2[a * l + b] - colMean[a] - colMean[b] + all);
        var eig = new SymmetricEigen(bm, l);
        // Top two (values ascending); pseudo-inverse rows v_k / sqrt(λ_k), sign fixed so the largest |entry| is positive.
        var pinv = new double[2][];
        for (int k = 0; k < 2; k++)
        {
            int col = l - 1 - k;
            double lambda = col >= 0 ? eig.Values[col] : 0;
            pinv[k] = new double[l];
            if (lambda <= 1e-12) continue;
            int arg = 0;
            for (int a = 0; a < l; a++) if (Math.Abs(eig.Vector(a, col)) > Math.Abs(eig.Vector(arg, col))) arg = a;
            double sign = eig.Vector(arg, col) < 0 ? -1 : 1;
            for (int a = 0; a < l; a++) pinv[k][a] = sign * eig.Vector(a, col) / Math.Sqrt(lambda);
        }
        var x = new float[n];
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            long row = (long)i * n;
            double px = 0, py = 0;
            for (int a = 0; a < l; a++)
            {
                double v = d[row + lm[a]];
                double delta = v * v - colMean[a];
                px += pinv[0][a] * delta;
                py += pinv[1][a] * delta;
            }
            x[i] = (float)(-0.5 * px);
            y[i] = (float)(-0.5 * py);
        }
        return (x, y);
    }

    /// <summary>
    /// SMACOF with unit weights: X ← (1/n) B(X) X, i.e. xᵢ ← (1/n) Σⱼ (δᵢⱼ/dᵢⱼ)(xᵢ − xⱼ). Returns the normalised stress
    /// √(Σ(δ − d)² / Σδ²) of the final layout and the iterations run.
    /// </summary>
    internal static (double Stress, int Iterations) Smacof(float[] d, int n, float[] x, float[] y, int maxIter, double tol, CancellationToken ct)
    {
        double sumD2 = 0;
        for (long i = 0; i < (long)n * n; i++) sumD2 += (double)d[i] * d[i];
        sumD2 = Math.Max(sumD2, 1e-12);
        var nx = new float[n];
        var ny = new float[n];
        var rowStress = new double[n];
        double prev = double.MaxValue, stress = 0;
        int it = 0;
        for (; it <= maxIter; it++)
        {
            ct.ThrowIfCancellationRequested();
            var cx = x;
            var cy = y;
            Parallel.For(0, n, Po(ct), i =>
            {
                float xi = cx[i], yi = cy[i];
                var row = new ReadOnlySpan<float>(d, (int)((long)i * n), n);
                int w = Vector<float>.Count, j = 0;
                var vxi = new Vector<float>(xi);
                var vyi = new Vector<float>(yi);
                var eps = new Vector<float>(1e-9f);
                Vector<float> accB = Vector<float>.Zero, accX = Vector<float>.Zero, accY = Vector<float>.Zero, accE = Vector<float>.Zero;
                for (; j + w <= n; j += w)
                {
                    var xj = new Vector<float>(cx, j);
                    var yj = new Vector<float>(cy, j);
                    var dl = new Vector<float>(row.Slice(j, w));
                    var dx = vxi - xj;
                    var dy = vyi - yj;
                    var dist = Vector.SquareRoot(dx * dx + dy * dy);
                    var b = Vector.ConditionalSelect(Vector.GreaterThan(dist, eps), dl / Vector.Max(dist, eps), Vector<float>.Zero);
                    accB += b;
                    accX += b * xj;
                    accY += b * yj;
                    var e = dl - dist;
                    accE += e * e;
                }
                double sb = Vector.Sum(accB), sx = Vector.Sum(accX), sy = Vector.Sum(accY), se = Vector.Sum(accE);
                for (; j < n; j++)
                {
                    double dx = xi - cx[j], dy = yi - cy[j];
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    double b = dist > 1e-9 ? row[j] / dist : 0;
                    sb += b;
                    sx += b * cx[j];
                    sy += b * cy[j];
                    double e = row[j] - dist;
                    se += e * e;
                }
                nx[i] = (float)((sb * xi - sx) / n);
                ny[i] = (float)((sb * yi - sy) / n);
                rowStress[i] = se;
            });
            stress = rowStress.Sum(); // the stress of the layout this pass started from
            if (it == maxIter || prev - stress < tol * prev) break; // keep the measured layout
            prev = stress;
            Array.Copy(nx, x, n);
            Array.Copy(ny, y, n);
        }
        return (Math.Sqrt(stress / sumD2), it);
    }

    /// <summary>
    /// One memory against the fixed anchors (unit-square positions; δ scaled by the fit's transform): minimise Σⱼ (δⱼ − ‖x − xⱼ‖)² by
    /// the one-point Guttman update x ← (1/m) Σⱼ (xⱼ + δⱼ (x − xⱼ)/‖x − xⱼ‖), from the softmax mean of its nearest anchors.
    /// </summary>
    public static (float X, float Y) Place(CloudAnchors a, float[] v, CloudLayoutOptions o)
    {
        int m = a.Index.Length;
        if (m == 0) return (0.5f, 0.5f);
        var sims = new float[m];
        for (int j = 0; j < m; j++) sims[j] = VectorMath.Dot(v, a.Vectors[j]);
        var (x, y, _) = CloudLayout.SoftmaxMean(a, sims, o);
        double s = a.Transform.S;
        for (int it = 0; it < o.PlaceIterations; it++)
        {
            double sx = 0, sy = 0;
            for (int j = 0; j < m; j++)
            {
                double ax = a.Xy[2 * j], ay = a.Xy[2 * j + 1];
                double dx = x - ax, dy = y - ay;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double delta = Math.Max(0, 1 - sims[j]) * s;
                if (dist > 1e-12) { sx += ax + delta * dx / dist; sy += ay + delta * dy / dist; }
                else { sx += ax; sy += ay; }
            }
            double nx = sx / m, ny = sy / m;
            bool done = Math.Abs(nx - x) + Math.Abs(ny - y) < 1e-6;
            x = nx;
            y = ny;
            if (done) break;
        }
        return CloudLayout.Clamp(x, y);
    }
}
