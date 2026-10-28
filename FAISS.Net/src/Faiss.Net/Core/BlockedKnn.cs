using System.Buffers;
using System.Numerics;
using Faiss.Net.Utils;

namespace Faiss.Net.Core;

/// <summary>
/// Many-to-many exact k-NN for L2 and inner product as a blocked matrix product — the FAISS
/// <c>exhaustive_L2sqr_blas</c> path, without BLAS.
/// <para>
/// The pairwise kernels in <see cref="VectorOps"/> compute one dot product at a time, so every
/// database vector is streamed from memory once per query and the scan is bandwidth-bound. Here a
/// tile of queries is scored against a cache-resident block of the database at once: the block is
/// transposed so the inner loop broadcasts one query coordinate and multiplies it into a whole row
/// of database coordinates, with a 4 x (2 x vector-width) register tile of accumulators. Each loaded
/// database value is reused by four queries and each query value by a vector's worth of database
/// rows, which turns the scan compute-bound.
/// </para>
/// <para>
/// L2 uses <c>‖x‖² + ‖y‖² − 2⟨x, y⟩</c>. That decomposition loses precision when two vectors are
/// much closer to each other than to the origin, so tiny negative values are clamped to zero and
/// results can differ from the pairwise path in the last few bits — which is why it is used only
/// above a batch-size threshold and is tested against the direct kernel with a tolerance.
/// </para>
/// </summary>
internal static unsafe class BlockedKnn
{
    /// <summary>Database vectors per transposed block: 256 x d floats stays inside L2 for d up to ~256.</summary>
    private const int BlockRows = 256;

    /// <summary>Queries per register tile.</summary>
    private const int TileRows = 4;

    /// <summary>
    /// Smallest batch for which the blocked path pays for its setup (norms, transposes). Below it the
    /// pairwise kernels, which also split a single query across cores, are faster.
    /// </summary>
    public const int MinQueries = 16;

    /// <summary>True when the blocked kernel handles this configuration.</summary>
    public static bool Applies(int nq, int nb, int d, MetricType metric) =>
        nq >= MinQueries && nb >= BlockRows && d >= 8 &&
        (metric == MetricType.L2 || metric == MetricType.InnerProduct);

    public static void Knn<TOrder>(
        float* xq, int nq, float* xb, int nb, int d, int k, MetricType metric,
        float* outDistances, long* outLabels, long* ids, int threads)
        where TOrder : struct, IScoreOrder
    {
        bool l2 = metric == MetricType.L2;
        float[]? xNorms = null, qNorms = null;
        if (l2)
        {
            xNorms = new float[nb];
            qNorms = new float[nq];
            nint pb = (nint)xb, pq = (nint)xq;
            Parallel.For(0, (nb + 4095) / 4096, new ParallelOptions { MaxDegreeOfParallelism = threads }, chunk =>
            {
                int end = Math.Min(nb, (chunk + 1) * 4096);
                for (int i = chunk * 4096; i < end; i++) xNorms[i] = VectorOps.Norm2Sqr((float*)pb + (long)i * d, d);
            });
            for (int i = 0; i < nq; i++) qNorms[i] = VectorOps.Norm2Sqr(xq + (long)i * d, d);
        }

        // Enough query tiles to occupy every core, but never fewer than one register tile per task.
        int perTask = Math.Clamp((nq + threads - 1) / threads, TileRows, 32);
        perTask = (perTask + TileRows - 1) / TileRows * TileRows;
        int tasks = (nq + perTask - 1) / perTask;

        nint qp = (nint)xq, bp = (nint)xb, dp = (nint)outDistances, lp = (nint)outLabels, ip = (nint)ids;
        Parallel.For(0, tasks, new ParallelOptions { MaxDegreeOfParallelism = threads }, task =>
        {
            int first = task * perTask;
            int count = Math.Min(perTask, nq - first);
            ScanTile<TOrder>((float*)qp, first, count, (float*)bp, nb, d, k, l2, xNorms, qNorms,
                (float*)dp, (long*)lp, (long*)ip);
        });
    }

