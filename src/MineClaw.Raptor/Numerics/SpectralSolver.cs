namespace MineClaw.Raptor.Numerics;

/// <summary>
/// Leading eigenvectors of the symmetric normalized adjacency M = D^-1/2 W D^-1/2. The largest eigenvalues of M are
/// the smallest of the normalized Laplacian L = I - M, which is what umap-learn asks ARPACK for
/// (<c>eigsh(L, k, which="SM", ncv=max(2k+1, sqrt(n)), tol=1e-4, v0=ones)</c>). Small graphs are solved densely
/// (exact); larger ones with a thick-restart Lanczos with full reorthogonalization started from the same v0 = ones.
/// Eigenvector signs are fixed deterministically (largest-magnitude entry positive, as sklearn's sign flip).
/// </summary>
public static class SpectralSolver
{
    public const int DenseMaxN = 400;

    /// <summary>
    /// Returns <paramref name="count"/> eigenvectors (each length n) for the largest eigenvalues of M, ordered by
    /// descending eigenvalue, or null when the iterative solver fails to converge (callers fall back to random init).
    /// </summary>
    public static double[][]? TopNormalizedAdjacency(SparseGraph g, int count, double tol = 1e-4, int maxRestarts = 300, int? ncv = null)
    {
        int n = g.N;
        if (count > n) throw new ArgumentException("count exceeds matrix size");
        var deg = g.RowSums();
        var dinv = new double[n];
        for (int i = 0; i < n; i++) dinv[i] = deg[i] > 0 ? 1.0 / Math.Sqrt(deg[i]) : 0;
        double[][]? result = n <= DenseMaxN
            ? Dense(g, dinv, count)
            : Lanczos(g, dinv, count, tol, maxRestarts, ncv ?? Math.Max(2 * count + 1, (int)Math.Sqrt(n)));
        if (result != null) foreach (var v in result) SignFlip(v);
        return result;
    }

    /// <summary>Dense symmetric matrix variant (used for the small meta-embedding in multi-component layouts).</summary>
    public static double[][] TopDense(double[] m, int n, int count)
    {
        var eig = new SymmetricEigen(m, n);
        var res = new double[count][];
        for (int c = 0; c < count; c++)
        {
            int col = n - 1 - c;
            var v = new double[n];
            for (int i = 0; i < n; i++) v[i] = eig.Vector(i, col);
            SignFlip(v);
            res[c] = v;
        }
        return res;
    }

