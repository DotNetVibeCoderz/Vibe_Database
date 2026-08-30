using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Faiss.Net;

namespace Faiss.Net.Benchmarks;

/// <summary>One measured index configuration. The JSON shape is shared with the Python suite.</summary>
public sealed class BenchmarkRecord
{
    [JsonPropertyName("implementation")] public string Implementation { get; set; } = "FAISS.Net";
    [JsonPropertyName("index")] public string Index { get; set; } = "";
    [JsonPropertyName("params")] public string Parameters { get; set; } = "";
    [JsonPropertyName("build_ms")] public double BuildMilliseconds { get; set; }
    [JsonPropertyName("train_ms")] public double TrainMilliseconds { get; set; }
    [JsonPropertyName("add_ms")] public double AddMilliseconds { get; set; }
    [JsonPropertyName("search_ms_per_query")] public double SearchMillisecondsPerQuery { get; set; }
    [JsonPropertyName("queries_per_second")] public double QueriesPerSecond { get; set; }
    [JsonPropertyName("recall_at_k")] public double RecallAtK { get; set; }
    [JsonPropertyName("memory_bytes")] public long MemoryBytes { get; set; }
}

/// <summary>Everything one run produces, including the environment it ran in.</summary>
public sealed class BenchmarkReport
{
    [JsonPropertyName("implementation")] public string Implementation { get; set; } = "FAISS.Net";
    [JsonPropertyName("version")] public string Version { get; set; } = FaissNet.Version;
    [JsonPropertyName("runtime")] public string Runtime { get; set; } = "";
    [JsonPropertyName("simd")] public string Simd { get; set; } = "";
    [JsonPropertyName("cpu_cores")] public int CpuCores { get; set; }
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("dimension")] public int Dimension { get; set; }
    [JsonPropertyName("database_size")] public int DatabaseSize { get; set; }
    [JsonPropertyName("query_count")] public int QueryCount { get; set; }
    [JsonPropertyName("k")] public int K { get; set; }
    [JsonPropertyName("records")] public List<BenchmarkRecord> Records { get; set; } = [];
}

/// <summary>
/// The matched benchmark suite: the same index configurations, on the same data, measured the same
/// way as <c>benchmarks/python/bench_faiss.py</c>.
/// <para>
/// Every configuration reports build time, per-query search time, recall@k and memory together,
/// because any one of them alone is misleading — an index is only faster than another at equal
/// recall, and only smaller at equal recall too. Search is measured over repeated passes after a
/// warm-up so JIT compilation and cold caches do not land in the reported number.
/// </para>
/// </summary>
public static class SuiteRunner
{
    /// <summary>Search passes over the whole query set, after warm-up. The median pass is reported.</summary>
    private const int SearchRepeats = 5;

