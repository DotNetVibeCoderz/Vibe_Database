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

    protected override void ComputeListScores(
        ReadOnlySpan<float> query, int list, float coarseScore, ReadOnlySpan<byte> codes, Span<float> scores,
        object? queryState) =>
        ScoreList(query, list, coarseScore, codes, default, scores, queryState);

    protected override void ComputeListScoresSubset(
        ReadOnlySpan<float> query, int list, float coarseScore, ReadOnlySpan<byte> codes,
        ReadOnlySpan<int> offsets, Span<float> scores, object? queryState) =>
        ScoreList(query, list, coarseScore, codes, offsets, scores, queryState);

    /// <summary>Scores every entry when <paramref name="offsets"/> is empty, otherwise only those entries.</summary>
    private unsafe void ScoreList(
        ReadOnlySpan<float> query, int list, float coarseScore, ReadOnlySpan<byte> codes,
        ReadOnlySpan<int> offsets, Span<float> scores, object? queryState)
    {
        bool all = offsets.IsEmpty;
        var vectors = MemoryMarshal.Cast<byte, float>(codes);
        fixed (float* pq = query)
        fixed (float* pv = vectors)
        {
            // Two loops rather than one with a per-entry choice: the unfiltered scan stays branch-free.
            if (all)
                for (int i = 0; i < scores.Length; i++)
                    scores[i] = VectorOps.Distance(pq, pv + (long)i * D, D, MetricType);
            else
                for (int i = 0; i < scores.Length; i++)
                    scores[i] = VectorOps.Distance(pq, pv + (long)offsets[i] * D, D, MetricType);
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
