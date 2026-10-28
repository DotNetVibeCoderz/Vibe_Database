using Faiss.Net.Core;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Residual quantizer (RQ): <c>M</c> codebooks applied in sequence, each quantizing what the
/// previous ones left over — FAISS's <c>faiss.ResidualQuantizer(d, M, nbits)</c>.
/// <para>
/// Stage <c>m</c>'s codebook is k-means on the residuals after stages <c>0..m-1</c>, so the first
/// codebook captures the coarse structure (cluster centres, say) and every later one refines the
/// error that is left. Because each codeword spans the full dimension, no bits are spent describing
/// the same coarse structure once per slice the way a <see cref="ProductQuantizer"/> must.
/// </para>
/// <para>
/// Greedy encoding — nearest codeword at each stage — is fast but myopic: the best first choice can
/// leave a residual later stages describe badly. Encoding therefore keeps a beam of the
/// <see cref="MaxBeamSize"/> best partial codes at every stage and expands each by every codeword
/// of the next, and training runs the same beam search to produce the residuals each stage is
/// trained on, so the codebooks fit the codes the encoder will actually choose. Cost per vector is
/// <c>M * beam * 2^nbits * d</c>.
/// </para>
/// </summary>
public sealed class ResidualQuantizer : AdditiveQuantizer
{
    /// <summary>
    /// Partial codes kept per stage during encoding and training. FAISS's <c>max_beam_size</c>,
    /// default 5; 1 is greedy encoding, larger values lower the error at proportional cost.
    /// </summary>
    public int MaxBeamSize { get; set; } = 5;

    /// <summary>Clustering settings for each stage's k-means. The stage index is added to the seed.</summary>
    public ClusteringParameters ClusteringParameters { get; set; } = new() { Iterations = 25 };

    /// <param name="d">Vector dimension.</param>
    /// <param name="m">Number of stages (codebooks).</param>
    /// <param name="nbits">Bits per stage; 8 is the default and the byte-aligned fast path.</param>
    /// <param name="searchType">
    /// Whether codes carry a norm. The default stores codes only, the FAISS code size for the bare
    /// quantizer; the L2 indexes build theirs with <see cref="AdditiveQuantizerSearchType.NormFloat"/>.
    /// </param>
    public ResidualQuantizer(int d, int m, int nbits = 8,
                             AdditiveQuantizerSearchType searchType = AdditiveQuantizerSearchType.LutNonorm)
        : base(d, m, nbits, searchType)
    {
    }

    protected override string ShortName => "RQ";

