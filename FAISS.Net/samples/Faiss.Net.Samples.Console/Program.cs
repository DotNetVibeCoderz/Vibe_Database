using System.Diagnostics;
using Faiss.Net;
using Faiss.Net.Binary;
using Faiss.Net.Gpu;
using Faiss.Net.IO;

namespace Faiss.Net.Samples.ConsoleApp;

/// <summary>
/// A guided tour of FAISS.Net. Each section is self-contained and prints the numbers it is talking
/// about, so the trade-offs between index types are visible rather than asserted.
/// <para>Run everything with <c>dotnet run</c>, or one section with <c>dotnet run -- ivf</c>.</para>
/// </summary>
public static class Program
{
    private const int Dimension = 128;
    private const int DatabaseSize = 50_000;
    private const int QueryCount = 200;
    private const int K = 10;

    private static float[] _database = [];
    private static float[] _queries = [];
    private static SearchResult _groundTruth = null!;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Banner();

        var sections = new (string Name, string Description, Action Run)[]
        {
            ("quickstart", "The three-line example: build, add, search", Quickstart),
            ("flat", "Exact search, and what it costs", ExactSearch),
            ("ivf", "Inverted file: trading recall for speed with nprobe", InvertedFile),
            ("pq", "Product quantization: 32x smaller, and what that costs", Compression),
            ("hnsw", "Graph search: the fastest option at high recall", GraphSearch),
            ("compare", "All index types side by side", Comparison),
            ("ids", "Application ids, filtering and deletion", IdsAndDeletion),
            ("cosine", "Cosine similarity for text and image embeddings", CosineSimilarity),
            ("persist", "Saving, loading and memory-mapping an index", Persistence),
            ("binary", "Hamming search over binary codes", BinarySearch),
            ("kmeans", "Clustering on its own", Clustering),
            ("gpu", "Running the same search on the GPU", GpuSearch),
        };

        string? requested = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        if (requested is "help" or "--help" or "-h")
        {
            Console.WriteLine("Sections:\n");
            foreach (var (name, description, _) in sections)
                Console.WriteLine($"  {name,-12} {description}");
            Console.WriteLine("\nRun everything:   dotnet run");
            Console.WriteLine("Run one section:  dotnet run -- ivf");
            return 0;
        }

