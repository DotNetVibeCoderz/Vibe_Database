using System.Collections.Concurrent;
using Faiss.Net.IO;
using Xunit;
using Xunit.Abstractions;

namespace Faiss.Net.Tests;

/// <summary>
/// Additive quantizers (RQ, LSQ) and their flat and IVF indexes. Every comparison is against PQ
/// at the same number of code bits, on seeded clustered data whose queries are drawn in the same
/// call as the database, so the numbers are reproducible and in-distribution.
/// <para>
/// Each index is trained once and shared by the tests that read it; a test that mutates one works
/// on a deserialized copy.
/// </para>
/// </summary>
public class AdditiveQuantizerTests(ITestOutputHelper output)
{
    private const int D = 32;
    private const int N = 4000;
    private const int Nq = 100;
    private const int K = 10;

    // 8 codebooks of 6 bits: 6 bytes of code, small enough that LSQ's dense codebook solve
    // (M * 2^nbits = 512 unknowns) keeps the suite fast.
    private const int M = 8;
    private const int Nbits = 6;
    private const int Nlist = 16;
    private const int Nprobe = 4;

    private static readonly Lazy<(float[] Database, float[] Queries)> Shared = new(() =>
    {
        var all = TestData.Clustered(N + Nq, D, seed: 42);
        return (all.AsSpan(0, N * D).ToArray(), all.AsSpan(N * D, Nq * D).ToArray());
    });

    private static readonly ConcurrentDictionary<(string, MetricType), Lazy<Index>> Built = new();

    /// <summary>The index a factory string describes, trained and filled with the shared database.</summary>
    private static Index Get(string description, MetricType metric = MetricType.L2) =>
        Built.GetOrAdd((description, metric), key => new Lazy<Index>(() =>
        {
            var (data, _) = Shared.Value;
            var index = FaissNet.IndexFactory(D, key.Item1, key.Item2);
            index.Train(data);
            index.Add(data);
            if (index is IndexIVF ivf) ivf.Nprobe = Nprobe;
            return index;
        })).Value;

    private static Index Copy(Index index) => IndexIO.Deserialize(IndexIO.Serialize(index));

    private static double Mse(float[] data, float[] reconstruction)
    {
        double sum = 0;
        for (int i = 0; i < data.Length; i++)
        {
            double diff = data[i] - reconstruction[i];
            sum += diff * diff;
        }
        return sum / (data.Length / D);
    }

    private static double IndexMse(Index index)
    {
        var (data, _) = Shared.Value;
        var reconstructed = new float[data.Length];
        index.ReconstructN(0, N, reconstructed);
        return Mse(data, reconstructed);
    }

    private static double QuantizerMse(AdditiveQuantizer aq, float[] data)
    {
        var codes = aq.ComputeCodes(data);
        var decoded = new float[data.Length];
        aq.Decode(codes, data.Length / D, decoded);
        return Mse(data, decoded);
    }

    private double Recall(string description, MetricType metric = MetricType.L2, SearchParameters? parameters = null)
    {
        var (data, queries) = Shared.Value;
        var truth = TestData.GroundTruth(data, queries, D, K, metric);
        double recall = FaissNet.ComputeRecall(truth, Get(description, metric).Search(queries, K, parameters));
        output.WriteLine($"{description} ({metric}): recall@{K} = {recall:F3}");
        return recall;
    }

    private static AdditiveQuantizer AqOf(Index index) => index switch
    {
        IndexAdditiveQuantizer flat => flat.Aq,
        IndexIVFAdditiveQuantizer ivf => ivf.Aq,
        _ => throw new InvalidOperationException($"{index.GetType().Name} is not additive."),
    };

    // ------------------------------------------------------- Reconstruction

