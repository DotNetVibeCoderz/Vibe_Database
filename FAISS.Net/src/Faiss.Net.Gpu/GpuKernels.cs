using ILGPU;

namespace Faiss.Net.Gpu;

/// <summary>
/// Kernels shared by the flat and IVF GPU indexes.
/// <para>
/// Every kernel here is a plain static method over <see cref="ArrayView{T}"/>s and scalars, which is
/// all ILGPU needs to compile it for CUDA, OpenCL or its CPU accelerator. The selection kernels keep
/// their running top-k in the output buffer itself, insertion-sorted, rather than in a private
/// array: a fixed-size per-thread array would have to be sized for the largest k at compile time,
/// while the output buffer is already exactly the right size.
/// </para>
/// <para>
/// Ties keep the candidate seen first (every comparison is strict). Candidates are visited in the
/// same order the CPU indexes visit them — position order for flat, probe order then list order for
/// IVF — so equal scores resolve the same way on both sides wherever the CPU heap does too.
/// </para>
/// </summary>
internal static class GpuKernels
{
    /// <summary>Metric code passed to kernels: 0 = squared L2 (smaller is better), 1 = inner product.</summary>
    public static int MetricCode(MetricType metric) => metric == MetricType.InnerProduct ? 1 : 0;

    /// <summary>One thread per (query, database vector) pair; writes the <c>nq x ntotal</c> distance matrix.</summary>
    public static void Distance(
        Index2D index, ArrayView<float> queries, ArrayView<float> database, ArrayView<float> distances,
        int d, int ntotal, int metric)
    {
        int q = index.X;
        int j = index.Y;
        if (j >= ntotal) return;

        long queryOffset = (long)q * d;
        long vectorOffset = (long)j * d;
        float sum = 0f;

        if (metric == 1)
        {
            for (int i = 0; i < d; i++) sum += queries[queryOffset + i] * database[vectorOffset + i];
        }
        else
        {
            for (int i = 0; i < d; i++)
            {
                float diff = queries[queryOffset + i] - database[vectorOffset + i];
                sum += diff * diff;
            }
        }

        distances[(long)q * ntotal + j] = sum;
    }

    /// <summary>
    /// One thread per query: a single pass over that query's distance row, keeping the k best
    /// positions. When <paramref name="useMask"/> is set, positions whose mask byte is zero are
    /// skipped — that is how a host-evaluated <see cref="IDSelector"/> reaches the device, and it is
    /// applied inside the scan so the k results are k members, not k results filtered afterwards.
    /// Keeping selection on the device is what avoids shipping the full matrix back across the bus.
    /// </summary>
    public static void Select(
        Index1D index, ArrayView<float> distances, ArrayView<float> outDistances, ArrayView<long> outLabels,
        ArrayView<byte> mask, int ntotal, int k, int metric, int nq, int useMask)
    {
        int q = index.X;
        if (q >= nq) return;

        long rowOffset = (long)q * ntotal;
        long outOffset = (long)q * k;
        bool similarity = metric == 1;
        Clear(outDistances, outLabels, outOffset, k, similarity);

        for (int j = 0; j < ntotal; j++)
        {
            if (useMask != 0 && mask[j] == 0) continue;
            Insert(outDistances, outLabels, outOffset, k, distances[rowOffset + j], j, similarity);
        }
    }

    /// <summary>
    /// One thread per query: selects the final k from the <c>nprobe * k</c> partial results the
    /// per-list scan wrote for that query. Padding entries (label <c>-1</c>) are skipped, so a query
    /// whose probed lists hold fewer than k candidates comes out padded exactly like the CPU heap.
    /// </summary>
    public static void Merge(
        Index1D index, ArrayView<float> partialDistances, ArrayView<long> partialLabels,
        ArrayView<float> outDistances, ArrayView<long> outLabels, int candidates, int k, int metric, int nq)
    {
        int q = index.X;
        if (q >= nq) return;

        long inOffset = (long)q * candidates;
        long outOffset = (long)q * k;
        bool similarity = metric == 1;
        Clear(outDistances, outLabels, outOffset, k, similarity);

        for (int j = 0; j < candidates; j++)
        {
            long label = partialLabels[inOffset + j];
            if (label < 0) continue;
            Insert(outDistances, outLabels, outOffset, k, partialDistances[inOffset + j], label, similarity);
        }
    }

    /// <summary>Fills a k-slot result window with the worst score and label <c>-1</c>.</summary>
    public static void Clear(ArrayView<float> distances, ArrayView<long> labels, long offset, int k, bool similarity)
    {
        float worst = similarity ? float.MinValue : float.MaxValue;
        for (int i = 0; i < k; i++)
        {
            distances[offset + i] = worst;
            labels[offset + i] = -1;
        }
    }

    /// <summary>Inserts one candidate into an insertion-sorted k-slot window if it beats the current worst.</summary>
    public static void Insert(
        ArrayView<float> distances, ArrayView<long> labels, long offset, int k, float score, long label, bool similarity)
    {
        float worst = distances[offset + k - 1];
        bool better = similarity ? score > worst : score < worst;
        if (!better) return;

        int position = k - 1;
        while (position > 0)
        {
            float previous = distances[offset + position - 1];
            bool shift = similarity ? score > previous : score < previous;
            if (!shift) break;
            distances[offset + position] = previous;
            labels[offset + position] = labels[offset + position - 1];
            position--;
        }
        distances[offset + position] = score;
        labels[offset + position] = label;
    }
}
