using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Applies a chain of <see cref="VectorTransform"/>s before delegating to a wrapped index.
/// <para>
/// This is how the composite recipes are built: <c>OPQ16_64,IVF1024,PQ16</c> is an
/// <see cref="OPQMatrix"/> in front of an <see cref="IndexIVFPQ"/>. The transform is applied to
/// added vectors and to queries alike, so callers keep working in the original space and cannot
/// forget to transform one side.
/// </para>
/// </summary>
public sealed class IndexPreTransform : Index
{
    /// <summary>Transforms applied in order, first to last.</summary>
    public List<VectorTransform> Transforms { get; private set; } = [];

    /// <summary>Index operating in the transformed space.</summary>
    public Index Base { get; private set; }

    public IndexPreTransform(VectorTransform transform, Index baseIndex)
        : this([transform], baseIndex)
    {
    }

    public IndexPreTransform(IEnumerable<VectorTransform> transforms, Index baseIndex)
        : base(GetInputDimension(transforms, baseIndex), baseIndex.MetricType)
    {
        Transforms = [.. transforms];
        Base = baseIndex;

        int dimension = D;
        foreach (var transform in Transforms)
        {
            if (transform.DIn != dimension)
                throw new ArgumentException(
                    $"Transform {transform.GetType().Name} expects d={transform.DIn} but receives d={dimension}.");
            dimension = transform.DOut;
        }
        if (dimension != baseIndex.D)
            throw new ArgumentException(
                $"Transform chain outputs d={dimension} but the wrapped index expects d={baseIndex.D}.");

        IsTrained = baseIndex.IsTrained && Transforms.TrueForAll(t => t.IsTrained);
    }

    private IndexPreTransform(int dimension, MetricType metric, Index baseIndex) : base(dimension, metric) => Base = baseIndex;

    private static int GetInputDimension(IEnumerable<VectorTransform> transforms, Index baseIndex)
    {
        foreach (var transform in transforms) return transform.DIn;
        return baseIndex.D;
    }

    public override bool SupportsReconstruct => Base.SupportsReconstruct;

    /// <summary>Runs the input through the chain, allocating only where a transform changes the data.</summary>
    private float[] ApplyChain(ReadOnlySpan<float> x, int n)
    {
        float[] current = x[..(n * D)].ToArray();
        int dimension = D;
        foreach (var transform in Transforms)
        {
            var next = new float[(long)n * transform.DOut];
            transform.Apply(current, n, next);
            current = next;
            dimension = transform.DOut;
        }
        _ = dimension;
        return current;
    }

    /// <summary>
    /// Trains each transform on the output of the previous one, then trains the wrapped index on the
    /// fully transformed data — the order matters, since a transform cannot be trained on data that
    /// has not yet passed through its predecessors.
    /// </summary>
    public override void Train(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        float[] current = x[..(n * D)].ToArray();

        foreach (var transform in Transforms)
        {
            if (!transform.IsTrained) transform.Train(current);
            var next = new float[(long)n * transform.DOut];
            transform.Apply(current, n, next);
            current = next;
        }

        Base.Train(current);
        IsTrained = Base.IsTrained;
    }

    public override void Add(ReadOnlySpan<float> x)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        Base.Add(ApplyChain(x, n));
        Ntotal = Base.Ntotal;
    }

    public override void AddWithIds(ReadOnlySpan<float> x, ReadOnlySpan<long> ids)
    {
        int n = ValidateBatch(x, nameof(x));
        if (n == 0) return;
        Base.AddWithIds(ApplyChain(x, n), ids);
        Ntotal = Base.Ntotal;
    }

    public override void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels) =>
        Base.Search(ApplyChain(queries, nq), nq, k, distances, labels);

    public override RangeSearchResult RangeSearch(ReadOnlySpan<float> queries, float radius)
    {
        int nq = ValidateBatch(queries, nameof(queries));
        return Base.RangeSearch(ApplyChain(queries, nq), radius);
    }

    /// <summary>
    /// Reconstructs in the transformed space and maps back through the chain in reverse. Exact only
    /// when every transform is invertible; a dimensionality-reducing chain returns a projection.
    /// </summary>
    public override void Reconstruct(long key, Span<float> output)
    {
        int dimension = Transforms.Count > 0 ? Transforms[^1].DOut : D;
        float[] current = new float[dimension];
        Base.Reconstruct(key, current);

        for (int i = Transforms.Count - 1; i >= 0; i--)
        {
            var previous = new float[Transforms[i].DIn];
            Transforms[i].ReverseTransform(current, 1, previous);
            current = previous;
        }
        current.CopyTo(output);
    }

    public override long RemoveIds(ReadOnlySpan<long> ids)
    {
        long removed = Base.RemoveIds(ids);
        Ntotal = Base.Ntotal;
        return removed;
    }

    public override long RemoveIds(Func<long, bool> predicate)
    {
        long removed = Base.RemoveIds(predicate);
        Ntotal = Base.Ntotal;
        return removed;
    }

    public override void Reset()
    {
        Base.Reset();
        Ntotal = 0;
    }

    public override long MemoryUsage => Base.MemoryUsage;

    public override string Describe() =>
        $"IndexPreTransform([{string.Join(" -> ", Transforms.Select(t => t.GetType().Name))}] -> {Base.Describe()})";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.PreTransform;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(Transforms.Count);
        foreach (var transform in Transforms) transform.Write(writer);
        IndexIO.WriteTo(writer, Base);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        Transforms = new List<VectorTransform>(count);
        for (int i = 0; i < count; i++) Transforms.Add(VectorTransform.Read(reader));
        Base = IndexIO.ReadFrom(reader);
        Ntotal = Base.Ntotal;
        IsTrained = Base.IsTrained;
    }

    /// <summary>Creates a blank instance for the deserializer to fill.</summary>
    internal static IndexPreTransform CreateForRead(int dimension, MetricType metric) =>
        new(dimension, metric, new IndexFlat(dimension, metric));
}
