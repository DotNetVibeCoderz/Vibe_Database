namespace Faiss.Net;

/// <summary>
/// Per-call search options, the <c>faiss.SearchParameters</c> family. Passing parameters overrides
/// the index's own settings for that one call only, so concurrent searches can use different
/// settings on the same index without racing on a shared property.
/// </summary>
/// <example>
/// <code>
/// var p = new SearchParametersIVF { Sel = IDSelector.Range(0, 5000), Nprobe = 32 };
/// var result = ivf.Search(query, k: 10, p);
/// </code>
/// </example>
public class SearchParameters
{
    /// <summary>Restricts results to a subset of ids. Named <c>sel</c> in FAISS.</summary>
    public IDSelector? Sel { get; set; }

    /// <summary>Alias of <see cref="Sel"/>, spelled out for C# callers.</summary>
    public IDSelector? Selector
    {
        get => Sel;
        set => Sel = value;
    }

    /// <summary>True when nothing here differs from the index's own defaults.</summary>
    public virtual bool IsDefault => Sel is null;

    /// <summary>A shallow copy of the same concrete type with a different selector.</summary>
    internal SearchParameters CloneWith(IDSelector? selector)
    {
        var copy = (SearchParameters)MemberwiseClone();
        copy.Sel = selector;
        return copy;
    }
}

/// <summary>Helpers for wrappers that forward parameters to a child with a translated selector.</summary>
internal static class SearchParametersCopy
{
    /// <summary>Parameters equal to <paramref name="parameters"/> but selecting with <paramref name="selector"/>.</summary>
    public static SearchParameters WithSelector(SearchParameters? parameters, IDSelector? selector) =>
        parameters is null ? new SearchParameters { Sel = selector } : parameters.CloneWith(selector);
}

/// <summary>Search options for the IVF family.</summary>
public class SearchParametersIVF : SearchParameters
{
    /// <summary>Cells visited for this call; <c>0</c> keeps <see cref="IndexIVF.Nprobe"/>.</summary>
    public int Nprobe { get; set; }

    /// <summary>
    /// Stops scanning a query once this many codes have been scored; <c>0</c> means no limit.
    /// Bounds the latency of a query whose nearest cells happen to be very large.
    /// </summary>
    public long MaxCodes { get; set; }

    /// <summary>Options passed on to the coarse quantizer's own search.</summary>
    public SearchParameters? QuantizerParams { get; set; }

    public override bool IsDefault => base.IsDefault && Nprobe == 0 && MaxCodes == 0 && QuantizerParams is null;
}

/// <summary>Search options for <see cref="IndexHNSWFlat"/> and the other graph indexes.</summary>
public class SearchParametersHNSW : SearchParameters
{
    /// <summary>Beam width for this call; <c>0</c> keeps the index's <c>EfSearch</c>.</summary>
    public int EfSearch { get; set; }

    public override bool IsDefault => base.IsDefault && EfSearch == 0;
}

/// <summary>Search options for <see cref="IndexRefine"/>.</summary>
public class SearchParametersRefine : SearchParameters
{
    /// <summary>Candidates fetched per result for this call; <c>0</c> keeps <see cref="IndexRefine.KFactor"/>.</summary>
    public float KFactor { get; set; }

    /// <summary>Options passed on to the base index.</summary>
    public SearchParameters? BaseIndexParams { get; set; }

    public override bool IsDefault => base.IsDefault && KFactor == 0 && BaseIndexParams is null;
}
