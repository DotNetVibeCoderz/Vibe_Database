using System.Buffers;
using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// IVF index whose list entries are scalar-quantized.
/// <para>
/// The middle ground between <see cref="IndexIVFFlat"/> and <see cref="IndexIVFPQ"/>: 4x smaller
/// than storing floats, but with none of PQ's clustering step and far less accuracy loss. In
/// practice this is the index to reach for when IVFFlat no longer fits in memory and IVFPQ gives up
/// more recall than the application can tolerate.
/// </para>
/// </summary>
public sealed class IndexIVFScalarQuantizer : IndexIVF
{
    /// <summary>The scalar quantizer; exposed like <c>index.sq</c> in Python.</summary>
    public ScalarQuantizer Sq { get; private set; }

    public IndexIVFScalarQuantizer(
        Index quantizer, int dimension, int nlist,
        ScalarQuantizerType type = ScalarQuantizerType.PerDimension8Bit,
        MetricType metric = MetricType.L2)
        : base(quantizer, dimension, nlist, new ScalarQuantizer(dimension, type).CodeSize, metric)
    {
        Sq = new ScalarQuantizer(dimension, type);
        // Residuals concentrate the value range around zero, which is exactly what a fixed-range
        // scalar quantizer benefits from. Inner product keeps raw vectors; see IndexIVFPQ.
        ByResidual = metric == MetricType.L2;
    }

    /// <summary>Convenience constructor that creates a matching flat coarse quantizer.</summary>
    public IndexIVFScalarQuantizer(
        int dimension, int nlist,
        ScalarQuantizerType type = ScalarQuantizerType.PerDimension8Bit,
        MetricType metric = MetricType.L2)
        : this(metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension),
               dimension, nlist, type, metric)
    {
    }

    public override bool SupportsReconstruct => true;

    /// <summary>Compression ratio against an equivalent <see cref="IndexFlat"/>, ignoring ids.</summary>
    public double CompressionRatio => (double)(D * sizeof(float)) / CodeSize;

    protected override void TrainEncoder(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos)
    {
        if (!ByResidual)
        {
            Sq.Train(x[..(n * D)]);
            return;
        }

        var residuals = new float[(long)n * D];
        ComputeResiduals(x, n, listNos, residuals);
        Sq.Train(residuals);
    }

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
            Sq.EncodeBatch(x[..(n * D)], n, codes);
            return;
        }

        var residuals = new float[(long)n * D];
        ComputeResiduals(x, n, listNos, residuals);
        Sq.EncodeBatch(residuals, n, codes);
    }

    protected override unsafe void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore, Span<float> scores)
    {
        float[] scratch = ArrayPool<float>.Shared.Rent(D * 2);
        try
        {
            var decoded = scratch.AsSpan(0, D);
            var effective = scratch.AsSpan(D, D);

            if (ByResidual)
            {
                GetCentroid(list, effective);
                for (int j = 0; j < D; j++) effective[j] = query[j] - effective[j];
            }
            else
            {
                query[..D].CopyTo(effective);
            }

            var codes = Lists.GetCodes(list);
            int codeSize = CodeSize;

            // Everything the decode loop needs is pinned once for the whole list; the per-candidate
            // body is then just decode-and-score with no setup at all.
            fixed (float* pq = effective)
            fixed (float* pd = decoded)
            fixed (byte* pcodes = codes)
            fixed (float* poffsets = Sq.Offsets)
            fixed (float* psteps = Sq.Steps)
            {
                for (int i = 0; i < scores.Length; i++)
                {
                    Sq.DecodeUnchecked(pcodes + (long)i * codeSize, pd, poffsets, psteps);
                    scores[i] = VectorOps.Distance(pq, pd, D, MetricType);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(scratch);
        }
    }

    protected override void DecodeEntry(int list, int offset, Span<float> output)
    {
        Sq.Decode(Lists.GetCodes(list).Slice(offset * CodeSize, CodeSize), output);
        if (!ByResidual) return;

        Span<float> centroid = D <= 1024 ? stackalloc float[D] : new float[D];
        GetCentroid(list, centroid);
        for (int j = 0; j < D; j++) output[j] += centroid[j];
    }

    public override string Describe() =>
        $"IndexIVFScalarQuantizer(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {Sq}, " +
        $"{MetricType.ToShortString()}, {CompressionRatio:F0}x smaller than flat)";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IVFScalarQuantizer;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(Nprobe);
        writer.Write(ByResidual);
        IndexIO.WriteTo(writer, Quantizer);
        Sq.Write(writer);
        Lists.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int nlist = reader.ReadInt32();
        Nprobe = reader.ReadInt32();
        ByResidual = reader.ReadBoolean();
        var quantizer = IndexIO.ReadFrom(reader);
        Sq = ScalarQuantizer.Read(reader);
        var lists = InvertedLists.Read(reader);
        RestoreIvf(quantizer, lists, nlist);
        IsTrained = Sq.IsTrained;
    }
}