    public static BenchmarkReport Run(Dataset dataset, bool verbose = true)
    {
        var report = new BenchmarkReport
        {
            Runtime = $".NET {Environment.Version}",
            Simd = FaissNet.SimdInfo,
            CpuCores = Environment.ProcessorCount,
            Os = Environment.OSVersion.ToString(),
            Dimension = dataset.Dimension,
            DatabaseSize = dataset.DatabaseSize,
            QueryCount = dataset.QueryCount,
            K = dataset.K,
        };

        int d = dataset.Dimension;
        int nlist = Math.Max(16, (int)Math.Sqrt(dataset.DatabaseSize));

        if (verbose)
        {
            Console.WriteLine($"  {report.Runtime}, {report.Simd}, {report.CpuCores} cores");
            Console.WriteLine($"  {dataset.DatabaseSize:N0} x {d} vectors, {dataset.QueryCount} queries, k={dataset.K}, nlist={nlist}");
            Console.WriteLine();
            Console.WriteLine($"  {"index",-28} {"build",11} {"ms/query",10} {"qps",10} {"recall",9} {"memory",10}");
            Console.WriteLine("  " + new string('-', 82));
        }

        // --- exact baseline
        Measure("IndexFlatL2", "exact", () => new IndexFlatL2(d), null);

        // --- IVF at several probe counts: one build, several search settings
        MeasureIvf("IndexIVFFlat", () => new IndexIVFFlat(d, nlist), [1, 4, 8, 16, 32]);
        MeasureIvf("IndexIVFPQ", () => new IndexIVFPQ(d, nlist, m: PickM(d)), [1, 4, 8, 16, 32]);
        MeasureIvf("IndexIVFSQ8", () => new IndexIVFScalarQuantizer(d, nlist), [1, 8, 32]);

        // --- flat compressed
        Measure($"IndexPQ", $"m={PickM(d)}", () => new IndexPQ(d, PickM(d)), null);
        Measure("IndexSQ8", "8-bit", () => new IndexScalarQuantizer(d), null);

        // --- graph
        foreach (int ef in new[] { 16, 32, 64, 128 })
        {
            Measure("IndexHNSWFlat", $"M=32,efSearch={ef}",
                () => new IndexHNSWFlat(d, 32) { EfConstruction = 80, EfSearch = ef }, null);
        }

        return report;

        // ------------------------------------------------------------ helpers

        void MeasureIvf(string name, Func<IndexIVF> factory, int[] probes)
        {
            var index = factory();
            var trainTime = Time(() => index.Train(dataset.Database));
            var addTime = Time(() => index.Add(dataset.Database));

            foreach (int nprobe in probes)
            {
                index.Nprobe = nprobe;
                Record(name, $"nlist={index.Nlist},nprobe={nprobe}", index,
                    trainTime.TotalMilliseconds, addTime.TotalMilliseconds);
            }
        }

        void Measure(string name, string parameters, Func<Index> factory, Action<Index>? configure)
        {
            var index = factory();
            configure?.Invoke(index);
            var trainTime = index.IsTrained ? TimeSpan.Zero : Time(() => index.Train(dataset.Database));
            var addTime = Time(() => index.Add(dataset.Database));
            Record(name, parameters, index, trainTime.TotalMilliseconds, addTime.TotalMilliseconds);
        }

        void Record(string name, string parameters, Index index, double trainMs, double addMs)
        {
            // Warm-up: the first search JITs the specialized kernels and touches the whole index.
            index.Search(dataset.Queries, dataset.K);

            var timings = new List<double>(SearchRepeats);
            SearchResult result = null!;
            for (int i = 0; i < SearchRepeats; i++)
                timings.Add(Time(() => result = index.Search(dataset.Queries, dataset.K)).TotalMilliseconds);
            timings.Sort();
            double median = timings[timings.Count / 2];

            var record = new BenchmarkRecord
            {
                Index = name,
                Parameters = parameters,
                TrainMilliseconds = trainMs,
                AddMilliseconds = addMs,
                BuildMilliseconds = trainMs + addMs,
                SearchMillisecondsPerQuery = median / dataset.QueryCount,
                QueriesPerSecond = dataset.QueryCount / (median / 1000.0),
                RecallAtK = dataset.Recall(result),
                MemoryBytes = index.MemoryUsage,
            };
            report.Records.Add(record);

            if (verbose)
                Console.WriteLine($"  {name + " " + parameters,-28} {record.BuildMilliseconds,9:F0}ms " +
                                  $"{record.SearchMillisecondsPerQuery,10:F4} {record.QueriesPerSecond,10:N0} " +
                                  $"{record.RecallAtK,9:P1} {Bytes(record.MemoryBytes),10}");
        }
    }

    /// <summary>Largest sub-quantizer count that divides d and keeps the code at 16 bytes or fewer.</summary>
    private static int PickM(int d)
    {
        for (int m = Math.Min(16, d); m >= 1; m--)
            if (d % m == 0) return m;
        return 1;
    }

    private static TimeSpan Time(Action action)
    {
        var stopwatch = Stopwatch.StartNew();
        action();
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2}GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1}MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1}KB",
        _ => $"{bytes}B",
    };

    /// <summary>Writes the report as JSON for <c>compare.py</c> to merge with the Python run.</summary>
    public static void Save(BenchmarkReport report, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options));
    }
}
