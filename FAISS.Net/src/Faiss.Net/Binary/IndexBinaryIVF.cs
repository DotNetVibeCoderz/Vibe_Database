using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net.Binary;

/// <summary>
/// Inverted-file index over binary codes: the same cell-probing idea as <see cref="IndexIVF"/>,
/// with Hamming distance throughout.
/// <para>
/// Clustering happens in Hamming space, so a centroid is not a mean but a <em>majority vote</em> —
/// bit <c>j</c> of a centroid is set when more than half the assigned codes have it set. That is the
/// exact minimizer of summed Hamming distance within a cell, which makes binary k-means the direct
/// analogue of Lloyd's algorithm rather than an approximation of it.
/// </para>
/// </summary>
public sealed class IndexBinaryIVF : IndexBinary
{
    private readonly List<long>[] _listIds;
    private readonly List<byte[]> _centroids = [];
    private byte[][] _listCodes;

    /// <summary>Number of cells.</summary>
    public int Nlist { get; }

    /// <summary>Cells visited per query; the recall/latency dial.</summary>
    public int Nprobe { get; set; } = 1;

    /// <summary>Binary k-means iterations used to train the coarse quantizer.</summary>
    public int TrainingIterations { get; set; } = 15;

    public IndexBinaryIVF(int dimension, int nlist) : base(dimension)
    {
        if (nlist <= 0) throw new ArgumentOutOfRangeException(nameof(nlist));
        Nlist = nlist;
        _listIds = new List<long>[nlist];
        _listCodes = new byte[nlist][];
        for (int i = 0; i < nlist; i++)
        {
            _listIds[i] = [];
            _listCodes[i] = [];
        }
        IsTrained = false;
    }

    /// <summary>Trains the coarse quantizer with binary k-means (majority-vote centroids).</summary>
    public override void Train(ReadOnlySpan<byte> x)
    {
        int n = x.Length / CodeSize;
        if (n == 0) throw new ArgumentException("No training codes supplied.", nameof(x));

        var rng = new RandomGenerator(1234);
        _centroids.Clear();

        // Seed from distinct random codes; duplicates would produce cells that can never win.
        var chosen = new HashSet<int>();
        var permutation = rng.Permutation(n);
        for (int i = 0; i < permutation.Length && _centroids.Count < Nlist; i++)
        {
            if (!chosen.Add(permutation[i])) continue;
            _centroids.Add(x.Slice(permutation[i] * CodeSize, CodeSize).ToArray());
        }
        while (_centroids.Count < Nlist) _centroids.Add(x[..CodeSize].ToArray());

        var assignment = new int[n];
        var bitCounts = new int[Nlist][];
        for (int c = 0; c < Nlist; c++) bitCounts[c] = new int[D];
        var counts = new int[Nlist];

        for (int iteration = 0; iteration < TrainingIterations; iteration++)
        {
            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                int best = NearestCentroid(x.Slice(i * CodeSize, CodeSize));
                if (assignment[i] != best) { assignment[i] = best; changed = true; }
            }
            if (!changed && iteration > 0) break;

            foreach (var counter in bitCounts) Array.Clear(counter);
            Array.Clear(counts);

            for (int i = 0; i < n; i++)
            {
                int cell = assignment[i];
                counts[cell]++;
                var code = x.Slice(i * CodeSize, CodeSize);
                var counter = bitCounts[cell];
                for (int bit = 0; bit < D; bit++)
                    if (HammingOps.GetBit(code, bit)) counter[bit]++;
            }

            for (int c = 0; c < Nlist; c++)
            {
                if (counts[c] == 0)
                {
                    // Empty cell: reseed from a random code rather than leaving it unreachable.
                    _centroids[c] = x.Slice(rng.NextInt(n) * CodeSize, CodeSize).ToArray();
                    continue;
                }
                var centroid = _centroids[c];
                int half = counts[c] / 2;
                for (int bit = 0; bit < D; bit++)
                    HammingOps.SetBit(centroid, bit, bitCounts[c][bit] > half);
            }
        }

