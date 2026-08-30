using System.Runtime.CompilerServices;
using Faiss.Net.Core;

namespace Faiss.Net;

/// <summary>
/// Product quantizer: splits a <c>d</c>-dimensional vector into <c>m</c> contiguous sub-vectors and
/// replaces each with the index of its nearest centroid in a per-subspace codebook.
/// <para>
/// A 128-dim float vector is 512 bytes; with <c>m = 16, nbits = 8</c> its code is 16 bytes, a 32x
/// reduction, and the codebooks together are only <c>m * 2^nbits * (d/m)</c> floats regardless of
/// how many vectors are stored. That asymmetry is what lets a PQ index hold a dataset that would
/// never fit uncompressed.
/// </para>
/// <para>
/// Search uses asymmetric distance computation (ADC): the query stays in full precision and a
/// lookup table of <c>m * 2^nbits</c> partial distances is built once per query, after which each
/// candidate costs <c>m</c> table lookups and adds — no vector arithmetic at all. Keeping the query
/// uncompressed is what makes ADC noticeably more accurate than comparing two codes.
/// </para>
/// </summary>
public sealed class ProductQuantizer
{
    /// <summary>Input vector dimension.</summary>
    public int D { get; private set; }

    /// <summary>Number of sub-quantizers. Must divide <see cref="D"/>.</summary>
    public int M { get; private set; }

    /// <summary>Bits per sub-code. 8 is the FAISS default and the byte-aligned fast path.</summary>
    public int Nbits { get; private set; }

    /// <summary>Centroids per sub-quantizer, <c>2^nbits</c>.</summary>
    public int Ksub { get; private set; }

    /// <summary>Dimension of each sub-vector, <c>d / m</c>.</summary>
    public int Dsub { get; private set; }

    /// <summary>Bytes per encoded vector.</summary>
    public int CodeSize { get; private set; }

    /// <summary>Codebooks laid out as <c>[m][ksub][dsub]</c>.</summary>
    public float[] Centroids { get; private set; }

    /// <summary>Set once <see cref="Train"/> has completed.</summary>
    public bool IsTrained { get; private set; }

    /// <summary>Clustering settings for codebook training.</summary>
    public ClusteringParameters ClusteringParameters { get; set; } = new() { Iterations = 25 };

