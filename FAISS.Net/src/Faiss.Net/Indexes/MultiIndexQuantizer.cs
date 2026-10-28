using Faiss.Net.Core;
using Faiss.Net.IO;

namespace Faiss.Net;

/// <summary>
/// Inverted multi-index coarse quantizer (Babenko and Lempitsky, CVPR 2012) — the FAISS
/// <c>MultiIndexQuantizer</c>, and the <c>IMI2x&lt;bits&gt;</c> of a factory string such as
/// <c>"IMI2x10,PQ16"</c>.
/// <para>
/// The vector is split into <see cref="M"/> halves (two, in practice), each with its own codebook
/// of <c>2^nbits</c> centroids. A cell is one centroid per half, so <c>IMI2x10</c> defines
/// <c>2^20</c> (about a million) cells from two codebooks of only 1024 centroids each — a cell count
/// a flat coarse quantizer could neither train nor search. The cells are implicit: nothing is stored
/// per cell, and <see cref="Index.Ntotal"/> is the cell count rather than a count of added vectors.
/// </para>
/// <para>
/// Search returns the k best cells without enumerating them: each half's centroids are sorted by
/// distance, and the multi-sequence algorithm walks the grid of index tuples in order of summed
/// distance with a priority queue, so producing <c>nprobe</c> cells costs about
/// <c>nprobe log nprobe</c> after <c>M * 2^nbits</c> distance computations. L2 only, as in FAISS.
/// </para>
/// </summary>
public sealed class MultiIndexQuantizer : Index
{
    /// <summary>The product quantizer whose sub-codebooks define the cells.</summary>
    public ProductQuantizer Pq { get; private set; }

    /// <summary>Number of sub-quantizers (the "2" of IMI2x10).</summary>
    public int M => Pq.M;

    /// <summary>Bits per sub-quantizer (the "10" of IMI2x10).</summary>
    public int Nbits => Pq.Nbits;

    /// <summary>Total number of cells, <c>2^(M * nbits)</c>.</summary>
    public long CellCount => 1L << (M * Nbits);

    /// <param name="dimension">Vector dimension; must be divisible by <paramref name="m"/>.</param>
    /// <param name="m">Sub-quantizers; 2 is the standard IMI.</param>
    /// <param name="nbits">Bits per sub-quantizer; the cell count is <c>2^(m * nbits)</c>.</param>
    public MultiIndexQuantizer(int dimension, int m = 2, int nbits = 8) : base(dimension, MetricType.L2)
    {
        if ((long)m * nbits > 30)
            throw new ArgumentException($"{m}x{nbits} would define 2^{m * nbits} cells; at most 2^30 are supported.");
        Pq = new ProductQuantizer(dimension, m, nbits);
        IsTrained = false;
    }

    /// <summary>The quantizer trains on the data itself; an IVF index must not k-means it and add centroids.</summary>
    internal override bool TrainsAlone => true;

    public override bool SupportsReconstruct => true;

    public override void Train(ReadOnlySpan<float> x)
    {
        ValidateBatch(x, nameof(x));
        Pq.Train(x);
        Ntotal = CellCount;
        IsTrained = true;
    }

    /// <summary>Not supported: the cells are implicit products of the codebooks, not stored vectors.</summary>
    public override void Add(ReadOnlySpan<float> x) =>
        throw new NotSupportedException("MultiIndexQuantizer cells are implicit; train it instead of adding vectors.");

    /// <summary>Keeps the codebooks: the cells cannot be removed without retraining.</summary>
    public override void Reset() { }

    public override unsafe void Search(ReadOnlySpan<float> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        EnsureTrained();
        if (nq == 0) return;
        int ksub = Pq.Ksub, m = M, dsub = Pq.Dsub;
        var queryArray = queries[..(nq * D)].ToArray();
        var outD = new float[(long)nq * k];
        var outL = new long[(long)nq * k];

        Parallel.For(0, nq, new ParallelOptions { MaxDegreeOfParallelism = Threads > 0 ? Threads : Environment.ProcessorCount }, q =>
        {
            // Per half: centroid distances, then centroid indices sorted by that distance.
            var table = new float[m * ksub];
            Pq.ComputeDistanceTable(queryArray.AsSpan(q * D, D), table, MetricType.L2);
            var order = new int[m][];
            for (int s = 0; s < m; s++)
            {
                var indices = new int[ksub];
                for (int c = 0; c < ksub; c++) indices[c] = c;
                var keys = table.AsSpan(s * ksub, ksub).ToArray();
                Array.Sort(keys, indices);
                order[s] = indices;
            }

            // Multi-sequence: pop the tuple of ranks with the smallest summed distance, push its
            // successors along each axis. A tuple is pushed only from its lexicographically first
            // predecessor, so each enters the queue once without a visited set.
            var queue = new PriorityQueue<int[], float>();
            var start = new int[m];
            queue.Enqueue(start, SumDistance(start));
            int found = 0;
            while (found < k && queue.TryDequeue(out var ranks, out float distance))
            {
                long label = 0;
                for (int s = 0; s < m; s++) label |= (long)order[s][ranks[s]] << (s * Nbits);
                outD[(long)q * k + found] = distance;
                outL[(long)q * k + found] = label;
                found++;

                for (int s = 0; s < m; s++)
                {
                    if (ranks[s] + 1 >= ksub) continue;
                    // Push the successor along axis s only if every lower axis is at rank 0 —
                    // otherwise another predecessor already pushed it.
                    bool first = true;
                    for (int t = 0; t < s; t++) if (ranks[t] != 0) { first = false; break; }
                    if (!first) continue;
                    var next = (int[])ranks.Clone();
                    next[s]++;
                    queue.Enqueue(next, SumDistance(next));
                }
            }
            for (; found < k; found++)
            {
                outD[(long)q * k + found] = float.MaxValue;
                outL[(long)q * k + found] = -1;
            }

            float SumDistance(int[] r)
            {
                float sum = 0;
                for (int s = 0; s < m; s++) sum += table[s * ksub + order[s][r[s]]];
                return sum;
            }
        });

        outD.CopyTo(distances);
        outL.CopyTo(labels);
    }

    /// <summary>The cell's centroid: one sub-centroid per half, concatenated.</summary>
    public override void Reconstruct(long key, Span<float> output)
    {
        EnsureTrained();
        if (key < 0 || key >= CellCount) throw new ArgumentOutOfRangeException(nameof(key));
        int ksub = Pq.Ksub, dsub = Pq.Dsub;
        for (int s = 0; s < M; s++)
        {
            int c = (int)((key >> (s * Nbits)) & (ksub - 1));
            Pq.Centroids.AsSpan((s * ksub + c) * dsub, dsub).CopyTo(output.Slice(s * dsub, dsub));
        }
    }

    public override long MemoryUsage => Pq.CodebookBytes;

    public override string Describe() => $"MultiIndexQuantizer(d={D}, IMI{M}x{Nbits}, {CellCount} cells)";

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.MultiIndexQuantizer;

    protected internal override void WriteBody(BinaryWriter writer) => Pq.Write(writer);

    protected internal override void ReadBody(BinaryReader reader)
    {
        Pq = ProductQuantizer.Read(reader);
        IsTrained = Pq.IsTrained;
        Ntotal = IsTrained ? CellCount : 0;
    }
}
