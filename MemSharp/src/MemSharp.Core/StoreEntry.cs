using System.Runtime.InteropServices;

namespace MemSharp;

/// <summary>
/// One keyspace slot: what the value is, the value itself, and when it expires.
/// </summary>
/// <remarks>
/// A struct stored by value inside the shard dictionary, not a class it points at. That removes one
/// heap object and one pointer indirection per key - on a keyspace of ten million keys it is about
/// 240 MB of object headers that never get allocated and never get traced by the GC.
///
/// <see cref="ExpiresAtTicks"/> is an absolute UTC tick count with 0 meaning "never", rather than a
/// <c>DateTime?</c>: the nullable would add a byte plus padding to every entry in the database to
/// express something a sentinel already covers, and the comparison on the read path becomes a
/// single integer compare.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
internal struct StoreEntry
{
    /// <summary>The payload: a <see cref="string"/>, or one of the collection stores.</summary>
    public object Value;

    /// <summary>Absolute expiry in UTC ticks, or 0 for no expiry.</summary>
    public long ExpiresAtTicks;

    /// <summary>Which kind of value <see cref="Value"/> holds.</summary>
    public MemType Type;

    public StoreEntry(MemType type, object value, long expiresAtTicks = 0)
    {
        Type = type;
        Value = value;
        ExpiresAtTicks = expiresAtTicks;
    }

    public readonly bool HasExpiry => ExpiresAtTicks != 0;

    public readonly bool IsExpired(long nowTicks) => ExpiresAtTicks != 0 && nowTicks >= ExpiresAtTicks;
}
