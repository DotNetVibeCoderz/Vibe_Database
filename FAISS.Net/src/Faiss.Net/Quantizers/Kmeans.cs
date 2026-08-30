using System.Collections.Concurrent;
using Faiss.Net.Core;
using Faiss.Net.Utils;

namespace Faiss.Net;

/// <summary>Tuning knobs for <see cref="Kmeans"/>. Defaults match FAISS.</summary>
public sealed class ClusteringParameters
{
    /// <summary>Lloyd iterations. FAISS defaults to 25; 10 is usually enough for a coarse quantizer.</summary>
    public int Iterations { get; set; } = 25;

    /// <summary>Independent restarts; the lowest-objective run wins. Above 1 costs proportional time.</summary>
    public int Redo { get; set; } = 1;

    /// <summary>
    /// Training vectors are subsampled to at most this many per centroid. Clustering quality
    /// saturates quickly, so this caps training time on large datasets instead of scanning everything.
    /// </summary>
    public int MaxPointsPerCentroid { get; set; } = 256;

    /// <summary>Warn below this many training points per centroid; the clustering is under-determined.</summary>
    public int MinPointsPerCentroid { get; set; } = 39;

    /// <summary>Seed for subsampling and initialization. Fixed seed means reproducible indexes.</summary>
    public long Seed { get; set; } = 1234;

    /// <summary>L2-normalize centroids after every update, giving cosine (spherical) k-means.</summary>
    public bool Spherical { get; set; }

    /// <summary>Prints per-iteration objective to the console.</summary>
    public bool Verbose { get; set; }

    /// <summary>Stop early when the objective improves by less than this fraction.</summary>
    public double Tolerance { get; set; } = 1e-6;
}

/// <summary>
/// k-means clustering, used directly (<c>faiss.Kmeans</c>) and internally to train the coarse
/// quantizer of every IVF index and the sub-codebooks of a product quantizer.
/// <para>
/// Initialization is k-means++, which costs one extra pass but reliably beats the random-sample
/// init for the small-k, high-dimension shapes quantizers use. Assignment reuses
/// <see cref="BruteForce"/>, so it is SIMD-accelerated and multi-threaded for free; the update step
/// accumulates into per-thread buffers and reduces once, avoiding contention on the centroid array.
/// </para>
/// </summary>
public sealed class Kmeans
{
    private readonly ClusteringParameters _parameters;

    /// <summary>Vector dimension.</summary>
    public int D { get; }

    /// <summary>Number of centroids.</summary>
    public int K { get; }

    /// <summary>Flat <c>k * d</c> centroids, valid after <see cref="Train"/>.</summary>
    public float[] Centroids { get; private set; }

    /// <summary>Sum of squared distances from points to their centroid after the final iteration.</summary>
    public double Objective { get; private set; } = double.NaN;

    /// <summary>Objective after each iteration of the winning run; useful for convergence plots.</summary>
    public IReadOnlyList<double> ObjectiveHistory => _history;
    private readonly List<double> _history = [];

    public Kmeans(int d, int k, ClusteringParameters? parameters = null)
    {
        if (d <= 0) throw new ArgumentOutOfRangeException(nameof(d));
        if (k <= 0) throw new ArgumentOutOfRangeException(nameof(k));
        D = d;
        K = k;
        _parameters = parameters ?? new ClusteringParameters();
        Centroids = new float[(long)k * d];
    }

    /// <summary>Convenience overload mirroring <c>faiss.Kmeans(d, k, niter=..., verbose=...)</c>.</summary>
    public Kmeans(int d, int k, int niter, bool verbose = false, bool spherical = false, long seed = 1234)
        : this(d, k, new ClusteringParameters { Iterations = niter, Verbose = verbose, Spherical = spherical, Seed = seed })
    {
    }

