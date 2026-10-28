using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Faiss.Net.Core;

namespace Faiss.Net;

/// <summary>
/// How an additive code is scored at search time — FAISS's <c>AdditiveQuantizer::Search_type_t</c>.
/// The numeric values match the FAISS enum so the two read alike.
/// </summary>
public enum AdditiveQuantizerSearchType
{
    /// <summary>
    /// <c>ST_LUT_nonorm</c>: codes only. The score is the inner product read from the lookup table,
    /// which is exact for <see cref="MetricType.InnerProduct"/> but has no way to supply the
    /// <c>‖x̂‖²</c> term an L2 distance needs.
    /// </summary>
    LutNonorm = 1,

    /// <summary>
    /// <c>ST_norm_float</c>: each code carries the squared norm of its reconstruction as a 4-byte
    /// float, so an L2 distance is <c>‖q‖² − 2⟨q, x̂⟩ + ‖x̂‖²</c> with the middle term from the
    /// lookup table.
    /// </summary>
    NormFloat = 3,
}

/// <summary>
/// Base of the additive quantizers (<see cref="ResidualQuantizer"/>, <see cref="LocalSearchQuantizer"/>):
/// a vector is approximated by the <em>sum</em> of <c>M</c> codewords, one from each of <c>M</c>
/// codebooks, every codeword spanning the full dimension.
/// <para>
/// That is the difference from a <see cref="ProductQuantizer"/>, whose codewords each cover a
/// disjoint slice of the dimensions. A PQ code can only describe correlations inside a slice; an
/// additive code can spend every codebook on whatever direction still carries error, which is why it
/// reaches a markedly lower reconstruction error for the same number of bits. The price is encoding:
/// choosing the best combination of <c>M</c> full-dimension codewords is a combinatorial problem
/// with no independent per-slice answer, so each subclass is defined by how it searches for one.
/// </para>
/// <para>
/// Search stays as cheap as PQ's asymmetric distance: since <c>⟨q, Σ c_m⟩ = Σ ⟨q, c_m⟩</c>, one
/// <c>M × 2^nbits</c> table of inner products per query scores every code with <c>M</c> lookups.
/// The only extra for L2 is the reconstruction norm, stored next to the code
/// (<see cref="AdditiveQuantizerSearchType.NormFloat"/>).
/// </para>
/// </summary>
public abstract class AdditiveQuantizer
{
    /// <summary>Input vector dimension.</summary>
    public int D { get; private set; }

    /// <summary>Number of codebooks, each contributing one codeword to a reconstruction.</summary>
    public int M { get; private set; }

    /// <summary>Bits per codebook index.</summary>
    public int Nbits { get; private set; }

    /// <summary>Codewords per codebook, <c>2^nbits</c> (FAISS's <c>K</c>).</summary>
    public int Ksub { get; private set; }

    /// <summary>How codes are scored; fixes whether a norm is stored with each code.</summary>
    public AdditiveQuantizerSearchType SearchType { get; private set; }

    /// <summary>Bytes taken by the packed codebook indices, <c>⌈M · nbits / 8⌉</c>.</summary>
    public int CodeBytes { get; private set; }

    /// <summary>Bytes taken by the stored norm: 4 for <see cref="AdditiveQuantizerSearchType.NormFloat"/>, else 0.</summary>
    public int NormBytes { get; private set; }

    /// <summary>Bytes per encoded vector, indices plus norm — FAISS's <c>code_size</c>.</summary>
    public int CodeSize => CodeBytes + NormBytes;

    /// <summary>Codebooks laid out as <c>[M][ksub][d]</c>.</summary>
    public float[] Codebooks { get; protected set; }

    /// <summary>Squared norm of every codeword, <c>[M][ksub]</c>; refreshed whenever the codebooks change.</summary>
    public float[] CodebookNorms { get; private set; }

    /// <summary>Set once <see cref="Train"/> has completed.</summary>
    public bool IsTrained { get; protected set; }

