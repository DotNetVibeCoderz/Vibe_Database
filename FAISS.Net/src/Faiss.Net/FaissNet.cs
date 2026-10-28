using System.Globalization;
using Faiss.Net.Core;
using Faiss.Net.IO;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>
/// Module-level entry points, mirroring the functions that live directly on the <c>faiss</c> module
/// in Python: <c>index_factory</c>, <c>normalize_L2</c>, <c>write_index</c>, <c>read_index</c>,
/// <c>kmeans_clustering</c>. Python's <c>faiss.x(...)</c> is <c>FaissNet.X(...)</c> here — the class
/// cannot be called <c>Faiss</c> because the required root namespace <c>Faiss.Net</c> already binds
/// that name, and a namespace shadows a type of the same name at every call site.
/// <para>
/// Everything here is a thin convenience over the types in this namespace, so nothing is only
/// reachable through the facade.
/// </para>
/// </summary>
public static class FaissNet
{
    /// <summary>Library version.</summary>
    public const string Version = "1.0.0";

    /// <summary>Description of the SIMD path selected at runtime.</summary>
    public static string SimdInfo => VectorOps.SimdDescription;

    /// <summary>
    /// Builds an index from a FAISS factory string, e.g. <c>"IVF1024,PQ16"</c>,
    /// <c>"OPQ16,IVF4096,PQ16"</c>, <c>"HNSW32"</c>, <c>"PCA64,Flat"</c>, <c>"IDMap,Flat"</c>.
    /// <para>
    /// Components are comma-separated and read left to right: optional transforms and an
    /// <c>IDMap</c> wrapper first, then an optional <c>IVF&lt;nlist&gt;</c> coarse level, then the
    /// encoding of the vectors themselves. Composing the same recipe by hand is always possible;
    /// the factory just makes the common combinations one line.
    /// </para>
    /// </summary>
    /// <param name="dimension">Vector dimension.</param>
    /// <param name="description">Factory string.</param>
    /// <param name="metric">Distance metric.</param>
    public static Index IndexFactory(int dimension, string description, MetricType metric = MetricType.L2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var parts = new List<string>(description.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        int position = 0;
        bool wrapInIdMap = false;
        var transforms = new List<VectorTransform>();
        int currentDimension = dimension;

        // --- leading wrappers and transforms
        while (position < parts.Count)
        {
            string part = parts[position];
            if (part.Equals("IDMap", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("IDMap2", StringComparison.OrdinalIgnoreCase))
            {
                wrapInIdMap = true;
                position++;
                continue;
            }

            var transform = TryParseTransform(part, currentDimension);
            if (transform is null) break;
            transforms.Add(transform);
            currentDimension = transform.DOut;
            position++;
        }

        if (position >= parts.Count)
            throw new ArgumentException($"Factory string '{description}' has no index component.", nameof(description));

        // --- optional trailing refinement: ",RFlat" or ",Refine(<encoding>)"
        string? refine = null;
        string last = parts[^1];
        if (parts.Count - position >= 2 &&
            (last.Equals("RFlat", StringComparison.OrdinalIgnoreCase) ||
             last.StartsWith("Refine(", StringComparison.OrdinalIgnoreCase)))
        {
            refine = last;
            parts.RemoveAt(parts.Count - 1);
        }

        // --- optional coarse quantizer level
        int nlist = 0;
        Index? coarse = null;
        string head = parts[position];
        if (head.StartsWith("IVF", StringComparison.OrdinalIgnoreCase))
        {
            string spec = head[3..];
            int underscore = spec.IndexOf('_');
            string count = underscore < 0 ? spec : spec[..underscore];
            if (!int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out nlist) || nlist <= 0)
                throw new ArgumentException($"Cannot parse cell count from '{head}'.", nameof(description));
            if (underscore >= 0)
                coarse = BuildCoarseQuantizer(spec[(underscore + 1)..], currentDimension, metric, description);
            position++;
            if (position >= parts.Count)
                throw new ArgumentException(
                    $"'{description}' declares IVF{nlist} but no encoding. Add ',Flat', ',PQ<m>' or ',SQ8'.",
                    nameof(description));
        }

        string encoding = parts[position++];
        string? storage = null;
        if (position < parts.Count)
        {
            // "HNSW32,SQ8": the second component is the graph's storage. A trailing ",Flat" after
            // anything else is accepted as a no-op, as earlier versions did.
            if (encoding.StartsWith("HNSW", StringComparison.OrdinalIgnoreCase) && nlist == 0)
                storage = parts[position++];
            else if (parts[position].Equals("Flat", StringComparison.OrdinalIgnoreCase))
                position++;
            if (position < parts.Count)
                throw new ArgumentException($"Unexpected trailing component '{parts[position]}' in '{description}'.", nameof(description));
        }

        Index index = storage is not null
            ? BuildHnsw(encoding, storage, currentDimension, metric, description)
            : BuildIndex(encoding, currentDimension, nlist, metric, description, coarse);

        if (refine is not null) index = BuildRefine(refine, index, currentDimension, metric, description);
        if (transforms.Count > 0) index = new IndexPreTransform(transforms, index);
        if (wrapInIdMap) index = new IndexIDMap2(index);
        return index;
    }

    /// <summary>The <c>_HNSW32</c> or <c>_Flat</c> suffix of an <c>IVF</c> component.</summary>
    private static Index BuildCoarseQuantizer(string spec, int dimension, MetricType metric, string description)
    {
        if (spec.StartsWith("HNSW", StringComparison.OrdinalIgnoreCase))
        {
            string rest = spec[4..];
            int m = rest.Length == 0 ? 32 : ParseInt(rest, spec);
            return new IndexHNSWFlat(dimension, m, metric);
        }
        if (spec.Equals("Flat", StringComparison.OrdinalIgnoreCase))
            return metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension);
        throw new ArgumentException($"'{description}': unknown coarse quantizer '{spec}'. Use IVF<n>_HNSW<M>.");
    }

