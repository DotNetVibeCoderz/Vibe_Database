using System.Runtime.CompilerServices;

namespace Faiss.Net.Utils;

/// <summary>
/// Fixed-capacity top-k heap over caller-supplied storage.
/// <para>
/// The root always holds the <em>worst</em> retained candidate, so a new candidate can be rejected
/// with one comparison against <see cref="WorstScore"/>. That test rejects the overwhelming majority
/// of candidates in a scan, which is why brute-force search stays memory-bound rather than
/// heap-bound. Storage is a caller-owned <see cref="Span{T}"/> (typically a slice of the caller's
/// output arrays or a pooled buffer), so scanning allocates nothing.
/// </para>
/// </summary>
/// <typeparam name="TOrder">Ordering policy; see <see cref="IScoreOrder"/>.</typeparam>
public ref struct KnnHeap<TOrder> where TOrder : struct, IScoreOrder
{
    private readonly Span<float> _scores;
    private readonly Span<long> _ids;

    /// <summary>Number of candidates currently retained.</summary>
    public int Count { get; private set; }

    /// <summary>Maximum number of candidates retained (k).</summary>
    public int Capacity => _scores.Length;

    public KnnHeap(Span<float> scores, Span<long> ids)
    {
        if (scores.Length != ids.Length)
            throw new ArgumentException("Score and id buffers must have equal length.");
        _scores = scores;
        _ids = ids;
        Count = 0;
    }

    /// <summary>
    /// Score of the worst retained candidate, or <see cref="IScoreOrder.Worst"/> while the heap has
    /// spare capacity. Candidates not better than this can be skipped.
    /// </summary>
    public float WorstScore
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Count < Capacity ? TOrder.Worst : _scores[0];
    }

    /// <summary>Adds a candidate if it belongs in the top k. Cheap rejection is inlined at call sites.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(float score, long id)
    {
        if (Count < Capacity)
        {
            _scores[Count] = score;
            _ids[Count] = id;
            Count++;
            SiftUp(Count - 1);
        }
        else if (TOrder.Better(score, _scores[0]))
        {
            _scores[0] = score;
            _ids[0] = id;
            SiftDown(0);
        }
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) >> 1;
            // Root holds the worst element, so a child rises while it is worse than its parent.
            if (TOrder.Better(_scores[index], _scores[parent])) break;
            Swap(index, parent);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        int n = Count;
        while (true)
        {
            int left = 2 * index + 1;
            if (left >= n) break;
            int worst = left;
            int right = left + 1;
            if (right < n && TOrder.Better(_scores[left], _scores[right])) worst = right;
            if (TOrder.Better(_scores[worst], _scores[index])) break;
            Swap(index, worst);
            index = worst;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Swap(int i, int j)
    {
        (_scores[i], _scores[j]) = (_scores[j], _scores[i]);
        (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
    }

    /// <summary>
    /// Sorts the retained candidates best-first in place and pads unused slots with
    /// <see cref="IScoreOrder.Worst"/> / id <c>-1</c>, matching FAISS, which returns <c>-1</c> labels
    /// when fewer than k results exist.
    /// </summary>
    public void Finish()
    {
        int n = Count;
        // Repeated extract-worst leaves the array sorted worst-last -> best-first after reversal.
        for (int end = n - 1; end > 0; end--)
        {
            Swap(0, end);
            int index = 0, limit = end;
            while (true)
            {
                int left = 2 * index + 1;
                if (left >= limit) break;
                int worst = left;
                int right = left + 1;
                if (right < limit && TOrder.Better(_scores[left], _scores[right])) worst = right;
                if (TOrder.Better(_scores[worst], _scores[index])) break;
                Swap(index, worst);
                index = worst;
            }
        }

        for (int i = n; i < Capacity; i++)
        {
            _scores[i] = TOrder.Worst;
            _ids[i] = -1;
        }
    }
}
