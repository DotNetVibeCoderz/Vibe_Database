namespace Faiss.Net;

/// <summary>
/// Distance metric used by an index. Mirrors <c>faiss.METRIC_*</c> in the Python API.
/// </summary>
public enum MetricType
{
    /// <summary>Maximum inner product search. Larger is better.</summary>
    InnerProduct = 0,

    /// <summary>Squared Euclidean (L2) distance. Smaller is better.</summary>
    L2 = 1,

    /// <summary>Manhattan (L1) distance. Smaller is better.</summary>
    L1 = 2,

    /// <summary>Chebyshev (L-infinity) distance. Smaller is better.</summary>
    Linf = 3,
}

public static class MetricTypeExtensions
{
    /// <summary>
    /// True when a larger score means a better match (inner product), false for distances.
    /// This drives heap direction throughout the library.
    /// </summary>
    public static bool IsSimilarity(this MetricType metric) => metric == MetricType.InnerProduct;

    public static string ToShortString(this MetricType metric) => metric switch
    {
        MetricType.InnerProduct => "IP",
        MetricType.L2 => "L2",
        MetricType.L1 => "L1",
        MetricType.Linf => "Linf",
        _ => metric.ToString(),
    };
}