        IsTrained = true;
    }

    private int NearestCentroid(ReadOnlySpan<byte> code)
    {
        int best = 0, bestDistance = int.MaxValue;
        for (int c = 0; c < _centroids.Count; c++)
        {
            int distance = HammingOps.Distance(code, _centroids[c]);
            if (distance < bestDistance) { bestDistance = distance; best = c; }
        }
        return best;
    }

    public override void Add(ReadOnlySpan<byte> x)
    {
        if (!IsTrained) throw new InvalidOperationException("IndexBinaryIVF must be trained before adding.");
        int n = x.Length / CodeSize;
        for (int i = 0; i < n; i++)
        {
            var code = x.Slice(i * CodeSize, CodeSize);
            int cell = NearestCentroid(code);
            AppendToList(cell, Ntotal + i, code);
        }
        Ntotal += n;
    }

    private void AppendToList(int cell, long id, ReadOnlySpan<byte> code)
    {
        var ids = _listIds[cell];
        int offset = ids.Count * CodeSize;
        if (offset + CodeSize > _listCodes[cell].Length)
        {
            int grown = Math.Max(offset + CodeSize, Math.Max(8 * CodeSize, _listCodes[cell].Length * 2));
            Array.Resize(ref _listCodes[cell], grown);
        }
        code.CopyTo(_listCodes[cell].AsSpan(offset, CodeSize));
        ids.Add(id);
    }

    public override unsafe void Search(ReadOnlySpan<byte> queries, int nq, int k, Span<float> distances, Span<long> labels)
    {
        if (!IsTrained) throw new InvalidOperationException("IndexBinaryIVF must be trained before searching.");
        if (nq == 0) return;
        if (Ntotal == 0)
        {
            distances.Fill(float.MaxValue);
            labels.Fill(-1);
            return;
        }

        int nprobe = Math.Clamp(Nprobe, 1, Nlist);
        fixed (byte* xq = queries)
        fixed (float* pdis = distances)
        fixed (long* plab = labels)
        {
            nint qp = (nint)xq, dp = (nint)pdis, lp = (nint)plab;
            int codeSize = CodeSize;

            void SearchOne(int q)
            {
                var query = new ReadOnlySpan<byte>((byte*)qp + (long)q * codeSize, codeSize);

                // Rank cells by Hamming distance to their centroid, then probe the closest nprobe.
                Span<int> cells = stackalloc int[nprobe];
                Span<int> cellDistances = stackalloc int[nprobe];
                cellDistances.Fill(int.MaxValue);
                cells.Fill(-1);
                for (int c = 0; c < Nlist; c++)
                {
                    int distance = HammingOps.Distance(query, _centroids[c]);
                    for (int slot = 0; slot < nprobe; slot++)
                    {
                        if (distance >= cellDistances[slot]) continue;
                        for (int shift = nprobe - 1; shift > slot; shift--)
                        {
                            cellDistances[shift] = cellDistances[shift - 1];
                            cells[shift] = cells[shift - 1];
                        }
                        cellDistances[slot] = distance;
                        cells[slot] = c;
                        break;
                    }
                }

                var heap = new KnnHeap<AscendingOrder>(
                    new Span<float>((float*)dp + (long)q * k, k),
                    new Span<long>((long*)lp + (long)q * k, k));

                foreach (int cell in cells)
                {
                    if (cell < 0) continue;
                    var ids = _listIds[cell];
                    var codes = _listCodes[cell];
                    for (int i = 0; i < ids.Count; i++)
                    {
                        int distance = HammingOps.Distance(query, codes.AsSpan(i * codeSize, codeSize));
                        if (distance < heap.WorstScore) heap.Push(distance, ids[i]);
                    }
                }
                heap.Finish();
            }

            if (nq == 1 || Threads == 1) for (int q = 0; q < nq; q++) SearchOne(q);
            else Parallel.For(0, nq, SearchOne);
        }
    }

    public override RangeSearchResult RangeSearch(ReadOnlySpan<byte> queries, int radius)
    {
        int nq = queries.Length / CodeSize;
        var perQuery = new List<(long Id, float Distance)>[nq];
        int nprobe = Math.Clamp(Nprobe, 1, Nlist);

        for (int q = 0; q < nq; q++)
        {
            var query = queries.Slice(q * CodeSize, CodeSize);

            // Rank cells without LINQ: a span cannot be captured by a lambda, and this avoids the
            // per-query delegate and array allocations a sort would cost anyway.
            var cellDistances = new (int Cell, int Distance)[Nlist];
            for (int c = 0; c < Nlist; c++) cellDistances[c] = (c, HammingOps.Distance(query, _centroids[c]));
            Array.Sort(cellDistances, static (a, b) => a.Distance.CompareTo(b.Distance));

            var hits = new List<(long, float)>();
            foreach (var (cell, _) in cellDistances.Take(nprobe))
            {
                var ids = _listIds[cell];
                var codes = _listCodes[cell];
                for (int i = 0; i < ids.Count; i++)
                {
                    int distance = HammingOps.Distance(query, codes.AsSpan(i * CodeSize, CodeSize));
                    if (distance <= radius) hits.Add((ids[i], distance));
                }
            }
            perQuery[q] = hits;
        }

        return Core.BruteForce.Flatten(perQuery);
    }

    public override void Reset()
    {
        for (int i = 0; i < Nlist; i++) _listIds[i].Clear();
        Ntotal = 0;
    }

    public override long MemoryUsage
    {
        get
        {
            long total = (long)_centroids.Count * CodeSize;
            for (int i = 0; i < Nlist; i++)
                total += _listCodes[i].Length + (long)_listIds[i].Count * sizeof(long);
            return total;
        }
    }

    public override string Describe() =>
        $"IndexBinaryIVF(d={D} bits, ntotal={Ntotal}, nlist={Nlist}, nprobe={Nprobe})";

    // -------------------------------------------------------- Serialization

    protected internal override int SerializationParameter => Nlist;

    protected internal override IndexTypeCode TypeCode => IndexTypeCode.BinaryIVF;

    protected internal override void WriteBody(BinaryWriter writer)
    {
        writer.Write(Nlist);
        writer.Write(Nprobe);
        writer.Write(_centroids.Count);
        foreach (var centroid in _centroids) writer.Write(centroid);
        for (int i = 0; i < Nlist; i++)
        {
            writer.Write(_listIds[i].Count);
            foreach (long id in _listIds[i]) writer.Write(id);
            writer.Write(_listCodes[i].AsSpan(0, _listIds[i].Count * CodeSize));
        }
    }

    protected internal override void ReadBody(BinaryReader reader)
    {
        int nlist = reader.ReadInt32();
        if (nlist != Nlist)
            throw new InvalidDataException($"Serialized nlist {nlist} does not match the constructed {Nlist}.");
        Nprobe = reader.ReadInt32();

        int centroidCount = reader.ReadInt32();
        _centroids.Clear();
        for (int i = 0; i < centroidCount; i++)
        {
            var centroid = new byte[CodeSize];
            reader.ReadExactly(centroid);
            _centroids.Add(centroid);
        }

        long total = 0;
        for (int i = 0; i < Nlist; i++)
        {
            int count = reader.ReadInt32();
            _listIds[i].Clear();
            for (int j = 0; j < count; j++) _listIds[i].Add(reader.ReadInt64());
            _listCodes[i] = new byte[(long)count * CodeSize];
            reader.ReadExactly(_listCodes[i]);
            total += count;
        }
        Ntotal = total;
        IsTrained = true;
    }
}
