using Faiss.Net.Core;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Local search quantizer (LSQ++, Martinez et al., ECCV 2018) — FAISS's
/// <c>faiss.LocalSearchQuantizer(d, M, nbits)</c>.
/// <para>
/// Where a <see cref="ResidualQuantizer"/> fixes its codebooks one stage at a time, LSQ treats all
/// <c>M</c> codebooks jointly. Training alternates two steps. With the codes fixed, the codebooks
/// that minimise the total reconstruction error are a linear least-squares problem,
/// <c>(BᵀB + λI) C = BᵀX</c> where <c>B</c> is the one-hot code matrix, solved exactly. With the
/// codebooks fixed, encoding one vector is a fully connected Markov random field over <c>M</c>
/// discrete variables — the cross terms <c>2⟨c_i, c_j⟩</c> couple every pair of codebooks — which is
/// minimised approximately by iterated conditional modes (ICM): re-choose one index at a time with
/// the others held, sweep, repeat.
/// </para>
/// <para>
/// ICM stops in local minima, so it is wrapped in iterated local search: perturb a few indices at
/// random, run ICM again, keep the result only if it lowers the error. During training the codebooks
/// are perturbed as well, with noise that anneals to zero (stochastic relaxation), which keeps early
/// iterations from locking in a poor solution. Every random choice comes from a seeded generator
/// (<see cref="RandomSeed"/>), and the per-vector streams are derived from the vector's position, so
/// training and encoding are reproducible bit for bit regardless of thread scheduling.
/// </para>
/// <para>
/// The codebook update factorises an <c>(M·2^nbits)²</c> matrix, which bounds the practical size:
/// <c>M = 16, nbits = 8</c> is a 4096² solve per training iteration.
/// </para>
/// </summary>
public sealed class LocalSearchQuantizer : AdditiveQuantizer
{
    /// <summary>Largest <c>M · 2^nbits</c> accepted; beyond it the codebook update's dense solve is impractical.</summary>
    public const int MaxCodewords = 8192;

    /// <summary>Alternations of codebook update and code refinement during training. FAISS <c>train_iters</c>.</summary>
    public int TrainIters { get; set; } = 25;

    /// <summary>Iterated-local-search rounds when encoding new vectors. FAISS <c>encode_ils_iters</c>.</summary>
    public int EncodeIlsIters { get; set; } = 16;

    /// <summary>Iterated-local-search rounds per training iteration. FAISS <c>train_ils_iters</c>.</summary>
    public int TrainIlsIters { get; set; } = 8;

    /// <summary>ICM sweeps over the <c>M</c> indices inside each local-search round. FAISS <c>icm_iters</c>.</summary>
    public int IcmIters { get; set; } = 4;

    /// <summary>Annealing exponent of the codebook perturbation, <c>T = (1 − t/T_max)^p</c>. FAISS <c>p</c>.</summary>
    public float P { get; set; } = 0.5f;

    /// <summary>Ridge regularisation of the codebook update; keeps unused codewords well defined. FAISS <c>lambd</c>.</summary>
    public float Lambd { get; set; } = 1e-2f;

    /// <summary>Indices re-drawn at random per local-search round, capped at <c>M</c>. FAISS <c>nperts</c>.</summary>
    public int Nperts { get; set; } = 4;

    /// <summary>Seed of every random choice in training and encoding. FAISS <c>random_seed</c>.</summary>
    public long RandomSeed { get; set; } = 0x12345;

    /// <param name="d">Vector dimension.</param>
    /// <param name="m">Number of codebooks.</param>
    /// <param name="nbits">Bits per codebook index; 8 is the default.</param>
    /// <param name="searchType">Whether codes carry a norm; see <see cref="ResidualQuantizer(int, int, int, AdditiveQuantizerSearchType)"/>.</param>
    public LocalSearchQuantizer(int d, int m, int nbits = 8,
                                AdditiveQuantizerSearchType searchType = AdditiveQuantizerSearchType.LutNonorm)
        : base(d, m, nbits, searchType)
    {
        if ((long)m * (1L << nbits) > MaxCodewords)
            throw new ArgumentException(
                $"LSQ{m}x{nbits} has {(long)m << nbits} codewords; the codebook update solves a dense system of that " +
                $"size, and more than {MaxCodewords} is impractical. Use fewer codebooks, fewer bits, or a ResidualQuantizer.");
    }

    protected override string ShortName => "LSQ";

