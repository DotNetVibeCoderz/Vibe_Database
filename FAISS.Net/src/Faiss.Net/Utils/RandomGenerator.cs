namespace Faiss.Net.Utils;

/// <summary>
/// Deterministic xoshiro-style PRNG used by clustering, quantizer training and the sample data
/// generators. Kept independent of <see cref="System.Random"/> so a given seed reproduces the same
/// index bit-for-bit across runs, frameworks and machines, which matters for regression tests and
/// for comparing benchmark numbers against Python FAISS.
/// </summary>
public sealed class RandomGenerator
{
    private ulong _s0, _s1;

    public RandomGenerator(long seed = 1234)
    {
        // SplitMix64 expansion so even low-entropy seeds (0, 1, 2...) start well mixed.
        ulong z = (ulong)seed + 0x9E3779B97F4A7C15UL;
        _s0 = Mix(ref z);
        _s1 = Mix(ref z);
        if ((_s0 | _s1) == 0) _s1 = 0x9E3779B97F4A7C15UL;
    }

    private static ulong Mix(ref ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        ulong r = z;
        r = (r ^ (r >> 30)) * 0xBF58476D1CE4E5B9UL;
        r = (r ^ (r >> 27)) * 0x94D049BB133111EBUL;
        return r ^ (r >> 31);
    }

    private ulong NextUInt64()
    {
        ulong s0 = _s0, s1 = _s1;
        ulong result = s0 + s1;
        s1 ^= s0;
        _s0 = ulong.RotateLeft(s0, 55) ^ s1 ^ (s1 << 14);
        _s1 = ulong.RotateLeft(s1, 36);
        return result;
    }

    /// <summary>Uniform integer in <c>[0, max)</c>.</summary>
    public int NextInt(int max) => max <= 0 ? 0 : (int)(NextUInt64() % (ulong)max);

    /// <summary>Uniform integer in <c>[min, max)</c>.</summary>
    public int NextInt(int min, int max) => min + NextInt(max - min);

    /// <summary>Uniform float in <c>[0, 1)</c>.</summary>
    public float NextFloat() => (NextUInt64() >> 40) * (1.0f / (1 << 24));

    /// <summary>Standard normal sample (Box-Muller).</summary>
    public float NextGaussian()
    {
        float u1 = MathF.Max(NextFloat(), 1e-7f);
        float u2 = NextFloat();
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2);
    }

    /// <summary>Fills a span with standard normal samples.</summary>
    public void FillGaussian(Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] = NextGaussian();
    }

    /// <summary>Fills a span with uniform <c>[0, 1)</c> samples.</summary>
    public void FillUniform(Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] = NextFloat();
    }

    /// <summary>In-place Fisher-Yates shuffle.</summary>
    public void Shuffle<T>(Span<T> items)
    {
        for (int i = items.Length - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>A random permutation of <c>[0, n)</c>.</summary>
    public int[] Permutation(int n)
    {
        var perm = new int[n];
        for (int i = 0; i < n; i++) perm[i] = i;
        Shuffle(perm.AsSpan());
        return perm;
    }
}