    [Fact]
    public void ResidualAndLocalSearchQuantizersReconstructBetterThanPq()
    {
        double pq = IndexMse(Get($"PQ{M}x{Nbits}"));
        double rq = IndexMse(Get($"RQ{M}x{Nbits}"));
        double lsq = IndexMse(Get($"LSQ{M}x{Nbits}"));

        output.WriteLine($"MSE at {M}x{Nbits} bits: PQ {pq:F5}, RQ {rq:F5}, LSQ {lsq:F5}");
        Assert.True(rq < 0.6 * pq, $"RQ MSE {rq} should be well below PQ {pq}");
        Assert.True(lsq < 0.7 * pq, $"LSQ MSE {lsq} should be well below PQ {pq}");
    }

    [Fact]
    public void BareQuantizerCodesCarryNoNorm()
    {
        Assert.Equal(M * Nbits / 8, new ResidualQuantizer(D, M, Nbits).CodeSize);
        Assert.Equal(M * Nbits / 8, new LocalSearchQuantizer(D, M, Nbits).CodeSize);
        Assert.Equal(3 + 4, new ResidualQuantizer(D, 3, 8, AdditiveQuantizerSearchType.NormFloat).CodeSize);
        Assert.Equal(2, new ResidualQuantizer(D, 3, 5).CodeSize); // 15 bits round up to 2 bytes
    }

    [Fact]
    public void BeamSearchBeatsGreedyResidualEncoding()
    {
        var (data, _) = Shared.Value;
        var rq = (ResidualQuantizer)AqOf(Copy(Get($"RQ{M}x{Nbits}")));
        double beam = QuantizerMse(rq, data);
        rq.MaxBeamSize = 1;
        double greedy = QuantizerMse(rq, data);
        output.WriteLine($"RQ MSE: beam 5 {beam:F5}, greedy {greedy:F5}");
        Assert.True(beam < greedy);
    }

    [Fact]
    public void TrainingIsDeterministicForAGivenSeed()
    {
        var (data, _) = Shared.Value;
        var training = data.AsSpan(0, 2000 * D).ToArray();

        var rq1 = new ResidualQuantizer(D, 4, Nbits);
        var rq2 = new ResidualQuantizer(D, 4, Nbits);
        rq1.Train(training);
        rq2.Train(training);
        Assert.Equal(rq1.Codebooks, rq2.Codebooks);
        Assert.Equal(rq1.ComputeCodes(training), rq2.ComputeCodes(training));

        var lsq1 = new LocalSearchQuantizer(D, 4, Nbits) { TrainIters = 6 };
        var lsq2 = new LocalSearchQuantizer(D, 4, Nbits) { TrainIters = 6 };
        lsq1.Train(training);
        lsq2.Train(training);
        Assert.Equal(lsq1.Codebooks, lsq2.Codebooks);
        Assert.Equal(lsq1.ComputeCodes(training), lsq2.ComputeCodes(training));

        // A different seed must actually change the result, or the seed is not wired through.
        var lsq3 = new LocalSearchQuantizer(D, 4, Nbits) { TrainIters = 6, RandomSeed = 7 };
        lsq3.Train(training);
        Assert.NotEqual(lsq1.Codebooks, lsq3.Codebooks);
    }

    [Fact]
    public void TableScoringEqualsDirectDistanceForPackedAndByteCodes()
    {
        // nbits = 8 takes the byte fast path in packing and scoring; nbits = 5 straddles bytes.
        var (data, _) = Shared.Value;
        var training = data.AsSpan(0, 1500 * D).ToArray();
        foreach (int nbits in new[] { 8, 5 })
        {
            var rq = new ResidualQuantizer(D, 3, nbits, AdditiveQuantizerSearchType.NormFloat) { MaxBeamSize = 2 };
            rq.Train(training);
            var codes = rq.ComputeCodes(training.AsSpan(0, 50 * D));
            var decoded = new float[D];
            var table = new float[rq.LookupTableSize];
            var score = new float[1];
            for (int i = 0; i < 50; i++)
            {
                var code = codes.AsSpan(i * rq.CodeSize, rq.CodeSize);
                rq.Decode(code, decoded);
                // The stored norm is the reconstruction's, and table scoring equals direct L2.
                Assert.Equal(Core.VectorOps.Norm2Sqr(decoded), rq.DecodeNorm(code), 3);
                var query = training.AsSpan((100 + i) * D, D);
                rq.ComputeLookupTable(query, table);
                rq.ScoreCodes(table, code, score, Core.VectorOps.Norm2Sqr(query), l2: true);
                Assert.Equal(Core.VectorOps.L2Sqr(query, decoded), score[0], 3);
            }
        }
    }

