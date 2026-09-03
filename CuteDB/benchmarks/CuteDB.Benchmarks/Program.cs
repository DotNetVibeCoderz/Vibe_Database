using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using CuteDB;
using CuteDB.Benchmarks;
using CuteDB.Native;

// -------------------------------------------------------------------------------------------
// CuteDB benchmarks — Gravicode Studios, led by Kang Fadhil.
//
//   dotnet run -c Release                    every suite (slow)
//   dotnet run -c Release -- --filter *Scan* one suite
//   dotnet run -c Release -- --list flat     what is available
//
// The published numbers in docs/en/performance.md come from a full run of this project. The
// `cutedb bench` command in the CLI is a much faster, much rougher version for checking your own
// hardware.
// -------------------------------------------------------------------------------------------

Console.WriteLine(CuteDatabase.EngineDescription);

if (!CuteNative.IsAvailable)
{
    // Half the scan suite compares the accelerator against the managed path. Without the library
    // both columns measure the same code, and the run silently means nothing — worth saying up
    // front rather than letting someone publish the result.
    Console.WriteLine();
    Console.WriteLine($"  WARNING: the native accelerator is not loaded ({CuteNative.UnavailableReason}).");
    Console.WriteLine("  The 'native' benchmarks will measure the managed path instead.");
    Console.WriteLine("  Build it first: pwsh native/build.ps1   (or: native/build.sh)");
}

Console.WriteLine();

var config = DefaultConfig.Instance
    .AddLogger(ConsoleLogger.Default)
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

BenchmarkSwitcher
    .FromTypes([
        typeof(ScanBenchmarks),
        typeof(ReadBenchmarks),
        typeof(WriteBenchmarks),
        typeof(SerializationBenchmarks),
    ])
    .Run(args, config);