    /// <summary><c>HNSW&lt;M&gt;,Flat|SQ…|PQ…</c>: a graph over the given storage.</summary>
    private static Index BuildHnsw(string graph, string storage, int dimension, MetricType metric, string description)
    {
        string rest = graph[4..];
        int m = rest.Length == 0 ? 32 : ParseInt(rest, graph);

        if (storage.Equals("Flat", StringComparison.OrdinalIgnoreCase)) return new IndexHNSWFlat(dimension, m, metric);
        if (storage.StartsWith("SQ", StringComparison.OrdinalIgnoreCase))
            return new IndexHNSWSQ(dimension, ParseScalarType(storage, description), m, metric);
        if (storage.StartsWith("PQ", StringComparison.OrdinalIgnoreCase))
        {
            var (pqM, nbits) = ParsePq(storage, dimension, description);
            return new IndexHNSWPQ(dimension, pqM, m, nbits, metric);
        }
        throw new ArgumentException($"'{description}': HNSW storage '{storage}' is not supported. Use Flat, SQ8, SQ4, SQfp16 or PQ<m>.");
    }

    /// <summary><c>RFlat</c> or <c>Refine(&lt;encoding&gt;)</c>: a re-ranking stage after the base index.</summary>
    private static Index BuildRefine(string spec, Index baseIndex, int dimension, MetricType metric, string description)
    {
        if (spec.Equals("RFlat", StringComparison.OrdinalIgnoreCase)) return new IndexRefineFlat(baseIndex);
        if (!spec.EndsWith(')'))
            throw new ArgumentException($"'{description}': malformed refinement '{spec}'. Use RFlat or Refine(SQ8).");
        string inner = spec["Refine(".Length..^1];
        var refineIndex = BuildIndex(inner, dimension, 0, metric, description, null);
        return new IndexRefine(baseIndex, refineIndex);
    }

    private static ScalarQuantizerType ParseScalarType(string encoding, string description) =>
        encoding[2..].ToLowerInvariant() switch
        {
            "8" => ScalarQuantizerType.PerDimension8Bit,
            "4" => ScalarQuantizerType.PerDimension4Bit,
            "fp16" => ScalarQuantizerType.Float16,
            "8_uniform" => ScalarQuantizerType.Uniform8Bit,
            _ => throw new ArgumentException($"'{description}': unknown scalar quantizer '{encoding}'. Use SQ8, SQ4 or SQfp16."),
        };

    private static (int M, int Nbits) ParsePq(string encoding, int dimension, string description)
    {
        string rest = encoding[2..];
        int nbits = 8;
        int cross = rest.IndexOf('x');
        if (cross >= 0)
        {
            nbits = ParseInt(rest[(cross + 1)..], encoding);
            rest = rest[..cross];
        }
        int m = ParseInt(rest, encoding);
        if (dimension % m != 0)
            throw new ArgumentException(
                $"'{description}': PQ{m} requires m to divide d={dimension}. Try m in " +
                $"{{{string.Join(", ", Divisors(dimension).Take(8))}}}.");
        return (m, nbits);
    }