    // --------------------------------------------------------- Flat indexes

    [Fact]
    public void FlatIndexesReachRecallAboveProductQuantization()
    {
        double pq = Recall($"PQ{M}x{Nbits}");
        double rq = Recall($"RQ{M}x{Nbits}");
        double lsq = Recall($"LSQ{M}x{Nbits}");

        Assert.Equal(M * Nbits / 8 + 4, ((IndexAdditiveQuantizer)Get($"RQ{M}x{Nbits}")).CodeSize); // codes + float norm
        Assert.True(rq >= 0.50, $"RQ recall {rq}");
        Assert.True(lsq >= 0.45, $"LSQ recall {lsq}");
        Assert.True(rq > pq + 0.15, $"RQ {rq} vs PQ {pq}");
        Assert.True(lsq > pq + 0.10, $"LSQ {lsq} vs PQ {pq}");
    }

    [Fact]
    public void InnerProductIndexesStoreNoNormAndScoreExactly()
    {
        string description = $"RQ{M}x{Nbits}";
        var index = (IndexAdditiveQuantizer)Get(description, MetricType.InnerProduct);
        Assert.Equal(M * Nbits / 8, index.CodeSize);
        double recall = Recall(description, MetricType.InnerProduct);
        Assert.True(recall >= 0.5, $"IP recall {recall}");

        // Scores are exactly the inner product with the reconstruction.
        var (_, queries) = Shared.Value;
        var query = queries.AsSpan(0, D);
        var result = index.Search(query, 3);
        var decoded = new float[D];
        for (int j = 0; j < 3; j++)
        {
            index.Reconstruct(result.Labels[j], decoded);
            Assert.Equal(Core.VectorOps.InnerProduct(query, decoded), result.Distances[j], 3);
        }
    }

    [Fact]
    public void ReconstructMatchesTheQuantizerDecode()
    {
        var (data, _) = Shared.Value;
        var index = (IndexAdditiveQuantizer)Get($"RQ{M}x{Nbits}");
        Assert.Equal(QuantizerMse(index.Aq, data), IndexMse(index), 6);
    }

    // ---------------------------------------------------------- IVF indexes

    [Fact]
    public void IvfVariantsMatchOrBeatIvfPq()
    {
        double pq = Recall($"IVF{Nlist},PQ{M}x{Nbits}");
        double rq = Recall($"IVF{Nlist},RQ{M}x{Nbits}");
        double lsq = Recall($"IVF{Nlist},LSQ{M}x{Nbits}");

        Assert.True(((IndexIVF)Get($"IVF{Nlist},RQ{M}x{Nbits}")).ByResidual);
        Assert.True(rq >= pq, $"IVFRQ {rq} vs IVFPQ {pq}");
        Assert.True(lsq >= pq - 0.01, $"IVFLSQ {lsq} vs IVFPQ {pq}");
        Assert.True(rq >= 0.55 && lsq >= 0.5, $"IVFRQ {rq}, IVFLSQ {lsq}");
    }

