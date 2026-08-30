using System.Runtime.InteropServices;
using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// IVF index that stores full-precision vectors inside each list.
/// <para>
/// Distances against probed candidates are exact, so the only recall loss comes from cells that
/// were not probed — which makes this the cleanest way to see what the coarse partition alone
/// costs, and the right choice whenever memory is adequate but a flat scan has become too slow.
/// Memory is the same as <see cref="IndexFlat"/> plus 8 bytes per vector for its id.
/// </para>
/// </summary>
public sealed class IndexIVFFlat : IndexIVF
{
    /// <param name="quantizer">Coarse quantizer, typically <c>new IndexFlatL2(d)</c>.</param>
    /// <param name="dimension">Vector dimension.</param>
    /// <param name="nlist">Number of cells. A good starting point is <c>sqrt(expected ntotal)</c>.</param>
    /// <param name="metric">Distance metric.</param>
    public IndexIVFFlat(Index quantizer, int dimension, int nlist, MetricType metric = MetricType.L2)
        : base(quantizer, dimension, nlist, dimension * sizeof(float), metric)
    {
        ByResidual = false; // vectors are stored verbatim
    }

    /// <summary>Convenience constructor that creates a matching flat coarse quantizer.</summary>
    public IndexIVFFlat(int dimension, int nlist, MetricType metric = MetricType.L2)
        : this(metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension),
               dimension, nlist, metric)
    {
    }

    public override bool SupportsReconstruct => true;

    protected override void EncodeVectors(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos, Span<byte> codes) =>
        MemoryMarshal.AsBytes(x[..(n * D)]).CopyTo(codes);

    protected override unsafe void ComputeListScores(ReadOnlySpan<float> query, int list, float coarseScore, Span<float> scores)
    {
        var vectors = MemoryMarshal.Cast<byte, float>(Lists.GetCodes(list));
        fixed (float* pq = query)
        fixed (float* pv = vectors)
        {
            for (int i = 0; i < scores.Length; i++)
                scores[i] = VectorOps.Distance(pq, pv + (long)i * D, D, MetricType);
        }
    }

    protected override void DecodeEntry(int list, int offset, Span<float> output) =>
        MemoryMarshal.Cast<byte, float>(Lists.GetCodes(list)).Slice(offset * D, D).CopyTo(output);

    public override string Describe() =>
        $"IndexIVFFlat(d={D}, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe}, {MetricType.ToShortString()}, exact within probed cells)";

    // -------------------------------------------------------- Serialization

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.IVFFlat;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(Nprobe);
        IndexIO.WriteTo(writer, Quantizer);
        Lists.Write(writer);
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int nlist = reader.ReadInt32();
        Nprobe = reader.ReadInt32();
        var quantizer = IndexIO.ReadFrom(reader);
        var lists = InvertedLists.Read(reader);
        RestoreIvf(quantizer, lists, nlist);
        IsTrained = true;
    }
}
