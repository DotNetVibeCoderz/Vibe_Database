using BenchmarkDotNet.Running;
using Faiss.Net;

namespace Faiss.Net.Benchmarks;

/// <summary>
/// Entry point for both kinds of benchmark: the matched suite that is comparable with Python FAISS,
/// and the BenchmarkDotNet micro-benchmarks used to catch regressions inside FAISS.Net itself.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();

        return command switch
        {
            "suite" => RunSuite(rest),
            "gendata" => GenerateData(rest),
            "micro" => RunMicro(rest),
            _ => Help(),
        };
    }

    private static int Help()
    {
        Console.WriteLine("""
            FAISS.Net benchmarks

              dotnet run -c Release -- gendata [--out DIR] [--d 128] [--n 100000] [--nq 1000] [--k 10]
                  Generates the shared dataset as .fvecs/.ivecs, including exact ground truth.
                  Both this suite and the Python one read these files, so the comparison is
                  measuring the implementations and not two different datasets.

              dotnet run -c Release -- suite [--data DIR] [--out results-dotnet.json]
                  Runs the matched suite (same index configurations as bench_faiss.py) and writes
                  the JSON that compare.py merges.

              dotnet run -c Release -- micro [--filter *Distance*]
                  BenchmarkDotNet micro-benchmarks: distance kernels, single-query latency, batch
                  throughput and build time. Release builds only.

            Full comparison against Python FAISS:

              dotnet run -c Release -- gendata --out data
              dotnet run -c Release -- suite --data data --out results-dotnet.json
              python benchmarks/python/bench_faiss.py --data data --out results-python.json
              python benchmarks/python/compare.py results-dotnet.json results-python.json
            """);
        return 0;
    }

    private static int GenerateData(string[] args)
    {
        string directory = Option(args, "--out", "data");
        int d = int.Parse(Option(args, "--d", "128"));
        int n = int.Parse(Option(args, "--n", "100000"));
        int nq = int.Parse(Option(args, "--nq", "1000"));
        int k = int.Parse(Option(args, "--k", "10"));

        Console.WriteLine($"Generating {n:N0} x {d} vectors plus {nq} held-out queries...");
        var dataset = Dataset.Generate(d, n, nq, k);

        Console.WriteLine("Computing exact ground truth (flat scan)...");
        dataset.ComputeGroundTruth();

        dataset.Save(directory);
        long bytes = new DirectoryInfo(directory).GetFiles().Sum(f => f.Length);
        Console.WriteLine($"Wrote {directory}/base.fvecs, query.fvecs, groundtruth.ivecs ({bytes / (1024.0 * 1024):F1} MB).");
        return 0;
    }

    private static int RunSuite(string[] args)
    {
        string? directory = OptionOrNull(args, "--data");
        string output = Option(args, "--out", "results-dotnet.json");

        Dataset dataset;
        if (directory is not null && File.Exists(Path.Combine(directory, "base.fvecs")))
        {
            Console.WriteLine($"Loading dataset from {directory}...");
            dataset = Dataset.Load(directory);
        }
        else
        {
            Console.WriteLine("No dataset directory given; generating one in memory.");
            Console.WriteLine("For a Python comparison, run `gendata` first so both sides read the same vectors.");
            dataset = Dataset.Generate(128, 100_000, 1_000, 10);
            dataset.ComputeGroundTruth();
        }

        Console.WriteLine();
        Console.WriteLine("FAISS.Net matched benchmark suite");
        Console.WriteLine(new string('=', 84));
        var report = SuiteRunner.Run(dataset);
        Console.WriteLine();

        SuiteRunner.Save(report, output);
        Console.WriteLine($"Wrote {output} ({report.Records.Count} configurations).");
        Console.WriteLine("Compare with Python: python benchmarks/python/compare.py " + output + " results-python.json");
        return 0;
    }

    private static int RunMicro(string[] args)
    {
        BenchmarkSwitcher
            .FromTypes([
                typeof(DistanceKernelBenchmarks),
                typeof(SingleQuerySearchBenchmarks),
                typeof(BatchSearchBenchmarks),
                typeof(BuildBenchmarks),
            ])
            .Run(args);
        return 0;
    }

    private static string Option(string[] args, string name, string fallback) =>
        OptionOrNull(args, name) ?? fallback;

    private static string? OptionOrNull(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