    /// <summary>Runs clustering and returns the final objective.</summary>
    /// <param name="x">Flat <c>n * d</c> training vectors.</param>
    public double Train(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        if (n == 0) throw new ArgumentException("No training vectors supplied.", nameof(x));

        if (n < K)
        {
            // Fewer points than centroids: keep every point as a centroid and pad by repetition, so
            // downstream indexes still see exactly K usable centroids instead of failing to train.
            for (int i = 0; i < K; i++)
                x.Slice((i % n) * D, D).CopyTo(Centroids.AsSpan(i * D, D));
            Objective = 0;
            return Objective;
        }

        if (_parameters.Verbose && n < (long)K * _parameters.MinPointsPerCentroid)
            Console.WriteLine($"[kmeans] warning: {n} points for {K} centroids " +
                              $"({n / (double)K:F1} per centroid, {_parameters.MinPointsPerCentroid} recommended).");

        var rng = new RandomGenerator(_parameters.Seed);
        float[] sample = Subsample(x, n, rng, out int sampleCount);

        double best = double.MaxValue;
        float[] bestCentroids = Centroids;
        List<double> bestHistory = [];

        for (int redo = 0; redo < Math.Max(1, _parameters.Redo); redo++)
        {
            var centroids = new float[(long)K * D];
            var history = new List<double>();
            double objective = RunLloyd(sample, sampleCount, centroids, history,
                new RandomGenerator(_parameters.Seed + redo));

            if (objective < best)
            {
                best = objective;
                bestCentroids = centroids;
                bestHistory = history;
            }
        }

        Centroids = bestCentroids;
        Objective = best;
        _history.Clear();
        _history.AddRange(bestHistory);
        return Objective;
    }

    /// <summary>Trains from jagged rows.</summary>
    public double Train(float[][] x) => Train(Index.Flatten(x, D));

    /// <summary>
    /// Assigns each vector to its nearest centroid. Mirrors <c>kmeans.index.search(x, 1)</c>.
    /// </summary>
    public unsafe (long[] Labels, float[] Distances) Assign(ReadOnlySpan<float> x)
    {
        int n = x.Length / D;
        var labels = new long[n];
        var distances = new float[n];
        if (n == 0) return (labels, distances);

        fixed (float* px = x)
        fixed (float* pc = Centroids)
        fixed (float* pd = distances)
        fixed (long* pl = labels)
            BruteForce.Knn(px, n, pc, K, D, 1,
                _parameters.Spherical ? MetricType.InnerProduct : MetricType.L2, pd, pl);

        return (labels, distances);
    }

    /// <summary>A flat index over the centroids, matching the <c>kmeans.index</c> attribute in Python.</summary>
    public IndexFlat ToIndex()
    {
        var index = _parameters.Spherical
            ? (IndexFlat)new IndexFlatIP(D)
            : new IndexFlatL2(D);
        index.Add(Centroids);
        return index;
    }

    // ------------------------------------------------------------ Internals

    /// <summary>Caps training data at <c>MaxPointsPerCentroid * K</c> by random subsampling.</summary>
    private float[] Subsample(ReadOnlySpan<float> x, int n, RandomGenerator rng, out int sampleCount)
    {
        long limit = (long)_parameters.MaxPointsPerCentroid * K;
        if (n <= limit)
        {
            sampleCount = n;
            return x.ToArray();
        }

        sampleCount = (int)limit;
        var perm = rng.Permutation(n);
        var sample = new float[(long)sampleCount * D];
        for (int i = 0; i < sampleCount; i++)
            x.Slice(perm[i] * D, D).CopyTo(sample.AsSpan(i * D, D));
        return sample;
    }

    private unsafe double RunLloyd(float[] x, int n, float[] centroids, List<double> history, RandomGenerator rng)
    {
        KmeansPlusPlusInit(x, n, centroids, rng);
        if (_parameters.Spherical) NormalizeCentroids(centroids);

        var labels = new long[n];
        var distances = new float[n];
        var counts = new int[K];
        double previous = double.MaxValue;
        double objective = double.MaxValue;

        for (int iteration = 0; iteration < Math.Max(1, _parameters.Iterations); iteration++)
        {
            // --- assignment: reuse the SIMD + threaded brute-force kernel
            fixed (float* px = x)
            fixed (float* pc = centroids)
            fixed (float* pd = distances)
            fixed (long* pl = labels)
                BruteForce.Knn(px, n, pc, K, D, 1,
                    _parameters.Spherical ? MetricType.InnerProduct : MetricType.L2, pd, pl);

            objective = 0;
            for (int i = 0; i < n; i++) objective += distances[i];
            if (_parameters.Spherical) objective = -objective; // similarity: minimize the negative
            history.Add(objective);

            if (_parameters.Verbose)
                Console.WriteLine($"[kmeans] iteration {iteration + 1}/{_parameters.Iterations} objective={objective:G6}");

            // --- update: per-thread accumulators, reduced once
            Accumulate(x, n, labels, centroids, counts);
            HandleEmptyClusters(x, n, centroids, counts, rng);
            if (_parameters.Spherical) NormalizeCentroids(centroids);

            if (previous - objective <= Math.Abs(previous) * _parameters.Tolerance && iteration > 0) break;
            previous = objective;
        }

        return objective;
    }