    protected AdditiveQuantizer(int d, int m, int nbits, AdditiveQuantizerSearchType searchType)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d));
        if (m <= 0) throw new ArgumentOutOfRangeException(nameof(m));
        if (nbits is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(nbits), "nbits must be between 1 and 16.");
        if (!Enum.IsDefined(searchType)) throw new ArgumentOutOfRangeException(nameof(searchType));

        D = d;
        M = m;
        Nbits = nbits;
        Ksub = 1 << nbits;
        SearchType = searchType;
        CodeBytes = (m * nbits + 7) / 8;
        NormBytes = searchType == AdditiveQuantizerSearchType.NormFloat ? sizeof(float) : 0;
        Codebooks = new float[(long)m * Ksub * d];
        CodebookNorms = new float[m * Ksub];
    }

    /// <summary>Codebook memory in bytes (independent of how many vectors are encoded).</summary>
    public long CodebookBytes => (long)Codebooks.Length * sizeof(float);

    /// <summary>The <c>ksub * d</c> codewords of one codebook.</summary>
    public ReadOnlySpan<float> CodebookFor(int m) => Codebooks.AsSpan(m * Ksub * D, Ksub * D);

    /// <summary>Learns the codebooks from <c>n * d</c> training vectors.</summary>
    public abstract void Train(ReadOnlySpan<float> x);

    /// <summary>
    /// Chooses codebook indices for <paramref name="n"/> vectors, <c>M</c> per vector in
    /// <paramref name="codes"/> — the part that distinguishes one additive quantizer from another.
    /// </summary>
    protected internal abstract void ComputeUnpackedCodes(ReadOnlySpan<float> x, int n, Span<int> codes);

    /// <summary>Recomputes <see cref="CodebookNorms"/>; subclasses call it after every codebook change.</summary>
    protected unsafe void UpdateCodebookNorms()
    {
        fixed (float* books = Codebooks)
        fixed (float* norms = CodebookNorms)
            VectorOps.ComputeNorms(books, M * Ksub, D, norms);
    }

    // ------------------------------------------------------------ Encoding

    /// <summary>Encodes a batch into a new array of <c>n * CodeSize</c> bytes.</summary>
    public byte[] ComputeCodes(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        var codes = new byte[(long)n * CodeSize];
        ComputeCodes(x, n, codes);
        return codes;
    }

    /// <summary>Encodes a batch into a caller-supplied buffer.</summary>
    public void ComputeCodes(ReadOnlySpan<float> x, int n, Span<byte> codes) =>
        ComputeCodes(x, n, codes, default);

    /// <summary>
    /// Encodes a batch whose vectors are residuals from known <paramref name="centroids"/> (one
    /// <c>d</c>-row per vector) — FAISS's <c>compute_codes_add_centroids</c>.
    /// <para>
    /// Only the stored norm changes: it becomes <c>‖c + r̂‖² − ‖c‖²</c> rather than <c>‖r̂‖²</c>. An
    /// inverted file can then score <c>‖q − c − r̂‖²</c> as the coarse distance <c>‖q − c‖²</c> it
    /// already has, minus <c>2⟨q, r̂⟩</c> from a lookup table built once per query, plus the stored
    /// term — no per-cell table rebuild and no per-cell <c>‖c‖²</c>.
    /// </para>
    /// </summary>
    public unsafe void ComputeCodes(ReadOnlySpan<float> x, int n, Span<byte> codes, ReadOnlySpan<float> centroids)
    {
        EnsureTrained();
        if (n == 0) return;
        if (x.Length < (long)n * D) throw new ArgumentException($"Expected {n * D} floats.", nameof(x));
        if (codes.Length < (long)n * CodeSize) throw new ArgumentException($"Expected {n * CodeSize} code bytes.", nameof(codes));
        bool hasCentroids = !centroids.IsEmpty;
        if (hasCentroids && centroids.Length < (long)n * D)
            throw new ArgumentException($"Expected {n * D} centroid floats.", nameof(centroids));

        var unpacked = new int[(long)n * M];
        ComputeUnpackedCodes(x, n, unpacked);

        fixed (byte* pcodes = codes)
        fixed (float* pcent = centroids)
        {
            nint cp = (nint)pcodes, centp = (nint)pcent;
            void Pack(int i, float[] scratch)
            {
                var unpackedRow = unpacked.AsSpan(i * M, M);
                var code = new Span<byte>((byte*)cp + (long)i * CodeSize, CodeSize);
                code.Clear();
                for (int m = 0; m < M; m++) SetCode(code, m, (uint)unpackedRow[m]);
                if (NormBytes == 0) return;

                DecodeUnpacked(unpackedRow, scratch);
                fixed (float* recon = scratch)
                {
                    float norm = VectorOps.Norm2Sqr(recon, D);
                    if (hasCentroids)
                        norm += 2f * VectorOps.InnerProduct((float*)centp + (long)i * D, recon, D);
                    WriteNorm(code, norm);
                }
            }

            if (n < 256)
            {
                var scratch = new float[D];
                for (int i = 0; i < n; i++) Pack(i, scratch);
            }
            else
            {
                Parallel.For(0, n, () => new float[D], (i, _, scratch) => { Pack(i, scratch); return scratch; }, _ => { });
            }
        }
    }

    // ------------------------------------------------------------ Decoding

    /// <summary>Reconstruction from unpacked indices: the sum of the chosen codewords.</summary>
    public unsafe void DecodeUnpacked(ReadOnlySpan<int> codes, Span<float> output)
    {
        output[..D].Clear();
        fixed (float* books = Codebooks)
        fixed (float* po = output)
        {
            for (int m = 0; m < M; m++)
                VectorOps.Add(po, books + ((long)m * Ksub + codes[m]) * D, D);
        }
    }

    /// <summary>Reconstructs the approximation of one encoded vector.</summary>
    public unsafe void Decode(ReadOnlySpan<byte> code, Span<float> output)
    {
        EnsureTrained();
        output[..D].Clear();
        fixed (float* books = Codebooks)
        fixed (float* po = output)
        {
            for (int m = 0; m < M; m++)
                VectorOps.Add(po, books + ((long)m * Ksub + GetCode(code, m)) * D, D);
        }
    }

    /// <summary>Reconstructs a batch of encoded vectors.</summary>
    public void Decode(ReadOnlySpan<byte> codes, int n, Span<float> output)
    {
        for (int i = 0; i < n; i++)
            Decode(codes.Slice(i * CodeSize, CodeSize), output.Slice(i * D, D));
    }

    /// <summary>The norm term stored with a code; 0 when <see cref="NormBytes"/> is 0.</summary>
    public float DecodeNorm(ReadOnlySpan<byte> code) =>
        NormBytes == 0 ? 0f : MemoryMarshal.Read<float>(code.Slice(CodeBytes, sizeof(float)));

    private void WriteNorm(Span<byte> code, float norm) =>
        MemoryMarshal.Write(code.Slice(CodeBytes, sizeof(float)), in norm);

    // ------------------------------------------------- Lookup tables (search)

    /// <summary>Floats in one query's lookup table, <c>M * ksub</c>.</summary>
    public int LookupTableSize => M * Ksub;

    /// <summary>
    /// Builds a query's table of inner products with every codeword — FAISS's <c>compute_LUT</c>.
    /// Entry <c>[m * ksub + k]</c> is <c>⟨q, c_mk⟩</c>; the inner product with any reconstruction is
    /// then the sum of <c>M</c> entries. Cost is <c>M * ksub * d</c>, independent of the database.
    /// </summary>
    public unsafe void ComputeLookupTable(ReadOnlySpan<float> query, Span<float> table)
    {
        EnsureTrained();
        if (table.Length < LookupTableSize) throw new ArgumentException($"Table needs {LookupTableSize} floats.", nameof(table));
        fixed (float* pq = query)
        fixed (float* books = Codebooks)
        fixed (float* pt = table)
        {
            int entries = M * Ksub;
            for (int e = 0; e < entries; e++) pt[e] = VectorOps.InnerProduct(pq, books + (long)e * D, D);
        }
    }

    /// <summary>
    /// Scores <c>scores.Length</c> consecutive codes against a query's lookup table, the inner loop of
    /// every additive-code search. For L2 the score is <c>bias − 2⟨q, x̂⟩ + norm</c>, with
    /// <paramref name="bias"/> the query norm for a flat index or the coarse distance for an inverted
    /// file; for inner product it is the table sum alone.
    /// <para>The metric branch is taken once per call, not per code.</para>
    /// </summary>
    /// <param name="l2">Score as an L2 distance; requires <see cref="AdditiveQuantizerSearchType.NormFloat"/>.</param>
    public unsafe void ScoreCodes(ReadOnlySpan<float> table, ReadOnlySpan<byte> codes, Span<float> scores, float bias, bool l2)
    {
        if (l2 && NormBytes == 0)
            throw new InvalidOperationException("L2 scoring needs a stored norm; build the quantizer with AdditiveQuantizerSearchType.NormFloat.");
        int count = scores.Length;
        int codeSize = CodeSize;
        fixed (float* pt = table)
        fixed (byte* pc = codes)
        fixed (float* ps = scores)
        {
            if (l2)
            {
                for (int i = 0; i < count; i++)
                {
                    byte* code = pc + (long)i * codeSize;
                    ps[i] = bias - 2f * InnerProductFromTable(pt, code) + Unsafe.ReadUnaligned<float>(code + CodeBytes);
                }
            }
            else
            {
                for (int i = 0; i < count; i++) ps[i] = InnerProductFromTable(pt, pc + (long)i * codeSize);
            }
        }
    }

    /// <summary><c>⟨q, x̂⟩</c> for one code: <c>M</c> table lookups and adds.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe float InnerProductFromTable(float* table, byte* code)
    {
        if (Nbits == 8)
        {
            // Byte-aligned fast path, unrolled by four to break the dependency chain on the sum.
            int m = 0;
            float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f;
            for (; m < M - 3; m += 4)
            {
                s0 += table[(m + 0) * 256 + code[m + 0]];
                s1 += table[(m + 1) * 256 + code[m + 1]];
                s2 += table[(m + 2) * 256 + code[m + 2]];
                s3 += table[(m + 3) * 256 + code[m + 3]];
            }
            float sum = s0 + s1 + s2 + s3;
            for (; m < M; m++) sum += table[m * 256 + code[m]];
            return sum;
        }

        var span = new ReadOnlySpan<byte>(code, CodeBytes);
        float total = 0f;
        for (int m = 0; m < M; m++) total += table[m * Ksub + (int)GetCode(span, m)];
        return total;
    }

    // -------------------------------------------------------- Code bit access

    /// <summary>Reads codebook index <paramref name="m"/> from a packed code (LSB-first, like PQ).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint GetCode(ReadOnlySpan<byte> code, int m)
    {
        if (Nbits == 8) return code[m];
        int bit = m * Nbits;
        int index = bit >> 3;
        int shift = bit & 7;
        int limit = CodeBytes;
        uint window = code[index];
        if (index + 1 < limit) window |= (uint)code[index + 1] << 8;
        if (index + 2 < limit) window |= (uint)code[index + 2] << 16;
        return (window >> shift) & (uint)(Ksub - 1);
    }

    /// <summary>Writes codebook index <paramref name="m"/>. The index bytes must be zeroed first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetCode(Span<byte> code, int m, uint value)
    {
        if (Nbits == 8) { code[m] = (byte)value; return; }
        int bit = m * Nbits;
        int index = bit >> 3;
        int shift = bit & 7;
        int limit = CodeBytes;
        uint shifted = value << shift;
        code[index] |= (byte)shifted;
        if (index + 1 < limit) code[index + 1] |= (byte)(shifted >> 8);
        if (index + 2 < limit) code[index + 2] |= (byte)(shifted >> 16);
    }

    protected void EnsureTrained()
    {
        if (!IsTrained)
            throw new InvalidOperationException($"{GetType().Name} must be trained before encoding or searching.");
    }

    /// <summary>Short type tag used by <see cref="ToString"/>, e.g. <c>RQ</c>.</summary>
    protected abstract string ShortName { get; }

    public override string ToString() =>
        $"{ShortName}{M}x{Nbits} (d={D}, ksub={Ksub}, code={CodeSize}B" +
        $"{(NormBytes > 0 ? " incl. norm" : "")}, codebooks={CodebookBytes / 1024}KB)";

    // -------------------------------------------------------- Serialization

    /// <summary>Writes the shared state, then <see cref="WriteParameters"/>.</summary>
    public void Write(BinaryWriter writer)
    {
        writer.Write(D);
        writer.Write(M);
        writer.Write(Nbits);
        writer.Write((int)SearchType);
        writer.Write(IsTrained);
        writer.Write(Codebooks.Length);
        writer.Write(MemoryMarshal.AsBytes(Codebooks.AsSpan()));
        WriteParameters(writer);
    }

    /// <summary>Subclass tuning parameters, written after the codebooks.</summary>
    protected abstract void WriteParameters(BinaryWriter writer);

    /// <summary>Reads the parameters written by <see cref="WriteParameters"/>.</summary>
    protected abstract void ReadParameters(BinaryReader reader);

    /// <summary>Shared deserialization: header, construction via <paramref name="create"/>, codebooks, parameters.</summary>
    protected static T ReadCommon<T>(BinaryReader reader, Func<int, int, int, AdditiveQuantizerSearchType, T> create)
        where T : AdditiveQuantizer
    {
        int d = reader.ReadInt32();
        int m = reader.ReadInt32();
        int nbits = reader.ReadInt32();
        var searchType = (AdditiveQuantizerSearchType)reader.ReadInt32();
        var quantizer = create(d, m, nbits, searchType);
        quantizer.IsTrained = reader.ReadBoolean();
        int length = reader.ReadInt32();
        if (length != quantizer.Codebooks.Length)
            throw new InvalidDataException($"Codebook length {length} does not match M={m}, nbits={nbits}, d={d}.");
        reader.ReadExactly(MemoryMarshal.AsBytes(quantizer.Codebooks.AsSpan()));
        quantizer.ReadParameters(reader);
        quantizer.UpdateCodebookNorms();
        return quantizer;
    }
}
