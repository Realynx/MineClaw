using System.Numerics;
using System.Runtime.InteropServices;

namespace MineClaw.Raptor.Numerics;

/// <summary>SIMD vector helpers. All stored embeddings are L2-normalized, so cosine similarity is a dot product.</summary>
public static class VectorMath
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Vector length mismatch.");
        int n = a.Length, i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count * 2)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            var acc0 = Vector<float>.Zero;
            var acc1 = Vector<float>.Zero;
            int j = 0;
            for (; j + 1 < va.Length; j += 2)
            {
                acc0 += va[j] * vb[j];
                acc1 += va[j + 1] * vb[j + 1];
            }
            for (; j < va.Length; j++) acc0 += va[j] * vb[j];
            sum = Vector.Dot(acc0 + acc1, Vector<float>.One);
            i = va.Length * Vector<float>.Count;
        }
        for (; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    /// <summary>Normalizes in place; returns the original norm. A zero vector is left as zeros.</summary>
    public static float Normalize(Span<float> v)
    {
        float norm = MathF.Sqrt(Dot(v, v));
        if (norm > 0 && float.IsFinite(norm))
        {
            float inv = 1f / norm;
            for (int i = 0; i < v.Length; i++) v[i] *= inv;
        }
        return norm;
    }

    public static float[] Normalized(ReadOnlySpan<float> v)
    {
        var copy = v.ToArray();
        Normalize(copy);
        return copy;
    }

    /// <summary>Cosine similarity of arbitrary (not necessarily normalized) vectors; 0 when either is zero.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double ab = Dot(a, b), aa = Dot(a, a), bb = Dot(b, b);
        if (aa <= 0 || bb <= 0) return 0;
        return ab / Math.Sqrt(aa * bb);
    }
}