    private static void ScanTile<TOrder>(
        float* xq, int first, int count, float* xb, int nb, int d, int k, bool l2,
        float[]? xNorms, float[]? qNorms, float* outDistances, long* outLabels, long* ids)
        where TOrder : struct, IScoreOrder
    {
        int width = Vector<float>.Count;
        int columns = 2 * width;
        int stride = (BlockRows + columns - 1) / columns * columns;

        float[] transposed = ArrayPool<float>.Shared.Rent(d * stride);
        float[] tile = ArrayPool<float>.Shared.Rent(TileRows * columns);
        var counts = new int[count];
        var worst = new float[count];
        Array.Fill(worst, TOrder.Worst);

        try
        {
            fixed (float* bt = transposed)
            fixed (float* tp = tile)
            {
                for (int blockStart = 0; blockStart < nb; blockStart += BlockRows)
                {
                    int rows = Math.Min(BlockRows, nb - blockStart);
                    int padded = (rows + columns - 1) / columns * columns;

                    // Transpose the block: bt[c * stride + j] = xb[blockStart + j][c]. Padding
                    // columns are zeroed so the kernel never needs a tail case.
                    float* block = xb + (long)blockStart * d;
                    for (int c = 0; c < d; c++)
                    {
                        float* row = bt + (long)c * stride;
                        for (int j = 0; j < rows; j++) row[j] = block[(long)j * d + c];
                        for (int j = rows; j < padded; j++) row[j] = 0f;
                    }

                    for (int r0 = 0; r0 < count; r0 += TileRows)
                    {
                        // A tile past the last query reuses the last row; its results are ignored.
                        float* q0 = xq + (long)(first + r0) * d;
                        float* q1 = xq + (long)(first + Math.Min(r0 + 1, count - 1)) * d;
                        float* q2 = xq + (long)(first + Math.Min(r0 + 2, count - 1)) * d;
                        float* q3 = xq + (long)(first + Math.Min(r0 + 3, count - 1)) * d;
                        int live = Math.Min(TileRows, count - r0);

                        for (int j = 0; j < padded; j += columns)
                        {
                            Kernel(q0, q1, q2, q3, bt + j, stride, d, tp, width);

                            int valid = Math.Min(columns, rows - j);
                            for (int r = 0; r < live; r++)
                            {
                                int local = r0 + r;
                                float* scores = tp + r * columns;
                                float queryNorm = l2 ? qNorms![first + local] : 0f;
                                for (int c = 0; c < valid; c++)
                                {
                                    int index = blockStart + j + c;
                                    float score = l2
                                        ? MathF.Max(0f, queryNorm + xNorms![index] - 2f * scores[c])
                                        : scores[c];
                                    if (!TOrder.Better(score, worst[local])) continue;

                                    long query = first + local;
                                    var heap = new KnnHeap<TOrder>(
                                        new Span<float>(outDistances + query * k, k),
                                        new Span<long>(outLabels + query * k, k), counts[local]);
                                    heap.Push(score, ids != null ? ids[index] : index);
                                    counts[local] = heap.Count;
                                    worst[local] = heap.WorstScore;
                                }
                            }
                        }
                    }
                }
            }

            for (int local = 0; local < count; local++)
            {
                long query = first + local;
                var heap = new KnnHeap<TOrder>(
                    new Span<float>(outDistances + query * k, k),
                    new Span<long>(outLabels + query * k, k), counts[local]);
                heap.Finish();
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(transposed);
            ArrayPool<float>.Shared.Return(tile);
        }
    }

    /// <summary>
    /// 4 x (2 x width) register tile: <c>out[r][c] = ⟨q_r, column c⟩</c> over the transposed block.
    /// Per coordinate it loads two database vectors and four broadcast query values, then issues
    /// eight independent multiply-adds — enough independent chains to keep the FMA units busy.
    /// </summary>
    private static void Kernel(float* q0, float* q1, float* q2, float* q3, float* bt, int stride, int d, float* output, int width)
    {
        Vector<float> a00 = default, a01 = default, a10 = default, a11 = default;
        Vector<float> a20 = default, a21 = default, a30 = default, a31 = default;

        for (int c = 0; c < d; c++)
        {
            float* row = bt + (long)c * stride;
            var b0 = *(Vector<float>*)row;
            var b1 = *(Vector<float>*)(row + width);

            var v0 = new Vector<float>(q0[c]);
            var v1 = new Vector<float>(q1[c]);
            var v2 = new Vector<float>(q2[c]);
            var v3 = new Vector<float>(q3[c]);

            a00 += v0 * b0; a01 += v0 * b1;
            a10 += v1 * b0; a11 += v1 * b1;
            a20 += v2 * b0; a21 += v2 * b1;
            a30 += v3 * b0; a31 += v3 * b1;
        }

        int columns = 2 * width;
        *(Vector<float>*)(output) = a00; *(Vector<float>*)(output + width) = a01;
        *(Vector<float>*)(output + columns) = a10; *(Vector<float>*)(output + columns + width) = a11;
        *(Vector<float>*)(output + 2 * columns) = a20; *(Vector<float>*)(output + 2 * columns + width) = a21;
        *(Vector<float>*)(output + 3 * columns) = a30; *(Vector<float>*)(output + 3 * columns + width) = a31;
    }
}
