using MineClaw.Raptor.Numerics;

namespace MineClaw.Raptor.Clustering;

/// <summary>sklearn <c>GaussianMixture</c> defaults (sklearn/mixture/_gaussian_mixture.py, _base.py).</summary>
public sealed record GmmOptions
{
    /// <summary>reg_covar default.</summary>
    public double RegCovar { get; init; } = 1e-6;
    /// <summary>tol default (on the change of the per-sample mean log-likelihood lower bound).</summary>
    public double Tol { get; init; } = 1e-3;
    /// <summary>max_iter default.</summary>
    public int MaxIter { get; init; } = 100;
    /// <summary>n_init default (only 1 is used by RAPTOR; more inits keep the best lower bound).</summary>
    public int NInit { get; init; } = 1;
    /// <summary>KMeans max_iter default (init_params="kmeans").</summary>
    public int KMeansMaxIter { get; init; } = 300;
    /// <summary>KMeans tol default.</summary>
    public double KMeansTol { get; init; } = 1e-4;
}

/// <summary>
/// Full-covariance Gaussian mixture fitted by EM, mirroring sklearn: k-means responsibilities as init,
/// <c>nk = Σresp + 10·eps</c>, covariance + reg_covar·I, precision Cholesky factors, log-sum-exp E-step,
/// convergence on |Δ lower bound| &lt; tol, a final E-step, and <c>bic = -2·score·n + p·ln n</c> with
/// <c>p = k·d(d+1)/2 + k·d + k − 1</c>. Throws <see cref="GmmFitException"/> when a covariance is not positive
/// definite even after regularization (sklearn raises ValueError there).
/// </summary>
public sealed class GaussianMixture
{
    private static readonly double Eps = Math.Pow(2, -52);
    private static readonly double Log2Pi = Math.Log(2 * Math.PI);

    public int K { get; }
    public int D { get; }
    public double[] Weights { get; private set; }
    public double[] Means { get; private set; }
    /// <summary>K × D × D.</summary>
    public double[] Covariances { get; private set; }
    /// <summary>K × D × D upper-triangular P with precision = P·Pᵀ (sklearn precisions_cholesky_).</summary>
    public double[] PrecisionsCholesky { get; private set; }
    public bool Converged { get; private set; }
    public int NIter { get; private set; }
    public double LowerBound { get; private set; }

    private GaussianMixture(int k, int d)
    {
        K = k;
        D = d;
        Weights = new double[k];
        Means = new double[k * d];
        Covariances = new double[k * d * d];
        PrecisionsCholesky = new double[k * d * d];
    }

    public static GaussianMixture Fit(double[] x, int n, int d, int k, long randomState, GmmOptions? options = null)
    {
        var o = options ?? new GmmOptions();
        if (n < k) throw new ArgumentException($"Expected n_samples >= n_components but got n_components = {k}, n_samples = {n}");
        var rng = new Rng(randomState);
        GaussianMixture? best = null;
        double maxLower = double.NegativeInfinity;
        var logResp = new double[n * k];
        for (int init = 0; init < o.NInit; init++)
        {
            var g = new GaussianMixture(k, d);
            var labels = KMeans.FitLabels(x, n, d, k, rng, o.KMeansMaxIter, o.KMeansTol);
            var resp = new double[n * k];
            for (int i = 0; i < n; i++) resp[i * k + labels[i]] = 1.0;
            g.EstimateParameters(x, n, resp, o.RegCovar, normalizeWeightsBySum: false);

            double lower = double.NegativeInfinity;
            bool converged = false;
            int iter = 0;
            for (iter = 1; iter <= o.MaxIter; iter++)
            {
                double prev = lower;
                double lpn = g.EStep(x, n, logResp);
                for (int t = 0; t < resp.Length; t++) resp[t] = Math.Exp(logResp[t]);
                g.EstimateParameters(x, n, resp, o.RegCovar, normalizeWeightsBySum: true);
                lower = lpn;
                if (Math.Abs(lower - prev) < o.Tol) { converged = true; break; }
            }
            g.Converged = converged;
            g.NIter = Math.Min(iter, o.MaxIter);
            g.LowerBound = lower;
            if (lower > maxLower || double.IsNegativeInfinity(maxLower))
            {
                maxLower = lower;
                best = g;
            }
        }
        return best!;
    }

    /// <summary>Mean per-sample log-likelihood (sklearn <c>score</c>).</summary>
    public double Score(double[] x, int n) => EStep(x, n, new double[n * K]);

    /// <summary>sklearn <c>_n_parameters</c> for covariance_type="full".</summary>
    public int NParameters => K * D * (D + 1) / 2 + D * K + K - 1;

    /// <summary>sklearn <c>bic</c>: -2·score(X)·n + n_parameters·ln(n).</summary>
    public double Bic(double[] x, int n) => -2 * Score(x, n) * n + NParameters * Math.Log(n);

    /// <summary>n × K responsibilities (rows sum to 1).</summary>
    public double[] PredictProba(double[] x, int n)
    {
        var lr = new double[n * K];
        EStep(x, n, lr);
        for (int t = 0; t < lr.Length; t++) lr[t] = Math.Exp(lr[t]);
        return lr;
    }

