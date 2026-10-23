using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using MemSharp;

BenchmarkSwitcher.FromAssembly(typeof(BoundaryBenchmarks).Assembly).Run(args);

internal static partial class Native
{
    private const string Library = "memsharp_native";

    [LibraryImport(Library, EntryPoint = "ms_noop")]
    public static partial int NoOp();

    // SuppressGCTransition skips the cooperative-to-preemptive GC mode switch. Legal only for calls
    // that are short, never block and never call back into .NET - which a no-op is.
    [LibraryImport(Library, EntryPoint = "ms_noop")]
    [SuppressGCTransition]
    public static partial int NoOpSuppressed();

    [LibraryImport(Library, EntryPoint = "ms_fnv1a")]
    public static unsafe partial ulong Fnv1a(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "ms_xxh3")]
    public static unsafe partial ulong Xxh3(byte* data, nuint length);

    [LibraryImport(Library, EntryPoint = "ms_set")]
    public static unsafe partial void Set(char* key, nuint keyLength, char* value, nuint valueLength);

    [LibraryImport(Library, EntryPoint = "ms_get")]
    public static unsafe partial nint Get(char* key, nuint keyLength, char* output, nuint capacity);
}

/// <summary>Question 1: what does crossing into native code cost, per call?</summary>
[MemoryDiagnoser]
public class BoundaryBenchmarks
{
    [Benchmark(Baseline = true)]
    public int ManagedNoOp() => ManagedNoOpCore();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ManagedNoOpCore() => 0;

    [Benchmark]
    public int PInvokeNoOp() => Native.NoOp();

    [Benchmark]
    public int PInvokeNoOpSuppressGCTransition() => Native.NoOpSuppressed();
}

/// <summary>
/// Question 2: is a Rust hash map behind P/Invoke a faster string GET than MemDb's sharded
/// dictionary? Both return a .NET string, which is what a caller of either actually needs.
/// </summary>
[MemoryDiagnoser]
public class KeyValueBenchmarks
{
    private MemDb _db = null!;
    private string[] _keys = null!;
    private readonly char[] _buffer = new char[256];
    private int _next;

    [GlobalSetup]
    public unsafe void Setup()
    {
        _db = new MemDb(new MemDbOptions { ExpirySweepInterval = TimeSpan.Zero, EnableStatistics = false });
        _keys = new string[100_000];
        for (int i = 0; i < _keys.Length; i++)
        {
            _keys[i] = $"key:{i}";
            string value = $"value-{i}-0123456789";
            _db.Set(_keys[i], value);
            fixed (char* k = _keys[i])
            fixed (char* v = value)
            {
                Native.Set(k, (nuint)_keys[i].Length, v, (nuint)value.Length);
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _db.Dispose();

    private string NextKey() => _keys[_next = (_next + 7919) % _keys.Length];

    [Benchmark(Baseline = true)]
    public string? MemDbGet() => _db.Get(NextKey());

    [Benchmark]
    public unsafe string? RustMapGet()
    {
        string key = NextKey();
        fixed (char* k = key)
        fixed (char* output = _buffer)
        {
            nint length = Native.Get(k, (nuint)key.Length, output, (nuint)_buffer.Length);
            return length < 0 ? null : new string(output, 0, (int)length);
        }
    }
}

/// <summary>
/// Question 3: for bulk byte work - the snapshot checksum - does Rust win, or does the algorithm?
/// 64 MB, about the size of a snapshot holding a million small keys.
/// </summary>
public class ChecksumBenchmarks
{
    private byte[] _data = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[64 * 1024 * 1024];
        new Random(42).NextBytes(_data);
    }

    /// <summary>MemSharp's checksum today: FNV-1a, one byte per multiply.</summary>
    [Benchmark(Baseline = true)]
    public ulong Fnv1aManaged()
    {
        ulong hash = 14695981039346656037;
        foreach (byte b in _data)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        return hash;
    }

    [Benchmark]
    public unsafe ulong Fnv1aRust()
    {
        fixed (byte* data = _data) return Native.Fnv1a(data, (nuint)_data.Length);
    }

    /// <summary>A vectorised hash in managed code - System.IO.Hashing, Microsoft's package.</summary>
    [Benchmark]
    public ulong Xxh3Managed() => XxHash3.HashToUInt64(_data);

    [Benchmark]
    public unsafe ulong Xxh3Rust()
    {
        fixed (byte* data = _data) return Native.Xxh3(data, (nuint)_data.Length);
    }
}
