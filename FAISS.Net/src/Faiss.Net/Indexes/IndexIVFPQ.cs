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

    private float[]? _precomputed;
    private readonly object _precomputeLock = new();

    /// <summary>
    /// Replaces the per-list lookup-table build with a precomputed term, the
    /// <c>use_precomputed_table</c> of FAISS. Off by default because it costs
    /// <see cref="PrecomputedTableBytes"/> of memory (67 MB at <c>nlist = 4096, m = 16</c>); turn it
    /// on when <c>nprobe</c> is large enough that rebuilding the table per probed cell dominates.
    /// <para>
    /// With residual coding the ADC distance to a code in cell <c>c</c> splits into
    /// <c>‖x − y_c‖² + (‖y_r‖² + 2⟨y_c, y_r⟩) − 2⟨x, y_r⟩</c>. The first term is the coarse
    /// distance the quantizer already returned, the second depends only on the cell and the
    /// codeword and is precomputed once, and the third depends only on the query and is computed
    /// once per query. Each probed cell then costs one table addition instead of a table build.
    /// Applies to residual (L2) indexes; inner-product indexes encode raw vectors and never rebuild
    /// the table per cell in the first place.
    /// </para>
    /// </summary>
    public bool UsePrecomputedTable { get; set; }

    /// <summary>Memory the precomputed table takes when enabled: <c>nlist * m * 2^nbits</c> floats.</summary>
    public long PrecomputedTableBytes => (long)Nlist * Pq.M * Pq.Ksub * sizeof(float);

    /// <summary>
    /// Builds the cell-dependent table term now rather than on the first search. Called
    /// automatically when <see cref="UsePrecomputedTable"/> is on.
    /// </summary>
    public unsafe void PrecomputeTable()
    {
        EnsureTrained();
        if (!ByResidual) return;
        int m = Pq.M, ksub = Pq.Ksub, dsub = Pq.Dsub;
        var table = new float[(long)Nlist * m * ksub];

        // ||codeword||^2, shared by every cell.
        var norms = new float[m * ksub];
        fixed (float* books = Pq.Centroids)
            for (int i = 0; i < m * ksub; i++) norms[i] = VectorOps.Norm2Sqr(books + (long)i * dsub, dsub);

        Parallel.For(0, Nlist, list =>
        {
            var centroid = new float[D];
            GetCentroid(list, centroid);
            fixed (float* pc = centroid)
            fixed (float* books = Pq.Centroids)
            fixed (float* pt = table)
            {
                float* row = pt + (long)list * m * ksub;
                for (int sub = 0; sub < m; sub++)
                {
                    float* part = pc + sub * dsub;
                    for (int c = 0; c < ksub; c++)
                    {
                        int index = sub * ksub + c;
                        row[index] = norms[index] + 2f * VectorOps.InnerProduct(part, books + (long)index * dsub, dsub);
                    }
                }
            }
        });

        _precomputed = table;
    }

    /// <summary>Per-query state for the precomputed path: the query term and a scratch table.</summary>
    private sealed class PrecomputedQuery(float[] queryTerm, float[] table)
    {
        public readonly float[] QueryTerm = queryTerm;
        public readonly float[] Table = table;
    }

    protected override object? PrepareQuery(ReadOnlySpan<float> query)
    {
        if (!UsePrecomputedTable || !ByResidual) return null;
        if (_precomputed is null)
            lock (_precomputeLock)
                if (_precomputed is null) PrecomputeTable();

        int size = Pq.DistanceTableSize;
        var queryTerm = ArrayPool<float>.Shared.Rent(size);
        var table = ArrayPool<float>.Shared.Rent(size);
        // -2 <x, codeword>: the inner-product table, scaled.
        Pq.ComputeDistanceTable(query, queryTerm.AsSpan(0, size), MetricType.InnerProduct);
        var span = queryTerm.AsSpan(0, size);
        for (int i = 0; i < span.Length; i++) span[i] *= -2f;
        return new PrecomputedQuery(queryTerm, table);
    }

    protected override void ReleaseQuery(object? queryState)
    {
        if (queryState is not PrecomputedQuery state) return;
        ArrayPool<float>.Shared.Return(state.QueryTerm);
        ArrayPool<float>.Shared.Return(state.Table);
    }

    protected override void TrainEncoder(ReadOnlySpan<float> x, int n, ReadOnlySpan<long> listNos)
    {
        _precomputed = null; // depends on the codebooks being replaced
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
        int tableSize = Pq.DistanceTableSize;
        if (queryState is PrecomputedQuery precomputed && _precomputed is { } cellTerms)
        {
            fixed (float* pt = precomputed.Table)
            fixed (float* pq = precomputed.QueryTerm)
            fixed (float* pcell = cellTerms)
            fixed (byte* pc = codes)
            {
                // One vector add per probed cell replaces the ksub * d table build.
                Buffer.MemoryCopy(pcell + (long)list * tableSize, pt, (long)tableSize * sizeof(float), (long)tableSize * sizeof(float));
                VectorOps.Add(pt, pq, tableSize);
                int codeSize = CodeSize;
                // Two loops rather than one with a per-entry choice: the unfiltered scan stays branch-free.
                if (all)
                    for (int i = 0; i < scores.Length; i++)
                        scores[i] = coarseScore + Pq.DistanceFromTable(pt, pc + (long)i * codeSize);
                else
                    for (int i = 0; i < scores.Length; i++)
                        scores[i] = coarseScore + Pq.DistanceFromTable(pt, pc + (long)offsets[i] * codeSize);
            }
            return;
        }

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

            int codeSize = CodeSize;
            fixed (float* pt = table)
            fixed (byte* pc = codes)
            {
                if (all)
                    for (int i = 0; i < scores.Length; i++)
                        scores[i] = Pq.DistanceFromTable(pt, pc + (long)i * codeSize);
                else
                    for (int i = 0; i < scores.Length; i++)
                        scores[i] = Pq.DistanceFromTable(pt, pc + (long)offsets[i] * codeSize);
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
        _precomputed = null;
    }
}