    public ProductQuantizer(int d, int m, int nbits = 8)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d));
        if (m <= 0 || d % m != 0)
            throw new ArgumentException($"Sub-quantizer count m={m} must divide dimension d={d}.", nameof(m));
        if (nbits is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(nbits), "nbits must be between 1 and 16.");

        D = d;
        M = m;
        Nbits = nbits;
        Ksub = 1 << nbits;
        Dsub = d / m;
        CodeSize = (m * nbits + 7) / 8;
        Centroids = new float[(long)m * Ksub * Dsub];
    }

    /// <summary>Codebook memory in bytes (independent of how many vectors are encoded).</summary>
    public long CodebookBytes => (long)Centroids.Length * sizeof(float);

    /// <summary>Centroids of one sub-quantizer, <c>ksub * dsub</c> floats.</summary>
    public ReadOnlySpan<float> CentroidsFor(int subquantizer) =>
        Centroids.AsSpan(subquantizer * Ksub * Dsub, Ksub * Dsub);

    /// <summary>
    /// Trains one k-means codebook per subspace. The subspaces are independent, so they are trained
    /// in parallel; each is a small <c>dsub</c>-dimensional problem, which is exactly why PQ trains
    /// far faster than clustering the full space into <c>ksub^m</c> cells.
    /// </summary>
    public void Train(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));

        // Copy per-subspace slices up front so the parallel loop touches disjoint arrays.
        var slices = new float[M][];
        for (int m = 0; m < M; m++)
        {
            var slice = new float[(long)n * Dsub];
            for (int i = 0; i < n; i++)
                x.Slice(i * D + m * Dsub, Dsub).CopyTo(slice.AsSpan(i * Dsub, Dsub));
            slices[m] = slice;
        }

        Parallel.For(0, M, m =>
        {
            var kmeans = new Kmeans(Dsub, Ksub, new ClusteringParameters
            {
                Iterations = ClusteringParameters.Iterations,
                Redo = ClusteringParameters.Redo,
                MaxPointsPerCentroid = ClusteringParameters.MaxPointsPerCentroid,
                MinPointsPerCentroid = ClusteringParameters.MinPointsPerCentroid,
                Seed = ClusteringParameters.Seed + m,
                Tolerance = ClusteringParameters.Tolerance,
            });
            kmeans.Train(slices[m]);
            kmeans.Centroids.CopyTo(Centroids, (long)m * Ksub * Dsub);
            slices[m] = null!; // release the slice as soon as its codebook exists
        });

        IsTrained = true;
    }

    // ------------------------------------------------------------ Encoding

    /// <summary>Encodes one vector into <paramref name="code"/> (<see cref="CodeSize"/> bytes).</summary>
    public unsafe void ComputeCode(ReadOnlySpan<float> vector, Span<byte> code)
    {
        EnsureTrained();
        code[..CodeSize].Clear();
        fixed (float* pv = vector)
        fixed (float* pc = Centroids)
        {
            for (int m = 0; m < M; m++)
            {
                float* sub = pv + m * Dsub;
                float* book = pc + (long)m * Ksub * Dsub;
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int c = 0; c < Ksub; c++)
                {
                    float distance = VectorOps.L2Sqr(sub, book + (long)c * Dsub, Dsub);
                    if (distance < bestDistance) { bestDistance = distance; best = c; }
                }
                SetCode(code, m, (uint)best);
            }
        }
    }

    /// <summary>Encodes a batch; parallel across vectors.</summary>
    public byte[] ComputeCodes(ReadOnlySpan<float> x)
    {
        EnsureTrained();
        int n = x.Length / D;
        var codes = new byte[(long)n * CodeSize];
        ComputeCodes(x, n, codes);
        return codes;
    }

    /// <summary>Encodes a batch into a caller-supplied buffer.</summary>
    public unsafe void ComputeCodes(ReadOnlySpan<float> x, int n, Span<byte> codes)
    {
        EnsureTrained();
        if (n == 0) return;

        fixed (float* px = x)
        fixed (byte* pcodes = codes)
        {
            nint xp = (nint)px, cp = (nint)pcodes;
            int d = D, codeSize = CodeSize;
            if (n < 64)
            {
                for (int i = 0; i < n; i++)
                    ComputeCode(new ReadOnlySpan<float>((float*)xp + (long)i * d, d),
                                new Span<byte>((byte*)cp + (long)i * codeSize, codeSize));
            }
            else
            {
                Parallel.For(0, n, i =>
                    ComputeCode(new ReadOnlySpan<float>((float*)xp + (long)i * d, d),
                                new Span<byte>((byte*)cp + (long)i * codeSize, codeSize)));
            }
        }
    }

    // ------------------------------------------------------------ Decoding

    /// <summary>Reconstructs the approximation of one encoded vector.</summary>
    public void Decode(ReadOnlySpan<byte> code, Span<float> output)
    {
        EnsureTrained();
        for (int m = 0; m < M; m++)
        {
            uint c = GetCode(code, m);
            int offset = (int)((m * (long)Ksub + c) * Dsub);
            Centroids.AsSpan(offset, Dsub).CopyTo(output.Slice(m * Dsub, Dsub));
        }
    }

    /// <summary>Reconstructs a batch of encoded vectors.</summary>
    public void Decode(ReadOnlySpan<byte> codes, int n, Span<float> output)
    {
        for (int i = 0; i < n; i++)
            Decode(codes.Slice(i * CodeSize, CodeSize), output.Slice(i * D, D));
    }

    // ------------------------------------------------- Distance tables (ADC)

    /// <summary>Floats needed for one query's lookup table.</summary>
    public int DistanceTableSize => M * Ksub;

    /// <summary>
    /// Builds the per-query lookup table: entry <c>[m * ksub + c]</c> is the contribution of
    /// centroid <c>c</c> of subspace <c>m</c>. Cost is <c>ksub * d</c> regardless of database size,
    /// so it amortizes away as soon as more than a few hundred candidates are scanned.
    /// </summary>
    public unsafe void ComputeDistanceTable(ReadOnlySpan<float> query, Span<float> table, MetricType metric)
    {
        EnsureTrained();
        fixed (float* pq = query)
        fixed (float* pc = Centroids)
        fixed (float* pt = table)
        {
            for (int m = 0; m < M; m++)
            {
                float* sub = pq + m * Dsub;
                float* book = pc + (long)m * Ksub * Dsub;
                float* row = pt + (long)m * Ksub;
                if (metric == MetricType.InnerProduct)
                    for (int c = 0; c < Ksub; c++) row[c] = VectorOps.InnerProduct(sub, book + (long)c * Dsub, Dsub);
                else
                    for (int c = 0; c < Ksub; c++) row[c] = VectorOps.L2Sqr(sub, book + (long)c * Dsub, Dsub);
            }
        }
    }

    /// <summary>
    /// Distance between a query and one code, given that query's table. This is the inner loop of
    /// every PQ search: <see cref="M"/> dependent loads and adds, no floating-point multiplies.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe float DistanceFromTable(float* table, byte* code)
    {
        float sum = 0f;
        if (Nbits == 8)
        {
            // Byte-aligned fast path: unrolled by four to break the dependency chain on `sum`.
            int m = 0;
            float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f;
            int limit = M - 3;
            for (; m < limit; m += 4)
            {
                s0 += table[(long)(m + 0) * Ksub + code[m + 0]];
                s1 += table[(long)(m + 1) * Ksub + code[m + 1]];
                s2 += table[(long)(m + 2) * Ksub + code[m + 2]];
                s3 += table[(long)(m + 3) * Ksub + code[m + 3]];
            }
            sum = s0 + s1 + s2 + s3;
            for (; m < M; m++) sum += table[(long)m * Ksub + code[m]];
            return sum;
        }

        for (int m = 0; m < M; m++)
            sum += table[(long)m * Ksub + GetCode(new ReadOnlySpan<byte>(code, CodeSize), m)];
        return sum;
    }

    // -------------------------------------------------------- Code bit access

    /// <summary>Reads sub-code <paramref name="m"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetCode(ReadOnlySpan<byte> code, int m)
    {
        if (Nbits == 8) return code[m];
        int bit = m * Nbits;
        int index = bit >> 3;
        int shift = bit & 7;
        uint window = code[index];
        if (index + 1 < code.Length) window |= (uint)code[index + 1] << 8;
        if (index + 2 < code.Length) window |= (uint)code[index + 2] << 16;
        return (window >> shift) & (uint)(Ksub - 1);
    }

    /// <summary>Writes sub-code <paramref name="m"/>. The buffer must be zeroed first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCode(Span<byte> code, int m, uint value)
    {
        if (Nbits == 8) { code[m] = (byte)value; return; }
        int bit = m * Nbits;
        int index = bit >> 3;
        int shift = bit & 7;
        uint shifted = value << shift;
        code[index] |= (byte)shifted;
        if (index + 1 < code.Length) code[index + 1] |= (byte)(shifted >> 8);
        if (index + 2 < code.Length) code[index + 2] |= (byte)(shifted >> 16);
    }

    private void EnsureTrained()
    {
        if (!IsTrained)
            throw new InvalidOperationException("ProductQuantizer must be trained before encoding or searching.");
    }

    public override string ToString() =>
        $"PQ{M}x{Nbits} (d={D}, dsub={Dsub}, ksub={Ksub}, code={CodeSize}B, codebooks={CodebookBytes / 1024}KB)";

    // -------------------------------------------------------- Serialization

    public void Write(BinaryWriter writer)
    {
        writer.Write(D);
        writer.Write(M);
        writer.Write(Nbits);
        writer.Write(IsTrained);
        writer.Write(Centroids.Length);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Centroids.AsSpan()));
    }

    public static ProductQuantizer Read(BinaryReader reader)
    {
        int d = reader.ReadInt32();
        int m = reader.ReadInt32();
        int nbits = reader.ReadInt32();
        var pq = new ProductQuantizer(d, m, nbits) { IsTrained = reader.ReadBoolean() };
        int length = reader.ReadInt32();
        pq.Centroids = new float[length];
        reader.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pq.Centroids.AsSpan()));
        return pq;
    }
}
