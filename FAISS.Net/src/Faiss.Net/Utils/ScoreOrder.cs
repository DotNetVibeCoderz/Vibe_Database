namespace Faiss.Net.Utils;

/// <summary>
/// Compile-time ordering policy for scores. Implemented by empty structs and consumed through
/// generic constraints so the JIT specializes each search kernel and the comparison becomes a
/// single inlined instruction instead of a branch on <see cref="MetricType"/> per candidate.
/// </summary>
public interface IScoreOrder
{
    /// <summary>True when <paramref name="a"/> is a strictly better match than <paramref name="b"/>.</summary>
    static abstract bool Better(float a, float b);

    /// <summary>Score used for empty result slots (worse than any real candidate).</summary>
    static abstract float Worst { get; }
}

/// <summary>Smaller is better: L2, L1, Linf.</summary>
public readonly struct AscendingOrder : IScoreOrder
{
    public static bool Better(float a, float b) => a < b;
    public static float Worst => float.MaxValue;
}

/// <summary>Larger is better: inner product.</summary>
public readonly struct DescendingOrder : IScoreOrder
{
    public static bool Better(float a, float b) => a > b;
    public static float Worst => float.MinValue;
}
