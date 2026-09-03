using BenchmarkDotNet.Running;

namespace MemSharp.Benchmarks;

/// <summary>
/// Entry point. With no arguments it prints the available suites rather than running all of them -
/// the full set takes the better part of an hour, which is not what someone exploring wants.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            System.Console.WriteLine("""
                MemSharp benchmarks - Gravicode Studios, led by Kang Fadhil

                Pick a suite:
                  dotnet run -c Release -- --filter '*SingleOperation*'
                  dotnet run -c Release -- --filter '*Keyspace*'
                  dotnet run -c Release -- --filter '*Concurrency*'
                  dotnet run -c Release -- --filter '*'            (everything; takes a while)

                For aggregate throughput and latency percentiles instead, use the CLI:
                  memsharp bench --tcp --pipeline 16
                """);
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
