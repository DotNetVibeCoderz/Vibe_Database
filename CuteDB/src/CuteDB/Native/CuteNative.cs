using System.Runtime.InteropServices;
using CuteDB.Storage;

namespace CuteDB.Native;

/// <summary>
/// Whether the Rust accelerator is loaded, and what it is.
/// </summary>
/// <remarks>
/// <para>
/// CuteDB works identically with or without the native library — every operation has a managed
/// implementation, and the test suite checks the two agree. The accelerator is an optimisation
/// for one thing only: scanning a large collection with a filter that no index can serve.
/// </para>
/// <para>
/// The library is loaded lazily on first use and never retried after a failure, so a machine with
/// no matching binary pays one failed <c>dlopen</c> for the life of the process and nothing
/// afterwards.
/// </para>
/// </remarks>
public static class CuteNative
{
    private static readonly Lazy<NativeState> State = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the accelerator loaded and its ABI matches this build.</summary>
    public static bool IsAvailable => State.Value.Available;

    /// <summary>The accelerator's version string, or null when it is not loaded.</summary>
    public static string? Version => State.Value.Version;

    /// <summary>Why the accelerator is unavailable, or null when it is available.</summary>
    public static string? UnavailableReason => State.Value.Reason;

    /// <summary>
    /// Turns the accelerator off for this process. Intended for benchmarking the managed path and
    /// for the parity tests; there is no reason to call it in production.
    /// </summary>
    public static bool Disabled { get; set; }
        = Environment.GetEnvironmentVariable("CUTEDB_DISABLE_NATIVE") is "1" or "true" or "TRUE";

    /// <summary>A one-line description for <c>cutedb info</c> and the demo's status bar.</summary>
    public static string Describe()
    {
        if (Disabled)
        {
            return "disabled (CUTEDB_DISABLE_NATIVE)";
        }

        return IsAvailable
            ? $"cutedb_core {Version} ({RuntimeInformation.RuntimeIdentifier})"
            : $"not loaded — {UnavailableReason}";
    }

    private static NativeState Probe()
    {
        try
        {
            var abi = NativeMethods.cutedb_abi_version();
            if (abi != PredicateProgram.AbiVersion)
            {
                return new NativeState(
                    false,
                    null,
                    $"the library reports bytecode ABI {abi}, this build speaks {PredicateProgram.AbiVersion}");
            }

            var version = Marshal.PtrToStringUTF8(NativeMethods.cutedb_version_string()) ?? "unknown";
            return new NativeState(true, version, null);
        }
        catch (DllNotFoundException)
        {
            return new NativeState(false, null, "cutedb_core was not found next to the application");
        }
        catch (EntryPointNotFoundException ex)
        {
            return new NativeState(false, null, $"the library is missing an entry point ({ex.Message})");
        }
        catch (BadImageFormatException)
        {
            return new NativeState(false, null, "the library was built for a different architecture");
        }
    }

    private readonly record struct NativeState(bool Available, string? Version, string? Reason);
}

/// <summary>The raw entry points of <c>cutedb_core</c>.</summary>
internal static unsafe partial class NativeMethods
{
    private const string Library = "cutedb_core";

    /// <summary>The bytecode ABI the loaded library implements.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint cutedb_abi_version();

    /// <summary>A NUL-terminated version string owned by the library.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial nint cutedb_version_string();

    /// <summary>
    /// Runs a compiled predicate over a slot table and writes the matching row numbers into
    /// <paramref name="outRows"/>.
    /// </summary>
    /// <returns>0 on success, or a negative error code.</returns>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int cutedb_scan(
        nint* slabs,
        nuint slabCount,
        DocRef* refs,
        nuint refCount,
        byte* program,
        nuint programLength,
        uint* outRows,
        nuint outCapacity,
        nuint* outCount);

    /// <summary>A 64-bit hash of a buffer, used where a fast non-cryptographic digest is needed.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial ulong cutedb_hash64(byte* data, nuint length);
}