    /// <summary>Fills log responsibilities; returns mean log_prob_norm.</summary>
    private double EStep(double[] x, int n, double[] logResp)
    {
        int k = K, d = D;
        var logDet = new double[k];
        var logW = new double[k];
        for (int c = 0; c < k; c++)
        {
            double s = 0;
            for (int j = 0; j < d; j++) s += Math.Log(PrecisionsCholesky[c * d * d + j * d + j]);
            logDet[c] = s;
            logW[c] = Math.Log(Weights[c]);
        }
        var lpn = new double[n];
        void Row(int i)
        {
            Span<double> y = stackalloc double[d];
            double max = double.NegativeInfinity;
            for (int c = 0; c < k; c++)
            {
                int pc = c * d * d;
                double sq = 0;
                // y = (x - mu) @ P, P upper-triangular
                for (int col = 0; col < d; col++)
                {
                    double v = 0;
                    for (int r = 0; r <= col; r++) v += (x[i * d + r] - Means[c * d + r]) * PrecisionsCholesky[pc + r * d + col];
                    sq += v * v;
                }
                double w = -0.5 * (d * Log2Pi + sq) + logDet[c] + logW[c];
                logResp[i * k + c] = w;
                if (w > max) max = w;
            }
            double sum = 0;
            for (int c = 0; c < k; c++) sum += Math.Exp(logResp[i * k + c] - max);
            double lse = max + Math.Log(sum);
            lpn[i] = lse;
            for (int c = 0; c < k; c++) logResp[i * k + c] -= lse;
        }
        if ((long)n * k * d * d > 2_000_000) Parallel.For(0, n, Row);
        else for (int i = 0; i < n; i++) Row(i);
        double total = 0;
        for (int i = 0; i < n; i++) total += lpn[i];
        return total / n;
    }

    /// <summary>_estimate_gaussian_parameters + _compute_precision_cholesky ("full").</summary>
    private void EstimateParameters(double[] x, int n, double[] resp, double reg, bool normalizeWeightsBySum)
    {
        int k = K, d = D;
        var nk = new double[k];
        for (int i = 0; i < n; i++) for (int c = 0; c < k; c++) nk[c] += resp[i * k + c];
        for (int c = 0; c < k; c++) nk[c] += 10 * Eps;
        var means = new double[k * d];
        var covs = new double[k * d * d];
        void CompMean(int c)
        {
            for (int i = 0; i < n; i++)
            {
                double r = resp[i * k + c];
                if (r == 0) continue;
                for (int j = 0; j < d; j++) means[c * d + j] += r * x[i * d + j];
            }
            for (int j = 0; j < d; j++) means[c * d + j] /= nk[c];
        }
        void CompCovariance(int c)
        {
            Span<double> diff = stackalloc double[d];
            int pc = c * d * d;
            for (int i = 0; i < n; i++)
            {
                double r = resp[i * k + c];
                if (r == 0) continue;
                for (int j = 0; j < d; j++) diff[j] = x[i * d + j] - means[c * d + j];
                for (int a = 0; a < d; a++)
                {
                    double ra = r * diff[a];
                    for (int b = a; b < d; b++) covs[pc + a * d + b] += ra * diff[b];
                }
            }
            for (int a = 0; a < d; a++)
                for (int b = a; b < d; b++)
                {
                    double v = covs[pc + a * d + b] / nk[c];
                    covs[pc + a * d + b] = v;
                    covs[pc + b * d + a] = v;
                }
            for (int a = 0; a < d; a++) covs[pc + a * d + a] += reg;
        }
        void Comp(int c)
        {
            CompMean(c);
            CompCovariance(c);
        }
        if ((long)n * d * d > 200_000 && k > 1) Parallel.For(0, k, Comp);
        else for (int c = 0; c < k; c++) Comp(c);

        double wsum = 0;
        for (int c = 0; c < k; c++) { Weights[c] = nk[c] / n; wsum += Weights[c]; }
        if (normalizeWeightsBySum) for (int c = 0; c < k; c++) Weights[c] /= wsum;
        Means = means;
        Covariances = covs;
        PrecisionsCholesky = ComputePrecisionCholesky(covs, k, d);
    }

    private static double[] ComputePrecisionCholesky(double[] covs, int k, int d)
    {
        var prec = new double[k * d * d];
        var l = new double[d * d];
        var inv = new double[d * d];
        for (int c = 0; c < k; c++)
        {
            int pc = c * d * d;
            CholeskyLower(covs, pc, d, l);
            InvertLower(l, d, inv);
            // precisions_chol = inv.T (upper)
            for (int i = 0; i < d; i++)
                for (int j = 0; j < d; j++) prec[pc + i * d + j] = inv[j * d + i];
        }
        return prec;
    }

    /// <summary>Lower Cholesky factor of the covariance at offset pc into l; throws on a non-positive pivot.</summary>
    private static void CholeskyLower(double[] covs, int pc, int d, double[] l)
    {
        Array.Clear(l);
        for (int i = 0; i < d; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = covs[pc + i * d + j];
                for (int t = 0; t < j; t++) s -= l[i * d + t] * l[j * d + t];
                if (i == j)
                {
                    if (!(s > 0) || double.IsNaN(s))
                        throw new GmmFitException("Fitting the mixture model failed because some components have ill-defined empirical covariance (for instance caused by singleton or collapsed samples). Try to decrease the number of components, or increase reg_covar.");
                    l[i * d + i] = Math.Sqrt(s);
                }
                else l[i * d + j] = s / l[j * d + j];
            }
    }

    /// <summary>inv = L^-1 (lower), solve_triangular(L, I, lower=True).</summary>
    private static void InvertLower(double[] l, int d, double[] inv)
    {
        Array.Clear(inv);
        for (int col = 0; col < d; col++)
            for (int i = col; i < d; i++)
            {
                double s = i == col ? 1.0 : 0.0;
                for (int t = col; t < i; t++) s -= l[i * d + t] * inv[t * d + col];
                inv[i * d + col] = s / l[i * d + i];
            }
    }
}

public sealed class GmmFitException(string message) : Exception(message);