    private static double[][] Dense(SparseGraph g, double[] dinv, int count)
    {
        int n = g.N;
        var m = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int p = g.RowPtr[i]; p < g.RowPtr[i + 1]; p++)
                m[i * n + g.Col[p]] = dinv[i] * g.Val[p] * dinv[g.Col[p]];
        var eig = new SymmetricEigen(m, n);
        var res = new double[count][];
        for (int c = 0; c < count; c++)
        {
            int col = n - 1 - c;
            var v = new double[n];
            for (int i = 0; i < n; i++) v[i] = eig.Vector(i, col);
            res[c] = v;
        }
        return res;
    }

    private static void MatVec(SparseGraph g, double[] dinv, double[] x, double[] y)
    {
        int n = g.N;
        void Row(int i)
        {
            double s = 0;
            for (int p = g.RowPtr[i]; p < g.RowPtr[i + 1]; p++)
            {
                int j = g.Col[p];
                s += g.Val[p] * dinv[j] * x[j];
            }
            y[i] = dinv[i] * s;
        }
        if (g.Nnz > 200_000)
            Parallel.For(0, (n + 1023) / 1024, b =>
            {
                int end = Math.Min(n, (b + 1) * 1024);
                for (int i = b * 1024; i < end; i++) Row(i);
            });
        else
            for (int i = 0; i < n; i++) Row(i);
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static void Axpy(double alpha, double[] x, double[] y)
    {
        for (int i = 0; i < y.Length; i++) y[i] += alpha * x[i];
    }

    private static double[][]? Lanczos(SparseGraph g, double[] dinv, int count, double tol, int maxRestarts, int ncv)
    {
        int n = g.N;
        int m = Math.Min(n - 1, Math.Max(ncv, count + 2));
        if (m <= count) return Dense(g, dinv, count);
        var krylov = new Krylov(g, dinv, m);
        int j0 = 0;
        for (int restart = 0; restart <= maxRestarts; restart++)
        {
            krylov.Extend(j0);
            var eig = new SymmetricEigen(krylov.H, m);
            if (krylov.Converged(eig, count, tol)) return krylov.RitzVectors(eig, count);
            if (restart == maxRestarts) return null;
            j0 = krylov.ThickRestart(eig, count);
        }
        return null;
    }

    /// <summary>
    /// The Lanczos basis V (m + 1 vectors), the projected matrix H (m × m) and the last residual norm. Operation order
    /// matches the original single-method solver exactly (the goldens pin the vectors bit for bit).
    /// </summary>
    private sealed class Krylov
    {
        private readonly SparseGraph _g;
        private readonly double[] _dinv;
        private readonly int _m, _n;
        private readonly double[][] _v;
        private readonly double[] _w;
        private readonly Rng _rng = new(7919);
        private double _lastBeta;

        public double[] H { get; }

        public Krylov(SparseGraph g, double[] dinv, int m)
        {
            _g = g;
            _dinv = dinv;
            _m = m;
            _n = g.N;
            _v = new double[m + 1][];
            for (int i = 0; i <= m; i++) _v[i] = new double[_n];
            double inv = 1.0 / Math.Sqrt(_n);
            for (int i = 0; i < _n; i++) _v[0][i] = inv;
            H = new double[m * m];
            _w = new double[_n];
        }

        /// <summary>Lanczos steps j0 .. m-1 with full (twice) reorthogonalization.</summary>
        public void Extend(int j0)
        {
            for (int j = j0; j < _m; j++) Step(j);
        }

        private void Step(int j)
        {
            int m = _m;
            MatVec(_g, _dinv, _v[j], _w);
            for (int i = 0; i <= j; i++) H[i * m + j] = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i <= j; i++)
                {
                    double h = Dot(_v[i], _w);
                    H[i * m + j] += h;
                    Axpy(-h, _v[i], _w);
                }
            for (int i = 0; i < j; i++) H[j * m + i] = H[i * m + j];
            double beta = Math.Sqrt(Dot(_w, _w));
            if (beta < 1e-12)
            {
                RandomContinuation(j);
                beta = 0;
            }
            else
            {
                for (int i = 0; i < _n; i++) _v[j + 1][i] = _w[i] / beta;
            }
            if (j + 1 < m)
            {
                H[(j + 1) * m + j] = beta;
                H[j * m + (j + 1)] = beta;
            }
            _lastBeta = beta;
        }

        /// <summary>Invariant subspace: continue with a random vector orthogonal to the basis.</summary>
        private void RandomContinuation(int j)
        {
            for (int i = 0; i < _n; i++) _w[i] = _rng.NextGaussian();
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i <= j; i++) Axpy(-Dot(_v[i], _w), _v[i], _w);
            double nw = Math.Sqrt(Dot(_w, _w));
            for (int i = 0; i < _n; i++) _v[j + 1][i] = _w[i] / nw;
        }

        /// <summary>Every wanted Ritz pair (descending Ritz values) has a small enough residual.</summary>
        public bool Converged(SymmetricEigen eig, int count, double tol)
        {
            for (int c = 0; c < count; c++)
            {
                int col = _m - 1 - c;
                double theta = eig.Values[col];
                double resid = Math.Abs(_lastBeta * eig.Vector(_m - 1, col));
                if (resid > tol * Math.Max(Math.Abs(theta), 1e-12)) return false;
            }
            return true;
        }

        /// <summary>The <paramref name="count"/> leading Ritz vectors, normalized.</summary>
        public double[][] RitzVectors(SymmetricEigen eig, int count)
        {
            var res = new double[count][];
            for (int c = 0; c < count; c++)
            {
                var x = RitzVector(eig, _m - 1 - c);
                double nx = Math.Sqrt(Dot(x, x));
                for (int i = 0; i < _n; i++) x[i] /= nx;
                res[c] = x;
            }
            return res;
        }

        private double[] RitzVector(SymmetricEigen eig, int col)
        {
            var x = new double[_n];
            for (int j = 0; j < _m; j++) Axpy(eig.Vector(j, col), _v[j], x);
            return x;
        }

        /// <summary>Thick restart: keep the leading Ritz vectors plus the residual; returns the next start step.</summary>
        public int ThickRestart(SymmetricEigen eig, int count)
        {
            int m = _m;
            int keep = Math.Min(m - 2, count + (m - count) / 2);
            var newBasis = new double[keep][];
            for (int c = 0; c < keep; c++) newBasis[c] = RitzVector(eig, m - 1 - c);
            var residual = _v[m];
            Array.Clear(H);
            for (int c = 0; c < keep; c++)
            {
                int col = m - 1 - c;
                H[c * m + c] = eig.Values[col];
                double s = _lastBeta * eig.Vector(m - 1, col);
                H[c * m + keep] = s;
                H[keep * m + c] = s;
            }
            for (int c = 0; c < keep; c++) _v[c] = newBasis[c];
            _v[keep] = (double[])residual.Clone();
            for (int i = keep + 1; i <= m; i++) _v[i] ??= new double[_n];
            return keep;
        }
    }

    public static void SignFlip(double[] v)
    {
        int arg = 0;
        double best = -1;
        for (int i = 0; i < v.Length; i++)
        {
            double a = Math.Abs(v[i]);
            if (a > best) { best = a; arg = i; }
        }
        if (v[arg] < 0) for (int i = 0; i < v.Length; i++) v[i] = -v[i];
    }
}
