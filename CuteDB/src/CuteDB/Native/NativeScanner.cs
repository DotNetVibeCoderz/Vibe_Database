using System.Buffers;
using CuteDB.Query;
using CuteDB.Storage;

namespace CuteDB.Native;

/// <summary>
/// Bridges a collection scan to the Rust accelerator.
/// </summary>
/// <remarks>
/// <para>
/// The whole scan happens on the native side: the slab base addresses, the slot table and the
/// compiled predicate go across once, and a list of matching row numbers comes back. Nothing is
/// copied — the slabs are unmanaged memory that never moves, and <see cref="DocRef"/> is laid out
/// to match the Rust struct — so the cost of the call is one P/Invoke regardless of collection
/// size.
/// </para>
/// <para>
/// Every reason to decline is checked before the call: the library must be loaded, the predicate
/// must compile, and the collection has to be big enough that the fixed cost is worth paying. A
/// scan of a few hundred documents is faster in managed code than it is to marshal.
/// </para>
/// </remarks>
internal static class NativeScanner
{
    /// <summary>
    /// Below this many rows the P/Invoke and the buffer rental cost more than the scan saves.
    /// </summary>
    private const int MinimumRowsToBotherWith = 2_000;

    /// <summary>
    /// Runs a scan natively. Returns false — having written nothing — when the accelerator cannot
    /// or should not handle this query, and the caller should use the managed path.
    /// </summary>
    internal static unsafe bool TryScan(
        DocumentStore store,
        CuteExpression predicate,
        CuteParameters? parameters,
        int limit,
        List<int> results)
    {
        if (CuteNative.Disabled || !CuteNative.IsAvailable)
        {
            return false;
        }

        if (store.RowCount < MinimumRowsToBotherWith)
        {
            return false;
        }

        if (!PredicateProgram.TryCompile(predicate, parameters, out var program))
        {
            return false;
        }

        var slabs = store.Slabs.SlabPointers;
        var refs = store.Refs;
        if (slabs.Length == 0 || refs.Length == 0)
        {
            return false;
        }

        // The output buffer is sized for every row matching. A LIMIT does not shrink it, because
        // the native side still has to be able to report matches up to the point it stops.
        var capacity = limit == int.MaxValue ? refs.Length : Math.Min(refs.Length, limit);
        var output = ArrayPool<uint>.Shared.Rent(capacity);

        try
        {
            nuint matched;
            int status;

            fixed (nint* slabPointer = slabs)
            fixed (DocRef* refPointer = refs)
            fixed (byte* programPointer = program.Bytes)
            fixed (uint* outputPointer = output)
            {
                status = NativeMethods.cutedb_scan(
                    slabPointer,
                    (nuint)slabs.Length,
                    refPointer,
                    (nuint)refs.Length,
                    programPointer,
                    (nuint)program.Bytes.Length,
                    outputPointer,
                    (nuint)capacity,
                    &matched);
            }

            if (status != 0)
            {
                // A malformed program or an unknown opcode means this build and the library
                // disagree about the bytecode. That is a bug rather than a data problem, but it
                // must not break the query — the managed evaluator produces the same answer.
                return false;
            }

            var count = (int)matched;
            for (var i = 0; i < count && results.Count < limit; i++)
            {
                results.Add((int)output[i]);
            }

            return true;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(output);
        }
    }
}