    /// <summary>
    /// Trains the codebooks stage by stage. Each stage clusters the current beam residuals of every
    /// training vector, then advances the beam with the new codebook, so stage <c>m + 1</c> is fitted
    /// to the residuals beam-search encoding really leaves after stage <c>m</c>.
    /// </summary>
    public override unsafe void Train(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));
        int maxBeam = Math.Max(1, MaxBeamSize);

        int beam = 1;
        var residuals = x[..(n * D)].ToArray();
        var codes = Array.Empty<int>();

        for (int stage = 0; stage < M; stage++)
        {
            var kmeans = new Kmeans(D, Ksub, new ClusteringParameters
            {
                Iterations = ClusteringParameters.Iterations,
                Redo = ClusteringParameters.Redo,
                MaxPointsPerCentroid = ClusteringParameters.MaxPointsPerCentroid,
                MinPointsPerCentroid = ClusteringParameters.MinPointsPerCentroid,
                Seed = ClusteringParameters.Seed + stage,
                Verbose = ClusteringParameters.Verbose,
                Tolerance = ClusteringParameters.Tolerance,
            });
            kmeans.Train(residuals);
            kmeans.Centroids.CopyTo(Codebooks, (long)stage * Ksub * D);

            // The last stage's residuals are not needed: training ends with its codebook.
            if (stage == M - 1) break;

            int newBeam = (int)Math.Min((long)beam * Ksub, maxBeam);
            var newResiduals = new float[(long)n * newBeam * D];
            var newCodes = new int[(long)n * newBeam * (stage + 1)];
            int currentBeam = beam, currentStage = stage;
            float[] currentResiduals = residuals;
            int[] currentCodes = codes;

            Parallel.For(0, n, () => new StepScratch(newBeam), (i, _, scratch) =>
            {
                BeamStep(
                    currentResiduals.AsSpan(i * currentBeam * D, currentBeam * D),
                    currentCodes.AsSpan(i * currentBeam * currentStage, currentBeam * currentStage),
                    currentBeam, currentStage, newBeam,
                    newResiduals.AsSpan(i * newBeam * D, newBeam * D),
                    newCodes.AsSpan(i * newBeam * (currentStage + 1), newBeam * (currentStage + 1)),
                    scratch);
                return scratch;
            }, _ => { });

            residuals = newResiduals;
            codes = newCodes;
            beam = newBeam;
        }

        UpdateCodebookNorms();
        IsTrained = true;
    }

    /// <summary>Beam-search encoding, parallel across vectors.</summary>
    protected internal override void ComputeUnpackedCodes(ReadOnlySpan<float> x, int n, Span<int> codes)
    {
        EnsureTrained();
        int maxBeam = Math.Max(1, MaxBeamSize);
        var input = x[..(n * D)].ToArray();
        var output = new int[(long)n * M];

        void Encode(int i, EncodeScratch scratch)
        {
            // Two buffers per quantity, swapped after each stage.
            input.AsSpan(i * D, D).CopyTo(scratch.ResidualsA);
            float[] residualsIn = scratch.ResidualsA, residualsOut = scratch.ResidualsB;
            int[] codesIn = scratch.CodesA, codesOut = scratch.CodesB;
            int beam = 1;
            for (int stage = 0; stage < M; stage++)
            {
                int newBeam = (int)Math.Min((long)beam * Ksub, maxBeam);
                BeamStep(residualsIn.AsSpan(0, beam * D), codesIn.AsSpan(0, beam * stage), beam, stage, newBeam,
                         residualsOut.AsSpan(0, newBeam * D), codesOut.AsSpan(0, newBeam * (stage + 1)), scratch.Step);
                (residualsIn, residualsOut) = (residualsOut, residualsIn);
                (codesIn, codesOut) = (codesOut, codesIn);
                beam = newBeam;
            }
            // Beam entries come back best first.
            codesIn.AsSpan(0, M).CopyTo(output.AsSpan(i * M, M));
        }

        if (n < 16)
        {
            var scratch = new EncodeScratch(maxBeam, D, M);
            for (int i = 0; i < n; i++) Encode(i, scratch);
        }
        else
        {
            Parallel.For(0, n, () => new EncodeScratch(maxBeam, D, M),
                (i, _, scratch) => { Encode(i, scratch); return scratch; }, _ => { });
        }
        output.AsSpan(0, n * M).CopyTo(codes);
    }

    /// <summary>Per-thread buffers for one beam expansion.</summary>
    private sealed class StepScratch(int beam)
    {
        public readonly float[] Scores = new float[beam];
        public readonly long[] Candidates = new long[beam];
    }

    /// <summary>Per-thread buffers for encoding one vector through all stages.</summary>
    private sealed class EncodeScratch(int beam, int d, int m)
    {
        public readonly float[] ResidualsA = new float[beam * d];
        public readonly float[] ResidualsB = new float[beam * d];
        public readonly int[] CodesA = new int[beam * m];
        public readonly int[] CodesB = new int[beam * m];
        public readonly StepScratch Step = new(beam);
    }

    /// <summary>
    /// One beam-search step for one vector: expands each of <paramref name="beam"/> partial codes
    /// (with residuals <paramref name="residuals"/>) by every codeword of <paramref name="stage"/>,
    /// and keeps the <paramref name="newBeam"/> expansions with the smallest remaining error, best
    /// first. Ties keep the earlier beam entry and codeword, so the step is deterministic.
    /// </summary>
    private unsafe void BeamStep(
        ReadOnlySpan<float> residuals, ReadOnlySpan<int> codes, int beam, int stage, int newBeam,
        Span<float> newResiduals, Span<int> newCodes, StepScratch scratch)
    {
        var heap = new KnnHeap<AscendingOrder>(scratch.Scores.AsSpan(0, newBeam), scratch.Candidates.AsSpan(0, newBeam));
        fixed (float* pres = residuals)
        fixed (float* books = Codebooks)
        fixed (float* pnew = newResiduals)
        {
            float* book = books + (long)stage * Ksub * D;
            for (int b = 0; b < beam; b++)
            {
                float* residual = pres + (long)b * D;
                for (int c = 0; c < Ksub; c++)
                {
                    float error = VectorOps.L2Sqr(residual, book + (long)c * D, D);
                    if (AscendingOrder.Better(error, heap.WorstScore)) heap.Push(error, (long)b * Ksub + c);
                }
            }
            heap.Finish();

            for (int j = 0; j < newBeam; j++)
            {
                long candidate = scratch.Candidates[j];
                int b = (int)(candidate / Ksub), c = (int)(candidate % Ksub);
                VectorOps.Sub(pres + (long)b * D, book + (long)c * D, pnew + (long)j * D, D);
                codes.Slice(b * stage, stage).CopyTo(newCodes.Slice(j * (stage + 1), stage));
                newCodes[j * (stage + 1) + stage] = c;
            }
        }
    }

    // -------------------------------------------------------- Serialization

    protected override void WriteParameters(BinaryWriter writer) => writer.Write(MaxBeamSize);

    protected override void ReadParameters(BinaryReader reader) => MaxBeamSize = reader.ReadInt32();

    /// <summary>Reads a quantizer written by <see cref="AdditiveQuantizer.Write"/>.</summary>
    public static ResidualQuantizer Read(BinaryReader reader) =>
        ReadCommon(reader, (d, m, nbits, searchType) => new ResidualQuantizer(d, m, nbits, searchType));
}