        var selected = requested is null
            ? sections
            : sections.Where(s => s.Name == requested).ToArray();

        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"Unknown section '{requested}'. Try: dotnet run -- help");
            return 1;
        }

        PrepareData();

        foreach (var (name, description, run) in selected)
        {
            Section(name, description);
            try
            {
                run();
            }
            catch (Exception exception)
            {
                Console.WriteLine($"  [skipped] {exception.Message}");
            }
        }

        Console.WriteLine();
        Rule();
        Console.WriteLine("  FAISS.Net — built by Gravicode Studios, led by Kang Fadhil.");
        Rule();
        return 0;
    }

    // ------------------------------------------------------------- Sections

    /// <summary>The example from the design document, verbatim.</summary>
    private static void Quickstart()
    {
        var vectors = FaissNet.RandomVectors(1000, 128, seed: 1);
        var query = vectors.AsSpan(0, 128).ToArray();

        var index = new IndexFlatL2(dimension: 128);
        index.Add(vectors);
        var results = index.Search(query, k: 10);

        Console.WriteLine($"  {index.Describe()}");
        Console.WriteLine($"  nearest ids : {string.Join(", ", results.LabelsFor(0).ToArray())}");
        Console.WriteLine($"  distances   : {string.Join(", ", results.DistancesFor(0).ToArray().Select(d => d.ToString("F4")))}");
        Console.WriteLine();
        Console.WriteLine("  Python equivalent:");
        Console.WriteLine("    index = faiss.IndexFlatL2(128); index.add(vectors); D, I = index.search(query, 10)");
    }

    private static void ExactSearch()
    {
        var index = new IndexFlatL2(Dimension);
        var addTime = Time(() => index.Add(_database));
        var searchTime = Time(() => index.Search(_queries, K));

        Console.WriteLine($"  {index.Describe()}");
        Report("build", addTime, "search", searchTime, index.MemoryUsage);
        Console.WriteLine($"  recall      : 100% by construction — every vector is compared");
        Console.WriteLine($"  per query   : {searchTime.TotalMilliseconds / QueryCount:F3} ms " +
                          $"({DatabaseSize * (long)Dimension / searchTime.TotalSeconds / 1e9 * QueryCount:F1} GFLOP-ish/s of distance work)");
    }

    private static void InvertedFile()
    {
        var index = new IndexIVFFlat(Dimension, nlist: 256);
        var trainTime = Time(() => index.Train(_database));
        var addTime = Time(() => index.Add(_database));

        var (min, max, mean, empty) = index.ListStatistics();
        Console.WriteLine($"  {index.Describe()}");
        Console.WriteLine($"  train {Format(trainTime)}, add {Format(addTime)}");
        Console.WriteLine($"  cells       : mean {mean:F0} vectors, min {min}, max {max}, {empty} empty");
        Console.WriteLine();
        Console.WriteLine("  nprobe   recall@10    search      per query   vectors scanned");
        Console.WriteLine("  " + new string('-', 62));

        foreach (int nprobe in new[] { 1, 2, 4, 8, 16, 32, 64 })
        {
            index.Nprobe = nprobe;
            var elapsed = Time(() => index.Search(_queries, K));
            double recall = FaissNet.ComputeRecall(_groundTruth, index.Search(_queries, K));
            double fraction = nprobe / 256.0;
            Console.WriteLine($"  {nprobe,6}   {recall,8:P1}    {Format(elapsed),-11} " +
                              $"{elapsed.TotalMilliseconds / QueryCount,6:F3} ms   ~{fraction * DatabaseSize,7:N0} ({fraction:P1})");
        }
        Console.WriteLine();
        Console.WriteLine("  nprobe is the whole dial: it can be changed at any time, with no retraining.");
    }

    private static void Compression()
    {
        long flatBytes = (long)DatabaseSize * Dimension * sizeof(float);
        Console.WriteLine($"  A flat index of this data is {Bytes(flatBytes)}.");
        Console.WriteLine();
        Console.WriteLine("  index                        memory     ratio   recall@10   build");
        Console.WriteLine("  " + new string('-', 70));

        Measure("IndexFlatL2", () =>
        {
            var index = new IndexFlatL2(Dimension);
            index.Add(_database);
            return index;
        });

        Measure("IndexScalarQuantizer(8b)", () =>
        {
            var index = new IndexScalarQuantizer(Dimension);
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        Measure("IndexPQ(m=32)", () =>
        {
            var index = new IndexPQ(Dimension, m: 32);
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        Measure("IndexPQ(m=16)", () =>
        {
            var index = new IndexPQ(Dimension, m: 16);
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        Measure("IndexPQ(m=8)", () =>
        {
            var index = new IndexPQ(Dimension, m: 8);
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        void Measure(string name, Func<Index> build)
        {
            Index index = null!;
            var elapsed = Time(() => index = build());
            double recall = FaissNet.ComputeRecall(_groundTruth, index.Search(_queries, K));
            Console.WriteLine($"  {name,-27} {Bytes(index.MemoryUsage),9}   {flatBytes / (double)index.MemoryUsage,4:F0}x   " +
                              $"{recall,8:P1}   {Format(elapsed)}");
        }

        Console.WriteLine();
        Console.WriteLine("  Each PQ row halves memory and gives up some recall. Pick the row that fits.");
    }

    private static void GraphSearch()
    {
        var index = new IndexHNSWFlat(Dimension, m: 32) { EfConstruction = 80 };
        var buildTime = Time(() => index.Add(_database));

        Console.WriteLine($"  {index.Describe()}");
        Console.WriteLine($"  build {Format(buildTime)}, graph adds {Bytes(index.MemoryUsage - (long)DatabaseSize * Dimension * 4)} " +
                          $"on top of the vectors");
        Console.WriteLine($"  layer sizes : {string.Join(" / ", index.Graph.LayerSizes())}");
        Console.WriteLine($"  mean degree : {index.Graph.AverageDegree():F1} on layer 0");
        Console.WriteLine();
        Console.WriteLine("  efSearch   recall@10    per query");
        Console.WriteLine("  " + new string('-', 42));

        foreach (int ef in new[] { 8, 16, 32, 64, 128, 256 })
        {
            index.EfSearch = ef;
            var elapsed = Time(() => index.Search(_queries, K));
            double recall = FaissNet.ComputeRecall(_groundTruth, index.Search(_queries, K));
            Console.WriteLine($"  {ef,8}   {recall,8:P1}    {elapsed.TotalMilliseconds / QueryCount,7:F3} ms");
        }
    }

    private static void Comparison()
    {
        Console.WriteLine("  index                     build      search/query   recall@10    memory");
        Console.WriteLine("  " + new string('-', 76));

        Compare("IndexFlatL2", () =>
        {
            var index = new IndexFlatL2(Dimension);
            index.Add(_database);
            return index;
        });

        Compare("IndexIVFFlat(256, np=8)", () =>
        {
            var index = new IndexIVFFlat(Dimension, 256) { Nprobe = 8 };
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        Compare("IndexIVFPQ(256, m=16, np=8)", () =>
        {
            var index = new IndexIVFPQ(Dimension, 256, m: 16) { Nprobe = 8 };
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        Compare("IndexIVFSQ(256, np=8)", () =>
        {
            var index = new IndexIVFScalarQuantizer(Dimension, 256) { Nprobe = 8 };
            index.Train(_database);
            index.Add(_database);
            return index;
        });

        Compare("IndexHNSWFlat(M=32, ef=64)", () =>
        {
            var index = new IndexHNSWFlat(Dimension, 32) { EfSearch = 64 };
            index.Add(_database);
            return index;
        });

        Compare("OPQ16,IVF256,PQ16 (np=8)", () =>
        {
            var index = FaissNet.IndexFactory(Dimension, "OPQ16,IVF256,PQ16");
            index.Train(_database);
            index.Add(_database);
            ((IndexIVFPQ)((IndexPreTransform)index).Base).Nprobe = 8;
            return index;
        });

        void Compare(string name, Func<Index> build)
        {
            Index index = null!;
            var buildTime = Time(() => index = build());
            var searchTime = Time(() => index.Search(_queries, K));
            double recall = FaissNet.ComputeRecall(_groundTruth, index.Search(_queries, K));
            Console.WriteLine($"  {name,-27} {Format(buildTime),-10} " +
                              $"{searchTime.TotalMilliseconds / QueryCount,8:F3} ms   {recall,8:P1}   {Bytes(index.MemoryUsage),9}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {DatabaseSize:N0} vectors of dimension {Dimension}, {QueryCount} queries, k={K}.");
        Console.WriteLine("  Flat is the reference: everything else buys speed or memory by giving up recall.");
    }

    private static void IdsAndDeletion()
    {
        // Application ids are rarely 0..n-1. IndexIDMap2 keeps the caller's own ids.
        var index = new IndexIDMap2(new IndexFlatL2(Dimension));
        var ids = new long[DatabaseSize];
        for (int i = 0; i < DatabaseSize; i++) ids[i] = 1_000_000_000L + i * 3;
        index.AddWithIds(_database, ids);

        var before = index.Search(_queries.AsSpan(0, Dimension).ToArray(), 5);
        Console.WriteLine($"  ids returned : {string.Join(", ", before.LabelsFor(0).ToArray())}");

        long removed = index.RemoveIds(id => id % 2 == 0);
        Console.WriteLine($"  removed {removed:N0} ids by predicate, {index.Ntotal:N0} remain");

        var after = index.Search(_queries.AsSpan(0, Dimension).ToArray(), 5);
        Console.WriteLine($"  after delete : {string.Join(", ", after.LabelsFor(0).ToArray())}");
        Console.WriteLine("  Surviving ids are unchanged — that is the point of the wrapper.");
    }

    private static void CosineSimilarity()
    {
        // Cosine similarity is inner product on normalized vectors. Putting the normalization
        // inside the index means queries cannot be forgotten.
        var index = new IndexPreTransform(new NormalizationTransform(Dimension), new IndexFlatIP(Dimension));
        index.Add(_database);

        var result = index.Search(_queries.AsSpan(0, Dimension).ToArray(), 5);
        Console.WriteLine($"  {index.Describe()}");
        Console.WriteLine("  rank   id        cosine");
        foreach (var (id, score) in result.Neighbors())
            Console.WriteLine($"  {Array.IndexOf(result.LabelsFor(0).ToArray(), id) + 1,4}   {id,-8}  {score:F4}");
        Console.WriteLine();
        Console.WriteLine("  Scores are in [-1, 1] and 1.0 means identical direction.");
    }

    private static void Persistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), "faissnet-sample");
        Directory.CreateDirectory(directory);
        string indexPath = Path.Combine(directory, "demo.index");
        string mappedPath = Path.Combine(directory, "demo.mmap");

        var index = new IndexIVFPQ(Dimension, nlist: 128, m: 16) { Nprobe = 8 };
        index.Train(_database);
        index.Add(_database);

        var writeTime = Time(() => FaissNet.WriteIndex(index, indexPath));
        long fileSize = new FileInfo(indexPath).Length;

        Index reloaded = null!;
        var readTime = Time(() => reloaded = FaissNet.ReadIndex(indexPath));

        Console.WriteLine($"  wrote {Bytes(fileSize)} in {Format(writeTime)}, read back in {Format(readTime)}");
        Console.WriteLine($"  reloaded    : {reloaded.Describe()}");

        var a = index.Search(_queries, K);
        var b = reloaded.Search(_queries, K);
        bool identical = a.Labels.SequenceEqual(b.Labels);
        Console.WriteLine($"  results identical after reload: {identical}");

        // Memory mapping: the file stays on disk and is paged in on demand.
        var flat = new IndexFlatL2(Dimension);
        flat.Add(_database);
        MappedIndexFlat.Write(flat, mappedPath);

        using var mapped = MappedIndexFlat.Open(mappedPath);
        var mappedTime = Time(() => mapped.Search(_queries, K));
        Console.WriteLine();
        Console.WriteLine($"  {mapped.Describe()}");
        Console.WriteLine($"  searched in {Format(mappedTime)} with nothing loaded into managed memory");
    }

    private static void BinarySearch()
    {
        const int bits = 256;
        int codeSize = bits / 8;

        // Binarize the float data by sign, the usual way binary codes are produced.
        var codes = new byte[DatabaseSize * codeSize];
        for (int i = 0; i < DatabaseSize; i++)
            HammingOps.Binarize(_database.AsSpan(i * Dimension, Dimension).ToArray()
                                        .Select(v => v - 0.5f).ToArray(),
                                codes.AsSpan(i * codeSize, codeSize));

        var index = new IndexBinaryFlat(bits);
        index.Add(codes);

        var queryCodes = codes.AsSpan(0, QueryCount * codeSize).ToArray();
        var elapsed = Time(() => index.Search(queryCodes, K));
        var result = index.Search(queryCodes, K);

        Console.WriteLine($"  {index.Describe()}");
        Console.WriteLine($"  memory      : {Bytes(index.MemoryUsage)} " +
                          $"({(long)DatabaseSize * Dimension * 4 / (double)index.MemoryUsage:F0}x smaller than float32)");
        Console.WriteLine($"  search      : {elapsed.TotalMilliseconds / QueryCount:F4} ms per query");
        Console.WriteLine($"  top hits    : {string.Join(", ", result.LabelsFor(0)[..5].ToArray())}");
        Console.WriteLine($"  distances   : {string.Join(", ", result.DistancesFor(0)[..5].ToArray().Select(d => $"{d:F0} bits"))}");
    }

    private static void Clustering()
    {
        var kmeans = new Kmeans(Dimension, k: 64, new ClusteringParameters { Iterations = 20, Seed = 7 });
        var elapsed = Time(() => kmeans.Train(_database));

        var (labels, _) = kmeans.Assign(_database);
        var counts = new int[64];
        foreach (long label in labels) counts[label]++;
        Array.Sort(counts);

        Console.WriteLine($"  clustered {DatabaseSize:N0} vectors into 64 centroids in {Format(elapsed)}");
        Console.WriteLine($"  objective   : {kmeans.Objective:G6} after {kmeans.ObjectiveHistory.Count} iterations");
        Console.WriteLine($"  cluster size: min {counts[0]}, median {counts[32]}, max {counts[^1]}");
        Console.WriteLine($"  convergence : {string.Join(" -> ", kmeans.ObjectiveHistory.Take(5).Select(o => o.ToString("G4")))} ...");
    }

    private static void GpuSearch()
    {
        Console.WriteLine($"  devices     : {string.Join("; ", StandardGpuResources.EnumerateDevices())}");

        using var resources = new StandardGpuResources();
        Console.WriteLine($"  using       : {resources.DeviceName}");
        if (!resources.IsHardwareAccelerated)
            Console.WriteLine("  note        : no CUDA/OpenCL device found — running ILGPU's CPU accelerator, so");
        if (!resources.IsHardwareAccelerated)
            Console.WriteLine("                the kernels are exercised for correctness but not for speed.");

        // A smaller slice: the CPU fallback accelerator is slow, and the point here is agreement.
        int n = Math.Min(DatabaseSize, 10_000);
        var subset = _database.AsSpan(0, n * Dimension).ToArray();
        var queries = _queries.AsSpan(0, 20 * Dimension).ToArray();

        var cpu = new IndexFlatL2(Dimension);
        cpu.Add(subset);
        var cpuTime = Time(() => cpu.Search(queries, K));

        using var gpu = new IndexFlatL2Gpu(Dimension, resources);
        gpu.Add(subset);
        gpu.Sync();
        var gpuTime = Time(() => gpu.Search(queries, K));

        bool identical = cpu.Search(queries, K).Labels.SequenceEqual(gpu.Search(queries, K).Labels);
        Console.WriteLine($"  cpu         : {Format(cpuTime)}");
        Console.WriteLine($"  gpu         : {Format(gpuTime)}");
        Console.WriteLine($"  identical results: {identical}");
    }

    // -------------------------------------------------------------- Helpers

    private static void PrepareData()
    {
        Console.Write($"  Generating {DatabaseSize:N0} x {Dimension} clustered vectors... ");

        // Database and queries come from ONE draw and are then split. Generating the query set from
        // a second, independent set of cluster centres would make the queries out-of-distribution:
        // their true neighbours would be arbitrary far-away points, every approximate index would
        // look far worse than it is, and the comparison below would measure nothing useful. Every
        // real ANN benchmark holds its queries out of the same distribution, and so does this one.
        var all = FaissNet.RandomClusteredVectors(DatabaseSize + QueryCount, Dimension,
            clusters: 200, spread: 0.06f, seed: 1234);
        _database = all.AsSpan(0, DatabaseSize * Dimension).ToArray();
        _queries = all.AsSpan(DatabaseSize * Dimension, QueryCount * Dimension).ToArray();

        var reference = new IndexFlatL2(Dimension);
        reference.Add(_database);
        _groundTruth = reference.Search(_queries, K);
        Console.WriteLine("done. Exact ground truth computed for recall comparisons.");
        Console.WriteLine();
    }

    private static TimeSpan Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static string Format(TimeSpan elapsed) =>
        elapsed.TotalSeconds >= 1 ? $"{elapsed.TotalSeconds:F2} s" : $"{elapsed.TotalMilliseconds:F1} ms";

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };

    private static void Report(string buildLabel, TimeSpan build, string searchLabel, TimeSpan search, long memory)
    {
        Console.WriteLine($"  {buildLabel,-11} : {Format(build)}");
        Console.WriteLine($"  {searchLabel,-11} : {Format(search)} for {QueryCount} queries");
        Console.WriteLine($"  memory      : {Bytes(memory)}");
    }

    private static void Banner()
    {
        Rule();
        Console.WriteLine("  FAISS.Net — high-performance vector search for .NET");
        Console.WriteLine($"  version {FaissNet.Version}  ·  SIMD: {FaissNet.SimdInfo}  ·  {Environment.ProcessorCount} cores  ·  .NET {Environment.Version}");
        Rule();
        Console.WriteLine();
    }

    private static void Section(string name, string description)
    {
        Console.WriteLine();
        Console.WriteLine($"── {name} {new string('─', Math.Max(0, 60 - name.Length))}");
        Console.WriteLine($"   {description}");
        Console.WriteLine();
    }

    private static void Rule() => Console.WriteLine(new string('═', 78));
}