    /// <summary>
    /// Trains as FAISS does: random initial codes, then <see cref="TrainIters"/> rounds of codebook
    /// update, annealed codebook perturbation, and local-search code refinement.
    /// </summary>
    public override void Train(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));
        var data = x[..(n * D)].ToArray();

        var rng = new RandomGenerator(RandomSeed);
        var codes = new int[(long)n * M];
        for (int i = 0; i < codes.Length; i++) codes[i] = rng.NextInt(Ksub);

        // Per-dimension standard deviation scales the codebook perturbation to the data.
        var stddev = new float[D];
        for (int j = 0; j < D; j++)
        {
            double mean = 0;
            for (int i = 0; i < n; i++) mean += data[i * D + j];
            mean /= n;
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double centred = data[i * D + j] - mean;
                sum += centred * centred;
            }
            stddev[j] = (float)Math.Sqrt(sum / n);
        }

        for (int iteration = 0; iteration < TrainIters; iteration++)
        {
            UpdateCodebooks(data, codes, n);

            float temperature = MathF.Pow(1f - (iteration + 1f) / TrainIters, P);
            PerturbCodebooks(temperature, stddev, rng);
            UpdateCodebookNorms();

            long roundSeed = ((long)rng.NextInt(int.MaxValue) << 31) ^ rng.NextInt(int.MaxValue);
            IcmEncode(data, n, codes, TrainIlsIters, roundSeed, freshStart: false);
        }

        UpdateCodebookNorms();
        IsTrained = true;
    }

    /// <summary>Beam-search starting codes, then <see cref="EncodeIlsIters"/> rounds of local search; see <see cref="BeamInit"/>.</summary>
    protected internal override void ComputeUnpackedCodes(ReadOnlySpan<float> x, int n, Span<int> codes)
    {
        EnsureTrained();
        var data = x[..(n * D)].ToArray();
        var output = new int[(long)n * M];
        IcmEncode(data, n, output, EncodeIlsIters, RandomSeed, freshStart: true);
        output.AsSpan(0, n * M).CopyTo(codes);
    }

    /// <summary>
    /// Least-squares codebooks for fixed codes: <c>(BᵀB + λI) C = BᵀX</c>. <c>BᵀB</c> counts how often
    /// each pair of codewords is used together, <c>BᵀX</c> sums the vectors that use each codeword;
    /// both are accumulated directly from the codes without forming <c>B</c>.
    /// </summary>
    private void UpdateCodebooks(float[] x, int[] codes, int n)
    {
        int size = M * Ksub;
        var btb = new double[(long)size * size];
        var btx = new double[(long)size * D];

        for (int i = 0; i < n; i++)
        {
            var row = codes.AsSpan(i * M, M);
            var vector = x.AsSpan(i * D, D);
            for (int m = 0; m < M; m++)
            {
                int r = m * Ksub + row[m];
                var target = btx.AsSpan(r * D, D);
                for (int j = 0; j < D; j++) target[j] += vector[j];
                long rowOffset = (long)r * size;
                for (int m2 = 0; m2 < M; m2++) btb[rowOffset + m2 * Ksub + row[m2]] += 1.0;
            }
        }
        for (int r = 0; r < size; r++) btb[(long)r * size + r] += Lambd;

        MatrixOps.CholeskySolve(btb, size, btx, D);
        for (long i = 0; i < Codebooks.Length; i++) Codebooks[i] = (float)btx[i];
    }

    /// <summary>Adds annealed Gaussian noise, <c>T · N(0, σ_j) / M</c> per dimension (stochastic relaxation).</summary>
    private void PerturbCodebooks(float temperature, float[] stddev, RandomGenerator rng)
    {
        if (temperature <= 0f) return;
        int entries = M * Ksub;
        for (int e = 0; e < entries; e++)
        {
            var codeword = Codebooks.AsSpan(e * D, D);
            for (int j = 0; j < D; j++) codeword[j] += temperature * stddev[j] * rng.NextGaussian() / M;
        }
    }

    /// <summary>
    /// Pairwise codeword terms <c>2⟨c_{m,k}, c_{m2,k2}⟩</c>, laid out <c>[m][m2][k2][k]</c> so that the
    /// contribution of a fixed neighbour index to every candidate <c>k</c> is one contiguous row —
    /// the ICM step then adds whole rows instead of gathering.
    /// </summary>
    private unsafe float[] ComputeBinaryTerms()
    {
        int k = Ksub;
        var binaries = new float[(long)M * M * k * k];
        Parallel.For(0, M * M, pair =>
        {
            int m = pair / M, m2 = pair % M;
            if (m == m2) return;
            fixed (float* books = Codebooks)
            fixed (float* pb = binaries)
            {
                float* block = pb + (long)pair * k * k;
                float* bookM = books + (long)m * k * D;
                float* bookM2 = books + (long)m2 * k * D;
                for (int k2 = 0; k2 < k; k2++)
                    for (int c = 0; c < k; c++)
                        block[(long)k2 * k + c] = 2f * VectorOps.InnerProduct(bookM + (long)c * D, bookM2 + (long)k2 * D, D);
            }
        });
        return binaries;
    }

    /// <summary>
    /// Iterated local search over every vector, parallel across vectors. Each vector draws from its
    /// own generator seeded by <paramref name="seed"/> and its position, which is what makes the
    /// result independent of scheduling.
    /// </summary>
    private void IcmEncode(float[] x, int n, int[] codes, int ilsIters, long seed, bool freshStart)
    {
        var binaries = ComputeBinaryTerms();
        int nperts = Math.Min(Math.Max(0, Nperts), M);

        Parallel.For(0, n, () => new IcmScratch(M, Ksub, D), (i, _, scratch) =>
        {
            EncodeOne(x.AsSpan(i * D, D), codes.AsSpan(i * M, M), binaries, ilsIters, nperts,
                      // Consecutive seeds are fine: the generator's SplitMix expansion decorrelates them.
                      new RandomGenerator(seed + i), freshStart, scratch);
            return scratch;
        }, _ => { });
    }

    /// <summary>Width of the beam search that produces starting codes; see <see cref="BeamInit"/>.</summary>
    private const int InitBeamSize = 5;

    private sealed class IcmScratch(int m, int k, int d)
    {
        public readonly float[] Unaries = new float[m * k];
        public readonly float[] Objective = new float[k];
        public readonly int[] Best = new int[m];
        public readonly int[] Start = new int[m];
        public readonly float[] Reconstruction = new float[d];
        public readonly int[] BeamA = new int[InitBeamSize * m];
        public readonly int[] BeamB = new int[InitBeamSize * m];
        public readonly float[] EnergyA = new float[InitBeamSize];
        public readonly float[] EnergyB = new float[InitBeamSize];
        public readonly float[] HeapScores = new float[InitBeamSize];
        public readonly long[] HeapIds = new long[InitBeamSize];
    }

    private unsafe void EncodeOne(
        ReadOnlySpan<float> vector, Span<int> codes, float[] binaries, int ilsIters, int nperts,
        RandomGenerator rng, bool freshStart, IcmScratch scratch)
    {
        int k = Ksub;

        // Unary terms ‖c_mk‖² − 2⟨x, c_mk⟩: the part of the error each codeword contributes alone.
        var unaries = scratch.Unaries;
        fixed (float* pv = vector)
        fixed (float* books = Codebooks)
        {
            for (int e = 0; e < M * k; e++)
                unaries[e] = CodebookNorms[e] - 2f * VectorOps.InnerProduct(pv, books + (long)e * D, D);
        }

        // A new vector starts from the beam-search code; a training vector keeps its current code.
        if (freshStart)
        {
            BeamInit(scratch.Start, unaries, binaries, scratch);
            scratch.Start.AsSpan().CopyTo(codes);
        }

        codes.CopyTo(scratch.Best);
        float bestError = ReconstructionError(vector, codes, scratch.Reconstruction);

        for (int round = 0; round < ilsIters; round++)
        {
            for (int p = 0; p < nperts; p++) codes[rng.NextInt(M)] = rng.NextInt(k);
            IcmSweeps(codes, unaries, binaries, scratch.Objective);

            float error = ReconstructionError(vector, codes, scratch.Reconstruction);
            if (error < bestError)
            {
                bestError = error;
                codes.CopyTo(scratch.Best);
            }
        }
        scratch.Best.AsSpan().CopyTo(codes);
    }

    /// <summary>
    /// Starting codes from a beam search over the codebooks in order: each partial code is extended
    /// by every codeword of the next codebook, scored by the exact error increment (its unary term
    /// plus its pairwise terms with the indices already chosen), and the <see cref="InitBeamSize"/>
    /// best are kept — residual-quantizer encoding, expressed in the terms ICM uses.
    /// <para>
    /// This is a deliberate departure from FAISS, which starts encoding from uniformly random codes.
    /// Escaping a wrong combination of codewords usually needs several indices to change together,
    /// which neither a single-index ICM move nor a few random perturbations achieve, so from a random
    /// start a fraction of vectors end at cluster-scale error. On the clustered test data
    /// (<c>d = 32</c>, LSQ8x6) the random start gave a mean squared error of 0.228 — worse than PQ at
    /// the same size — and this start gives 0.059. A small tail remains (about 0.3% of vectors still
    /// above 10x the median error), which comes from the trained codebooks rather than the start.
    /// Training keeps FAISS's behaviour: codes carry over from one iteration to the next.
    /// </para>
    /// </summary>
    private unsafe void BeamInit(Span<int> output, float[] unaries, float[] binaries, IcmScratch scratch)
    {
        int k = Ksub;
        int[] beamIn = scratch.BeamA, beamOut = scratch.BeamB;
        float[] energyIn = scratch.EnergyA, energyOut = scratch.EnergyB;
        int beam = 1;
        energyIn[0] = 0f;

        fixed (float* pu = unaries)
        fixed (float* pb = binaries)
        fixed (float* po = scratch.Objective)
        {
            for (int m = 0; m < M; m++)
            {
                int newBeam = (int)Math.Min((long)beam * k, InitBeamSize);
                var heap = new KnnHeap<AscendingOrder>(scratch.HeapScores.AsSpan(0, newBeam), scratch.HeapIds.AsSpan(0, newBeam));
                for (int b = 0; b < beam; b++)
                {
                    Buffer.MemoryCopy(pu + (long)m * k, po, (long)k * sizeof(float), (long)k * sizeof(float));
                    for (int m2 = 0; m2 < m; m2++)
                        VectorOps.Add(po, pb + (((long)m * M + m2) * k + beamIn[b * M + m2]) * k, k);
                    float baseEnergy = energyIn[b];
                    for (int c = 0; c < k; c++)
                    {
                        float energy = baseEnergy + po[c];
                        if (AscendingOrder.Better(energy, heap.WorstScore)) heap.Push(energy, (long)b * k + c);
                    }
                }
                heap.Finish();

                for (int j = 0; j < newBeam; j++)
                {
                    long id = scratch.HeapIds[j];
                    int b = (int)(id / k), c = (int)(id % k);
                    beamIn.AsSpan(b * M, m).CopyTo(beamOut.AsSpan(j * M, m));
                    beamOut[j * M + m] = c;
                    energyOut[j] = scratch.HeapScores[j];
                }
                (beamIn, beamOut) = (beamOut, beamIn);
                (energyIn, energyOut) = (energyOut, energyIn);
                beam = newBeam;
            }
        }
        beamIn.AsSpan(0, M).CopyTo(output);
    }

    /// <summary>
    /// <see cref="IcmIters"/> sweeps of iterated conditional modes: each index in turn is set to the
    /// codeword minimising its unary term plus its pairwise terms with the other, fixed, indices.
    /// </summary>
    private unsafe void IcmSweeps(Span<int> codes, float[] unaries, float[] binaries, float[] objective)
    {
        int k = Ksub;
        fixed (float* pu = unaries)
        fixed (float* pb = binaries)
        fixed (float* po = objective)
        {
            for (int sweep = 0; sweep < IcmIters; sweep++)
            {
                for (int m = 0; m < M; m++)
                {
                    Buffer.MemoryCopy(pu + (long)m * k, po, (long)k * sizeof(float), (long)k * sizeof(float));
                    for (int m2 = 0; m2 < M; m2++)
                    {
                        if (m2 == m) continue;
                        VectorOps.Add(po, pb + (((long)m * M + m2) * k + codes[m2]) * k, k);
                    }

                    int best = 0;
                    float bestValue = po[0];
                    for (int c = 1; c < k; c++)
                        if (po[c] < bestValue) { bestValue = po[c]; best = c; }
                    codes[m] = best;
                }
            }
        }
    }

    private float ReconstructionError(ReadOnlySpan<float> vector, ReadOnlySpan<int> codes, float[] reconstruction)
    {
        DecodeUnpacked(codes, reconstruction);
        return VectorOps.L2Sqr(vector, reconstruction);
    }

    // -------------------------------------------------------- Serialization

    protected override void WriteParameters(BinaryWriter writer)
    {
        writer.Write(TrainIters);
        writer.Write(EncodeIlsIters);
        writer.Write(TrainIlsIters);
        writer.Write(IcmIters);
        writer.Write(P);
        writer.Write(Lambd);
        writer.Write(Nperts);
        writer.Write(RandomSeed);
    }

    protected override void ReadParameters(BinaryReader reader)
    {
        TrainIters = reader.ReadInt32();
        EncodeIlsIters = reader.ReadInt32();
        TrainIlsIters = reader.ReadInt32();
        IcmIters = reader.ReadInt32();
        P = reader.ReadSingle();
        Lambd = reader.ReadSingle();
        Nperts = reader.ReadInt32();
        RandomSeed = reader.ReadInt64();
    }

    /// <summary>Reads a quantizer written by <see cref="AdditiveQuantizer.Write"/>.</summary>
    public static LocalSearchQuantizer Read(BinaryReader reader) =>
        ReadCommon(reader, (d, m, nbits, searchType) => new LocalSearchQuantizer(d, m, nbits, searchType));
}