    [Theory]
    [InlineData("RQ")]
    [InlineData("LSQ")]
    public void IvfResidualScoresAreDistancesToTheReconstruction(string kind)
    {
        var (_, queries) = Shared.Value;
        var index = (IndexIVF)Copy(Get($"IVF{Nlist},{kind}{M}x{Nbits}"));
        index.Nprobe = Nlist;
        index.MakeDirectMap();
        var result = index.Search(queries.AsSpan(0, 5 * D), K);
        var decoded = new float[D];
        for (int q = 0; q < 5; q++)
        {
            for (int j = 0; j < K; j++)
            {
                index.Reconstruct(result.Labels[q * K + j], decoded);
                float exact = Core.VectorOps.L2Sqr(queries.AsSpan(q * D, D), decoded);
                Assert.Equal(exact, result.Distances[q * K + j], 2);
            }
        }
    }

    [Fact]
    public void IvfInnerProductEncodesRawVectors()
    {
        var all = new SearchParametersIVF { Nprobe = Nlist };
        foreach (string kind in new[] { "RQ", "LSQ" })
        {
            var index = (IndexIVFAdditiveQuantizer)Get($"IVF{Nlist},{kind}{M}x{Nbits}", MetricType.InnerProduct);
            Assert.False(index.ByResidual);
            Assert.Equal(M * Nbits / 8, index.CodeSize);
        }

        double pq = Recall($"IVF{Nlist},PQ{M}x{Nbits}", MetricType.InnerProduct, all);
        double rq = Recall($"IVF{Nlist},RQ{M}x{Nbits}", MetricType.InnerProduct, all);
        double lsq = Recall($"IVF{Nlist},LSQ{M}x{Nbits}", MetricType.InnerProduct, all);
        Assert.True(rq >= 0.5 && rq > pq, $"IVFRQ IP {rq} vs IVFPQ IP {pq}");
        // LSQ leaves a small tail of vectors at cluster-scale error (local minima of its encoder).
        // That barely moves its MSE or its L2 recall, but on un-normalised data a vector whose
        // reconstruction overshoots can enter many inner-product top-k lists, so LSQ's IP recall
        // here sits near PQ's rather than near RQ's. The floor guards against regressions only.
        Assert.True(lsq >= 0.25, $"IVFLSQ IP {lsq}");
    }

    // ---------------------------------------------------- Selector, persistence

    public static TheoryData<string> Descriptions => new()
    {
        $"RQ{M}x{Nbits}", $"LSQ{M}x{Nbits}", $"IVF{Nlist},RQ{M}x{Nbits}", $"IVF{Nlist},LSQ{M}x{Nbits}",
    };

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void SelectorResultsAreMembers(string description)
    {
        var (_, queries) = Shared.Value;
        var index = Get(description);
        var batch = queries.AsSpan(0, 20 * D);

        var sel = IDSelector.Predicate(id => id % 7 == 3);
        var found = index.Search(batch, K, new SearchParametersIVF { Sel = sel, Nprobe = Nlist });
        Assert.All(found.Labels, id => Assert.True(id >= 0 && sel.IsMember(id), $"id {id}"));

        // The filtered result is the best members, not merely some: it equals an unfiltered search
        // over the same codes with every non-member removed.
        var members = Copy(index);
        members.RemoveIds(id => !sel.IsMember(id));
        if (members is IndexIVF ivf) ivf.Nprobe = Nlist;
        var reference = members.Search(batch, K);
        Assert.Equal(reference.Distances, found.Distances);

        var range = IDSelector.Range(1000, 1200);
        found = index.Search(batch, K, new SearchParametersIVF { Sel = range, Nprobe = Nlist });
        Assert.All(found.Labels, id => Assert.InRange(id, 1000, 1199));
    }

    [Theory]
    [MemberData(nameof(Descriptions))]
    public void SerializationRoundTripsByteIdentically(string description)
    {
        var (_, queries) = Shared.Value;
        var index = Get(description);

        var bytes = IndexIO.Serialize(index);
        var restored = IndexIO.Deserialize(bytes);
        Assert.Equal(index.GetType(), restored.GetType());
        Assert.Equal(index.Ntotal, restored.Ntotal);

        var before = index.Search(queries, K);
        var after = restored.Search(queries, K);
        Assert.Equal(before.Labels, after.Labels);
        Assert.Equal(before.Distances, after.Distances);
        Assert.Equal(bytes, IndexIO.Serialize(restored));

        // Encoding parameters survive too: new vectors get identical codes in both copies.
        var extra = queries.AsSpan(0, 10 * D).ToArray();
        var original = Copy(index);
        original.Add(extra);
        restored.Add(extra);
        Assert.Equal(IndexIO.Serialize(original), IndexIO.Serialize(restored));
    }