    private static VectorTransform? TryParseTransform(string part, int dimension)
    {
        if (part.Equals("L2norm", StringComparison.OrdinalIgnoreCase))
            return new NormalizationTransform(dimension);

        if (part.StartsWith("PCAW", StringComparison.OrdinalIgnoreCase))
            return new PCAMatrix(dimension, ParseInt(part[4..], part), -0.5f);

        if (part.StartsWith("PCAR", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("PCA", StringComparison.OrdinalIgnoreCase))
        {
            int offset = part.StartsWith("PCAR", StringComparison.OrdinalIgnoreCase) ? 4 : 3;
            return new PCAMatrix(dimension, ParseInt(part[offset..], part));
        }

        if (part.StartsWith("RR", StringComparison.OrdinalIgnoreCase))
            return new RandomRotationMatrix(dimension, ParseInt(part[2..], part));

        if (part.StartsWith("OPQ", StringComparison.OrdinalIgnoreCase))
        {
            string rest = part[3..];
            int underscore = rest.IndexOf('_');
            int m = ParseInt(underscore < 0 ? rest : rest[..underscore], part);
            int dOut = underscore < 0 ? dimension : ParseInt(rest[(underscore + 1)..], part);
            if (dOut > dimension)
                throw new ArgumentException($"'{part}': OPQ cannot raise the dimension from {dimension} to {dOut}.");
            if (dOut % m != 0)
                throw new ArgumentException($"'{part}': m={m} must divide the output dimension {dOut}.");
            return new OPQMatrix(dimension, m, dOut);
        }

        return null;
    }

    private static Index BuildIndex(string encoding, int dimension, int nlist, MetricType metric, string description, Index? coarse)
    {
        Index Quantizer() => coarse ?? (metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension));

        if (encoding.Equals("Flat", StringComparison.OrdinalIgnoreCase))
        {
            if (nlist > 0) return new IndexIVFFlat(Quantizer(), dimension, nlist, metric);
            return metric == MetricType.InnerProduct ? new IndexFlatIP(dimension) : new IndexFlatL2(dimension);
        }

        if (encoding.StartsWith("HNSW", StringComparison.OrdinalIgnoreCase))
        {
            if (nlist > 0) throw new ArgumentException($"'{description}': HNSW cannot sit under an IVF level.");
            string rest = encoding[4..];
            int m = rest.Length == 0 ? 32 : ParseInt(rest, encoding);
            return new IndexHNSWFlat(dimension, m, metric);
        }

        if (encoding.StartsWith("PQ", StringComparison.OrdinalIgnoreCase))
        {
            var (m, nbits) = ParsePq(encoding, dimension, description);
            return nlist > 0
                ? new IndexIVFPQ(Quantizer(), dimension, nlist, m, nbits, metric)
                : new IndexPQ(dimension, m, nbits, metric);
        }

        if (encoding.StartsWith("SQ", StringComparison.OrdinalIgnoreCase))
        {
            var type = ParseScalarType(encoding, description);
            return nlist > 0
                ? new IndexIVFScalarQuantizer(Quantizer(), dimension, nlist, type, metric)
                : new IndexScalarQuantizer(dimension, type, metric);
        }

        throw new ArgumentException(
            $"'{description}': unknown encoding '{encoding}'. Supported: Flat, PQ<m>[x<bits>], SQ8, SQ4, SQfp16, HNSW<M>.");
    }

    private static int ParseInt(string text, string context) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : throw new ArgumentException($"Cannot parse a positive integer from '{context}'.");

    private static IEnumerable<int> Divisors(int value)
    {
        for (int i = 1; i <= value; i++) if (value % i == 0) yield return i;
    }

    // ------------------------------------------------------------- Utilities

    /// <summary>
    /// L2-normalizes rows in place, the standard preparation for cosine similarity via an
    /// inner-product index. Equivalent to <c>faiss.normalize_L2(x)</c>.
    /// </summary>
    public static void NormalizeL2(Span<float> x, int dimension) => VectorOps.NormalizeL2(x, dimension);

