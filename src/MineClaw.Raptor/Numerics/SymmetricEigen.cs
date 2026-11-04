namespace MineClaw.Raptor.Numerics;

/// <summary>
/// Dense symmetric eigen-decomposition: Householder tridiagonalization + implicit QL (the EISPACK tred2/tql2 pair, as
/// in JAMA). Eigenvalues ascending; eigenvectors are the columns of <see cref="Vectors"/> (row-major n×n).
/// </summary>
public sealed class SymmetricEigen
{
    public int N { get; }
    public double[] Values { get; }
    /// <summary>Row-major n×n; column j is the eigenvector for <c>Values[j]</c>.</summary>
    public double[] Vectors { get; }

    public SymmetricEigen(double[] symmetric, int n)
    {
        N = n;
        var v = new double[n][];
        for (int i = 0; i < n; i++)
        {
            v[i] = new double[n];
            for (int j = 0; j < n; j++) v[i][j] = 0.5 * (symmetric[i * n + j] + symmetric[j * n + i]);
        }
        var d = new double[n];
        var e = new double[n];
        if (n == 1)
        {
            d[0] = v[0][0];
            v[0][0] = 1;
        }
        else
        {
            Tred2(n, v, d, e);
            Tql2(n, v, d, e);
        }
        Values = d;
        Vectors = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++) Vectors[i * n + j] = v[i][j];
    }

    public double Vector(int row, int col) => Vectors[row * N + col];

    // Every helper below keeps the JAMA statement order exactly: the goldens pin the results bit for bit.

    private static void Tred2(int n, double[][] V, double[] d, double[] e)
    {
        for (int j = 0; j < n; j++) d[j] = V[n - 1][j];
        for (int i = n - 1; i > 0; i--)
        {
            double scale = 0.0;
            for (int k = 0; k < i; k++) scale += Math.Abs(d[k]);
            d[i] = scale == 0.0 ? SkipRow(V, d, e, i) : HouseholderRow(V, d, e, i, scale);
        }
        AccumulateTransforms(n, V, d, e);
    }

    /// <summary>Row i is already reduced (zero scale): copy it down; h stays 0.</summary>
    private static double SkipRow(double[][] V, double[] d, double[] e, int i)
    {
        e[i] = d[i - 1];
        for (int j = 0; j < i; j++)
        {
            d[j] = V[i - 1][j];
            V[i][j] = 0.0;
            V[j][i] = 0.0;
        }
        return 0.0;
    }

    /// <summary>Householder reflection that annihilates row i left of the subdiagonal; returns h.</summary>
    private static double HouseholderRow(double[][] V, double[] d, double[] e, int i, double scale)
    {
        double h = 0.0;
        for (int k = 0; k < i; k++)
        {
            d[k] /= scale;
            h += d[k] * d[k];
        }
        double f = d[i - 1];
        double g = Math.Sqrt(h);
        if (f > 0) g = -g;
        e[i] = scale * g;
        h -= f * g;
        d[i - 1] = f - g;
        for (int j = 0; j < i; j++) e[j] = 0.0;
        ApplyToColumns(V, d, e, i);
        FormK(d, e, i, h);
        ReduceRows(V, d, e, i);
        return h;
    }

    private static void ApplyToColumns(double[][] V, double[] d, double[] e, int i)
    {
        for (int j = 0; j < i; j++)
        {
            double f = d[j];
            V[j][i] = f;
            double g = e[j] + V[j][j] * f;
            for (int k = j + 1; k <= i - 1; k++)
            {
                g += V[k][j] * d[k];
                e[k] += V[k][j] * f;
            }
            e[j] = g;
        }
    }

    private static void FormK(double[] d, double[] e, int i, double h)
    {
        double f = 0.0;
        for (int j = 0; j < i; j++)
        {
            e[j] /= h;
            f += e[j] * d[j];
        }
        double hh = f / (h + h);
        for (int j = 0; j < i; j++) e[j] -= hh * d[j];
    }

    private static void ReduceRows(double[][] V, double[] d, double[] e, int i)
    {
        for (int j = 0; j < i; j++)
        {
            double f = d[j];
            double g = e[j];
            for (int k = j; k <= i - 1; k++) V[k][j] -= f * e[k] + g * d[k];
            d[j] = V[i - 1][j];
            V[i][j] = 0.0;
        }
    }

    private static void AccumulateTransforms(int n, double[][] V, double[] d, double[] e)
    {
        for (int i = 0; i < n - 1; i++)
        {
            V[n - 1][i] = V[i][i];
            V[i][i] = 1.0;
            double h = d[i + 1];
            if (h != 0.0) ApplyTransform(V, d, i, h);
            for (int k = 0; k <= i; k++) V[k][i + 1] = 0.0;
        }
        for (int j = 0; j < n; j++)
        {
            d[j] = V[n - 1][j];
            V[n - 1][j] = 0.0;
        }
        V[n - 1][n - 1] = 1.0;
        e[0] = 0.0;
    }

    private static void ApplyTransform(double[][] V, double[] d, int i, double h)
    {
        for (int k = 0; k <= i; k++) d[k] = V[k][i + 1] / h;
        for (int j = 0; j <= i; j++)
        {
            double g = 0.0;
            for (int k = 0; k <= i; k++) g += V[k][i + 1] * V[k][j];
            for (int k = 0; k <= i; k++) V[k][j] -= g * d[k];
        }
    }

    private static void Tql2(int n, double[][] V, double[] d, double[] e)
    {
        for (int i = 1; i < n; i++) e[i - 1] = e[i];
        e[n - 1] = 0.0;
        double f = 0.0, tst1 = 0.0;
        double eps = Math.Pow(2.0, -52.0);
        for (int l = 0; l < n; l++)
        {
            tst1 = Math.Max(tst1, Math.Abs(d[l]) + Math.Abs(e[l]));
            int m = SmallSubdiagonal(n, e, l, eps * tst1);
            if (m > l) f = Converge(n, V, d, e, l, m, f, eps * tst1);
            d[l] += f;
            e[l] = 0.0;
        }
        SortAscending(n, V, d);
    }

    /// <summary>First m ≥ l whose subdiagonal element is negligible (n - 1 when none is).</summary>
    private static int SmallSubdiagonal(int n, double[] e, int l, double negligible)
    {
        int m = l;
        while (m < n)
        {
            if (Math.Abs(e[m]) <= negligible) break;
            m++;
        }
        return m == n ? n - 1 : m;
    }

    /// <summary>Implicit QL iterations until e[l] is negligible; returns the accumulated shift f.</summary>
    private static double Converge(int n, double[][] V, double[] d, double[] e, int l, int m, double f, double negligible)
    {
        int iter = 0;
        do
        {
            if (++iter > 200) throw new InvalidOperationException("Symmetric eigen-decomposition did not converge.");
            f = QlStep(n, V, d, e, l, m, f);
        } while (Math.Abs(e[l]) > negligible);
        return f;
    }

    private static double QlStep(int n, double[][] V, double[] d, double[] e, int l, int m, double f)
    {
        double g = d[l];
        double p = (d[l + 1] - g) / (2.0 * e[l]);
        double r = Hypot(p, 1.0);
        if (p < 0) r = -r;
        d[l] = e[l] / (p + r);
        d[l + 1] = e[l] * (p + r);
        double dl1 = d[l + 1];
        double h = g - d[l];
        for (int i = l + 2; i < n; i++) d[i] -= h;
        f += h;
        p = d[m];
        double c = 1.0, c2 = c, c3 = c;
        double el1 = e[l + 1];
        double s = 0.0, s2 = 0.0;
        for (int i = m - 1; i >= l; i--)
        {
            c3 = c2;
            c2 = c;
            s2 = s;
            g = c * e[i];
            h = c * p;
            r = Hypot(p, e[i]);
            e[i + 1] = s * r;
            s = e[i] / r;
            c = p / r;
            p = c * d[i] - s * g;
            d[i + 1] = h + s * (c * g + s * d[i]);
            Rotate(n, V, i, c, s);
        }
        p = -s * s2 * c3 * el1 * e[l] / dl1;
        e[l] = s * p;
        d[l] = c * p;
        return f;
    }

    private static void Rotate(int n, double[][] V, int i, double c, double s)
    {
        for (int k = 0; k < n; k++)
        {
            double h = V[k][i + 1];
            V[k][i + 1] = s * V[k][i] + c * h;
            V[k][i] = c * V[k][i] - s * h;
        }
    }

    /// <summary>Selection sort of the eigenvalues ascending, swapping the eigenvector columns along.</summary>
    private static void SortAscending(int n, double[][] V, double[] d)
    {
        for (int i = 0; i < n - 1; i++)
        {
            int k = i;
            double p = d[i];
            for (int j = i + 1; j < n; j++)
                if (d[j] < p)
                {
                    k = j;
                    p = d[j];
                }
            if (k == i) continue;
            d[k] = d[i];
            d[i] = p;
            for (int j = 0; j < n; j++) (V[j][i], V[j][k]) = (V[j][k], V[j][i]);
        }
    }

    private static double Hypot(double a, double b)
    {
        double aa = Math.Abs(a), bb = Math.Abs(b);
        if (aa > bb) { double r = bb / aa; return aa * Math.Sqrt(1 + r * r); }
        if (bb > 0) { double r = aa / bb; return bb * Math.Sqrt(1 + r * r); }
        return 0;
    }
}