    /// <summary>k-means++ seeding: each new centroid is drawn with probability proportional to D^2.</summary>
    private unsafe void KmeansPlusPlusInit(float[] x, int n, float[] centroids, RandomGenerator rng)
    {
        int first = rng.NextInt(n);
        x.AsSpan(first * D, D).CopyTo(centroids.AsSpan(0, D));

        var closest = new float[n];
        fixed (float* px = x)
        fixed (float* pc = centroids)
        {
            for (int i = 0; i < n; i++)
                closest[i] = VectorOps.L2Sqr(px + (long)i * D, pc, D);

            for (int c = 1; c < K; c++)
            {
                double total = 0;
                for (int i = 0; i < n; i++) total += closest[i];

                int chosen;
                if (total <= 0)
                {
                    chosen = rng.NextInt(n);
                }
                else
                {
                    double target = rng.NextFloat() * total;
                    double running = 0;
                    chosen = n - 1;
                    for (int i = 0; i < n; i++)
                    {
                        running += closest[i];
                        if (running >= target) { chosen = i; break; }
                    }
                }

                float* newCentroid = pc + (long)c * D;
                x.AsSpan(chosen * D, D).CopyTo(new Span<float>(newCentroid, D));

                for (int i = 0; i < n; i++)
                {
                    float dist = VectorOps.L2Sqr(px + (long)i * D, newCentroid, D);
                    if (dist < closest[i]) closest[i] = dist;
                }
            }
        }
    }

    /// <summary>Recomputes centroids as cluster means using thread-local accumulators.</summary>
    private void Accumulate(float[] x, int n, long[] labels, float[] centroids, int[] counts)
    {
        Array.Clear(counts);
        Array.Clear(centroids);

        var pool = new ConcurrentBag<(float[] Sums, int[] Counts)>();
        int threads = Environment.ProcessorCount;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            var sums = new float[(long)K * D];
            var localCounts = new int[K];
            int chunk = (n + threads - 1) / threads;
            int start = t * chunk;
            int end = Math.Min(n, start + chunk);

            for (int i = start; i < end; i++)
            {
                int c = (int)labels[i];
                if (c < 0) continue;
                localCounts[c]++;
                var source = x.AsSpan(i * D, D);
                var target = sums.AsSpan(c * D, D);
                for (int j = 0; j < D; j++) target[j] += source[j];
            }
            pool.Add((sums, localCounts));
        });

        foreach (var (sums, localCounts) in pool)
        {
            for (int c = 0; c < K; c++) counts[c] += localCounts[c];
            for (int j = 0; j < centroids.Length; j++) centroids[j] += sums[j];
        }

        for (int c = 0; c < K; c++)
        {
            if (counts[c] == 0) continue;
            float scale = 1f / counts[c];
            var row = centroids.AsSpan(c * D, D);
            for (int j = 0; j < D; j++) row[j] *= scale;
        }
    }

    /// <summary>
    /// Refills centroids that captured no points by splitting the largest cluster with a tiny
    /// symmetric perturbation. Without this, an IVF index silently ends up with dead lists and its
    /// effective <c>nlist</c> drops below what the caller asked for.
    /// </summary>
    private void HandleEmptyClusters(float[] x, int n, float[] centroids, int[] counts, RandomGenerator rng)
    {
        for (int c = 0; c < K; c++)
        {
            if (counts[c] > 0) continue;

            int biggest = 0;
            for (int j = 1; j < K; j++) if (counts[j] > counts[biggest]) biggest = j;
            if (counts[biggest] <= 1)
            {
                x.AsSpan(rng.NextInt(n) * D, D).CopyTo(centroids.AsSpan(c * D, D));
                counts[c] = 1;
                continue;
            }

            const float epsilon = 1.0f / 1024f;
            var source = centroids.AsSpan(biggest * D, D);
            var target = centroids.AsSpan(c * D, D);
            for (int j = 0; j < D; j++)
            {
                float jitter = (j % 2 == 0) ? (1 + epsilon) : (1 - epsilon);
                target[j] = source[j] * jitter;
                source[j] *= (j % 2 == 0) ? (1 - epsilon) : (1 + epsilon);
            }
            counts[c] = counts[biggest] / 2;
            counts[biggest] -= counts[c];
        }
    }

    private void NormalizeCentroids(float[] centroids) => VectorOps.NormalizeL2(centroids, D);
}