    /// <summary>Writes an index to disk. Equivalent to <c>faiss.write_index</c>.</summary>
    public static void WriteIndex(Index index, string path) => IndexIO.WriteIndex(index, path);

    /// <summary>Reads an index from disk. Equivalent to <c>faiss.read_index</c>.</summary>
    public static Index ReadIndex(string path) => IndexIO.ReadIndex(path);

    /// <summary>Runs k-means and returns the centroids. Equivalent to <c>faiss.Kmeans(...).train(x)</c>.</summary>
    public static float[] KmeansClustering(ReadOnlySpan<float> x, int dimension, int k, int iterations = 25, long seed = 1234)
    {
        var kmeans = new Kmeans(dimension, k, new ClusteringParameters { Iterations = iterations, Seed = seed });
        kmeans.Train(x);
        return kmeans.Centroids;
    }

    /// <summary>
    /// Reproducible random vectors, for samples, tests and benchmarks. Uniform in <c>[0, 1)</c>,
    /// matching how the FAISS tutorials generate their data so results are comparable.
    /// </summary>
    public static float[] RandomVectors(int n, int dimension, long seed = 1234)
    {
        var rng = new RandomGenerator(seed);
        var data = new float[(long)n * dimension];
        rng.FillUniform(data);
        return data;
    }

    /// <summary>
    /// Reproducible clustered random vectors: <paramref name="clusters"/> Gaussian blobs. Far more
    /// representative than uniform noise for judging an approximate index, because uniform data in
    /// high dimension has no cluster structure for a coarse quantizer to exploit — it makes IVF and
    /// HNSW look worse than they are on real embeddings.
    /// </summary>
    public static float[] RandomClusteredVectors(int n, int dimension, int clusters, float spread = 0.1f, long seed = 1234)
    {
        var rng = new RandomGenerator(seed);
        var centers = new float[(long)clusters * dimension];
        rng.FillUniform(centers);

        var data = new float[(long)n * dimension];
        for (int i = 0; i < n; i++)
        {
            int cluster = rng.NextInt(clusters);
            for (int j = 0; j < dimension; j++)
                data[(long)i * dimension + j] = centers[(long)cluster * dimension + j] + rng.NextGaussian() * spread;
        }
        return data;
    }

    /// <summary>
    /// Fraction of true nearest neighbours an approximate result recovers — <c>recall@k</c>, the
    /// standard quality measure for an ANN index.
    /// </summary>
    /// <param name="groundTruth">Exact result, typically from an <see cref="IndexFlat"/>.</param>
    /// <param name="candidate">Result from the index under test.</param>
    /// <param name="k">Rank to evaluate at; defaults to the candidate's k.</param>
    public static double ComputeRecall(SearchResult groundTruth, SearchResult candidate, int k = 0)
    {
        if (k <= 0) k = Math.Min(groundTruth.K, candidate.K);
        int queries = Math.Min(groundTruth.QueryCount, candidate.QueryCount);
        if (queries == 0 || k == 0) return 0;

        long found = 0, expected = 0;
        var truth = new HashSet<long>();
        for (int q = 0; q < queries; q++)
        {
            truth.Clear();
            var truthLabels = groundTruth.LabelsFor(q);
            for (int i = 0; i < k && i < truthLabels.Length; i++)
                if (truthLabels[i] >= 0) { truth.Add(truthLabels[i]); expected++; }

            var candidateLabels = candidate.LabelsFor(q);
            for (int i = 0; i < k && i < candidateLabels.Length; i++)
                if (candidateLabels[i] >= 0 && truth.Contains(candidateLabels[i])) found++;
        }
        return expected == 0 ? 0 : found / (double)expected;
    }

    /// <summary>
    /// <c>recall@1</c>: how often the single best true neighbour appears anywhere in the top k.
    /// Often the number that actually matters, since applications usually care most about the top hit.
    /// </summary>
    public static double ComputeRecallAt1(SearchResult groundTruth, SearchResult candidate)
    {
        int queries = Math.Min(groundTruth.QueryCount, candidate.QueryCount);
        int hits = 0, counted = 0;
        for (int q = 0; q < queries; q++)
        {
            var truth = groundTruth.LabelsFor(q);
            if (truth.Length == 0 || truth[0] < 0) continue;
            counted++;
            foreach (long label in candidate.LabelsFor(q))
                if (label == truth[0]) { hits++; break; }
        }
        return counted == 0 ? 0 : hits / (double)counted;
    }
}
