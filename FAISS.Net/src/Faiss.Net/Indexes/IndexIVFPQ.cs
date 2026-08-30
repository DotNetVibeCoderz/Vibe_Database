using System.Buffers;
using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// The workhorse billion-scale index: an inverted file whose list entries are product-quantized
/// codes. Combines both savings — probing skips most of the database, and what is scanned is a few
/// bytes per vector instead of a few hundred.
/// <para>
/// Codes are stored as residuals from the cell centroid (see <see cref="IndexIVF.ByResidual"/>).
/// Residuals are far smaller in magnitude than the vectors themselves, so the same code budget
/// resolves them much more finely; this is where a large part of IVFPQ's accuracy advantage over a
/// plain <see cref="IndexPQ"/> comes from, and it costs one lookup-table build per probed cell
/// rather than one per query.
/// </para>
/// <para>
/// Sizing: <c>m</c> trades accuracy against memory (code size is <c>m</c> bytes at 8 bits), and
/// <c>nlist</c>/<c>nprobe</c> trade speed against recall. Typical starting point for a million
/// 128-dim vectors: <c>nlist = 1024</c>, <c>m = 16</c>, <c>nprobe = 8</c> — about 24 MB against
/// 512 MB for a flat index.
/// </para>
/// </summary>
public sealed class IndexIVFPQ : IndexIVF
{
    /// <summary>The residual product quantizer; exposed like <c>index.pq</c> in Python.</summary>
    public ProductQuantizer Pq { get; private set; }

    /// <param name="quantizer">Coarse quantizer, typically <c>new IndexFlatL2(d)</c>.</param>
    /// <param name="dimension">Vector dimension.</param>
    /// <param name="nlist">Number of cells.</param>
    /// <param name="m">Sub-quantizers per vector; must divide <paramref name="dimension"/>.</param>
    /// <param name="nbits">Bits per sub-code; 8 is the default.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexIVFPQ(Index quantizer, int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : base(quantizer, dimension, nlist, (m * nbits + 7) / 8, metric)
    {
        Pq = new ProductQuantizer(dimension, m, nbits);
        // Residual coding is a clean win for L2. For inner product the residual decomposition needs
        // an extra correction term per candidate, so raw vectors are encoded instead: simpler, and
        // exact against the decoded vector.
        ByResidual = metric == MetricType.L2;
    }

    /// <summary>Convenience constructor that creates a matching flat coarse quantizer.</summary>
    public IndexIVFPQ(int dimension, int nlist, int m, int nbits = 8, MetricType metric = MetricType.L2)
        : this(metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension),
               dimension, nlist, m, nbits, metric)
    {
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>, ignoring ids.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    protected override void TrainEncoder(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos)
    {
        if (!ByResidual)
        {
            Pq.Train(x[..(n * D)]);
            return;
        }

        var residuals = new float[(long)n * D];
        ComputeResiduals(x, n, listNos, residuals);
        Pq.Train(residuals);
    }

    /// <summary>Subtracts each vector's cell centroid, the form the codes are trained and stored in.</summary>
    private void ComputeResiduals(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<float> residuals)
    {
        Span<float> centroid = D <= 1024 ? stackalloc float[D] : new float[D];
        int cached = -1;
        for (int i = 0; i < n; i++)
        {
            int list = (int)listNos[i];
            if (list < 0) list = 0;
            if (list != cached)
            {
                GetCentroid(list, centroid);
                cached = list;
            }
            var source = x.Slice(i * D, D);
            var target = residuals.Slice(i * D, D);
            for (int j = 0; j < D; j++) target[j] = source[j] - centroid[j];
        }
    }

    protected override void EncodeVectors(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<byte> codes)
    {
        if (!ByResidual)
        {
            Pq.ComputeCodes(x[..(n * D)], n, codes);
            return;
        }

        var residuals = new float[(long)n * D];
        ComputeResiduals(x, n, listNos, residuals);
        Pq.ComputeCodes(residuals, n, codes);
    }

    protected override unsafe void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore, Span<float> scores)
    {
        int tableSize = Pq.DistanceTableSize;
        float[] table = ArrayPool<float>.Shared.Rent(tableSize);
        float[]? residual = null;
        try
        {
            ReadOnlySpan<float> effectiveQuery = query;
            if (ByResidual)
            {
                // The code encodes (vector - centroid), so the query must be shifted the same way
                // for the table lookups to add up to a distance in the original space.
                residual = ArrayPool<float>.Shared.Rent(D);
                var centroid = residual.AsSpan(0, D);
                GetCentroid(list, centroid);
                for (int j = 0; j < D; j++) centroid[j] = query[j] - centroid[j];
                effectiveQuery = centroid;
            }

            Pq.ComputeDistanceTable(effectiveQuery, table.AsSpan(0, tableSize), MetricType);

            var codes = Lists.GetCodes(list);
            int codeSize = CodeSize;
            fixed (float* pt = table)
            fixed (byte* pc = codes)
            {
                for (int i = 0; i < scores.Length; i++)
                    scores[i] = Pq.DistanceFromTable(pt, pc + (long)i * codeSize);
            }
        }
        finally
        {
            if (residual is not null) ArrayPool<float>.Shared.Return(residual);
            ArrayPool<float>.Shared.Return(table);
        }
    }

    protected override void DecodeEntry(int list, int offset, Span<float> output)
    {
        Pq.Decode(Lists.GetCodes(list).Slice(offset * CodeSize, CodeSize), output);
        if (!ByResidual) return;

        Span<float> centroid = D <= 1024 ? stackalloc float[D] : new float[D];
        GetCentroid(list, centroid);
        for (int j = 0; j < D; j++) output[j] += centroid[j];
    }

    public override long MemoryUsage => base.MemoryUsage + Pq.CodebookBytes;

    public override string Describe() =>
        $"IndexIVFPQ(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {Pq}, " +
        $"{(ByResidual ? "residual" : "direct")}, {MetricType.ToShortString()}, {CompressionRatio:F0}x smaller than flat)";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IVFPQ;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(Nprobe);
        writer.Write(ByResidual);
        IndexIO.WriteTo(writer, Quantizer);
        Pq.Write(writer);
        Lists.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int nlist = reader.ReadInt32();
        Nprobe = reader.ReadInt32();
        ByResidual = reader.ReadBoolean();
        var quantizer = IndexIO.ReadFrom(reader);
        Pq = ProductQuantizer.Read(reader);
        var lists = InvertedLists.Read(reader);
        RestoreIvf(quantizer, lists, nlist);
        IsTrained = Pq.IsTrained;
    }
}
