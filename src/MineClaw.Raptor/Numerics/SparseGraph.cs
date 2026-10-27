namespace MineClaw.Raptor.Numerics;

/// <summary>Square sparse matrix in CSR form with sorted column indices per row (the UMAP fuzzy graph).</summary>
public sealed class SparseGraph
{
    public int N { get; }
    public int[] RowPtr { get; }
    public int[] Col { get; }
    public double[] Val { get; }

    public SparseGraph(int n, int[] rowPtr, int[] col, double[] val)
    {
        N = n;
        RowPtr = rowPtr;
        Col = col;
        Val = val;
    }

    public int Nnz => RowPtr[N];

    public double MaxValue()
    {
        double m = 0;
        foreach (var v in Val) if (v > m) m = v;
        return m;
    }

    /// <summary>Returns a copy keeping only entries for which <paramref name="keep"/> is true (scipy mask + eliminate_zeros).</summary>
    public SparseGraph Where(Func<double, bool> keep)
    {
        var rp = new int[N + 1];
        var cols = new List<int>(Nnz);
        var vals = new List<double>(Nnz);
        for (int i = 0; i < N; i++)
        {
            for (int p = RowPtr[i]; p < RowPtr[i + 1]; p++)
            {
                if (!keep(Val[p])) continue;
                cols.Add(Col[p]);
                vals.Add(Val[p]);
            }
            rp[i + 1] = cols.Count;
        }
        return new SparseGraph(N, rp, cols.ToArray(), vals.ToArray());
    }

    public double[] RowSums()
    {
        var s = new double[N];
        for (int i = 0; i < N; i++)
            for (int p = RowPtr[i]; p < RowPtr[i + 1]; p++) s[i] += Val[p];
        return s;
    }

    /// <summary>Connected component label per vertex (labels in order of first appearance) and the component count.</summary>
    public (int[] Labels, int Count) ConnectedComponents()
    {
        var labels = new int[N];
        Array.Fill(labels, -1);
        int count = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < N; s++)
        {
            if (labels[s] >= 0) continue;
            labels[s] = count;
            stack.Push(s);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                for (int p = RowPtr[u]; p < RowPtr[u + 1]; p++)
                {
                    int v = Col[p];
                    if (labels[v] < 0)
                    {
                        labels[v] = count;
                        stack.Push(v);
                    }
                }
            }
            count++;
        }
        return (labels, count);
    }

    /// <summary>Induced subgraph on <paramref name="vertices"/> (ascending), re-indexed 0..m-1.</summary>
    public SparseGraph Subgraph(int[] vertices)
    {
        var map = new Dictionary<int, int>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++) map[vertices[i]] = i;
        var rp = new int[vertices.Length + 1];
        var cols = new List<int>();
        var vals = new List<double>();
        for (int i = 0; i < vertices.Length; i++)
        {
            int u = vertices[i];
            for (int p = RowPtr[u]; p < RowPtr[u + 1]; p++)
            {
                if (!map.TryGetValue(Col[p], out int c)) continue;
                cols.Add(c);
                vals.Add(Val[p]);
            }
            rp[i + 1] = cols.Count;
        }
        return new SparseGraph(vertices.Length, rp, cols.ToArray(), vals.ToArray());
    }
}
