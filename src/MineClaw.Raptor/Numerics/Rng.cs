namespace MineClaw.Raptor.Numerics;

/// <summary>
/// Seeded xoshiro256** generator (SplitMix64 seeding). Every stochastic step in the library (k-means++, UMAP noise
/// and negative sampling, random fallbacks) draws from one of these, so a fixed seed gives bit-identical trees.
/// It does not reproduce numpy's MT19937 stream; see README "Deviations".
/// </summary>
public sealed class Rng
{
    private ulong _s0, _s1, _s2, _s3;
    private double? _spareGaussian;

    public Rng(long seed)
    {
        ulong x = unchecked((ulong)seed);
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    private static ulong SplitMix(ref ulong x)
    {
        ulong z = unchecked(x += 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    public ulong NextULong()
    {
        ulong result = unchecked(Rotl(_s1 * 5, 7) * 9);
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, 1) with 53 bits of precision.</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

    public double Uniform(double low, double high) => low + (high - low) * NextDouble();

    /// <summary>Uniform integer in [0, n).</summary>
    public int NextInt(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        return (int)((NextULong() >> 33) % (ulong)n);
    }

    /// <summary>Standard normal (Marsaglia polar method).</summary>
    public double NextGaussian()
    {
        if (_spareGaussian is { } spare)
        {
            _spareGaussian = null;
            return spare;
        }
        double u, v, s;
        do
        {
            u = NextDouble() * 2 - 1;
            v = NextDouble() * 2 - 1;
            s = u * u + v * v;
        } while (s >= 1 || s == 0);
        double m = Math.Sqrt(-2.0 * Math.Log(s) / s);
        _spareGaussian = v * m;
        return u * m;
    }

    /// <summary>Three 32-bit seeds for the UMAP tau_rand state (umap-learn draws these from random_state).</summary>
    public long[] NextTauState() => [(long)(NextULong() >> 33), (long)(NextULong() >> 33), (long)(NextULong() >> 33)];
}
