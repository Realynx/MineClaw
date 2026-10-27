namespace MineClaw.Raptor.Umap;

/// <summary>
/// umap_.py find_ab_params: least-squares fit of <c>1 / (1 + a·x^(2b))</c> to the target membership curve
/// (1 below min_dist, exp(-(x - min_dist)/spread) above) on linspace(0, 3·spread, 300), p0 = (1, 1).
/// scipy uses MINPACK Levenberg–Marquardt; this is a plain LM with the analytic Jacobian (same minimum).
/// For the defaults (1.0, 0.1) it gives a ≈ 1.5769, b ≈ 0.8951.
/// </summary>
public static class AbParams
{
    private static readonly Dictionary<(double, double), (double, double)> Cache = new();

    public static (double A, double B) Find(double spread, double minDist)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((spread, minDist), out var hit)) return hit;
            var r = Fit(spread, minDist);
            Cache[(spread, minDist)] = r;
            return r;
        }
    }

    private static (double, double) Fit(double spread, double minDist)
    {
        const int N = 300;
        var xv = new double[N];
        var yv = new double[N];
        for (int i = 0; i < N; i++)
        {
            xv[i] = spread * 3 * i / (N - 1);
            yv[i] = xv[i] < minDist ? 1.0 : Math.Exp(-(xv[i] - minDist) / spread);
        }
        double a = 1, b = 1, lambda = 1e-3;
        double cost = Cost(xv, yv, a, b);
        for (int iter = 0; iter < 500; iter++)
        {
            double jtj00 = 0, jtj01 = 0, jtj11 = 0, g0 = 0, g1 = 0;
            for (int i = 0; i < N; i++)
            {
                double x = xv[i];
                double p = x > 0 ? Math.Pow(x, 2 * b) : 0;
                double den = 1 + a * p;
                double f = 1 / den;
                double r = f - yv[i];
                double da = -p / (den * den);
                double db = x > 0 ? -a * p * 2 * Math.Log(x) / (den * den) : 0;
                jtj00 += da * da; jtj01 += da * db; jtj11 += db * db;
                g0 += da * r; g1 += db * r;
            }
            bool improved = false;
            for (int tries = 0; tries < 30 && !improved; tries++)
            {
                double m00 = jtj00 * (1 + lambda), m11 = jtj11 * (1 + lambda), m01 = jtj01;
                double det = m00 * m11 - m01 * m01;
                if (Math.Abs(det) < 1e-300) { lambda *= 10; continue; }
                double sa = -(m11 * g0 - m01 * g1) / det;
                double sb = -(-m01 * g0 + m00 * g1) / det;
                double na = a + sa, nb = b + sb;
                double nc = Cost(xv, yv, na, nb);
                if (nc < cost)
                {
                    bool done = Math.Abs(cost - nc) < 1e-15 * Math.Max(1, cost);
                    a = na; b = nb; cost = nc; lambda = Math.Max(lambda / 10, 1e-12); improved = true;
                    if (done) return (a, b);
                }
                else lambda *= 10;
            }
            if (!improved) break;
        }
        return (a, b);
    }

    private static double Cost(double[] xv, double[] yv, double a, double b)
    {
        double s = 0;
        for (int i = 0; i < xv.Length; i++)
        {
            double p = xv[i] > 0 ? Math.Pow(xv[i], 2 * b) : 0;
            double r = 1 / (1 + a * p) - yv[i];
            s += r * r;
        }
        return s;
    }
}