    // ------------------------------------------------------------- Factory

    [Theory]
    [InlineData("RQ8x6", typeof(IndexResidualQuantizer), 8, 6)]
    [InlineData("RQ4", typeof(IndexResidualQuantizer), 4, 8)]
    [InlineData("LSQ8x6", typeof(IndexLocalSearchQuantizer), 8, 6)]
    [InlineData("LSQ4", typeof(IndexLocalSearchQuantizer), 4, 8)]
    [InlineData("IVF32,RQ5x8", typeof(IndexIVFResidualQuantizer), 5, 8)]
    [InlineData("IVF32,LSQ3x4", typeof(IndexIVFLocalSearchQuantizer), 3, 4)]
    public void FactoryBuildsAdditiveIndexes(string description, Type expected, int m, int nbits)
    {
        var index = FaissNet.IndexFactory(D, description);
        Assert.IsType(expected, index);
        var aq = AqOf(index);
        Assert.Equal(m, aq.M);
        Assert.Equal(nbits, aq.Nbits);
        Assert.Equal(AdditiveQuantizerSearchType.NormFloat, aq.SearchType);

        var ip = FaissNet.IndexFactory(D, description, MetricType.InnerProduct);
        Assert.IsType(expected, ip);
        Assert.Equal(MetricType.InnerProduct, ip.MetricType);
        Assert.Equal(AdditiveQuantizerSearchType.LutNonorm, AqOf(ip).SearchType);
    }

    [Fact]
    public void FactoryRejectsOversizedLsq() =>
        Assert.Throws<ArgumentException>(() => FaissNet.IndexFactory(D, "LSQ64x8"));
}

/// <summary>
/// Regression: LSQ trained from random codes landed in a different basin per seed — final MSE from
/// 0.054 to 0.443 on this data, with PQ at 0.121 — and which basin a seed reached depended on the
/// SIMD width, so CI failed on arm64 what passed on x64. It now starts from residual-quantizer
/// codes and keeps its best codebooks.
/// </summary>
public class LocalSearchQuantizerStabilityTests
{
    [Fact]
    public void EverySeedBeatsProductQuantization()
    {
        const int d = 32;
        var data = TestData.Clustered(4000, d, seed: 42);
        double Mse(AdditiveQuantizer q)
        {
            var codes = q.ComputeCodes(data);
            var reconstruction = new float[data.Length];
            q.Decode(codes, 4000, reconstruction);
            double sum = 0;
            for (int i = 0; i < data.Length; i++) { double e = data[i] - reconstruction[i]; sum += e * e; }
            return sum / 4000;
        }

        var pq = new ProductQuantizer(d, 8, 6);
        pq.Train(data);
        var pqCodes = pq.ComputeCodes(data);
        var pqReconstruction = new float[data.Length];
        pq.Decode(pqCodes, 4000, pqReconstruction);
        double pqMse = 0;
        for (int i = 0; i < data.Length; i++) { double e = data[i] - pqReconstruction[i]; pqMse += e * e; }
        pqMse /= 4000;

        foreach (long seed in new long[] { 1, 2, 3, 4 })
        {
            var lsq = new LocalSearchQuantizer(d, 8, 6) { RandomSeed = seed, TrainIters = 10 };
            lsq.Train(data);
            double mse = Mse(lsq);
            Assert.True(mse < 0.6 * pqMse, $"seed {seed}: LSQ MSE {mse:F4} vs PQ {pqMse:F4}");
        }
    }
}
